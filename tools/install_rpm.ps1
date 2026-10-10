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
# JSI Advanced Transparent Pods (JPLRepo, official GitHub release) - see-through IVA windows
$atp = "$ksp\KSPChatBridge_staged\JSIAdvTransparentPods_V0.1.24.0.zip"
if (-not (Test-Path $atp)) { Invoke-WebRequest "https://github.com/JPLRepo/JSIAdvTransparentPods/releases/download/V0.1.24.0/JSIAdvTransparentPods_V0.1.24.0.zip" -OutFile $atp }
$t2 = Join-Path $env:TEMP "jsiatp"; if (Test-Path $t2) { Remove-Item $t2 -Recurse -Force }
Expand-Archive $atp $t2; Copy-Item "$t2\GameData\*" "$ksp\GameData\" -Recurse -Force
"JSI Advanced Transparent Pods installed: " + (Get-Item "$ksp\GameData\JSI\JSIAdvTransparentPods\Plugins\JSIAdvTransparentPods.dll").LastWriteTime
