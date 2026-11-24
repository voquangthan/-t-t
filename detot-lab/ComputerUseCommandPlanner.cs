using System.Text.Json;

public sealed record ComputerUseCommandAction(string CommandId);

/// <summary>Fixed, read-only terminal commands. No model-supplied command line or arguments.</summary>
public static class ComputerUseCommandPlanner
{
    public const string ToolName = "detot_safe_terminal";
    public static IReadOnlyList<string> CommandIds { get; } = Array.AsReadOnly(new[]
    {
        "powershell-date", "powershell-location", "cmd-version", "git-status", "git-branch", "dotnet-version"
    });

    public static object CreateToolDefinition() => new
    {
        type = "function",
        name = ToolName,
        description = "Run only the exact read-only terminal command explicitly requested by the user in this turn. The backend validates its scope; the model cannot create it. No free-form command, arguments, script, path, environment assignment, elevation, file modification, or retry is supported. Output is bounded and the child process is cancellable.",
        parameters = new
        {
            type = "object",
            properties = new { commandId = new { type = "string", @enum = CommandIds } },
            required = new[] { "commandId" },
            additionalProperties = false
        }
    };

    public static bool TryParse(string? json, out ComputerUseCommandAction? action)
    {
        action = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 256) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("commandId", out var id) || id.ValueKind != JsonValueKind.String ||
                !CommandIds.Contains(id.GetString()!, StringComparer.Ordinal)) return false;
            action = new(id.GetString()!);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
