using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class DesktopCompanionProtocol
{
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "observe-applications", "open-workspace", "open-edge", "open-github",
        "open-vscode", "read-workspace", "read-github-account", "read-github-pull-requests",
        "observe-desktop-ui", "activate-window", "click-element", "double-click-element",
        "select-element", "type-text", "scroll-element", "press-key", "open-settings",
        "open-task-manager", "open-word", "open-excel", "open-powerpoint", "open-pdf-reader",
        "open-workspace-item", "expand-element", "collapse-element"
    };
    private static readonly HashSet<string> TargetActions = new(StringComparer.Ordinal)
        { "activate-window", "click-element", "double-click-element", "select-element", "type-text", "scroll-element", "press-key", "expand-element", "collapse-element" };
    private static readonly HashSet<string> NavigationKeys = new(StringComparer.Ordinal)
        { "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Home", "End", "PageUp", "PageDown" };

    public static string PipeNameForCurrentUser()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20].ToLowerInvariant();
        return $"detot-desktop-{hash}";
    }

    public static bool IsAllowedAction(string? action) => action is not null && Actions.Contains(action);

    public static string SerializeResponse(string requestId, object result)
    {
        var response = JsonSerializer.Serialize(new { requestId, result });
        return response.Length <= 30 * 1024
            ? response
            : JsonSerializer.Serialize(new { requestId, result = new { error = "response_too_large" } });
    }

    public static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 512));
        var buffer = new char[256];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == '\n') return builder.ToString().TrimEnd('\r');
                if (builder.Length >= maximumCharacters) throw new InvalidDataException("Pipe message exceeded its limit.");
                builder.Append(value);
            }
        }
    }

    public static string SerializeRequest(string? action, string requestId, string? targetId = null,
        string? value = null, string? direction = null)
    {
        if (!IsAllowedAction(action)) throw new ArgumentException("Desktop action is not allowed.", nameof(action));
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("Invalid request id.", nameof(requestId));
        if (!IsValidArguments(action!, targetId, value, direction))
            throw new ArgumentException("Desktop action arguments are invalid.", nameof(targetId));
        var request = new Dictionary<string, string?> { ["action"] = action!, ["requestId"] = requestId };
        if (targetId is not null) request["targetId"] = targetId;
        if (value is not null) request["value"] = value;
        if (direction is not null) request["direction"] = direction;
        var serialized = JsonSerializer.Serialize(request);
        if (serialized.Length > 4096) throw new ArgumentException("Desktop request exceeded its protocol limit.", nameof(value));
        return serialized;
    }

    public static bool IsValidArguments(string action, string? targetId, string? value, string? direction)
    {
        if (TargetActions.Contains(action) && (targetId is null || !Guid.TryParseExact(targetId, "N", out _))) return false;
        if (!TargetActions.Contains(action) && targetId is not null) return false;
        if (action == "type-text")
        {
            if (string.IsNullOrEmpty(value) || value.Length > 512 || value.Contains('\r') || value.Contains('\n') || value.Contains('\0') || direction is not null)
                return false;
        }
        else if (action == "press-key")
        {
            if (value is null || !NavigationKeys.Contains(value) || direction is not null) return false;
        }
        else if (action == "open-workspace-item")
        {
            if (value is null || !DesktopOperator.IsSafeWorkspaceRelativePath(value) || direction is not null) return false;
        }
        else if (value is not null) return false;
        if (action == "scroll-element") return direction is "up" or "down";
        return direction is null;
    }

    public static bool TryParseRequest(string? json, out Request? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            JsonProperty[] properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray() : Array.Empty<JsonProperty>();
            if (root.ValueKind != JsonValueKind.Object || properties.Any(p => p.Name is not ("action" or "requestId" or "targetId" or "value" or "direction")) ||
                properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                !root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
                !IsAllowedAction(action.GetString()) || !Guid.TryParseExact(id.GetString(), "N", out _)) return false;
            var targetId = ReadOptionalString(root, "targetId");
            var value = ReadOptionalString(root, "value");
            var direction = ReadOptionalString(root, "direction");
            if (targetId == InvalidOptionalString || value == InvalidOptionalString || direction == InvalidOptionalString ||
                !IsValidArguments(action.GetString()!, targetId, value, direction)) return false;
            request = new Request(action.GetString()!, id.GetString()!, targetId, value, direction);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private const string InvalidOptionalString = "\0invalid";
    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : InvalidOptionalString;
    }

    public sealed record Request(string Action, string RequestId, string? TargetId = null,
        string? Value = null, string? Direction = null);
}

public sealed record DesktopGithubCheck(string Name, string? Status, string? Conclusion);
public sealed record DesktopGithubCheckSummary(string Status, int TotalCount, int DisplayedCount,
    bool Truncated, int FailureCount, string[] FailureNames, DesktopGithubCheck[] Checks);

public static class DesktopGithubChecks
{
    private static readonly HashSet<string> SuccessfulConclusions = new(StringComparer.OrdinalIgnoreCase)
        { "success", "neutral", "skipped" };

