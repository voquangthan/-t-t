using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    checks++;
    Console.WriteLine($"PASS: {name}");
}

Check(DesktopOperator.IsAvailable(true, false, IPAddress.Loopback), "local Windows loopback is allowed");
Check(!DesktopOperator.IsAvailable(true, true, IPAddress.Loopback), "public-origin mode is denied");
Check(!DesktopOperator.IsAvailable(true, false, IPAddress.Parse("192.0.2.2")), "non-loopback caller is denied");
Check(!DesktopOperator.IsAvailable(false, false, IPAddress.Loopback), "non-Windows host is denied");
Check(!DesktopOperator.OpenKnownApplication("arbitrary" ).Succeeded, "unlisted applications are denied");
var verifiedOpenResponse = DesktopOperator.ToChatActionResponse(
    new DesktopOperator.ActionResult(true, "Opened and verified.", true), "open-vscode");
Check(verifiedOpenResponse.WindowVerified == true && verifiedOpenResponse.ToolUsed &&
      verifiedOpenResponse.ComputerUseAction == "open-vscode",
    "chat response preserves verified application-window status");
var unverifiedOpenResponse = DesktopOperator.ToChatActionResponse(
    new DesktopOperator.ActionResult(true, "Launch accepted; window not verified.", false), "open-vscode");
Check(unverifiedOpenResponse.WindowVerified == false,
    "chat response distinguishes an accepted launch from an unverified window");
var nonWindowActionResponse = DesktopOperator.ToChatActionResponse(
    new DesktopOperator.ActionResult(true, "Read complete."), "read-workspace");
Check(nonWindowActionResponse.WindowVerified is null,
    "chat response preserves unavailable window-verification status for other actions");
Check(!WindowsDesktopAutomation.HasVisibleWindow("arbitrary"), "UI Automation cannot inspect unlisted process names");
Check(ComputerUsePolicy.CanObserve("approved-applications"), "approved application observation has its own permission");
Check(ComputerUsePolicy.CanObserve("github-pull-requests"), "GitHub observation has its own permission");
Check(ComputerUsePolicy.CanObserve("github-account") &&
      JsonSerializer.Serialize(ComputerUsePolicy.Describe()).Contains("github-account", StringComparison.Ordinal),
    "GitHub account observation is available only through the read-only Companion action");
Check(!ComputerUsePolicy.CanObserve("desktop-screenshot"), "unlisted observation targets are denied");
Check(ComputerUsePolicy.CanObserve("desktop-ui"), "general desktop UI observation is explicitly allowed");
Check(ComputerUseChatIntent.DetectDirectRequest("Quan sát giao diện") == "read-desktop-ui" &&
      ComputerUseChatIntent.ObservationTargetForAction("read-desktop-ui") == "desktop-ui",
    "a bounded natural-language request reaches the general UI observation action");
var emptyApplicationObservation = WindowsDesktopAutomation.CreateApplicationObservation([], available: true, partial: false);
Check(emptyApplicationObservation.Status == "empty" && emptyApplicationObservation.Applications.Count == 0,
    "a successful empty desktop observation is distinct from an error");
var partialApplicationObservation = WindowsDesktopAutomation.CreateApplicationObservation(
    ["Visual Studio Code"], available: true, partial: true);
Check(partialApplicationObservation.Status == "partial" &&
      partialApplicationObservation.Applications.SequenceEqual(["Visual Studio Code"]),
    "partial desktop observations preserve discovered apps and disclose incomplete coverage");
var unavailableApplicationObservation = WindowsDesktopAutomation.CreateApplicationObservation(
    ["Visual Studio Code"], available: false, partial: false);
Check(unavailableApplicationObservation.Status == "unavailable" &&
      unavailableApplicationObservation.Applications.Count == 0,
    "UI Automation failure is represented as unavailable, never as a successful empty result");
var unavailableApplicationMessage = WindowsDesktopAutomation.FormatApplicationObservation(unavailableApplicationObservation);
Check(unavailableApplicationMessage.Status == "unavailable" && unavailableApplicationMessage.Error is not null &&
      unavailableApplicationMessage.Reply is null &&
      !unavailableApplicationMessage.Error.Contains("không thấy", StringComparison.OrdinalIgnoreCase),
    "an unavailable UIA observation becomes an error instead of a no-apps claim");
var partialEmptyMessage = WindowsDesktopAutomation.FormatApplicationObservation(
    WindowsDesktopAutomation.CreateApplicationObservation([], available: true, partial: true));
Check(partialEmptyMessage.Status == "partial" && partialEmptyMessage.Reply is not null &&
      partialEmptyMessage.Reply.Contains("không thể kết luận danh sách trống", StringComparison.OrdinalIgnoreCase),
    "a partial empty result does not claim that no applications are open");
var successfulEmptyMessage = WindowsDesktopAutomation.FormatApplicationObservation(emptyApplicationObservation);
Check(successfulEmptyMessage.Status == "empty" && successfulEmptyMessage.Reply is not null &&
      successfulEmptyMessage.Reply.Contains("không thấy ứng dụng nào", StringComparison.OrdinalIgnoreCase),
    "only a successful complete empty observation may report no apps");
Check(ComputerUsePolicy.CanNavigate("open-vscode"), "allowlisted application navigation has its own permission");
Check(ComputerUsePolicy.CanNavigate("open-settings") && ComputerUsePolicy.CanNavigate("open-task-manager") &&
      ComputerUsePolicy.CanNavigate("open-word") && ComputerUsePolicy.CanNavigate("open-excel") &&
      ComputerUsePolicy.CanNavigate("open-powerpoint") && ComputerUsePolicy.CanNavigate("open-pdf-reader"),
    "only fixed Windows and document app launch actions are allowlisted");
Check(ComputerUsePolicy.IsReadOnlyDesktopApplication("SystemSettings") &&
      ComputerUsePolicy.IsReadOnlyDesktopApplication("Taskmgr") &&
      !ComputerUsePolicy.CanInteractWithApplication("SystemSettings", "type-text") &&
      !ComputerUsePolicy.CanInteractWithApplication("Taskmgr", "click-element") &&
      ComputerUsePolicy.CanInteractWithApplication("SystemSettings", "activate-window") &&
      ComputerUsePolicy.CanInteractWithApplication("WINWORD", "type-text"),
    "Settings and Task Manager stay observation-only while app activation remains available");
Check(ComputerUsePolicy.CanInteract("press-key") && !ComputerUsePolicy.CanInteract("run-shell"),
    "UI interaction exposes a fixed key action while arbitrary commands stay denied");
Check(ComputerUsePolicy.CanInteract("expand-element") && ComputerUsePolicy.CanInteract("collapse-element") &&
      !ComputerUsePolicy.RequiresUiApproval("MenuItem", "expand-element") &&
      ComputerUsePolicy.RequiresUiApproval("MenuItem", "click-element"),
    "menu expansion is a bounded navigation action while selecting menu commands stays gated");
Check(!ComputerUsePolicy.CanNavigate("run-shell"), "navigation cannot be used to run shell commands");
Check(!ComputerUsePolicy.CanWrite("edit-file"), "write actions remain hard-blocked");
Check(!ComputerUsePolicy.CanWrite("run-tests"), "project code execution remains hard-blocked");
var safeUiActions = ComputerUsePolicy.AllowedUiActions("Hyperlink", true, false, false,
    supportsInvoke: true, supportsSelection: false, supportsValue: false, valueReadOnly: false, supportsScroll: false);
Check(safeUiActions.SequenceEqual(["click-element"]) &&
      !ComputerUsePolicy.AllowedUiActions("RadioButton", true, false, false,
          supportsInvoke: true, supportsSelection: true, supportsValue: false, valueReadOnly: false, supportsScroll: false)
          .Contains("press-key", StringComparer.Ordinal) &&
      ComputerUsePolicy.CanInteractWithApplication("WINWORD", "press-key") &&
      !ComputerUsePolicy.CanPressKeyOn("RadioButton", "ArrowDown") &&
      !ComputerUsePolicy.CanPressKeyOn("ComboBox", "ArrowDown") &&
      ComputerUsePolicy.CanPressKeyOn("Edit", "ArrowLeft") &&
      !ComputerUsePolicy.CanPressKeyOn("Edit", "ArrowDown") &&
      ComputerUsePolicy.RequiresUiApproval("RadioButton", "press-key", "ArrowDown"),
    "key navigation is limited to non-selection edit controls; radio and combo key effects require approval");
var collapsedMenuActions = ComputerUsePolicy.AllowedUiActions("MenuItem", true, false, false,
    supportsInvoke: true, supportsSelection: false, supportsValue: false, valueReadOnly: false,
    supportsScroll: false, supportsExpandCollapse: true, expandCollapseState: "Collapsed");
var expandedComboActions = ComputerUsePolicy.AllowedUiActions("ComboBox", true, false, false,
    supportsInvoke: true, supportsSelection: false, supportsValue: false, valueReadOnly: false,
    supportsScroll: false, supportsExpandCollapse: true, expandCollapseState: "Expanded");
