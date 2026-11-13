$ErrorActionPreference = 'Stop'
$lab = Split-Path -Parent $PSScriptRoot
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$node = Join-Path $lab ".local/node-v24.22.3-win-$arch/node.exe"
$runner = Join-Path $PSScriptRoot 'milestone1.mjs'
if (!(Test-Path -LiteralPath $node -PathType Leaf)) { throw 'Missing lab Node.js.' }

$key = Read-Host 'DeepSeek API key for synthetic Milestone 1 test (hidden)' -AsSecureString
try {
    if (!$key.Length) { throw 'A DeepSeek key is required.' }
    $info = [Diagnostics.ProcessStartInfo]::new($node)
    $info.Arguments = '"' + $runner + '" --live'
    $info.WorkingDirectory = $lab
    $info.UseShellExecute = $false
    $info.EnvironmentVariables['DEEPSEEK_API_KEY'] = [Net.NetworkCredential]::new('', $key).Password
    $child = [Diagnostics.Process]::Start($info)
    $info.EnvironmentVariables.Remove('DEEPSEEK_API_KEY')
    try {
        $child.WaitForExit()
        exit $child.ExitCode
    } finally {
        $child.Dispose()
    }
} finally {
    $key.Dispose()
}
