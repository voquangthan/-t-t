using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        @"(?:ví\s+dụ|chẳng\s+hạn|giả\s+sử|tưởng\s+tượng|nếu\s+như|đừng|không\s+(?:mở|vào|xem|bấm|nhấn|chọn|cuộn|nhập|gõ|điền)|đọc\s+(?:câu|đoạn\s+văn|nội\s+dung)|read\s+(?:this\s+)?(?:text|sentence|the\s+following)|dịch|translate|giải\s+thích|explain|summarize|tóm\s+tắt|phân\s+tích|analyze|báo\s+lỗi|bị\s+lỗi|error|failed|not\s+working|can't|cannot|won't|doesn't|do\s+not|don't|never|hypothetical|for\s+example|example\s*:|how\s+(?:do\s+i|can\s+i|to)|what\s+does|explain\s+(?:the\s+)?(?:phrase|words?)|should\s+i|can\s+i|cách\s+mở|mô\s+tả\s+về|description\s+of)",
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
    private static readonly Regex CurrentUiSurface = new(
        @"\b(?:cửa\s+sổ|window|tab|trang|page|giao\s+diện|ui|màn\s+hình|screen)\b.{0,32}\b(?:đang\s+mở|hiện\s+tại|đang\s+hiển\s+thị|currently\s+open|currently\s+visible|open\s+now|visible\s+now)\b|\b(?:đang\s+mở|hiện\s+tại|đang\s+hiển\s+thị|currently\s+open|currently\s+visible|open\s+now|visible\s+now)\b.{0,32}\b(?:cửa\s+sổ|window|tab|trang|page|giao\s+diện|ui|màn\s+hình|screen)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExplicitInputSurface = new(
        @"\b(?:ô|trường|hộp|thanh|field|input|search\s+box|address\s+bar)\b",
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
    private readonly bool requiresNewWindow;
    private readonly bool allowCurrentUiObservation;
    private readonly bool allowTargetOnlyObservation;
    public bool HasInteractiveUiScope { get; }

    private ComputerUseIntentScope(string request, HashSet<string> apps, HashSet<string> openTargets,
        HashSet<string> clickTargets, HashSet<string> typeTargets, bool wholeDesktop, bool allowInvoke, bool allowFocus,
        bool allowScroll, bool allowType, bool allowNavigate, bool requiresNewWindow,
        bool allowCurrentUiObservation, bool allowTargetOnlyObservation, bool hasInteractiveUiScope)
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
        this.requiresNewWindow = requiresNewWindow;
        this.allowCurrentUiObservation = allowCurrentUiObservation;
        this.allowTargetOnlyObservation = allowTargetOnlyObservation;
        HasInteractiveUiScope = hasInteractiveUiScope;
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

        // Keep the named adapters for common apps. For other explicitly requested
        // installed apps, use the existing desktop UIA adapter as a narrowly scoped
        // fallback; this does not grant whole-desktop observation.
        if (openTargets.Any(target => !apps.Any(app => IsAppTarget(app, target))))
            apps.Add("desktop");

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
        var requiresNewWindow = Regex.IsMatch(request, @"(?:cửa\s+sổ\s+mới|new\s+window)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var allowCurrentUiObservation = apps.Count == 1 && CurrentUiSurface.IsMatch(request);
        var hasBoundClickTarget = hasClick && clickTargets.Any(target => !apps.Any(app => IsAppTarget(app, target)));
        var hasBoundInputTarget = hasType && ExplicitInputSurface.IsMatch(request) &&
            typeTargets.Any(target => !apps.Any(app => IsAppTarget(app, target)));
        var allowTargetOnlyObservation = apps.Count == 1 &&
            (hasBoundClickTarget || hasBoundInputTarget || hasScroll);
        var hasInteractiveUiScope = wholeDesktop || openTargets.Count > 0 || allowCurrentUiObservation ||
            allowTargetOnlyObservation || hasNavigate;
        scope = new ComputerUseIntentScope(request, apps, openTargets, clickTargets, typeTargets, wholeDesktop,
            hasClick, FocusCue.IsMatch(request) || hasType, hasScroll, hasType, hasNavigate, requiresNewWindow,
            allowCurrentUiObservation, allowTargetOnlyObservation, hasInteractiveUiScope);
        return true;
    }

    public bool Allows(ComputerUseUiAction action, string? observedElementName = null, string? observedTarget = null)
    {
        if (!ComputerUseUiPlanner.AllowedApps.Contains(action.App, StringComparer.Ordinal)) return false;
        if (action.App == "desktop")
        {
            if (action.Operation == "observe")
                return wholeDesktop || allowTargetOnlyObservation && action.Text is not null &&
                    openTargets.Any(target => IsAppTarget(action.Text, target)) &&
                    ComputerUseUiPlanner.IsExactUserText(action.Text, originalRequest);
            if (action.Operation == "activate-window")
                return wholeDesktop || IsScopedTarget(observedTarget, requireNewWindow: requiresNewWindow);
            if (action.Operation == "launch-installed")
                return openTargets.Contains(Normalize(action.Text ?? "")) &&
                    ComputerUseUiPlanner.IsExactUserText(action.Text!, originalRequest);
            if (!wholeDesktop && !IsScopedTarget(observedTarget, requireNewWindow: requiresNewWindow)) return false;
            return action.Operation switch
            {
                "invoke" => allowInvoke && MatchesClickTarget(observedElementName),
                "focus" => allowFocus && (MatchesClickTarget(observedElementName) || MatchesTypeTarget(observedElementName)),
                "scroll" => allowScroll,
                "type" => allowType && MatchesTypeTarget(observedElementName) && action.Text is not null &&
                    ComputerUseUiPlanner.IsExactUserText(action.Text, originalRequest),
                _ => false
            };
        }
        if (!apps.Contains(action.App)) return false;

        return action.Operation switch
        {
            "observe" => AllowsObservationTarget(action.App),
            "activate-window" => AllowsWindowActivationTarget(action.App),
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

    public string FilterObservationForModel(JsonElement result, ComputerUseUiAction sourceAction)
    {
        if (wholeDesktop || allowCurrentUiObservation)
            return result.GetRawText();

        var root = JsonNode.Parse(result.GetRawText());
        if (root is null || !ContainsObservation(root)) return result.GetRawText();
        RestrictObservation(root);
        return root.ToJsonString();

        static bool ContainsObservation(JsonNode? node)
        {
            if (node is JsonObject value)
            {
                if (value.ContainsKey("observationId") || value.ContainsKey("elements") || value.ContainsKey("windows")) return true;
                return value.Any(property => ContainsObservation(property.Value));
            }
            return node is JsonArray items && items.Any(ContainsObservation);
        }

        void RestrictObservation(JsonNode? node)
        {
            if (node is not JsonObject value) return;
            foreach (var property in new[] { "title", "windowTitle", "text", "value", "url", "screenshot", "screenText" })
                value.Remove(property);

            if (value["windows"] is JsonArray windows)
            {
                for (var index = windows.Count - 1; index >= 0; index--)
                {
                    if (windows[index] is not JsonObject window) { windows.RemoveAt(index); continue; }
                    var id = window["windowId"]?.DeepClone();
                    windows[index] = id is null ? new JsonObject() : new JsonObject { ["windowId"] = id };
                }
            }
            if (value["elements"] is JsonArray elements)
            {
                for (var index = elements.Count - 1; index >= 0; index--)
                {
                    if (elements[index] is not JsonObject element ||
                        !allowTargetOnlyObservation && requiresNewWindow || !IsRequestedObservationElement(element))
                    {
                        elements.RemoveAt(index);
                        continue;
                    }
                    var safeElement = new JsonObject();
                    foreach (var property in new[] { "id", "type", "name", "enabled", "offscreen", "invokable", "scrollDirection" })
                        if (element[property] is { } propertyValue) safeElement[property] = propertyValue.DeepClone();
                    elements[index] = safeElement;
                }
            }
            foreach (var child in value.ToArray())
            {
                if (child.Value is JsonObject) RestrictObservation(child.Value);
                else if (child.Value is JsonArray array)
                    foreach (var item in array) RestrictObservation(item);
            }
        }

        bool IsRequestedObservationElement(JsonObject element)
        {
            var name = element["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var text)
                ? text : null;
            var scrollDirection = element["scrollDirection"] is JsonValue scrollValue &&
                scrollValue.TryGetValue<string>(out var direction) ? direction : null;
            var matchesClickTarget = allowInvoke && MatchesClickTarget(name);
            var matchesInputTarget = allowType && MatchesTypeTarget(name);
            var matchesScrollableControl = allowScroll && !string.IsNullOrWhiteSpace(scrollDirection);
            return sourceAction.Operation switch
            {
                "observe" => matchesClickTarget || matchesInputTarget || matchesScrollableControl,
                "invoke" => matchesClickTarget,
                "focus" => matchesClickTarget || matchesInputTarget,
                "type" => matchesInputTarget,
                "scroll" => matchesScrollableControl,
                _ => false
            };
        }
    }

    private bool AllowsObservationTarget(string appName) => wholeDesktop ||
        allowCurrentUiObservation && apps.Count == 1 && apps.Any(app => IsAppTarget(app, appName)) ||
        allowTargetOnlyObservation && apps.Count == 1 && apps.Any(app => IsAppTarget(app, appName)) ||
        (requiresNewWindow || allowTargetOnlyObservation) && openTargets.Any(target => IsAppTarget(appName, target));

    private bool AllowsWindowActivationTarget(string appName) => wholeDesktop ||
        allowCurrentUiObservation && apps.Count == 1 && apps.Any(app => IsAppTarget(app, appName)) ||
        allowTargetOnlyObservation && apps.Count == 1 && apps.Any(app => IsAppTarget(app, appName)) ||
        requiresNewWindow && openTargets.Any(target => IsAppTarget(appName, target));

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

    public static void RecordObservedTargets(JsonElement result, ComputerUseUiAction sourceAction,
        IDictionary<string, string> observationTargets, IDictionary<string, string> windowTargets)
    {
        string? target;
        if (sourceAction.App != "desktop") target = sourceAction.App;
        else if (sourceAction.Operation is "observe" or "launch-installed") target = sourceAction.Text;
        else if (sourceAction.Operation == "activate-window" && sourceAction.WindowId is not null &&
                 windowTargets.TryGetValue(sourceAction.WindowId, out var selectedTarget)) target = selectedTarget;
        else if (sourceAction.ObservationId is not null &&
                 observationTargets.TryGetValue(sourceAction.ObservationId, out var priorTarget)) target = priorTarget;
        else target = null;
        if (target is not null)
        {
            var scopedTarget = sourceAction.App == "desktop" && !target.Contains('|')
                ? $"{Normalize(target)}|{(sourceAction.Operation == "launch-installed" ? "new" : "observed")}" : target;
            RecordObservationTarget(result, scopedTarget);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("observation", out var nested))
                RecordObservationTarget(nested, scopedTarget);
            if (sourceAction.App == "desktop" && (sourceAction.Operation is "observe" or "launch-installed") &&
                result.ValueKind == JsonValueKind.Object && result.TryGetProperty("windows", out var windows) &&
                windows.ValueKind == JsonValueKind.Array)
            {
                foreach (var window in windows.EnumerateArray())
                    if (window.ValueKind == JsonValueKind.Object && window.TryGetProperty("windowId", out var id) &&
                        id.ValueKind == JsonValueKind.String)
                        windowTargets[id.GetString()!] = scopedTarget;
            }
            if (sourceAction.App == "desktop" && sourceAction.Operation == "launch-installed" &&
                result.ValueKind == JsonValueKind.Object && result.TryGetProperty("windowId", out var launchedWindowId) &&
                launchedWindowId.ValueKind == JsonValueKind.String)
                windowTargets[launchedWindowId.GetString()!] = scopedTarget;
        }

        void RecordObservationTarget(JsonElement observation, string scopedTarget)
        {
            if (observation.ValueKind == JsonValueKind.Object &&
                observation.TryGetProperty("observationId", out var id) && id.ValueKind == JsonValueKind.String)
                observationTargets[id.GetString()!] = scopedTarget;
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

    private bool IsScopedTarget(string? target, bool requireNewWindow)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var separator = target.IndexOf('|');
        if (separator < 0) return false;
        var appName = target[..separator];
        var origin = target[(separator + 1)..];
        return AllowsExistingUiTarget(appName, origin) && (!requireNewWindow || origin == "new");
    }

    private bool AllowsExistingUiTarget(string appName, string origin) =>
        allowCurrentUiObservation && apps.Count == 1 && apps.Any(app => IsAppTarget(app, appName)) ||
        allowTargetOnlyObservation && apps.Count == 1 && apps.Any(app => IsAppTarget(app, appName)) ||
        openTargets.Any(target => IsAppTarget(appName, target)) && (origin is "new" or "observed");

    private static bool IsAppTarget(string app, string target)
    {
        var normalizedApp = Normalize(app);
        var normalizedTarget = Normalize(target);
        var aliases = normalizedApp switch
        {
            "edge" => new[] { "edge", "microsoftedge", "browser", "trinhduyet" },
            "vscode" => new[] { "vscode", "visualstudiocode" },
            "visualstudio" => new[] { "visualstudio", "microsoftvisualstudio" },
            "explorer" => new[] { "explorer", "fileexplorer", "filemanager", "quanlytep" },
            "notepad" => new[] { "notepad", "soghinchu" },
            _ => new[] { normalizedApp }
        };
        return aliases.Contains(normalizedTarget, StringComparer.Ordinal);
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
