#if WINDOWS
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
#endif

public static class WindowsDesktopAutomation
{
    private static readonly IReadOnlyDictionary<string, string> ApprovedApplications =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code"] = "Visual Studio Code",
            ["msedge"] = "Microsoft Edge",
            ["explorer"] = "File Explorer",
            ["devenv"] = "Visual Studio",
            ["notepad"] = "Notepad",
            ["WindowsTerminal"] = "Windows Terminal"
        };

    private static readonly IReadOnlyDictionary<string, string> WindowTitleApplications =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code"] = "Visual Studio Code",
            ["msedge"] = "Microsoft Edge",
            ["explorer"] = "File Explorer",
            ["devenv"] = "Visual Studio",
            ["notepad"] = "Notepad"
        };

    public sealed record WindowTitle(string Application, string Title);

    public static IReadOnlyList<string> ReadOpenApprovedApplications()
    {
#if !WINDOWS
        return [];
#else
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var automation = new UIA3Automation();
            var desktop = automation.GetDesktop();
            var openApplications = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (processName, displayName) in ApprovedApplications)
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        try
                        {
                            var hasWindow = desktop.FindAllChildren(cf =>
                                cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id))).Length > 0;
                            if (hasWindow) openApplications.Add(displayName);
                        }
                        catch (Exception)
                        {
                            // Ignore processes that exit while UI Automation is inspecting the desktop.
                        }
                    }
                }
            }
            return openApplications.ToArray();
        }
        catch (Exception)
        {
            return [];
        }
#endif
    }

    public static IReadOnlyList<WindowTitle> ReadOpenApprovedWindowTitles()
    {
#if !WINDOWS
        return [];
#else
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var automation = new UIA3Automation();
            var desktop = automation.GetDesktop();
            var candidates = new List<(string ProcessName, string Title)>();
            foreach (var processName in WindowTitleApplications.Keys)
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        try
                        {
                            var windows = desktop.FindAllChildren(cf =>
                                cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id)));
                            foreach (var window in windows)
                                candidates.Add((processName, window.Properties.Name.Value));
                        }
                        catch (Exception)
                        {
                            // Ignore processes or UI Automation providers that disappear during inspection.
                        }
                    }
                }
            }
            return FilterApprovedWindowTitles(candidates);
        }
        catch (Exception)
        {
            return [];
        }
#endif
    }

    public static IReadOnlyList<WindowTitle> FilterApprovedWindowTitles(
        IEnumerable<(string ProcessName, string Title)> candidates)
    {
        var results = new List<WindowTitle>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (processName, rawTitle) in candidates)
        {
            if (!WindowTitleApplications.TryGetValue(processName, out var application) ||
                string.IsNullOrWhiteSpace(rawTitle)) continue;

            var title = new string(rawTitle.Where(character => !char.IsControl(character)).ToArray()).Trim();
            if (title.Length == 0) continue;
            if (title.Length > 160) title = title[..160];
            if (seen.Add(application + "\0" + title)) results.Add(new(application, title));
        }
        return results;
    }

    public static bool HasVisibleWindow(string processName)
    {
#if !WINDOWS
        return false;
#else
        if (!OperatingSystem.IsWindows() || !ApprovedApplications.ContainsKey(processName)) return false;
        try
        {
            using var automation = new UIA3Automation();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var windows = automation.GetDesktop().FindAllChildren(cf =>
                            cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id)));
                        if (windows.Any(window => !string.IsNullOrWhiteSpace(window.Properties.Name.Value))) return true;
                    }
                    catch (Exception)
                    {
                        // A process can exit between enumeration and UIA lookup; try the next match.
                    }
                }
            }
        }
        catch (Exception)
        {
            // UIA providers can fail independently; never let observation crash the backend.
            return false;
        }
        return false;
#endif
    }
}
