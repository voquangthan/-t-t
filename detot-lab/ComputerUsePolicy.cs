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
        "activate-window", "click-element", "double-click-element", "select-element", "type-text", "scroll-element", "press-key",
        "expand-element", "collapse-element"
    };

    public static bool CanObserve(string? target) => target is not null && ObservationTargets.Contains(target);

    public static bool CanNavigate(string? action) => action is not null && NavigationActions.Contains(action);

    public static bool CanInteract(string? action) => action is not null && UiActions.Contains(action);

    public static bool IsReadOnlyDesktopApplication(string? processName) => processName is not null &&
        (processName.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase));

    public static bool CanInteractWithApplication(string? processName, string? action) =>
        !IsReadOnlyDesktopApplication(processName) || action == "activate-window";

    public static bool CanPressKeyOn(string? controlType, string? key) => controlType == "Edit" && key is
        ("ArrowLeft" or "ArrowRight" or "Home" or "End");

    public static void PressAndRelease<T>(T key, Action<T> press, Action<T> release)
    {
        try { press(key); }
        finally { release(key); }
    }

    public static string VerificationStatus(bool postconditionObserved) =>
        postconditionObserved ? "verified" : "reobserve_required";

    public static string TrySetProtectedTextValue(bool isEdit, Func<bool> supportsValue,
        Func<(bool Supported, bool Value)> readPassword, Func<(bool Supported, bool Value)> readOnly,
        Action<string> setValue, string value)
    {
        if (!isEdit) return "not_editable";
        try
        {
            if (!supportsValue()) return "value_pattern_unavailable";
            var password = readPassword();
            if (!password.Supported) return "target_security_state_unavailable";
            if (password.Value) return "password_input_denied";
            var readOnlyState = readOnly();
            if (!readOnlyState.Supported) return "target_security_state_unavailable";
            if (readOnlyState.Value) return "target_read_only";
        }
        catch { return "target_security_state_unavailable"; }
        setValue(value);
        return "set";
    }

    public static string[] AllowedUiActions(string controlType, bool enabled, bool offscreen, bool isPassword,
        bool supportsInvoke, bool supportsSelection, bool supportsValue, bool valueReadOnly, bool supportsScroll,
        bool supportsExpandCollapse = false, string? expandCollapseState = null)
    {
        if (!enabled || offscreen) return [];
        var actions = new List<string>();
        if (controlType == "Hyperlink" && supportsInvoke) actions.Add("click-element");
        if (controlType == "TabItem" && supportsSelection) actions.Add("select-element");
        if (!isPassword && controlType == "Edit" && supportsValue && !valueReadOnly) actions.Add("type-text");
        if (supportsScroll) actions.Add("scroll-element");
        if (controlType == "Edit") actions.Add("press-key");
        if (supportsExpandCollapse && (controlType is "MenuItem" or "ComboBox" or "TreeItem"))
        {
            if (expandCollapseState == "Collapsed") actions.Add("expand-element");
            else if (expandCollapseState == "Expanded") actions.Add("collapse-element");
        }
        return actions.ToArray();
    }

    public static IReadOnlyList<BoundedNode<T>> CollectBoundedDescendants<T>(T root, int limit, int depthLimit,
        Func<T, int, T?> childAt, out bool truncated) where T : class
    {
        truncated = false;
        var result = new List<BoundedNode<T>>();
        var queue = new Queue<(T Parent, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (parent, depth) = queue.Dequeue();
            if (depth >= depthLimit)
            {
                try { truncated |= childAt(parent, 0) is not null; }
                catch { truncated = true; }
                continue;
            }
            var budget = limit - result.Count;
            if (budget <= 0) { truncated = true; break; }
            var exhausted = false;
            for (var index = 0; index < budget; index++)
            {
                T? child;
                try { child = childAt(parent, index); }
                catch { truncated = true; exhausted = true; break; }
                if (child is null) { exhausted = true; break; }
                result.Add(new(child, parent, depth + 1));
                queue.Enqueue((child, depth + 1));
            }
            if (!exhausted && result.Count < limit)
            {
                try { truncated |= childAt(parent, budget) is not null; }
                catch { truncated = true; }
            }
            if (result.Count >= limit)
            {
                truncated = true;
                break;
            }
        }
        if (queue.Count > 0) truncated = true;
        return result;
    }

    public sealed record BoundedNode<T>(T Element, T Parent, int Depth) where T : class;

    public static bool RequiresUiApproval(string controlType, string action, string? key = null) =>
        action == "press-key" ? !CanPressKeyOn(controlType, key) :
        (action is "click-element" or "double-click-element" or "select-element") && (controlType switch
        {
            "Button" or "MenuItem" or "CheckBox" or "RadioButton" when action == "click-element" => true,
            "ListItem" or "TreeItem" when action is "select-element" or "double-click-element" => true,
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
        interact = new[] { "activate-window", "select-element (TabItem only)", "type-text (revalidated non-password Edit only)", "scroll-element", "press-key (ArrowLeft/ArrowRight/Home/End on Edit only)", "click-element (Hyperlink only)", "expand/collapse-element (MenuItem/ComboBox/TreeItem navigation only)" },
        readOnlyApplications = new[] { "Windows Settings", "Task Manager" },
        approvalRequired = new[] { "click-element on Button/MenuItem/CheckBox/RadioButton", "select/double-click ListItem/TreeItem", "keys on RadioButton/ComboBox/selection controls", "keyboard-submit" },
        interactionRequirements = new[] { "fresh-observation-target-id", "same-interactive-session", "revalidate-target-before-action" },
        accountSelection = Array.Empty<string>(),
        write = Array.Empty<string>(),
        codeExecution = false
    };
}
