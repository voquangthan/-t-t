using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("dataset manager accepts licensed supported dataset metadata", DatasetAccepts),
    ("dataset manager rejects unknown license and unsupported formats", DatasetRejects),
    ("backup encrypts source data before provider upload", BackupIsEncrypted),
    ("restore verifies encryption and external session revocation authority", RestoreIsVerified),
    ("trusted learning requires source check and user approval before Drive indexing", TrustedLearningWorkflow),
    ("trusted learning rejects unknown licenses and unsafe URL scopes", TrustedLearningRejectsUnsafeSources),
    ("universal reader extracts text with checksum and line citations", ReaderExtractsText),
    ("universal reader extracts office XML with structural citations", ReaderExtractsDocx),
    ("universal reader extracts PDF text with page citations", ReaderExtractsPdf),
    ("universal reader extracts Parquet rows with row group citations", ReaderExtractsParquet),
    ("universal reader opens SQLite read-only and returns table citations", ReaderExtractsSqlite),
    ("universal reader rejects unsupported formats and filters unsafe ZIP paths", ReaderRejectsAndFilters),
    ("universal URL reader blocks local and non-HTTPS destinations", UrlReaderRejectsUnsafeTargets),
    ("universal URL reader extracts main content and labels search pages", UrlReaderExtractsContent),
};

var failed = 0;
foreach (var (name, run) in checks)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
Console.WriteLine($"{checks.Count - failed}/{checks.Count} checks passed");
return failed == 0 ? 0 : 1;

static async Task DatasetAccepts()
{
    var provider = new FakeStorageProvider();
    var manager = new DatasetManager(provider);
    var session = await manager.StartUploadAsync(new("dataset", "vietnamese", "faq.jsonl", "v1.0", "support", "curated test data", "CC BY 4.0", 12), CancellationToken.None);
    Assert(session.Size == 12, "validated metadata should reach provider");
    Assert(provider.Started?.Version == "v1.0", "dataset version should be preserved");
}

static async Task DatasetRejects()
{
    var provider = new FakeStorageProvider();
    var manager = new DatasetManager(provider);
    await Throws<ArgumentException>(() => manager.StartUploadAsync(new("dataset", "vietnamese", "faq.jsonl", "v1", "", "source", "unknown", 12), CancellationToken.None));
    await Throws<ArgumentException>(() => manager.StartUploadAsync(new("dataset", "vietnamese", "faq.jsonl", "v1", "", "source", "fg", 12), CancellationToken.None));
    await Throws<ArgumentException>(() => manager.StartUploadAsync(new("dataset", "vietnamese", "faq.exe", "v1", "", "source", "CC BY 4.0", 12), CancellationToken.None));
    Assert(DatasetLicenseCatalog.IsRecognized("CC BY 4.0"), "common license display names should map to a recognized license");
    Assert(provider.Started is null, "invalid metadata must not start a cloud upload");
}

static async Task ReaderExtractsText()
{
    var reader = new UniversalFileReader();
    var data = Encoding.UTF8.GetBytes("Question,Answer\r\nXin chào,Xin chào bạn");
    await using var stream = new MemoryStream(data);
    var result = await reader.ReadAsync("faq.csv", "text/csv", stream, CancellationToken.None);
    Assert(result.Sha256 == Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), "reader should return source SHA-256");
    Assert(result.Excerpts.Count == 1 && result.Excerpts[0].Citation.Contains("dòng 1-", StringComparison.Ordinal), "CSV should retain line citation");
    Assert(result.Excerpts[0].Text.Contains("Xin chào", StringComparison.Ordinal), "CSV content should remain readable");
}

static async Task ReaderExtractsDocx()
{
    using var archiveBytes = new MemoryStream();
    using (var archive = new System.IO.Compression.ZipArchive(archiveBytes, System.IO.Compression.ZipArchiveMode.Create, true))
    {
        var entry = archive.CreateEntry("word/document.xml");
        await using var output = entry.Open();
        var xml = System.Text.Encoding.UTF8.GetBytes("<document xmlns:w=\"urn:w\"><body><p><r><t>Hello from DOCX</t></r></p></body></document>");
        await output.WriteAsync(xml);
    }
    archiveBytes.Position = 0;
    var result = await new UniversalFileReader().ReadAsync("sample.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", archiveBytes, CancellationToken.None);
    Assert(result.Excerpts.Any(item => item.Text.Contains("Hello from DOCX", StringComparison.Ordinal)), "DOCX paragraph should be extracted");
    Assert(result.Excerpts[0].Citation.Contains("document · đoạn", StringComparison.Ordinal), "DOCX citation should identify document part and paragraph range");
}

