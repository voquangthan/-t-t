using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record CalendarIntent(string Intent, string Status, string? Title, string? Start,
    int? DurationMinutes, string? Question);

internal static partial class CalendarIntentParser
{
    internal static CalendarIntent Parse(string output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6 ||
            !TryText(root, "intent", out var intent) || !TryText(root, "status", out var status) ||
            !OptionalText(root, "title", out var title) || !OptionalText(root, "start", out var start) ||
            !OptionalInteger(root, "durationMinutes", out var duration) ||
            !OptionalText(root, "question", out var question))
            throw new JsonException("Calendar intent schema is invalid.");

        if (intent == "none" && status == "none" && title is null && start is null && duration is null && question is null)
            return new(intent, status, null, null, null, null);
        if (intent != "create") throw new JsonException("Calendar intent is not supported.");
        if (status == "clarify" && question is { Length: > 0 and <= 300 } && duration is null)
            return new(intent, status, title, start, null, question);
        if (status == "ready" && title is { Length: > 0 and <= 120 } &&
            start is { Length: > 0 and <= 40 } && duration is >= 1 and <= 480 && question is null &&
            DateTimeOffset.TryParseExact(start, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            return new(intent, status, title, start, duration, null);
        throw new JsonException("Calendar event fields are incomplete or invalid.");
    }

    internal static string? RequiredClarification(string source)
    {
        if (AmbiguousHour().Match(source) is { Success: true } ambiguous)
        {
            var suffix = source[(ambiguous.Index + ambiguous.Length)..];
            if (!Meridiem().IsMatch(suffix) && !DurationLead().IsMatch(source[..ambiguous.Index]))
            {
                var hour = int.Parse(ambiguous.Groups[1].Value, CultureInfo.InvariantCulture);
                var alternatives = hour == 12 ? "00:00 đêm hay 12:00 trưa" : $"{hour:00}:00 sáng hay {hour + 12:00}:00 chiều";
                return $"Mốc “{ambiguous.Value.Trim()}” chưa rõ là giờ nào. Bạn muốn {alternatives}?";
            }
        }
        if (!DateReference().IsMatch(source)) return "Bạn muốn tạo sự kiện vào ngày nào?";
        if (!TimeReference().IsMatch(source)) return "Bạn muốn tạo sự kiện lúc mấy giờ?";
        return null;
    }

    private static bool TryText(JsonElement root, string name, out string value)
    {
        value = "";
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            (value = property.GetString() ?? "").Length > 0;
    }

    private static bool OptionalText(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return value is null || value.Length <= 300;
    }

    private static bool OptionalInteger(JsonElement root, string name, out int? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var number)) return false;
        value = number;
        return true;
    }

    [GeneratedRegex(@"\b(1[0-2]|0?[1-9])\s*(?:h(?:\b|$)|giờ\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmbiguousHour();

    [GeneratedRegex(@"\b(?:sáng|trưa|chiều|tối|đêm|am|pm)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Meridiem();

    [GeneratedRegex(@"\b(?:trong|kéo dài|thời lượng)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationLead();

    [GeneratedRegex(@"\b(?:hôm nay|ngày mai|mai|ngày kia|mốt|ngày mốt|tomorrow|today|tonight|next\s+(?:week|monday|tuesday|wednesday|thursday|friday|saturday|sunday)|tuần sau|tuần tới|cuối tuần|thứ\s+(?:hai|ba|tư|năm|sáu|bảy|chủ nhật|[2-7])|\d{4}-\d{1,2}-\d{1,2}|(?:ngày\s+)?\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?|ngày\s+\d{1,2}(?:\s+tháng\s+\d{1,2})?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateReference();

    [GeneratedRegex(@"\b(?:\d{1,2}(?::\d{2})?\s*(?:h|giờ|am|pm)|\d{1,2}:\d{2}|(?:sáng|trưa|chiều|tối)\s+\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeReference();
}
