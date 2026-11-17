public static class ComputerUsePolicy
{
    private static readonly HashSet<string> ObservationTargets = new(StringComparer.Ordinal)
    {
        "approved-applications",
        "approved-window-titles",
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

    // Editing files, running project commands, and writing to Git remain deliberately unavailable.
    public static bool CanWrite(string? action) => false;

    public static object Describe() => new
    {
        observe = new[] { "approved-applications", "approved-window-titles", "github-account", "github-pull-requests" },
        navigate = new[] { "open-workspace", "open-github", "open-edge", "open-vscode" },
        write = Array.Empty<string>(),
        codeExecution = false
    };
}
