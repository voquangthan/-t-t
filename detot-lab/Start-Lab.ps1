param([switch]$ConfigureDeepSeek, [switch]$FromClipboard, [switch]$CalendarTrial)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$root = $PSScriptRoot
$credentialPath = Join-Path $root '.local/deepseek-key.dpapi'
if ($FromClipboard -and !$ConfigureDeepSeek) { throw "Use -FromClipboard with -ConfigureDeepSeek." }
if ($ConfigureDeepSeek) {
    $secret = if (!$FromClipboard) { Read-Host 'Nhập DeepSeek API key (ẩn)' -AsSecureString }
    try {
        if ($FromClipboard) {
            [void](Read-Host 'Copy the API key NOW, return to this window, and press Enter without pasting')
            $plain = ([string](Get-Clipboard -Raw)).Trim()
            if (!$plain) { throw 'Clipboard has no text. Copy the API key after the prompt appears.' }
            if ($plain -match 'Start-Lab\.ps1|-ConfigureDeepSeek') {
                throw 'Clipboard contains the startup command. Copy the API key after the prompt appears.'
            }
        } else {
            $plain = [Net.NetworkCredential]::new('', $secret).Password.Trim()
        }
        if ($plain -cnotmatch '^[!-~]{8,512}$') {
            throw 'Enter the complete API key as a single value without spaces.'
        }
        $cleanSecret = ConvertTo-SecureString -String $plain -AsPlainText -Force
        New-Item -ItemType Directory -Path (Split-Path -Parent $credentialPath) -Force | Out-Null
        $cleanSecret | ConvertFrom-SecureString | Set-Content -LiteralPath $credentialPath -Encoding ASCII
        Write-Host 'Đã lưu khóa DeepSeek cho tài khoản Windows hiện tại.'
    } finally {
        if ($cleanSecret) { $cleanSecret.Dispose() }
        $plain = $null
        if ($secret) { $secret.Dispose() }
    }
    return
}
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$node = Join-Path $root ".local/node-v24.22.3-win-$arch/node.exe"
$dotnet = Join-Path $root '.local/dotnet/dotnet.exe'
$gateway = Join-Path $root '.local/openclaw-registry/node_modules/openclaw/openclaw.mjs'
$candidate = Join-Path $root 'tests/runtime-candidate/node_modules/openclaw/openclaw.mjs'
if (Test-Path -LiteralPath $candidate -PathType Leaf) { $gateway = $candidate }
if (!(Test-Path -LiteralPath $node -PathType Leaf)) { $node = (Get-Command node.exe -ErrorAction Stop).Source }
if (!(Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$app = Join-Path $root 'bin/Debug/net10.0/Detot.Lab.dll'
foreach ($path in $node, $dotnet, $gateway) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing lab dependency: $path" }
}

foreach ($port in 18793, 5183) {
    $socket = [Net.Sockets.TcpClient]::new()
    $connected = $false
    try {
        $attempt = $socket.ConnectAsync([Net.IPAddress]::Loopback, $port)
        $connected = $attempt.Wait(500) -and $socket.Connected
    } catch [AggregateException] {
        # A refused connection is expected when the lab is stopped.
    } finally {
        $socket.Dispose()
    }
    if ($connected) {
        $listener = Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        $owners = ($listener.OwningProcess | Sort-Object -Unique) -join ', '
        throw "Lab port $port is already in use (PID: $owners). Stop the old lab with Ctrl+C before restarting."
    }
}

if ($gateway -eq $candidate) {
    & $node (Join-Path $root 'tests/runtime-candidate/apply-retention-fix.mjs') $gateway
    if ($LASTEXITCODE -ne 0) { throw 'Candidate retention patch failed.' }
}
& $dotnet build (Join-Path $root 'Detot.Lab.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }
& npm.cmd --prefix (Join-Path $root 'frontend') run build
if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed. Run npm ci in detot-lab/frontend and retry.' }

# The process-local credentials are only passed to the children that need them.
$key = if (Test-Path -LiteralPath $credentialPath -PathType Leaf) {
    try {
        Get-Content -LiteralPath $credentialPath -Raw | ForEach-Object { ConvertTo-SecureString -String $_.Trim() }
    } catch {
        throw 'Không đọc được khóa đã lưu. Chạy Start-Lab.ps1 -ConfigureDeepSeek để lưu lại khóa.'
    }
} elseif (![string]::IsNullOrWhiteSpace($env:DEEPSEEK_API_KEY)) {
    ConvertTo-SecureString -String $env:DEEPSEEK_API_KEY -AsPlainText -Force
} else {
    Read-Host 'DeepSeek API key (hidden, this session only)' -AsSecureString
}
if (!$key.Length) { throw 'A DeepSeek key is required.' }
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $rng.GetBytes($bytes)
}
finally {
    $rng.Dispose()
}
$token = [BitConverter]::ToString($bytes).Replace("-", "")
$state = Join-Path $root '.state'
New-Item -ItemType Directory -Path $state -Force | Out-Null

function ConvertTo-LabProcessArgument([string]$Argument) {
    # Windows argv parsing doubles backslashes before a quote or the closing delimiter.
    $escaped = $Argument -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1'
    '"' + $escaped + '"'
}

function Start-LabChild($Executable, $Arguments, $Variables) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Arguments = ($Arguments | ForEach-Object { ConvertTo-LabProcessArgument $_ }) -join ' '
    $allowed = @('SystemRoot','WINDIR','COMSPEC','PATH','PATHEXT','TEMP','TMP','USERPROFILE','LOCALAPPDATA','APPDATA','PROGRAMDATA','PROGRAMFILES','PROGRAMFILES(X86)','PROCESSOR_ARCHITECTURE','NUMBER_OF_PROCESSORS','USERNAME','USERDOMAIN','HOMEDRIVE','HOMEPATH')
    foreach ($name in @($info.EnvironmentVariables.Keys)) {
        if ($name -notin $allowed) { $info.EnvironmentVariables.Remove($name) }
    }
    foreach ($entry in $Variables.GetEnumerator()) { $info.EnvironmentVariables[$entry.Key] = $entry.Value }
    [Diagnostics.Process]::Start($info)
}

