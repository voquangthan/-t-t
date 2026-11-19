using System.Text.RegularExpressions;
using System.Text.Json;

public static class ComputerUseChatIntent
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const string Prefix = @"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?";
    private const string Suffix = @"(?:\s+(?:giúp\s+(?:t|tao|tôi|mình)|cho\s+(?:t|tao|tôi|mình)|đi|nha|nhé|với|please))*\s*[.!?]*\s*$";

    private static readonly (string Action, Regex Pattern)[] Patterns =
    [
        ("open-vscode", new(Prefix + @"(?:mở|open|launch|khởi\s+chạy)\s+(?:cái\s+)?(?:vs\s*code|visual\s+studio\s+code)" + Suffix, Options)),
        ("open-edge", new(Prefix + @"(?:mở|open|launch|khởi\s+chạy)\s+(?:cái\s+)?(?:microsoft\s+)?edge" + Suffix, Options)),
        // File Explorer is intentionally limited to the configured workspace. This makes a
        // natural request useful without turning the Companion into an arbitrary path launcher.
        ("open-workspace", new(Prefix + @"(?:mở|open)\s+(?:(?:project\s+)?workspace|(?:file\s+)?explorer|trình\s+quản\s+lý\s+tệp)" + Suffix, Options)),
        ("open-github", new(Prefix + @"(?:mở|open)\s+(?:(?:repository|repo)\s+)?github" + Suffix, Options)),
        ("open-settings", new(Prefix + @"(?:mở|open|launch)\s+(?:windows\s+)?settings(?:\s+(?:windows|của\s+windows))?" + Suffix, Options)),
        ("open-task-manager", new(Prefix + @"(?:mở|open|launch)\s+(?:windows\s+)?task\s+manager" + Suffix, Options)),
        ("open-word", new(Prefix + @"(?:mở|open|launch)\s+(?:microsoft\s+)?word" + Suffix, Options)),
        ("open-excel", new(Prefix + @"(?:mở|open|launch)\s+(?:microsoft\s+)?excel" + Suffix, Options)),
        ("open-powerpoint", new(Prefix + @"(?:mở|open|launch)\s+(?:microsoft\s+)?powerpoint" + Suffix, Options)),
        ("open-pdf-reader", new(Prefix + @"(?:mở|open|launch)\s+(?:pdf\s+reader|acrobat(?:\s+reader)?)" + Suffix, Options)),
        ("open-notepad", new(Prefix + @"(?:mở|open|launch)\s+(?:windows\s+)?notepad" + Suffix, Options)),
        ("open-calculator", new(Prefix + @"(?:mở|open|launch)\s+(?:windows\s+)?(?:calculator|máy\s+tính)" + Suffix, Options)),
        ("open-paint", new(Prefix + @"(?:mở|open|launch)\s+(?:microsoft\s+)?paint" + Suffix, Options)),
        ("read-github-account", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|check|read|inspect)\s+(?:tài\s+khoản\s+github(?:\s+(?:hiện\s+tại|đang\s+dùng))?|current\s+github\s+account|github\s+account)" + Suffix, Options)),
        ("read-github-pull-requests", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|check|read|inspect)\s+(?:(?:danh\s+sách|list)\s+)?(?:pr(?:s)?|pull\s+requests?)(?:\s+(?:và|and|with)\s+(?:ci|checks?))?" + Suffix, Options)),
        ("read-approved-applications", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|liệt\s+kê|check|read|list|inspect)\s+(?:các\s+)?(?:ứng\s+dụng|apps?|applications?)(?:\s+(?:đang\s+mở|đang\s+chạy|open|currently\s+open|running))" + Suffix, Options)),
        ("read-workspace-explorer", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|check|read|inspect)\s+(?:workspace\s+(?:trong\s+)?)?(?:file\s+)?explorer(?:\s+(?:của\s+workspace))?" + Suffix, Options)),
        ("read-windows-settings", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|check|read|inspect)\s+(?:windows\s+)?settings(?:\s+(?:windows|của\s+windows))?" + Suffix, Options)),
        ("read-desktop-ui", new(Prefix + @"(?:quan\s+sát|xem|đọc|inspect|observe)\s+(?:giao\s+diện|màn\s+hình|desktop|các\s+cửa\s+sổ\s+đang\s+mở|open\s+windows)" + Suffix, Options)),
    ];

    public static string? DetectDirectRequest(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 160 || message.Contains('\n') || message.Contains('\r') ||
            message.Contains('`') || message.Contains('"') || message.Contains('\'')) return null;
        var text = message.Trim();
        foreach (var (action, pattern) in Patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            return action;
        }
        return null;
    }

    public static string? ObservationTargetForAction(string action) => action switch
    {
        "read-approved-applications" => "approved-applications",
        "read-desktop-ui" => "desktop-ui",
        "read-workspace-explorer" => "workspace-explorer",
        "read-windows-settings" => "windows-settings",
        "read-github-account" => "github-account",
        "read-github-pull-requests" => "github-pull-requests",
        _ => null
    };

}

