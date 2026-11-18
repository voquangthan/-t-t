#if WINDOWS
using FlaUI.Core;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
#endif

public static class WindowsDesktopAutomation
{
    public sealed record ApplicationObservation(string Status, IReadOnlyList<string> Applications);
    public sealed record ApplicationObservationMessage(string Status, string? Reply, string? Error);

    private static readonly IReadOnlyDictionary<string, string> ApprovedApplications =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code"] = "Visual Studio Code",
            ["msedge"] = "Microsoft Edge",
            ["explorer"] = "File Explorer",
            ["devenv"] = "Visual Studio",
            ["notepad"] = "Notepad",
            ["WindowsTerminal"] = "Windows Terminal",
            ["SystemSettings"] = "Windows Settings (read-only)",
            ["Taskmgr"] = "Task Manager (read-only)",
            ["WINWORD"] = "Microsoft Word",
            ["EXCEL"] = "Microsoft Excel",
            ["POWERPNT"] = "Microsoft PowerPoint",
            ["Acrobat"] = "PDF Reader",
            ["AcroRd32"] = "PDF Reader"
        };

    public static IReadOnlyList<string> ReadOpenApprovedApplications()
        => ObserveOpenApprovedApplications().Applications;

    public static ApplicationObservation ObserveOpenApprovedApplications()
    {
#if !WINDOWS
        return CreateApplicationObservation([], available: false, partial: false);
#else
        if (!OperatingSystem.IsWindows()) return CreateApplicationObservation([], available: false, partial: false);
        try
        {
            using var automation = new UIA3Automation();
            var desktop = automation.GetDesktop();
            var openApplications = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var hadInspectionErrors = false;
            foreach (var (processName, displayName) in ApprovedApplications)
            {
                System.Diagnostics.Process[] processes;
                try { processes = System.Diagnostics.Process.GetProcessesByName(processName); }
                catch (Exception)
                {
                    hadInspectionErrors = true;
                    continue;
                }

                foreach (var process in processes)
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
                            // Keep partial status because this process could not be inspected.
                            hadInspectionErrors = true;
                        }
                    }
                }
            }
            return CreateApplicationObservation(openApplications, available: true, partial: hadInspectionErrors);
        }
        catch (Exception)
        {
            return CreateApplicationObservation([], available: false, partial: false);
        }
#endif
    }

    public static ApplicationObservation CreateApplicationObservation(IEnumerable<string> applications,
        bool available, bool partial)
    {
        var names = applications.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!available) return new("unavailable", []);
        if (partial) return new("partial", names);
        return new(names.Length == 0 ? "empty" : "available", names);
    }

    public static ApplicationObservationMessage FormatApplicationObservation(ApplicationObservation observation)
    {
        if (observation.Status == "unavailable")
            return new("unavailable", null, "Không thể quan sát các ứng dụng desktop trong phiên này.");
        if (observation.Status == "partial")
            return new("partial", observation.Applications.Count == 0
                ? "Chỉ quan sát được một phần ứng dụng desktop; không thể kết luận danh sách trống."
                : "Chỉ quan sát được một phần ứng dụng desktop: " +
                  string.Join(", ", observation.Applications) + ".", null);
        if (observation.Status == "empty")
            return new("empty", "Quan sát hoàn tất; không thấy ứng dụng nào trong danh sách được phép.", null);
        return new("available", "Các ứng dụng đang mở: " + string.Join(", ", observation.Applications) + ".", null);
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