static async Task ReaderRejectsAndFilters()
{
    var reader = new UniversalFileReader();
    await using (var pdf = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7")))
        await Throws<InvalidDataException>(() => reader.ReadAsync("scan.pdf", "application/pdf", pdf, CancellationToken.None));
    using var zipBytes = new MemoryStream();
    using (var archive = new System.IO.Compression.ZipArchive(zipBytes, System.IO.Compression.ZipArchiveMode.Create, true))
    {
        foreach (var (name, value) in new[] { ("../outside.txt", "must not read"), ("safe/readme.txt", "safe text") })
        {
            var entry = archive.CreateEntry(name);
            await using var output = entry.Open();
            await output.WriteAsync(Encoding.UTF8.GetBytes(value));
        }
    }
    zipBytes.Position = 0;
    var extracted = await reader.ReadAsync("sample.zip", "application/zip", zipBytes, CancellationToken.None);
    Assert(extracted.Excerpts.Any(item => item.Text.Contains("safe text", StringComparison.Ordinal)), "safe text should be read from archive");
    Assert(extracted.Excerpts.All(item => !item.Text.Contains("must not read", StringComparison.Ordinal)), "path traversal entry must be ignored");
}

static async Task UrlReaderRejectsUnsafeTargets()
{
    var reader = new UniversalUrlReader(new FakeHttpClientFactory());
    await Throws<ArgumentException>(() => reader.ReadAsync("http://127.0.0.1/admin", CancellationToken.None));
    await Throws<ArgumentException>(() => reader.ReadAsync("https://192.168.1.10/private", CancellationToken.None));
    await Throws<ArgumentException>(() => reader.ReadAsync("https://user:pass@example.com/", CancellationToken.None));
    Assert(!TrustedLearningSources.IsPublicAddress(System.Net.IPAddress.Parse("192.0.2.12")), "reserved IPv4 documentation ranges must be blocked");
    Assert(!TrustedLearningSources.IsPublicAddress(System.Net.IPAddress.Parse("203.0.113.12")), "reserved IPv4 example ranges must be blocked");
    Assert(TrustedLearningSources.IsPublicAddress(System.Net.IPAddress.Parse("8.8.8.8")), "a public resolver address should remain permitted");
}

static async Task UrlReaderExtractsContent()
{
    var html = "<html><head><title>Docs</title></head><body><nav>Menu noise</nav><main><h1>Reader guide</h1><p>Stable article content for this extraction check.</p></main></body></html>";
    var factory = new StaticResponseHttpClientFactory(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    });
    var page = await new UniversalUrlReader(factory).ReadAsync("https://docs.example.test/search?q=reader", CancellationToken.None);
    Assert(page.Title == "Docs" && page.IsSearchPage, "page metadata should identify search results");
    Assert(page.Text.Contains("Reader guide", StringComparison.Ordinal) && !page.Text.Contains("Menu noise", StringComparison.Ordinal), "main content should be extracted without navigation");
}

static async Task ReaderExtractsPdf()
{
    var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
    var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
    var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
    page.AddText("Detot PDF test text", 12, new UglyToad.PdfPig.Core.PdfPoint(25, 700), font);
    await using var input = new MemoryStream(builder.Build());
    var result = await new UniversalFileReader().ReadAsync("sample.pdf", "application/pdf", input, CancellationToken.None);
    Assert(result.Excerpts.Count == 1 && result.Excerpts[0].Text.Contains("Detot PDF test text", StringComparison.Ordinal), "PDF text should be extracted");
    Assert(result.Excerpts[0].Citation == "page 1", "PDF citation should preserve page number");
}

static async Task ReaderExtractsParquet()
{
    var field = new Parquet.Schema.DataField<string>("city");
    var countField = new Parquet.Schema.DataField<int>("count");
    var schema = new Parquet.Schema.ParquetSchema(field, countField);
    using var data = new MemoryStream();
    await using (var writer = await Parquet.ParquetWriter.CreateAsync(schema, data))
    {
        using var group = writer.CreateRowGroup();
        await group.WriteAsync(field, new[] { "Hà Nội", "Đà Nẵng" });
        await group.WriteAsync(countField, new ReadOnlyMemory<int>(new[] { 12, 25 }));
    }
    data.Position = 0;
    var result = await new UniversalFileReader().ReadAsync("cities.parquet", "application/vnd.apache.parquet", data, CancellationToken.None);
    Assert(result.Excerpts.Count == 2 && result.Excerpts[0].Text.Contains("Hà Nội", StringComparison.Ordinal), "Parquet values should be extracted");
    Assert(result.Excerpts[0].Text.Contains("count=12", StringComparison.Ordinal), "Parquet primitive values should be rendered");
    Assert(result.Excerpts[0].Citation.Contains("row group 1", StringComparison.Ordinal), "Parquet row location should be preserved");
}

