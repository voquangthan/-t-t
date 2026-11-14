using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Schema;

public sealed record DocumentExcerpt(string Text, string Citation);
public sealed record ExtractedDocument(string Name, string MediaType, string Sha256, IReadOnlyList<DocumentExcerpt> Excerpts);

/// <summary>Bounded, local-only extraction. Never executes document content or archive entries.</summary>
public sealed class UniversalFileReader
{
    public const long MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxZipEntries = 100;
    private const long MaxExpandedBytes = 32 * 1024 * 1024;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".jsonl", ".ndjson", ".xml", ".csv", ".tsv",
        ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".sql", ".html", ".css", ".yml", ".yaml",
        ".toml", ".ini", ".sh", ".ps1", ".bat", ".java", ".go", ".rs", ".php", ".rb", ".c", ".h", ".cpp", ".hpp"
    };

    public async Task<ExtractedDocument> ReadAsync(string name, string mediaType, Stream content, CancellationToken cancellationToken)
    {
        name = Path.GetFileName(name);
        var extension = Path.GetExtension(name);
        using var memory = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > MaxFileBytes) throw new InvalidDataException($"{name}: vượt giới hạn {MaxFileBytes / 1024 / 1024} MiB.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        List<DocumentExcerpt> excerpts;
        try
        {
            excerpts = extension.ToLowerInvariant() switch
            {
                ".docx" => ReadDocx(bytes),
                ".xlsx" => ReadXlsx(bytes),
                ".pptx" => ReadPptx(bytes),
                ".zip" => ReadZip(bytes),
                _ when TextExtensions.Contains(extension) => ReadText(name, bytes),
                ".pdf" => ReadPdf(bytes),
                ".doc" or ".xls" or ".ppt" => throw Unsupported(name, "Office định dạng cũ chưa được hỗ trợ. Hãy lưu thành DOCX/XLSX/PPTX."),
                ".parquet" => await ReadParquetAsync(bytes, cancellationToken),
                ".sqlite" or ".sqlite3" or ".db" => await ReadSqliteAsync(bytes, cancellationToken),
                ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".tif" or ".tiff" or ".bmp" =>
                    throw Unsupported(name, "Ảnh cần gửi qua route Vision trong chat; Reader văn bản không chuyển ảnh vào model text."),
                _ => throw Unsupported(name, $"Không hỗ trợ định dạng {extension}.")
            };
        }
        catch (System.Xml.XmlException error) { throw Unsupported(name, $"XML trong tài liệu không hợp lệ: {error.Message}"); }
        catch (DecoderFallbackException) { throw Unsupported(name, "Nội dung ZIP không phải UTF-8 hợp lệ."); }
        if (excerpts.Count == 0 || excerpts.All(item => string.IsNullOrWhiteSpace(item.Text)))
            throw Unsupported(name, "Không trích xuất được nội dung văn bản. Tài liệu scan/ảnh cần OCR.");
        return new(name, string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType, hash, excerpts);
    }

    private static List<DocumentExcerpt> ReadText(string name, byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw Unsupported(name, "Tệp không phải UTF-8 hợp lệ; chưa hỗ trợ encoding này."); }
        if (text.Contains('\0')) throw Unsupported(name, "Tệp có dữ liệu nhị phân, không thể đọc như văn bản.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is ".json" or ".xml")
        {
            try
            {
                if (extension == ".json") System.Text.Json.JsonDocument.Parse(text).Dispose();
                else XDocument.Parse(text, LoadOptions.SetLineInfo);
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or System.Xml.XmlException)
            { throw Unsupported(name, $"Cấu trúc {extension} không hợp lệ: {error.Message}"); }
        }
        var rows = text.Replace("\r\n", "\n").Split('\n');
        var output = new List<DocumentExcerpt>();
        var start = 0;
        while (start < rows.Length)
        {
            var end = Math.Min(rows.Length, start + 80);
            var part = string.Join('\n', rows[start..end]).Trim();
            if (part.Length > 0) output.Add(new(part, $"{name} · dòng {start + 1}-{end}"));
            start = end;
        }
        return output;
    }

    private static List<DocumentExcerpt> ReadDocx(byte[] bytes)
    {
        using var archive = OpenZip(bytes, "DOCX");
        var output = new List<DocumentExcerpt>();
        foreach (var part in archive.Entries.Where(entry => entry.FullName.StartsWith("word/", StringComparison.Ordinal) &&
                     entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                     Regex.IsMatch(entry.FullName, @"^word/(document|footnotes|endnotes|comments|header\d*|footer\d*)\.xml$")))
        {
            var xml = LoadXml(part);
            var paragraphs = xml.Descendants().Where(element => element.Name.LocalName == "p")
                .Select(element => string.Concat(element.Descendants().Where(child => child.Name.LocalName is "t" or "tab" or "br")
                    .Select(child => child.Name.LocalName == "t" ? child.Value : child.Name.LocalName == "tab" ? "\t" : "\n")))
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            for (var i = 0; i < paragraphs.Length; i += 40)
                output.Add(new(string.Join("\n", paragraphs.Skip(i).Take(40)), $"{Path.GetFileNameWithoutExtension(part.Name)} · đoạn {i + 1}-{Math.Min(i + 40, paragraphs.Length)}"));
        }
        return output;
    }

    private static List<DocumentExcerpt> ReadPptx(byte[] bytes)
    {
        using var archive = OpenZip(bytes, "PPTX");
        var output = new List<DocumentExcerpt>();
        foreach (var slide in archive.Entries.Where(entry => Regex.IsMatch(entry.FullName, @"^ppt/slides/slide\d+\.xml$"))
                     .OrderBy(entry => int.Parse(Regex.Match(entry.Name, @"\d+").Value, System.Globalization.CultureInfo.InvariantCulture)))
        {
            var text = string.Join(" ", LoadXml(slide).Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value));
            if (!string.IsNullOrWhiteSpace(text)) output.Add(new(text, $"slide {Regex.Match(slide.Name, @"\d+").Value}"));
        }
        return output;
    }

    private static List<DocumentExcerpt> ReadPdf(byte[] bytes)
    {
        try
        {
            using var document = PdfDocument.Open(bytes);
            if (document.NumberOfPages > 200) throw Unsupported("PDF", "Vượt giới hạn 200 trang mỗi tệp.");
            var output = new List<DocumentExcerpt>();
            foreach (var page in document.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                const int maxChars = 6000;
                for (var offset = 0; offset < text.Length; offset += maxChars)
                    output.Add(new(text.Substring(offset, Math.Min(maxChars, text.Length - offset)),
                        $"page {page.Number}" + (text.Length > maxChars ? $" · ký tự {offset + 1}-{Math.Min(offset + maxChars, text.Length)}" : "")));
            }
            return output;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { throw Unsupported("PDF", $"Không thể phân tích tài liệu: {error.Message}"); }
    }

    private static async Task<List<DocumentExcerpt>> ReadParquetAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new MemoryStream(bytes, writable: false);
            await using var reader = await ParquetReader.CreateAsync(input, cancellationToken: cancellationToken);
            if (reader.RowGroupCount > 1000) throw Unsupported("Parquet", "Vượt giới hạn 1000 row group.");
            var fields = reader.Schema.DataFields.Take(50).ToArray();
            if (fields.Length == 0) throw Unsupported("Parquet", "Không có cột dữ liệu phẳng để đọc.");
            var output = new List<DocumentExcerpt>();
            var rowsRead = 0L;
            for (var groupIndex = 0; groupIndex < reader.RowGroupCount && rowsRead < 2000; groupIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var group = reader.OpenRowGroupReader(groupIndex);
                var count = (int)Math.Min(group.RowCount, 2000 - rowsRead);
                var columns = new string?[count][];
                for (var column = 0; column < fields.Length; column++)
                {
                    var field = fields[column];
                    var dataType = Nullable.GetUnderlyingType(field.ClrType) ?? field.ClrType;
                    Array values = dataType == typeof(string)
                        ? await ReadStringParquetValuesAsync(group, field, count, cancellationToken)
                        : field.IsNullable && dataType.IsValueType
                            ? await ReadNullableParquetValuesAsync(group, field, count, dataType, cancellationToken)
                            : await ReadTypedParquetValuesAsync(group, field, count, dataType, cancellationToken);
                    columns[column] = Enumerable.Range(0, count).Select(index => index < values.Length
                        ? ParquetValue(values.GetValue(index)) : "").ToArray();
                }
                for (var row = 0; row < count; row++)
                {
                    var values = fields.Select((field, column) => $"{field.Name}={columns[column][row]}");
                    output.Add(new(string.Join(" | ", values), $"row group {groupIndex + 1} · hàng {rowsRead + row + 1}"));
                }
                rowsRead += count;
            }
            return output;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            var cause = error is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : error;
            throw Unsupported("Parquet", $"Không thể đọc cấu trúc Parquet: {cause.Message}");
        }
    }

    private static async Task<List<DocumentExcerpt>> ReadSqliteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), "detot-reader-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
                Pooling = false, DefaultTimeout = 2
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using (var safety = connection.CreateCommand())
            {
                safety.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;";
                await safety.ExecuteNonQueryAsync(cancellationToken);
            }
            var tables = new List<string>();
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name LIMIT 20";
                await using var result = await schema.ExecuteReaderAsync(cancellationToken);
                while (await result.ReadAsync(cancellationToken)) tables.Add(result.GetString(0));
            }
            var output = new List<DocumentExcerpt>();
            var remaining = 1000;
            foreach (var table in tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var command = connection.CreateCommand();
                var escaped = table.Replace("\"", "\"\"", StringComparison.Ordinal);
                command.CommandText = $"SELECT * FROM \"{escaped}\" LIMIT {remaining}";
                await using var rows = await command.ExecuteReaderAsync(cancellationToken);
                var columns = Math.Min(rows.FieldCount, 50);
                while (remaining > 0 && await rows.ReadAsync(cancellationToken))
                {
                    var values = Enumerable.Range(0, columns).Select(index => $"{rows.GetName(index)}={SafeSqliteValue(rows.GetValue(index))}");
                    output.Add(new(string.Join(" | ", values), $"table {table} · hàng {1000 - remaining + 1}"));
                    remaining--;
                }
                if (remaining == 0) break;
            }
            return output;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { throw Unsupported("SQLite", $"Không thể đọc database chỉ-đọc: {error.Message}"); }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private static string SafeSqliteValue(object value)
    {
        if (value is DBNull) return "NULL";
        var text = value switch
        {
            byte[] bytes => Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 128))),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }

    private static string ParquetValue(object? value)
    {
        if (value is null) return "NULL";
        var text = value switch
        {
            byte[] bytes => Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 128))),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }

    private static async Task<Array> ReadStringParquetValuesAsync(ParquetRowGroupReader group, DataField field, int count, CancellationToken cancellationToken)
    {
        var values = new string?[count];
        await group.ReadAsync(field, values.AsMemory(), cancellationToken: cancellationToken);
        return values;
    }

    private static async Task<Array> ReadTypedParquetValuesAsync(ParquetRowGroupReader group, DataField field, int count, Type elementType, CancellationToken cancellationToken)
    {
        var method = typeof(UniversalFileReader).GetMethod(nameof(ReadTypedParquetValuesGenericAsync), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(elementType);
        var task = (Task<Array>)method.Invoke(null, [group, field, count, cancellationToken])!;
        return await task;
    }

    private static async Task<Array> ReadTypedParquetValuesGenericAsync<T>(ParquetRowGroupReader group, DataField field, int count, CancellationToken cancellationToken) where T : struct
    {
        var values = new T[count];
        await group.ReadAsync(field, values.AsMemory(), cancellationToken: cancellationToken);
        return values;
    }

    private static async Task<Array> ReadNullableParquetValuesAsync(ParquetRowGroupReader group, DataField field, int count, Type elementType, CancellationToken cancellationToken)
    {
        var method = typeof(UniversalFileReader).GetMethod(nameof(ReadNullableParquetValuesGenericAsync), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(elementType);
        var task = (Task<Array>)method.Invoke(null, [group, field, count, cancellationToken])!;
        return await task;
    }

    private static async Task<Array> ReadNullableParquetValuesGenericAsync<T>(ParquetRowGroupReader group, DataField field, int count, CancellationToken cancellationToken) where T : struct
    {
        var values = new T?[count];
        await group.ReadAsync(field, values.AsMemory(), cancellationToken: cancellationToken);
        return values;
    }

    private static List<DocumentExcerpt> ReadXlsx(byte[] bytes)
    {
        using var archive = OpenZip(bytes, "XLSX");
        var shared = archive.GetEntry("xl/sharedStrings.xml") is { } strings
            ? LoadXml(strings).Descendants().Where(element => element.Name.LocalName == "si")
                .Select(item => string.Concat(item.Descendants().Where(child => child.Name.LocalName == "t").Select(child => child.Value))).ToArray()
            : Array.Empty<string>();
        var sheetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (archive.GetEntry("xl/workbook.xml") is { } workbookEntry && archive.GetEntry("xl/_rels/workbook.xml.rels") is { } relsEntry)
        {
            var relationships = LoadXml(relsEntry).Descendants().Where(element => element.Name.LocalName == "Relationship")
                .ToDictionary(element => (string?)element.Attribute("Id") ?? "", element => (string?)element.Attribute("Target") ?? "", StringComparer.Ordinal);
            foreach (var sheet in LoadXml(workbookEntry).Descendants().Where(element => element.Name.LocalName == "sheet"))
            {
                var id = sheet.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
                if (id is null || !relationships.TryGetValue(id, out var target) || target.Contains("..", StringComparison.Ordinal)) continue;
                var entryPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target.TrimStart('/');
                sheetNames[entryPath] = (string?)sheet.Attribute("name") ?? Path.GetFileNameWithoutExtension(entryPath);
            }
        }
        var output = new List<DocumentExcerpt>();
        var sheets = archive.Entries.Where(entry => Regex.IsMatch(entry.FullName, @"^xl/worksheets/sheet\d+\.xml$"))
            .OrderBy(entry => int.Parse(Regex.Match(entry.Name, @"\d+").Value, System.Globalization.CultureInfo.InvariantCulture));
        foreach (var sheet in sheets)
        {
            var rows = LoadXml(sheet).Descendants().Where(element => element.Name.LocalName == "row");
            foreach (var row in rows)
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
                {
                    var type = (string?)cell.Attribute("t");
                    var value = cell.Elements().FirstOrDefault(element => element.Name.LocalName is "v" or "is");
                    var text = value is null ? "" : type == "s" && int.TryParse(value.Value, out var ix) && ix >= 0 && ix < shared.Length
                        ? shared[ix] : string.Concat(value.DescendantsAndSelf().Where(element => element.Name.LocalName == "t").Select(element => element.Value));
                    cells.Add($"{(string?)cell.Attribute("r") ?? "cell"}={text}");
                }
                if (cells.Count > 0) output.Add(new(string.Join(" | ", cells), $"sheet {(sheetNames.GetValueOrDefault(sheet.FullName) ?? Path.GetFileNameWithoutExtension(sheet.Name))} · hàng {(string?)row.Attribute("r") ?? "?"}"));
            }
        }
        return output;
    }

    private static List<DocumentExcerpt> ReadZip(byte[] bytes)
    {
        using var archive = OpenZip(bytes, "ZIP");
        var output = new List<DocumentExcerpt>();
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).Take(MaxZipEntries))
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.StartsWith('/') || path.Split('/').Any(part => part is ".." or ".") || entry.Length > MaxFileBytes) continue;
            if (entry.CompressedLength == 0 && entry.Length > 0 || entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > 100) continue;
            var extension = Path.GetExtension(path);
            if (!TextExtensions.Contains(extension) || entry.Length > 1024 * 1024) continue;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (text.Contains('\0')) continue;
            output.AddRange(ReadText(path, Encoding.UTF8.GetBytes(text)));
        }
        if (output.Count == 0) throw Unsupported("ZIP", "Không tìm thấy tệp văn bản hỗ trợ trong giới hạn an toàn; ZIP lồng nhau không được mở.");
        return output;
    }

    private static ZipArchive OpenZip(byte[] bytes, string format)
    {
        try
        {
            var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxZipEntries || archive.Entries.Sum(entry => entry.Length) > MaxExpandedBytes)
            { archive.Dispose(); throw Unsupported(format, "Archive vượt giới hạn số mục hoặc kích thước giải nén."); }
            return archive;
        }
        catch (InvalidDataException error) when (error.Message.StartsWith(format + ":", StringComparison.Ordinal)) { throw; }
        catch (InvalidDataException) { throw Unsupported(format, "Archive hỏng hoặc không hợp lệ."); }
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > 8 * 1024 * 1024) throw Unsupported(entry.FullName, "Phần XML trong tài liệu quá lớn.");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static InvalidDataException Unsupported(string name, string reason) => new($"{name}: {reason}");
}

