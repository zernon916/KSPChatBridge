# Install RasterPropMonitor (official FirstPersonKSP GitHub release) into GameData. KSP must be closed.
param([string]$Version = "1.1.0.1")
$ErrorActionPreference = "Stop"
$ksp = if ($env:KSPDir) { $env:KSPDir } else { "C:\Steam\steamapps\common\Kerbal Space Program" }
if (Get-Process KSP_x64, KSP -ErrorAction SilentlyContinue) { "KSP is running - quit it first."; exit 1 }
$zip = "$ksp\KSPChatBridge_staged\RasterPropMonitor-$Version.zip"
if (-not (Test-Path $zip)) { Invoke-WebRequest "https://github.com/FirstPersonKSP/RasterPropMonitor/releases/download/$Version/RasterPropMonitor-$Version.zip" -OutFile $zip }
$tmp = Join-Path $env:TEMP "rpm_$Version"; if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Expand-Archive $zip $tmp
Copy-Item "$tmp\RasterPropMonitor-$Version\GameData\*" "$ksp\GameData\" -Recurse -Force
"RasterPropMonitor $Version installed: " + (Get-Item "$ksp\GameData\JSI\RasterPropMonitor\Plugins\RasterPropMonitor.dll").LastWriteTime
