using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

public sealed class GoogleDriveStorage : IStorageProvider
{
    private const string Scope = "https://www.googleapis.com/auth/drive.file";
    private const string OwnerKey = "detotOwner";
    private const string OwnerValue = "detot-ai-v1";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const int ChunkSize = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, string> FolderNames = new(StringComparer.Ordinal)
    {
        ["root"] = "DETOT-AI", ["datasets"] = "datasets",
        ["dataset-vietnamese"] = "vietnamese", ["dataset-programming"] = "programming",
        ["dataset-it-support"] = "it-support", ["dataset-agent-training"] = "agent-training",
        ["models"] = "models", ["memory-backups"] = "memory-backups",
        ["training-results"] = "training-results", ["documents"] = "documents",
    };

    private readonly string _origin;
    private readonly string _host;
    private readonly string _callback;
    private readonly string _directory;
    private readonly string _accountPath;
    private readonly string _sessionsDirectory;
    private readonly string _stagingDirectory;
    private readonly string _historyPath;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly string _loginHint;
    private readonly bool _secure;
    private readonly bool _enabled;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _clients;
    private readonly MemoryBackupService _backups;
    private readonly ConsentPolicy? _consent;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;

    public static GoogleDriveStorage Map(WebApplication app, string directory, string origin, bool secure, string loginHint,
        ConsentPolicy? consent = null)
    {
        var storage = new GoogleDriveStorage(app, directory, origin, secure, loginHint, consent);
        storage.MapRoutes(app);
        return storage;
    }

