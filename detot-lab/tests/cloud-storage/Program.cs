using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("dataset manager accepts licensed supported dataset metadata", DatasetAccepts),
    ("dataset manager rejects unknown license and unsupported formats", DatasetRejects),
    ("backup encrypts source data before provider upload", BackupIsEncrypted),
    ("restore verifies encryption and external session revocation authority", RestoreIsVerified),
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
    await Throws<ArgumentException>(() => manager.StartUploadAsync(new("dataset", "vietnamese", "faq.exe", "v1", "", "source", "CC BY 4.0", 12), CancellationToken.None));
    Assert(provider.Started is null, "invalid metadata must not start a cloud upload");
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

    public Task<CloudStorageStatus> StatusAsync(CancellationToken cancellationToken) => Task.FromResult(new CloudStorageStatus(true, true, "test@example.invalid", true, _upload.Length, null));
    public Task<IReadOnlyList<CloudArtifact>> ListAsync(string? kind, string? pipeline, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudArtifact>>([]);
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
        var artifact = new CloudArtifact("test-id", Started!.FileName, Started.Kind, Started.Category, Started.Version, Started.Pipeline, Started.Source, Started.License, _size, DateTimeOffset.UtcNow, hash, "PASS", null);
        return new UploadProgress(id, _size, _upload.Length, true, false, hash, artifact, null);
    }
    public Task<UploadProgress> UploadStatusAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(new UploadProgress(id, _size, _upload.Length, _upload.Length == _size, false, null, null, null));
    public async Task<VerifiedDownload> DownloadVerifiedAsync(string id, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), "detot-fake-cloud-" + Guid.NewGuid().ToString("N") + ".enc");
        var bytes = _upload.ToArray();
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new VerifiedDownload(path, "fake.detot.enc", "application/octet-stream", bytes.Length, sha);
    }
    public Task<bool> VerifyAsync(string id, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task DeleteAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;
}
