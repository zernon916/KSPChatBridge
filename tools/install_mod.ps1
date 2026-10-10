# Build the in-game mod and install it into GameData. KSP must be closed (the DLL is locked while it runs).
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
function Find-KSP {
    foreach ($d in @($env:KSPDir, "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program",
                     "C:\Steam\steamapps\common\Kerbal Space Program")) { if ($d -and (Test-Path "$d\KSP_x64.exe")) { return $d } }
    throw "KSP folder not found - set the KSPDir environment variable"
}
$ksp = Find-KSP
if (Get-Process KSP_x64 -ErrorAction SilentlyContinue) { "KSP is running - quit it first."; exit 1 }
Push-Location "$root\KSPChatMod"
dotnet build -c Release -nologo -v q "-p:KSPDir=$ksp"
Pop-Location
Copy-Item "$root\KSPChatMod\AICS_RPM.cfg" "$ksp\GameData\KSPChatBridge\" -Force   # optional RasterPropMonitor pages (inert without RPM)
New-Item -ItemType Directory -Force "$ksp\GameData\KSPChatBridge\Defaults\charts" | Out-Null
Copy-Item "$root\tools\charts\*.json" "$ksp\GameData\KSPChatBridge\Defaults\charts\" -Force   # shipped chart variants (copied once into PluginData/charts)
foreach ($old in "Bridge", "BRIDGE_FREE.md") { $p = "$ksp\GameData\KSPChatBridge\$old"; if (Test-Path $p) { Remove-Item $p -Recurse -Force } }   # bridge leftovers
"installed: " + (Get-Item "$ksp\GameData\KSPChatBridge\Plugins\KSPChatBridge.dll").LastWriteTime