    private GoogleDriveStorage(WebApplication app, string directory, string origin, bool secure, string loginHint,
        ConsentPolicy? consent)
    {
        _origin = origin.TrimEnd('/');
        _host = new Uri(_origin).Authority;
        _callback = _origin + "/api/drive/callback";
        _directory = Path.Combine(directory, "drive");
        _accountPath = Path.Combine(_directory, "account.bin");
        _sessionsDirectory = Path.Combine(_directory, "uploads");
        _stagingDirectory = Path.Combine(_directory, "upload-staging");
        _historyPath = Path.Combine(_directory, "history.bin");
        var driveClientId = app.Configuration["DETOT_GOOGLE_DRIVE_CLIENT_ID"];
        var driveClientSecret = app.Configuration["DETOT_GOOGLE_DRIVE_CLIENT_SECRET"];
        var dedicatedClient = !string.IsNullOrWhiteSpace(driveClientId) || !string.IsNullOrWhiteSpace(driveClientSecret);
        _clientId = dedicatedClient ? driveClientId : app.Configuration["DETOT_GOOGLE_CLIENT_ID"];
        _clientSecret = dedicatedClient ? driveClientSecret : app.Configuration["DETOT_GOOGLE_CLIENT_SECRET"];
        _loginHint = loginHint.Trim();
        _secure = secure;
        _enabled = OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret);
        _protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Detot.GoogleDrive.v1");
        _clients = app.Services.GetRequiredService<IHttpClientFactory>();
        _backups = new MemoryBackupService(this, _directory, app.Configuration, app.Environment.ContentRootPath);
        _consent = consent;
    }

    private void MapRoutes(WebApplication app)
    {
        Func<HttpContext, Task<IResult>> statusRoute = StatusRoute;
        app.MapGet("/api/drive/status", (Delegate)statusRoute);
        app.MapPost("/api/drive/connect", (Delegate)((HttpContext context) => Guard(context, () => ConnectRoute(context), requireAccount: false)));
        Func<HttpContext, Task<IResult>> callbackRoute = CallbackRoute;
        app.MapGet("/api/drive/callback", (Delegate)callbackRoute);
        Func<HttpContext, Task<IResult>> refreshRoute = context => Guard(context, async () =>
        {
            _accessToken = null;
            var token = await AccessToken(context.RequestAborted);
            return Results.Ok(new { refreshed = true, email = LoadAccount()!.Email, expiresInSeconds = Math.Max(0, (int)(_accessTokenExpires - DateTimeOffset.UtcNow).TotalSeconds), tokenPresent = token.Length > 0 });
        });
        app.MapPost("/api/drive/refresh", (Delegate)refreshRoute);
        Func<HttpContext, Task<IResult>> disconnectRoute = context => Guard(context, () => DisconnectRoute(context));
        app.MapDelete("/api/drive/connect", (Delegate)disconnectRoute);
        app.MapGet("/api/drive/items", (string? kind, string? pipeline, HttpContext context) =>
            Guard(context, async () => Results.Ok(await ListAsync(kind, pipeline, context.RequestAborted))));
        app.MapPost("/api/drive/uploads", (DatasetManager.UploadRequest request, HttpContext context) => Guard(context, async () =>
        {
            var manager = new DatasetManager(this);
            return Results.Ok(await manager.StartUploadAsync(request, context.RequestAborted));
        }));
        app.MapGet("/api/drive/uploads/{id:guid}", (Guid id, HttpContext context) =>
            Guard(context, async () => Results.Ok(await UploadStatusAsync(id, context.RequestAborted))));
        var uploadChunk = app.MapPut("/api/drive/uploads/{id:guid}", (Guid id, HttpContext context) => Guard(context, async () =>
        {
            if (!ContentRangeHeaderValue.TryParse(context.Request.Headers.ContentRange, out var range) ||
                range?.From is not long start || range.To is not long end || range.Length is null ||
                context.Request.ContentLength is not long length || length != end - start + 1 || length is <= 0 or > ChunkSize)
                return Results.BadRequest(new { error = "Content-Range không hợp lệ; mỗi khối tối đa 8 MiB." });
            return Results.Ok(await UploadChunkAsync(id, start, context.Request.Body, length, context.RequestAborted));
        }));
        uploadChunk.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ChunkSize + 1024));
        app.MapGet("/api/drive/items/{id}/download", async (string id, HttpContext context) =>
        {
            var result = await Guard(context, async () =>
            {
                var file = await DownloadVerifiedAsync(id, context.RequestAborted);
                context.Response.Headers["X-Detot-SHA256"] = file.Sha256;
                var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                return Results.Stream(stream, file.ContentType, file.Name, enableRangeProcessing: false);
            });
            return result;
        });
        app.MapPost("/api/drive/items/{id}/verify", (string id, HttpContext context) => Guard(context, async () =>
        {
            var passed = await VerifyAsync(id, context.RequestAborted);
            RecordHistory("verify", id, passed ? "PASS" : "FAIL", null);
            return Results.Ok(new { result = passed ? "PASS" : "FAIL" });
        }));
        app.MapPut("/api/drive/items/{id}/metadata", (string id, DatasetMetadataEdit edit, HttpContext context) => Guard(context, async () =>
            Results.Ok(await UpdateDatasetMetadataAsync(id, edit, context.RequestAborted))));
        app.MapDelete("/api/drive/items/{id}", (string id, HttpContext context) => Guard(context, async () =>
        {
            await DeleteAsync(id, context.RequestAborted);
            RecordHistory("delete-cloud", id, "MOVED_TO_TRASH", null);
            return Results.NoContent();
        }));
        app.MapGet("/api/drive/history", (Delegate)((HttpContext context) => Guard(context, () =>
            Task.FromResult<IResult>(Results.Ok(ReadHistory().OrderByDescending(row => row.Utc).Take(100))))));
        app.MapGet("/api/drive/backups/sources", (Delegate)((HttpContext context) => Guard(context, () =>
            Task.FromResult<IResult>(!CheckConsent(context, "memory_core", "direct_request", "local_only", "memory_backup_sources")
                ? Results.StatusCode(403) : Results.Ok(_backups.Sources())))));
        app.MapPost("/api/drive/backups", (MemoryBackupService.BackupRequest request, HttpContext context) => Guard(context, async () =>
        {
            if (request.Sources?.Any(source => source is "memory-core" or "dreaming") == true &&
                !CheckConsent(context, "memory_core", "direct_request", "external_service", "memory_backup"))
                return Results.StatusCode(403);
            var result = await _backups.CreateAsync(request.Sources, request.Passphrase, context.RequestAborted);
            return Results.Ok(result);
        }));
        app.MapPost("/api/drive/backups/{id}/restore", (string id, MemoryBackupService.RestoreRequest request, HttpContext context) => Guard(context, async () =>
        {
            if (!CheckConsent(context, "memory_core", "direct_request", "external_service", "memory_restore"))
                return Results.StatusCode(403);
            if (id.Length is < 5 or > 256 || !Regex.IsMatch(id, "^[A-Za-z0-9_-]+$")) return Results.BadRequest();
            var ready = await _backups.RestoreToStagingAsync(id, request.Passphrase, context.RequestAborted);
            return Results.Ok(new { ready.Files, ready.Bytes, ready.Message, downloadUrl = $"/api/drive/restores/{ready.Id}/download" });
        }));
        app.MapGet("/api/drive/restores/{id:guid}/download", async (Guid id, HttpContext context) => await Guard(context, () =>
        {
            if (!CheckConsent(context, "memory_core", "direct_request", "external_service", "memory_restore_download"))
                return Task.FromResult<IResult>(Results.StatusCode(403));
            var archive = _backups.TakeRestore(id);
            if (!File.Exists(archive.Path)) return Task.FromResult<IResult>(Results.NotFound());
            var stream = new FileStream(archive.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return Task.FromResult<IResult>(Results.Stream(stream, "application/zip", "detot-memory-restored.zip", enableRangeProcessing: false));
        }));
        app.MapPost("/api/drive/retention", (RetentionRequest request, HttpContext context) => Guard(context, async () =>
        {
            if (request.Keep is < 1 or > 100) return Results.BadRequest(new { error = "Giữ từ 1 đến 100 bản backup." });
            var backups = (await ListAsync("backup", null, context.RequestAborted)).OrderByDescending(item => item.ModifiedTime).ToArray();
            var removed = 0;
            foreach (var item in backups.Skip(request.Keep))
            {
                await DeleteAsync(item.Id, context.RequestAborted);
                removed++;
            }
            RecordHistory("retention", $"keep:{request.Keep}", $"MOVED_TO_TRASH:{removed}", null);
            return Results.Ok(new { retained = Math.Min(request.Keep, backups.Length), movedToTrash = removed,
                message = "Các bản vượt retention được chuyển vào Thùng rác Drive và có thể khôi phục." });
        }));
    }

    private async Task<IResult> StatusRoute(HttpContext context)
    {
        if (!ValidRequest(context)) return Results.StatusCode(403);
        if (!_enabled) return Results.Ok(new CloudStorageStatus(false, false, null, false, null, "Cần Google OAuth trên máy chủ Windows."));
        var account = LoadAccount();
        if (account is null) return Results.Ok(new CloudStorageStatus(true, false, null, false, null, "Chưa kết nối Google Drive."));
        try { return Results.Ok(await StatusAsync(context.RequestAborted)); }
        catch (Exception error) when (IsRemoteError(error))
        {
            var message = DriveStatusMessage(error);
            context.RequestServices.GetService<ILogger<GoogleDriveStorage>>()?.LogWarning(
                "Drive status check failed ({ErrorType}, {Reason}).", error.GetType().Name,
                error is DriveApiException driveError ? DriveApiReason(driveError.Body) ?? "unknown" : "non-drive-api-error");
            return Results.Ok(new CloudStorageStatus(true, true, account.Email, false, null,
                message));
        }
    }

    private static string DriveStatusMessage(Exception error)
    {
        if (error is not DriveApiException drive) return "Drive đang tạm thời không truy cập được. Kiểm tra kết nối rồi thử lại.";
        var reason = DriveApiReason(drive.Body);
        var detail = DriveApiMessage(drive.Body);
        if (reason is "accessNotConfigured" or "SERVICE_DISABLED" || ContainsAny(detail, "has not been used", "has not been enabled", "API has not been", "API is disabled"))
            return "Google Drive API chưa được bật trong Google Cloud project của OAuth client. Bật Drive API rồi làm mới trang.";
        if (reason is "insufficientPermissions" or "insufficientFilePermissions" || ContainsAny(detail, "insufficient authentication scopes", "insufficient permission", "insufficient permissions"))
            return "Google Drive từ chối quyền hiện tại. Kiểm tra drive.file đã được cấp trong Google consent rồi làm mới trạng thái.";
        if (reason is "rateLimitExceeded" or "userRateLimitExceeded" or "backendError")
            return "Google Drive API đang giới hạn hoặc tạm lỗi. Thử lại sau một lát.";
        var safeDetail = new string(detail.Where(character => !char.IsControl(character)).Take(240).ToArray());
        if (string.IsNullOrWhiteSpace(safeDetail))
            safeDetail = new string(drive.Body.Where(character => !char.IsControl(character)).Take(240).ToArray());
        return $"Google Drive API trả HTTP {(int)drive.Status}{(string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})")}" +
            (string.IsNullOrWhiteSpace(safeDetail) ? ". Kiểm tra trạng thái API và quyền OAuth." : $": {safeDetail}");
    }

    private static bool ContainsAny(string value, params string[] phrases) =>
        phrases.Any(phrase => value.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private static string DriveApiMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("error").TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String ? message.GetString() ?? "" : "";
        }
        catch (JsonException) { return body; }
        catch (KeyNotFoundException) { return ""; }
    }

    private static string? DriveApiReason(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");
            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 &&
                errors[0].TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                return reason.GetString();
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0 &&
                details[0].TryGetProperty("reason", out var detailReason) && detailReason.ValueKind == JsonValueKind.String)
                return detailReason.GetString();
            if (error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                return status.GetString();
            return null;
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
    }

    private async Task<IResult> Guard(HttpContext context, Func<Task<IResult>> action, bool requireAccount = true)
    {
        if (!ValidRequest(context)) return Results.StatusCode(403);
        if (!_enabled) return Results.Problem("Google Drive chưa được cấu hình trên máy chủ.", statusCode: 503);
        await _gate.WaitAsync(context.RequestAborted);
        try
        {
            if (requireAccount && LoadAccount() is null)
                return Results.Problem("Hãy kết nối Google Drive trước.", statusCode: 409);
            return await action();
        }
        catch (StorageConflictException error) { return Results.Conflict(new { error = error.Message }); }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        catch (Exception error) when (IsRemoteError(error))
        {
            context.RequestServices.GetService<ILogger<GoogleDriveStorage>>()?.LogWarning("Drive request failed ({ErrorType}).", error.GetType().Name);
            return Results.Problem("Google Drive chưa xử lý được yêu cầu. Kiểm tra kết nối hoặc quyền OAuth rồi thử lại.", statusCode: 502);
        }
        finally { _gate.Release(); }
    }

    private bool CheckConsent(HttpContext context, string source, string purpose, string destination, string actor) =>
        _consent?.Check(context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "",
            [new ConsentPolicy.ConsentRequirement(source, purpose, destination)], actor, null, out _) == true;

    private bool ValidRequest(HttpContext context) =>
        string.Equals(context.Request.Host.Value, _host, StringComparison.OrdinalIgnoreCase) &&
        (HttpMethods.IsGet(context.Request.Method) || context.Request.Headers.Origin == _origin);

    private Task<IResult> ConnectRoute(HttpContext context)
    {
        var state = RandomToken();
        var verifier = RandomToken();
        var payload = _protector.Protect(JsonSerializer.Serialize(new OAuthState(state, verifier,
            DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()), Json));
        context.Response.Cookies.Append("detot_drive", payload, new CookieOptions
        {
            HttpOnly = true, Secure = _secure, SameSite = SameSiteMode.Lax,
            Path = "/api/drive/callback", MaxAge = TimeSpan.FromMinutes(5)
        });
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _clientId, ["redirect_uri"] = _callback, ["response_type"] = "code",
            ["scope"] = "openid email " + Scope, ["access_type"] = "offline", ["prompt"] = "consent select_account",
            ["state"] = state, ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256", ["include_granted_scopes"] = "false"
        };
        if (!string.IsNullOrWhiteSpace(_loginHint)) query["login_hint"] = _loginHint;
        return Task.FromResult<IResult>(Results.Ok(new { url = QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", query) }));
    }

    private async Task<IResult> CallbackRoute(HttpContext context)
    {
        if (!ValidRequest(context)) return Results.StatusCode(403);
        if (!_enabled) return Results.Problem("Google Drive chưa được cấu hình trên máy chủ.", statusCode: 503);
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Cookies.Delete("detot_drive", new CookieOptions { HttpOnly = true, Secure = _secure, SameSite = SameSiteMode.Lax, Path = "/api/drive/callback" });
        string outcome;
        try
        {
            if (!context.Request.Cookies.TryGetValue("detot_drive", out var protectedState)) return Results.BadRequest();
            var auth = JsonSerializer.Deserialize<OAuthState>(_protector.Unprotect(protectedState), Json)
                ?? throw new JsonException();
            if (auth.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
                !FixedEquals(auth.State, context.Request.Query["state"].ToString())) return Results.BadRequest();
            var code = context.Request.Query["code"].ToString();
            if (string.IsNullOrWhiteSpace(code)) return Results.Redirect("/app/index.html?drive=denied");

            using var http = NewGoogleClient();
            using var tokenResponse = await TokenRequest(http, new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = _callback,
                ["code_verifier"] = auth.Verifier
            }, context.RequestAborted);
            var token = tokenResponse.RootElement;
            var scope = Required(token, "scope");
            if (!IsDriveGrant(scope)) return Results.Redirect("/app/index.html?drive=scope_missing");
            var refresh = token.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : null;
            using var identity = await GoogleGet(http, "https://openidconnect.googleapis.com/v1/userinfo",
                Required(token, "access_token"), context.RequestAborted);
            if (!identity.RootElement.GetProperty("email_verified").GetBoolean()) return Results.BadRequest();
            var previous = LoadAccount();
            if (string.IsNullOrWhiteSpace(refresh)) refresh = previous?.RefreshToken;
            if (string.IsNullOrWhiteSpace(refresh)) return Results.Redirect("/app/index.html?drive=refresh_missing");
            SaveAccount(new DriveAccount(Required(identity.RootElement, "sub"), Required(identity.RootElement, "email"), refresh,
                scope, previous?.Folders ?? new Dictionary<string, string>(StringComparer.Ordinal)));
            await using var gate = await EnterGate(context.RequestAborted);
            await EnsureStructureAsync(context.RequestAborted);
            RecordHistory("connect", Required(identity.RootElement, "email"), "CONNECTED", null);
            outcome = "connected";
        }
        catch (Exception error) when (IsRemoteError(error))
        {
            context.RequestServices.GetService<ILogger<GoogleDriveStorage>>()?.LogWarning("Drive OAuth callback failed ({ErrorType}).", error.GetType().Name);
            outcome = "error";
        }
        return Results.Redirect("/app/index.html?drive=" + outcome);
    }

    private async Task<IAsyncDisposable> EnterGate(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new SemaphoreLease(_gate);
    }

    private async Task<IResult> DisconnectRoute(HttpContext context)
    {
        var account = LoadAccount();
        if (account is null) return Results.NoContent();
        TryDelete(_accountPath);
        _accessToken = null;
        RecordHistory("disconnect", account.Email, "LOCAL_TOKEN_REMOVED", null);
        return Results.NoContent();
    }

    public async Task<CloudStorageStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var account = LoadAccount();
        if (account is null) return new CloudStorageStatus(_enabled, false, null, false, null, "Chưa kết nối Google Drive.");
        await EnsureStructureAsync(cancellationToken);
        var items = await ListAsync(null, null, cancellationToken);
        return new CloudStorageStatus(_enabled, true, account.Email, true, items.Sum(item => item.Size),
            "Đang hiển thị dung lượng của thư mục DETOT-AI do ứng dụng quản lý; không yêu cầu quyền xem toàn bộ Drive.");
    }

    public async Task<IReadOnlyList<CloudArtifact>> ListAsync(string? kind, string? pipeline, CancellationToken cancellationToken)
    {
        var folders = await EnsureStructureAsync(cancellationToken);
        var files = new List<DriveFile>();
        foreach (var (folderKey, folderId) in folders.Where(pair => pair.Key != "root" && pair.Key != "datasets"))
        {
            if (kind == "dataset" && !folderKey.StartsWith("dataset-", StringComparison.Ordinal)) continue;
            if (kind is not null && kind != "dataset" && folderKey != FolderForKind(kind)) continue;
            files.AddRange(await ListChildren(folderId, cancellationToken));
        }
        return files.Select(ToArtifact)
            .Where(item => pipeline is null || item.Pipeline.Equals(pipeline, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ModifiedTime).ToArray();
    }

    public async Task<UploadSession> StartUploadAsync(UploadSpec spec, long size, CancellationToken cancellationToken)
    {
        if (size is <= 0 or > DatasetManager.MaxDatasetBytesForProvider)
            throw new ArgumentException("Kích thước tệp phải từ 1 byte đến 4 GiB.");
        var folders = await EnsureStructureAsync(cancellationToken);
        var folderKey = FolderForSpec(spec);
        if (!folders.TryGetValue(folderKey, out var folderId)) throw new InvalidOperationException("Không tạo được thư mục đích trong Drive.");
        var storedName = VersionedName(spec.FileName, spec.Version);
        var existing = await ListChildren(folderId, cancellationToken);
        if (existing.Any(file => file.Name.Equals(storedName, StringComparison.OrdinalIgnoreCase)))
            throw new StorageConflictException("Tên và phiên bản này đã tồn tại. Đổi version để tránh ghi đè.");

        var description = MetadataDescription(spec);
        var metadata = new Dictionary<string, object>
        {
            ["name"] = storedName, ["mimeType"] = ContentType(spec.FileName), ["parents"] = new[] { folderId },
            ["description"] = description,
            ["appProperties"] = new Dictionary<string, string>
            {
                [OwnerKey] = OwnerValue, ["detotKind"] = spec.Kind, ["detotCategory"] = spec.Category,
                ["detotVersion"] = spec.Version, ["detotPipeline"] = spec.Pipeline, ["detotSha256"] = "pending"
            }
        };
        using var http = NewGoogleClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=" + Uri.EscapeDataString(FileFields));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(cancellationToken));
        request.Headers.Add("X-Upload-Content-Type", ContentType(spec.FileName));
        request.Headers.Add("X-Upload-Content-Length", size.ToString(CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(metadata, options: Json);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccess(response);
        if (!response.Headers.Location?.IsAbsoluteUri ?? true) throw new HttpRequestException("Drive did not return an upload session.");
        var sessionUri = response.Headers.Location!;
        if (sessionUri.Scheme != Uri.UriSchemeHttps || !sessionUri.Host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Drive returned an unexpected upload host.");

        Directory.CreateDirectory(_sessionsDirectory);
        Directory.CreateDirectory(_stagingDirectory);
        var id = Guid.NewGuid();
        var stagedPath = Path.Combine(_stagingDirectory, id.ToString("N") + ".part");
        await using (File.Create(stagedPath)) { }
        SaveSession(new UploadState(id, sessionUri.ToString(), size, 0, spec, folderId, storedName, stagedPath, DateTimeOffset.UtcNow));
        RecordHistory("upload-start", storedName, "STARTED", null);
        return new UploadSession(id, size, 0, ChunkSize);
    }

    public async Task<UploadProgress> UploadChunkAsync(Guid id, long start, Stream data, long length, CancellationToken cancellationToken)
    {
        var state = LoadSession(id) ?? throw new ArgumentException("Không tìm thấy phiên tải lên.");
        if (start != state.NextOffset || length <= 0 || length > ChunkSize || start + length > state.Size)
            return new UploadProgress(id, state.Size, state.NextOffset, false, false, null, null,
                "Khối không liên tục; đọc tiến độ rồi tiếp tục tại byte đã xác nhận.");

        await using (var staged = new FileStream(state.StagingPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            staged.SetLength(start);
            staged.Position = start;
            await data.CopyToAsync(staged, cancellationToken);
            await staged.FlushAsync(cancellationToken);
        }
        if (new FileInfo(state.StagingPath).Length != start + length)
            throw new IOException("Kích thước khối nhận được không khớp.");

        var result = await SendStagedChunk(state, start, length, cancellationToken);
        if (result.CompletedFile is not null)
            return await CompleteUpload(state with { NextOffset = state.Size }, result.CompletedFile.Value, cancellationToken);

        var updated = state with { NextOffset = result.NextOffset };
        SaveSession(updated);
        return new UploadProgress(id, state.Size, result.NextOffset, false, false, null, null, result.Message);
    }

    public async Task<UploadProgress> UploadStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        var state = LoadSession(id) ?? throw new ArgumentException("Không tìm thấy phiên tải lên.");
        var progress = await QueryRemoteProgress(state, cancellationToken);
        if (progress.CompletedFile is not null)
            return await CompleteUpload(state with { NextOffset = state.Size }, progress.CompletedFile.Value, cancellationToken);
        var updated = state with { NextOffset = progress.NextOffset };
        SaveSession(updated);
        return new UploadProgress(id, state.Size, progress.NextOffset, false, false, null, null, progress.Message);
    }

    public async Task<VerifiedDownload> DownloadVerifiedAsync(string id, CancellationToken cancellationToken)
    {
        var file = await GetOwnedFile(id, cancellationToken);
        if (!TryAppValue(file, "detotSha256", out var expected) || !Regex.IsMatch(expected, "^[a-fA-F0-9]{64}$"))
            throw new InvalidOperationException("Tệp chưa có SHA-256 trong manifest và chưa thể xác minh.");
        Directory.CreateDirectory(_stagingDirectory);
        var path = Path.Combine(_stagingDirectory, Guid.NewGuid().ToString("N") + ".download");
        try
        {
            using var http = NewGoogleClient();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id) + "?alt=media");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(cancellationToken));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccess(response);
            await using (var remote = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await remote.CopyToAsync(output, cancellationToken);
            await using var local = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(local, cancellationToken)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected)))
                throw new InvalidDataException("SHA-256 không khớp với manifest; tệp tải xuống bị loại bỏ.");
            RecordHistory("download", file.Name, "PASS", actual);
            return new VerifiedDownload(path, file.Name, file.MimeType, new FileInfo(path).Length, actual);
        }
        catch { TryDelete(path); throw; }
    }

    public async Task<bool> VerifyAsync(string id, CancellationToken cancellationToken)
    {
        var file = await DownloadVerifiedAsync(id, cancellationToken);
        TryDelete(file.Path);
        return true;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var file = await GetOwnedFile(id, cancellationToken);
        using var http = NewGoogleClient();
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(cancellationToken));
        request.Content = JsonContent.Create(new { trashed = true }, options: Json);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccess(response);
        RecordHistory("delete-cloud", file.Name, "MOVED_TO_TRASH", null);
    }

    private async Task<UploadChunkResult> SendStagedChunk(UploadState state, long start, long length, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var http = NewGoogleClient();
                await using var input = new FileStream(state.StagingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                input.Position = start;
                using var content = new StreamContent(new LimitedReadStream(input, length));
                content.Headers.ContentLength = length;
                content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, state.Size);
                using var request = new HttpRequestMessage(HttpMethod.Put, state.SessionUri) { Content = content };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.PermanentRedirect)
                    return new UploadChunkResult(RemoteOffset(response, state.Size), null, null);
                if (response.IsSuccessStatusCode)
                {
                    var file = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                    return new UploadChunkResult(state.Size, file.RootElement.Clone(), null);
                }
                if ((int)response.StatusCode >= 500 && attempt < 3) { await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken); continue; }
                await EnsureSuccess(response);
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                var reconciled = await QueryRemoteProgress(state, cancellationToken);
                if (reconciled.CompletedFile is not null)
                    return new UploadChunkResult(state.Size, reconciled.CompletedFile, null);
                if (reconciled.NextOffset >= start + length)
                    return new UploadChunkResult(reconciled.NextOffset, null, "Drive đã nhận khối; tiến độ được đồng bộ lại.");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
            }
        }
        return await QueryRemoteProgress(state, cancellationToken);
    }

    private async Task<UploadChunkResult> QueryRemoteProgress(UploadState state, CancellationToken cancellationToken)
    {
        using var http = NewGoogleClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, state.SessionUri);
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentLength = 0;
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(state.Size);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PermanentRedirect)
            return new UploadChunkResult(RemoteOffset(response, state.Size), null, "Tải lên có thể tiếp tục từ byte đã xác nhận.");
        if (response.IsSuccessStatusCode)
        {
            var file = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return new UploadChunkResult(state.Size, file.RootElement.Clone(), null);
        }
        await EnsureSuccess(response);
        throw new HttpRequestException("Không xác minh được trạng thái phiên tải lên.");
    }

    private async Task<UploadProgress> CompleteUpload(UploadState state, JsonElement completedFile, CancellationToken cancellationToken)
    {
        var fileId = Required(completedFile, "id");
        await using var staged = File.OpenRead(state.StagingPath);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(staged, cancellationToken)).ToLowerInvariant();
        var siblings = await ListChildren(state.FolderId, cancellationToken);
        var duplicate = siblings.FirstOrDefault(file => file.Id != fileId && TryAppValue(file, "detotSha256", out var oldHash) &&
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(oldHash), Convert.FromHexString(sha)));
        if (duplicate is not null)
        {
            await TrashFile(fileId, cancellationToken);
            CleanupSession(state);
            var existingArtifact = ToArtifact(duplicate);
            RecordHistory("upload", state.StoredName, "DUPLICATE_DISCARDED", sha);
            return new UploadProgress(state.Id, state.Size, state.Size, true, true, sha, existingArtifact,
                "Bản sao đã bị chuyển vào Thùng rác; tệp trùng đã có trên Drive.");
        }

        var existingProps = completedFile.TryGetProperty("appProperties", out var props)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(props.GetRawText(), Json) ?? new()
            : new Dictionary<string, string>(StringComparer.Ordinal);
        existingProps[OwnerKey] = OwnerValue;
        existingProps["detotSha256"] = sha;
        var description = MetadataDescription(state.Spec, sha);
        using var http = NewGoogleClient();
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"),
            "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(fileId) + "?fields=" + Uri.EscapeDataString(FileFields));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(cancellationToken));
        request.Content = JsonContent.Create(new { appProperties = existingProps, description }, options: Json);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccess(response);
        using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var completed = JsonSerializer.Deserialize<DriveFile>(data.RootElement.GetRawText(), Json)
            ?? throw new JsonException("Drive returned an invalid file record.");
        var artifact = ToArtifact(completed);
        CleanupSession(state);
        RecordHistory("upload", artifact.Name, "PASS", sha);
        return new UploadProgress(state.Id, state.Size, state.Size, true, false, sha, artifact, "Tải lên và xác minh SHA-256 hoàn tất.");
    }

    private async Task<Dictionary<string, string>> EnsureStructureAsync(CancellationToken cancellationToken)
    {
        var account = LoadAccount() ?? throw new InvalidOperationException("Google Drive chưa kết nối.");
        var folders = new Dictionary<string, string>(account.Folders, StringComparer.Ordinal);
        foreach (var key in FolderNames.Keys)
        {
            if (folders.TryGetValue(key, out var id) && await FolderExists(id, cancellationToken)) continue;
            string? parent = key switch
            {
                "root" => null,
                "datasets" or "models" or "memory-backups" or "training-results" or "documents" => folders["root"],
                _ => folders["datasets"]
            };
            var found = await FindFolder(FolderNames[key], parent, key, cancellationToken);
            folders[key] = found ?? await CreateFolder(FolderNames[key], parent, key, cancellationToken);
        }
        if (!DictionaryEqual(account.Folders, folders)) SaveAccount(account with { Folders = folders });
        return folders;
    }

    private async Task<string?> FindFolder(string name, string? parent, string key, CancellationToken cancellationToken)
    {
        var q = $"mimeType = '{FolderMime}' and trashed = false and name = '{QueryLiteral(name)}' and appProperties has {{ key='detotOwner' and value='{OwnerValue}' }} and appProperties has {{ key='detotFolder' and value='{key}' }}";
        q += parent is null ? " and 'root' in parents" : $" and '{QueryLiteral(parent)}' in parents";
        var found = await ListQuery(q, cancellationToken);
        return found.FirstOrDefault()?.Id;
    }

    private async Task<bool> FolderExists(string id, CancellationToken cancellationToken)
    {
        try
        {
            using var http = NewGoogleClient();
            using var response = await AuthorizedRequest(http, HttpMethod.Get,
                "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id) + "?fields=id,mimeType,appProperties", null, cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return data.RootElement.TryGetProperty("mimeType", out var mime) && mime.GetString() == FolderMime &&
                data.RootElement.TryGetProperty("appProperties", out var appProps) &&
                appProps.TryGetProperty(OwnerKey, out var owner) && owner.GetString() == OwnerValue;
        }
        catch (HttpRequestException) { return false; }
    }

    private async Task<string> CreateFolder(string name, string? parent, string key, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["name"] = name, ["mimeType"] = FolderMime,
            ["appProperties"] = new Dictionary<string, string> { [OwnerKey] = OwnerValue, ["detotFolder"] = key }
        };
        if (parent is not null) body["parents"] = new[] { parent };
        using var http = NewGoogleClient();
        using var response = await AuthorizedRequest(http, HttpMethod.Post,
            "https://www.googleapis.com/drive/v3/files?fields=id", body, cancellationToken);
        await EnsureSuccess(response);
        using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return Required(data.RootElement, "id");
    }

    private async Task<List<DriveFile>> ListChildren(string folderId, CancellationToken cancellationToken)
    {
        var q = $"'{QueryLiteral(folderId)}' in parents and trashed = false and appProperties has {{ key='detotOwner' and value='{OwnerValue}' }}";
        return await ListQuery(q, cancellationToken);
    }

    private async Task<List<DriveFile>> ListQuery(string q, CancellationToken cancellationToken)
    {
        var list = new List<DriveFile>();
        string? page = null;
        for (var pageNumber = 0; pageNumber < 20; pageNumber++)
        {
            var query = new Dictionary<string, string?> { ["q"] = q, ["pageSize"] = "1000", ["fields"] = "nextPageToken,files(" + FileFields + ")" };
            if (page is not null) query["pageToken"] = page;
            using var http = NewGoogleClient();
            using var response = await AuthorizedRequest(http, HttpMethod.Get,
                QueryHelpers.AddQueryString("https://www.googleapis.com/drive/v3/files", query), null, cancellationToken);
            await EnsureSuccess(response);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("files", out var files))
                foreach (var element in files.EnumerateArray())
                {
                    var file = JsonSerializer.Deserialize<DriveFile>(element.GetRawText(), Json);
                    if (file is not null && TryAppValue(file, OwnerKey, out var owner) && owner == OwnerValue) list.Add(file);
                }
            page = document.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(page)) return list;
        }
        throw new InvalidOperationException("Drive trả quá nhiều trang metadata; thu hẹp lựa chọn dataset.");
    }

    private async Task<DriveFile> GetOwnedFile(string id, CancellationToken cancellationToken)
    {
        if (id.Length is < 5 or > 256 || !Regex.IsMatch(id, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("Mã tệp không hợp lệ.");
        using var http = NewGoogleClient();
        using var response = await AuthorizedRequest(http, HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id) + "?fields=" + Uri.EscapeDataString(FileFields), null, cancellationToken);
        await EnsureSuccess(response);
        using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var file = JsonSerializer.Deserialize<DriveFile>(data.RootElement.GetRawText(), Json)
            ?? throw new JsonException("Drive returned an invalid file.");
        if (!TryAppValue(file, OwnerKey, out var owner) || owner != OwnerValue)
            throw new ArgumentException("Tệp này không thuộc thư mục Đệ Tốt; không thao tác với tệp Drive cũ.");
        return file;
    }

    private async Task TrashFile(string id, CancellationToken cancellationToken)
    {
        using var http = NewGoogleClient();
        using var response = await AuthorizedRequest(http, new HttpMethod("PATCH"),
            "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id), new { trashed = true }, cancellationToken);
        await EnsureSuccess(response);
    }

    private async Task<CloudArtifact> UpdateDatasetMetadataAsync(string id, DatasetMetadataEdit edit, CancellationToken cancellationToken)
    {
        var source = edit.Source?.Trim() ?? "";
        var license = edit.License?.Trim() ?? "";
        if (source.Length is < 1 or > 1000) throw new ArgumentException("Nguồn dữ liệu phải dài từ 1 đến 1000 ký tự.");
        if (!DatasetLicenseCatalog.IsRecognized(license)) throw new ArgumentException("Chọn giấy phép được nhận diện; không lưu giá trị tùy ý như “fg”.");
        var file = await GetOwnedFile(id, cancellationToken);
        if (!TryAppValue(file, "detotKind", out var kind) || kind != "dataset")
            throw new ArgumentException("Chỉ có thể sửa metadata của dataset do Đệ Tốt quản lý.");
        var metadata = ParseMetadata(file.Description);
        metadata["source"] = source;
        metadata["license"] = license;
        using var http = NewGoogleClient();
        using var response = await AuthorizedRequest(http, new HttpMethod("PATCH"),
            "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(file.Id) + "?fields=" + Uri.EscapeDataString(FileFields),
            new { description = JsonSerializer.Serialize(metadata, Json) }, cancellationToken);
        await EnsureSuccess(response);
        var updated = await GetOwnedFile(file.Id, cancellationToken);
        RecordHistory("metadata", file.Name, "UPDATED_WITH_LICENSE", null);
        return ToArtifact(updated);
    }

    private async Task<string> AccessToken(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && _accessTokenExpires > DateTimeOffset.UtcNow.AddMinutes(2)) return _accessToken;
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && _accessTokenExpires > DateTimeOffset.UtcNow.AddMinutes(2)) return _accessToken;
            var account = LoadAccount() ?? throw new InvalidOperationException("Google Drive chưa kết nối.");
            using var http = NewGoogleClient();
            using var response = await TokenRequest(http, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken }, cancellationToken);
            var root = response.RootElement;
            if (root.TryGetProperty("scope", out var scope) && !IsDriveGrant(scope.GetString()))
                throw new InvalidOperationException("Quyền Drive hiện tại không đúng phạm vi tối thiểu; kết nối lại để cấp drive.file.");
            _accessToken = Required(root, "access_token");
            var seconds = root.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var value) ? value : 3600;
            _accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 60, 86400));
            if (root.TryGetProperty("refresh_token", out var renewed) && !string.IsNullOrWhiteSpace(renewed.GetString()))
                SaveAccount(account with { RefreshToken = renewed.GetString()! });
            return _accessToken;
        }
        finally { _tokenGate.Release(); }
    }

    private async Task<HttpResponseMessage> AuthorizedRequest(HttpClient http, HttpMethod method, string url, object? body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(cancellationToken));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<JsonDocument> TokenRequest(HttpClient http, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        values["client_id"] = _clientId!;
        values["client_secret"] = _clientSecret!;
        using var body = new FormUrlEncodedContent(values);
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", body, cancellationToken);
        await EnsureSuccess(response);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private HttpClient NewGoogleClient()
    {
        var client = _clients.CreateClient("google");
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    private DriveAccount? LoadAccount() => !File.Exists(_accountPath)
        ? null
        : JsonSerializer.Deserialize<DriveAccount>(_protector.Unprotect(File.ReadAllText(_accountPath)), Json);

    private void SaveAccount(DriveAccount account) => WriteEncrypted(_accountPath, account);

    private UploadState? LoadSession(Guid id)
    {
        var path = SessionPath(id);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<UploadState>(_protector.Unprotect(File.ReadAllText(path)), Json);
    }

    private void SaveSession(UploadState state) => WriteEncrypted(SessionPath(state.Id), state);

    private void WriteEncrypted<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, _protector.Protect(JsonSerializer.Serialize(value, Json)));
            File.Move(temp, path, true);
        }
        finally { TryDelete(temp); }
    }

    private string SessionPath(Guid id) => Path.Combine(_sessionsDirectory, id.ToString("N") + ".bin");

    private void CleanupSession(UploadState state)
    {
        TryDelete(state.StagingPath);
        TryDelete(SessionPath(state.Id));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private CloudArtifact ToArtifact(DriveFile file)
    {
        var metadata = ParseMetadata(file.Description);
        var app = file.AppProperties ?? new Dictionary<string, string>();
        var kind = app.GetValueOrDefault("detotKind", "document");
        var category = app.GetValueOrDefault("detotCategory", "");
        var sha = app.GetValueOrDefault("detotSha256", "");
        var size = long.TryParse(file.Size, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return new CloudArtifact(file.Id, file.Name, kind, category, app.GetValueOrDefault("detotVersion", ""),
            app.GetValueOrDefault("detotPipeline", ""), metadata.GetValueOrDefault("source", ""), metadata.GetValueOrDefault("license", ""), size, file.ModifiedTime,
            sha, Regex.IsMatch(sha, "^[a-fA-F0-9]{64}$") ? "not-checked" : "unknown", file.WebViewLink);
    }

    private static Dictionary<string, string> ParseMetadata(string? description)
    {
        try { return description is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(description, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static bool TryAppValue(DriveFile file, string key, out string value)
    {
        value = "";
        return file.AppProperties is not null && file.AppProperties.TryGetValue(key, out value!);
    }

    private static string FolderForSpec(UploadSpec spec) => spec.Kind switch
    {
        "dataset" => "dataset-" + spec.Category,
        "model" => "models", "backup" => "memory-backups", "training-result" => "training-results", "document" => "documents",
        _ => throw new ArgumentException("Loại artifact không hợp lệ.")
    };

    private static string FolderForKind(string kind) => kind switch
    {
        "dataset" => "datasets", "model" => "models", "backup" => "memory-backups",
        "training-result" => "training-results", "document" => "documents",
        _ => throw new ArgumentException("Loại artifact không hợp lệ.")
    };

    private static string VersionedName(string name, string version)
    {
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        return $"{stem}@{version}{extension}";
    }

    private static string MetadataDescription(UploadSpec spec, string? sha = null) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["source"] = spec.Source, ["license"] = spec.License, ["pipeline"] = spec.Pipeline,
        ["version"] = spec.Version, ["originalFileName"] = spec.FileName,
        ["sha256"] = sha ?? "pending"
    }, Json);

    private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".json" or ".jsonl" => "application/json", ".csv" => "text/csv", ".parquet" => "application/vnd.apache.parquet",
        ".pdf" => "application/pdf", ".md" or ".txt" => "text/plain", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".onnx" => "application/onnx", ".gguf" or ".safetensors" or ".bin" => "application/octet-stream", ".enc" or ".zip" => "application/octet-stream",
        _ => "application/octet-stream"
    };

    private void RecordHistory(string action, string target, string result, string? sha)
    {
        var rows = ReadHistory().TakeLast(499).Append(new SyncHistory(DateTimeOffset.UtcNow, action, target, result, sha)).ToArray();
        WriteEncrypted(_historyPath, rows);
    }

    private SyncHistory[] ReadHistory()
    {
        if (!File.Exists(_historyPath)) return [];
        try { return JsonSerializer.Deserialize<SyncHistory[]>(_protector.Unprotect(File.ReadAllText(_historyPath)), Json) ?? []; }
        catch (JsonException) { return []; }
        catch (CryptographicException) { return []; }
    }

    private static long RemoteOffset(HttpResponseMessage response, long total)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;
        var range = values.FirstOrDefault();
        var match = range is null ? Match.Empty : Regex.Match(range, @"^bytes=0-(\d+)$", RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups[1].Value, out var last) ? Math.Min(total, last + 1) : 0;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PermanentRedirect) return;
        var body = await response.Content.ReadAsStringAsync();
        throw new DriveApiException(response.StatusCode, body[..Math.Min(2000, body.Length)]);
    }

    private static bool IsRemoteError(Exception error) => error is HttpRequestException or JsonException or IOException or CryptographicException
        or InvalidOperationException or KeyNotFoundException or DriveApiException or OperationCanceledException;

    private static string QueryLiteral(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
    private static string RandomToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static string Required(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new JsonException();
    private static bool IsDriveGrant(string? scope)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
            { Scope, "openid", "email", "https://www.googleapis.com/auth/userinfo.email" };
        var grants = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return grants.Contains(Scope, StringComparer.Ordinal) && grants.All(allowed.Contains);
    }

    private static bool DictionaryEqual(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value == value);

    private static async Task<JsonDocument> GoogleGet(HttpClient http, string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccess(response);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private sealed record OAuthState(string State, string Verifier, long Expires);
    private sealed record DriveAccount(string Subject, string Email, string RefreshToken, string Scope, Dictionary<string, string> Folders);
    private sealed record UploadState(Guid Id, string SessionUri, long Size, long NextOffset, UploadSpec Spec,
        string FolderId, string StoredName, string StagingPath, DateTimeOffset Created);
    private sealed record UploadChunkResult(long NextOffset, JsonElement? CompletedFile, string? Message);
    private sealed record DriveFile(string Id, string Name, string MimeType, string? Size, DateTimeOffset ModifiedTime,
        string? Description, Dictionary<string, string>? AppProperties, string? WebViewLink);
    private sealed record SyncHistory(DateTimeOffset Utc, string Action, string Target, string Result, string? Sha256);
    public sealed record RetentionRequest(int Keep);
    public sealed record DatasetMetadataEdit(string? Source, string? License);
    private sealed class DriveApiException(HttpStatusCode status, string body) : HttpRequestException($"Drive API returned {(int)status}.")
    {
        public HttpStatusCode Status { get; } = status;
        public string Body { get; } = body;
    }
    private sealed class StorageConflictException(string message) : Exception(message);
    private sealed class SemaphoreLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
    private const string FileFields = "id,name,mimeType,size,modifiedTime,description,appProperties,webViewLink";

    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private readonly long _limit = limit;
        private long _remaining = limit;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _limit;
        public override long Position { get => _limit - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining)); _remaining -= read; return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken); _remaining -= read; return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
