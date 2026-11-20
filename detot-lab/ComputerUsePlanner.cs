using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record ComputerUsePlan(bool Handled, IReadOnlyList<string> Actions);

public static partial class ComputerUsePlanner
{
    private const int MaximumSteps = 5;

    [GeneratedRegex(@"\b(mở|vào|xem|kiểm\s+tra|đọc|liệt\s+kê|open|launch|check|inspect|read|list|show|verify|go\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActionCue();

    public static bool MightBeActionRequest(string? message) =>
        !string.IsNullOrWhiteSpace(message) && message.Length <= 600 &&
        !message.Contains('\n') && !message.Contains('\r') && !message.Contains('`') &&
        !message.Contains('"') && !message.Contains('\'') && ActionCue().IsMatch(message);

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
}