static async Task ReaderExtractsSqlite()
{
    var path = Path.Combine(Path.GetTempPath(), "detot-test-" + Guid.NewGuid().ToString("N") + ".sqlite");
    try
    {
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE notes(topic TEXT); INSERT INTO notes VALUES ('DNS');";
            await command.ExecuteNonQueryAsync();
        }
        await using (var input = File.OpenRead(path))
        {
            var result = await new UniversalFileReader().ReadAsync("notes.sqlite", "application/x-sqlite3", input, CancellationToken.None);
            Assert(result.Excerpts.Count == 1 && result.Excerpts[0].Text.Contains("DNS", StringComparison.Ordinal), "SQLite table values should be extracted");
            Assert(result.Excerpts[0].Citation.Contains("table notes", StringComparison.Ordinal), "SQLite citation should preserve table and row");
        }
    }
    finally { try { File.Delete(path); } catch (IOException) { } }
}

static async Task BackupIsEncrypted()
{
    var root = Path.Combine(Path.GetTempPath(), "detot-cloud-tests-" + Guid.NewGuid().ToString("N"));
    var source = Path.Combine(root, "memory");
    Directory.CreateDirectory(source);
    const string sentinel = "private-memory-sentinel-7391";
    const string passphrase = "distinct local backup passphrase";
    await File.WriteAllTextAsync(Path.Combine(source, "memory.json"), sentinel);
    await File.WriteAllTextAsync(Path.Combine(source, "secret.env"), "PRIVATE_API_KEY=should-not-be-backed-up");
    await File.WriteAllTextAsync(Path.Combine(source, "credentials.json"), "{\"api_key\":\"CONTENT_SECRET_SHOULD_NOT_BE_BACKED_UP\"}");
    try
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DETOT_MEMORY_CORE_PATH"] = source,
        }).Build();
        var provider = new FakeStorageProvider();
        var service = new MemoryBackupService(provider, Path.Combine(root, "state"), configuration, root);
        var result = await service.CreateAsync(["memory-core"], passphrase, CancellationToken.None);
        var uploaded = provider.Uploaded.ToArray();
        Assert(uploaded.Length > 32, "provider should receive encrypted archive bytes");
        Assert(Encoding.ASCII.GetString(uploaded, 0, 8) == "DETOBAK1", "backup should use authenticated encrypted format");
        var content = Encoding.UTF8.GetString(uploaded);
        Assert(!content.Contains(sentinel, StringComparison.Ordinal), "plaintext memory must not reach storage");
        Assert(!content.Contains(passphrase, StringComparison.Ordinal), "passphrase must not reach storage");
        Assert(!content.Contains("should-not-be-backed-up", StringComparison.Ordinal), "secret-looking file should be excluded");
        Assert(!content.Contains("CONTENT_SECRET_SHOULD_NOT_BE_BACKED_UP", StringComparison.Ordinal), "credential content should be excluded");
        Assert(result.Sha256.Length == 64 && result.Files == 1, "backup result should report manifest and ciphertext checksum");
    }
    finally { try { Directory.Delete(root, recursive: true); } catch { } }
}

