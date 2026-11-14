param([switch]$ConfigureDeepSeek, [switch]$FromClipboard, [switch]$CalendarTrial,
    [string]$ImportGoogleOAuth, [string]$ImportDriveOAuth, [string]$RestoredState, [switch]$EnableGoogle,
    [string]$PublicOrigin = $env:DETOT_PUBLIC_ORIGIN,
    [ValidateRange(1024,65535)][int]$WebPort = 5183,
    [ValidateRange(1024,65535)][int]$GatewayPort = 18793,
    [string]$BuildDirectory)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$root = $PSScriptRoot
$PublicOrigin = ([string]$PublicOrigin).TrimEnd('/')
$workspaceOrigin = if ($PublicOrigin) { $PublicOrigin } else { "http://localhost:$WebPort" }
$googleConfig = Join-Path $root '.local/google-client.json'
$driveConfig = Join-Path $root '.local/google-drive-client.json'
if ($ImportGoogleOAuth -and $ImportDriveOAuth) { throw 'Import one OAuth client at a time.' }
if ($ImportGoogleOAuth -or $ImportDriveOAuth) {
    $file = if ($ImportDriveOAuth) { $ImportDriveOAuth } else { $ImportGoogleOAuth }
    $configPath = if ($ImportDriveOAuth) { $driveConfig } else { $googleConfig }
    $web = (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).web
    if (!$web.client_id -or !$web.client_secret -or $web.redirect_uris -cnotcontains "$workspaceOrigin/api/drive/callback") {
        throw "Import a Google OAuth Web client JSON with the exact callback: $workspaceOrigin/api/drive/callback"
    }
    if ($ImportGoogleOAuth -and $web.redirect_uris -cnotcontains "$workspaceOrigin/api/google/callback") {
        throw "Import a Google OAuth Web client JSON with the exact callback: $workspaceOrigin/api/google/callback"
    }
    $googleSecret = ConvertTo-SecureString -String $web.client_secret -AsPlainText -Force
    try {
        New-Item -ItemType Directory -Path (Split-Path -Parent $configPath) -Force | Out-Null
        @{ clientId = $web.client_id; protectedSecret = ($googleSecret | ConvertFrom-SecureString) } |
            ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
        Write-Host 'Google OAuth configuration saved.'
    } finally {
        $googleSecret.Dispose()
    }
    return
}
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
$gateway = Join-Path $root 'tests/runtime-candidate/node_modules/openclaw/openclaw.mjs'
if (!(Test-Path -LiteralPath $gateway -PathType Leaf)) {
    throw 'Missing locked OpenClaw Gateway. From detot-lab, run npm.cmd --prefix tests/runtime-candidate ci.'
}
if (!(Test-Path -LiteralPath $node -PathType Leaf)) { $node = (Get-Command node.exe -ErrorAction Stop).Source }
if (!(Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$isolatedPorts = $WebPort -ne 5183 -or $GatewayPort -ne 18793
if ($WebPort -eq $GatewayPort) { throw 'Backend and Gateway ports must differ.' }
$runtimeDir = Join-Path $root ".local/runtime-$WebPort-$GatewayPort"
$buildDir = if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    Join-Path $runtimeDir 'build'
} elseif ([IO.Path]::IsPathRooted($BuildDirectory)) {
    [IO.Path]::GetFullPath($BuildDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $root $BuildDirectory))
}
if ($BuildDirectory) {
    $localRoot = [IO.Path]::GetFullPath((Join-Path $root '.local')) + [IO.Path]::DirectorySeparatorChar
    if (!$buildDir.StartsWith($localRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'BuildDirectory must be inside the lab .local directory.'
    }
}
$runtimeManifest = Join-Path $runtimeDir 'processes.json'
$app = Join-Path $buildDir 'Detot.Lab.dll'
foreach ($path in $node, $dotnet, $gateway) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing lab dependency: $path" }
}

if (Test-Path -LiteralPath $runtimeManifest -PathType Leaf) {
    $tracked = Get-Content -LiteralPath $runtimeManifest -Raw | ConvertFrom-Json
    foreach ($entry in @($tracked.processes)) {
        try {
            $process = Get-Process -Id ([int]$entry.id) -ErrorAction Stop
            $sameProcess = $process.ProcessName -eq $entry.name -and
                [Math]::Abs(($process.StartTime.ToUniversalTime().Ticks - [long]$entry.startTicks)) -lt [TimeSpan]::TicksPerSecond
            if ($sameProcess) {
                $process.Kill($true)
                $process.WaitForExit(10000) | Out-Null
            }
        } catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            # The tracked process has already exited.
        }
    }
    Remove-Item -LiteralPath $runtimeManifest -Force
}

