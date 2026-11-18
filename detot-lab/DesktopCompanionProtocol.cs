using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class DesktopCompanionProtocol
{
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "observe-applications", "open-workspace", "open-edge", "open-github",
        "open-vscode", "read-workspace", "read-github-account", "read-github-pull-requests"
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
        if (!IsAllowedAction(action)) throw new ArgumentException("Desktop action is not allowed.", nameof(action));
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("Invalid request id.", nameof(requestId));
        return JsonSerializer.Serialize(new { action = action!, requestId });
    }

    public static bool TryParseRequest(string? json, out Request? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
                !IsAllowedAction(action.GetString()) || !Guid.TryParseExact(id.GetString(), "N", out _)) return false;
            request = new Request(action.GetString()!, id.GetString()!);
            return true;
        }
        catch (JsonException) { return false; }
    }

    public sealed record Request(string Action, string RequestId);
}

public sealed class DesktopCompanionClient
{
    private readonly string pipeName;
    public DesktopCompanionClient(string? pipeName = null) => this.pipeName = pipeName ?? DesktopCompanionProtocol.PipeNameForCurrentUser();

    public async Task<JsonElement> ExecuteAsync(string action, CancellationToken cancellationToken = default)
    {
        if (!DesktopCompanionProtocol.IsAllowedAction(action))
            return JsonSerializer.SerializeToElement(new { error = "action_not_allowed" });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500, timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var requestId = Guid.NewGuid().ToString("N");
            await writer.WriteLineAsync(DesktopCompanionProtocol.SerializeRequest(action, requestId).AsMemory(), timeout.Token);
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
    }

    private static JsonElement Error(string error) => JsonSerializer.SerializeToElement(new { error });
}
