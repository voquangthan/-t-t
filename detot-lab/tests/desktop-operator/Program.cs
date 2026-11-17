using System.Diagnostics;
using System.Net;
using System.Text.Json;

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
Check(!ComputerUsePolicy.CanObserve("desktop-screenshot"), "unlisted observation targets are denied");
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
Check(!ComputerUsePolicy.CanNavigate("run-shell"), "navigation cannot be used to run shell commands");
Check(!ComputerUsePolicy.CanWrite("edit-file"), "write actions remain hard-blocked");
Check(!ComputerUsePolicy.CanWrite("run-tests"), "project code execution remains hard-blocked");
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
