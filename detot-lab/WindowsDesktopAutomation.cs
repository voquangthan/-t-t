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