    public static DesktopGithubCheckSummary Summarize(IEnumerable<JsonElement> checkRuns, int? apiTotalCount = null,
        bool paginationIncomplete = false, int displayLimit = 8)
    {
        var all = checkRuns.Select(run => new DesktopGithubCheck(
            ReadString(run, "name") ?? "check", ReadString(run, "status"), ReadString(run, "conclusion"))).ToArray();
        var total = Math.Max(all.Length, apiTotalCount ?? all.Length);
        var truncated = paginationIncomplete || all.Length < total || all.Length > displayLimit;
        var failures = all.Where(run => run.Conclusion is { Length: > 0 } conclusion &&
            !SuccessfulConclusions.Contains(conclusion)).ToArray();
        return new DesktopGithubCheckSummary(
            all.Length == 0 && total == 0 && !paginationIncomplete ? "empty" : "available",
            total, Math.Min(displayLimit, all.Length), truncated, failures.Length,
            failures.Select(run => run.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray(),
            all.Take(displayLimit).ToArray());
    }

    public static string FormatCoverage(DesktopGithubCheckSummary summary) =>
        $"{summary.DisplayedCount}/{summary.TotalCount} checks hiển thị" +
        (summary.FailureCount > 0 ? $"; tổng cộng {summary.FailureCount} check lỗi" : "") +
        (summary.Truncated ? "; danh sách bị cắt, kết quả chỉ tổng hợp trên dữ liệu đã lấy" : "");

    private static string? ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}

public sealed class DesktopCompanionClient
{
    public const int RequestDeadlineMilliseconds = 20_000;
    private readonly string pipeName;
    public DesktopCompanionClient(string? pipeName = null) => this.pipeName = pipeName ?? DesktopCompanionProtocol.PipeNameForCurrentUser();

    public async Task<JsonElement> ExecuteAsync(string action, CancellationToken cancellationToken = default)
        => await ExecuteAsync(action, null, null, null, cancellationToken);

    public async Task<JsonElement> ExecuteAsync(string action, string? targetId, string? value = null,
        string? direction = null, CancellationToken cancellationToken = default)
    {
        if (!DesktopCompanionProtocol.IsAllowedAction(action))
            return JsonSerializer.SerializeToElement(new { error = "action_not_allowed" });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestDeadlineMilliseconds);
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500, timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var requestId = Guid.NewGuid().ToString("N");
            await writer.WriteLineAsync(DesktopCompanionProtocol.SerializeRequest(action, requestId,
                targetId, value, direction).AsMemory(), timeout.Token);
            var responseLine = await DesktopCompanionProtocol.ReadBoundedLineAsync(reader, 32 * 1024, timeout.Token);
            if (responseLine is null) return Error("companion_protocol_error");
            using var document = JsonDocument.Parse(responseLine);
            if (!document.RootElement.TryGetProperty("requestId", out var id) || id.GetString() != requestId ||
                !document.RootElement.TryGetProperty("result", out var result)) return Error("companion_protocol_error");
            return result.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Error("companion_timeout"); }
        catch (TimeoutException) { return Error("companion_unavailable"); }
        catch (IOException) { return Error("companion_unavailable"); }
        catch (JsonException) { return Error("companion_protocol_error"); }
        catch (UnauthorizedAccessException) { return Error("companion_unauthorized"); }
        catch (ArgumentException) { return Error("invalid_action_arguments"); }
    }

    private static JsonElement Error(string error) => JsonSerializer.SerializeToElement(new { error });
}

/// <summary>Serves one authenticated local pipe request at a time and survives disconnected clients.</summary>
public sealed class DesktopCompanionPipeServer(string pipeName, int requestDeadlineMilliseconds = 17_000)
{
    public async Task RunAsync(Func<DesktopCompanionProtocol.Request, CancellationToken, Task<object>> execute,
        CancellationToken shutdownToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        while (!shutdownToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            try
            {
                await pipe.WaitForConnectionAsync(shutdownToken);
                await HandleConnectionAsync(pipe, execute, shutdownToken);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // A client may disappear between accept/read/write. Never let a broken pipe terminate the accept loop.
                Console.Error.WriteLine($"Desktop Companion pipe request ended: {error.GetType().Name}");
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe,
        Func<DesktopCompanionProtocol.Request, CancellationToken, Task<object>> execute,
        CancellationToken shutdownToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        deadline.CancelAfter(requestDeadlineMilliseconds);
        Task<object>? action = null;
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 2048, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 2048, leaveOpen: true) { AutoFlush = true };
            var line = await DesktopCompanionProtocol.ReadBoundedLineAsync(reader, 4096, deadline.Token);
            if (!DesktopCompanionProtocol.TryParseRequest(line, out var request) || request is null)
            {
                await TryWriteResponseAsync(pipe, writer,
                    JsonSerializer.Serialize(new { requestId = "", result = new { error = "invalid_request" } }), deadline.Token);
                return;
            }

            action = execute(request, deadline.Token);
            while (!action.IsCompleted && !deadline.IsCancellationRequested)
            {
                await Task.Delay(100, deadline.Token);
                if (!pipe.IsConnected)
                {
                    deadline.Cancel();
                    break;
                }
            }
            var result = await action;
            if (pipe.IsConnected && !deadline.IsCancellationRequested)
                await TryWriteResponseAsync(pipe, writer,
                    DesktopCompanionProtocol.SerializeResponse(request.RequestId, result), deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (action is not null)
            {
                try { await action; }
                catch (Exception error) { Console.Error.WriteLine($"Desktop Companion action ended: {error.GetType().Name}"); }
            }
        }
        catch (Exception error)
        {
            // Do not retry a response write here: the peer may already have closed its end of the pipe.
            Console.Error.WriteLine($"Desktop Companion action ended: {error.GetType().Name}");
        }
    }

    private static async Task TryWriteResponseAsync(NamedPipeServerStream pipe, StreamWriter writer,
        string response, CancellationToken cancellationToken)
    {
        if (!pipe.IsConnected) return;
        await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
    }
}
