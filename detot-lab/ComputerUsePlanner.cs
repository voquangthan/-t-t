using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

public sealed record ComputerUsePlan(bool Handled, IReadOnlyList<string> Actions);

public static partial class ComputerUsePlanner
{
    private const int MaximumSteps = 5;
    public const string ToolName = "detot_computer_use_plan";
    public static IReadOnlyList<string> AllowedActionIds { get; } = Array.AsReadOnly(new[]
    {
        "open-workspace", "open-notepad", "open-vscode", "open-edge", "open-github",
        "read-github-account", "read-github-pull-requests", "read-approved-applications"
    });

    [GeneratedRegex(@"\b(mở|vào|xem|kiểm\s+tra|đọc|liệt\s+kê|bấm|nhấn|chọn|cuộn|nhập|gõ|điền|click|press|select|scroll|type|enter|fill|open|launch|check|inspect|read|list|show|verify|go\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActionCue();

    [GeneratedRegex(@"^\s*(?:làm\s+sao|cách\s+(?:mở|dùng|bấm)|hướng\s+dẫn|how\s+(?:do\s+i|can\s+i|to)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstructionQuestion();

    public static bool MightBeActionRequest(string? message) =>
        !string.IsNullOrWhiteSpace(message) && message.Length <= 1500 &&
        !message.Contains('\n') && !message.Contains('\r') && !message.Contains('`') &&
        !InstructionQuestion().IsMatch(message) && ActionCue().IsMatch(message);

    public static object CreateToolDefinition() => new
    {
        type = "function",
        name = ToolName,
        description = "Plan up to five ordered, distinct existing Computer Use navigation/observation actions for the user's direct request in this message. Use only the fixed action enum. Do not use for examples, quoted text, hypotheticals, or how-to questions. No write, shell, arbitrary path, URL, click, typing, or account selection actions exist.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                handled = new { type = "boolean" },
                actions = new
                {
                    type = "array",
                    items = new { type = "string", @enum = AllowedActionIds },
                    maxItems = MaximumSteps,
                    uniqueItems = true
                }
            },
            required = new[] { "handled", "actions" },
            additionalProperties = false
        }
    };

    public static bool TryParse(string? json, out ComputerUsePlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("handled", out var handledValue) ||
                handledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("actions", out var actionsValue) ||
                actionsValue.ValueKind != JsonValueKind.Array || actionsValue.GetArrayLength() > MaximumSteps)
                return false;

            var handled = handledValue.GetBoolean();
            var actions = new List<string>(actionsValue.GetArrayLength());
            foreach (var item in actionsValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return false;
                var action = item.GetString();
                if (!IsAllowed(action) || actions.Contains(action!, StringComparer.Ordinal)) return false;
                actions.Add(action!);
            }

            if (handled != (actions.Count > 0)) return false;
            plan = new ComputerUsePlan(handled, actions);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsAllowed(string? action) => action is not null &&
        (ComputerUsePolicy.CanNavigate(action) ||
         ComputerUsePolicy.CanObserve(ComputerUseChatIntent.ObservationTargetForAction(action)));

    public static bool IsVerifiedActionResult(string action, int statusCode, JsonElement body)
    {
        if (statusCode >= StatusCodes.Status400BadRequest || body.ValueKind != JsonValueKind.Object ||
            body.TryGetProperty("error", out _)) return false;
        static bool IsTrue(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        return action switch
        {
            "open-notepad" => IsTrue(body, "windowVerified"),
            "open-workspace" => IsTrue(body, "workspaceVerified"),
            "open-vscode" => IsTrue(body, "windowVerified") && IsTrue(body, "workspaceVerified"),
            "open-edge" => IsTrue(body, "windowVerified"),
            "open-github" => IsTrue(body, "windowVerified") && IsTrue(body, "urlVerified"),
            "read-github-account" => ReadString(body, "status") is "verified" or "signed_out",
            "read-approved-applications" => ReadString(body, "computerUseObservationStatus") is "available" or "empty",
            "read-github-pull-requests" => ReadString(body, "reply") is { Length: > 0 } reply &&
                !reply.Contains("GitHub đang giới hạn", StringComparison.OrdinalIgnoreCase) &&
                !reply.Contains("hiện không đọc được trạng thái", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
