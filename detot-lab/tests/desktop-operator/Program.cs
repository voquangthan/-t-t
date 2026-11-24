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
Check(ComputerUsePlanner.MightBeActionRequest("Mở VS Code rồi xem các PR và CI") &&
      ComputerUsePlanner.MightBeActionRequest("Mở Edge, bấm tab Gmail, rồi cuộn xuống") &&
      !ComputerUsePlanner.MightBeActionRequest("How do I open Edge?") &&
      !ComputerUsePlanner.MightBeActionRequest("Làm sao bấm nút đăng nhập an toàn?") &&
      !ComputerUsePlanner.MightBeActionRequest("Ví dụ: `open Edge` rồi chạy lệnh này") &&
      !ComputerUsePlanner.MightBeActionRequest("chào buổi sáng"),
    "multi-step planning is attempted only for short, direct action requests");
Check(ComputerUsePlanner.TryParse("""{"handled":true,"actions":["open-vscode","open-workspace","read-github-pull-requests"]}""",
      out var acceptedPlan) && acceptedPlan is { Handled: true } &&
      acceptedPlan.Actions.SequenceEqual(["open-vscode", "open-workspace", "read-github-pull-requests"]),
    "LLM plans accept ordered sequences of existing allowlisted actions");
Check(ComputerUsePlanner.TryParse("""{"handled":false,"actions":[]}""", out var notHandledPlan) &&
      notHandledPlan is { Handled: false, Actions.Count: 0 },
    "LLM can defer ordinary questions to normal chat");
Check(!ComputerUsePlanner.TryParse("""{"handled":true,"actions":["run-shell"]}""", out _) &&
      !ComputerUsePlanner.TryParse("""{"handled":true,"actions":["open-vscode","open-vscode"]}""", out _) &&
      !ComputerUsePlanner.TryParse("""{"handled":true,"actions":[{"action":"open-vscode"}]}""", out _) &&
      !ComputerUsePlanner.TryParse("""{"handled":true,"actions":["open-vscode"],"command":"anything"}""", out _) &&
      !ComputerUsePlanner.TryParse("""{"handled":true,"actions":["open-vscode","open-edge","open-github","read-github-account","read-github-pull-requests","read-approved-applications"]}""", out _),
    "LLM plans reject shell, duplicates, dynamic arguments, extra properties, and overlong plans");
using (var computerUseTool = JsonDocument.Parse(JsonSerializer.Serialize(ComputerUsePlanner.CreateToolDefinition())))
{
    var toolRoot = computerUseTool.RootElement;
    var actionSchema = toolRoot.GetProperty("parameters").GetProperty("properties").GetProperty("actions");
    Check(toolRoot.GetProperty("name").GetString() == ComputerUsePlanner.ToolName &&
          actionSchema.GetProperty("maxItems").GetInt32() == 5 &&
          actionSchema.GetProperty("uniqueItems").GetBoolean() &&
          actionSchema.GetProperty("items").GetProperty("enum").EnumerateArray()
              .Select(item => item.GetString()).SequenceEqual(ComputerUsePlanner.AllowedActionIds),
        "Gateway Computer Use tool exposes only the bounded existing permission action IDs");
}
Check(ComputerUseUiPlanner.TryParse("""{"operation":"observe","app":"edge"}""", out var observeUi) &&
      observeUi is { Operation: "observe", App: "edge" } &&
      ComputerUseUiPlanner.TryParse("""{"operation":"invoke","app":"vscode","observationId":"00000000000000000000000000000000","elementId":"e12"}""", out var invokeUi) &&
      invokeUi is { Operation: "invoke", App: "vscode", ElementId: "e12" } &&
      ComputerUseUiPlanner.TryParse("""{"operation":"type","app":"edge","observationId":"00000000000000000000000000000000","elementId":"e3","text":"ASP.NET Core"}""", out var typeUi) &&
      typeUi is { Text: "ASP.NET Core" } &&
      ComputerUseUiPlanner.TryParse("""{"operation":"navigate","app":"edge","url":"https://github.com/voquangthan/-t-t/pulls"}""", out var navigateUi) &&
      navigateUi is { Operation: "navigate", Url: "https://github.com/voquangthan/-t-t/pulls" } &&
      ComputerUseUiPlanner.TryParse("""{"operation":"activate-window","app":"vscode","windowId":"00000000000000000000000000000001"}""", out var activateWindowUi) &&
      activateWindowUi is { Operation: "activate-window", WindowId: "00000000000000000000000000000001" },
    "generic UI planner parses one bounded operation using app-scoped recent observation IDs");
