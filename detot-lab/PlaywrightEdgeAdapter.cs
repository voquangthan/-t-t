using System.Diagnostics;
using System.Text.Json;

public sealed class PlaywrightEdgeAdapter : IAsyncDisposable
{
    private readonly string? nodeExecutable;
    private readonly string scriptPath;
    private readonly string profileDirectory;
    private readonly string? playwrightCoreDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? worker;
    private int nextId;

    public PlaywrightEdgeAdapter(string? nodeExecutable, string scriptPath, string profileDirectory,
        string? playwrightCoreDirectory)
    {
        this.nodeExecutable = nodeExecutable;
        this.scriptPath = Path.GetFullPath(scriptPath);
        this.profileDirectory = Path.GetFullPath(profileDirectory);
        this.playwrightCoreDirectory = playwrightCoreDirectory is null ? null : Path.GetFullPath(playwrightCoreDirectory);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(nodeExecutable) && File.Exists(nodeExecutable) &&
        File.Exists(scriptPath) && playwrightCoreDirectory is not null && Directory.Exists(playwrightCoreDirectory);

    public async Task<JsonElement> ExecuteAsync(string action, string? repository,
        CancellationToken cancellationToken = default)
    {
        if (action is not ("open-edge" or "open-github" or "read-github" or "read-account"))
            return JsonSerializer.SerializeToElement(new { error = "action_not_allowed" });
        var repositoryPath = DesktopOperator.SafeGitHubRepository(repository);
        if (action is "open-github" or "read-github" && repositoryPath is null)
            return JsonSerializer.SerializeToElement(new { error = "repository_not_allowed" });
        if (!IsConfigured) return JsonSerializer.SerializeToElement(new { error = "playwright_unavailable" });

        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureWorkerAsync(cancellationToken);
            var id = Interlocked.Increment(ref nextId);
            var payload = JsonSerializer.Serialize(new { id, action, repository = repositoryPath });
            await worker!.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await worker.StandardInput.FlushAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(40));
            var line = await worker.StandardOutput.ReadLineAsync(deadline.Token);
            if (line is null) throw new InvalidOperationException("browser_worker_exited");
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id ||
                !document.RootElement.TryGetProperty("result", out var result))
                throw new InvalidOperationException("browser_worker_protocol_error");
            return result.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopWorkerAsync();
            return JsonSerializer.SerializeToElement(new { error = "browser_timeout" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopWorkerAsync();
            throw;
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException or IOException or
                                      System.ComponentModel.Win32Exception)
        {
            await StopWorkerAsync();
            return JsonSerializer.SerializeToElement(new { error = "browser_operation_failed" });
        }
        finally { gate.Release(); }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (worker is { HasExited: false }) return;
        worker?.Dispose();
        Directory.CreateDirectory(profileDirectory);
        var start = new ProcessStartInfo(nodeExecutable!)
        {
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(scriptPath);
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "LOCALAPPDATA", "USERPROFILE", "ProgramFiles", "ProgramFiles(x86)" })
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) start.Environment[key] = value;
        start.Environment["DETOT_PLAYWRIGHT_CORE"] = playwrightCoreDirectory!;
        start.Environment["DETOT_DESKTOP_EDGE_PROFILE"] = profileDirectory;
        worker = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!worker.Start()) throw new InvalidOperationException("browser_worker_start_failed");
        _ = worker.StandardError.ReadToEndAsync();
        await Task.Delay(150, cancellationToken);
        if (worker.HasExited) throw new InvalidOperationException("browser_worker_start_failed");
    }

    private async Task StopWorkerAsync()
    {
        if (worker is null) return;
        try
        {
            if (!worker.HasExited)
            {
                await worker.StandardInput.WriteLineAsync("{\"action\":\"shutdown\"}");
                await worker.StandardInput.FlushAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await worker.WaitForExitAsync(deadline.Token);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or OperationCanceledException)
        {
            try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        finally
        {
            worker.Dispose();
            worker = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { await StopWorkerAsync(); }
        finally { gate.Release(); gate.Dispose(); }
    }
}
