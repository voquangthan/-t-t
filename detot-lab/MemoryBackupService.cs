using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class MemoryBackupService
{
    private const int KdfIterations = 600_000;
    private const int CryptoChunkBytes = 8 * 1024 * 1024;
    private const long MaxBackupBytes = DatasetManager.MaxDatasetBytesForProvider;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DETOBAK1");
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".jsonl", ".md", ".txt", ".csv", ".yaml", ".yml", ".toml", ".xml" };
    private static readonly Regex CredentialAssignment = new(
        "(?i)(?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|password|authorization|credential)\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?\\S+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] SecretPatterns = ["token", "secret", "password", "credential", "oauth", "refresh", "api-key", "apikey", ".env", "cookie"];
    private readonly IStorageProvider _storage;
    private readonly string _workDirectory;
    private readonly string _node;
    private readonly string _revocationAuthority;
    private readonly string _preflightScript;
    private readonly Dictionary<string, BackupSource> _sources;
    private readonly Dictionary<Guid, RestoredArchive> _restored = new();

    public MemoryBackupService(IStorageProvider storage, string workDirectory, IConfiguration configuration, string contentRoot)
    {
        _storage = storage;
        _workDirectory = Path.Combine(workDirectory, "backup-work");
        _node = configuration["DETOT_LAB_NODE"] ?? "";
        _revocationAuthority = configuration["DETOT_COPY_AUTHORITY_DIR"] ?? "";
        _preflightScript = Path.Combine(contentRoot, "tests", "runtime-candidate", "check-restored-state.mjs");
        _sources = new Dictionary<string, BackupSource>(StringComparer.Ordinal);
        Add("memory-core", "Memory Core", configuration["DETOT_MEMORY_CORE_PATH"]);
        Add("dreaming", "Dreaming", configuration["DETOT_DREAMING_PATH"]);
        Add("related", "Dữ liệu liên quan", configuration["DETOT_MEMORY_RELATED_PATH"]);
        CleanupOrphanedWorkFiles();

        void Add(string id, string label, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) _sources[id] = new BackupSource(id, label, Path.GetFullPath(path));
        }
    }

    public IReadOnlyList<BackupSourceInfo> Sources()
    {
        return _sources.Values.Select(source =>
        {
            if (!Directory.Exists(source.Path)) return new BackupSourceInfo(source.Id, source.Label, false, 0, 0);
            var files = EnumerateSafeFiles(source).ToArray();
            return new BackupSourceInfo(source.Id, source.Label, true, files.Length, files.Sum(file => file.Size));
        }).ToArray();
    }

    public async Task<BackupResult> CreateAsync(string[]? selected, string? passphrase, CancellationToken cancellationToken)
    {
        ValidatePassphrase(passphrase);
        if (selected is not { Length: > 0 } || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length ||
            selected.Any(id => !_sources.ContainsKey(id)))
            throw new ArgumentException("Chọn ít nhất một nguồn backup đã được cấu hình.");
        var selectedSources = selected.Select(id => _sources[id]).ToArray();
        if (selectedSources.Any(source => !Directory.Exists(source.Path)))
            throw new ArgumentException("Một nguồn backup đã cấu hình nhưng thư mục không tồn tại.");

        Directory.CreateDirectory(_workDirectory);
        var id = Guid.NewGuid();
        var zipPath = Path.Combine(_workDirectory, id.ToString("N") + ".zip");
        var encryptedPath = Path.Combine(_workDirectory, id.ToString("N") + ".detot.enc");
        try
        {
            var manifest = await CreateZip(zipPath, selectedSources, cancellationToken);
            await Encrypt(zipPath, encryptedPath, passphrase!, cancellationToken);
            var size = new FileInfo(encryptedPath).Length;
            if (size > MaxBackupBytes) throw new ArgumentException("Backup vượt quá giới hạn 4 GiB.");
            var filename = $"detot-memory-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.detot.enc";
            var spec = new UploadSpec("backup", "", filename, DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"), "",
                string.Join(",", selectedSources.Select(source => source.Id)), "private-user-data");
            var session = await _storage.StartUploadAsync(spec, size, cancellationToken);
            var received = session.ReceivedBytes;
            await using var input = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            while (received < size)
            {
                input.Position = received;
                var length = Math.Min(session.ChunkSize, size - received);
                var progress = await _storage.UploadChunkAsync(session.Id, received, input, length, cancellationToken);
                if (progress.Complete)
                {
                    if (!progress.Duplicate && progress.Artifact is null) throw new IOException("Drive không xác nhận backup.");
                    return new BackupResult(progress.Artifact?.Id, progress.Artifact?.Name ?? filename,
                        manifest.Files.Length, manifest.TotalBytes, progress.Sha256 ?? "", progress.Duplicate,
                        "Backup đã mã hóa trước khi truyền. Manifest và SHA-256 đã được đính kèm.");
                }
                if (progress.ReceivedBytes <= received)
                    throw new IOException("Tải lên tạm ngắt. Có thể tiếp tục phiên trong danh sách tải lên.");
                received = progress.ReceivedBytes;
            }
            throw new IOException("Drive chưa xác nhận hoàn tất backup.");
        }
        finally
        {
            TryDelete(zipPath);
            TryDelete(encryptedPath);
        }
    }

    public async Task<RestoreReady> RestoreToStagingAsync(string fileId, string? passphrase, CancellationToken cancellationToken)
    {
        ValidatePassphrase(passphrase);
        if (!File.Exists(_preflightScript) || !File.Exists(_node) || !Directory.Exists(_revocationAuthority))
            throw new InvalidOperationException("Khôi phục bị khóa: cần Node runtime và danh sách thu hồi session bên ngoài snapshot.");

        var cloud = await _storage.DownloadVerifiedAsync(fileId, cancellationToken);
        Directory.CreateDirectory(_workDirectory);
        var id = Guid.NewGuid();
        var zipPath = Path.Combine(_workDirectory, id.ToString("N") + ".restored.zip");
        var stagePath = Path.Combine(_workDirectory, id.ToString("N") + ".stage");
        try
        {
            await Decrypt(cloud.Path, zipPath, passphrase!, cancellationToken);
            var manifest = await ExtractAndVerify(zipPath, stagePath, cancellationToken);
            await VerifyRevocations(stagePath, cancellationToken);
            lock (_restored)
            {
                ExpireRestores();
                _restored[id] = new RestoredArchive(zipPath, DateTimeOffset.UtcNow.AddMinutes(15), manifest.Files.Length);
            }
            zipPath = "";
            return new RestoreReady(id, manifest.Files.Length, manifest.TotalBytes,
                "PASS: mã hóa, manifest, SHA-256 và danh sách thu hồi session đều đã kiểm tra. Tệp được giải nén trong vùng tạm, chưa ghi đè Memory Core hoặc Dreaming.");
        }
        finally
        {
            TryDelete(cloud.Path);
            if (zipPath.Length > 0) TryDelete(zipPath);
            TryDeleteDirectory(stagePath);
        }
    }

    public RestoredArchive TakeRestore(Guid id)
    {
        lock (_restored)
        {
            ExpireRestores();
            if (!_restored.Remove(id, out var archive)) throw new ArgumentException("Phiên khôi phục đã hết hạn hoặc đã tải xuống.");
            return archive;
        }
    }

    public sealed record BackupSourceInfo(string Id, string Label, bool Available, int Files, long Bytes);
    public sealed record BackupResult(string? ArtifactId, string Name, int Files, long Bytes, string Sha256,
        bool Duplicate, string Message);
    public sealed record RestoreReady(Guid Id, int Files, long Bytes, string Message);
    public sealed record BackupRequest(string[]? Sources, string? Passphrase);
    public sealed record RestoreRequest(string? Passphrase);
    public sealed record RestoredArchive(string Path, DateTimeOffset ExpiresUtc, int Files);
    private sealed record BackupSource(string Id, string Label, string Path);
    private sealed record Manifest(int Version, DateTimeOffset CreatedUtc, string[] Sources, ManifestFile[] Files, long TotalBytes);
    private sealed record ManifestFile(string Path, long Bytes, string Sha256);

    private static void ValidatePassphrase(string? passphrase)
    {
        if (passphrase is null || passphrase.Length is < 16 or > 200)
            throw new ArgumentException("Backup cần cụm mật khẩu riêng, dài từ 16 đến 200 ký tự. Mật khẩu không được lưu.");
    }

    private static IEnumerable<SafeFile> EnumerateSafeFiles(BackupSource source)
    {
        var root = Path.GetFullPath(source.Path);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || IsSecretPath(Path.GetRelativePath(root, path))) continue;
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                var info = new FileInfo(path);
                if (info.Exists && ContainsCredentialMaterial(path)) yield return new SafeFile(source, path, info.Length);
            }
        }
    }

    private static bool IsSecretPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(part => SecretPatterns.Any(pattern => part.Contains(pattern, StringComparison.OrdinalIgnoreCase))) ||
            parts.Any(part => part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || part.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsCredentialMaterial(string path)
    {
        if (!TextExtensions.Contains(Path.GetExtension(path))) return false;
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
                if (CredentialAssignment.IsMatch(line) || line.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)) return false;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void CleanupOrphanedWorkFiles()
    {
        if (!Directory.Exists(_workDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(_workDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".restored.zip", StringComparison.OrdinalIgnoreCase) &&
                File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1)) continue;
            TryDelete(path);
        }
        foreach (var path in Directory.EnumerateDirectories(_workDirectory, "*.stage", SearchOption.TopDirectoryOnly))
            TryDeleteDirectory(path);
    }

    private static async Task<Manifest> CreateZip(string path, BackupSource[] sources, CancellationToken cancellationToken)
    {
        var entries = new List<ManifestFile>();
        long total = 0;
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var source in sources)
            foreach (var safe in EnumerateSafeFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (safe.Size > MaxBackupBytes || total + safe.Size > MaxBackupBytes)
                    throw new ArgumentException("Tổng dữ liệu chọn vượt giới hạn backup 4 GiB.");
                var relative = Path.GetRelativePath(source.Path, safe.Path).Replace('\\', '/');
                var archiveName = source.Id + "/" + relative;
                var entry = archive.CreateEntry(archiveName, CompressionLevel.Fastest);
                await using var input = new FileStream(safe.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = entry.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    count += read;
                }
                var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                entries.Add(new ManifestFile(archiveName, count, sha));
                total += count;
            }
            var manifest = new Manifest(1, DateTimeOffset.UtcNow, sources.Select(source => source.Id).ToArray(), entries.ToArray(), total);
            var manifestEntry = archive.CreateEntry("_manifest.json", CompressionLevel.Fastest);
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest, Json, cancellationToken);
            return manifest;
        }
    }

    private static async Task Encrypt(string source, string target, string passphrase, CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var noncePrefix = RandomNumberGenerator.GetBytes(4);
        var header = new byte[36];
        Magic.CopyTo(header, 0); salt.CopyTo(header, 8); noncePrefix.CopyTo(header, 24);
        BitConverter.GetBytes(KdfIterations).CopyTo(header, 28);
        BitConverter.GetBytes(CryptoChunkBytes).CopyTo(header, 32);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            await output.WriteAsync(header, cancellationToken);
            using var aes = new AesGcm(key, 16);
            var plain = new byte[CryptoChunkBytes];
            var cipher = new byte[CryptoChunkBytes];
            var tag = new byte[16];
            var lengthBytes = new byte[4];
            ulong counter = 0;
            int read;
            while ((read = await input.ReadAsync(plain, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nonce = Nonce(noncePrefix, counter);
                var aad = AssociatedData(header, counter, read);
                aes.Encrypt(nonce, plain.AsSpan(0, read), cipher.AsSpan(0, read), tag, aad);
                BitConverter.GetBytes(read).CopyTo(lengthBytes, 0);
                await output.WriteAsync(lengthBytes, cancellationToken);
                await output.WriteAsync(tag, cancellationToken);
                await output.WriteAsync(cipher.AsMemory(0, read), cancellationToken);
                counter++;
            }
            Array.Clear(plain);
            BitConverter.GetBytes(0).CopyTo(lengthBytes, 0);
            await output.WriteAsync(lengthBytes, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task Decrypt(string source, string target, string passphrase, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(source);
        var header = new byte[36];
        await ReadExactly(input, header, cancellationToken);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Định dạng backup không được hỗ trợ.");
        var iterations = BitConverter.ToInt32(header, 28);
        var chunkSize = BitConverter.ToInt32(header, 32);
        if (iterations != KdfIterations || chunkSize != CryptoChunkBytes) throw new InvalidDataException("Thông số mã hóa backup không hợp lệ.");
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, header.AsSpan(8, 16), iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            using var aes = new AesGcm(key, 16);
            var lengthBytes = new byte[4];
            var tag = new byte[16];
            ulong counter = 0;
            long total = 0;
            while (true)
            {
                await ReadExactly(input, lengthBytes, cancellationToken);
                var length = BitConverter.ToInt32(lengthBytes, 0);
                if (length == 0) break;
                if (length is < 1 or > CryptoChunkBytes || total + length > MaxBackupBytes)
                    throw new InvalidDataException("Kích thước khối backup không hợp lệ.");
                await ReadExactly(input, tag, cancellationToken);
                var cipher = new byte[length];
                await ReadExactly(input, cipher, cancellationToken);
                var plain = new byte[length];
                var nonce = Nonce(header.AsSpan(24, 4), counter);
                aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(header, counter, length));
                await output.WriteAsync(plain, cancellationToken);
                CryptographicOperations.ZeroMemory(plain);
                total += length;
                counter++;
            }
            if (input.Position != input.Length) throw new InvalidDataException("Có dữ liệu thừa sau backup.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<Manifest> ExtractAndVerify(string zipPath, string stagePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagePath);
        var root = Path.GetFullPath(stagePath) + Path.DirectorySeparatorChar;
        Manifest? manifest = null;
        long total = 0;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.Equals("_manifest.json", StringComparison.Ordinal))
            {
                await using var stream = entry.Open();
                manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, Json, cancellationToken);
                continue;
            }
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Đường dẫn trong backup không hợp lệ.");
            if (entry.Length > MaxBackupBytes || total + entry.Length > MaxBackupBytes) throw new InvalidDataException("Backup giải nén vượt giới hạn 4 GiB.");
            var outputPath = Path.GetFullPath(Path.Combine(stagePath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!outputPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Đường dẫn backup thoát khỏi vùng tạm.");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var input = entry.Open();
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            var buffer = new byte[1024 * 1024];
            int read;
            long count = 0;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                count += read;
            }
            total += count;
        }
        if (manifest is null || manifest.Version != 1) throw new InvalidDataException("Backup thiếu manifest hợp lệ.");
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var actualFiles = Directory.EnumerateFiles(stagePath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(stagePath, path).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (!expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actualFiles) || manifest.Files.Sum(file => file.Bytes) != manifest.TotalBytes)
            throw new InvalidDataException("Manifest không khớp với các tệp backup.");
        foreach (var item in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(stagePath, item.Path.Replace('/', Path.DirectorySeparatorChar));
            await using var input = File.OpenRead(path);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
            if (input.Length != item.Bytes || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(item.Sha256)))
                throw new InvalidDataException("SHA-256 trong manifest không khớp.");
        }
        return manifest;
    }

    private async Task VerifyRevocations(string stagePath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_node)
        {
            WorkingDirectory = Path.GetDirectoryName(_preflightScript)!,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(_preflightScript);
        start.ArgumentList.Add(stagePath);
        start.ArgumentList.Add(_revocationAuthority);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Không chạy được kiểm tra thu hồi session.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var errorRead = process.StandardError.ReadToEndAsync(deadline.Token);
        var outputRead = process.StandardOutput.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await outputRead;
        var error = await errorRead;
        if (process.ExitCode != 0) throw new InvalidDataException("Khôi phục bị từ chối bởi kiểm tra thu hồi session." + (string.IsNullOrWhiteSpace(error) ? "" : " " + error.Trim()));
        _ = output;
    }

    private static async Task ReadExactly(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("Backup bị cắt giữa lúc truyền.");
            offset += read;
        }
    }

    private static byte[] Nonce(ReadOnlySpan<byte> prefix, ulong counter)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce);
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(unchecked((long)counter))).CopyTo(nonce, 4);
        return nonce;
    }

    private static byte[] AssociatedData(byte[] header, ulong counter, int length)
    {
        var aad = new byte[header.Length + 12];
        header.CopyTo(aad, 0);
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(unchecked((long)counter))).CopyTo(aad, header.Length);
        BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(length)).CopyTo(aad, header.Length + 8);
        return aad;
    }

    private static async Task ReadExactly(Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
        await ReadExactly(stream, buffer.AsMemory(), cancellationToken);

    private void ExpireRestores()
    {
        foreach (var (id, archive) in _restored.Where(item => item.Value.ExpiresUtc < DateTimeOffset.UtcNow).ToArray())
        {
            TryDelete(archive.Path);
            _restored.Remove(id);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record SafeFile(BackupSource Source, string Path, long Size);
}