var syntheticTokenText = string.Concat("ghp_", new string('a', 26), new string('0', 10));
var syntheticTokenRequest = JsonSerializer.Serialize(new
{
    operation = "type", app = "edge", observationId = "00000000000000000000000000000000", elementId = "e3", text = syntheticTokenText
});
Check(!ComputerUseUiPlanner.TryParse("""{"operation":"invoke","app":"WindowsTerminal","observationId":"00000000000000000000000000000000","elementId":"e2"}""", out _) &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"run-shell","app":"edge"}""", out _) &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"observe","app":"edge","command":"whoami"}""", out _) &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"type","app":"edge","observationId":"00000000000000000000000000000000","elementId":"e3","text":"password is abc"}""", out _) &&
      !ComputerUseUiPlanner.TryParse(syntheticTokenRequest, out _) &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"navigate","app":"edge","url":"http://127.0.0.1:5183"}""", out _) &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"navigate","app":"edge","url":"https://user:pass@example.com"}""", out _) &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"activate-window","app":"vscode","windowId":"not-a-guid"}""", out _) &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"invoke","app":"edge","observationId":"bad","elementId":"e3"}""", out _),
    "generic UI planner rejects terminal, shell, injected fields, secret-like text, and invalid evidence IDs");
Check(ComputerUseUiPlanner.TryParse("""{"operation":"observe","app":"desktop"}""", out var genericDesktop) &&
      genericDesktop is { Operation: "observe", App: "desktop" } &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"launch","app":"desktop"}""", out _) &&
      !ComputerUseUiPlanner.CanObserveDesktopProcess("WindowsTerminal") &&
      !ComputerUseUiPlanner.CanObserveDesktopProcess("KeePass.exe") &&
      !ComputerUseUiPlanner.CanObserveDesktopProcess("CredentialUIBroker") &&
      ComputerUseUiPlanner.CanObserveDesktopProcess("WINWORD.exe") &&
      ComputerUseUiPlanner.CanObserveDesktopProcess("msedge"),
    "generic desktop observation targets running user apps while excluding terminals, credential tools and security surfaces");
