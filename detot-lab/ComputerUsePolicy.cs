public static class ComputerUsePolicy
{
    private static readonly System.Text.RegularExpressions.Regex GitHubLogin = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly HashSet<string> ObservationTargets = new(StringComparer.Ordinal)
    {
        "approved-applications",
        "github-account",
        "github-pull-requests"
    };

    private static readonly HashSet<string> NavigationActions = new(StringComparer.Ordinal)
    {
        "open-workspace",
        "open-github",
        "open-edge",
        "open-vscode"
    };

    public static bool CanObserve(string? target) => target is not null && ObservationTargets.Contains(target);

    public static bool CanNavigate(string? action) => action is not null && NavigationActions.Contains(action);

    public static bool CanSelectGitHubAccount(string? action)
    {
        const string prefix = "select-github-account:";
        if (action is null || !action.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var target = action[prefix.Length..];
        return target == "bottom" || GitHubLogin.IsMatch(target);
    }

    // Editing files, running project commands, and writing to Git remain deliberately unavailable.
    public static bool CanWrite(string? action) => false;

    public static object Describe() => new
    {
        observe = new[] { "approved-applications", "github-account", "github-pull-requests" },
        navigate = new[] { "open-workspace", "open-github", "open-edge", "open-vscode" },
        accountSelection = new[] { "explicitly-requested-listed-github-account" },
        write = Array.Empty<string>(),
        codeExecution = false
    };
}
