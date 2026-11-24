using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop Companion requires Windows.");
var workspace = Environment.GetEnvironmentVariable("DETOT_DESKTOP_WORKSPACE_ROOT");
var edgeProfile = Environment.GetEnvironmentVariable("DETOT_DESKTOP_EDGE_PROFILE");
if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace) ||
    string.IsNullOrWhiteSpace(edgeProfile) || !Path.IsPathFullyQualified(edgeProfile))
{
    Console.Error.WriteLine("Set a valid DETOT_DESKTOP_WORKSPACE_ROOT and isolated DETOT_DESKTOP_EDGE_PROFILE in the launcher.");
    return 2;
}
workspace = Path.GetFullPath(workspace);
edgeProfile = Path.GetFullPath(edgeProfile);
if (Path.GetFullPath(edgeProfile).StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("The Edge profile must be isolated from the repository workspace.");
    return 2;
}

var pipeName = DesktopCompanionProtocol.PipeNameForCurrentUser();
var handler = new CompanionActions(workspace, edgeProfile);
if (args.Contains("--observe-approved-applications", StringComparer.Ordinal))
{
    var observation = CompanionUiAutomation.ObserveApprovedApplications();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        observation.Status,
        applications = observation.Applications
    }));
    return observation.Status == "unavailable" ? 4 : 0;
}
if (args.Contains("--smoke-test", StringComparer.Ordinal))
{
    var smoke = await handler.RunSmokeTestAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(smoke));
    return smoke.TryGetProperty("smokeTestPassed", out var passed) && passed.GetBoolean() ? 0 : 3;
}
Console.WriteLine($"Desktop Companion ready for this Windows user (pipe: {pipeName}). Press Ctrl+C to stop.");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
while (!shutdown.IsCancellationRequested)
{
    await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
    try { await server.WaitForConnectionAsync(shutdown.Token); }
    catch (OperationCanceledException) { break; }
    using var reader = new StreamReader(server, new UTF8Encoding(false), false, 2048, leaveOpen: true);
    await using var writer = new StreamWriter(server, new UTF8Encoding(false), 2048, leaveOpen: true) { AutoFlush = true };
    try
    {
        var line = await DesktopCompanionProtocol.ReadBoundedLineAsync(reader, 4096, shutdown.Token);
        if (!DesktopCompanionProtocol.TryParseRequest(line, out var request) || request is null)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { requestId = "", result = new { error = "invalid_request" } }));
            continue;
        }
        using var actionDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        actionDeadline.CancelAfter(TimeSpan.FromSeconds(request.UiAction?.Operation is "launch" or "navigate" ? 20 : 10));
        var result = await handler.ExecuteAsync(request.Action, request.UiAction, request.UserConfirmed, actionDeadline.Token);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { requestId = request.RequestId, result }));
    }
    catch (OperationCanceledException) { }
    catch (Exception error) when (error is IOException or InvalidOperationException or HttpRequestException or
                                  System.ComponentModel.Win32Exception or UnauthorizedAccessException)
    {
        // Never include UI text, paths, request content, credentials, or exception messages in the IPC response.
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { requestId = "", result = new { error = "desktop_action_failed" } }));
        Console.Error.WriteLine($"Desktop action failed: {error.GetType().Name}");
    }
}
return 0;

static class DesktopSessionDiagnostics
{
    private const int UserObjectName = 2;
    private const int TokenIntegrityLevel = 25;

    public static string ReadThreadDesktopName()
    {
        try
        {
            var desktop = GetThreadDesktop(GetCurrentThreadId());
            if (desktop == IntPtr.Zero) return "unavailable";
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, UserObjectName, name, name.Capacity * sizeof(char), out _)
                ? name.ToString() : "unavailable";
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return "unavailable"; }
    }

    public static string ReadCurrentIntegrityLevel()
    {
        using var process = Process.GetCurrentProcess();
        return ReadIntegrityLevel(process);
    }

    public static string[] ReadApprovedProcessIntegrityLevels(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Select(process =>
            {
                using (process) { return ReadIntegrityLevel(process); }
            }).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch { return ["unavailable"]; }
    }

    private static string ReadIntegrityLevel(Process process)
    {
        try
        {
            if (!OpenProcessToken(process.Handle, 0x0008, out var token)) return "unavailable";
            using (token)
            {
                _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var required);
                if (required <= 0) return "unavailable";
                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, required, out _)) return "unavailable";
                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    if (count == 0) return "unavailable";
                    var rid = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                    return rid < 0x2000 ? "untrusted" : rid < 0x3000 ? "low" : rid < 0x4000 ? "medium" : rid < 0x5000 ? "high" : "system";
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
                                      UnauthorizedAccessException or DllNotFoundException) { return "unavailable"; }
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}

