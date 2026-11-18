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
        ("open-workspace", new(Prefix + @"(?:mở|open)\s+(?:project\s+)?workspace" + Suffix, Options)),
        ("open-github", new(Prefix + @"(?:mở|open)\s+(?:(?:repository|repo)\s+)?github" + Suffix, Options)),
        ("open-settings", new(Prefix + @"(?:mở|open|launch)\s+(?:windows\s+)?settings(?:\s+(?:windows|của\s+windows))?" + Suffix, Options)),
        ("open-task-manager", new(Prefix + @"(?:mở|open|launch)\s+(?:windows\s+)?task\s+manager" + Suffix, Options)),
        ("open-word", new(Prefix + @"(?:mở|open|launch)\s+(?:microsoft\s+)?word" + Suffix, Options)),
        ("open-excel", new(Prefix + @"(?:mở|open|launch)\s+(?:microsoft\s+)?excel" + Suffix, Options)),
        ("open-powerpoint", new(Prefix + @"(?:mở|open|launch)\s+(?:microsoft\s+)?powerpoint" + Suffix, Options)),
        ("open-pdf-reader", new(Prefix + @"(?:mở|open|launch)\s+(?:pdf\s+reader|acrobat(?:\s+reader)?)" + Suffix, Options)),
        ("read-github-account", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|check|read|inspect)\s+(?:tài\s+khoản\s+github(?:\s+(?:hiện\s+tại|đang\s+dùng))?|current\s+github\s+account|github\s+account)" + Suffix, Options)),
        ("read-github-pull-requests", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|check|read|inspect)\s+(?:(?:danh\s+sách|list)\s+)?(?:pr(?:s)?|pull\s+requests?)(?:\s+(?:và|and|with)\s+(?:ci|checks?))?" + Suffix, Options)),
        ("read-approved-applications", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|liệt\s+kê|check|read|list|inspect)\s+(?:các\s+)?(?:ứng\s+dụng|apps?|applications?)(?:\s+(?:đang\s+mở|đang\s+chạy|open|currently\s+open|running))" + Suffix, Options)),
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
        "read-github-account" => "github-account",
        "read-github-pull-requests" => "github-pull-requests",
        _ => null
    };

}

public sealed record ComputerUseTaskScope(string Intent, string[] AllowedOperations, string? Application = null,
    string? ObservationTarget = null, string? RequestedValue = null)
{
    public bool Allows(ComputerUseAgentAction action)
    {
        if (!AllowedOperations.Contains(action.Operation, StringComparer.Ordinal)) return false;
        if (action.Operation == "open_application" && action.Application != Application) return false;
        if (action.Operation != "open_application" && action.Application is not null) return false;
        if (action.Operation == "open_workspace_item" && action.Value != RequestedValue) return false;
        if (action.Operation != "open_workspace_item" && RequestedValue is not null && action.Operation != "observe") return false;
        return true;
    }
}

public static class ComputerUseTaskAuthorization
{
    private static readonly Regex WorkspaceItemRequest = new(
        @"^\s*(?:(?:đệ\s+tốt|mày|bạn)\s*,?\s*)?(?:hãy\s+)?(?:mở|open)\s+(?:tệp|file|thư\s+mục|folder)\s+(?<path>[^\r\n""'`<>]{1,512}?)\s*[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool ShouldExposeTool(ComputerUseTaskScope? scope, bool interactionConsent,
        bool observationConsent) => scope is not null && interactionConsent && observationConsent;

    public static bool ShouldDispatchToolCall(ComputerUseTaskScope? scope, string? toolName) =>
        scope is not null && toolName == "detot_computer_use";

    public static ComputerUseTaskScope? FromCurrentMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 640 || message.Contains('\n') || message.Contains('\r') ||
            message.Contains('`') || message.Contains('"') || message.Contains('\'')) return null;
        var itemMatch = WorkspaceItemRequest.Match(message);
        if (itemMatch.Success)
        {
            var path = itemMatch.Groups["path"].Value.Trim();
            if (path.Length is > 0 and <= 512 && DesktopOperator.IsSafeWorkspaceRelativePath(path))
                return new("open-workspace-item", ["observe", "open_workspace_item"], RequestedValue: path);
            return null;
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
            "open-pdf-reader" => "pdf-reader", _ => null
        };
        if (application is null) return null;
        string[] operations = intent == "open-workspace" ? ["observe", "open_workspace"] : ["observe", "open_application"];
        return new(intent, operations, application);
    }

    public static bool HasUsableObservation(string? status, bool hasTarget) => status switch
    {
        "available" or "partial" => hasTarget,
        "empty" => true,
        _ => false
    };

    public static bool IsActionVerified(string? status, bool succeeded, string? verification) =>
        status != "approval_required" && succeeded && verification == "verified";
}

public sealed class ComputerUseTurnState
{
    private readonly Dictionary<string, HashSet<string>> observedTargets = new(StringComparer.Ordinal);
    public bool RequiresObservation { get; private set; } = true;
    public bool Stopped { get; private set; }
    public bool CanAcceptModelCompletion => !Stopped;

    public bool RecordObservation(string? status, IReadOnlyDictionary<string, HashSet<string>> targets)
    {
        if (Stopped || !ComputerUseTaskAuthorization.HasUsableObservation(status, targets.Count > 0))
        {
            Stop();
            return false;
        }
        observedTargets.Clear();
        foreach (var (id, actions) in targets) observedTargets[id] = new(actions, StringComparer.Ordinal);
        RequiresObservation = false;
        return true;
    }

    public bool CanAct(ComputerUseAgentAction action)
    {
        if (Stopped || action.Operation == "observe") return !Stopped;
        if (RequiresObservation) return false;
        if (action.TargetId is null) return true;
        return observedTargets.TryGetValue(action.TargetId, out var actions) &&
            actions.Contains(RequiredObservedAction(action.Operation));
    }

    public bool RecordAction(string? status, bool succeeded, string? verification)
    {
        if (Stopped || !ComputerUseTaskAuthorization.IsActionVerified(status, succeeded, verification))
        {
            Stop();
            return false;
        }
        RequiresObservation = true;
        observedTargets.Clear();
        return true;
    }

    public void Stop()
    {
        Stopped = true;
        RequiresObservation = true;
        observedTargets.Clear();
    }

    private static string RequiredObservedAction(string operation) => operation switch
    {
        "activate_window" => "activate-window", "click" => "click-element", "select_tab" or "select" => "select-element",
        "expand" => "expand-element", "collapse" => "collapse-element", "type_text" => "type-text",
        "scroll" => "scroll-element", "press_key" => "press-key", _ => ""
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
        { "edge", "vscode", "workspace", "settings", "task-manager", "word", "excel", "powerpoint", "pdf-reader", "github" };

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
