public static class ComputerUsePolicy
{
    private static readonly System.Text.RegularExpressions.Regex CredentialAssignment = new(
        @"(?i)(\b(?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|private[_-]?key|password|authorization|credential)\b[\""']?\s*[:=]\s*[\""']?)(?:(?:Bearer|Basic)\s+)?[^\s,;\""'}]+",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex CredentialXmlElement = new(
        @"(?is)<(?<tag>[A-Za-z0-9_.:-]*(?:api[_-]?key|client[_-]?secret|access[_-]?token|refresh[_-]?token|private[_-]?key|password|authorization|credential)[A-Za-z0-9_.:-]*)\b[^>]*>.*?</\k<tag>\s*>",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex RecognizableToken = new(
        @"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|AIza[0-9A-Za-z_-]{30,})\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ObservationTargets = new(StringComparer.Ordinal)
    {
        "approved-applications",
        "desktop-ui",
        "workspace-explorer",
        "windows-settings",
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
        "open-pdf-reader",
        "open-notepad",
        "open-calculator",
        "open-paint"
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

    public static bool TryInvokeApprovedButton(bool isButton, bool supportsInvoke, Action invoke, out string status)
    {
        if (!isButton)
        {
            status = "target_stale";
            return false;
        }
        if (!supportsInvoke)
        {
            status = "invoke_pattern_unavailable";
            return false;
        }
        try { invoke(); }
        catch
        {
            status = "action_failed";
            return false;
        }
        status = "click_sent_unverified";
        return true;
    }

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
        bool supportsExpandCollapse = false, string? expandCollapseState = null,
        bool safeSelectionContainer = false)
    {
        if (!enabled || offscreen) return [];
        var actions = new List<string>();
        if (controlType == "Hyperlink" && supportsInvoke) actions.Add("click-element");
        if (supportsSelection && safeSelectionContainer && (controlType is "TabItem" or "ListItem" or "TreeItem"))
            actions.Add("select-element");
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

    public static bool IsSafeSelectionContext(string controlType, string? selectionContainerControlType,
        bool insideComboBox) => !insideComboBox && ((controlType, selectionContainerControlType) switch
        {
            ("TabItem", "Tab") => true,
            ("ListItem", "List") => true,
            ("TreeItem", "Tree") => true,
            _ => false
        });

    public static bool IsUnsafeSelectionAncestor(string? controlType) =>
        controlType is null or "Unknown" or "ComboBox";

    public static bool CanSelectInContext(string controlType, string action, bool safeSelectionContainer) =>
        action != "select-element" ||
        controlType is ("TabItem" or "ListItem" or "TreeItem") && safeSelectionContainer;

    public static bool MatchesExpectedUiTarget(string? actualControlType, string? actualName,
        string? expectedControlType, string? expectedName) =>
        !string.IsNullOrWhiteSpace(actualName) &&
        actualControlType == expectedControlType &&
        !string.IsNullOrWhiteSpace(expectedName) &&
        string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase);

    public static bool TryRunExpectedUiAction<T>(string? expectedControlType, string? expectedName,
        Func<(bool Readable, string? ControlType, string? Name)> readLiveTarget,
        Func<T> action, out T? result, out string status) where T : class
    {
        result = null;
        if ((expectedControlType is null) != (expectedName is null))
        {
            status = "target_stale";
            return false;
        }

        if (expectedControlType is not null)
        {
            (bool Readable, string? ControlType, string? Name) live;
            try { live = readLiveTarget(); }
            catch
            {
                status = "target_state_unavailable";
                return false;
            }
            if (!live.Readable)
            {
                status = "target_state_unavailable";
                return false;
            }
            if (!MatchesExpectedUiTarget(live.ControlType, live.Name, expectedControlType, expectedName))
            {
                status = "target_stale";
                return false;
            }
        }

        result = action();
        status = "performed";
        return true;
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

    public static bool RequiresUiApproval(string controlType, string action, string? key = null,
        bool safeSelectionContainer = false) =>
        action == "press-key" ? !CanPressKeyOn(controlType, key) :
        action == "select-element" ? !CanSelectInContext(controlType, action, safeSelectionContainer) :
        (action is "click-element" or "double-click-element") && (controlType switch
        {
            "Button" or "MenuItem" or "CheckBox" or "RadioButton" when action == "click-element" => true,
            "ListItem" or "TreeItem" when action == "double-click-element" => true,
            _ => false
        });

    // GCM's current chooser is not bound to a trusted pending Git operation, repository, or host.
    // Keep account selection denied until such transaction context can be verified end to end.
    public static bool CanSelectGitHubAccount(string? action) => false;

    public static string RedactWorkspaceExcerpt(string text)
    {
        if (text.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase))
            return "[redacted: private key material detected]";
        var redacted = CredentialXmlElement.Replace(text, "<sensitive-field>[REDACTED]</sensitive-field>");
        redacted = CredentialAssignment.Replace(redacted, "$1[REDACTED]");
        return RecognizableToken.Replace(redacted, "[REDACTED]");
    }

    // Editing files, running project commands, and writing to Git remain deliberately unavailable.
    public static bool CanWrite(string? action) => false;

    public static object Describe() => new
    {
        observe = new[] { "approved-applications", "desktop-ui", "workspace-explorer", "windows-settings", "github-account", "github-pull-requests" },
        navigate = new[] { "open-workspace", "open-workspace-item (existing items under workspace only)", "open-github", "open-edge", "open-vscode", "open-settings", "open-task-manager", "open-word", "open-excel", "open-powerpoint", "open-pdf-reader", "open-notepad", "open-calculator", "open-paint" },
        interact = new[] { "activate-window", "select-element (TabItem/ListItem/TreeItem only)", "type-text (revalidated non-password Edit only)", "scroll-element", "press-key (ArrowLeft/ArrowRight/Home/End on Edit only)", "click-element (Hyperlink only)", "expand/collapse-element (MenuItem/ComboBox/TreeItem navigation only)" },
        readOnlyApplications = new[] { "Windows Settings", "Task Manager" },
        approvalRequired = new[] { "click-element on Button/MenuItem/CheckBox/RadioButton", "select on CheckBox/RadioButton/ComboBox or unknown control types", "double-click ListItem/TreeItem", "keys on RadioButton/ComboBox/selection controls", "keyboard-submit" },
        interactionRequirements = new[] { "fresh-observation-target-id", "same-interactive-session", "revalidate-target-before-action" },
        accountSelection = Array.Empty<string>(),
        write = Array.Empty<string>(),
        codeExecution = false
    };
}
