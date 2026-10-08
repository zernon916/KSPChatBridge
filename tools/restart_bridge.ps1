# Gracefully restart the KSPChatBridge HTTP server: waits (up to 10 min) until no chat is in progress.
# Starts it windowless and detached (tools\launch_detached.ps1): no console pops up / steals focus, and an
# interrupted shell can't take it down.
$ErrorActionPreference = "SilentlyContinue"
$root = Split-Path $PSScriptRoot -Parent
$end = (Get-Date).AddMinutes(10)
while ((Get-Date) -lt $end) {
    $h = Invoke-RestMethod http://127.0.0.1:8765/health -TimeoutSec 5
    if (-not $h -or -not $h.busy) { break }
    Start-Sleep 2
}
Get-CimInstance Win32_Process -Filter "Name='python.exe' OR Name='pythonw.exe'" |
    Where-Object { $_.CommandLine -match 'run_bridge.py"? serve' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep 1
New-Item -ItemType Directory -Force "$root\logs" | Out-Null
& "$PSScriptRoot\launch_detached.ps1" -Out "$root\logs\bridge_stdout.txt" -Err "$root\logs\bridge_stderr.txt" `
    -Prog @("python", "run_bridge.py", "serve") -Dir $root | Out-Null
"bridge restarted"
