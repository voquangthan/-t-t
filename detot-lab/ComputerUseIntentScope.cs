using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Backend-derived authority for one chat turn. A model tool call can select a step,
/// but it cannot create or widen this scope.
/// </summary>
public sealed class ComputerUseIntentScope
{
    private static readonly Regex DirectStart = new(
        @"^\s*(?:(?:please|hãy|vui\s+lòng|làm\s+ơn|giúp\s+(?:tôi|tao|mình))\s+)*(?:mở|vào|xem|kiểm\s+tra|đọc|liệt\s+kê|bấm|nhấn|chọn|cuộn|nhập|gõ|điền|focus|click|press|select|scroll|type|enter|fill|open|launch|check|inspect|read|list|show|verify|go\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UnsafeContext = new(
        @"(?:ví\s+dụ|chẳng\s+hạn|giả\s+sử|tưởng\s+tượng|nếu\s+như|đừng|không\s+(?:mở|vào|xem|bấm|nhấn|chọn|cuộn|nhập|gõ|điền)|do\s+not|don't|never|hypothetical|for\s+example|example\s*:|how\s+(?:do\s+i|can\s+i|to)|what\s+does|explain\s+(?:the\s+)?(?:phrase|words?)|should\s+i|can\s+i)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex OpenCue = new(
        @"\b(?:mở|vào|open|launch|go\s+to)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ClickCue = new(
        @"\b(?:bấm|nhấn|chọn|click|press|select|invoke)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FocusCue = new(
        @"\b(?:focus|đặt\s+con\s+trỏ|chọn)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ScrollCue = new(
        @"\b(?:cuộn|scroll)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TypeCue = new(
        @"\b(?:nhập|gõ|điền|type|enter|fill)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NavigateCue = new(
        @"\b(?:điều\s+hướng|truy\s+cập|mở|vào|navigate|visit|open|go\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ClickTarget = new(
        @"\b(?:bấm|nhấn|chọn|click|press|select|focus)\s+(?:vào\s+)?(?:(?:nút|tab|menu|liên\s+kết|link|button|the\s+tab|the\s+button)\s+)?(?<target>[^,;.!?\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TargetTail = new(
        @"\s+(?:trong|trên|ở|in|on|then|rồi|sau\s+đó|and\s+then)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex OpenTarget = new(
        @"\b(?:mở|open|launch)\s+(?:ứng\s+dụng\s+)?(?<target>[\p{L}\p{N}][\p{L}\p{N} .+_'-]{0,60}?)(?=\s+(?:để|rồi|sau\s+đó|and\s+then|then|to|for|và|and)\b|[,;.!?]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TypeTarget = new(
        @"\b(?:vào|in|into)\s+(?:(?:the|ô|trường|hộp|thanh|field|input|search\s+box|address\s+bar)\s+)?(?<target>[^,;.!?\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string originalRequest;
    private readonly HashSet<string> apps;
    private readonly HashSet<string> openTargets;
    private readonly HashSet<string> clickTargets;
    private readonly HashSet<string> typeTargets;
    private readonly bool wholeDesktop;
    private readonly bool allowInvoke;
    private readonly bool allowFocus;
    private readonly bool allowScroll;
    private readonly bool allowType;
    private readonly bool allowNavigate;

    private ComputerUseIntentScope(string request, HashSet<string> apps, HashSet<string> openTargets,
        HashSet<string> clickTargets, HashSet<string> typeTargets, bool wholeDesktop, bool allowInvoke, bool allowFocus,
        bool allowScroll, bool allowType, bool allowNavigate)
    {
        originalRequest = request;
        this.apps = apps;
        this.openTargets = openTargets;
        this.clickTargets = clickTargets;
        this.typeTargets = typeTargets;
        this.wholeDesktop = wholeDesktop;
        this.allowInvoke = allowInvoke;
        this.allowFocus = allowFocus;
        this.allowScroll = allowScroll;
        this.allowType = allowType;
        this.allowNavigate = allowNavigate;
    }

    public static bool TryCreate(string? request, out ComputerUseIntentScope? scope)
    {
        scope = null;
        if (string.IsNullOrWhiteSpace(request) || request.Length > 1500 || request.Contains('\n') ||
            request.Contains('\r') || request.Contains('`') || request.Contains('"') ||
            request.Contains('\u201c') || request.Contains('\u201d') || request.Contains('\u2018') ||
            request.Contains('\u2019') || request.Contains('?') || UnsafeContext.IsMatch(request) ||
            !DirectStart.IsMatch(request)) return false;

        var apps = new HashSet<string>(StringComparer.Ordinal);
        AddIfMatch(request, @"\b(?:edge|microsoft\s+edge|browser|trình\s+duyệt)\b", "edge", apps);
        AddIfMatch(request, @"\b(?:vs\s*code|visual\s+studio\s+code|vscode)\b", "vscode", apps);
        AddIfMatch(request, @"\bvisual\s+studio\b(?!\s+code)", "visual-studio", apps);
        AddIfMatch(request, @"\b(?:file\s+explorer|explorer|file\s+manager|quản\s+lý\s+tệp)\b", "explorer", apps);
        AddIfMatch(request, @"\bnotepad\b|sổ\s+ghi\s+chú", "notepad", apps);
        // These services are accessed in the explicitly named browser adapter.
        if (Regex.IsMatch(request, @"\b(?:github|gmail|google\s+calendar)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            apps.Add("edge");

        var wholeDesktop = Regex.IsMatch(request, @"\b(?:toàn\s+bộ\s+màn\s+hình|màn\s+hình\s+desktop|desktop\s+screen|whole\s+desktop|entire\s+screen|all\s+open\s+windows|(?:liệt\s+kê|xem|kiểm\s+tra)\s+(?:các\s+)?(?:ứng\s+dụng|apps?)\s+(?:đang\s+mở|đang\s+chạy|open|running))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (wholeDesktop) apps.Add("desktop");
        var requestsWorkspace = Regex.IsMatch(request, @"\bworkspace\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && OpenCue.IsMatch(request);
        if (requestsWorkspace) apps.Add("vscode");

        var openTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in OpenTarget.Matches(request))
        {
            var target = match.Groups["target"].Value.Trim();
            if (Regex.IsMatch(target, @"^(?:github|gmail|google\s+calendar|workspace)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            if (ComputerUseUiPlanner.IsSafeInstalledApplicationName(target)) openTargets.Add(Normalize(target));
        }

        var clickTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ClickTarget.Matches(request))
        {
            var target = TargetTail.Replace(match.Groups["target"].Value.Trim(), "").Trim();
            target = Regex.Replace(target, @"^(?:the\s+)?(?:nút|tab|menu|liên\s+kết|link|button|menu\s+item|ô|trường)\s+",
                "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            target = Regex.Replace(target, @"\s+(?:rồi|and\s+then|then)\b.*$", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            if (target.Length is > 1 and <= 80) clickTargets.Add(Normalize(target));
        }

        var typeTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in TypeTarget.Matches(request))
        {
            var target = TargetTail.Replace(match.Groups["target"].Value.Trim(), "").Trim();
            target = Regex.Replace(target, @"\s+(?:ô|trường|hộp|thanh|field|input|box|bar)$", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            if (target.Length is > 1 and <= 80) typeTargets.Add(Normalize(target));
        }

        var hasOpen = OpenCue.IsMatch(request);
        var hasClick = ClickCue.IsMatch(request) && clickTargets.Count > 0;
        var hasType = TypeCue.IsMatch(request);
        var hasScroll = ScrollCue.IsMatch(request);
        var hasNavigate = NavigateCue.IsMatch(request) && Regex.IsMatch(request, @"https://[^\s]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (apps.Count == 0 && openTargets.Count == 0) return false;
        if (apps.Count == 0 && !(hasOpen && openTargets.Count > 0)) return false;
        scope = new ComputerUseIntentScope(request, apps, openTargets, clickTargets, typeTargets, wholeDesktop,
            hasClick, FocusCue.IsMatch(request) || hasType, hasScroll, hasType, hasNavigate);
        return true;
    }

    public bool Allows(ComputerUseUiAction action, string? observedElementName = null)
    {
        if (!ComputerUseUiPlanner.AllowedApps.Contains(action.App, StringComparer.Ordinal)) return false;
        if (action.App == "desktop")
        {
            if (action.Operation is "observe" or "activate-window") return wholeDesktop;
            if (action.Operation == "launch-installed")
                return openTargets.Contains(Normalize(action.Text ?? "")) &&
                    ComputerUseUiPlanner.IsExactUserText(action.Text!, originalRequest);
            return false;
        }
        if (!apps.Contains(action.App)) return false;

        return action.Operation switch
        {
            "observe" or "activate-window" => true,
            "launch-installed" => openTargets.Contains(Normalize(action.Text ?? "")) &&
                ComputerUseUiPlanner.IsExactUserText(action.Text!, originalRequest),
            "navigate" => allowNavigate && action.App == "edge" && action.Url is not null &&
                ComputerUseUiPlanner.IsExactUserText(action.Url, originalRequest),
            "invoke" => allowInvoke && MatchesClickTarget(observedElementName),
            "focus" => allowFocus && (MatchesClickTarget(observedElementName) || MatchesTypeTarget(observedElementName)),
            "scroll" => allowScroll,
            "type" => allowType && MatchesTypeTarget(observedElementName) && action.Text is not null &&
                ComputerUseUiPlanner.IsExactUserText(action.Text, originalRequest),
            _ => false
        };
    }

    public bool AllowsFixedAction(string action) => action switch
    {
        "open-workspace" => Regex.IsMatch(originalRequest, @"\bworkspace\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            OpenCue.IsMatch(originalRequest),
        "open-vscode" => apps.Contains("vscode") && ContainsRequestedOpenTarget("vs code", "visual studio code", "vscode"),
        "open-edge" => apps.Contains("edge") && ContainsRequestedOpenTarget("edge", "microsoft edge", "browser", "trình duyệt"),
        "open-github" => apps.Contains("edge") && ContainsWord("github") && OpenCue.IsMatch(originalRequest),
        "read-github-account" => apps.Contains("edge") && ContainsWord("github") &&
            Regex.IsMatch(originalRequest, @"(?:tài\s+khoản|account)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        "read-github-pull-requests" => apps.Contains("edge") && ContainsWord("github") &&
            Regex.IsMatch(originalRequest, @"\b(?:pr|pull\s+requests?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        "read-approved-applications" => wholeDesktop,
        _ => false
    };

    public static void RecordObservedElements(JsonElement result, IDictionary<string, string> observations)
    {
        RecordFrom(result);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("observation", out var nested))
            RecordFrom(nested);

        void RecordFrom(JsonElement observation)
        {
            if (observation.ValueKind != JsonValueKind.Object ||
                !observation.TryGetProperty("observationId", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                !observation.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array) return;
            var observationId = idValue.GetString();
            foreach (var element in elements.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty("id", out var elementId) || elementId.ValueKind != JsonValueKind.String ||
                    !element.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                observations[$"{observationId}:{elementId.GetString()}"] = name.GetString() ?? "";
            }
        }
    }

    private bool MatchesClickTarget(string? elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName) || clickTargets.Count == 0) return false;
        var normalizedElement = Normalize(elementName);
        return clickTargets.Any(target => normalizedElement.Contains(target, StringComparison.Ordinal));
    }

    private bool MatchesTypeTarget(string? elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName)) return false;
        var normalized = Normalize(elementName);
        return typeTargets.Any(target => normalized.Contains(target, StringComparison.Ordinal)) ||
            normalized.Contains("search", StringComparison.Ordinal) &&
                Regex.IsMatch(originalRequest, @"\b(?:tìm\s+kiếm|search)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            normalized.Contains("address", StringComparison.Ordinal) &&
                Regex.IsMatch(originalRequest, @"\b(?:địa\s+chỉ|address)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void AddIfMatch(string request, string pattern, string app, ISet<string> apps)
    {
        if (Regex.IsMatch(request, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) apps.Add(app);
    }

    private bool ContainsRequestedOpenTarget(params string[] targets) => openTargets.Any(openTarget =>
        targets.Any(target => Normalize(target) == openTarget));

    private bool ContainsWord(string value) => Regex.IsMatch(originalRequest,
        $@"\b{Regex.Escape(value)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }
}