Check(collapsedMenuActions.Contains("expand-element", StringComparer.Ordinal) &&
      !collapsedMenuActions.Contains("collapse-element", StringComparer.Ordinal) &&
      expandedComboActions.Contains("collapse-element", StringComparer.Ordinal) &&
      !ComputerUsePolicy.AllowedUiActions("Button", true, false, false,
          supportsInvoke: true, supportsSelection: false, supportsValue: false, valueReadOnly: false,
          supportsScroll: false, supportsExpandCollapse: true, expandCollapseState: "Collapsed")
          .Contains("expand-element", StringComparer.Ordinal) &&
      !ComputerUsePolicy.AllowedUiActions("MenuItem", true, false, false,
          supportsInvoke: true, supportsSelection: false, supportsValue: false, valueReadOnly: false,
          supportsScroll: false, supportsExpandCollapse: true, expandCollapseState: "LeafNode")
          .Contains("expand-element", StringComparer.Ordinal),
    "expand/collapse is advertised only for known navigation controls in a verified state");
var safeListSelection = ComputerUsePolicy.AllowedUiActions("ListItem", true, false, false,
    supportsInvoke: true, supportsSelection: true, supportsValue: false, valueReadOnly: false,
    supportsScroll: false, safeSelectionContainer: true);
var safeTreeSelection = ComputerUsePolicy.AllowedUiActions("TreeItem", true, false, false,
    supportsInvoke: true, supportsSelection: true, supportsValue: false, valueReadOnly: false,
    supportsScroll: false, safeSelectionContainer: true);
Check(safeListSelection.Contains("select-element", StringComparer.Ordinal) &&
      safeTreeSelection.Contains("select-element", StringComparer.Ordinal) &&
      !ComputerUsePolicy.AllowedUiActions("RadioButton", true, false, false,
          supportsInvoke: true, supportsSelection: true, supportsValue: false, valueReadOnly: false,
          supportsScroll: false).Contains("select-element", StringComparer.Ordinal) &&
      !ComputerUsePolicy.RequiresUiApproval("ListItem", "select-element", safeSelectionContainer: true) &&
      !ComputerUsePolicy.RequiresUiApproval("TreeItem", "select-element", safeSelectionContainer: true) &&
      ComputerUsePolicy.RequiresUiApproval("RadioButton", "select-element") &&
      ComputerUsePolicy.RequiresUiApproval("ComboBox", "select-element") &&
      ComputerUsePolicy.RequiresUiApproval("ListItem", "select-element") &&
      ComputerUsePolicy.IsSafeSelectionContext("ListItem", "List", insideComboBox: false) &&
      ComputerUsePolicy.IsSafeSelectionContext("TreeItem", "Tree", insideComboBox: false) &&
      !ComputerUsePolicy.IsSafeSelectionContext("ListItem", "List", insideComboBox: true) &&
      !ComputerUsePolicy.IsSafeSelectionContext("ListItem", "ComboBox", insideComboBox: false) &&
      !ComputerUsePolicy.IsSafeSelectionContext("TreeItem", "Tree", insideComboBox: true) &&
      new[] { "ListItem", "List", "ComboBox", "Window" }.Any(ComputerUsePolicy.IsUnsafeSelectionAncestor),
    "selection is permitted only for a proven list/tree/tab container outside ComboBox ancestry");
Check(ComputerUsePolicy.IsSafeSelectionContext("TabItem", "Tab", insideComboBox: false) &&
      !ComputerUsePolicy.IsSafeSelectionContext("ListItem", null, insideComboBox: false) &&
      !ComputerUsePolicy.CanSelectInContext("ListItem", "select-element", safeSelectionContainer: false),
    "unknown or changed selection containers fail closed at action time");
Check(ComputerUsePolicy.MatchesExpectedUiTarget("TabItem", "Lịch", "TabItem", "Lịch") &&
      !ComputerUsePolicy.MatchesExpectedUiTarget("TabItem", "Gmail", "TabItem", "Lịch") &&
      !ComputerUsePolicy.MatchesExpectedUiTarget("Hyperlink", null, "Hyperlink", "Help"),
    "a UI target is valid only while its live readable name and control type still match the authorized scope");
var sameLabelActionCount = 0;
var changedLabelActionCount = 0;
var unreadableLabelActionCount = 0;
var sameLabelRan = ComputerUsePolicy.TryRunExpectedUiAction("TabItem", "Lịch",
    () => (true, "TabItem", "Lịch"), () => { sameLabelActionCount++; return "ran"; }, out var sameLabelResult, out _);
var changedLabelRan = ComputerUsePolicy.TryRunExpectedUiAction("Hyperlink", "Docs",
    () => (true, "Hyperlink", "Other link"), () => { changedLabelActionCount++; return "ran"; }, out _, out var changedLabelStatus);
var unreadableLabelRan = ComputerUsePolicy.TryRunExpectedUiAction("TabItem", "Lịch",
    () => (false, "TabItem", (string?)null), () => { unreadableLabelActionCount++; return "ran"; }, out _, out var unreadableLabelStatus);
Check(sameLabelRan && sameLabelResult == "ran" && sameLabelActionCount == 1 &&
      !changedLabelRan && changedLabelActionCount == 0 && changedLabelStatus == "target_stale" &&
      !unreadableLabelRan && unreadableLabelActionCount == 0 && unreadableLabelStatus == "target_state_unavailable",
    "live label/type revalidation runs immediately before the action and rejects changed or unreadable targets");
var safeAtObservation = ComputerUsePolicy.IsSafeSelectionContext("ListItem", "List", insideComboBox: false);
var unsafeAtAction = ComputerUsePolicy.IsSafeSelectionContext("ListItem", "List", insideComboBox: true);
Check(safeAtObservation && !unsafeAtAction &&
      ComputerUsePolicy.RequiresUiApproval("ListItem", "select-element", safeSelectionContainer: unsafeAtAction),
    "selection is denied when its ComboBox ancestry changes between observation and execution");
var keyEventOrder = new List<string>();
ComputerUsePolicy.PressAndRelease("Tab", key => keyEventOrder.Add("down:" + key), key => keyEventOrder.Add("up:" + key));
Check(keyEventOrder.SequenceEqual(["down:Tab", "up:Tab"]), "a key action always emits a matching release");
try
{
    ComputerUsePolicy.PressAndRelease("Tab", _ => throw new InvalidOperationException("fixture"), _ => keyEventOrder.Add("released-after-error"));
}
catch (InvalidOperationException) { }
Check(keyEventOrder.Last() == "released-after-error", "key release runs even when key-down throws");
Check(ComputerUsePolicy.VerificationStatus(false) == "reobserve_required" &&
      ComputerUsePolicy.VerificationStatus(true) == "verified",
    "sending a key without an observed UI postcondition never claims it was verified");
Check(!ComputerUsePolicy.AllowedUiActions("Button", true, false, false,
        supportsInvoke: true, supportsSelection: false, supportsValue: false, valueReadOnly: false, supportsScroll: false)
      .Contains("click-element", StringComparer.Ordinal) &&
      ComputerUsePolicy.RequiresUiApproval("Button", "click-element"),
    "generic button invocation requires approval instead of inferring risk from its label");
Check(!ComputerUsePolicy.AllowedUiActions("Edit", true, false, true,
        supportsInvoke: false, supportsSelection: false, supportsValue: true, valueReadOnly: false, supportsScroll: false)
      .Contains("type-text", StringComparer.Ordinal),
    "password UI elements never advertise text entry");
var valueWrites = 0;
Check(ComputerUsePolicy.TrySetProtectedTextValue(true, () => true, () => (true, true), () => (true, false),
        _ => valueWrites++, "synthetic") == "password_input_denied" && valueWrites == 0,
    "a target that changes to password is denied before SetValue");
Check(ComputerUsePolicy.TrySetProtectedTextValue(true, () => true, () => (true, false), () => (true, true),
        _ => valueWrites++, "synthetic") == "target_read_only" && valueWrites == 0,
    "a target that changes to read-only is denied before SetValue");
Check(ComputerUsePolicy.TrySetProtectedTextValue(true, () => true, () => (false, false), () => (true, false),
        _ => valueWrites++, "synthetic") == "target_security_state_unavailable" && valueWrites == 0,
    "an unsupported password flag fails closed before SetValue");
Check(ComputerUsePolicy.TrySetProtectedTextValue(true, () => true, () => (true, false), () => (false, false),
        _ => valueWrites++, "synthetic") == "target_security_state_unavailable" && valueWrites == 0,
    "an unsupported read-only flag fails closed before SetValue");
Check(ComputerUsePolicy.TrySetProtectedTextValue(true, () => true, () => throw new InvalidOperationException(), () => (true, false),
        _ => valueWrites++, "synthetic") == "target_security_state_unavailable" && valueWrites == 0,
    "an unreadable password flag fails closed before SetValue");
Check(ComputerUsePolicy.TrySetProtectedTextValue(true, () => true, () => (true, false), () => (true, false),
        _ => valueWrites++, "synthetic") == "set" && valueWrites == 1,
    "a freshly revalidated non-password editable value can be set");
Check(!ComputerUsePolicy.AllowedUiActions("Edit", false, false, false,
        supportsInvoke: false, supportsSelection: false, supportsValue: true, valueReadOnly: false, supportsScroll: false).Any() &&
      !ComputerUsePolicy.AllowedUiActions("Edit", true, true, false,
        supportsInvoke: false, supportsSelection: false, supportsValue: true, valueReadOnly: false, supportsScroll: false).Any(),
    "disabled and offscreen elements expose no actions");
Check(DesktopCompanionProtocol.IsAllowedAction("read-workspace") &&
      DesktopCompanionProtocol.IsAllowedAction("read-github-pull-requests") &&
      DesktopCompanionProtocol.IsAllowedAction("read-github-account"),
    "Desktop Companion exposes only explicit read action IDs");