public sealed record ComputerUseTaskScope(string Intent, string[] AllowedOperations, string? Application = null,
    string? ObservationTarget = null, string? RequestedValue = null, string? TargetLabel = null,
    string? TargetControlType = null, bool RequiresApplicationLaunchBeforeTarget = false,
    int MaximumTargetActions = 1, bool BindApplicationProcess = false)
{
    private static readonly IReadOnlyDictionary<string, string[]> ApplicationProcesses =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["edge"] = ["msedge"], ["vscode"] = ["Code"], ["workspace"] = ["explorer"],
            ["settings"] = ["SystemSettings"], ["task-manager"] = ["Taskmgr"],
            ["word"] = ["WINWORD"], ["excel"] = ["EXCEL"], ["powerpoint"] = ["POWERPNT"],
            ["pdf-reader"] = ["Acrobat", "AcroRd32"], ["github"] = ["msedge"],
            ["notepad"] = ["Notepad"], ["calculator"] = ["CalculatorApp"], ["paint"] = ["mspaint"]
        };

    public bool Allows(ComputerUseAgentAction action)
    {
        if (!AllowedOperations.Contains(action.Operation, StringComparer.Ordinal)) return false;
        if (action.Operation == "open_application" && action.Application != Application) return false;
        if (action.Operation == "open_workspace" && Application != "workspace") return false;
        if (action.Operation is not ("open_application" or "open_workspace") && action.Application is not null) return false;
        if (action.Operation is "activate_window" or "click" or "select_tab" or "select" or "expand" or "collapse" &&
            (action.Value is not null || action.Direction is not null)) return false;
        if ((action.Operation is "open_workspace_item" or "read_workspace_item") && action.Value != RequestedValue) return false;
        if (action.Operation == "type_text" && action.Value != RequestedValue) return false;
        if (action.Operation == "scroll" && RequestedValue is not null && action.Direction != RequestedValue) return false;
        if (action.Operation is not ("open_workspace_item" or "read_workspace_item" or "type_text" or "scroll" or "observe") && RequestedValue is not null) return false;
        return true;
    }

    public bool MatchesTarget(string? controlType, string? name) => TargetLabel is null ||
        controlType == TargetControlType && string.Equals(name?.Trim(), TargetLabel, StringComparison.OrdinalIgnoreCase);

    public bool MatchesObservedTarget(ComputerUseObservedTarget target) =>
        MatchesTarget(target.ControlType, target.Name) && MatchesApplicationProcess(target.Process);

    public bool MatchesApplicationProcess(string? process)
    {
        var bindToApplication = BindApplicationProcess || RequiresApplicationLaunchBeforeTarget && TargetLabel is not null;
        return !bindToApplication || ApplicationProcesses.TryGetValue(Application ?? "", out var expected) &&
            expected.Contains(process ?? "", StringComparer.OrdinalIgnoreCase);
    }
}