sealed class CompanionActions(string workspace, string edgeProfile)
{
    private static readonly HttpClient Github = CreateGithubClient();
    private readonly CompanionDesktopUi desktopUi = new();
    private const int ProcessCommandLineInformation = 60;
    private sealed record EdgeVerificationResult(string Status, IReadOnlyList<string> States, bool WindowFound,
        bool UrlVerified, int? SessionId, string? CanonicalUrl);
    private sealed record WorkspaceSnapshot(bool WindowFound, bool WorkspaceVerified, int? SessionId);
    private sealed record VsCodeWorkspaceSnapshot(bool WindowFound, bool WorkspaceVerified, int? SessionId,
        string ExpectedWorkspace, string? ObservedWorkspace, string Reason, bool WindowTitleMatched,
        bool RootLabelMatched, bool AccessiblePathMatched, bool ExistingWindowObserved);
    private sealed record VsCodeWindowInventory(bool Available, IReadOnlySet<nint> Handles);
    private sealed record AddressBarSnapshot(bool Available, string? Value);
    private sealed record GithubAccountSnapshot(string Status, string? Username);
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    public Task<object> ExecuteAsync(string action, CancellationToken cancellationToken) =>
        ExecuteAsync(action, null, false, cancellationToken);

    public Task<object> ExecuteAsync(string action, ComputerUseUiAction? uiAction, CancellationToken cancellationToken) =>
        ExecuteAsync(action, uiAction, false, cancellationToken);

    public async Task<object> ExecuteAsync(string action, ComputerUseUiAction? uiAction, bool userConfirmed,
        CancellationToken cancellationToken) => action switch
    {
        "observe-applications" => ObserveApplications(),
        "open-workspace" => OpenWorkspace(),
        "open-edge" => OpenEdge("about:blank", "about:blank"),
        "open-github" => await OpenRepositoryPullsAsync(cancellationToken),
        "open-vscode" => OpenVsCode(),
        "read-workspace" => await ReadWorkspaceAsync(cancellationToken),
        "read-github-account" => await ReadGithubAccountAsync(cancellationToken),
        "read-github-pull-requests" => await ReadPullRequestsAsync(cancellationToken),
        "ui-operation" or "confirmed-ui-operation" when uiAction is not null =>
            await ExecuteUiOperationAsync(uiAction, userConfirmed, cancellationToken),
        _ => new { error = "action_not_allowed" }
    };

    private async Task<object> ExecuteUiOperationAsync(ComputerUseUiAction action, bool userConfirmed,
        CancellationToken cancellationToken)
    {
        if (!ComputerUseUiPlanner.AllowedApps.Contains(action.App, StringComparer.Ordinal) ||
            !ComputerUseUiPlanner.AllowedOperations.Contains(action.Operation, StringComparer.Ordinal))
            return new { error = "action_not_allowed" };
        if (action.App == "desktop" && action.Operation == "launch")
            return new { status = "launch_denied", actionVerified = false, app = action.App,
                reason = "generic_desktop_target_is_open_windows_only" };
        if (action.App == "desktop" && action.Operation == "launch-installed")
            return await LaunchInstalledApplicationAsync(action.Text!, cancellationToken);
        if (action.Operation == "launch") return await LaunchApprovedAppAsync(action.App, cancellationToken);
        if (action.Operation == "navigate")
            return action.App == "edge" && action.Url is not null
                ? await NavigateEdgeToUrlAsync(action.Url, cancellationToken)
                : new { error = "action_not_allowed" };
        var processIds = ReadUiProcessIds(action.App);
        return await desktopUi.ExecuteAsync(action, processIds, userConfirmed, cancellationToken);
    }

