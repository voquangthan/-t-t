Warning: truncated output (original token count: 18227)
Total output lines: 1355

using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
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
if (args.Contains("--smoke-test", StringComparer.Ordinal))
{
    var smoke = await handler.RunSmokeTestAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(smoke));
    return smoke.TryGetProperty("smokeTestPassed", out var passed) && passed.GetBoolean() ? 0 : 3;
}
Console.WriteLine($"Desktop Companion ready for this Windows user (pipe: {pipeName}). Press Ctrl+C to stop.");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
await new DesktopCompanionPipeServer(pipeName).RunAsync(
    (request, cancellationToken) => handler.ExecuteAsync(request, cancellationToken), shutdown.Token);
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
                    return DesktopCompanionVerification.IntegrityLevelFromRid(rid);
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
    private sealed record CachedUiTarget(AutomationElement Element, nint WindowHandle, int ProcessId, int SessionId,
        string ControlType, DateTime ExpiresAt);
    private readonly UIA3Automation uiAutomation = new();
    private readonly Dictionary<string, CachedUiTarget> uiTargets = new(StringComparer.Ordinal);
    private const int UiWindowLimit = 6;
    private const int UiElementLimitPerWindow = 12;
    private const int UiTreeDepthLimit = 4;
    private const int UiTargetLifetimeSeconds = 90;
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    public Task<object> ExecuteAsync(string action, CancellationToken cancellationToken) =>
        ExecuteAsync(new DesktopCompanionProtocol.Request(action, Guid.NewGuid().ToString("N")), cancellationToken);

    public async Task<object> ExecuteAsync(DesktopCompanionProtocol.Request request, CancellationToken cancellationToken)
    {
        return request.Action switch
        {
            "observe-applications" => ObserveApplications(),
            "observe-desktop-ui" => ObserveDesktopUi(),
            "activate-window" or "click-element" or "double-click-element" or "select-element" or "type-text" or "scroll-element" or "press-key"
                => InteractWithUi(request),
            "open-workspace" => OpenWorkspace(),
            "open-workspace-item" => OpenWorkspaceItem(request.Value!),
            "open-edge" => OpenEdge("about:blank", "about:blank"),
            "open-github" => await OpenRepositoryPullsAsync(cancellationToken),
            "open-vscode" => OpenVsCode(),
            "open-settings" or "open-task-manager" or "open-word" or "open-excel" or "open-powerpoint" or "open-pdf-reader"
                => OpenApprovedApplication(request.Action),
            "read-workspace" => await ReadWorkspaceAsync(cancellationToken),
            "read-github-account" => await ReadGithubAccountAsync(cancellationToken),
            "read-github-pull-requests" => await ReadPullRequestsAsync(cancellationToken),
            _ => new { error = "action_not_allowed" }
        };
    }

    private object ObserveDesktopUi()
    {
        uiTargets.Clear();
        try
        {
            var desktop = uiAutomation.GetDesktop();
            var allWindows = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window));
            var windows = allWindows
                .Select(window => (Window: window, Handle: SafeNativeWindowHandle(window), ProcessId: SafeProcessId(window),
                    Name: SafeName(window), Focused: SafeBoolean(() => window.Properties.HasKeyboardFocus.ValueOrDefault),
                    Offscreen: SafeBoolean(() => window.Properties.IsOffscreen.ValueOrDefault)))
                .Where(item => item.Handle > 0 && !item.Offscreen)
                .OrderByDescending(item => item.Focused).Take(UiWindowLimit).ToArray();
            var observedWindows = new List<object>();
            var observedElements = new List<object>();
            var truncated = allWindows.Length > UiWindowLimit;
            foreach (var item in windows)
            {
                var windowId = NewTargetId();
                var processName = SafeProcessName(item.ProcessId);
                var sessionId = SafeSessionId(item.ProcessId);
                uiTargets[windowId] = new(item.Window, item.Handle, item.ProcessId, sessionId, "Window",
                    DateTime.UtcNow.AddSeconds(UiTargetLifetimeSeconds));
                observedWindows.Add(new
                {
                    windowId,
                    title = Clip(item.Name, 128),
                    process = processName,
                    processId = item.ProcessId,
                    sessionId,
                    focused = item.Focused,
                    allowedActions = new[] { "activate-window" },
                    dialog = SafeBoolean(() => item.Window.Properties.IsDialog.ValueOrDefault),
                    enabled = SafeBoolean(() => item.Window.Properties.IsEnabled.ValueOrDefault)
                });

                var nodes = SafeDescendants(item.Window, UiElementLimitPerWindow + 1, UiTreeDepthLimit, out var treeTruncated);
                if (nodes.Count > UiElementLimitPerWindow || treeTruncated) truncated = true;
                var elementIds = new Dictionary<AutomationElement, string>();
                foreach (var node in nodes.Take(UiElementLimitPerWindow))
                {
                    var controlType = SafeControlType(node.Element);
                    var isPassword = SafeBoolean(() => node.Element.Properties.IsPassword.ValueOrDefault);
                    var enabled = SafeBoolean(() => node.Element.Properties.IsEnabled.ValueOrDefault);
                    var offscreen = SafeBoolean(() => node.Element.Properties.IsOffscreen.ValueOrDefault);
                    var allowed = ComputerUsePolicy.IsReadOnlyDesktopApplication(processName)
                        ? Array.Empty<string>()
                        : GetAllowedUiActions(node.Element, controlType, enabled, offscreen, isPassword);
                    var elementId = NewTargetId();
                    elementIds[node.Element] = elementId;
                    uiTargets[elementId] = new(node.Element, item.Handle, item.ProcessId, sessionId, controlType,
                        DateTime.UtcNow.AddSeconds(UiTargetLifetimeSeconds));
                    observedElements.Add(new
                    {
                        elementId,
                        windowId,
                        parentId = node.Parent is not null && elementIds.TryGetValue(node.Parent, out var parentId) ? parentId : windowId,
                        depth = node.Depth,
                        controlType,
                        name = isPassword ? "[redacted]" : Clip(SafeName(node.Element), 96),
                        automationId = Clip(SafeAutomationId(node.Element), 80),
                        enabled,
                        offscreen,
                        focused = SafeBoolean(() => node.Element.Properties.HasKeyboardFocus.ValueOrDefault),
                        isPassword,
                        bounds = SafeBounds(node.Element),
                        allowedActions = allowed,
                        approvalRequiredActions = GetApprovalActions(controlType)
                    });
                }
            }
            return new
            {
                observationStatus = truncated ? "partial" : windows.Length == 0 ? "empty" : "available",
                capturedAt = DateTimeOffset.UtcNow,
                interactiveSessionId = Process.GetCurrentProcess().SessionId,
                desktop = DesktopSessionDiagnostics.ReadThreadDesktopName(),
                topLevelWindowCount = allWindows.Length,
                windows = observedWindows,
                elements = observedElements,
                truncated,
                targetExpiresInSeconds = UiTargetLifetimeSeconds
            };
        }
        catch (Exception error)
        {
            uiTargets.Clear();
            return new { observationStatus = "unavailable", error = "uia_observation_failed", errorType = error.GetType().Name,
                windows = Array.Empty<object>(), elements = Array.Empty<object>(), truncated = false };
        }
    }

    private object InteractWithUi(DesktopCompanionProtocol.Request request)
    {
        if (request.TargetId is null || !uiTargets.TryGetValue(request.TargetId, out var target))
            return new { succeeded = false, status = "target_not_found", verification = "not_run" };
        if (target.ExpiresAt < DateTime.UtcNow)
        {
            uiTargets.Remove(request.TargetId);
            return new { succeeded = false, status = "target_expired", verification = "not_run" };
        }
        if (target.SessionId != Process.GetCurrentProcess().SessionId)
            return new { succeeded = false, status = "wrong_interactive_session", verification = "not_run" };
        var targetProcessName = SafeProcessName(target.ProcessId);
        if (!ComputerUsePolicy.CanInteractWithApplication(targetProcessName, request.Action))
            return new { succeeded = false, status = "read_only_application", verification = "not_run" };
        if (!IsWindowStillPresent(target.WindowHandle, target.ProcessId) ||
            !IsElementBoundToWindow(target.Element, target.WindowHandle, target.ProcessId) ||
            SafeSessionId(target.ProcessId) != target.SessionId)
        {
            uiTargets.Remove(request.TargetId);
            return new { succeeded = false, status = "target_stale", verification = "not_run" };
        }
        var currentControlType = SafeControlType(target.Element);
        if (currentControlType == "Unknown" || currentControlType != target.ControlType)
        {
            uiTargets.Remove(request.TargetId);
            return new { succeeded = false, status = "target_stale", verification = "not_run" };
        }
        if (!TryReadBoolean(() => target.Element.Properties.IsEnabled.ValueOrDefault, out var currentlyEnabled) ||
            !TryReadBoolean(() => target.Element.Properties.IsOffscreen.ValueOrDefault, out var currentlyOffscreen))
            return new { succeeded = false, status = "target_state_unavailable", verification = "not_run" };
        if (!currentlyEnabled || currentlyOffscreen)
            return new { succeeded = false, status = "target_not_interactable", verification = "not_run" };
        var passwordKnown = TryReadSupportedBoolean(() =>
        {
            var supported = target.Element.Properties.IsPassword.TryGetValue(out var value);
            return (supported, value);
        }, out var isPassword);
        if (request.Action == "type-text" && !passwordKnown)
            return new { succeeded = false, status = "target_security_state_unavailable", verification = "not_run" };
        if (request.Action == "type-text" && isPassword)
            return new { succeeded = false, status = "password_input_denied", verification = "not_run" };
        var freshAllowedActions = currentControlType == "Window"
            ? new[] { "activate-window" }
            : GetAllowedUiActions(target.Element, currentControlType, currentlyEnabled,
                currentlyOffscreen, passwordKnown ? isPassword : true);
        if (!freshAllowedActions.Contains(request.Action, StringComparer.Ordinal) ||
            (request.Action == "press-key" && !ComputerUsePolicy.CanPressKeyOn(currentControlType, request.Value)))
            return new { succeeded = false,
                status = IsApprovalRequired(currentControlType, request.Action, request.Value) ? "approval_required" : "action_not_allowed",
                verification = "not_run" };
        try
        {
            var verified = false;
            switch (request.Action)
            {
                case "activate-window":
                    target.Element.Focus();
                    verified = GetForegroundWindow() == target.WindowHandle;
                    break;
                case "select-element":
                    target.Element.Patterns.SelectionItem.Pattern.Select();
                    verified = target.Element.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault;
                    break;
                case "click-element":
                    target.Element.Click();
                    break;
                case "double-click-element":
                    return new { succeeded = false, status = "approval_required", verification = "not_run" };
                case "type-text":
                    var setStatus = ComputerUsePolicy.TrySetProtectedTextValue(
                        SafeControlType(target.Element) == "Edit",
                        () => target.Element.Patterns.Value.IsSupported,
                        () =>
                        {
                            var supported = target.Element.Properties.IsPassword.TryGetValue(out var value);
                            return (supported, value);
                        },
                        () =>
                        {
                            var supported = target.Element.Patterns.Value.Pattern.IsReadOnly.TryGetValue(out var value);
                            return (supported, value);
                        },
                        value => target.Element.Patterns.Value.Pattern.SetValue(value),
                        request.Value!);
                    if (setStatus != "set") return new { succeeded = false, status = setStatus, verification = "not_run" };
                    verified = string.Equals(target.Element.Patterns.Value.Pattern.Value.Value, request.Value, StringComparison.Ordinal);
                    break;
                case "scroll-element":
                    var scroll = target.Element.Patterns.Scroll.Pattern;
                    var before = scroll.VerticalScrollPercent.ValueOrDefault;
                    scroll.Scroll(ScrollAmount.NoAmount, request.Direction == "down" ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement);
                    var after = scroll.VerticalScrollPercent.ValueOrDefault;
                    verified = Math.Abs(after - before) > 0.01;
                    break;
                case "press-key":
                    target.Element.Focus();
                    if (GetForegroundWindow() != target.WindowHandle ||
                        !SafeBoolean(() => target.Element.Properties.HasKeyboardFocus.ValueOrDefault))
                        return new { succeeded = false, status = "target_focus_not_verified", verification = "not_run" };
                    var key = request.Value switch
                    {
                        "ArrowUp" => VirtualKeyShort.UP,
                        "ArrowDown" => VirtualKeyShort.DOWN,
                        "ArrowLeft" => VirtualKeyShort.LEFT,
                        "ArrowRight" => VirtualKeyShort.RIGHT,
                        "Home" => VirtualKeyShort.HOME,
                        "End" => VirtualKeyShort.END,
                        "PageUp" => VirtualKeyShort.PRIOR,
                        "PageDown" => VirtualKeyShort.NEXT,
                        _ => throw new InvalidOperationException("Key is not on the navigation allowlist.")
                    };
                    ComputerUsePolicy.PressAndRelease(key, value => Keyboard.Press(value), value => Keyboard.Release(value));
                    verified = false;
                    break;
                default:
                    return new { succeeded = false, status = "action_not_allowed", verification = "not_run" };
            }
            var refreshed = ObserveDesktopUi();
            return new { succeeded = true, status = "performed", verification = ComputerUsePolicy.VerificationStatus(verified),
                targetInvalidated = true, refreshedObservation = refreshed };
        }
        catch (Exception error)
        {
            uiTargets.Remove(request.TargetId);
            return new { succeeded = false, status = "action_failed", errorType = error.GetType().Name,
                verification = "not_verified", targetInvalidated = true };
        }
    }

    private static IReadOnlyList<(AutomationElement Element, AutomationElement? Parent, int Depth)> SafeDescendants(
        AutomationElement root, int limit, int depthLimit, out bool truncated)
    {
        var nodes = ComputerUsePolicy.CollectBoundedDescendants(root, limit, depthLimit,
            (parent, index) => parent.FindChildAt(index), out truncated);
        return nodes.Select(node => (node.Element, ReferenceEquals(node.Parent, root) ? null : node.Parent, node.Depth)).ToArray();
    }

    private static string[] GetAllowedUiActions(AutomationElement element, string controlType,
        bool enabled, bool offscreen, bool isPassword)
    {
        try
        {
            var valueSupported = element.Patterns.Value.IsSupported;
            var valueReadOnly = valueSupported && element.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault;
            return ComputerUsePolicy.AllowedUiActions(controlType, enabled, offscreen, isPassword,
                element.Patterns.Invoke.IsSupported, element.Patterns.SelectionItem.IsSupported,
                valueSupported, valueReadOnly, element.Patterns.Scroll.IsSupported);
        }
        catch { return []; }
    }

    private static string[] GetApprovalActions(string controlType) => controlType switch
    {
        "Button" or "MenuItem" => ["click-element"],
        "CheckBox" or "RadioButton" => ["click-element", "press-key"],
        "ListItem" or "TreeItem" => ["select-element", "double-click-element", "press-key"],
        "ComboBox" => ["press-key"],
        _ => []
    };

    private static bool IsApprovalRequired(string controlType, string action, string? key = null) =>
        ComputerUsePolicy.RequiresUiApproval(controlType, action, key);

    private bool IsWindowStillPresent(nint handle, int expectedProcessId)
    {
        try
        {
            return uiAutomation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window))
                .Any(window => SafeNativeWindowHandle(window) == handle && SafeProcessId(window) == expectedProcessId);
        }
 …6227 tokens truncated…indow_not_found" :
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
            var merged = pull.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind == JsonValueKind.String;
            var headBranch = pull.TryGetProperty("head", out var headRef) && headRef.TryGetProperty("ref", out var headRefName)
                ? headRefName.GetString() : null;
            var baseBranch = pull.TryGetProperty("base", out var baseRef) && baseRef.TryGetProperty("ref", out var baseRefName)
                ? baseRefName.GetString() : null;
            var sha = pull.TryGetProperty("head", out var head) && head.TryGetProperty("sha", out var shaElement)
                ? shaElement.GetString() : null;
            var checksStatus = "unavailable";
            object[] checks = [];
            var totalChecks = 0;
            var checksTruncated = false;
            var failedChecks = 0;
            string[] failedCheckNames = [];
            if (sha is { Length: >= 40 })
            {
                var summary = await ReadAllCheckRunsAsync(repoPath, sha, cancellationToken);
                if (summary is not null)
                {
                    checksStatus = summary.Status;
                    totalChecks = summary.TotalCount;
                    checksTruncated = summary.Truncated;
                    failedChecks = summary.FailureCount;
                    failedCheckNames = summary.FailureNames;
                    checks = summary.Checks.Select(run => (object)new
                    {
                        name = run.Name, status = run.Status, conclusion = run.Conclusion
                    }).ToArray();
                }
            }
            pulls.Add(new { number = n, title = title.GetString()![..Math.Min(180, title.GetString()!.Length)], state, merged,
                headBranch, baseBranch, headSha = sha is { Length: >= 8 } ? sha[..8] : null, checksStatus, totalChecks,
                checksTotalCount = totalChecks,
                displayedChecks = checks.Length, checksTruncated, failedChecks, failedCheckNames, checks });
        }
        return new { repository = repoPath, pullRequests = pulls };
    }

    private static async Task<DesktopGithubCheckSummary?> ReadAllCheckRunsAsync(string repository, string sha,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        const int maximumPages = 10;
        var all = new List<JsonElement>();
        var expectedTotal = 0;
        var incomplete = false;
        for (var page = 1; page <= maximumPages; page++)
        {
            using var response = await Github.GetAsync(
                $"repos/{repository}/commits/{sha}/check-runs?per_page={pageSize}&page={page}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("check_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
                return null;
            if (document.RootElement.TryGetProperty("total_count", out var total) && total.TryGetInt32(out var parsedTotal))
                expectedTotal = Math.Max(expectedTotal, parsedTotal);
            foreach (var run in runs.EnumerateArray()) all.Add(run.Clone());
            if (runs.GetArrayLength() < pageSize || all.Count >= expectedTotal) break;
            if (page == maximumPages) incomplete = true;
        }
        return DesktopGithubChecks.Summarize(all, expectedTotal == 0 ? all.Count : expectedTotal, incomplete);
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

