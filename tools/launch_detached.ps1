# Start a program fully detached from the calling shell (WMI Win32_Process.Create) and with NO window: WMI starts
# pythonw.exe tools\spawn_hidden.py, which spawns the target with CREATE_NO_WINDOW and redirected output.
# (An interrupted shell killed the plane controller mid-flight on 2026-10-08; visible cmd/python consoles popped up
# on Luke's desktop and stole focus.)  Prints the target's pid.
param([Parameter(Mandatory)][string]$Out, [Parameter(Mandatory)][string]$Err, [Parameter(Mandatory)][string[]]$Prog,
      [string]$Dir = (Split-Path $PSScriptRoot -Parent))
$pyw = (Get-Command pythonw.exe).Source
$q = ($Prog | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine = "`"$pyw`" tools\spawn_hidden.py `"$Out`" `"$Err`" $q"; CurrentDirectory = $Dir }
for ($i = 0; $i -lt 50 -and -not (Test-Path "$Out.pid"); $i++) { Start-Sleep -Milliseconds 100 }
if (Test-Path "$Out.pid") { "pid " + (Get-Content "$Out.pid") } else { "spawn failed rc $($r.ReturnValue)" }