public static class ComputerUseTaskAuthorization
{
    private static readonly Regex ExplicitTwoStepRequest = new(
        @"^\s*(?<first>[^\r\n]{1,256}?)\s+(?:rồi|sau\s+đó|then)\s+(?<second>[^\r\n]{1,384}?)\s*[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly (string Operation, string ControlType, Regex Pattern)[] NamedUiRequests =
    [
        ("activate_window", "Window", new(@"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:chuyển|đổi|focus|activate)\s+(?:sang\s+)?(?:cửa\s+sổ|window)\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
        ("select_tab", "TabItem", new(@"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:chọn|select)\s+tab\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
        ("select", "ListItem", new(@"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:chọn|select)\s+(?:mục\s+)?(?:trong\s+)?danh\s+sách\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
        ("select", "TreeItem", new(@"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:chọn|select)\s+(?:mục\s+)?(?:trong\s+)?cây\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
        ("scroll", "Document", new(@"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:cuộn|scroll)\s+(?<direction>lên|xuống|up|down)\s+(?:trong|in)\s+(?:tài\s+liệu|document)\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
        ("expand", "TreeItem", new(@"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:mở\s+rộng|expand)\s+(?:mục|item|thư\s+mục|folder)\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
        ("collapse", "TreeItem", new(@"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:thu\s+gọn|collapse)\s+(?:mục|item|thư\s+mục|folder)\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
        ("click", "Button", new(@"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:bấm|nhấn|click)\s+(?:vào\s+)?(?:nút|button)\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
        ("click", "Hyperlink", new(@"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:bấm|nhấn|click)\s+(?:vào\s+)?(?:link|liên\s+kết)\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
    ];

    private static readonly Regex WorkspaceItemRequest = new(
        @"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:mở|open)\s+(?:tệp|file|thư\s+mục|folder)\s+(?<path>[^\r\n""'`<>]{1,512}?)\s*[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReadWorkspaceItemRequest = new(
        @"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:đọc|read|xem\s+nội\s+dung|summarize|tóm\s+tắt)\s+(?:tài\s+liệu|document|tệp|file)\s+(?<path>[^\r\n""'`<>]{1,512}?)\s*[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TypeIntoFieldRequest = new(
        @"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:nhập|gõ|type)\s+(?<value>[^\r\n""'`<>]{1,512}?)\s+(?:vào|into)\s+(?:ô|field)\s+(?<label>[^\r\n""'`<>]{1,96}?)\s*[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SafeInspectionIntent = new(
        @"\b(?:inspect|examine|look\s+at|review|check|read|view|summari[sz]e|tell\s+me\s+about|show\s+me|xem|kiểm\s+tra|đọc|quan\s+sát|tóm\s+tắt|cho\s+(?:tôi|mình|t)\s+xem|cho\s+biết)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UnsafeInspectionIntent = new(
        @"\b(?:delete|remove|send|submit|save|overwrite|install|uninstall|execute|run|sign\s+in|log\s+in|log\s+out|change|modify|edit|type|enter|fill|create|purchase|buy|pay|cancel|merge|push|commit|click|bấm|nhấn|xóa|gỡ|gửi|nộp|lưu|ghi\s+đè|cài\s+đặt|chạy|thực\s+thi|đăng\s+nhập|đăng\s+xuất|thay\s+đổi|sửa|nhập|điền|tạo|mua|thanh\s+toán|hủy)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WebAddress = new(@"(?:https?://|www\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly (string Application, Regex Pattern)[] InspectionApplications =
    [
        ("edge", new(@"\b(?:microsoft\s+)?edge\b|\bbrowser\b|trình\s+duyệt", Options)),
        ("vscode", new(@"\b(?:vs\s*code|visual\s+studio\s+code)\b", Options)),
        ("settings", new(@"\b(?:windows\s+)?settings\b|cài\s+đặt\s+windows", Options)),
        ("task-manager", new(@"\b(?:windows\s+)?task\s+manager\b|trình\s+quản\s+lý\s+tác\s+vụ", Options)),
        ("word", new(@"\b(?:microsoft\s+)?word\b", Options)),
        ("excel", new(@"\b(?:microsoft\s+)?excel\b", Options)),
        ("powerpoint", new(@"\b(?:microsoft\s+)?powerpoint\b", Options)),
        ("pdf-reader", new(@"\b(?:pdf\s+reader|acrobat(?:\s+reader)?)\b", Options)),
        ("notepad", new(@"\b(?:windows\s+)?notepad\b", Options)),
        ("calculator", new(@"\b(?:windows\s+)?calculator\b|máy\s+tính", Options)),
        ("paint", new(@"\b(?:microsoft\s+)?paint\b", Options))
    ];

    public static bool ShouldExposeTool(ComputerUseTaskScope? scope, bool interactionConsent,
        bool observationConsent, bool workspaceFileConsent = false) => scope is not null &&
        (scope.AllowedOperations.Contains("read_workspace_item", StringComparer.Ordinal)
            ? workspaceFileConsent
            : interactionConsent && observationConsent);

    public static bool ShouldDispatchToolCall(ComputerUseTaskScope? scope, string? toolName) =>
        scope is not null && toolName == "detot_computer_use";

    public static bool RequiresToolInvocation(ComputerUseTaskScope? scope) =>
        scope?.AllowedOperations.Contains("read_workspace_item", StringComparer.Ordinal) == true ||
        scope?.BindApplicationProcess == true;

    public static ComputerUseTaskScope? FromCurrentMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 640 || message.Contains('\n') || message.Contains('\r') ||
            message.Contains('`') || message.Contains('"') || message.Contains('\'')) return null;
        var compound = ExplicitTwoStepRequest.Match(message);
        if (compound.Success)
        {
            var first = FromSingleRequest(compound.Groups["first"].Value.Trim());
            var secondText = compound.Groups["second"].Value.Trim();
            var second = FromSingleRequest(secondText) ?? FromPlannedInspectionRequest(secondText, first?.Application);
            var plannedInspection = second is { Intent: "planned-app-inspection" } &&
                string.Equals(first?.Application, second.Application, StringComparison.Ordinal);
            var namedTargetAction = second is not null && second.AllowedOperations.Contains("observe", StringComparer.Ordinal) &&
                second.AllowedOperations.Count(operation => operation != "observe") == 1 &&
                second.TargetLabel is not null && second.TargetControlType is not null &&
                !second.AllowedOperations.Contains("read_workspace_item", StringComparer.Ordinal);
            if (first is null || second is null ||
                first.AllowedOperations.Any(operation => operation is not ("observe" or "open_application" or "open_workspace")) ||
                first.Application is null ||
                (!plannedInspection && !namedTargetAction)) return null;

            var targetOperations = plannedInspection
                ? second.AllowedOperations.Where(operation => operation is not ("observe" or "open_application" or "open_workspace"))
                : second.AllowedOperations.Where(operation => operation != "observe");
            var allowedOperations = first.AllowedOperations.Where(operation => operation != "observe")
                .Concat(targetOperations).Prepend("observe").Distinct(StringComparer.Ordinal).ToArray();
            return new(first.Intent + "-then-" + second.Intent,
                allowedOperations,
                first.Application, RequestedValue: second.RequestedValue,
                TargetLabel: second.TargetLabel, TargetControlType: second.TargetControlType,
                RequiresApplicationLaunchBeforeTarget: true, MaximumTargetActions: plannedInspection ? 2 : 1,
                BindApplicationProcess: plannedInspection);
        }
        return FromSingleRequest(message) ?? FromPlannedInspectionRequest(message);
    }

    private static ComputerUseTaskScope? FromPlannedInspectionRequest(string message, string? inheritedApplication = null)
    {
        if (!SafeInspectionIntent.IsMatch(message) || UnsafeInspectionIntent.IsMatch(message) || WebAddress.IsMatch(message)) return null;
        var applications = InspectionApplications.Where(candidate => candidate.Pattern.IsMatch(message))
            .Select(candidate => candidate.Application).Distinct(StringComparer.Ordinal).ToArray();
        if (applications.Length > 1 || (applications.Length == 0 && inheritedApplication is null)) return null;
        var application = applications.Length == 1 ? applications[0] : inheritedApplication!;
        if ((inheritedApplication is not null && application != inheritedApplication) ||
            !InspectionApplications.Any(candidate => candidate.Application == application)) return null;
        const string launchOperation = "open_application";
        return new("planned-app-inspection", ["observe", launchOperation, "activate_window", "select_tab", "select", "expand", "collapse", "scroll"],
            application, RequiresApplicationLaunchBeforeTarget: true, MaximumTargetActions: 2,
            BindApplicationProcess: true);
    }

    private static ComputerUseTaskScope? FromSingleRequest(string message)
    {
        var readItemMatch = ReadWorkspaceItemRequest.Match(message);
        if (readItemMatch.Success)
        {
            var path = readItemMatch.Groups["path"].Value.Trim();
            if (path.Length > 0 && path.Length <= 512 && DesktopOperator.IsSafeWorkspaceDocumentPath(path))
                return new("read-workspace-item", ["read_workspace_item"], RequestedValue: path);
            return null;
        }
        var itemMatch = WorkspaceItemRequest.Match(message);
        if (itemMatch.Success)
        {
            var path = itemMatch.Groups["path"].Value.Trim();
            if (path.Length is > 0 and <= 512 && DesktopOperator.IsSafeWorkspaceRelativePath(path))
                return new("open-workspace-item", ["observe", "open_workspace_item"], RequestedValue: path);
            return null;
        }
        var typeMatch = TypeIntoFieldRequest.Match(message);
        if (typeMatch.Success)
        {
            var value = typeMatch.Groups["value"].Value.Trim();
            var label = typeMatch.Groups["label"].Value.Trim();
            if (value.Length is > 0 and <= 512 && label.Length is > 0 and <= 96)
                return new("type-into-field", ["observe", "type_text"], RequestedValue: value,
                    TargetLabel: label, TargetControlType: "Edit");
            return null;
        }
        foreach (var (operation, controlType, pattern) in NamedUiRequests)
        {
            var match = pattern.Match(message);
            if (!match.Success) continue;
            var label = match.Groups["label"].Value.Trim();
            var direction = match.Groups["direction"].Success
                ? match.Groups["direction"].Value.Equals("lên", StringComparison.OrdinalIgnoreCase) ||
                  match.Groups["direction"].Value.Equals("up", StringComparison.OrdinalIgnoreCase) ? "up" : "down"
                : null;
            return label.Length is > 0 and <= 96 && !(operation == "click" && IsCompoundClickRequest(label))
                ? new("named-ui-" + operation, ["observe", operation], RequestedValue: direction,
                    TargetLabel: label, TargetControlType: controlType)
                : null;
        }
        var intent = ComputerUseChatIntent.DetectDirectRequest(message);
        if (intent is null) return null;
        var observationTarget = ComputerUseChatIntent.ObservationTargetForAction(intent);
        if (observationTarget is not null)
            return new(intent, ["observe"], ObservationTarget: observationTarget);

        var application = intent switch
        {
            "open-edge" => "edge", "open-vscode" => "vscode", "open-workspace" => "workspace",
            "open-github" => "github", "open-settings" => "settings", "open-task-manager" => "task-manager",
            "open-word" => "word", "open-excel" => "excel", "open-powerpoint" => "powerpoint",
            "open-pdf-reader" => "pdf-reader", "open-notepad" => "notepad",
            "open-calculator" => "calculator", "open-paint" => "paint", _ => null
        };
        if (application is null) return null;
        string[] operations = intent == "open-workspace" ? ["observe", "open_workspace"] : ["observe", "open_application"];
        return new(intent, operations, application);
    }

    private static bool IsCompoundClickRequest(string label) => Regex.IsMatch(label,
        @"\b(?:rồi|sau\s+đó|tiếp\s+theo|then|and\s+then)\b|\b(?:và|and)\s+(?:xóa|gửi|đóng|lưu|áp\s+dụng|delete|send|close|save|submit|apply|remove)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool HasUsableObservation(string? status, bool hasTarget) => status switch
    {
        "available" or "partial" => hasTarget,
        "empty" => true,
        _ => false
    };

    public static bool IsActionVerified(string? status, bool succeeded, string? verification) =>
        status != "approval_required" && succeeded && verification == "verified";
}

public sealed record ComputerUseObservedTarget(string ControlType, string Name, HashSet<string> AllowedActions,
    string? WindowTitle = null, string? Process = null);

public sealed record ComputerUseObservationSelection(string Status, JsonElement[] Elements, string[] WindowIds,
    bool Truncated);

public static class ComputerUseObservationFilter
{
    private const int CompanionElementLimit = 72;

    public static Dictionary<string, ComputerUseObservedTarget> CollectObservedTargets(JsonElement observation,
        ComputerUseTaskScope scope)
    {
        var targets = new Dictionary<string, ComputerUseObservedTarget>(StringComparer.Ordinal);
        var windowsById = new Dictionary<string, (string? Title, string? Process)>(StringComparer.Ordinal);
        if (observation.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Array)
            foreach (var window in windows.EnumerateArray())
            {
                var id = Read(window, "windowId");
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                var title = SafeText(Read(window, "title"), 96);
                var process = SafeText(Read(window, "process"), 48);
                windowsById[id!] = (title, process);
                targets[id!] = new("Window", title, new(StringComparer.Ordinal) { "activate-window" }, title, process);
            }
        if (observation.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
            foreach (var element in elements.EnumerateArray())
            {
                var id = Read(element, "elementId");
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                var allowed = element.TryGetProperty("allowedActions", out var actions) && actions.ValueKind == JsonValueKind.Array
                    ? actions.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()).Where(item => item is not null).Cast<string>().ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                var approvalActions = element.TryGetProperty("approvalRequiredActions", out var required) && required.ValueKind == JsonValueKind.Array
                    ? required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()).Where(item => item is not null).Cast<string>().ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                if (scope.TargetControlType == "Button" && scope.AllowedOperations.Contains("click", StringComparer.Ordinal) &&
                    scope.MatchesTarget(Read(element, "controlType"), Read(element, "name")) &&
                    approvalActions.Contains("click-element")) allowed.Add("click-element");
                var windowId = Read(element, "windowId");
                windowsById.TryGetValue(windowId ?? "", out var context);
                targets[id!] = new(SafeText(Read(element, "controlType"), 32), SafeText(Read(element, "name"), 96),
                    allowed, context.Title, context.Process);
            }
        return targets;
    }

    public static bool IsUsableForNamedAction(ComputerUseObservationSelection selection) =>
        selection.Status == "ok" && selection.Elements.Length == 1;

    public static ComputerUseObservationSelection Select(JsonElement observation, ComputerUseTaskScope scope,
        int maximumUnscopedControls = 24)
    {
        var sourceTruncated = !observation.TryGetProperty("truncated", out var truncatedValue) ||
            truncatedValue.ValueKind != JsonValueKind.False;
        var sourcePartial = string.Equals(Read(observation, "observationStatus"), "partial", StringComparison.Ordinal);
        var controls = scope.TargetControlType == "Window"
            ? observation.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Array
                ? windows.EnumerateArray().Take(CompanionElementLimit + 1).Select(window => JsonSerializer.SerializeToElement(new
                    {
                        elementId = Read(window, "windowId"), windowId = Read(window, "windowId"), controlType = "Window",
                        name = Read(window, "title"), process = Read(window, "process"), nameTruncated = false, isPassword = false,
                        allowedActions = window.TryGetProperty("allowedActions", out var actions) && actions.ValueKind == JsonValueKind.Array
                            ? actions.EnumerateArray().Where(action => action.ValueKind == JsonValueKind.String).Select(action => action.GetString()!).ToArray()
                            : Array.Empty<string>()
                    })).ToArray()
                : []
            : observation.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array
                ? elements.EnumerateArray().Take(CompanionElementLimit + 1).Select(element => element.Clone()).ToArray()
                : [];
        if (scope.TargetLabel is not null)
        {
            if (sourceTruncated || sourcePartial || controls.Length > CompanionElementLimit)
                return new("observation_truncated", [], [], true);
            var appWindowIds = observation.TryGetProperty("windows", out var appWindows) && appWindows.ValueKind == JsonValueKind.Array
                ? appWindows.EnumerateArray()
                    .Where(window => scope.MatchesApplicationProcess(Read(window, "process")))
                    .Select(window => Read(window, "windowId"))
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var operation = TargetUiOperation(scope);
            var requiredAction = RequiredObservedAction(operation);
            var matches = controls.Where(element => !IsTrue(element, "isPassword") && !IsTrue(element, "nameTruncated") &&
                    (!scope.RequiresApplicationLaunchBeforeTarget || appWindowIds.Contains(Read(element, "windowId") ?? "")) &&
                    scope.MatchesTarget(Read(element, "controlType"), Read(element, "name")) &&
                    (HasAction(element, requiredAction) || IsExplicitButtonApproval(element, scope, operation, requiredAction)))
                .ToArray();
            if (matches.Length == 0) return new("target_not_found", [], [], false);
            if (matches.Length != 1) return new("target_ambiguous", [], [], false);
            var windowId = Read(matches[0], "windowId");
            return new("ok", matches, windowId is null ? [] : [windowId], false);
        }

        JsonElement[] applicationWindows = observation.TryGetProperty("windows", out var allWindows) && allWindows.ValueKind == JsonValueKind.Array
            ? allWindows.EnumerateArray().Where(window => !scope.BindApplicationProcess ||
                    scope.MatchesApplicationProcess(Read(window, "process")))
                .ToArray()
            : Array.Empty<JsonElement>();
        var applicationWindowIds = applicationWindows.Select(window => Read(window, "windowId"))
            .Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var applicationControls = scope.BindApplicationProcess
            ? controls.Where(element => applicationWindowIds.Contains(Read(element, "windowId") ?? "")).ToArray()
            : controls;
        var selected = applicationControls.Take(maximumUnscopedControls).ToArray();
        var selectedWindows = applicationWindows
            .Select(window => Read(window, "windowId")).Where(id => id is not null).Cast<string>().ToArray();
        var wasTruncated = sourceTruncated || applicationControls.Length > maximumUnscopedControls;
        var status = wasTruncated ? "partial" : scope.BindApplicationProcess && applicationWindows.Length == 0
            ? "empty" : Read(observation, "observationStatus") ?? "unavailable";
        return new(status, selected, selectedWindows, wasTruncated);
    }

    public static string RequiredObservedAction(string operation) => operation switch
    {
        "activate_window" => "activate-window", "click" => "click-element", "select_tab" or "select" => "select-element",
        "expand" => "expand-element", "collapse" => "collapse-element", "type_text" => "type-text",
        "scroll" => "scroll-element", "press_key" => "press-key", _ => ""
    };

    public static string TargetUiOperation(ComputerUseTaskScope scope) => scope.AllowedOperations.FirstOrDefault(operation =>
        operation is not ("observe" or "open_application" or "open_workspace")) ?? "";

    private static bool HasAction(JsonElement element, string action) =>
        element.TryGetProperty("allowedActions", out var actions) && actions.ValueKind == JsonValueKind.Array &&
        actions.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == action);

    private static bool IsExplicitButtonApproval(JsonElement element, ComputerUseTaskScope scope,
        string operation, string action) => scope.TargetControlType == "Button" && operation == "click" &&
        action == "click-element" && Read(element, "controlType") == "Button" &&
        element.TryGetProperty("approvalRequiredActions", out var actions) && actions.ValueKind == JsonValueKind.Array &&
        actions.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == action);

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Read(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string SafeText(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= maximumLength ? value : value[..maximumLength];
}

public sealed class ComputerUseTurnState
{
    private readonly Dictionary<string, ComputerUseObservedTarget> observedTargets = new(StringComparer.Ordinal);
    private bool workspaceItemRead;
    private bool applicationLaunchCompleted;
    private int targetActionsCompleted;
    public bool RequiresObservation { get; private set; } = true;
    public bool Stopped { get; private set; }
    public bool CanAcceptModelCompletion => !Stopped;

    public bool RecordObservation(string? status, IReadOnlyDictionary<string, ComputerUseObservedTarget> targets,
        ComputerUseTaskScope scope)
    {
        var observationScope = ObservationScope(scope);
        var scopedTargets = targets.Where(pair => observationScope.MatchesObservedTarget(pair.Value) &&
                (observationScope.TargetLabel is null || pair.Value.AllowedActions.Contains(
                    ComputerUseObservationFilter.RequiredObservedAction(
                        ComputerUseObservationFilter.TargetUiOperation(observationScope)))))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (Stopped || observationScope.BindApplicationProcess && status == "partial" ||
            observationScope.TargetLabel is not null && scopedTargets.Count != 1 ||
            !ComputerUseTaskAuthorization.HasUsableObservation(status, scopedTargets.Count > 0))
        {
            Stop();
            return false;
        }
        observedTargets.Clear();
        foreach (var (id, target) in scopedTargets)
            observedTargets[id] = target with { AllowedActions = new(target.AllowedActions, StringComparer.Ordinal) };
        RequiresObservation = false;
        return true;
    }

    public bool CanAct(ComputerUseAgentAction action, ComputerUseTaskScope scope)
    {
        if (Stopped) return false;
        if (action.Operation == "observe") return scope.AllowedOperations.Contains("observe", StringComparer.Ordinal);
        if (!scope.Allows(action)) return false;
        if (scope.RequiresApplicationLaunchBeforeTarget)
        {
            var isLaunch = action.Operation is "open_application" or "open_workspace";
            if (isLaunch && applicationLaunchCompleted || !isLaunch && !applicationLaunchCompleted) return false;
            if (!isLaunch && targetActionsCompleted >= scope.MaximumTargetActions) return false;
        }
        if (action.Operation == "read_workspace_item") return !workspaceItemRead;
        if (RequiresObservation) return false;
        if (action.TargetId is null) return true;
        return observedTargets.TryGetValue(action.TargetId, out var target) &&
            target.AllowedActions.Contains(RequiredObservedAction(action.Operation));
    }

    public bool TryGetObservedTarget(string? targetId, out ComputerUseObservedTarget? target)
    {
        if (targetId is not null && observedTargets.TryGetValue(targetId, out var found))
        {
            target = found;
            return true;
        }
        target = null;
        return false;
    }

    public bool RecordAction(string? status, bool succeeded, string? verification, string? operation = null,
        bool singleTargetAction = false)
    {
        if (Stopped || !ComputerUseTaskAuthorization.IsActionVerified(status, succeeded, verification))
        {
            Stop();
            return false;
        }
        if (operation == "read_workspace_item") workspaceItemRead = true;
        if (operation is "open_application" or "open_workspace") applicationLaunchCompleted = true;
        else if (singleTargetAction) targetActionsCompleted++;
        RequiresObservation = true;
        observedTargets.Clear();
        return true;
    }

    public ComputerUseTaskScope ObservationScope(ComputerUseTaskScope scope) =>
        scope.RequiresApplicationLaunchBeforeTarget && !applicationLaunchCompleted
            ? scope with { TargetLabel = null, TargetControlType = null }
            : scope;

    public void Stop()
    {
        Stopped = true;
        RequiresObservation = true;
        observedTargets.Clear();
    }

    private static string RequiredObservedAction(string operation) => operation switch
    {
        _ => ComputerUseObservationFilter.RequiredObservedAction(operation)
    };
}

public static class ComputerUseChatDispatcher
{
    public sealed record DispatchResult<T>(bool Dispatched, string? Action, T? Value) where T : class;
    public sealed record RouteResult<T>(bool ContinueToChat, T? Result) where T : class;

    public static bool IsPracticeMode(bool startPracticeRequested, bool activeCase) => startPracticeRequested || activeCase;

    public static bool IsMiniPracticeMode(bool miniPracticeRequested, bool startMiniPracticeRequested) =>
        miniPracticeRequested || startMiniPracticeRequested;

    public static async Task<DispatchResult<T>> TryDispatchAsync<T>(string? message, bool practiceActive,
        bool miniPracticeActive, Func<string, Task<T>> executor) where T : class
    {
        if (practiceActive || miniPracticeActive)
            return new(false, null, null);
        var action = ComputerUseChatIntent.DetectDirectRequest(message);
        if (action is null)
            return new(false, null, null);
        return new(true, action, await executor(action));
    }

    public static async Task<RouteResult<T>> DispatchBeforeChatAuthorizationAsync<T>(string? message,
        bool practiceActive, bool miniPracticeActive, Func<string, Task<T>> executor,
        Func<Task<T?>> authorizeNormalChat) where T : class
    {
        var dispatch = await TryDispatchAsync(message, practiceActive, miniPracticeActive, executor);
        if (dispatch.Dispatched) return new(false, dispatch.Value);

        var authorizationFailure = await authorizeNormalChat();
        return authorizationFailure is null
            ? new(true, null)
            : new(false, authorizationFailure);
    }
}

public sealed record ComputerUseAgentAction(string Operation, string? TargetId = null,
    string? Application = null, string? Value = null, string? Direction = null)
{
    private static readonly HashSet<string> Applications = new(StringComparer.Ordinal)
        { "edge", "vscode", "workspace", "settings", "task-manager", "word", "excel", "powerpoint", "pdf-reader", "github", "notepad", "calculator", "paint" };

    public static bool TryParse(JsonElement arguments, out ComputerUseAgentAction? action)
    {
        action = null;
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var properties = arguments.EnumerateObject().ToArray();
        if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            properties.Any(property => property.Name is not ("operation" or "targetId" or "application" or "value" or "direction")) ||
            !arguments.TryGetProperty("operation", out var operationValue) || operationValue.ValueKind != JsonValueKind.String)
            return false;

        string? Read(string name)
        {
            if (!arguments.TryGetProperty(name, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : "\0invalid";
        }
        var operation = operationValue.GetString();
        var targetId = Read("targetId");
        var application = Read("application");
        var value = Read("value");
        var direction = Read("direction");
        if (new[] { targetId, application, value, direction }.Any(item => item == "\0invalid")) return false;

        var valid = operation switch
        {
            "observe" or "open_workspace" => targetId is null && application is null && value is null && direction is null,
            "open_application" => targetId is null && application is not null && Applications.Contains(application) && value is null && direction is null,
            "open_workspace_item" => targetId is null && application is null && value is { Length: > 0 and <= 512 } &&
                DesktopOperator.IsSafeWorkspaceRelativePath(value) && direction is null,
            "read_workspace_item" => targetId is null && application is null && value is { Length: > 0 and <= 512 } &&
                DesktopOperator.IsSafeWorkspaceDocumentPath(value) && direction is null,
            "activate_window" or "click" or "select_tab" or "select" or "expand" or "collapse" => ValidTarget(targetId) && application is null && value is null && direction is null,
            "type_text" => ValidTarget(targetId) && application is null && value is { Length: > 0 and <= 512 } &&
                !value.Contains('\r') && !value.Contains('\n') && !value.Contains('\0') && direction is null,
            "scroll" => ValidTarget(targetId) && application is null && value is null && direction is ("up" or "down"),
            "press_key" => ValidTarget(targetId) && application is null && value is ("ArrowLeft" or "ArrowRight" or "Home" or "End") && direction is null,
            _ => false
        };
        if (!valid) return false;
        action = new(operation!, targetId, application, value, direction);
        return true;
    }

    private static bool ValidTarget(string? targetId) => targetId is not null && Guid.TryParseExact(targetId, "N", out _);
}
