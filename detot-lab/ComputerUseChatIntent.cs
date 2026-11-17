using System.Text.RegularExpressions;

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
        ("read-github-account", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|check|read|inspect)\s+(?:tài\s+khoản\s+github(?:\s+(?:hiện\s+tại|đang\s+dùng))?|current\s+github\s+account|github\s+account)" + Suffix, Options)),
        ("read-github-pull-requests", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|check|read|inspect)\s+(?:(?:danh\s+sách|list)\s+)?(?:pr(?:s)?|pull\s+requests?)(?:\s+(?:và|and|with)\s+(?:ci|checks?))?" + Suffix, Options)),
        ("read-approved-applications", new(Prefix + @"(?:kiểm\s+tra|xem|đọc|liệt\s+kê|check|read|list|inspect)\s+(?:các\s+)?(?:ứng\s+dụng|apps?|applications?)(?:\s+(?:đang\s+mở|đang\s+chạy|open|currently\s+open|running))" + Suffix, Options)),
    ];

    public static string? DetectDirectRequest(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 160 || message.Contains('\n') || message.Contains('\r') ||
            message.Contains('`') || message.Contains('"') || message.Contains('\'')) return null;
        var text = message.Trim();
        return Patterns.FirstOrDefault(item => item.Pattern.IsMatch(text)).Action;
    }

    public static string? ObservationTargetForAction(string action) => action switch
    {
        "read-approved-applications" => "approved-applications",
        "read-github-account" => "github-account",
        "read-github-pull-requests" => "github-pull-requests",
        _ => null
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
