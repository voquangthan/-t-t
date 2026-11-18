using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

public static class DesktopOperator
{
    private static readonly Regex GitHubRemote = new(
        @"^(?:https://github\.com/|git@github\.com:)(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed record WorkspaceStatus(bool Available, string? Repository, string? Branch,
        bool? HasTrackedChanges, string? Message);

    public sealed record ActionResult(bool Succeeded, string Message, bool? WindowVerified = null);
    public sealed record ChatActionResponse(string Reply, bool ToolUsed, string ComputerUseAction,
        bool? WindowVerified);

    public static ChatActionResponse ToChatActionResponse(ActionResult result, string action) =>
        new(result.Message, true, action, result.WindowVerified);

    public static bool IsAvailable(bool isWindows, bool hasPublicOrigin, IPAddress? remoteAddress) =>
        isWindows && !hasPublicOrigin && remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);

    public static string? FindRepositoryRoot(string contentRoot)
    {
        DirectoryInfo? current = new DirectoryInfo(Path.GetFullPath(contentRoot));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    public static bool IsSafeWorkspaceRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 512 ||
            Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Contains('\0')) return false;
        var segments = relativePath.Replace('\\', '/').Split('/');
        return segments.Length > 0 && segments.All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    public static string? ResolveWorkspaceItemPath(string? workspaceRoot, string? relativePath)
    {
        if (workspaceRoot is null || !Directory.Exists(workspaceRoot) || !IsSafeWorkspaceRelativePath(relativePath)) return null;
        try
        {
            var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(root, relativePath!.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                (!File.Exists(target) && !Directory.Exists(target))) return null;

            var current = root;
            foreach (var segment in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            }
            return target;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static async Task<WorkspaceStatus> ReadWorkspaceStatusAsync(string? repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        if (repositoryRoot is null || !Directory.Exists(repositoryRoot))
            return new(false, null, null, null, "Không tìm thấy workspace Git.");

        try
        {
            var status = await RunGitAsync(repositoryRoot,
                ["status", "--porcelain=v1", "--branch", "--untracked-files=no"], cancellationToken);
            if (status.ExitCode != 0) return new(false, null, null, null, "Không đọc được trạng thái Git.");

            var lines = status.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var header = lines.FirstOrDefault(line => line.StartsWith("## ", StringComparison.Ordinal));
            var branchPart = header is null ? null : header[3..].Split("...", 2, StringSplitOptions.None)[0];
            if (branchPart?.StartsWith("No commits yet on ", StringComparison.Ordinal) == true)
                branchPart = branchPart[18..];
            var branch = branchPart is not null && Regex.IsMatch(branchPart, @"^[A-Za-z0-9._/-]{1,160}$")
                ? branchPart : null;
            var modifiedCount = lines.Count(line => line.Length >= 3 && !line.StartsWith("## ", StringComparison.Ordinal));

            var remote = await RunGitAsync(repositoryRoot, ["remote", "get-url", "origin"], cancellationToken);
            var repositoryUrl = remote.ExitCode == 0 ? SafeGitHubUrl(remote.Output.Trim()) : null;
            return new(true, repositoryUrl, branch, modifiedCount > 0,
                repositoryUrl is null ? "Đã kiểm tra Git; remote origin không phải GitHub hợp lệ." : null);
        }
        catch (TimeoutException)
        {
            return new(false, null, null, null, "Kiểm tra Git quá thời gian cho phép.");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new(false, null, null, null, "Không thể kiểm tra Git trong workspace này.");
        }
    }

    public static ActionResult OpenWorkspace(string? repositoryRoot)
    {
        if (!OperatingSystem.IsWindows() || repositoryRoot is null || !Directory.Exists(repositoryRoot))
            return new(false, "Workspace không khả dụng trên máy này.");
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { repositoryRoot }
            });
            return new(true, "Đã mở workspace trong File Explorer.");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, "Không mở được File Explorer.");
        }
    }

    public static ActionResult OpenGitHub(string? repositoryUrl)
    {
        if (!OperatingSystem.IsWindows() || repositoryUrl is null || SafeGitHubUrl(repositoryUrl) != repositoryUrl)
            return new(false, "Không có địa chỉ GitHub hợp lệ để mở.");
        try
        {
            Process.Start(new ProcessStartInfo(repositoryUrl) { UseShellExecute = true });
            return new(true, "Đã mở repository GitHub trong trình duyệt mặc định.");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, "Không mở được trình duyệt.");
        }
    }

    public static ActionResult OpenKnownApplication(string application)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Mở ứng dụng chỉ khả dụng trên Windows.");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var candidates = application switch
        {
            "edge" => new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
            },
            "vscode" => new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
                Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
                string.IsNullOrWhiteSpace(userProfile) ? "" : Path.Combine(userProfile, "AppData", "Local", "Programs", "Microsoft VS Code", "Code.exe")
            },
            _ => []
        };
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null) return new(false, "Không tìm thấy ứng dụng đã cho phép.");
        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            var processName = application == "vscode" ? "Code" : "msedge";
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (WindowsDesktopAutomation.HasVisibleWindow(processName))
                    return new(true, application == "edge"
                        ? "Đã gửi yêu cầu mở và xác nhận cửa sổ Microsoft Edge qua UI Automation."
                        : "Đã gửi yêu cầu mở và xác nhận cửa sổ Visual Studio Code qua UI Automation.", true);
                Thread.Sleep(250);
            }
            return new(true, "Windows đã nhận yêu cầu mở ứng dụng; phiên Backend này chưa xác nhận được cửa sổ qua UI Automation.", false);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, "Không mở được ứng dụng.");
        }
    }

    public static string? SafeGitHubUrl(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return null;
        var match = GitHubRemote.Match(remote.Trim());
        if (!match.Success) return null;
        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;
        if (owner is "." or ".." || repo is "." or "..") return null;
        return $"https://github.com/{owner}/{repo}";
    }

    public static string? SafeGitHubRepository(string? remote)
    {
        var url = SafeGitHubUrl(remote);
        if (url is null) return null;
        var path = new Uri(url).AbsolutePath.Trim('/');
        return Regex.IsMatch(path, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)
            ? path : null;
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(string repositoryRoot,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.fsmonitor=false");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.untrackedCache=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_PAGER"] = "cat";

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start Git.");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            _ = await error;
            return (process.ExitCode, await output);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Git status exceeded its time limit.");
        }
    }
}
