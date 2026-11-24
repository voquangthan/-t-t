public static class ComputerUsePolicy
{
    public const string ModelRoute = "deepseek/deepseek-flash";
    public const string ModelConsentDestination = "DeepSeek via OpenClaw Gateway";
    public const string DefaultChatModelRoute = "openclaw/default";

    public static string SelectModelRoute(bool computerUseRequest, bool modelConsentAllowed) =>
        computerUseRequest && modelConsentAllowed ? ModelRoute : DefaultChatModelRoute;

    public static bool ShouldExposeComputerUsePlannerTool(bool normalChat, bool hasOtherTools,
        bool desktopAvailable, bool modelConsentAllowed, ComputerUseIntentScope? requestScope,
        bool isActionRequest) =>
        normalChat && !hasOtherTools && desktopAvailable && modelConsentAllowed && isActionRequest &&
        requestScope is { HasInteractiveUiScope: true };

    private static readonly HashSet<string> ObservationTargets = new(StringComparer.Ordinal)
    {
        "approved-applications",
        "github-account",
        "github-pull-requests"
    };

    private static readonly HashSet<string> NavigationActions = new(StringComparer.Ordinal)
    {
        "open-workspace", "open-notepad",
        "open-github",
        "open-edge",
        "open-vscode"
    };

    public static bool CanObserve(string? target) => target is not null && ObservationTargets.Contains(target);

    public static string IsolatedGatewaySessionKey(Guid? requestId)
    {
        var sessionId = requestId is { } id && id != Guid.Empty ? id : Guid.NewGuid();
        return $"agent:main:detot-computer-use-{sessionId:N}";
    }

    public static bool CanNavigate(string? action)
    {
        if (action is null) return false;
        if (NavigationActions.Contains(action)) return true;
        const string launchPrefix = "open-installed:";
        return action.StartsWith(launchPrefix, StringComparison.Ordinal) &&
            ComputerUseUiPlanner.IsSafeInstalledApplicationName(action[launchPrefix.Length..]);
    }

    public static bool CanUseUiOperation(string? app, string? operation) =>
        app is not null && ComputerUseUiPlanner.AllowedApps.Contains(app, StringComparer.Ordinal) &&
        operation is not null && ComputerUseUiPlanner.AllowedOperations.Contains(operation, StringComparer.Ordinal);

    public static bool ShouldExposeInteractiveUiTool(bool normalChat, bool hasOtherTools, bool desktopAvailable,
        bool uiDataConsent, ComputerUseIntentScope? requestScope) =>
        normalChat && !hasOtherTools && desktopAvailable && uiDataConsent &&
        requestScope is { HasInteractiveUiScope: true };

    public static bool TryAuthorizeInteractiveUiAction(ComputerUseIntentScope? scope,
        ComputerUseUiAction action, string? observedElementName = null, string? observedTarget = null,
        Action? onAuthorized = null)
    {
        if (scope is null || !scope.Allows(action, observedElementName, observedTarget)) return false;
        onAuthorized?.Invoke();
        return true;
    }

    public static bool TryAuthorizeModelAssistedUiAction(bool gatewayDataConsentAllowed,
        ComputerUseIntentScope? scope, ComputerUseUiAction action, string? observedElementName = null,
        string? observedTarget = null, Action? onAuthorized = null) =>
        gatewayDataConsentAllowed && TryAuthorizeInteractiveUiAction(scope, action,
            observedElementName, observedTarget, onAuthorized);

    // GCM's current chooser is not bound to a trusted pending Git operation, repository, or host.
    // Keep account selection denied until such transaction context can be verified end to end.
    public static bool CanSelectGitHubAccount(string? action) => false;

    // Editing files, running project commands, and writing to Git remain deliberately unavailable.
    public static bool CanWrite(string? action) => false;

    public static object Describe() => new
    {
        observe = new[] { "approved-applications", "github-account", "github-pull-requests" },
        navigate = new[] { "open-workspace", "open-installed:<safe app name>", "open-github", "open-edge", "open-vscode" },
        accountSelection = Array.Empty<string>(),
        ui = new
        {
            apps = ComputerUseUiPlanner.AllowedApps,
            operations = ComputerUseUiPlanner.AllowedOperations,
            restrictions = new[] { "navigation_only_invoke", "approved_text_fields_only", "exact_user_text_only", "no_sensitive_text", "no_submit", "no_terminal", "no_credentials", "confirm_risky_actions" }
        },
        write = Array.Empty<string>(),
        codeExecution = false
    };
}