Check(!DesktopCompanionProtocol.IsAllowedAction("run-shell") &&
      !DesktopCompanionProtocol.IsAllowedAction("open-url:https://attacker.example") &&
      !DesktopCompanionProtocol.IsAllowedAction("read-file"),
    "Desktop Companion rejects arbitrary commands, URLs, and file reads");
var companionRequest = DesktopCompanionProtocol.SerializeRequest("open-vscode", Guid.NewGuid().ToString("N"));
Check(DesktopCompanionProtocol.TryParseRequest(companionRequest, out var parsedCompanionRequest) &&
      parsedCompanionRequest?.Action == "open-vscode",
    "Desktop Companion protocol accepts a fixed action and opaque request ID");
var workspaceItemRequest = DesktopCompanionProtocol.SerializeRequest("open-workspace-item", Guid.NewGuid().ToString("N"), value: "docs/audit.txt");
Check(DesktopCompanionProtocol.TryParseRequest(workspaceItemRequest, out var parsedWorkspaceItem) &&
      parsedWorkspaceItem?.Value == "docs/audit.txt" &&
      !DesktopCompanionProtocol.IsValidArguments("open-workspace-item", null, "../private.txt", null),
    "Explorer navigation accepts only a relative workspace item path");
Check(!DesktopCompanionProtocol.TryParseRequest("{\"action\":\"open-edge\",\"requestId\":\"00000000000000000000000000000000\",\"command\":\"whoami\"}", out _) &&
      !DesktopCompanionProtocol.TryParseRequest("{\"action\":\"run-shell\",\"requestId\":\"00000000000000000000000000000000\"}", out _),
    "Desktop Companion rejects injected properties and unknown actions");
var uiTargetId = Guid.NewGuid().ToString("N");
var uiRequest = DesktopCompanionProtocol.SerializeRequest("type-text", Guid.NewGuid().ToString("N"), uiTargetId, "search text");
Check(DesktopCompanionProtocol.TryParseRequest(uiRequest, out var parsedUiRequest) &&
      parsedUiRequest?.Action == "type-text" && parsedUiRequest.TargetId == uiTargetId && parsedUiRequest.Value == "search text",
    "UIA request carries only an observed opaque target and bounded text value");
Check(!DesktopCompanionProtocol.TryParseRequest("{\"action\":\"type-text\",\"requestId\":\"00000000000000000000000000000000\",\"targetId\":\"00000000000000000000000000000000\",\"value\":\"x\",\"value\":\"y\"}", out _) &&
      !DesktopCompanionProtocol.TryParseRequest("{\"action\":\"press-key\",\"requestId\":\"00000000000000000000000000000000\",\"targetId\":\"00000000000000000000000000000000\",\"value\":\"Enter\"}", out _),
    "UIA IPC rejects duplicate fields and submit/command keys");
Check(DesktopCompanionProtocol.TryParseRequest(
        DesktopCompanionProtocol.SerializeRequest("press-key", Guid.NewGuid().ToString("N"), uiTargetId, "ArrowDown"), out var keyRequest) &&
      keyRequest?.Value == "ArrowDown" &&
      DesktopCompanionProtocol.TryParseRequest(DesktopCompanionProtocol.SerializeRequest("expand-element", Guid.NewGuid().ToString("N"), uiTargetId,
          expectedName: "Tài liệu", expectedControlType: "TreeItem"), out var expandRequest) &&
      expandRequest?.Action == "expand-element" &&
      DesktopCompanionProtocol.TryParseRequest(DesktopCompanionProtocol.SerializeRequest("collapse-element", Guid.NewGuid().ToString("N"), uiTargetId,
          expectedName: "Tài liệu", expectedControlType: "TreeItem"), out var collapseRequest) &&
      collapseRequest?.Action == "collapse-element" &&
      !DesktopCompanionProtocol.IsValidArguments("select-element", uiTargetId, null, null) &&
      !DesktopCompanionProtocol.IsValidArguments("expand-element", uiTargetId, null, null) &&
      !DesktopCompanionProtocol.IsValidArguments("click-element", uiTargetId, null, null) &&
      !DesktopCompanionProtocol.IsAllowedAction("run-shell") &&
      !DesktopCompanionProtocol.IsValidArguments("type-text", uiTargetId, new string('x', 513), null),
    "UIA input enforces navigation keys, stateful menu actions, and text size bounds");
var scopedSelectionRequest = DesktopCompanionProtocol.SerializeRequest("select-element", Guid.NewGuid().ToString("N"),
    uiTargetId, expectedName: "Lịch", expectedControlType: "TabItem");
var scopedHyperlinkRequest = DesktopCompanionProtocol.SerializeRequest("click-element", Guid.NewGuid().ToString("N"),
    uiTargetId, expectedName: "Tài liệu hướng dẫn", expectedControlType: "Hyperlink");
var scopedTextRequest = DesktopCompanionProtocol.SerializeRequest("type-text", Guid.NewGuid().ToString("N"),
    uiTargetId, "tìm kiếm tài liệu", expectedName: "Tìm kiếm", expectedControlType: "Edit");
var scopedTreeSelectionRequest = DesktopCompanionProtocol.SerializeRequest("select-element", Guid.NewGuid().ToString("N"),
    uiTargetId, expectedName: "Tài liệu", expectedControlType: "TreeItem");
var scopedWindowRequest = DesktopCompanionProtocol.SerializeRequest("activate-window", Guid.NewGuid().ToString("N"),
    uiTargetId, expectedName: "Visual Studio Code", expectedControlType: "Window");
Check(DesktopCompanionProtocol.TryParseRequest(scopedSelectionRequest, out var parsedScopedSelection) &&
      parsedScopedSelection is { ExpectedName: "Lịch", ExpectedControlType: "TabItem" } &&
      DesktopCompanionProtocol.TryParseRequest(scopedHyperlinkRequest, out var parsedScopedHyperlink) &&
      parsedScopedHyperlink is { ExpectedName: "Tài liệu hướng dẫn", ExpectedControlType: "Hyperlink" } &&
      DesktopCompanionProtocol.TryParseRequest(scopedTextRequest, out var parsedScopedText) &&
      parsedScopedText is { ExpectedName: "Tìm kiếm", ExpectedControlType: "Edit", Value: "tìm kiếm tài liệu" } &&
      DesktopCompanionProtocol.TryParseRequest(scopedTreeSelectionRequest, out var parsedScopedTreeSelection) &&
      parsedScopedTreeSelection is { ExpectedName: "Tài liệu", ExpectedControlType: "TreeItem" } &&
      DesktopCompanionProtocol.TryParseRequest(scopedWindowRequest, out var parsedScopedWindow) &&
      parsedScopedWindow is { ExpectedName: "Visual Studio Code", ExpectedControlType: "Window" } &&
      !DesktopCompanionProtocol.IsValidArguments("select-element", uiTargetId, null, null, "Lịch", "Hyperlink") &&
      !DesktopCompanionProtocol.IsValidArguments("select-element", uiTargetId, null, null, "Lịch", "Edit") &&
      !DesktopCompanionProtocol.IsValidArguments("type-text", uiTargetId, "x", null, "Tìm kiếm", "TabItem") &&
      !DesktopCompanionProtocol.IsValidArguments("select-element", uiTargetId, null, null, "Lịch", null),
    "the authenticated Companion protocol carries a bounded backend-authorized target label/type pair and rejects mismatches");
var cappedUiResponse = DesktopCompanionProtocol.SerializeResponse(Guid.NewGuid().ToString("N"), new { data = new string('x', 32 * 1024) });
Check(cappedUiResponse.Length < 1024 && cappedUiResponse.Contains("response_too_large", StringComparison.Ordinal),
    "desktop IPC caps oversized UI observations to a bounded protocol error");
var dedicatedEdgeCommand = "msedge.exe --user-data-dir=\"C:\\Users\\Tester\\AppData\\Local\\DetotLab\\Edge Profile\" --new-window https://github.com/owner/repo/pulls";
Check(DesktopCompanionVerification.IsDedicatedEdgeCommandLine(dedicatedEdgeCommand,
      "C:\\Users\\Tester\\AppData\\Local\\DetotLab\\Edge Profile") &&
      DesktopCompanionVerification.IsDedicatedEdgeCommandLine("msedge.exe \"--user-data-dir=C:\\Users\\Tester\\AppData\\Local\\DetotLab\\Edge Profile\" --new-window https://github.com/owner/repo/pulls",
      "C:\\Users\\Tester\\AppData\\Local\\DetotLab\\Edge Profile") &&
      !DesktopCompanionVerification.IsDedicatedEdgeCommandLine(dedicatedEdgeCommand, "C:\\Users\\Tester\\Personal Edge"),
    "Edge window verification accepts only the isolated Detot profile process");
Check(DesktopCompanionVerification.GithubAddressMatches("https://github.com/owner/repo/pulls?state=open", "https://github.com/owner/repo/pulls") &&
      !DesktopCompanionVerification.GithubAddressMatches("https://github.com.evil.test/owner/repo/pulls", "https://github.com/owner/repo/pulls") &&
      !DesktopCompanionVerification.GithubAddressMatches("https://github.com/owner/other/pulls", "https://github.com/owner/repo/pulls"),
    "Edge URL verification requires the exact HTTPS GitHub host and repository path");
