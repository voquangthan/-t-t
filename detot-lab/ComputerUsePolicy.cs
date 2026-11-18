public static class ComputerUsePolicy
{
    private static readonly HashSet<string> ObservationTargets = new(StringComparer.Ordinal)
    {
        "approved-applications",
        "desktop-ui",
        "github-account",
        "github-pull-requests"
    };

    private static readonly HashSet<string> NavigationActions = new(StringComparer.Ordinal)
    {
        "open-workspace",
        "open-workspace-item",
        "open-github",
        "open-edge",
        "open-vscode",
        "open-settings",
        "open-task-manager",
        "open-word",
        "open-excel",
        "open-powerpoint",
        "open-pdf-reader"
    };

    private static readonly HashSet<string> UiActions = new(StringComparer.Ordinal)
    {
        "activate-window", "click-element", "double-click-element", "select-element", "type-text", "scroll-element", "press-key"
    };

    public static bool CanObserve(string? target) => target is not null && ObservationTargets.Contains(target);

    public static bool CanNavigate(string? action) => action is not null && NavigationActions.Contains(action);

    public static bool CanInteract(string? action) => action is not null && UiActions.Contains(action);

    public static bool IsReadOnlyDesktopApplication(string? processName) => processName is not null &&
        (processName.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase));

    public static bool CanInteractWithApplication(string? processName, string? action) =>
        !IsReadOnlyDesktopApplication(processName) || action == "activate-window";

    public static string[] AllowedUiActions(string controlType, bool enabled, bool offscreen, bool isPassword,
        bool supportsInvoke, bool supportsSelection, bool supportsValue, bool valueReadOnly, bool supportsScroll)
    {
        if (!enabled || offscreen) return [];
        var actions = new List<string>();
        if (controlType == "Hyperlink" && supportsInvoke) actions.Add("click-element");
        if ((controlType is "TabItem" or "TreeItem" or "ListItem") && supportsSelection) actions.Add("select-element");
        if (!isPassword && controlType == "Edit" && supportsValue && !valueReadOnly) actions.Add("type-text");
        if (supportsScroll) actions.Add("scroll-element");
        actions.Add("press-key");
        return actions.ToArray();
    }

    public static bool RequiresUiApproval(string controlType, string action) =>
        (action is "click-element" or "double-click-element") && (controlType switch
        {
            "Button" or "MenuItem" or "CheckBox" or "RadioButton" when action == "click-element" => true,
            "ListItem" or "TreeItem" when action == "double-click-element" => true,
            _ => false
        });

    // GCM's current chooser is not bound to a trusted pending Git operation, repository, or host.
    // Keep account selection denied until such transaction context can be verified end to end.
    public static bool CanSelectGitHubAccount(string? action) => false;

    // Editing files, running project commands, and writing to Git remain deliberately unavailable.
    public static bool CanWrite(string? action) => false;

    public static object Describe() => new
    {
        observe = new[] { "approved-applications", "desktop-ui", "github-account", "github-pull-requests" },
        navigate = new[] { "open-workspace", "open-workspace-item (existing items under workspace only)", "open-github", "open-edge", "open-vscode", "open-settings", "open-task-manager", "open-word", "open-excel", "open-powerpoint", "open-pdf-reader" },
        interact = new[] { "activate-window", "select-element", "type-text", "scroll-element", "press-key (navigation keys only)", "click-element (Hyperlink/TabItem only)" },
        readOnlyApplications = new[] { "Windows Settings", "Task Manager" },
        approvalRequired = new[] { "click-element on Button/MenuItem/CheckBox/RadioButton", "double-click-element on ListItem/TreeItem", "keyboard-submit" },
        interactionRequirements = new[] { "fresh-observation-target-id", "same-interactive-session", "revalidate-target-before-action" },
        accountSelection = Array.Empty<string>(),
        write = Array.Empty<string>(),
        codeExecution = false
    };
}
