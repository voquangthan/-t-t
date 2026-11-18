param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
$labRoot = $PSScriptRoot
$workspaceRoot = Split-Path -Parent $labRoot
$dotnet = $null
$search = $labRoot
while ($search -and !$dotnet) {
    $candidate = Join-Path $search 'detot-lab/.local/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $dotnet = $candidate; break }
    $search = Split-Path -Parent $search
}
if (!$dotnet -or !(Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (!$command) { throw 'Install/use the configured .NET SDK, then run this launcher again.' }
    $dotnet = $command.Source
}
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is not available in this Windows session.' }
$profileRoot = Join-Path $env:LOCALAPPDATA 'DetotLab/ComputerUse/EdgeProfile'
New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
$env:DETOT_DESKTOP_WORKSPACE_ROOT = [IO.Path]::GetFullPath($workspaceRoot)
$env:DETOT_DESKTOP_EDGE_PROFILE = [IO.Path]::GetFullPath($profileRoot)
Write-Host 'Starting the allowlisted Desktop Companion in this interactive user session.'
Write-Host 'It accepts fixed local actions only. Press Ctrl+C to stop; it does not run as administrator or auto-start.'
if ($SmokeTest) {
    Write-Host 'Smoke test will open the dedicated Edge GitHub page and VS Code workspace, then report observed state.'
    & $dotnet run --project (Join-Path $labRoot 'desktop-companion/DesktopCompanion.csproj') --configuration Release -- --smoke-test
} else {
    & $dotnet run --project (Join-Path $labRoot 'desktop-companion/DesktopCompanion.csproj') --configuration Release
}
if ($LASTEXITCODE -ne 0) { throw "Desktop Companion exited with code $LASTEXITCODE." }