Check(DesktopCompanionVerification.GetEdgeVerificationStatus(false, false, false, false) == "launch_failed" &&
      DesktopCompanionVerification.GetEdgeVerificationStatus(true, false, false, false) == "process_started" &&
      DesktopCompanionVerification.GetEdgeVerificationStatus(true, true, false, false) == "window_not_found" &&
      DesktopCompanionVerification.GetEdgeVerificationStatus(true, true, true, false) == "url_not_verified" &&
      DesktopCompanionVerification.GetEdgeVerificationStatus(true, true, true, true) == "verified",
    "Edge smoke exposes distinct launch, process, window, URL, and verified states");
Check(DesktopCompanionVerification.WorkspaceLabelMatches("D:\\work\\codex-computer-use", "codex-computer-use - Visual Studio Code",
      ["codex-computer-use"], []) &&
      DesktopCompanionVerification.WorkspaceLabelMatches("D:\\work\\codex-computer-use", "codex-computer-use - Visual Studio Code",
      ["Open Editors", "another-workspace", "codex-computer-use"], []) &&
      DesktopCompanionVerification.WorkspaceLabelMatches("D:\\work\\codex-computer-use", "Visual Studio Code",
      [], ["D:\\work\\codex-computer-use"]) &&
      !DesktopCompanionVerification.WorkspaceLabelMatches("D:\\work\\codex-computer-use", "codex-computer-use - Visual Studio Code",
      [], []) &&
      !DesktopCompanionVerification.WorkspaceLabelMatches("D:\\work\\codex-computer-use", "Visual Studio Code",
      ["another-workspace"], []),
    "VS Code workspace verification requires an accessible root label or full path; title alone is insufficient");
var reusedWindowWorkspace = DesktopCompanionVerification.EvaluateWorkspace("D:\\work\\codex-computer-use", true,
    "codex-computer-use - Visual Studio Code", ["Open Editors", "codex-computer-use"], []);
var wrongWindowWorkspace = DesktopCompanionVerification.EvaluateWorkspace("D:\\work\\codex-computer-use", true,
    "codex-computer-use - Visual Studio Code", ["another-workspace"], []);
var timedOutWorkspace = DesktopCompanionVerification.EvaluateWorkspace("D:\\work\\codex-computer-use", false,
    null, [], []);
Check(reusedWindowWorkspace.Verified && reusedWindowWorkspace.Reason == "verified_by_workspace_root_and_title" &&
      reusedWindowWorkspace.ExpectedWorkspace == "D:\\work\\codex-computer-use" &&
      !wrongWindowWorkspace.Verified && wrongWindowWorkspace.Reason == "workspace_mismatch" &&
      wrongWindowWorkspace.ObservedWorkspace == "different workspace (name redacted)" &&
      !timedOutWorkspace.Verified && timedOutWorkspace.Reason == "window_not_found",
    "VS Code workspace evidence distinguishes reused matching, wrong workspace, and window timeout without leaking outside names");
var vscodeArguments = DesktopCompanionVerification.BuildVsCodeWorkspaceArguments("D:\\work\\codex-computer-use");
Check(vscodeArguments.SequenceEqual(["--new-window", "D:\\work\\codex-computer-use"], StringComparer.OrdinalIgnoreCase) &&
      !vscodeArguments.Contains("--reuse-window", StringComparer.Ordinal),
    "opening the target VS Code workspace requests a separate window and preserves existing user windows");
Check(!DesktopCompanionVerification.IsNewWindow(101, new HashSet<nint> { 101, 202 }) &&
      DesktopCompanionVerification.IsNewWindow(303, new HashSet<nint> { 101, 202 }) &&
      !DesktopCompanionVerification.IsNewWindow(0, new HashSet<nint>()),
    "workspace verification excludes reused windows and requires a new identifiable window handle");
Check(DesktopCompanionVerification.VsCodeActionReply("launch_failed", false, false, "D:\\repo", null, "launch_failed")
          .Contains("chưa khởi chạy", StringComparison.Ordinal) &&
      DesktopCompanionVerification.VsCodeActionReply("window_not_found", false, false, "D:\\repo", null, "timeout_window_not_found")
          .Contains("chưa thấy cửa sổ", StringComparison.Ordinal) &&
      DesktopCompanionVerification.VsCodeActionReply("workspace_not_verified", true, false, "D:\\repo", "different workspace (name redacted)", "workspace_mismatch")
          .Contains("workspace chưa xác minh", StringComparison.Ordinal) &&
      DesktopCompanionVerification.VsCodeActionReply("verified", true, true, "D:\\repo", "repo", "verified_by_accessible_path")
          .Contains("Đã mở và xác minh", StringComparison.Ordinal),
    "VS Code chat response distinguishes launch failure, missing window, workspace mismatch, and verified success");
Check(DesktopCompanionVerification.IntegrityLevelFromRid(0x0000) == "untrusted" &&
      DesktopCompanionVerification.IntegrityLevelFromRid(0x1000) == "low" &&
      DesktopCompanionVerification.IntegrityLevelFromRid(0x2000) == "medium" &&
      DesktopCompanionVerification.IntegrityLevelFromRid(0x3000) == "high" &&
      DesktopCompanionVerification.IntegrityLevelFromRid(0x4000) == "system",
    "Windows integrity RID boundaries map Untrusted, Low, Medium, High, and System correctly");
Check(DesktopCompanionVerification.SameInteractiveSession(5, "Default", true, 5, 5, true, true) &&
      DesktopCompanionVerification.SameInteractiveSession(5, "Default", true, 5, 5, true, true) &&
      !DesktopCompanionVerification.SameInteractiveSession(5, "Default", true, 4, 5, true, true) &&
      !DesktopCompanionVerification.SameInteractiveSession(5, "unavailable", true, 5, 5, true, true),
    "same-session result depends on UIA-visible windows, session IDs, and desktop availability, not workspace outcome");
Check(DesktopCompanionVerification.ExtractSignedInUsername(["Signed in as voquangthan"]) == "voquangthan" &&
      DesktopCompanionVerification.ExtractSignedInUsername(["Welcome voquangthan"]) is null &&
      DesktopCompanionVerification.ExtractSignedInUsername(["Signed in as user-a", "Signed in as user-b"]) is null,
    "GitHub account observation returns only one explicit signed-in accessibility label");
Check(DesktopCompanionVerification.IsGithubLoginPage("https://github.com/login?return_to=%2Fsettings%2Fprofile") &&
      !DesktopCompanionVerification.IsGithubLoginPage("https://github.com.evil.test/login") &&
      DesktopCompanionVerification.HasGithubSignedOutMarker(["Sign in to GitHub"]) &&
      !DesktopCompanionVerification.HasGithubSignedOutMarker(["Welcome", "Signed in as voquangthan"]),
    "GitHub signed-out status requires the exact login URL and its explicit accessibility heading");
Check(DesktopCompanionVerification.NormalizeGithubAccountResult(true, "verified", "voquangthan") == ("verified", "voquangthan") &&
      DesktopCompanionVerification.NormalizeGithubAccountResult(true, "signed_out", "ignored") == ("signed_out", null) &&
      DesktopCompanionVerification.NormalizeGithubAccountResult(true, "unknown", null) == ("not_verified", null) &&
      DesktopCompanionVerification.NormalizeGithubAccountResult(false, null, null) == ("failed", null) &&
      DesktopCompanionVerification.NormalizeGithubAccountResult(true, "verified", "not a username") == ("not_verified", null),
    "GitHub account API distinguishes verified, signed-out, unverified, malformed and failed observations");
Check(DesktopCompanionVerification.IsSmokeTestPassed(true, true, true, true, true, "verified") &&
      DesktopCompanionVerification.IsSmokeTestPassed(true, true, true, true, true, "signed_out") &&
      !DesktopCompanionVerification.IsSmokeTestPassed(true, true, true, true, true, "not_verified") &&
      !DesktopCompanionVerification.IsSmokeTestPassed(true, true, true, true, true, "failed"),
    "smoke cannot convert an unverified or failed GitHub account observation to PASS");
var unavailableCompanion = await new DesktopCompanionClient($"detot-no-listener-{Guid.NewGuid():N}").ExecuteAsync("open-edge");
Check(unavailableCompanion.TryGetProperty("error", out var unavailableCompanionError) &&
      unavailableCompanionError.GetString() is "companion_unavailable" or "companion_unauthorized",
    "Backend reports an unavailable Companion instead of falling back to isolated-session UI automation");
using (var oversizedReader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("123456789\n"))))
{
    var boundedReadRejected = false;
    try { _ = await DesktopCompanionProtocol.ReadBoundedLineAsync(oversizedReader, 8); }
    catch (InvalidDataException) { boundedReadRejected = true; }
    Check(boundedReadRejected, "Desktop Companion enforces bounded IPC frame sizes");
}
JsonElement[] MakeCheckRuns(int count, int failingIndex)
{
    var runs = new JsonElement[count];
    for (var index = 0; index < count; index++)
    {
        using var run = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            name = $"check-{index + 1}", status = "completed", conclusion = index == failingIndex ? "failure" : "success"
        }));
        runs[index] = run.RootElement.Clone();
    }
    return runs;
}
var nineChecks = DesktopGithubChecks.Summarize(MakeCheckRuns(9, 8));
var twentyChecks = DesktopGithubChecks.Summarize(MakeCheckRuns(24, 23));
var cappedChecks = DesktopGithubChecks.Summarize(MakeCheckRuns(20, 18), apiTotalCount: 120, paginationIncomplete: true);
Check(nineChecks.TotalCount == 9 && nineChecks.DisplayedCount == 8 && nineChecks.Truncated &&
      nineChecks.FailureCount == 1 && nineChecks.FailureNames.SequenceEqual(["check-9"]) &&
      DesktopGithubChecks.FormatCoverage(nineChecks).Contains("1 check lỗi", StringComparison.Ordinal),
    "GitHub reader reports a failure on check 9 despite the eight-check display limit");
