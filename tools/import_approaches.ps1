# Copies the newest per-end chart downloads (KSC_09.json, Island_27_tight.json, ...) from Downloads into PluginData\charts (hot-swapped live by the mod).
$dst = 'C:\Steam\steamapps\common\Kerbal Space Program\GameData\KSPChatBridge\PluginData\charts'
New-Item -ItemType Directory -Force $dst | Out-Null
$files = Get-ChildItem "$env:USERPROFILE\Downloads" -Filter '*.json' | Where-Object { $_.BaseName -match '^(KSC|Island)_\d\d(_[A-Za-z0-9]+)?( \(\d+\))?$' } | Sort-Object LastWriteTime -Descending
if (-not $files) { Write-Host 'No chart files (KSC_09.json, ...) in Downloads.'; exit 1 }
$done = @{}
foreach ($f in $files) { $name = ($f.BaseName -replace ' \(\d+\)$','') + '.json'; if ($done[$name]) { continue }; $done[$name] = 1; Copy-Item $f.FullName (Join-Path $dst $name) -Force; Write-Host "Copied $($f.Name) -> $dst\$name" }