Check(ComputerUseUiPlanner.TryParse("""{"operation":"launch-installed","app":"desktop","text":"Word"}""", out var launchInstalled) &&
      launchInstalled is { Operation: "launch-installed", App: "desktop", Text: "Word" } &&
      ComputerUseUiPlanner.IsExactUserText("Word", "Mở Word") &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"launch-installed","app":"desktop","text":"C:\\Windows\\System32\\cmd.exe"}""", out _) &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"launch-installed","app":"edge","text":"Word"}""", out _) &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"launch-installed","app":"desktop","text":"PowerShell"}""", out _) &&
      !ComputerUseUiPlanner.CanObserveDesktopProcess("mshta.exe"),
    "installed app launch accepts only the exact requested safe app name, never paths or shell names");
var shortcutTestRoot = Path.Combine(Path.GetTempPath(), "detot-shortcut-test-" + Guid.NewGuid().ToString("N"));
try
{
    var shortcutFolder = Path.Combine(shortcutTestRoot, "Programs", "Office");
    Directory.CreateDirectory(shortcutFolder);
    File.WriteAllText(Path.Combine(shortcutFolder, "Word.lnk"), "test shortcut placeholder");
    File.WriteAllText(Path.Combine(shortcutFolder, "WordPad.lnk"), "test shortcut placeholder");
    var shortcutSearch = InstalledApplicationShortcutResolver.FindExact([Path.Combine(shortcutTestRoot, "Programs")], "Word");
    Check(shortcutSearch.Complete && shortcutSearch.Matches.Count == 1 && shortcutSearch.Matches[0].DisplayName == "Word" &&
          InstalledApplicationShortcutResolver.IsTrustedExecutableTarget(
              Path.Combine(shortcutTestRoot, "Program Files", "Office", "WINWORD.EXE"),
              [Path.Combine(shortcutTestRoot, "Program Files")]) &&
          !InstalledApplicationShortcutResolver.IsTrustedExecutableTarget(
              Path.Combine(shortcutTestRoot, "Downloads", "untrusted.exe"),
              [Path.Combine(shortcutTestRoot, "Program Files")]) &&
          !InstalledApplicationShortcutResolver.IsTrustedExecutableTarget(
              Path.Combine(shortcutTestRoot, "Program Files", "WindowsPowerShell", "powershell.exe"),
              [Path.Combine(shortcutTestRoot, "Program Files")]),
        "installed app resolution is exact and launch targets must be trusted executables");
}
finally
{
    Directory.Delete(shortcutTestRoot, recursive: true);
}
Check(ComputerUseUiPlanner.IsExactUserText("đặt lịch họp", "hãy nhập 'đặt lịch họp' vào ô tìm kiếm") &&
      !ComputerUseUiPlanner.IsExactUserText("xóa dữ liệu", "hãy nhập tên vào ô tìm kiếm"),
    "typing is restricted to exact text present in the user's current request");
Check(ComputerUseUiPlanner.IsSafeTypingSurface("edge", "Edit", "Address and search bar") &&
      ComputerUseUiPlanner.IsSafeTypingSurface("explorer", "Edit", "Search") &&
      ComputerUseUiPlanner.IsSafeTypingSurface("edge", "Edit", "Message body") &&
      !ComputerUseUiPlanner.IsSafeTypingSurface("edge", "Edit", "Password") &&
      ComputerUseUiPlanner.IsSafeTypingSurface("vscode", "Edit", "Search") &&
      !ComputerUseUiPlanner.IsSafeTypingSurface("explorer", "Edit", "File name") &&
      !ComputerUseUiPlanner.IsSafeTypingSurface("edge", "Edit", "Credit card number") &&
      !ComputerUseUiPlanner.IsSafeTypingSurface("edge", "Edit", "Code Editor") &&
      !ComputerUseUiPlanner.IsSafeTypingSurface("vscode", "Edit", "Code Editor") &&
      !ComputerUseUiPlanner.IsExactUserText("person@example.com", "search person@example.com") &&
      !ComputerUseUiPlanner.IsExactUserText("+84 912 345 678", "search +84 912 345 678"),
    "ordinary Edge text entry is enabled while identity, financial, file-name and code surfaces remain blocked");
Check(ComputerUseUiPlanner.ClassifyAction("invoke", "TabItem", "Calendar") == "allowed" &&
      ComputerUseUiPlanner.ClassifyAction("invoke", "Button", "Save") == "confirmation_required" &&
      ComputerUseUiPlanner.ClassifyAction("invoke", "Hyperlink", "Download report") == "confirmation_required" &&
      ComputerUseUiPlanner.ClassifyAction("invoke", "Button", "Delete") == "denied" &&
      ComputerUseUiPlanner.ClassifyAction("invoke", "Button", "Restart") == "denied" &&
      ComputerUseUiPlanner.ClassifyAction("type", "Edit", "Search") == "allowed" &&
      ComputerUseUiPlanner.ClassifyAction("invoke", "Button", "Run") == "denied",
    "UI action classification allows navigation and gates save/delete/run controls");
using (var uiTool = JsonDocument.Parse(JsonSerializer.Serialize(ComputerUseUiPlanner.CreateToolDefinition())))
{
    var root = uiTool.RootElement;
    var properties = root.GetProperty("parameters").GetProperty("properties");
    Check(root.GetProperty("name").GetString() == ComputerUseUiPlanner.ToolName &&
          properties.GetProperty("operation").GetProperty("enum").EnumerateArray()
              .Select(item => item.GetString()).SequenceEqual(ComputerUseUiPlanner.AllowedOperations) &&
          properties.GetProperty("app").GetProperty("enum").EnumerateArray()
              .Select(item => item.GetString()).SequenceEqual(ComputerUseUiPlanner.AllowedApps),
        "Gateway UI tool exposes only fixed app and operation enums without command or URL parameters");
}
using (var verifiedGithub = JsonDocument.Parse("""{"windowVerified":true,"urlVerified":true}"""))
using (var missingGithubEvidence = JsonDocument.Parse("""{"windowVerified":true,"urlVerified":false}"""))
using (var signedOutAccount = JsonDocument.Parse("""{"status":"signed_out"}"""))
using (var uncertainAccount = JsonDocument.Parse("""{"status":"not_verified"}"""))
using (var partialApps = JsonDocument.Parse("""{"computerUseObservationStatus":"partial"}"""))
using (var failedResult = JsonDocument.Parse("""{"error":"unavailable"}"""))
{
    Check(ComputerUsePlanner.IsVerifiedActionResult("open-github", 200, verifiedGithub.RootElement) &&
          !ComputerUsePlanner.IsVerifiedActionResult("open-github", 200, missingGithubEvidence.RootElement) &&
          !ComputerUsePlanner.IsVerifiedActionResult("open-github", 502, verifiedGithub.RootElement),
        "multi-step navigation requires successful HTTP status and exact window/URL evidence");
    Check(ComputerUsePlanner.IsVerifiedActionResult("read-github-account", 200, signedOutAccount.RootElement) &&
          !ComputerUsePlanner.IsVerifiedActionResult("read-github-account", 200, uncertainAccount.RootElement) &&
          !ComputerUsePlanner.IsVerifiedActionResult("read-approved-applications", 200, partialApps.RootElement) &&
          !ComputerUsePlanner.IsVerifiedActionResult("open-edge", 200, failedResult.RootElement),
        "multi-step observations fail closed on uncertain, partial, or error results");
}
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
Check(ComputerUsePolicy.CanUseUiOperation("edge", "observe") &&
      ComputerUsePolicy.CanUseUiOperation("vscode", "invoke") &&
      !ComputerUsePolicy.CanUseUiOperation("WindowsTerminal", "observe") &&
      !ComputerUsePolicy.CanUseUiOperation("edge", "run-shell"),
    "universal UI policy accepts only approved apps and bounded UI operations");
Check(ComputerUsePolicy.ShouldExposeInteractiveUiTool(normalChat: true, hasOtherTools: false, desktopAvailable: true, uiDataConsent: true) &&
      !ComputerUsePolicy.ShouldExposeInteractiveUiTool(normalChat: false, hasOtherTools: false, desktopAvailable: true, uiDataConsent: true) &&
      !ComputerUsePolicy.ShouldExposeInteractiveUiTool(normalChat: true, hasOtherTools: true, desktopAvailable: true, uiDataConsent: true) &&
      !ComputerUsePolicy.ShouldExposeInteractiveUiTool(normalChat: true, hasOtherTools: false, desktopAvailable: false, uiDataConsent: true) &&
      !ComputerUsePolicy.ShouldExposeInteractiveUiTool(normalChat: true, hasOtherTools: false, desktopAvailable: true, uiDataConsent: false),
    "semantic UI tool has no keyword gate and preserves mode, tool, runtime and explicit data-consent gates");
var consentTestRoot = Path.Combine(Path.GetTempPath(), "detot-ui-consent-" + Guid.NewGuid().ToString("N"));
try
{
    var consent = new ConsentPolicy(consentTestRoot, new EphemeralDataProtectionProvider());
    Check(!consent.IsAllowed("owner", "computer_use", "direct_request", "DeepSeek"),
        "sending UIA labels and results to DeepSeek defaults to deny");
    consent.Set("owner", "computer_use", "direct_request", "DeepSeek", true, "owner", Guid.NewGuid());
    Check(consent.IsAllowed("owner", "computer_use", "direct_request", "DeepSeek"),
        "explicit one-time UI data consent enables semantic multi-step planning");
    consent.Set("owner", "computer_use", "direct_request", "DeepSeek", false, "owner", Guid.NewGuid());
    Check(!consent.IsAllowed("owner", "computer_use", "direct_request", "DeepSeek"),
        "revoking UI data consent disables subsequent UI tool availability");
}
finally { if (Directory.Exists(consentTestRoot)) Directory.Delete(consentTestRoot, recursive: true); }
Check(!ComputerUsePolicy.CanNavigate("run-shell"), "navigation cannot be used to run shell commands");
Check(!ComputerUsePolicy.CanWrite("edit-file"), "write actions remain hard-blocked");
Check(!ComputerUsePolicy.CanWrite("run-tests"), "project code execution remains hard-blocked");
Check(DesktopCompanionProtocol.IsAllowedAction("read-workspace") &&
      DesktopCompanionProtocol.IsAllowedAction("read-github-pull-requests") &&
      DesktopCompanionProtocol.IsAllowedAction("read-github-account"),
    "Desktop Companion exposes only explicit read action IDs");
var uiRequestJson = DesktopCompanionProtocol.SerializeUiRequest(
    new ComputerUseUiAction("observe", "edge", null, null, null, null, null, null), Guid.NewGuid().ToString("N"));
Check(DesktopCompanionProtocol.TryParseRequest(uiRequestJson, out var uiRequest) &&
      uiRequest?.Action == "ui-operation" && uiRequest.UiAction is { Operation: "observe", App: "edge" } &&
      !DesktopCompanionProtocol.IsAllowedAction("run-shell"),
    "authenticated Companion protocol carries only validated universal UI operations");
var selectWindowAction = new ComputerUseUiAction("activate-window", "vscode", null, null,
    "00000000000000000000000000000001", null, null, null);
var selectWindowJson = DesktopCompanionProtocol.SerializeUiRequest(selectWindowAction, Guid.NewGuid().ToString("N"));
Check(DesktopCompanionProtocol.TryParseRequest(selectWindowJson, out var selectedWindowRequest) &&
      selectedWindowRequest?.UiAction == selectWindowAction,
    "window-selection IDs survive authenticated IPC validation for explicit window activation");
var confirmedUiRequestJson = DesktopCompanionProtocol.SerializeUiRequest(
    new ComputerUseUiAction("invoke", "edge", "00000000000000000000000000000000", "e4", null, null, null, null),
    Guid.NewGuid().ToString("N"), userConfirmed: true);
Check(DesktopCompanionProtocol.TryParseRequest(confirmedUiRequestJson, out var confirmedUiRequest) &&
      confirmedUiRequest is { Action: "confirmed-ui-operation", UserConfirmed: true } &&
      !ComputerUseUiPlanner.TryParse("""{"operation":"invoke","app":"edge","observationId":"00000000000000000000000000000000","elementId":"e4","userConfirmed":true}""", out _),
    "explicit-confirmation IPC is separate from and cannot be forged through the model tool schema");
var approvalNow = DateTimeOffset.Parse("2026-11-22T10:00:00Z");
var approvalAction = new ComputerUseUiAction("invoke", "edge", "00000000000000000000000000000000", "e4", null, null, null, null);
var approvals = new ComputerUseApprovalQueue(() => "A1B2C3D4");
var approvalChallenge = approvals.Create("owner-a", approvalAction, approvalNow);
Check(approvalChallenge.Code == "A1B2C3D4" && approvalChallenge.ExpiresAt == approvalNow.AddMinutes(2) &&
      !approvals.TryConsume("owner-b", "xác nhận mã A1B2C3D4", approvalNow, out _) &&
      !approvals.TryConsume("owner-a", "xác nhận mã WRONG000", approvalNow, out _) &&
      approvals.TryConsume("owner-a", "xác nhận mã A1B2C3D4", approvalNow, out var consumedApproval) &&
      consumedApproval == approvalAction && !approvals.TryConsume("owner-a", "xác nhận mã A1B2C3D4", approvalNow, out _),
    "risky UI approval is owner-scoped, exact-code, one-time, and separately consumed");
var expiredApprovals = new ComputerUseApprovalQueue(() => "11223344");
expiredApprovals.Create("owner-a", approvalAction, approvalNow);
Check(!expiredApprovals.TryConsume("owner-a", "confirm 11223344", approvalNow.AddMinutes(2), out _) &&
      !expiredApprovals.HasPending("owner-a"), "expired UI approvals cannot execute or be replayed");
Check(!DesktopCompanionProtocol.IsAllowedAction("run-shell") &&
      !DesktopCompanionProtocol.IsAllowedAction("open-url:https://attacker.example") &&
      !DesktopCompanionProtocol.IsAllowedAction("read-file"),
    "Desktop Companion rejects arbitrary commands, URLs, and file reads");
var companionRequest = DesktopCompanionProtocol.SerializeRequest("open-vscode", Guid.NewGuid().ToString("N"));
Check(DesktopCompanionProtocol.TryParseRequest(companionRequest, out var parsedCompanionRequest) &&
      parsedCompanionRequest?.Action == "open-vscode",
    "Desktop Companion protocol accepts a fixed action and opaque request ID");
Check(!DesktopCompanionProtocol.TryParseRequest("{\"action\":\"open-edge\",\"requestId\":\"00000000000000000000000000000000\",\"command\":\"whoami\"}", out _) &&
      !DesktopCompanionProtocol.TryParseRequest("{\"action\":\"run-shell\",\"requestId\":\"00000000000000000000000000000000\"}", out _),
    "Desktop Companion rejects injected properties and unknown actions");
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
var testPipeName = $"detot-test-{Guid.NewGuid():N}";
await using (var pipeServer = new NamedPipeServerStream(testPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
{
    var serverReply = Task.Run(async () =>
    {
        using var serverDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await pipeServer.WaitForConnectionAsync(serverDeadline.Token);
            using var pipeReader = new StreamReader(pipeServer, Encoding.UTF8, false, 1024, leaveOpen: true);
            var line = await DesktopCompanionProtocol.ReadBoundedLineAsync(pipeReader, 4096, serverDeadline.Token);
            if (!DesktopCompanionProtocol.TryParseRequest(line, out var received) || received is null) return false;
            await using var pipeWriter = new StreamWriter(pipeServer, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await pipeWriter.WriteLineAsync(JsonSerializer.Serialize(new
            {
                requestId = received.RequestId,
                result = new { succeeded = true, action = received.Action }
            }));
            return true;
        }
        catch (OperationCanceledException) { return false; }
    });
    using var clientDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    JsonElement companionResult;
    try { companionResult = await new DesktopCompanionClient(testPipeName).ExecuteAsync("read-workspace", clientDeadline.Token); }
    catch (OperationCanceledException) { companionResult = JsonSerializer.SerializeToElement(new { error = "companion_timeout" }); }
    var serverCompleted = await serverReply.WaitAsync(TimeSpan.FromSeconds(12));
    if (companionResult.TryGetProperty("error", out var pipeError))
        Console.WriteLine($"BLOCKED: named-pipe integration self-test is unavailable in this execution session ({pipeError.GetString()}); interactive Companion smoke is required.");
    else
        Check(serverCompleted && companionResult.TryGetProperty("action", out var acceptedAction) &&
              acceptedAction.GetString() == "read-workspace",
            "authenticated local named-pipe request and response complete with correlation validation");
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
        name is "Visual Studio Code" or "Microsoft Edge" or "File Explorer" or "Visual Studio" or "Notepad" or "Windows Terminal"),
    "application observation returns only approved app identities");
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
