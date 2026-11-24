public sealed record StartMenuShortcutMatch(string DisplayName, string ShortcutPath);

public sealed record StartMenuShortcutSearchResult(
    IReadOnlyList<StartMenuShortcutMatch> Matches,
    bool Complete);

/// <summary>Resolves only exact Start Menu app names and trusted executable locations.</summary>
public static class InstalledApplicationShortcutResolver
{
    private const int MaximumShortcutsToInspect = 5000;

    public static StartMenuShortcutSearchResult FindExact(IEnumerable<string> startMenuRoots, string appName)
    {
        if (!ComputerUseUiPlanner.IsSafeInstalledApplicationName(appName)) return new([], true);

        var matches = new List<StartMenuShortcutMatch>();
        var inspected = 0;
        foreach (var root in startMenuRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerator<string>? files = null;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).GetEnumerator();
                while (files.MoveNext())
                {
                    if (++inspected > MaximumShortcutsToInspect) return new(matches, false);
                    var path = files.Current;
                    if (string.Equals(Path.GetFileNameWithoutExtension(path), appName, StringComparison.OrdinalIgnoreCase))
                        matches.Add(new StartMenuShortcutMatch(Path.GetFileNameWithoutExtension(path), path));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return new(matches, false);
            }
            finally
            {
                files?.Dispose();
            }
        }

        return new(matches
            .GroupBy(match => Path.GetFullPath(match.ShortcutPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray(), true);
    }

    public static bool IsTrustedExecutableTarget(string? targetPath, IEnumerable<string> trustedInstallRoots)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathFullyQualified(targetPath) ||
            !string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !ComputerUseUiPlanner.CanObserveDesktopProcess(Path.GetFileNameWithoutExtension(targetPath))) return false;

        string fullPath;
        try { fullPath = Path.GetFullPath(targetPath); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }

        foreach (var root in trustedInstallRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            string fullRoot;
            try { fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { continue; }
            if (fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
