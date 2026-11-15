using System.Globalization;
using System.Text.Json;

internal static class MemoryIntentParser
{
    internal static (string Intent, string? Work, string? Category) Parse(string output, string message, bool pendingChoice)
    {
        try
        {
            using var parsed = JsonDocument.Parse(output);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                !root.TryGetProperty("intent", out var intent) || intent.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("work", out var value) || value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                !root.TryGetProperty("category", out var category) || category.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                return ("failed", null, null);
            }
            var work = value.GetString()?.Trim() is { Length: > 0 and <= 500 } excerpt &&
                message.Contains(excerpt, StringComparison.Ordinal) ? excerpt : null;
            var kind = category.GetString();
            return intent.GetString() switch
            {
                "save" when work is not null && kind is ("task" or "device" or "person" or "preference" or "event") => ("save", work, kind),
                "complete" when kind is null or "task" => ("complete", work, null),
                "select" when pendingChoice && kind is null && int.TryParse(value.GetString(), out var ordinal) &&
                    ordinal is >= 1 and <= 10 => ("complete", ordinal.ToString(CultureInfo.InvariantCulture), null),
                "recall" when (value.ValueKind == JsonValueKind.Null || work is not null) &&
                    kind is (null or "task" or "device" or "person" or "preference" or "event") => ("recall", work, kind),
                "schedule" when work is not null && kind == "task" => ("schedule", work, "task"),
                "none" when value.ValueKind == JsonValueKind.Null && kind is null => ("none", null, null),
                _ => ("failed", null, null)
            };
        }
        catch (JsonException)
        {
            return ("failed", null, null);
        }
    }
}
