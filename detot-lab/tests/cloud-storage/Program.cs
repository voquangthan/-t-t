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
        Assert(evidence.Count == 1 && evidence[0].Citation == "drive://drive-learning-1", "RAG results must preserve the exact source citation");
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