static async Task RestoreIsVerified()
{
    var node = Environment.GetEnvironmentVariable("DETOT_TEST_NODE") ?? "C:\\Program Files\\nodejs\\node.exe";
    if (!File.Exists(node)) throw new InvalidOperationException("Node runtime required for restore-preflight integration check.");
    var root = Path.Combine(Path.GetTempPath(), "detot-restore-tests-" + Guid.NewGuid().ToString("N"));
    var source = Path.Combine(root, "memory");
    var authority = Path.Combine(root, "revocation-authority");
    Directory.CreateDirectory(source); Directory.CreateDirectory(authority);
    const string sentinel = "private-memory-restore-roundtrip";
    const string passphrase = "restore integration test passphrase";
    await File.WriteAllTextAsync(Path.Combine(source, "memory.md"), sentinel);
    try
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DETOT_MEMORY_CORE_PATH"] = source,
            ["DETOT_LAB_NODE"] = node,
            ["DETOT_COPY_AUTHORITY_DIR"] = authority,
        }).Build();
        var provider = new FakeStorageProvider();
        var service = new MemoryBackupService(provider, Path.Combine(root, "state"), configuration, Directory.GetCurrentDirectory());
        await service.CreateAsync(["memory-core"], passphrase, CancellationToken.None);
        var ready = await service.RestoreToStagingAsync("test-id", passphrase, CancellationToken.None);
        Assert(ready.Files == 1 && ready.Bytes > 0 && ready.Message.StartsWith("PASS:", StringComparison.Ordinal), "restore preflight should validate archive and manifest");
        var archive = service.TakeRestore(ready.Id);
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(archive.Path);
            var entry = zip.GetEntry("memory-core/memory.md") ?? throw new InvalidOperationException("restored source file is missing");
            using var reader = new StreamReader(entry.Open());
            Assert(await reader.ReadToEndAsync() == sentinel, "decrypted archive content should match source");
        }
        finally { try { File.Delete(archive.Path); } catch { } }

        await File.WriteAllTextAsync(Path.Combine(authority, sentinel + ".revoked"), "");
        await Throws<InvalidDataException>(() => service.RestoreToStagingAsync("test-id", passphrase, CancellationToken.None));
    }
    finally { try { Directory.Delete(root, recursive: true); } catch { } }
}

