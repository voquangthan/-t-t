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
        "ui-operation", "confirmed-ui-operation", "run-safe-command"
    };

    public static string PipeNameForCurrentUser()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20].ToLowerInvariant();
        return $"detot-desktop-{hash}";
    }

    public static bool IsAllowedAction(string? action) => action is not null && Actions.Contains(action);

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

    public static string SerializeRequest(string? action, string requestId)
    {
        if (!IsAllowedAction(action) || action == "run-safe-command") throw new ArgumentException("Desktop action requires a typed request or is not allowed.", nameof(action));
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("Invalid request id.", nameof(requestId));
        return JsonSerializer.Serialize(new { action = action!, requestId });
    }

    public static string SerializeUiRequest(ComputerUseUiAction uiAction, string requestId, bool userConfirmed = false)
    {
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("Invalid request id.", nameof(requestId));
        if (!ComputerUseUiPlanner.AllowedApps.Contains(uiAction.App, StringComparer.Ordinal) ||
            !ComputerUseUiPlanner.AllowedOperations.Contains(uiAction.Operation, StringComparer.Ordinal))
            throw new ArgumentException("Desktop UI operation is not allowed.", nameof(uiAction));
        var payload = new Dictionary<string, object?>
        {
            ["operation"] = uiAction.Operation,
            ["app"] = uiAction.App
        };
        if (uiAction.ObservationId is not null) payload["observationId"] = uiAction.ObservationId;
        if (uiAction.ElementId is not null) payload["elementId"] = uiAction.ElementId;
        if (uiAction.WindowId is not null) payload["windowId"] = uiAction.WindowId;
        if (uiAction.Text is not null) payload["text"] = uiAction.Text;
        if (uiAction.Url is not null) payload["url"] = uiAction.Url;
        if (uiAction.Direction is not null) payload["direction"] = uiAction.Direction;
        if (uiAction.TimeoutMs is not null) payload["timeoutMs"] = uiAction.TimeoutMs;
        return userConfirmed
            ? JsonSerializer.Serialize(new { action = "confirmed-ui-operation", requestId, uiAction = payload, userConfirmed = true })
            : JsonSerializer.Serialize(new { action = "ui-operation", requestId, uiAction = payload });
    }

    public static string SerializeCommandRequest(ComputerUseCommandAction commandAction, string requestId)
    {
        if (!Guid.TryParseExact(requestId, "N", out _) ||
            !ComputerUseCommandPlanner.CommandIds.Contains(commandAction.CommandId, StringComparer.Ordinal))
            throw new ArgumentException("Terminal command is not allowed.", nameof(commandAction));
        return JsonSerializer.Serialize(new { action = "run-safe-command", requestId,
            commandAction = new { commandId = commandAction.CommandId } });
    }

    public static bool TryParseRequest(string? json, out Request? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
                !IsAllowedAction(action.GetString()) || !Guid.TryParseExact(id.GetString(), "N", out _)) return false;
            if (action.GetString() is "ui-operation" or "confirmed-ui-operation")
            {
                var confirmed = action.GetString() == "confirmed-ui-operation";
                var expectedPropertyCount = confirmed ? 4 : 3;
                if (root.EnumerateObject().Count() != expectedPropertyCount || !root.TryGetProperty("uiAction", out var uiAction) ||
                    uiAction.ValueKind != JsonValueKind.Object ||
                    !ComputerUseUiPlanner.TryParse(uiAction.GetRawText(), out var parsedUiAction)) return false;
                if (confirmed && (!root.TryGetProperty("userConfirmed", out var confirmation) ||
                    confirmation.ValueKind != JsonValueKind.True)) return false;
                if (!confirmed && root.TryGetProperty("userConfirmed", out _)) return false;
                request = new Request(action.GetString()!, id.GetString()!, parsedUiAction, null, confirmed);
                return true;
            }
            if (action.GetString() == "run-safe-command")
            {
                if (root.EnumerateObject().Count() != 3 || !root.TryGetProperty("commandAction", out var commandAction) ||
                    commandAction.ValueKind != JsonValueKind.Object ||
                    !ComputerUseCommandPlanner.TryParse(commandAction.GetRawText(), out var parsedCommand)) return false;
                request = new Request(action.GetString()!, id.GetString()!, null, parsedCommand, false);
                return true;
            }
            if (root.EnumerateObject().Count() != 2) return false;
            request = new Request(action.GetString()!, id.GetString()!, null, null, false);
            return true;
        }
        catch (JsonException) { return false; }
    }

    public sealed record Request(string Action, string RequestId, ComputerUseUiAction? UiAction,
        ComputerUseCommandAction? CommandAction, bool UserConfirmed);
}

public sealed class DesktopCompanionClient
{
    private readonly string pipeName;
    public DesktopCompanionClient(string? pipeName = null) => this.pipeName = pipeName ?? DesktopCompanionProtocol.PipeNameForCurrentUser();

    public async Task<JsonElement> ExecuteAsync(string action, CancellationToken cancellationToken = default)
    {
        if (!DesktopCompanionProtocol.IsAllowedAction(action))
            return JsonSerializer.SerializeToElement(new { error = "action_not_allowed" });
        if (action == "run-safe-command") return JsonSerializer.SerializeToElement(new { error = "command_id_required" });
        if (action == "ui-operation") return JsonSerializer.SerializeToElement(new { error = "invalid_ui_operation" });
        return await SendAsync(DesktopCompanionProtocol.SerializeRequest(action, Guid.NewGuid().ToString("N")), cancellationToken);
    }

    public async Task<JsonElement> ExecuteUiAsync(ComputerUseUiAction action, CancellationToken cancellationToken = default)
        => await ExecuteUiAsync(action, false, cancellationToken);

    public async Task<JsonElement> ExecuteConfirmedUiAsync(ComputerUseUiAction action, CancellationToken cancellationToken = default)
        => await ExecuteUiAsync(action, true, cancellationToken);

    public async Task<JsonElement> ExecuteCommandAsync(ComputerUseCommandAction action, CancellationToken cancellationToken = default)
    {
        string serialized;
        try { serialized = DesktopCompanionProtocol.SerializeCommandRequest(action, Guid.NewGuid().ToString("N")); }
        catch (ArgumentException) { return JsonSerializer.SerializeToElement(new { error = "command_not_allowed" }); }
        return await SendAsync(serialized, cancellationToken, 15);
    }

    private async Task<JsonElement> ExecuteUiAsync(ComputerUseUiAction action, bool userConfirmed,
        CancellationToken cancellationToken)
    {
        string serialized;
        try { serialized = DesktopCompanionProtocol.SerializeUiRequest(action, Guid.NewGuid().ToString("N"), userConfirmed); }
        catch (ArgumentException) { return JsonSerializer.SerializeToElement(new { error = "action_not_allowed" }); }
        return await SendAsync(serialized, cancellationToken, action.Operation is "launch" or "navigate" ? 25 : 12);
    }

    private async Task<JsonElement> SendAsync(string serializedRequest, CancellationToken cancellationToken, int timeoutSeconds = 12)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500, timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var requestDocument = JsonDocument.Parse(serializedRequest);
            var requestId = requestDocument.RootElement.GetProperty("requestId").GetString()!;
            await writer.WriteLineAsync(serializedRequest.AsMemory(), timeout.Token);
            var responseLine = await DesktopCompanionProtocol.ReadBoundedLineAsync(reader, 64 * 1024, timeout.Token);
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
    }

    private static JsonElement Error(string error) => JsonSerializer.SerializeToElement(new { error });
}
