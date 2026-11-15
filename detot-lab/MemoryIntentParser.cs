using System.Globalization;
using System.Text.Json;

internal static class MemoryIntentParser
{
    internal static (string Intent, string? Work, string? Category, Guid? MemoryId) Parse(string output, string message, bool pendingChoice)
    {
        try
        {
            using var parsed = JsonDocument.Parse(output);
            var root = parsed.RootElement;
            var propertyCount = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Count() : 0;
            if (root.ValueKind != JsonValueKind.Object || propertyCount is not (3 or 4) ||
                !root.TryGetProperty("intent", out var intent) || intent.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("work", out var value) || value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                !root.TryGetProperty("category", out var category) || category.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                (propertyCount == 4 && (!root.TryGetProperty("memoryId", out var memoryIdElement) ||
                    memoryIdElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))) ||
                (propertyCount == 3 && root.TryGetProperty("memoryId", out _)))
            {
                return ("failed", null, null, null);
            }
            Guid? targetId = null;
            if (root.TryGetProperty("memoryId", out var targetElement) && targetElement.ValueKind == JsonValueKind.String)
            {
                if (!Guid.TryParseExact(targetElement.GetString(), "D", out var parsedId) ||
                    !message.Contains(parsedId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    return ("failed", null, null, null);
                targetId = parsedId;
            }
            var work = value.GetString()?.Trim() is { Length: > 0 and <= 500 } excerpt &&
                message.Contains(excerpt, StringComparison.Ordinal) ? excerpt : null;
            var kind = category.GetString();
            return intent.GetString() switch
            {
                "save" when targetId is null && work is not null && kind is ("task" or "device" or "person" or "preference" or "event" or "note") => ("save", work, kind, null),
                "complete" when targetId is null && kind is null or "task" => ("complete", work, null, null),
                "select" when pendingChoice && kind is null && int.TryParse(value.GetString(), out var ordinal) &&
                    ordinal is >= 1 and <= 10 && targetId is null => ("complete", ordinal.ToString(CultureInfo.InvariantCulture), null, null),
                "recall" when targetId is null && (value.ValueKind == JsonValueKind.Null || work is not null) &&
                    kind is (null or "task" or "device" or "person" or "preference" or "event" or "note") => ("recall", work, kind, null),
                "schedule" when targetId is null && work is not null && kind == "task" => ("schedule", work, "task", null),
                "update" when targetId is not null && work is not null && kind is null => ("update", work, null, targetId),
                "none" when targetId is null && value.ValueKind == JsonValueKind.Null && kind is null => ("none", null, null, null),
                _ => ("failed", null, null, null)
            };
        }
        catch (JsonException)
        {
            return ("failed", null, null, null);
        }
    }
}