static async Task TrustedLearningWorkflow()
{
    var root = Path.Combine(Path.GetTempPath(), "detot-learning-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    const string content = "{\"question\":\"How to configure a VLAN trunk safely\",\"answer\":\"Configure the switch trunk with an explicit allowed VLAN list, verify the native VLAN on both ends, and inspect the link status before changing production traffic.\"}";
    var storage = new FakeStorageProvider
    {
        Listed = [new CloudArtifact("drive-learning-1", "network.jsonl", "dataset", "programming", "1.0.0", "", "curated", "fg", Encoding.UTF8.GetByteCount(content), DateTimeOffset.UtcNow, "", "PASS", null)],
        SeedDownloadBytes = Encoding.UTF8.GetBytes(content),
        SeedDownloadName = "network.jsonl",
    };
    try
    {
        var service = new TrustedLearningSources(root, new EphemeralDataProtectionProvider(), new FakeHttpClientFactory(), storage,
            NullLogger<TrustedLearningSources>.Instance);
        var pending = await service.AddAsync(new("drive-folder", "Programming docs", "drive://datasets/programming", "CC-BY-4.0", ""), CancellationToken.None);
        Assert(pending.Status == "pending" && pending.ApprovedUtc is null, "new sources must begin pending, not approved");
        await Throws<InvalidOperationException>(() => service.ApproveAsync(pending.Id, CancellationToken.None));
        var failed = await service.CheckAsync(pending.Id, CancellationToken.None);
        Assert(failed.Status == "check-failed" && failed.CheckSummary!.Contains("giấy phép", StringComparison.OrdinalIgnoreCase),
            "unrecognized Drive metadata must stop before any dataset is read");
        storage.Listed = storage.Listed.Select(item => item with { License = "CC-BY-4.0" }).ToArray();
        var checkedSource = await service.CheckAsync(pending.Id, CancellationToken.None);
        Assert(checkedSource.Status == "checked" && checkedSource.FileCount == 1, "check should verify the selected Drive category and its license");
        var approved = await service.ApproveAsync(pending.Id, CancellationToken.None);
        Assert(approved.Status == "approved", "only an explicit approval should activate collection");
        var sync = await service.SyncAsync(pending.Id, CancellationToken.None);
        Assert(sync.Accepted > 0 && sync.NeedsReview == 0, "approved text dataset should pass quality checks and enter the index");
        var evidence = service.Search("VLAN trunk allowed list", 4);
        Assert(evidence.Count == 1 && evidence[0].Citation.StartsWith("drive://drive-learning-1#", StringComparison.Ordinal) &&
            Uri.UnescapeDataString(evidence[0].Citation).Contains("dòng", StringComparison.Ordinal), "RAG results must preserve source ID and extracted line citation");
        Assert(service.BuildContext("VLAN trunk")?.Contains("[K1]", StringComparison.Ordinal) == true, "chat context should include a stable citation marker");
        var secondMachine = new TrustedLearningSources(Path.Combine(root, "second-device"), new EphemeralDataProtectionProvider(),
            new FakeHttpClientFactory(), storage, NullLogger<TrustedLearningSources>.Instance);
        await secondMachine.InitializeCloudSnapshotAsync(CancellationToken.None);
        Assert(secondMachine.Search("VLAN trunk").Count == 1 && secondMachine.List().Single().Status == "approved",
            "the verified Drive snapshot should carry approved sources and indexed knowledge to another device");
        await service.RemoveAsync(pending.Id, CancellationToken.None);
        Assert(service.Search("VLAN trunk").Count == 0, "revoking a source must remove its knowledge from retrieval");
    }
    finally { try { Directory.Delete(root, recursive: true); } catch { } }
}

static async Task TrustedLearningRejectsUnsafeSources()
{
    var root = Path.Combine(Path.GetTempPath(), "detot-learning-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var service = new TrustedLearningSources(root, new EphemeralDataProtectionProvider(), new FakeHttpClientFactory(), new FakeStorageProvider(),
            NullLogger<TrustedLearningSources>.Instance);
        await Throws<ArgumentException>(() => service.AddAsync(new("website", "Unsafe", "https://localhost/private", "CC-BY-4.0", "https://example.org/license"), CancellationToken.None));
        await Throws<ArgumentException>(() => service.AddAsync(new("website", "Unlicensed", "https://example.org/docs", "unknown", "https://example.org/license"), CancellationToken.None));
        await Throws<ArgumentException>(() => service.AddAsync(new("github", "Wide scope", "https://github.com/example", "MIT", ""), CancellationToken.None));
        Assert(service.List().Count == 0, "invalid sources must not be saved");
    }
    finally { try { Directory.Delete(root, recursive: true); } catch { } }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FakeStorageProvider : IStorageProvider
{
    private readonly MemoryStream _upload = new();
    private long _size;
    public UploadSpec? Started { get; private set; }
    public byte[] Uploaded => _upload.ToArray();
    public IReadOnlyList<CloudArtifact> Listed { get; set; } = [];
    private readonly List<CloudArtifact> _generated = [];
    public byte[]? SeedDownloadBytes { get; set; }
    public string? SeedDownloadName { get; set; }

    public Task<CloudStorageStatus> StatusAsync(CancellationToken cancellationToken) => Task.FromResult(new CloudStorageStatus(true, true, "test@example.invalid", true, _upload.Length, null));
    public Task<IReadOnlyList<CloudArtifact>> ListAsync(string? kind, string? pipeline, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudArtifact>>([.. Listed, .. _generated]);
    public Task<UploadSession> StartUploadAsync(UploadSpec spec, long size, CancellationToken cancellationToken)
    {
        Started = spec; _size = size; _upload.SetLength(0);
        return Task.FromResult(new UploadSession(Guid.NewGuid(), size, 0, 1024 * 1024));
    }
    public async Task<UploadProgress> UploadChunkAsync(Guid id, long start, Stream data, long length, CancellationToken cancellationToken)
    {
        _upload.Position = start;
        await data.CopyToAsync(_upload, cancellationToken);
        var complete = _upload.Length == _size;
        if (!complete) return new UploadProgress(id, _size, _upload.Length, false, false, null, null, null);
        var hash = Convert.ToHexString(SHA256.HashData(_upload.ToArray())).ToLowerInvariant();
        var artifact = new CloudArtifact("fake-cloud-" + Guid.NewGuid().ToString("N"), Started!.FileName, Started.Kind, Started.Category, Started.Version, Started.Pipeline, Started.Source, Started.License, _size, DateTimeOffset.UtcNow, hash, "PASS", null);
        _generated.Add(artifact);
        return new UploadProgress(id, _size, _upload.Length, true, false, hash, artifact, null);
    }
    public Task<UploadProgress> UploadStatusAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(new UploadProgress(id, _size, _upload.Length, _upload.Length == _size, false, null, null, null));
    public async Task<VerifiedDownload> DownloadVerifiedAsync(string id, CancellationToken cancellationToken)
    {
        var generated = _generated.FirstOrDefault(item => item.Id == id);
        var bytes = generated is not null ? _upload.ToArray() : SeedDownloadBytes ?? _upload.ToArray();
        var name = generated?.Name ?? SeedDownloadName ?? "fake.detot.enc";
        var path = Path.Combine(Path.GetTempPath(), "detot-fake-cloud-" + Guid.NewGuid().ToString("N") + Path.GetExtension(name));
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new VerifiedDownload(path, name, "application/octet-stream", bytes.Length, sha);
    }
    public Task<bool> VerifyAsync(string id, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task DeleteAsync(string id, CancellationToken cancellationToken) { _generated.RemoveAll(item => item.Id == id); return Task.CompletedTask; }
}

sealed class FakeHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new HttpClientHandler());
}

sealed class StaticResponseHttpClientFactory(Func<HttpResponseMessage> response) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new StaticResponseHandler(response), disposeHandler: true);
}

sealed class StaticResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
}
