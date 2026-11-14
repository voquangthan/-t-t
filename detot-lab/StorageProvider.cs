using System.Text.RegularExpressions;

public sealed record CloudStorageStatus(bool Enabled, bool Connected, string? Email, bool Available,
    long? AppFolderBytes, string? Message);

public sealed record CloudArtifact(string Id, string Name, string Kind, string Category, string Version,
    string Pipeline, string Source, string License, long Size, DateTimeOffset ModifiedTime,
    string Sha256, string Integrity, string? WebViewLink);

public sealed record UploadSpec(string Kind, string Category, string FileName, string Version,
    string Pipeline, string Source, string License);

public sealed record UploadSession(Guid Id, long Size, long ReceivedBytes, int ChunkSize);
public sealed record UploadProgress(Guid Id, long Size, long ReceivedBytes, bool Complete,
    bool Duplicate, string? Sha256, CloudArtifact? Artifact, string? Message);
public sealed record VerifiedDownload(string Path, string Name, string ContentType, long Size, string Sha256);

public interface IStorageProvider
{
    Task<CloudStorageStatus> StatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudArtifact>> ListAsync(string? kind, string? pipeline, CancellationToken cancellationToken);
    Task<UploadSession> StartUploadAsync(UploadSpec spec, long size, CancellationToken cancellationToken);
    Task<UploadProgress> UploadChunkAsync(Guid id, long start, Stream data, long length, CancellationToken cancellationToken);
    Task<UploadProgress> UploadStatusAsync(Guid id, CancellationToken cancellationToken);
    Task<VerifiedDownload> DownloadVerifiedAsync(string id, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(string id, CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
}

public sealed class DatasetManager(IStorageProvider storage)
{
    public const long MaxDatasetBytesForProvider = 4L * 1024 * 1024 * 1024;
    private const long MaxDatasetBytes = MaxDatasetBytesForProvider;
    private static readonly HashSet<string> DatasetExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".jsonl", ".csv", ".parquet" };
    private static readonly Dictionary<string, HashSet<string>> ArtifactExtensions = new(StringComparer.Ordinal)
    {
        ["model"] = new(StringComparer.OrdinalIgnoreCase) { ".gguf", ".safetensors", ".onnx", ".bin" },
        ["backup"] = new(StringComparer.OrdinalIgnoreCase) { ".enc" },
        ["training-result"] = new(StringComparer.OrdinalIgnoreCase) { ".json", ".jsonl", ".csv", ".parquet" },
        ["document"] = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".md", ".txt", ".docx", ".xlsx", ".pptx" },
    };
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
        { "dataset", "model", "backup", "training-result", "document" };
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
        { "vietnamese", "programming", "it-support", "agent-training" };

    public Task<CloudStorageStatus> StatusAsync(CancellationToken cancellationToken) => storage.StatusAsync(cancellationToken);

    public async Task<IReadOnlyList<CloudArtifact>> ListAsync(string? kind, string? pipeline, CancellationToken cancellationToken)
    {
        if (kind is not null && !Kinds.Contains(kind)) throw new ArgumentException("Loại dữ liệu không hợp lệ.");
        if (pipeline is { Length: > 80 }) throw new ArgumentException("Tên pipeline quá dài.");
        return await storage.ListAsync(kind, pipeline, cancellationToken);
    }

    public Task<UploadSession> StartUploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        if (request.Size is <= 0 or > MaxDatasetBytes) throw new ArgumentException("Kích thước tệp phải từ 1 byte đến 4 GiB.");
        var kind = request.Kind?.Trim() ?? "";
        var category = request.Category?.Trim() ?? "";
        var name = Path.GetFileName(request.FileName?.Trim() ?? "");
        var version = request.Version?.Trim() ?? "";
        var pipeline = request.Pipeline?.Trim() ?? "";
        var source = request.Source?.Trim() ?? "";
        var license = request.License?.Trim() ?? "";

        if (!Kinds.Contains(kind)) throw new ArgumentException("Loại artifact không hợp lệ.");
        if (kind == "dataset" && !Categories.Contains(category)) throw new ArgumentException("Chọn một nhóm dataset hợp lệ.");
        if (kind != "dataset" && category.Length != 0) throw new ArgumentException("Chỉ dataset mới có nhóm dữ liệu.");
        if (name.Length is < 1 or > 180 || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Tên tệp không hợp lệ.");
        if (!Regex.IsMatch(version, @"^[A-Za-z0-9][A-Za-z0-9._+-]{0,39}$"))
            throw new ArgumentException("Phiên bản chỉ dùng chữ, số, dấu chấm, gạch dưới, cộng hoặc gạch ngang.");
        if (kind == "dataset" && !DatasetExtensions.Contains(Path.GetExtension(name)))
            throw new ArgumentException("Dataset chỉ hỗ trợ JSON, JSONL, CSV và Parquet.");
        if (kind != "dataset" && !ArtifactExtensions[kind].Contains(Path.GetExtension(name)))
            throw new ArgumentException("Định dạng tệp không được hỗ trợ cho loại dữ liệu đã chọn.");
        if (source.Length is < 1 or > 1000 || license.Length is < 1 or > 300 || pipeline.Length > 80)
            throw new ArgumentException("Nguồn và giấy phép là bắt buộc; kiểm tra độ dài metadata.");
        if (license.Contains("unknown", StringComparison.OrdinalIgnoreCase) || license.Contains("chưa rõ", StringComparison.OrdinalIgnoreCase) ||
            license.Contains("không rõ", StringComparison.OrdinalIgnoreCase) || license.Equals("unspecified", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Không tải dataset khi chưa xác định được giấy phép.");

        var spec = new UploadSpec(kind, category, name, version, pipeline, source, license);
        return storage.StartUploadAsync(spec, request.Size, cancellationToken);
    }

    public sealed record UploadRequest(string? Kind, string? Category, string? FileName, string? Version,
        string? Pipeline, string? Source, string? License, long Size);
}
