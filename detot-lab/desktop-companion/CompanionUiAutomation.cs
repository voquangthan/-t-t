using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal static class CompanionUiAutomation
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

    private static readonly Lazy<IUiAutomation> Automation = new(() =>
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddWinAppUiAutomation()
            .BuildServiceProvider();
        return services.GetRequiredService<IUiAutomation>();
    });

    public static WindowsDesktopAutomation.ApplicationObservation ObserveApprovedApplications()
    {
        if (!OperatingSystem.IsWindows())
            return WindowsDesktopAutomation.CreateApplicationObservation([], available: false, partial: false);

        try
        {
            var automation = Automation.Value;
            var observed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var partial = false;
            foreach (var (processName, displayName) in ApprovedApplications)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(processName); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    partial = true;
                    continue;
                }

                foreach (var process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            var windows = automation.FindWindowsByPid(process.Id);
                            if (windows.Any(window => window.Item1 != IntPtr.Zero &&
                                IsWindowVisible(window.Item1) && !string.IsNullOrWhiteSpace(window.Item3)))
                                observed.Add(displayName);
                        }
                        catch (Exception error) when (error is InvalidOperationException or
                            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                        {
                            // A process may exit while UIA enumerates its windows. Preserve partial status.
                            partial = true;
                        }
                    }
                }
            }

            return WindowsDesktopAutomation.CreateApplicationObservation(observed, available: true, partial);
        }
        catch (Exception error) when (error is InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            return WindowsDesktopAutomation.CreateApplicationObservation([], available: false, partial: false);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
