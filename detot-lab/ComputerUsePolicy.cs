public static class ComputerUsePolicy
{
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

    // GCM's current chooser is not bound to a trusted pending Git operation, repository, or host.
    // Keep account selection denied until such transaction context can be verified end to end.
    public static bool CanSelectGitHubAccount(string? action) => false;

    // Editing files, running project commands, and writing to Git remain deliberately unavailable.
    public static bool CanWrite(string? action) => false;

    public static object Describe() => new
    {
        observe = new[] { "approved-applications", "github-account", "github-pull-requests" },
        navigate = new[] { "open-workspace", "open-github", "open-edge", "open-vscode" },
        accountSelection = Array.Empty<string>(),
        write = Array.Empty<string>(),
        codeExecution = false
    };
}
