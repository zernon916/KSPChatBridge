# Regenerates the Desktop approach chart with the latest terrain_kerbin.json (written by the mod on first KSC/flight load, or Settings > Export terrain for charts).
$root = Split-Path $PSScriptRoot -Parent
$pd = 'C:\Steam\steamapps\common\Kerbal Space Program\GameData\KSPChatBridge\PluginData'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'AICS_approach_map.html'
python (Join-Path $root 'tools\approach_map.py') (Join-Path $pd 'logs') $out (Join-Path $pd 'terrain_kerbin.json')
if (-not (Test-Path (Join-Path $pd 'terrain_kerbin.json'))) { Write-Host 'No terrain_kerbin.json yet: load KSC or a flight on Kerbin once (or AICS > Settings > Export terrain for charts), then run this again.' }