public sealed record UniversalUrlReadResult(string Title, string FinalUrl, string Text, bool IsSearchPage,
    string? ETag, DateTimeOffset? LastModifiedUtc, bool NotModified);

public sealed class UniversalUrlReader(IHttpClientFactory clients, UniversalFileReader? files = null)
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> ReaderFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".jsonl", ".ndjson", ".xml", ".csv", ".tsv",
        ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".sql", ".css", ".yml", ".yaml", ".toml", ".ini",
        ".sh", ".ps1", ".java", ".go", ".rs", ".php", ".rb", ".c", ".h", ".cpp", ".hpp",
        ".pdf", ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt", ".parquet", ".sqlite", ".sqlite3", ".db", ".zip"
    };
    public async Task<(string Title, string FinalUrl, string Text, bool IsSearchPage)> ReadAsync(string value, CancellationToken cancellationToken)
    {
        var result = await ReadVersionedAsync(value, null, null, cancellationToken);
        return (result.Title, result.FinalUrl, result.Text, result.IsSearchPage);
    }

    public async Task<UniversalUrlReadResult> ReadVersionedAsync(string value, string? etag, DateTimeOffset? lastModifiedUtc,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Fragment.Length > 0 || IPAddress.TryParse(uri.Host, out _) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("URL phải là HTTPS công khai, không chứa credential hay IP trực tiếp.");
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("text/html,text/plain,application/xhtml+xml,application/pdf,application/json");
            if (!string.IsNullOrWhiteSpace(etag) && System.Net.Http.Headers.EntityTagHeaderValue.TryParse(etag, out var entityTag))
                request.Headers.IfNoneMatch.Add(entityTag);
            if (lastModifiedUtc.HasValue) request.Headers.IfModifiedSince = lastModifiedUtc.Value;
            using var response = await clients.CreateClient("learning-source").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new UniversalUrlReadResult("", uri.ToString(), "", false, response.Headers.ETag?.Tag ?? etag,
                    response.Content.Headers.LastModified ?? lastModifiedUtc, true);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                if (redirects == 3 || response.Headers.Location is null) throw new InvalidDataException("URL chuyển hướng quá số lần cho phép.");
                uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                if (uri.Scheme != "https" || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) || IPAddress.TryParse(uri.Host, out _))
                    throw new ArgumentException("Đích chuyển hướng không an toàn.");
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"URL trả HTTP {(int)response.StatusCode}.");
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            extension = extension switch
            {
                _ when type == "application/pdf" => ".pdf",
                _ when type == "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                _ when type == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                _ when type == "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
                _ when type == "text/csv" => ".csv",
                _ when type == "application/x-ndjson" => ".jsonl",
                _ when type == "application/json" && string.IsNullOrEmpty(extension) => ".json",
                _ => extension
            };
            var isDocument = ReaderFileExtensions.Contains(extension);
            if (!isDocument && type is not ("text/html" or "application/xhtml+xml" or "text/plain" or "text/markdown" or "application/json"))
                throw new InvalidDataException($"Chưa hỗ trợ nội dung URL dạng {type}.");
            if (response.Content.Headers.ContentLength is > MaxBytes) throw new InvalidDataException("Trang vượt giới hạn 2 MiB.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                if (output.Length + count > MaxBytes) throw new InvalidDataException("Trang vượt giới hạn 2 MiB.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            var bytes = output.ToArray();
            if (isDocument)
            {
                var reader = files ?? new UniversalFileReader();
                await using var documentStream = new MemoryStream(bytes, writable: false);
                var fileName = Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName) || Path.GetExtension(fileName).Length == 0) fileName = "document" + extension;
                var document = await reader.ReadAsync(fileName, type, documentStream, cancellationToken);
                var documentText = string.Join("\n\n", document.Excerpts.Select(item => $"[{item.Citation}]\n{item.Text}"));
                return new UniversalUrlReadResult(fileName, uri.ToString(), documentText, false, response.Headers.ETag?.Tag,
                    response.Content.Headers.LastModified, false);
            }
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim().Trim('"');
            Encoding encoding;
            try { encoding = string.IsNullOrWhiteSpace(charset) ? new UTF8Encoding(false, true) : Encoding.GetEncoding(charset); }
            catch (ArgumentException) { throw new InvalidDataException($"Trang khai báo encoding không được hỗ trợ: {charset}."); }
            string raw;
            try { raw = encoding.GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidDataException("Không thể giải mã nội dung URL theo encoding đã khai báo."); }
            var title = uri.Host;
            var text = raw;
            if (type is "text/html" or "application/xhtml+xml")
            {
                title = WebUtility.HtmlDecode(Regex.Match(raw, "(?is)<title[^>]*>(.*?)</title>").Groups[1].Value).Trim();
                var main = Regex.Match(raw, "(?is)<(main|article)[^>]*>(.*?)</\\1>");
                if (main.Success) raw = main.Groups[2].Value;
                text = Regex.Replace(raw, "(?is)<(script|style|nav|header|footer|svg|noscript)[^>]*>.*?</\\1>", " ");
                text = Regex.Replace(text, "(?i)<(p|h[1-6]|li|tr|article|section|br|div)[^>]*>", "\\n");
                text = WebUtility.HtmlDecode(Regex.Replace(text, "(?s)<[^>]+>", " "));
                text = Regex.Replace(text, @"[\t \u00a0]+|\n{3,}", match => match.Value.Contains('\n') ? "\n\n" : " ").Trim();
            }
            if (string.IsNullOrWhiteSpace(title)) title = uri.Host;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Trang không có nội dung văn bản trích xuất được; đây có thể là trang tìm kiếm hoặc website cần JavaScript.");
            var queryKeys = System.Web.HttpUtility.ParseQueryString(uri.Query).AllKeys;
            var searchPage = uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase) ||
                queryKeys.Any(key => key is not null && key.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                                     key is not null && key.Equals("query", StringComparison.OrdinalIgnoreCase) ||
                                     key is not null && key.Equals("search", StringComparison.OrdinalIgnoreCase));
            return new UniversalUrlReadResult(title, uri.ToString(), text, searchPage, response.Headers.ETag?.Tag,
                response.Content.Headers.LastModified, false);
        }
        throw new InvalidOperationException("Không thể đọc URL.");
    }
}