function Stop-LabChild($Child) {
    try {
        if (!$Child.HasExited) {
            # taskkill /T preserves tree cleanup on .NET Framework.
            $taskkill = Join-Path ([Environment]::SystemDirectory) 'taskkill.exe'
            try { & $taskkill /PID $Child.Id /T /F 2>$null | Out-Null } catch { }
            if (!$Child.HasExited) { $Child.Kill() }
        }
    } finally {
        $Child.Dispose()
    }
}

$gatewayProcess = $null
$appProcess = $null
try {
    $provider = [Net.NetworkCredential]::new('', $key)
    if ($provider.Password.Trim() -cnotmatch '^[!-~]{8,512}$') {
        throw 'Invalid DeepSeek key format. Run Start-Lab.ps1 -ConfigureDeepSeek and enter the complete API key without spaces.'
    }
    $gatewayProcess = Start-LabChild $node @($gateway, 'gateway', 'run') @{
        DEEPSEEK_API_KEY = $provider.Password.Trim()
        OPENCLAW_GATEWAY_TOKEN = $token
        OPENCLAW_STATE_DIR = $state
        OPENCLAW_CONFIG_PATH = (Join-Path $root 'gateway.lab.json')
        OPENCLAW_SKIP_CHANNELS = '1'
        OPENCLAW_DISABLE_BONJOUR = '1'
    }
    $provider = $null
    $key.Dispose()
    $key = $null
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $probe = [Net.Http.HttpClient]::new($handler)
    $probe.Timeout = [TimeSpan]::FromSeconds(2)
    try {
        $deadline = [DateTime]::UtcNow.AddMinutes(5)
        do {
            if ($gatewayProcess.HasExited) { throw "Gateway exited ($($gatewayProcess.ExitCode))." }
            try {
                $response = $probe.GetAsync('http://127.0.0.1:18793/readyz').GetAwaiter().GetResult()
                $ready = $response.IsSuccessStatusCode
                $response.Dispose()
            } catch {
                $ready = $false
            }
            if (!$ready) { Start-Sleep -Seconds 1 }
        } until ($ready -or [DateTime]::UtcNow -ge $deadline)
        if (!$ready) { throw 'Gateway readiness timed out.' }
    } finally {
        $probe.Dispose()
    }
    $backendVariables = @{
        DETOT_GATEWAY_TOKEN = $token
        DETOT_ENABLE_CALENDAR_TRIAL = if ($CalendarTrial) { 'true' } else { 'false' }
    }
    foreach ($name in @('DETOT_GOOGLE_CLIENT_ID','DETOT_GOOGLE_CLIENT_SECRET','DETOT_GOOGLE_RELEASE_ENABLED')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $backendVariables[$name] = $value }
    }
    $appProcess = Start-LabChild $dotnet @($app) $backendVariables
    $backendVariables.Clear()
    $token = $null
    Write-Host 'Open http://127.0.0.1:5183/workspace and send a synthetic prompt. Press Ctrl+C here to stop.'
    while (!$appProcess.HasExited -and !$gatewayProcess.HasExited) {
        Start-Sleep -Seconds 1
    }
    if ($gatewayProcess.HasExited) { throw "Gateway exited ($($gatewayProcess.ExitCode))." }
    throw "Backend exited ($($appProcess.ExitCode))."
} finally {
    if ($key) { $key.Dispose() }
    foreach ($child in $appProcess, $gatewayProcess) {
        if ($child) { Stop-LabChild $child }
    }
}