foreach ($port in $GatewayPort, $WebPort) {
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

& $node (Join-Path $root 'tests/runtime-candidate/apply-retention-fix.mjs') $gateway
if ($LASTEXITCODE -ne 0) { throw 'Candidate retention patch failed.' }
& npm.cmd --prefix (Join-Path $root 'frontend') run build
if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed. Run npm ci in detot-lab/frontend and retry.' }
& $dotnet build (Join-Path $root 'Detot.Lab.csproj') -o $buildDir
if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }

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
$state = if ($RestoredState) { (Resolve-Path -LiteralPath $RestoredState).Path } elseif ($isolatedPorts) {
    Join-Path $root ".local/state-$WebPort-$GatewayPort"
} else { Join-Path $root '.state' }
$authority = Join-Path $root '.local/copy-authority'
if ($RestoredState -and !(Test-Path -LiteralPath $authority -PathType Container)) {
    throw 'Restore requires the existing external revocation authority.'
}
if (!$RestoredState) { New-Item -ItemType Directory -Path $authority -Force | Out-Null }
if ($RestoredState) {
    & $node (Join-Path $root 'tests/runtime-candidate/check-restored-state.mjs') $state $authority
    if ($LASTEXITCODE -ne 0) { throw 'Restore rejected before starting Gateway or backend.' }
}
New-Item -ItemType Directory -Path $state -Force | Out-Null
$configPath = Join-Path $root 'gateway.lab.json'
if ($isolatedPorts) {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $config.gateway.port = $GatewayPort
    $configPath = Join-Path $root ".local/gateway-$WebPort-$GatewayPort.json"
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 30))
}

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
    $allowed = @('SystemRoot','WINDIR','COMSPEC','PATH','PATHEXT','TEMP','TMP','USERPROFILE','LOCALAPPDATA','APPDATA','PROGRAMDATA','PROGRAMFILES','PROGRAMFILES(X86)','PROCESSOR_ARCHITECTURE','NUMBER_OF_PROCESSORS','USERNAME','USERDOMAIN','HOMEDRIVE','HOMEPATH','XDG_CACHE_HOME')
    foreach ($name in @($info.EnvironmentVariables.Keys)) {
        if ($name -notin $allowed) { $info.EnvironmentVariables.Remove($name) }
    }
    foreach ($entry in $Variables.GetEnumerator()) { $info.EnvironmentVariables[$entry.Key] = $entry.Value }
    [Diagnostics.Process]::Start($info)
}

function Save-LabProcesses {
    $children = @($appProcess, $gatewayProcess) | Where-Object { $_ }
    $records = foreach ($child in $children) {
        [pscustomobject]@{ id = $child.Id; name = $child.ProcessName; startTicks = $child.StartTime.ToUniversalTime().Ticks }
    }
    New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
    [IO.File]::WriteAllText($runtimeManifest, (@{ processes = @($records) } | ConvertTo-Json -Depth 4))
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
        OPENCLAW_CONFIG_PATH = $configPath
        XDG_CACHE_HOME = Join-Path $root '.local/openclaw-cache'
        OPENCLAW_SKIP_CHANNELS = '1'
        OPENCLAW_DISABLE_BONJOUR = '1'
        DETOT_COPY_AUTHORITY_DIR = $authority
        DETOT_RESTORE_AUTHORITY = if ($RestoredState) { $authority } else { '' }
    }
    Save-LabProcesses
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
                $response = $probe.GetAsync("http://127.0.0.1:$GatewayPort/readyz").GetAwaiter().GetResult()
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
        DETOT_LAB_NODE = $node
        DETOT_LAB_RUNTIME = $gateway
        OPENCLAW_STATE_DIR = $state
        OPENCLAW_CONFIG_PATH = $configPath
        DETOT_WEB_PORT = [string]$WebPort
        DETOT_GATEWAY_PORT = [string]$GatewayPort
        DETOT_COPY_AUTHORITY_DIR = $authority
        DETOT_ENABLE_CALENDAR_TRIAL = if ($CalendarTrial) { 'true' } else { 'false' }
    }
    foreach ($name in @('DETOT_GOOGLE_CLIENT_ID','DETOT_GOOGLE_CLIENT_SECRET','DETOT_GOOGLE_DRIVE_LOGIN_HINT',
        'DETOT_GOOGLE_DRIVE_CLIENT_ID','DETOT_GOOGLE_DRIVE_CLIENT_SECRET',
        'DETOT_MEMORY_CORE_PATH','DETOT_DREAMING_PATH','DETOT_MEMORY_RELATED_PATH',
        'DETOT_SEARCH_PROVIDER','DETOT_SEARXNG_BASE_URL','DETOT_SEARCH_API_KEY')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value -and !$RestoredState) { $backendVariables[$name] = $value }
    }
    if ($PublicOrigin) {
        $backendVariables['DETOT_PUBLIC_ORIGIN'] = $PublicOrigin
        $backendVariables['DETOT_REMOTE_PASSWORD'] = $env:DETOT_REMOTE_PASSWORD
    }
    foreach ($client in @(
        @{Path = $googleConfig; Prefix = 'DETOT_GOOGLE'},
        @{Path = $driveConfig; Prefix = 'DETOT_GOOGLE_DRIVE'}
    )) {
        $idName = $client.Prefix + '_CLIENT_ID'
        $secretName = $client.Prefix + '_CLIENT_SECRET'
        if ($RestoredState -or $backendVariables[$idName] -or $backendVariables[$secretName] -or
            !(Test-Path -LiteralPath $client.Path -PathType Leaf)) { continue }
        $saved = Get-Content -LiteralPath $client.Path -Raw | ConvertFrom-Json
        $googleSecret = ConvertTo-SecureString -String $saved.protectedSecret
        try {
            $backendVariables[$idName] = $saved.clientId
            $backendVariables[$secretName] = [Net.NetworkCredential]::new('', $googleSecret).Password
        } finally {
            $googleSecret.Dispose()
        }
    }
    $appProcess = Start-LabChild $dotnet @($app) $backendVariables
    Save-LabProcesses
    $backendVariables.Clear()
    $token = $null
    Write-Host "Open $workspaceOrigin/workspace. Press Ctrl+C here to stop."
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
    if (Test-Path -LiteralPath $runtimeManifest -PathType Leaf) { Remove-Item -LiteralPath $runtimeManifest -Force }
}
