param([switch]$ConfigureDeepSeek, [switch]$FromClipboard, [switch]$CalendarTrial,
    [string]$ImportGoogleOAuth, [string]$RestoredState, [switch]$EnableGoogle,
    [string]$PublicOrigin = $env:DETOT_PUBLIC_ORIGIN,
    [ValidateRange(1024,65535)][int]$WebPort = 5183,
    [ValidateRange(1024,65535)][int]$GatewayPort = 18793)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$root = $PSScriptRoot
$PublicOrigin = $PublicOrigin.TrimEnd('/')
$workspaceOrigin = if ($PublicOrigin) { $PublicOrigin } else { "http://127.0.0.1:$WebPort" }
$googleConfig = Join-Path $root '.local/google-client.json'
if ($ImportGoogleOAuth) {
    $web = (Get-Content -LiteralPath $ImportGoogleOAuth -Raw | ConvertFrom-Json).web
    if (!$web.client_id -or !$web.client_secret -or
        $web.redirect_uris -cnotcontains "$workspaceOrigin/api/google/callback") {
        throw 'Import a Google OAuth Web client JSON with the exact Detot callback URI.'
    }
    $googleSecret = ConvertTo-SecureString -String $web.client_secret -AsPlainText -Force
    try {
        New-Item -ItemType Directory -Path (Split-Path -Parent $googleConfig) -Force | Out-Null
        @{ clientId = $web.client_id; protectedSecret = ($googleSecret | ConvertFrom-SecureString) } |
            ConvertTo-Json | Set-Content -LiteralPath $googleConfig -Encoding UTF8
        Write-Host 'Google OAuth configuration saved. Gmail release setting is unchanged.'
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
$gateway = Join-Path $root '.local/openclaw-registry/node_modules/openclaw/openclaw.mjs'
$candidate = Join-Path $root 'tests/runtime-candidate/node_modules/openclaw/openclaw.mjs'
if (Test-Path -LiteralPath $candidate -PathType Leaf) { $gateway = $candidate }
if (!(Test-Path -LiteralPath $node -PathType Leaf)) { $node = (Get-Command node.exe -ErrorAction Stop).Source }
if (!(Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$isolatedPorts = $WebPort -ne 5183 -or $GatewayPort -ne 18793
if ($WebPort -eq $GatewayPort) { throw 'Backend and Gateway ports must differ.' }
$buildDir = if ($isolatedPorts) { Join-Path $root ".local/build-$WebPort-$GatewayPort" } else { Join-Path $root 'bin/Debug/net10.0' }
$app = Join-Path $buildDir 'Detot.Lab.dll'
foreach ($path in $node, $dotnet, $gateway) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing lab dependency: $path" }
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

if ($gateway -eq $candidate) {
    & $node (Join-Path $root 'tests/runtime-candidate/apply-retention-fix.mjs') $gateway
    if ($LASTEXITCODE -ne 0) { throw 'Candidate retention patch failed.' }
}
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
if ($gateway -ne $candidate) { throw 'Install the locked Gateway candidate before launching this lab.' }
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
        OPENCLAW_CONFIG_PATH = $configPath
        OPENCLAW_SKIP_CHANNELS = '1'
        OPENCLAW_DISABLE_BONJOUR = '1'
        DETOT_COPY_AUTHORITY_DIR = $authority
        DETOT_RESTORE_AUTHORITY = if ($RestoredState) { $authority } else { '' }
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
        DETOT_ENABLE_CALENDAR_TRIAL = if ($CalendarTrial) { 'true' } else { 'false' }
    }
    foreach ($name in @('DETOT_GOOGLE_CLIENT_ID','DETOT_GOOGLE_CLIENT_SECRET')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value -and !$RestoredState) { $backendVariables[$name] = $value }
    }
    if ($PublicOrigin) {
        $backendVariables['DETOT_PUBLIC_ORIGIN'] = $PublicOrigin
        $backendVariables['DETOT_REMOTE_PASSWORD'] = $env:DETOT_REMOTE_PASSWORD
    }
    if (!$RestoredState -and
        !$backendVariables['DETOT_GOOGLE_CLIENT_ID'] -and !$backendVariables['DETOT_GOOGLE_CLIENT_SECRET'] -and
        (Test-Path -LiteralPath $googleConfig -PathType Leaf)) {
        $saved = Get-Content -LiteralPath $googleConfig -Raw | ConvertFrom-Json
        $googleSecret = ConvertTo-SecureString -String $saved.protectedSecret
        try {
            $backendVariables['DETOT_GOOGLE_CLIENT_ID'] = $saved.clientId
            $backendVariables['DETOT_GOOGLE_CLIENT_SECRET'] = [Net.NetworkCredential]::new('', $googleSecret).Password
        } finally {
            $googleSecret.Dispose()
        }
    }
    $appProcess = Start-LabChild $dotnet @($app) $backendVariables
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
}
