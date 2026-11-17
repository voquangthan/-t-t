#if WINDOWS
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
#endif

public static class WindowsDesktopAutomation
{
    public static bool HasVisibleWindow(string processName)
    {
#if !WINDOWS
        return false;
#else
        if (!OperatingSystem.IsWindows() || processName is not ("Code" or "msedge" or "explorer")) return false;
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