Check(twentyChecks.TotalCount == 24 && twentyChecks.FailureCount == 1 &&
      twentyChecks.FailureNames.SequenceEqual(["check-24"]) &&
      DesktopGithubChecks.FormatCoverage(twentyChecks).Contains("24 checks", StringComparison.Ordinal),
    "GitHub reader aggregates failures beyond the displayed first eight among 20+ checks");
Check(cappedChecks.Truncated && cappedChecks.TotalCount == 120 &&
      DesktopGithubChecks.FormatCoverage(cappedChecks).Contains("danh sách bị cắt", StringComparison.Ordinal),
    "GitHub reader marks results incomplete if the API pagination cap is reached");

var testPipeName = $"detot-test-{Guid.NewGuid():N}";
using (var serverShutdown = new CancellationTokenSource())
{
    var firstActionCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var actionCount = 0;
    var pipeServer = new DesktopCompanionPipeServer(testPipeName, requestDeadlineMilliseconds: 3_000);
    var serverTask = pipeServer.RunAsync(async (request, token) =>
    {
        if (Interlocked.Increment(ref actionCount) == 1)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) { firstActionCancelled.TrySetResult(); throw; }
        }
        return new { succeeded = true, action = request.Action };
    }, serverShutdown.Token);
    try
    {
        using var clientADeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clientACancelled = false;
        try { _ = await new DesktopCompanionClient(testPipeName).ExecuteAsync("read-workspace", clientADeadline.Token); }
        catch (OperationCanceledException) { clientACancelled = true; }
        await firstActionCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var clientBDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var clientB = await new DesktopCompanionClient(testPipeName).ExecuteAsync("read-workspace", clientBDeadline.Token);
        Check(clientACancelled && clientB.TryGetProperty("action", out var acceptedAction) &&
              acceptedAction.GetString() == "read-workspace" && actionCount == 2,
            "client B is served after client A disconnects during a cancellable action");
    }
    finally
    {
        serverShutdown.Cancel();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
Check(!ComputerUsePolicy.CanSelectGitHubAccount("select-github-account:voquangthan"),
    "GCM account selection remains denied without a trusted pending Git transaction");
Check(!ComputerUsePolicy.CanSelectGitHubAccount("select-github-account:bottom"),
    "a positional account request cannot authorize an unbound GCM transaction");
Check(ComputerUseChatIntent.DetectDirectRequest("Chọn tài khoản GitHub voquangthan") is null,
    "a named-account request cannot dispatch the unbound GCM selector");
Check(ComputerUseChatIntent.DetectDirectRequest("Chọn cái ở dưới đi") is null,
    "a position-only request cannot dispatch an unbound authentication action");
Check(ComputerUseChatIntent.DetectDirectRequest("Làm sao chọn tài khoản GitHub voquangthan?") is null,
    "an account-selection how-to question does not switch identity");
Check(ComputerUseChatIntent.DetectDirectRequest("Trong ví dụ: chọn cái ở dưới") is null,
    "an embedded account-selection example does not switch identity");
Check(ComputerUseChatIntent.DetectDirectRequest("Chọn cái ở dưới\nMở Edge") is null,
    "multiline content cannot trigger an authentication action");
Check(!System.Text.Json.JsonSerializer.Serialize(ComputerUsePolicy.Describe())
      .Contains("explicitly-requested-listed-github-account", StringComparison.Ordinal),
    "the public permission contract does not advertise unbound GCM account selection");
Check(ComputerUseChatIntent.DetectDirectRequest("Mở Edge đi") == "open-edge",
    "a direct chat request maps to an allowlisted Edge action");
Check(ComputerUseChatIntent.DetectDirectRequest("Đệ Tốt, mở VS Code giúp t") == "open-vscode",
    "a direct chat request maps to an allowlisted VS Code action");
Check(ComputerUseChatIntent.DetectDirectRequest("Mở Windows Settings") == "open-settings" &&
      ComputerUseChatIntent.DetectDirectRequest("Open Task Manager") == "open-task-manager" &&
      ComputerUseChatIntent.DetectDirectRequest("Mở Word") == "open-word" &&
      ComputerUseChatIntent.DetectDirectRequest("Mở Excel") == "open-excel" &&
      ComputerUseChatIntent.DetectDirectRequest("Mở PowerPoint") == "open-powerpoint" &&
      ComputerUseChatIntent.DetectDirectRequest("Mở PDF Reader") == "open-pdf-reader",
    "fixed application launch intents are recognized without command text");
Check(ComputerUseChatIntent.DetectDirectRequest("Kiểm tra tài khoản GitHub hiện tại") == "read-github-account",
    "a direct GitHub account inspection request is recognized");
Check(ComputerUseChatIntent.DetectDirectRequest("Đọc PR và CI") == "read-github-pull-requests",
    "a direct PR and CI inspection request is recognized");
Check(ComputerUseChatIntent.DetectDirectRequest("Xem ứng dụng đang mở") == "read-approved-applications",
    "a direct approved-app observation request is recognized");
Check(ComputerUseChatIntent.DetectDirectRequest("Làm sao để mở Edge?") is null,
    "a how-to question does not launch an application");
Check(ComputerUseChatIntent.DetectDirectRequest("Trong ví dụ này: mở Edge") is null,
    "an embedded example does not launch an application");
Check(ComputerUseChatIntent.DetectDirectRequest("Mở Edge\nĐọc PR") is null,
    "multi-line content does not trigger a desktop action");
Check(ComputerUseChatIntent.DetectDirectRequest("Mở GitHub") is { } openGithub &&
      ComputerUsePolicy.CanNavigate(openGithub),
    "chat navigation intents remain within the navigation permission set");
Check(ComputerUseChatIntent.DetectDirectRequest("Đọc PR và CI") is { } readGitHub &&
      ComputerUsePolicy.CanObserve(ComputerUseChatIntent.ObservationTargetForAction(readGitHub)),
    "chat observation intents remain within the observation permission set");
using (var validAgentAction = JsonDocument.Parse("{\"operation\":\"click\",\"targetId\":\"0123456789abcdef0123456789abcdef\"}"))
    Check(ComputerUseAgentAction.TryParse(validAgentAction.RootElement, out var parsedAgentAction) &&
          parsedAgentAction is { Operation: "click", TargetId: "0123456789abcdef0123456789abcdef" },
        "agent Computer Use accepts only a typed action with an opaque target ID");
using (var validExpandAction = JsonDocument.Parse("{\"operation\":\"expand\",\"targetId\":\"0123456789abcdef0123456789abcdef\"}"))
    Check(ComputerUseAgentAction.TryParse(validExpandAction.RootElement, out var parsedExpandAction) &&
          parsedExpandAction is { Operation: "expand", TargetId: "0123456789abcdef0123456789abcdef" },
        "agent Computer Use accepts a bounded observed-target menu expansion");
using (var arbitraryAgentAction = JsonDocument.Parse("{\"operation\":\"run_shell\",\"value\":\"whoami\"}"))
    Check(!ComputerUseAgentAction.TryParse(arbitraryAgentAction.RootElement, out _),
        "agent Computer Use rejects arbitrary execution operations");
using (var extraAgentAction = JsonDocument.Parse("{\"operation\":\"observe\",\"shell\":\"whoami\"}"))
    Check(!ComputerUseAgentAction.TryParse(extraAgentAction.RootElement, out _),
        "agent Computer Use rejects arguments outside its strict schema");
using (var badTargetAgentAction = JsonDocument.Parse("{\"operation\":\"type_text\",\"targetId\":\"stale\",\"value\":\"hello\"}"))
    Check(!ComputerUseAgentAction.TryParse(badTargetAgentAction.RootElement, out _),
        "agent Computer Use rejects non-opaque or malformed UI target IDs");
var unrelatedScope = ComputerUseTaskAuthorization.FromCurrentMessage("Hôm nay trời thế nào?");
var openEdgeScope = ComputerUseTaskAuthorization.FromCurrentMessage("Mở Edge đi");
var openFileScope = ComputerUseTaskAuthorization.FromCurrentMessage("Mở file docs/audit.txt");
var selectTabScope = ComputerUseTaskAuthorization.FromCurrentMessage("Chọn tab Lịch");
var selectListScope = ComputerUseTaskAuthorization.FromCurrentMessage("Chọn danh sách Báo cáo tháng");
var selectTreeScope = ComputerUseTaskAuthorization.FromCurrentMessage("Chọn cây Tài liệu");
var typeFieldScope = ComputerUseTaskAuthorization.FromCurrentMessage("Nhập tìm kiếm tài liệu vào ô Tìm kiếm");
var activateWindowScope = ComputerUseTaskAuthorization.FromCurrentMessage("Chuyển sang cửa sổ Visual Studio Code");
var unrelatedFakeGatewayCalls = new[] { "detot_computer_use" };
Check(unrelatedScope is null &&
      !ComputerUseTaskAuthorization.ShouldExposeTool(unrelatedScope, interactionConsent: true, observationConsent: true) &&
      !unrelatedFakeGatewayCalls.Any(name => ComputerUseTaskAuthorization.ShouldDispatchToolCall(unrelatedScope, name)),
    "unrelated chat exposes no UI labels and cannot dispatch a fake tool call even when both consent grants exist");
Check(openEdgeScope is { } scopedEdge && scopedEdge.Allows(new("observe")) &&
      ComputerUseTaskAuthorization.ShouldExposeTool(openEdgeScope, interactionConsent: true, observationConsent: true) &&
      scopedEdge.Allows(new("open_application", Application: "edge")) &&
      !scopedEdge.Allows(new("open_application", Application: "vscode")) &&
      !scopedEdge.Allows(new("click", TargetId: "0123456789abcdef0123456789abcdef")) &&
      ComputerUseTaskAuthorization.ShouldDispatchToolCall(openEdgeScope, "detot_computer_use") &&
      !ComputerUseTaskAuthorization.ShouldDispatchToolCall(openEdgeScope, "other_tool"),
    "a direct desktop request authorizes only its matching application and required observation");
Check(openFileScope is { RequestedValue: "docs/audit.txt" } &&
      openFileScope.Allows(new("observe")) &&
      openFileScope.Allows(new("open_workspace_item", Value: "docs/audit.txt")) &&
      !openFileScope.Allows(new("open_workspace_item", Value: "docs/other.txt")) &&
      ComputerUseTaskAuthorization.FromCurrentMessage("Mở file ../private.txt") is null,
    "workspace navigation scope binds the exact safe relative path requested by the current user message");
Check(selectListScope is { TargetControlType: "ListItem", TargetLabel: "Báo cáo tháng" } &&
      selectListScope.Allows(new("select", TargetId: "0123456789abcdef0123456789abcdef")) &&
      !selectListScope.Allows(new("select_tab", TargetId: "0123456789abcdef0123456789abcdef")) &&
      selectTreeScope is { TargetControlType: "TreeItem", TargetLabel: "Tài liệu" } &&
      selectTreeScope.Allows(new("select", TargetId: "0123456789abcdef0123456789abcdef")) &&
      typeFieldScope is { TargetControlType: "Edit", TargetLabel: "Tìm kiếm", RequestedValue: "tìm kiếm tài liệu" } &&
      typeFieldScope.Allows(new("type_text", TargetId: "0123456789abcdef0123456789abcdef", Value: "tìm kiếm tài liệu")) &&
      !typeFieldScope.Allows(new("type_text", TargetId: "0123456789abcdef0123456789abcdef", Value: "khác")) &&
      activateWindowScope is { TargetControlType: "Window", TargetLabel: "Visual Studio Code" } &&
      activateWindowScope.Allows(new("activate_window", TargetId: "0123456789abcdef0123456789abcdef")),
    "named list selection, text entry, and window activation bind one label and exact allowed value");
var tabTargetId = "1123456789abcdef0123456789abcdef";
var tabTarget = new ComputerUseObservedTarget("TabItem", "Lịch", new(StringComparer.Ordinal) { "select-element" });
var tabTurnState = new ComputerUseTurnState();
Check(selectTabScope is { TargetControlType: "TabItem", TargetLabel: "Lịch" } &&
      tabTurnState.RecordObservation("available", new Dictionary<string, ComputerUseObservedTarget> { [tabTargetId] = tabTarget }, selectTabScope) &&
      tabTurnState.CanAct(new("select_tab", TargetId: tabTargetId), selectTabScope) &&
      !tabTurnState.CanAct(new("click", TargetId: tabTargetId), selectTabScope),
    "an explicit tab request exposes only the single named TabItem and its selection operation");
JsonDocument MakeObservationFixture(int[] matchingIndices, bool truncated = false, int controlCount = 27, bool partialStatus = false)
{
    var controls = Enumerable.Range(0, controlCount).Select(index => new
    {
        elementId = $"target-{index}", windowId = $"window-{index % 3}", controlType = "TabItem",
        name = matchingIndices.Contains(index) ? "Lịch" : $"Tab {index}", nameTruncated = false, isPassword = false,
        allowedActions = new[] { "select-element" }
    }).ToArray();
    return JsonDocument.Parse(JsonSerializer.Serialize(new { observationStatus = truncated || partialStatus ? "partial" : "available", truncated, elements = controls }));
}
using (var duplicateAcrossBackendLimit = MakeObservationFixture([1, 25]))
{
    var selection = ComputerUseObservationFilter.Select(duplicateAcrossBackendLimit.RootElement, selectTabScope!);
    Check(selection.Status == "target_ambiguous" && selection.Elements.Length == 0 &&
          !ComputerUseObservationFilter.IsUsableForNamedAction(selection),
        "duplicate matches across all 27 bounded controls are rejected by the same gate that prevents action dispatch");
}
using (var uniqueAfterBackendLimit = MakeObservationFixture([25]))
{
    var selection = ComputerUseObservationFilter.Select(uniqueAfterBackendLimit.RootElement, selectTabScope!);
    Check(selection.Status == "ok" && selection.Elements.Length == 1 &&
          selection.Elements[0].GetProperty("elementId").GetString() == "target-25" &&
          ComputerUseObservationFilter.IsUsableForNamedAction(selection),
        "a unique matching control after the former 24-item cutoff remains discoverable");
}
using (var truncatedNamedObservation = MakeObservationFixture([1], truncated: true))
    Check(ComputerUseObservationFilter.Select(truncatedNamedObservation.RootElement, selectTabScope!).Status == "observation_truncated",
        "a truncated source observation cannot be used to claim a unique target");
using (var overLimitNamedObservation = MakeObservationFixture([1], controlCount: 73))
    Check(ComputerUseObservationFilter.Select(overLimitNamedObservation.RootElement, selectTabScope!).Status == "observation_truncated",
        "a response exceeding the Companion's documented 72-control bound fails closed for named actions");
using (var partialNamedObservation = MakeObservationFixture([1], partialStatus: true))
    Check(ComputerUseObservationFilter.Select(partialNamedObservation.RootElement, selectTabScope!).Status == "observation_truncated",
        "partial observation status cannot be treated as proof of a unique named target");
using (var namedWindowObservation = JsonDocument.Parse("""
    {"observationStatus":"available","truncated":false,"windows":[
      {"windowId":"0123456789abcdef0123456789abcdef","title":"Visual Studio Code","allowedActions":["activate-window"]},
      {"windowId":"1123456789abcdef0123456789abcdef","title":"Edge","allowedActions":["activate-window"]}
    ],"elements":[]}
    """))
{
    var selection = ComputerUseObservationFilter.Select(namedWindowObservation.RootElement, activateWindowScope!);
    Check(selection.Status == "ok" && selection.Elements.Length == 1 &&
          selection.Elements[0].GetProperty("elementId").GetString() == "0123456789abcdef0123456789abcdef" &&
          selection.WindowIds.SequenceEqual(["0123456789abcdef0123456789abcdef"]),
        "named window activation resolves exactly one observed top-level window instead of an arbitrary UI element");
}
using (var cappedUnscopedObservation = MakeObservationFixture([]))
{
    var selection = ComputerUseObservationFilter.Select(cappedUnscopedObservation.RootElement,
        new ComputerUseTaskScope("open-edge", ["observe", "open_application"], "edge"));
    Check(selection.Elements.Length == 24 && selection.Truncated && selection.Status == "partial",
        "the unscoped observation payload marks Backend-side control truncation accurately");
}
var ambiguousTabState = new ComputerUseTurnState();
Check(selectTabScope is not null && !ambiguousTabState.RecordObservation("available", new Dictionary<string, ComputerUseObservedTarget>
      { [tabTargetId] = tabTarget, ["2123456789abcdef0123456789abcdef"] = tabTarget }, selectTabScope) &&
      !ambiguousTabState.CanAct(new("select_tab", TargetId: tabTargetId), selectTabScope),
    "duplicate matching UI labels stop before selection instead of choosing arbitrarily");
var failedObserveState = new ComputerUseTurnState();
Check(openEdgeScope is not null && !failedObserveState.RecordObservation("unavailable", new Dictionary<string, ComputerUseObservedTarget>(), openEdgeScope) &&
      !failedObserveState.CanAct(new("open_application", Application: "edge"), openEdgeScope) &&
      !failedObserveState.CanAcceptModelCompletion,
    "failed observation stops the turn before actions or model success claims");
var failedActionState = new ComputerUseTurnState();
var testClickScope = new ComputerUseTaskScope("test", ["observe", "click"]);
var clickTarget = new ComputerUseObservedTarget("Hyperlink", "Help", new(StringComparer.Ordinal) { "click-element" });
Check(failedActionState.RecordObservation("available", new Dictionary<string, ComputerUseObservedTarget>
      { ["0123456789abcdef0123456789abcdef"] = clickTarget }, testClickScope) &&
      failedActionState.CanAct(new("click", TargetId: "0123456789abcdef0123456789abcdef"), testClickScope) &&
      !failedActionState.RecordAction("not_verified", false, "not_verified") &&
      !failedActionState.RecordObservation("available", new Dictionary<string, ComputerUseObservedTarget>(), testClickScope) &&
      !failedActionState.CanAct(new("click", TargetId: "0123456789abcdef0123456789abcdef"), testClickScope) &&
      !failedActionState.CanAcceptModelCompletion,
    "an unverified action cannot be followed by observe/action or accepted as model completion");
var verifiedActionState = new ComputerUseTurnState();
Check(verifiedActionState.RecordObservation("available", new Dictionary<string, ComputerUseObservedTarget>
      { ["0123456789abcdef0123456789abcdef"] = clickTarget }, testClickScope) &&
      verifiedActionState.RecordAction("completed", true, "verified") &&
      !verifiedActionState.CanAct(new("click", TargetId: "0123456789abcdef0123456789abcdef"), testClickScope) &&
      verifiedActionState.CanAct(new("observe"), testClickScope),
    "a verified action invalidates old targets and requires a new observation before another action");
var agentConsentRoot = Path.Combine(Path.GetTempPath(), "detot-computer-use-consent-" + Guid.NewGuid().ToString("N"));
try
{
    var agentConsent = new ConsentPolicy(agentConsentRoot, new EphemeralDataProtectionProvider());
    var agentConsentSnapshot = agentConsent.Get("synthetic-owner");
    Check(agentConsentSnapshot.Options.Any(option => option.Source == "computer_use" && option.Destination == "local_only") &&
          agentConsentSnapshot.Options.Any(option => option.Source == "desktop_ui" && option.Destination == "DeepSeek") &&
          !agentConsent.IsAllowed("synthetic-owner", "computer_use", "direct_request", "local_only") &&
          !agentConsent.IsAllowed("synthetic-owner", "desktop_ui", "direct_request", "DeepSeek"),
        "agent interaction and external UI observation permissions are explicit and disabled by default");
}
finally
{
    if (Directory.Exists(agentConsentRoot)) Directory.Delete(agentConsentRoot, recursive: true);
}
var computerUseExecutions = 0;
Task<string> FakeComputerUseExecutor(string action)
{
    computerUseExecutions++;
    return Task.FromResult(action);
}
var activeCaseDispatch = await ComputerUseChatDispatcher.TryDispatchAsync("Mở Edge",
    ComputerUseChatDispatcher.IsPracticeMode(false, true), false, FakeComputerUseExecutor);
Check(!activeCaseDispatch.Dispatched && computerUseExecutions == 0,
    "an active incident case never invokes the real desktop executor");
var startPracticeDispatch = await ComputerUseChatDispatcher.TryDispatchAsync("Mở Edge",
    ComputerUseChatDispatcher.IsPracticeMode(true, false), false, FakeComputerUseExecutor);
Check(!startPracticeDispatch.Dispatched && computerUseExecutions == 0,
    "starting a practice case never invokes the real desktop executor");
var miniPracticeDispatch = await ComputerUseChatDispatcher.TryDispatchAsync("Mở VS Code", false,
    ComputerUseChatDispatcher.IsMiniPracticeMode(true, false), FakeComputerUseExecutor);
Check(!miniPracticeDispatch.Dispatched && computerUseExecutions == 0,
    "an active mini-practice never invokes the real desktop executor");
var startMiniPracticeDispatch = await ComputerUseChatDispatcher.TryDispatchAsync("Mở VS Code", false,
    ComputerUseChatDispatcher.IsMiniPracticeMode(false, true), FakeComputerUseExecutor);
Check(!startMiniPracticeDispatch.Dispatched && computerUseExecutions == 0,
    "starting a mini-practice never invokes the real desktop executor");
var normalChatDispatch = await ComputerUseChatDispatcher.TryDispatchAsync("Mở VS Code", false, false, FakeComputerUseExecutor);
Check(normalChatDispatch.Dispatched && normalChatDispatch.Action == "open-vscode" &&
      normalChatDispatch.Value == "open-vscode" && computerUseExecutions == 1,
    "a direct normal-chat command invokes exactly one allowlisted action");
computerUseExecutions = 0;
var normalQuestionDispatch = await ComputerUseChatDispatcher.TryDispatchAsync("Làm sao mở Edge?", false, false, FakeComputerUseExecutor);
Check(!normalQuestionDispatch.Dispatched && computerUseExecutions == 0,
    "a normal-chat how-to question does not invoke the desktop executor");
var routeExecutorCalls = 0;
var routeConsentChecks = 0;
var routeProviderCalls = 0;
Task<string> RouteExecutor(string action)
{
    routeExecutorCalls++;
    return Task.FromResult("executed:" + action);
}
var localRoute = await ComputerUseChatDispatcher.DispatchBeforeChatAuthorizationAsync(
    "Mở VS Code", false, false, RouteExecutor, () =>
    {
        routeConsentChecks++;
        return Task.FromResult<string?>("403");
    });
if (localRoute.ContinueToChat) routeProviderCalls++;
Check(!localRoute.ContinueToChat && localRoute.Result == "executed:open-vscode" &&
      routeExecutorCalls == 1 && routeConsentChecks == 0 && routeProviderCalls == 0,
    "an approved local Computer Use action runs before denied chat-history consent and never calls the provider");
routeExecutorCalls = 0;
routeConsentChecks = 0;
routeProviderCalls = 0;
var deniedChatRoute = await ComputerUseChatDispatcher.DispatchBeforeChatAuthorizationAsync(
    "Dựa trên lịch sử cuộc trò chuyện, nhắc lại việc cũ", false, false, RouteExecutor, () =>
    {
        routeConsentChecks++;
        return Task.FromResult<string?>("403");
    });
if (deniedChatRoute.ContinueToChat) routeProviderCalls++;
Check(!deniedChatRoute.ContinueToChat && deniedChatRoute.Result == "403" &&
      routeExecutorCalls == 0 && routeConsentChecks == 1 && routeProviderCalls == 0,
    "normal chat with missing or revoked history consent is denied before the provider receives history");
routeConsentChecks = 0;
routeProviderCalls = 0;
var authorizedChatRoute = await ComputerUseChatDispatcher.DispatchBeforeChatAuthorizationAsync(
    "Dựa trên lịch sử cuộc trò chuyện, nhắc lại việc cũ", false, false, RouteExecutor, () =>
    {
        routeConsentChecks++;
        return Task.FromResult<string?>(null);
    });
if (authorizedChatRoute.ContinueToChat) routeProviderCalls++;
Check(authorizedChatRoute.ContinueToChat && authorizedChatRoute.Result is null &&
      routeExecutorCalls == 0 && routeConsentChecks == 1 && routeProviderCalls == 1,
    "normal chat continues to the provider only after history consent is granted");
var permissionDescription = System.Text.Json.JsonSerializer.Serialize(ComputerUsePolicy.Describe());
Check(permissionDescription.Contains("\"codeExecution\":false", StringComparison.Ordinal),
    "the exposed permission description keeps code execution disabled");
Check(WindowsDesktopAutomation.ReadOpenApprovedApplications().All(name =>
        name is "Visual Studio Code" or "Microsoft Edge" or "File Explorer" or "Visual Studio" or "Notepad" or "Windows Terminal" or
            "Windows Settings (read-only)" or "Task Manager (read-only)" or "Microsoft Word" or "Microsoft Excel" or
            "Microsoft PowerPoint" or "PDF Reader"),
    "application observation returns only approved app identities");
var eightLeafRoot = new UiTreeFixtureNode();
for (var index = 0; index < 8; index++) eightLeafRoot.Children.Add(new UiTreeFixtureNode());
var eightLeafNodes = ComputerUsePolicy.CollectBoundedDescendants(eightLeafRoot, 13, 4,
    (node, index) => index < node.Children.Count ? node.Children[index] : null, out var eightLeafTruncated);
Check(eightLeafNodes.Count == 8 && !eightLeafTruncated,
    "bounded UI traversal visits all eight leaf siblings below its element limit");
var oversizedRoot = new UiTreeFixtureNode();
for (var index = 0; index < 14; index++) oversizedRoot.Children.Add(new UiTreeFixtureNode());
var oversizedNodes = ComputerUsePolicy.CollectBoundedDescendants(oversizedRoot, 13, 4,
    (node, index) => index < node.Children.Count ? node.Children[index] : null, out var oversizedTruncated);
Check(oversizedNodes.Count == 13 && oversizedTruncated,
    "bounded UI traversal marks sibling overflow as partial");
var deepRoot = new UiTreeFixtureNode();
var deepChild = new UiTreeFixtureNode();
deepChild.Children.Add(new UiTreeFixtureNode());
deepRoot.Children.Add(deepChild);
var deepNodes = ComputerUsePolicy.CollectBoundedDescendants(deepRoot, 13, 1,
    (node, index) => index < node.Children.Count ? node.Children[index] : null, out var deepTruncated);
Check(deepNodes.Count == 1 && deepTruncated,
    "bounded UI traversal marks descendants beyond the depth limit as partial");
Check(DesktopOperator.SafeGitHubUrl("https://github.com/voquangthan/-t-t.git") == "https://github.com/voquangthan/-t-t",
    "HTTPS GitHub remote is canonicalized");
Check(DesktopOperator.SafeGitHubUrl("git@github.com:voquangthan/-t-t.git") == "https://github.com/voquangthan/-t-t",
    "SSH GitHub remote is canonicalized");
Check(DesktopOperator.SafeGitHubUrl("https://attacker.example/voquangthan/-t-t") is null,
    "non-GitHub remote is rejected");
Check(DesktopOperator.SafeGitHubUrl("https://user:token@github.com/owner/repo.git") is null,
    "credential-bearing GitHub remote is rejected without disclosure");

var temporaryRoot = Path.Combine(Path.GetTempPath(), "detot-desktop-operator-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
try
{
    var repo = Path.Combine(temporaryRoot, "repo");
    Directory.CreateDirectory(repo);
    RunGit(repo, "init", "--initial-branch=main");
    File.WriteAllText(Path.Combine(repo, "tracked.txt"), "synthetic local fixture\n");
    RunGit(repo, "add", "tracked.txt");
    RunGit(repo, "remote", "add", "origin", "https://github.com/voquangthan/-t-t.git");

    Check(DesktopOperator.FindRepositoryRoot(Path.Combine(temporaryRoot, "outside", "folder")) is null,
        "path outside a repository is rejected");
    Directory.CreateDirectory(Path.Combine(repo, "nested", "folder"));
    Check(DesktopOperator.FindRepositoryRoot(Path.Combine(repo, "nested", "folder")) == repo,
        "repository root is discovered from a nested path");
    Directory.CreateDirectory(Path.Combine(repo, "documents"));
    File.WriteAllText(Path.Combine(repo, "documents", "audit.txt"), "synthetic workspace item\n");
    Check(DesktopOperator.ResolveWorkspaceItemPath(repo, "documents/audit.txt") == Path.Combine(repo, "documents", "audit.txt") &&
          DesktopOperator.ResolveWorkspaceItemPath(repo, "documents") == Path.Combine(repo, "documents"),
        "only existing workspace files and folders resolve for Explorer navigation");
    Check(DesktopOperator.ResolveWorkspaceItemPath(repo, "../outside.txt") is null &&
          DesktopOperator.ResolveWorkspaceItemPath(repo, Path.Combine(temporaryRoot, "outside.txt")) is null &&
          DesktopOperator.ResolveWorkspaceItemPath(repo, "missing.txt") is null &&
          !DesktopOperator.IsSafeWorkspaceRelativePath("documents/../outside.txt"),
        "workspace navigation rejects traversal, rooted paths, and missing items");
    var status = await DesktopOperator.ReadWorkspaceStatusAsync(repo);
    Check(status.Available && status.Branch == "main", "read-only Git inspection returns the current branch");
    Check(status.HasTrackedChanges == true, "staged tracked change is detected without returning its path");
    Check(status.Repository == "https://github.com/voquangthan/-t-t", "status exposes only a sanitized GitHub URL");
    Check(!status.GetType().GetProperties().Any(property => property.Name.Contains("File", StringComparison.OrdinalIgnoreCase)),
        "status DTO contains no file list or file contents");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(temporaryRoot, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(temporaryRoot, recursive: true);
}

Console.WriteLine($"Desktop Operator checks: {checks}/{checks} PASS");

var node = FindExecutable("node.exe");
if (node is not null)
{
    var adapterRoot = CreateIsolatedTestDirectory("desktop-adapter");
    var script = Path.Combine(AppContext.BaseDirectory, "fake-edge-reader-" + Guid.NewGuid().ToString("N") + ".mjs");
    var profile = Path.Combine(adapterRoot, "profile");
    var moduleRoot = Path.Combine(adapterRoot, "playwright-core");
    Directory.CreateDirectory(moduleRoot);
    File.WriteAllText(script, "import readline from 'node:readline'; import fs from 'node:fs/promises'; for await (const line of readline.createInterface({input:process.stdin,crlfDelay:Infinity})) { const req=JSON.parse(line); if(req.action==='shutdown') { await fs.writeFile(process.env.DETOT_DESKTOP_EDGE_PROFILE+'/shutdown-marker','stopped'); break; } if(req.id===1) await new Promise(resolve=>setTimeout(resolve,1000)); process.stdout.write(JSON.stringify({id:req.id,result:{accepted:req.action==='open-edge',sentinelPresent:!!process.env.DETOT_TEST_SENTINEL}})+'\\n'); }");
    var previousSentinel = Environment.GetEnvironmentVariable("DETOT_TEST_SENTINEL");
    Environment.SetEnvironmentVariable("DETOT_TEST_SENTINEL", "synthetic-secret-sentinel");
    try
    {
        await using (var adapter = new PlaywrightEdgeAdapter(node, script, profile, moduleRoot))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                await adapter.ExecuteAsync("open-edge", null, cancellation.Token);
                Check(false, "caller cancellation is propagated while a browser command is pending");
            }
            catch (OperationCanceledException)
            {
                Check(true, "caller cancellation is propagated while a browser command is pending");
            }

            var result = await adapter.ExecuteAsync("open-edge", null);
            Check(result.GetProperty("accepted").GetBoolean(), "the next browser command succeeds after cancellation resets its worker");
            Check(!result.GetProperty("sentinelPresent").GetBoolean(), "browser child process does not inherit unrelated secret environment variables");
            var rejected = await adapter.ExecuteAsync("run-shell", null);
            Check(rejected.GetProperty("error").GetString() == "action_not_allowed", "browser adapter rejects unlisted actions before starting a worker");
            var rejectedRepository = await adapter.ExecuteAsync("read-github", "https://attacker.example/owner/repo", CancellationToken.None);
            Check(rejectedRepository.GetProperty("error").GetString() == "repository_not_allowed", "browser adapter rejects repositories outside the GitHub allowlist");
        }
        Check(File.Exists(Path.Combine(profile, "shutdown-marker")), "cancelled browser worker receives shutdown and exits before reuse");
    }
    finally
    {
        Environment.SetEnvironmentVariable("DETOT_TEST_SENTINEL", previousSentinel);
        if (Directory.Exists(adapterRoot)) Directory.Delete(adapterRoot, recursive: true);
        if (File.Exists(script)) File.Delete(script);
    }
}
else Console.WriteLine("SKIP: node.exe not found; child process protocol checks not run.");

Console.WriteLine($"Desktop Operator + Adapter checks: {checks}/{checks} PASS");

if (args.Contains("--launch-app-smoke", StringComparer.Ordinal))
{
    var vscode = DesktopOperator.OpenKnownApplication("vscode");
    Check(vscode.Succeeded, "Windows accepts a request to open allowlisted VS Code");
    Console.WriteLine($"VS Code UIA window confirmation: {(vscode.WindowVerified == true ? "PASS" : "UNAVAILABLE in this test session")}");
    var edge = DesktopOperator.OpenKnownApplication("edge");
    Check(edge.Succeeded, "Windows accepts a request to open allowlisted Edge");
    Console.WriteLine($"Edge UIA window confirmation: {(edge.WindowVerified == true ? "PASS" : "UNAVAILABLE in this test session")}");
}

if (args.Contains("--github-read-smoke", StringComparer.Ordinal))
{
    if (node is null) throw new InvalidOperationException("node.exe is required for the Playwright smoke test.");
    var repositoryRoot = DesktopOperator.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("repository root unavailable");
    var profile = CreateIsolatedTestDirectory("edge-read-smoke");
    var adapter = new PlaywrightEdgeAdapter(node,
        Path.Combine(repositoryRoot, "detot-lab", "tools", "computer-use", "edge-reader.mjs"), profile,
        Path.Combine(repositoryRoot, "detot-lab", "tests", "runtime-candidate", "node_modules", "playwright-core"));
    await using (adapter)
    {
        var result = await adapter.ExecuteAsync("read-github", "https://github.com/voquangthan/-t-t");
        if (result.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"GitHub read smoke failed: {error.GetString()}");
        var pullRequests = result.GetProperty("pullRequests");
        Check(pullRequests.ValueKind == JsonValueKind.Array, "Playwright reads the configured repository PR and CI summary");
        var checkCount = pullRequests.EnumerateArray().Sum(pr => pr.GetProperty("checks").GetArrayLength());
        Console.WriteLine($"GitHub read smoke: {pullRequests.GetArrayLength()} PRs, {checkCount} CI checks read; no PR body, comments, files, or credentials.");
    }
    if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
}

if (args.Contains("--edge-open-smoke", StringComparer.Ordinal))
{
    if (node is null) throw new InvalidOperationException("node.exe is required for the Edge launch smoke test.");
    var repositoryRoot = DesktopOperator.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("repository root unavailable");
    var profile = CreateIsolatedTestDirectory("edge-open-smoke");
    var adapter = new PlaywrightEdgeAdapter(node,
        Path.Combine(repositoryRoot, "detot-lab", "tools", "computer-use", "edge-reader.mjs"), profile,
        Path.Combine(repositoryRoot, "detot-lab", "tests", "runtime-candidate", "node_modules", "playwright-core"));
    try
    {
        await using (adapter)
        {
            var result = await adapter.ExecuteAsync("open-edge", null);
            if (result.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"Edge launch smoke failed: {error.GetString()}");
            Check(result.TryGetProperty("opened", out var opened) && opened.ValueKind == JsonValueKind.True,
                "Playwright opens Edge with an isolated lab profile");
        }
    }
    finally
    {
        if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
    }
}

if (args.Contains("--vscode-open-smoke", StringComparer.Ordinal))
{
    var result = DesktopOperator.OpenKnownApplication("vscode");
    if (!result.Succeeded) throw new InvalidOperationException("VS Code launch smoke failed.");
    Check(result.WindowVerified == true, "Windows UI Automation confirms the allowlisted VS Code window");
}

static void RunGit(string directory, params string[] arguments)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
    start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
    start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start fixture Git.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"Git fixture setup failed: {error}{output}");
}

static string? FindExecutable(string name)
{
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
    {
        var candidate = Path.Combine(directory, name);
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

static string CreateIsolatedTestDirectory(string purpose)
{
    var root = Path.Combine(AppContext.BaseDirectory, ".computer-use-test-data");
    var directory = Path.Combine(root, purpose + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}

sealed class UiTreeFixtureNode
{
    public List<UiTreeFixtureNode> Children { get; } = [];
}