    private async Task<object> NavigateEdgeToUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var expected) || expected.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(expected.UserInfo) || expected.IsLoopback || url.Length > 1500)
            return new { status = "url_not_allowed", actionVerified = false, app = "edge" };
        var executable = FindEdge();
        if (executable is null) return new { status = "launch_failed", actionVerified = false, app = "edge" };
        try
        {
            Directory.CreateDirectory(edgeProfile);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add($"--user-data-dir={edgeProfile}");
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add(expected.AbsoluteUri);
            using var launched = Process.Start(start);
            if (launched is null) return new { status = "launch_failed", actionVerified = false, app = "edge" };
            var verification = await WaitForEdgeUrlAsync(expected, TimeSpan.FromSeconds(15), cancellationToken);
            return new
            {
                status = verification ? "verified" : "url_not_verified",
                actionVerified = verification,
                app = "edge",
                host = expected.IdnHost,
                path = expected.AbsolutePath
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
                                      UnauthorizedAccessException or IOException)
        {
            return new { status = "launch_failed", actionVerified = false, app = "edge" };
        }
    }

    private async Task<bool> WaitForEdgeUrlAsync(Uri expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + timeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var automation = new UIA3Automation();
                foreach (var process in GetDedicatedEdgeProcesses())
                {
                    using (process)
                    {
                        foreach (var window in automation.GetDesktop().FindAllChildren(cf =>
                                     cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id))))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var address = ReadAddressBar(window);
                            if (address.Available && Uri.TryCreate(address.Value, UriKind.Absolute, out var observed) &&
                                SameNavigationTarget(expected, observed)) return true;
                        }
                    }
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
                COMException or UnauthorizedAccessException)
            {
                // Edge and UIA may still be starting; retry until the bounded deadline.
            }
            await Task.Delay(300, cancellationToken);
        } while (DateTime.UtcNow < until);
        return false;
    }

    private static bool SameNavigationTarget(Uri expected, Uri observed) =>
        expected.Scheme == Uri.UriSchemeHttps && observed.Scheme == Uri.UriSchemeHttps &&
        string.Equals(expected.IdnHost, observed.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        expected.Port == observed.Port && string.Equals(expected.AbsolutePath, observed.AbsolutePath, StringComparison.Ordinal) &&
        string.Equals(expected.Query, observed.Query, StringComparison.Ordinal);

    private async Task<object> LaunchApprovedAppAsync(string app, CancellationToken cancellationToken)
    {
        try
        {
            switch (app)
            {
                case "edge":
                {
                    var result = ToElement(OpenEdge("about:blank", "about:blank"));
                    var verified = ReadBoolean(result, "windowVerified");
                    return new { status = verified ? "verified" : "launch_not_verified", actionVerified = verified, app };
                }
                case "vscode":
                {
                    var result = ToElement(OpenVsCode());
                    var verified = ReadBoolean(result, "workspaceVerified");
                    return new { status = verified ? "verified" : "workspace_not_verified", actionVerified = verified, app };
                }
                case "explorer":
                {
                    var result = ToElement(OpenWorkspace());
                    var verified = ReadBoolean(result, "workspaceVerified");
                    return new { status = verified ? "verified" : "workspace_not_verified", actionVerified = verified, app };
                }
                case "notepad":
                case "visual-studio":
                    _ = Process.Start(new ProcessStartInfo(app == "notepad" ? "notepad.exe" : "devenv.exe")
                    { UseShellExecute = true });
                    var deadline = DateTime.UtcNow.AddSeconds(8);
                    while (DateTime.UtcNow < deadline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (CompanionDesktopUi.FindAppWindows(app, ReadUiProcessIds(app)).Count > 0)
                            return new { status = "verified", actionVerified = true, app };
                        await Task.Delay(200, cancellationToken);
                    }
                    return new { status = "window_not_verified", actionVerified = false, app };
                default:
                    return new { error = "app_not_allowed" };
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
                                      UnauthorizedAccessException or COMException)
        {
            return new { status = "launch_failed", actionVerified = false, app };
        }
    }

    private async Task<object> LaunchInstalledApplicationAsync(string appName, CancellationToken cancellationToken)
    {
        if (!ComputerUseUiPlanner.IsSafeInstalledApplicationName(appName))
            return new { status = "application_launch_denied", actionVerified = false };

        var menuRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path, "Programs"));
        var search = InstalledApplicationShortcutResolver.FindExact(menuRoots, appName);
        if (!search.Complete) return new { status = "application_search_incomplete", actionVerified = false };
        if (search.Matches.Count == 0) return new { status = "application_not_found", actionVerified = false };
        if (search.Matches.Count > 1) return new { status = "application_ambiguous", actionVerified = false };

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (shellType is null) return new { status = "shortcut_resolver_unavailable", actionVerified = false };
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return new { status = "shortcut_resolver_unavailable", actionVerified = false };
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                binder: null, target: shell, args: [search.Matches[0].ShortcutPath]);
            if (shortcut is null) return new { status = "shortcut_unavailable", actionVerified = false };

            var shortcutType = shortcut.GetType();
            var targetPath = Convert.ToString(shortcutType.InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, binder: null, target: shortcut, args: null));
            var arguments = Convert.ToString(shortcutType.InvokeMember("Arguments",
                System.Reflection.BindingFlags.GetProperty, binder: null, target: shortcut, args: null));
            if (!string.IsNullOrWhiteSpace(arguments))
                return new { status = "application_launch_denied", actionVerified = false, reason = "shortcut_arguments_not_allowed" };

            targetPath = targetPath is null ? null : Environment.ExpandEnvironmentVariables(targetPath);
            var trustedRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };
            if (!InstalledApplicationShortcutResolver.IsTrustedExecutableTarget(targetPath, trustedRoots))
                return new { status = "application_launch_denied", actionVerified = false, reason = "target_not_a_trusted_app_executable" };

            var processName = Path.GetFileNameWithoutExtension(targetPath);
            var existingProcessIds = ReadProcessIdsByNameInCurrentSession(processName);
            var existingWindowHandles = CompanionDesktopUi.FindAppWindows("desktop", existingProcessIds)
                .Select(window => (nint)window.Handle).ToHashSet();
            using var started = Process.Start(new ProcessStartInfo(targetPath!)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(targetPath!)!
            });
            if (started is null) return new { status = "launch_failed", actionVerified = false };

            var deadline = DateTime.UtcNow.AddSeconds(12);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processIds = ReadProcessIdsByNameInCurrentSession(processName);
                var newWindow = CompanionDesktopUi.FindAppWindows("desktop", processIds)
                    .FirstOrDefault(window => DesktopCompanionVerification.IsNewWindow(window.Handle, existingWindowHandles));
                if (newWindow.Handle != IntPtr.Zero)
                {
                    var windowId = desktopUi.RegisterSelectableWindow("desktop", newWindow.ProcessId,
                        newWindow.Handle, newWindow.Title);
                    if (windowId is not null)
                        return new { status = "verified", actionVerified = true, newWindowVerified = true, windowId, app = appName };
                }
                await Task.Delay(250, cancellationToken);
            } while (DateTime.UtcNow < deadline);

            return new { status = "window_not_verified", actionVerified = false, app = appName };
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
                                      UnauthorizedAccessException or COMException or System.Reflection.TargetInvocationException or
                                      System.Security.SecurityException)
        {
            return new { status = "launch_failed", actionVerified = false, app = appName };
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private IReadOnlyList<int> ReadUiProcessIds(string app)
    {
        if (app == "desktop") return CompanionUiAutomation.GetInteractiveProcessIds();
        var processName = app switch
        {
            "edge" => "msedge",
            "vscode" => "Code",
            "explorer" => "explorer",
            "visual-studio" => "devenv",
            "notepad" => "notepad",
            _ => ""
        };
        if (processName.Length == 0) return [];
        var processes = app == "edge" ? GetDedicatedEdgeProcesses() : Process.GetProcessesByName(processName).ToList();
        var ids = new List<int>();
        foreach (var process in processes)
        {
            using (process)
            {
                if (SafeSessionId(process) == Process.GetCurrentProcess().SessionId) ids.Add(process.Id);
            }
        }
        return ids;
    }

    private static List<int> ReadProcessIdsByNameInCurrentSession(string processName)
    {
        using var current = Process.GetCurrentProcess();
        var currentSessionId = current.SessionId;
        var processIds = new List<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == currentSessionId) processIds.Add(process.Id);
                }
                catch (InvalidOperationException) { }
            }
        }
        return processIds;
    }

    public async Task<JsonElement> RunSmokeTestAsync(CancellationToken cancellationToken)
    {
        var ipc = await VerifyPipeAsync(cancellationToken);
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var currentDesktop = DesktopSessionDiagnostics.ReadThreadDesktopName();
        var currentIntegrity = DesktopSessionDiagnostics.ReadCurrentIntegrityLevel();
        var before = ToElement(await ExecuteAsync("observe-applications", cancellationToken));
        var git = ToElement(await ExecuteAsync("read-workspace", cancellationToken));
        var edge = ToElement(await ExecuteAsync("open-github", cancellationToken));
        var pulls = ToElement(await ExecuteAsync("read-github-pull-requests", cancellationToken));
        var code = ToElement(await ExecuteAsync("open-vscode", cancellationToken));
        var account = ToElement(await ExecuteAsync("read-github-account", cancellationToken));
        await Task.Delay(1500, cancellationToken);
        var after = ToElement(await ExecuteAsync("observe-applications", cancellationToken));
        var processSessions = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge"] = ReadProcessSessions("msedge"),
            ["VSCode"] = ReadProcessSessions("Code")
        };
        var processIntegrity = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge"] = DesktopSessionDiagnostics.ReadApprovedProcessIntegrityLevels("msedge"),
            ["VSCode"] = DesktopSessionDiagnostics.ReadApprovedProcessIntegrityLevels("Code")
        };
        var ipcVerified = ipc.TryGetProperty("action", out var ipcAction) && ipcAction.GetString() == "read-workspace";
        var edgeWindowFound = ReadBoolean(edge, "windowVerified");
        var vscodeWindowFound = ReadBoolean(code, "windowVerified");
        var workspaceVerified = ReadBoolean(code, "workspaceVerified");
        var edgeSessionId = ReadNullableInt(edge, "sessionId");
        var codeSessionId = ReadNullableInt(code, "sessionId");
        var sameSession = DesktopCompanionVerification.SameInteractiveSession(currentSessionId, currentDesktop,
            Environment.UserInteractive, edgeSessionId, codeSessionId, edgeWindowFound, vscodeWindowFound);
        var edgeVerified = ReadString(edge, "status") == "verified" && ReadBoolean(edge, "urlVerified");
        var pullsAvailable = !pulls.TryGetProperty("error", out _);
        var accountStatus = ReadString(account, "status");
        var smokePassed = DesktopCompanionVerification.IsSmokeTestPassed(ipcVerified, sameSession,
            edgeVerified, workspaceVerified, pullsAvailable, accountStatus);
        return JsonSerializer.SerializeToElement(new
        {
            verifiedSameSession = sameSession,
            smokeTestPassed = smokePassed,
            authenticatedLocalIpc = ipc,
            processSessions,
            processIntegrity,
            executionContext = new { sessionId = currentSessionId, user = currentUser,
                integrityLevel = currentIntegrity, userInteractive = Environment.UserInteractive, desktop = currentDesktop },
            session = new { before, after },
            repository = git,
            edge,
            publicPullRequestsAndChecks = pulls,
            visualStudioCode = code,
            accountRead = account
        });
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? ReadNullableInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value);

    private async Task<JsonElement> VerifyPipeAsync(CancellationToken cancellationToken)
    {
        var name = DesktopCompanionProtocol.PipeNameForCurrentUser();
        try
        {
            await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            var listener = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 2048, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), 2048, leaveOpen: true) { AutoFlush = true };
                var line = await DesktopCompanionProtocol.ReadBoundedLineAsync(reader, 4096, cancellationToken);
                if (!DesktopCompanionProtocol.TryParseRequest(line, out var request) || request is null)
                    return ToElement(new { error = "invalid_request" });
                var result = await ExecuteAsync(request.Action, cancellationToken);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { requestId = request.RequestId, result }));
                return ToElement(new { action = request.Action });
            }, cancellationToken);
            var clientResult = await new DesktopCompanionClient(name).ExecuteAsync("read-workspace", cancellationToken);
            var serverResult = await listener.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
            if (clientResult.TryGetProperty("error", out var error)) return ToElement(new { error = error.GetString() });
            if (!serverResult.TryGetProperty("action", out _)) return serverResult;
            return serverResult;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            return ToElement(new { error = "companion_ipc_unavailable" });
        }
    }

    private async Task<object> ReadGithubAccountAsync(CancellationToken cancellationToken)
    {
        const string profileSettingsUrl = "https://github.com/settings/profile";
        var opened = OpenEdge(profileSettingsUrl, profileSettingsUrl);
        if (ReadString(ToElement(opened), "status") == "launch_failed")
            return new { status = "failed", username = (string?)null };

        var deadline = DateTime.UtcNow.AddSeconds(4);
        var menuOpened = false;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var observation = TryReadGithubAccountState(!menuOpened, out var openedThisPass);
            menuOpened |= openedThisPass;
            if (observation.Status is "verified" or "signed_out")
                return new { status = observation.Status, username = observation.Username };
            await Task.Delay(250, cancellationToken);
        }
        return new { status = "not_verified", username = (string?)null };
    }

    private GithubAccountSnapshot TryReadGithubAccountState(bool allowOpenMenu, out bool menuOpened)
    {
        menuOpened = false;
        try
        {
            using var automation = new UIA3Automation();
            var desktop = automation.GetDesktop();
            foreach (var process in GetDedicatedEdgeProcesses())
            {
                using (process)
                {
                    var windows = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id)));
                    foreach (var window in windows)
                    {
                        var address = ReadAddressBar(window);
                        if (DesktopCompanionVerification.IsGithubLoginPage(address.Value))
                        {
                            var loginLabels = ReadAccessibleLabels(window);
                            if (DesktopCompanionVerification.HasGithubSignedOutMarker(loginLabels))
                                return new("signed_out", null);
                            continue;
                        }
                        if (!DesktopCompanionVerification.GithubAddressMatches(address.Value, "https://github.com/settings/profile")) continue;
                        var labels = ReadAccessibleLabels(window);
                        var visibleUsername = DesktopCompanionVerification.ExtractSignedInUsername(labels);
                        if (visibleUsername is not null) return new("verified", visibleUsername);
                        if (!allowOpenMenu) continue;
                        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                        var profileMenu = buttons.FirstOrDefault(button =>
                        {
                            var name = SafeName(button);
                            return name.Contains("view profile and more", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("open user navigation menu", StringComparison.OrdinalIgnoreCase);
                        });
                        if (profileMenu is null) continue;
                        try { profileMenu.Patterns.Invoke.Pattern.Invoke(); }
                        catch { continue; }
                        menuOpened = true;
                        Thread.Sleep(200);
                        var afterClickLabels = ReadAccessibleLabels(window);
                        var login = DesktopCompanionVerification.ExtractSignedInUsername(afterClickLabels);
                        if (login is not null) return new("verified", login);
                    }
                }
            }
        }
        catch { /* Accessibility or browser state was insufficient; report not_verified. */ }
        return new("not_verified", null);
    }

    private static int[] ReadProcessSessions(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Select(process =>
            {
                using (process) { try { return process.SessionId; } catch { return -1; } }
            }).Where(session => session >= 0).Distinct().ToArray();
        }
        catch { return []; }
    }

    private static object ObserveApplications()
    {
        var observation = CompanionUiAutomation.ObserveApprovedApplications();
        var formatted = WindowsDesktopAutomation.FormatApplicationObservation(observation);
        return formatted.Error is not null
            ? new { error = "desktop_observation_unavailable" }
            : new { observationStatus = formatted.Status, applications = observation.Applications };
    }

    private object OpenWorkspace()
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(workspace);
        Process.Start(start);
        var snapshot = WaitForExplorerWorkspace(TimeSpan.FromSeconds(8));
        return new { succeeded = snapshot.WorkspaceVerified, windowVerified = snapshot.WindowFound,
            workspaceVerified = snapshot.WorkspaceVerified, sessionId = snapshot.SessionId };
    }

    private object OpenVsCode()
    {
        var executable = FindVsCode();
        if (executable is null) return new { error = "vscode_unavailable" };
        var before = ReadVsCodeWindowInventory();
        if (!before.Available)
            return new { succeeded = false, status = "workspace_not_verified", processStarted = false,
                windowVerified = false, workspaceVerified = false, expectedWorkspace = Path.GetFullPath(workspace),
                observedWorkspace = (string?)null, workspaceVerificationReason = "prelaunch_window_inventory_unavailable",
                evidence = new { windowTitleMatched = false, rootLabelMatched = false, accessiblePathMatched = false,
                    existingWindowObserved = false } };
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in DesktopCompanionVerification.BuildVsCodeWorkspaceArguments(workspace))
            start.ArgumentList.Add(argument);
        using var launched = Process.Start(start);
        if (launched is null)
            return new { succeeded = false, status = "launch_failed", processStarted = false,
                windowVerified = false, workspaceVerified = false, sessionId = (int?)null,
                expectedWorkspace = Path.GetFullPath(workspace), observedWorkspace = (string?)null,
                workspaceVerificationReason = "launch_failed" };
        VsCodeWorkspaceSnapshot snapshot;
        try { snapshot = WaitForWorkspaceWindow(TimeSpan.FromSeconds(12), before.Handles); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            var expected = Path.GetFullPath(workspace);
            return new { succeeded = false, status = "workspace_not_verified", processStarted = true,
                windowVerified = false, workspaceVerified = false, expectedWorkspace = expected,
                observedWorkspace = (string?)null, workspaceVerificationReason = "window_inspection_failed",
                evidence = new { windowTitleMatched = false, rootLabelMatched = false, accessiblePathMatched = false,
                    existingWindowObserved = false } };
        }
        return new
        {
            succeeded = snapshot.WorkspaceVerified,
            status = !snapshot.WindowFound && snapshot.Reason is "timeout_window_not_found" or "no_new_window_after_launch"
                ? "window_not_found" :
                snapshot.WorkspaceVerified ? "verified" : "workspace_not_verified",
            processStarted = true,
            windowVerified = snapshot.WindowFound,
            workspaceVerified = snapshot.WorkspaceVerified,
            sessionId = snapshot.SessionId,
            expectedWorkspace = snapshot.ExpectedWorkspace,
            observedWorkspace = snapshot.ObservedWorkspace,
            workspaceVerificationReason = snapshot.Reason,
            evidence = new { windowTitleMatched = snapshot.WindowTitleMatched,
                rootLabelMatched = snapshot.RootLabelMatched, accessiblePathMatched = snapshot.AccessiblePathMatched,
                existingWindowObserved = snapshot.ExistingWindowObserved }
        };
    }

    private async Task<object> OpenRepositoryPullsAsync(CancellationToken cancellationToken)
    {
        var status = await DesktopOperator.ReadWorkspaceStatusAsync(workspace, cancellationToken);
        var repository = DesktopOperator.SafeGitHubRepository(status.Repository);
        if (repository is null) return new { error = "github_repository_unavailable" };
        var expected = $"https://github.com/{repository}/pulls";
        var opened = OpenEdge(expected, expected);
        return opened;
    }

    private object OpenEdge(string url, string expectedUrl)
    {
        var executable = FindEdge();
        if (executable is null)
            return new { succeeded = false, status = "launch_failed", states = new[] { "launch_failed" }, processStarted = false,
                windowVerified = false, urlVerified = false, url = (string?)null, sessionId = (int?)null, profile = "detot-dedicated" };
        Directory.CreateDirectory(edgeProfile);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add($"--user-data-dir={edgeProfile}");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add(url);
        Process? launched;
        try { launched = Process.Start(start); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new { succeeded = false, status = "launch_failed", states = new[] { "launch_failed" }, processStarted = false,
                windowVerified = false, urlVerified = false, url = (string?)null, sessionId = (int?)null, profile = "detot-dedicated" };
        }
        if (launched is null)
            return new { succeeded = false, status = "launch_failed", states = new[] { "launch_failed" }, processStarted = false,
                windowVerified = false, urlVerified = false, url = (string?)null, sessionId = (int?)null, profile = "detot-dedicated" };
        using (launched) { }

        var requireUrl = !expectedUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
        var result = WaitForEdgeVerification(expectedUrl, requireUrl, TimeSpan.FromSeconds(15));
        var success = requireUrl ? result.Status == "verified" : result.Status == "window_found";
        return new { succeeded = success, status = result.Status, states = result.States, processStarted = true,
            windowVerified = result.WindowFound, urlVerified = result.UrlVerified,
            url = result.UrlVerified ? result.CanonicalUrl : null, sessionId = result.SessionId, profile = "detot-dedicated" };
    }

    private EdgeVerificationResult WaitForEdgeVerification(string expectedUrl, bool requireUrl, TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        var states = new List<string> { "process_started" };
        var dedicatedProcessFound = false;
        var windowFound = false;
        int? sessionId = null;
        string? canonicalUrl = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var automation = new UIA3Automation();
                var desktop = automation.GetDesktop();
                foreach (var process in GetDedicatedEdgeProcesses())
                {
                    using (process)
                    {
                        dedicatedProcessFound = true;
                        var session = SafeSessionId(process);
                        var windows = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id)));
                        foreach (var window in windows)
                        {
                            windowFound = true;
                            sessionId = session;
                            if (!states.Contains("window_found", StringComparer.Ordinal)) states.Add("window_found");
                            var address = ReadAddressBar(window);
                            if (DesktopCompanionVerification.GithubAddressMatches(address.Value, expectedUrl))
                            {
                                canonicalUrl = DesktopCompanionVerification.CanonicalGithubAddress(address.Value, expectedUrl);
                                states.Add("verified");
                                return new("verified", states, true, true, sessionId, canonicalUrl);
                            }
                            if (requireUrl && !states.Contains("url_not_verified", StringComparer.Ordinal)) states.Add("url_not_verified");
                        }
                    }
                }
            }
            catch { /* Edge/UIA may still be initializing. */ }
            Thread.Sleep(300);
        }
        var status = !requireUrl && windowFound ? "window_found" :
            DesktopCompanionVerification.GetEdgeVerificationStatus(true, dedicatedProcessFound,
                windowFound, canonicalUrl is not null);
        if (!states.Contains(status, StringComparer.Ordinal)) states.Add(status);
        return new(status, states, windowFound, canonicalUrl is not null, sessionId, canonicalUrl);
    }

    private VsCodeWindowInventory ReadVsCodeWindowInventory()
    {
        try
        {
            using var automation = new UIA3Automation();
            var desktop = automation.GetDesktop();
            var handles = new HashSet<nint>();
            foreach (var process in Process.GetProcessesByName("Code"))
            {
                using (process)
                {
                    if (SafeSessionId(process) != Process.GetCurrentProcess().SessionId) continue;
                    foreach (var window in desktop.FindAllChildren(cf =>
                                 cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id))))
                    {
                        var handle = SafeNativeWindowHandle(window);
                        if (handle <= 0) return new(false, handles);
                        handles.Add(handle);
                    }
                }
            }
            return new(true, handles);
        }
        catch { return new(false, new HashSet<nint>()); }
    }

    private VsCodeWorkspaceSnapshot WaitForWorkspaceWindow(TimeSpan duration, IReadOnlySet<nint> existingWindowHandles)
    {
        var until = DateTime.UtcNow + duration;
        var emptyEvidence = DesktopCompanionVerification.EvaluateWorkspace(workspace, false, null, [], []);
        VsCodeWorkspaceSnapshot snapshot = new(false, false, null, emptyEvidence.ExpectedWorkspace,
            null, "timeout_window_not_found", false, false, false, false);
        var accessibilityFailed = false;
        var existingWindowObserved = false;
        var windowHandleUnavailable = false;
        do
        {
            try
            {
                using var automation = new UIA3Automation();
                var desktop = automation.GetDesktop();
                foreach (var process in Process.GetProcessesByName("Code"))
                {
                    using (process)
                    {
                        var processSession = SafeSessionId(process);
                        if (processSession != Process.GetCurrentProcess().SessionId) continue;
                        var windows = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id)));
                        foreach (var window in windows)
                        {
                            var handle = SafeNativeWindowHandle(window);
                            if (handle <= 0)
                            {
                                windowHandleUnavailable = true;
                                continue;
                            }
                            if (!DesktopCompanionVerification.IsNewWindow(handle, existingWindowHandles))
                            {
                                existingWindowObserved = true;
                                continue;
                            }
                            var title = SafeName(window);
                            // VS Code exposes Explorer section headers (for example, "Open Editors") as TreeItems too.
                            // Only inspect direct TreeItem children of UIA Tree controls; descendants can be project file names.
                            var treeControls = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Tree));
                            var roots = treeControls.SelectMany(tree =>
                                tree.FindAllChildren(cf => cf.ByControlType(ControlType.TreeItem))).ToArray();
                            var rootNames = roots.Select(SafeName).ToArray();
                            var paths = roots.Select(SafeHelpText).ToArray();
                            var evidence = DesktopCompanionVerification.EvaluateWorkspace(workspace, true, title, rootNames, paths);
                            snapshot = new(true, evidence.Verified, processSession, evidence.ExpectedWorkspace,
                                evidence.ObservedWorkspace, evidence.Reason, evidence.WindowTitleMatched,
                                evidence.RootLabelMatched, evidence.AccessiblePathMatched, existingWindowObserved);
                            if (evidence.Verified) return snapshot;
                        }
                    }
                }
            }
            catch { accessibilityFailed = true; }
            Thread.Sleep(250);
        } while (DateTime.UtcNow < until);
        if (!snapshot.WindowFound)
            return snapshot with { Reason = accessibilityFailed ? "uia_unavailable" :
                windowHandleUnavailable ? "window_handle_unavailable" :
                existingWindowObserved ? "no_new_window_after_launch" : "timeout_window_not_found",
                ExistingWindowObserved = existingWindowObserved };
        return snapshot with
        {
            Reason = snapshot.RootLabelMatched || snapshot.AccessiblePathMatched
                ? "timeout_workspace_mismatch"
                : "timeout_workspace_accessibility_unavailable",
            ExistingWindowObserved = existingWindowObserved
        };
    }

    private static nint SafeNativeWindowHandle(AutomationElement window)
    {
        try { return window.Properties.NativeWindowHandle.Value; }
        catch { return 0; }
    }

    private WorkspaceSnapshot WaitForExplorerWorkspace(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        WorkspaceSnapshot snapshot = new(false, false, null);
        do
        {
            try
            {
                using var automation = new UIA3Automation();
                foreach (var process in Process.GetProcessesByName("explorer"))
                {
                    using (process)
                    {
                        var session = SafeSessionId(process);
                        if (session != Process.GetCurrentProcess().SessionId) continue;
                        var windows = automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window).And(cf.ByProcessId(process.Id)));
                        foreach (var window in windows)
                        {
                            var title = SafeName(window);
                            var edits = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
                            var paths = edits.Select(edit =>
                            {
                                try { return edit.Patterns.Value.Pattern.Value.Value; }
                                catch { return null; }
                            }).ToArray();
                            var folder = Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                            var verified = title.Contains(folder, StringComparison.OrdinalIgnoreCase) ||
                                paths.Any(path => path is not null && string.Equals(path.TrimEnd(['\\', '/']),
                                    workspace.TrimEnd(['\\', '/']), StringComparison.OrdinalIgnoreCase));
                            snapshot = new(true, verified, session);
                            if (verified) return snapshot;
                        }
                    }
                }
            }
            catch { }
            Thread.Sleep(250);
        } while (DateTime.UtcNow < until);
        return snapshot;
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Properties.Name.Value ?? ""; }
        catch { return ""; }
    }

    private static string SafeHelpText(AutomationElement element)
    {
        try { return element.Properties.HelpText.Value ?? ""; }
        catch { return ""; }
    }

    private IReadOnlyList<Process> GetDedicatedEdgeProcesses()
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName("msedge"))
        {
            try
            {
                if (SafeSessionId(process) != Process.GetCurrentProcess().SessionId)
                {
                    process.Dispose();
                    continue;
                }
                var commandLine = ReadProcessCommandLine(process);
                if (DesktopCompanionVerification.IsDedicatedEdgeCommandLine(commandLine, edgeProfile)) matches.Add(process);
                else process.Dispose();
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                process.Dispose();
            }
        }
        return matches;
    }

    private static int? SafeSessionId(Process process)
    {
        try { return process.SessionId; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static string? ReadProcessCommandLine(Process process)
    {
        try
        {
            var status = NtQueryInformationProcess(process.Handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (status != unchecked((int)0xC0000004) || needed <= 0 || needed > 64 * 1024) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                status = NtQueryInformationProcess(process.Handle, ProcessCommandLineInformation, buffer, needed, out _);
                if (status != 0) return null;
                var commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
                if (commandLine.Buffer == IntPtr.Zero || commandLine.Length == 0 || commandLine.Length > 60 * 1024) return null;
                return Marshal.PtrToStringUni(commandLine.Buffer, commandLine.Length / sizeof(char));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or DllNotFoundException)
        {
            return null;
        }
    }

    private static AddressBarSnapshot ReadAddressBar(AutomationElement window)
    {
        try
        {
            var addressControls = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)
                .Or(cf.ByControlType(ControlType.ComboBox)));
            foreach (var control in addressControls)
            {
                var name = SafeName(control);
                if (!(name.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("search or enter", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("địa chỉ", StringComparison.OrdinalIgnoreCase))) continue;
                try { return new(true, control.Patterns.Value.Pattern.Value.Value); }
                catch { return new(false, null); }
            }
        }
        catch { }
        return new(false, null);
    }

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass,
        IntPtr information, int informationLength, out int returnLength);

    private static string[] ReadAccessibleLabels(AutomationElement window)
    {
        try
        {
            return window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)
                    .Or(cf.ByControlType(ControlType.MenuItem)).Or(cf.ByControlType(ControlType.Button)))
                .Select(SafeName).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        }
        catch { return []; }
    }

    private async Task<object> ReadWorkspaceAsync(CancellationToken cancellationToken)
    {
        var status = await DesktopOperator.ReadWorkspaceStatusAsync(workspace, cancellationToken);
        return new { available = status.Available, repository = status.Repository, branch = status.Branch,
            hasTrackedChanges = status.HasTrackedChanges, message = status.Message };
    }

    private async Task<object> ReadPullRequestsAsync(CancellationToken cancellationToken)
    {
        var status = await DesktopOperator.ReadWorkspaceStatusAsync(workspace, cancellationToken);
        var repoPath = DesktopOperator.SafeGitHubRepository(status.Repository);
        if (repoPath is null) return new { error = "github_repository_unavailable" };
        using var response = await Github.GetAsync($"repos/{repoPath}/pulls?state=all&per_page=3", cancellationToken);
        if (!response.IsSuccessStatusCode) return new { error = $"github_http_{(int)response.StatusCode}" };
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return new { error = "github_response_invalid" };
        var pulls = new List<object>();
        foreach (var pull in document.RootElement.EnumerateArray())
        {
            if (!pull.TryGetProperty("number", out var number) || !number.TryGetInt32(out var n) ||
                !pull.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String) continue;
            var state = pull.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
            var sha = pull.TryGetProperty("head", out var head) && head.TryGetProperty("sha", out var shaElement)
                ? shaElement.GetString() : null;
            var checksStatus = "unavailable";
            object[] checks = [];
            if (sha is { Length: >= 40 })
            {
                using var checkResponse = await Github.GetAsync($"repos/{repoPath}/commits/{sha}/check-runs?per_page=20", cancellationToken);
                if (checkResponse.IsSuccessStatusCode)
                {
                    using var checkDocument = JsonDocument.Parse(await checkResponse.Content.ReadAsStringAsync(cancellationToken));
                    if (checkDocument.RootElement.TryGetProperty("check_runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                    {
                        checksStatus = runs.GetArrayLength() == 0 ? "empty" : "available";
                        checks = runs.EnumerateArray().Take(8).Select(run => (object)new
                        {
                            name = run.TryGetProperty("name", out var name) ? name.GetString() : "check",
                            status = run.TryGetProperty("status", out var runStatus) ? runStatus.GetString() : null,
                            conclusion = run.TryGetProperty("conclusion", out var conclusion) && conclusion.ValueKind == JsonValueKind.String
                                ? conclusion.GetString() : null
                        }).ToArray();
                    }
                }
            }
            pulls.Add(new { number = n, title = title.GetString()![..Math.Min(180, title.GetString()!.Length)], state,
                headSha = sha is { Length: >= 8 } ? sha[..8] : null, checksStatus, checks });
        }
        return new { repository = repoPath, pullRequests = pulls };
    }

    private static HttpClient CreateGithubClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Detot-DesktopCompanion", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string? FindEdge()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return paths.FirstOrDefault(File.Exists);
    }

    private static string? FindVsCode()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe")
        };
        return paths.FirstOrDefault(File.Exists);
    }
}
