using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Uses the Microsoft WinApp UI Automation library inside the existing authenticated Companion.</summary>
internal sealed class CompanionDesktopUi
{
    private const int MaximumElements = 120;
    private const int InspectDepth = 8;
    private static readonly TimeSpan ObservationLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, CachedObservation> observations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedWindow> selectableWindows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int ProcessId, DateTime ProcessStartUtc)> requestedAppProcesses =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedObservation(string App, int ProcessId, IntPtr WindowHandle, string WindowTitle,
        DateTimeOffset CreatedAt, bool Complete, IReadOnlyDictionary<string, UiElement> Elements);
    private sealed record CachedWindow(string App, int ProcessId, IntPtr WindowHandle, string Title, DateTimeOffset CreatedAt);
    private sealed record ElementView(string Id, string Type, string Name, bool Enabled, bool Offscreen,
        bool Invokable, string? ScrollDirection);

    public string? RegisterSelectableWindow(string app, int processId, IntPtr handle, string title,
        string? requestedAppName = null)
    {
        if (!ComputerUseUiPlanner.AllowedApps.Contains(app, StringComparer.Ordinal) || processId <= 0 ||
            !IsWindow(handle) || !WindowBelongsToProcess(handle, processId) ||
            ComputerUseUiPlanner.IsProtectedSurface(title)) return null;
        if (!string.IsNullOrWhiteSpace(requestedAppName))
        {
            if (!ComputerUseUiPlanner.IsSafeInstalledApplicationName(requestedAppName)) return null;
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!ComputerUseUiPlanner.CanObserveDesktopProcess(process.ProcessName) ||
                    process.SessionId != Process.GetCurrentProcess().SessionId) return null;
                requestedAppProcesses[requestedAppName] = (processId, process.StartTime.ToUniversalTime());
                if (requestedAppProcesses.Count > 64)
                    foreach (var stale in requestedAppProcesses.Keys.Take(requestedAppProcesses.Count - 64))
                        requestedAppProcesses.TryRemove(stale, out _);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception or UnauthorizedAccessException or NotSupportedException or
                System.Security.SecurityException)
            {
                return null;
            }
        }
        var id = Guid.NewGuid().ToString("N");
        selectableWindows[id] = new CachedWindow(app, processId, handle, title, DateTimeOffset.UtcNow);
        PruneSelectableWindows();
        return id;
    }

    public async Task<object> ExecuteAsync(ComputerUseUiAction action, IReadOnlyList<int> processIds,
        bool userConfirmed, CancellationToken cancellationToken)
    {
        if (action.Operation == "launch") return new { error = "launch_requires_companion_dispatch" };
        if (action.Operation == "observe")
        {
            var targetedProcessIds = action.App == "desktop" && action.Text is not null
                ? processIds.Where(processId => IsRequestedProcess(processId, action.Text)).ToArray()
                : processIds;
            return await ObserveAsync(action.App, targetedProcessIds, cancellationToken);
        }
        if (action.Operation == "activate-window") return await ActivateWindowAsync(action, processIds, cancellationToken);
        if (action.ObservationId is null || action.ElementId is null ||
            !observations.TryGetValue(action.ObservationId, out var cached) || cached.App != action.App ||
            DateTimeOffset.UtcNow - cached.CreatedAt > ObservationLifetime)
            return new { status = "stale_observation", actionVerified = false };
        if (!processIds.Contains(cached.ProcessId)) return new { status = "app_process_not_approved", actionVerified = false };
        if (!cached.Complete) return new { status = "observation_incomplete", actionVerified = false };
        if (!cached.Elements.TryGetValue(action.ElementId, out var prior))
            return new { status = "element_not_observed", actionVerified = false };
        if (!IsWindow(cached.WindowHandle) || !WindowBelongsToProcess(cached.WindowHandle, cached.ProcessId))
            return new { status = "window_changed", actionVerified = false };

        var automation = CompanionUiAutomation.Api;
        var target = UiTarget.FromWindowHandle(cached.WindowHandle);
        var current = await automation.InspectAsync(target, null, InspectDepth, cancellationToken);
        var liveElements = current.Where(item => item.Id is not null && !ComputerUseUiPlanner.IsSensitiveName(item.Name))
            .ToDictionary(item => item.Id!, StringComparer.Ordinal);
        if (!SameSnapshot(cached.Elements, liveElements))
            return new { status = "stale_observation", actionVerified = false };
        var element = current.FirstOrDefault(item => item.Id == action.ElementId);
        if (element is null || !SameIdentity(prior, element))
            return new { status = "stale_element", actionVerified = false };
        if (element.IsOffscreen) return new { status = "element_offscreen", actionVerified = false };
        if (ComputerUseUiPlanner.IsProtectedSurface(cached.WindowTitle) ||
            ComputerUseUiPlanner.IsProtectedSurface(element.Name))
            return new { status = "protected_surface", actionVerified = false };

        var classification = ComputerUseUiPlanner.ClassifyAction(action.Operation, element.Type, element.Name ?? string.Empty,
            element.ScrollDir);
        if (classification == "denied" || classification == "confirmation_required" && !userConfirmed ||
            userConfirmed && (classification != "confirmation_required" || action.Operation != "invoke"))
            return new { status = classification, actionVerified = false, elementType = element.Type,
                elementName = ComputerUseUiPlanner.IsSensitiveName(element.Name) ? "[redacted]" : Limit(element.Name, 100) };

        switch (action.Operation)
        {
            case "focus":
            {
                await automation.FocusAsync(target, element, cancellationToken);
                var focused = await automation.GetFocusedElementAsync(target, cancellationToken);
                if (focused is null || !MatchesFocusedElement(element, focused))
                    return new { status = "focus_not_verified", actionVerified = false };
                break;
            }
            case "scroll":
            {
                var scrollDirections = element.ScrollDir;
                if (string.IsNullOrWhiteSpace(scrollDirections))
                    return new { status = "not_scrollable", actionVerified = false };
                if ((action.Direction is "up" or "down") && !scrollDirections.Contains('v') ||
                    (action.Direction is "left" or "right") && !scrollDirections.Contains('h'))
                    return new { status = "scroll_direction_not_supported", actionVerified = false };
                await automation.ScrollContainerAsync(target, element, action.Direction!, null, cancellationToken);
                break;
            }
            case "invoke":
            {
                var requestedAction = element.Type is "TabItem" or "TreeItem"
                    ? UiInvokeAction.Select : UiInvokeAction.Invoke;
                var invokeResult = await automation.InvokeAsync(target, element, requestedAction, cancellationToken);
                if (!string.Equals(invokeResult.PerformedAction, requestedAction.ToString().ToLowerInvariant(), StringComparison.Ordinal))
                    return new { status = "ui_action_not_verified", actionVerified = false };
                await Task.Delay(250, cancellationToken);
                break;
            }
            case "type":
            {
                if (!await IsSafeEditableAsync(automation, target, element, action.App, cancellationToken))
                    return new { status = "text_target_not_allowed", actionVerified = false };
                await automation.SetValueAsync(target, element, action.Text!, cancellationToken);
                var entered = await automation.GetTextAsync(target, element, cancellationToken);
                if (!string.Equals(entered, action.Text, StringComparison.Ordinal))
                    return new { status = "text_not_verified", actionVerified = false };
                break;
            }
            default:
                return new { status = "operation_not_allowed", actionVerified = false };
        }

        var after = await ObserveWindowAsync(action.App, cached.ProcessId, cached.WindowHandle,
            cached.WindowTitle, cancellationToken);
        var verified = after is not null && ReadObservationComplete(after);
        return new
        {
            status = verified ? "verified" : "result_not_verified",
            actionVerified = verified,
            operation = action.Operation,
            app = action.App,
            observation = after
        };
    }

    public static IReadOnlyList<(IntPtr Handle, int ProcessId, string Title)> FindAppWindows(string app,
        IReadOnlyList<int> processIds)
    {
        var automation = CompanionUiAutomation.Api;
        var matches = new List<(IntPtr Handle, int ProcessId, string Title)>();
        foreach (var processId in processIds.Distinct())
        {
            try
            {
                foreach (var (handle, pid, title) in automation.FindWindowsByPid(processId))
                    if (handle != IntPtr.Zero && pid == processId && IsWindowVisible(handle) && IsWindow(handle) &&
                        !string.IsNullOrWhiteSpace(title))
                        matches.Add((handle, pid, title));
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or COMException)
            {
                // Window enumeration is best-effort; callers distinguish unavailable/ambiguous targets.
            }
        }
        return matches;
    }

    private async Task<object> ObserveAsync(string app, IReadOnlyList<int> processIds, CancellationToken cancellationToken)
    {
        var windows = FindAppWindows(app, processIds);
        if (windows.Count == 0) return new { status = "window_not_found", app, actionVerified = false };
        if (windows.Count > 32) return new { status = "window_selection_incomplete", app, actionVerified = false };
        var foreground = GetForegroundWindow();
        var selected = windows.FirstOrDefault(window => window.Handle == foreground);
        if (selected.Handle == IntPtr.Zero)
        {
            if (windows.Count != 1)
            {
                var safeWindows = windows.Where(window => !ComputerUseUiPlanner.IsProtectedSurface(window.Title)).ToArray();
                if (safeWindows.Length == 0) return new { status = "protected_surface", app, actionVerified = false };
                var candidates = safeWindows.Select(window =>
                {
                    var id = Guid.NewGuid().ToString("N");
                    selectableWindows[id] = new CachedWindow(app, window.ProcessId, window.Handle, window.Title, DateTimeOffset.UtcNow);
                    return new { windowId = id, title = Limit(window.Title, 100) };
                }).ToArray();
                PruneSelectableWindows();
                return new { status = "window_selection_required", app, actionVerified = false, windows = candidates };
            }
            selected = windows[0];
        }
        return await ObserveWindowAsync(app, selected.ProcessId, selected.Handle, selected.Title, cancellationToken)
            ?? (object)new { status = "observation_unavailable", app, actionVerified = false };
    }

    private bool IsRequestedProcess(int processId, string requestedApp)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (requestedAppProcesses.TryGetValue(requestedApp, out var trustedProcess) &&
                trustedProcess.ProcessId == processId &&
                trustedProcess.ProcessStartUtc == process.StartTime.ToUniversalTime() &&
                ComputerUseUiPlanner.CanObserveDesktopProcess(process.ProcessName)) return true;
            return ComputerUseUiPlanner.IsRequestedProcessTarget(requestedApp, process.ProcessName);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                      System.ComponentModel.Win32Exception or NotSupportedException or
                                      System.Security.SecurityException)
        {
            return false;
        }
    }

    private async Task<object> ActivateWindowAsync(ComputerUseUiAction action, IReadOnlyList<int> processIds,
        CancellationToken cancellationToken)
    {
        if (action.WindowId is null || !selectableWindows.TryRemove(action.WindowId, out var selected) ||
            selected.App != action.App || DateTimeOffset.UtcNow - selected.CreatedAt > ObservationLifetime ||
            !processIds.Contains(selected.ProcessId) || !IsWindow(selected.WindowHandle) ||
            !WindowBelongsToProcess(selected.WindowHandle, selected.ProcessId))
            return new { status = "window_selection_stale", actionVerified = false };

        _ = SetForegroundWindow(selected.WindowHandle);
        await Task.Delay(150, cancellationToken);
        if (GetForegroundWindow() != selected.WindowHandle)
            return new { status = "window_activation_not_verified", actionVerified = false };
        var observation = await ObserveWindowAsync(action.App, selected.ProcessId, selected.WindowHandle,
            selected.Title, cancellationToken);
        if (observation is null) return new { status = "observation_unavailable", actionVerified = false };
        var verified = ReadObservationComplete(observation);
        return new { status = verified ? "verified" : "observation_incomplete", actionVerified = verified,
            operation = action.Operation, app = action.App, observation };
    }

    private async Task<object?> ObserveWindowAsync(string app, int processId, IntPtr handle, string title,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(handle) || !WindowBelongsToProcess(handle, processId)) return null;
        if (ComputerUseUiPlanner.IsProtectedSurface(title))
            return new { status = "protected_surface", app, actionVerified = false };

        var target = UiTarget.FromWindowHandle(handle);
        var found = (await CompanionUiAutomation.Api.InspectAsync(target, null, InspectDepth, cancellationToken)).ToArray();
        var hasPasswordControl = false;
        foreach (var element in found.Take(MaximumElements))
        {
            if (ComputerUseUiPlanner.IsProtectedSurface(element.Name) || ComputerUseUiPlanner.IsSensitiveName(element.Name))
            {
                hasPasswordControl = true;
                break;
            }
            if ((element.Type is "Edit" or "ComboBox") &&
                await IsPasswordAsync(target, element, cancellationToken) != false)
            {
                hasPasswordControl = true;
                break;
            }
        }
        if (hasPasswordControl) return new { status = "protected_surface", app, actionVerified = false };

        var included = found.Take(MaximumElements).Where(element =>
                element.Id is not null && !ComputerUseUiPlanner.IsSensitiveName(element.Name))
            .ToArray();
        var complete = found.Length <= MaximumElements && !found.Any(element => element.HasMoreChildren == true) &&
            found.All(element => element.Id is not null);
        var observationId = Guid.NewGuid().ToString("N");
        var mapped = included.ToDictionary(element => element.Id!, StringComparer.Ordinal);
        var snapshot = new CachedObservation(app, processId, handle, title, DateTimeOffset.UtcNow, complete, mapped);
        observations[observationId] = snapshot;
        PruneObservations();
        var elements = included.Select(element => new ElementView(element.Id!, element.Type,
            Limit(element.Name, 120), element.IsEnabled, element.IsOffscreen, element.IsInvokable,
            element.ScrollDir)).ToArray();
        return new
        {
            status = complete ? "observed" : "observation_incomplete",
            app,
            observationId,
            windowTitle = Limit(title, 120),
            complete,
            actionVerified = false,
            elements
        };
    }

    private static async Task<bool> IsSafeEditableAsync(IUiAutomation automation, UiTarget target, UiElement element,
        string app, CancellationToken cancellationToken)
    {
        if (!ComputerUseUiPlanner.IsSafeTypingSurface(app, element.Type, element.Name ?? string.Empty)) return false;
        var properties = await automation.GetPropertiesAsync(target, element, null, cancellationToken);
        var password = ReadBooleanProperty(properties, "IsPassword");
        var readOnly = ReadBooleanProperty(properties, "IsReadOnly");
        return password == false && readOnly == false && !ComputerUseUiPlanner.IsSensitiveName(element.Name) &&
            !ComputerUseUiPlanner.IsProtectedSurface(element.Name);
    }

    private static async Task<bool?> IsPasswordAsync(UiTarget target, UiElement element, CancellationToken cancellationToken)
    {
        var properties = await CompanionUiAutomation.Api.GetPropertiesAsync(target, element, null, cancellationToken);
        return ReadBooleanProperty(properties, "IsPassword");
    }

    private static bool? ReadBooleanProperty(IReadOnlyDictionary<string, object?> properties, string name)
    {
        var value = properties.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        if (value is bool boolean) return boolean;
        return value is not null && bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static bool SameIdentity(UiElement left, UiElement right) =>
        left.Type == right.Type && left.Name == right.Name &&
        left.AutomationId == right.AutomationId && left.Selector == right.Selector;

    private static bool MatchesFocusedElement(UiElement requested, UiElement focused) =>
        requested.Type == focused.Type && requested.Name == focused.Name &&
        (requested.AutomationId is null || focused.AutomationId is null || requested.AutomationId == focused.AutomationId) &&
        (requested.Selector is null || focused.Selector is null || requested.Selector == focused.Selector);

    private static bool SameSnapshot(IReadOnlyDictionary<string, UiElement> expected,
        IReadOnlyDictionary<string, UiElement> actual) => expected.Count == actual.Count &&
        expected.All(item => actual.TryGetValue(item.Key, out var current) && SameIdentity(item.Value, current));

    private static bool ReadObservationComplete(object? observation)
    {
        if (observation is null) return false;
        try
        {
            using var json = System.Text.Json.JsonSerializer.SerializeToDocument(observation);
            return json.RootElement.TryGetProperty("complete", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch { return false; }
    }

    private void PruneObservations()
    {
        var cutoff = DateTimeOffset.UtcNow - ObservationLifetime;
        foreach (var item in observations.Where(item => item.Value.CreatedAt < cutoff).ToArray())
            observations.TryRemove(item.Key, out _);
        if (observations.Count <= 16) return;
        foreach (var item in observations.OrderBy(item => item.Value.CreatedAt).Take(observations.Count - 16))
            observations.TryRemove(item.Key, out _);
    }

    private void PruneSelectableWindows()
    {
        foreach (var item in selectableWindows.Where(item => DateTimeOffset.UtcNow - item.Value.CreatedAt > ObservationLifetime))
            selectableWindows.TryRemove(item.Key, out _);
        while (selectableWindows.Count > 32)
        {
            var oldest = selectableWindows.MinBy(item => item.Value.CreatedAt);
            if (oldest.Key is null || !selectableWindows.TryRemove(oldest.Key, out _)) break;
        }
    }

    private static string Limit(string? value, int length) => string.IsNullOrWhiteSpace(value)
        ? "" : value.Length <= length ? value : value[..length];

    private static bool WindowBelongsToProcess(IntPtr handle, int processId)
    {
        _ = GetWindowThreadProcessId(handle, out var actualProcessId);
        return actualProcessId == processId;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr handle);
}
