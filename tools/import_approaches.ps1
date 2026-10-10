# Copies the newest approaches*.json from Downloads into the KSP PluginData folder (the mod reads it on the next landing).
$src = Get-ChildItem "$env:USERPROFILE\Downloads" -Filter 'approaches*.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $src) { Write-Host 'No approaches*.json in Downloads.'; exit 1 }
$dst = 'C:\Steam\steamapps\common\Kerbal Space Program\GameData\KSPChatBridge\PluginData\approaches.json'
Copy-Item $src.FullName $dst -Force; Write-Host "Copied $($src.Name) -> $dst"
