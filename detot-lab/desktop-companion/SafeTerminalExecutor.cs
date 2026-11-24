using System.Diagnostics;
using System.Text;

/// <summary>Runs a small fixed read-only command catalog in the configured lab workspace.</summary>
internal sealed class SafeTerminalExecutor(string workspaceRoot)
{
    private const int MaximumOutputCharacters = 8192;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);

    public async Task<object> ExecuteAsync(string commandId, CancellationToken cancellationToken)
    {
        if (!ComputerUseCommandPlanner.CommandIds.Contains(commandId, StringComparer.Ordinal))
            return new { status = "command_not_allowed", exitCode = (int?)null };

        var start = CreateStartInfo(commandId, workspaceRoot);
        if (start is null) return new { status = "command_not_allowed", exitCode = (int?)null };
        start.WorkingDirectory = workspaceRoot;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return new { status = "launch_failed", exitCode = (int?)null };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(MaximumDuration);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                return new
                {
                    status = "completed",
                    commandId,
                    workingDirectory = workspaceRoot,
                    exitCode = process.ExitCode,
                    stdout = stdout.Text,
                    stderr = stderr.Text,
                    outputTruncated = stdout.Truncated || stderr.Truncated,
                    elevated = false
                };
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
                if (cancellationToken.IsCancellationRequested)
                    return new { status = "cancelled", commandId, exitCode = (int?)null };
                return new { status = "timed_out", commandId, exitCode = (int?)null };
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
                                      IOException or UnauthorizedAccessException)
        {
            return new { status = "launch_failed", commandId, exitCode = (int?)null };
        }
    }

    internal static ProcessStartInfo? CreateStartInfo(string commandId, string workspaceRoot)
    {
        var info = commandId switch
        {
            "powershell-date" => new ProcessStartInfo(PowerShellPath())
            {
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Get-Date -Format o" }
            },
            "powershell-location" => new ProcessStartInfo(PowerShellPath())
            {
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Get-Location | Select-Object -ExpandProperty Path" }
            },
            "cmd-version" => new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                ArgumentList = { "/d", "/c", "ver" }
            },
            "git-status" when FindGit() is { } gitStatus => new ProcessStartInfo(gitStatus)
            {
                ArgumentList = { "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "--no-optional-locks", "status", "--short" }
            },
            "git-branch" when FindGit() is { } gitBranch => new ProcessStartInfo(gitBranch)
            {
                ArgumentList = { "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "branch", "--show-current" }
            },
            "dotnet-version" when FindDotnet() is { } dotnet => new ProcessStartInfo(dotnet) { ArgumentList = { "--version" } },
            _ => null
        };
        if (info is null) return null;
        info.WorkingDirectory = workspaceRoot;
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        info.Environment.Clear();
        info.Environment["SystemRoot"] = windowsDirectory;
        info.Environment["WINDIR"] = windowsDirectory;
        info.Environment["PATH"] = systemDirectory;
        info.Environment["COMSPEC"] = Path.Combine(systemDirectory, "cmd.exe");
        info.Environment["PSModulePath"] = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "Modules");
        info.Environment["TEMP"] = Path.GetTempPath();
        info.Environment["TMP"] = Path.GetTempPath();
        return info;
    }

    private static string PowerShellPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string? FindGit()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var candidate in new[] { Path.Combine(programFiles, "Git", "cmd", "git.exe"),
                     Path.Combine(programFiles, "Git", "bin", "git.exe") })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static string? FindDotnet()
    {
        var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var output = new StringBuilder(MaximumOutputCharacters);
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            var remaining = MaximumOutputCharacters - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        return (output.ToString(), truncated);
    }
}
