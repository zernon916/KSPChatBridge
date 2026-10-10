# Build the mod (Release, WITHOUT copying into the live GameData) and zip a CKAN / manual-install package:
#   dist/KSPChatBridge-<version>.zip
#     GameData/KSPChatBridge/Plugins/KSPChatBridge.dll   (the only DLL: KSP loads every *.dll under GameData)
#     GameData/KSPChatBridge/KSPChatBridge.version       (KSP-AVC)
#     GameData/KSPChatBridge/AICS_RPM.cfg, AICS_IVA.cfg, Defaults/charts, personalities.txt, templates, LICENSE, README.md
# PluginData (per-user settings, .env, logs, models) is never packaged; no PDB / build path is embedded in the DLL.
# Writes SHA-256 sums to dist/SHA256SUMS-<version>.txt. Nothing is installed, tagged or uploaded.
param([string]$Version = "", [string]$Suffix = "")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
# Validate every recursive-cleanup target before any deletion.
function Assert-BuildPath([string]$Path) {
    $expected = [IO.Path]::GetFullPath((Join-Path $root 'dist')) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe build path: $resolved" }
    if ((Test-Path -LiteralPath $resolved) -and ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Build directory must not be a junction: $resolved" }
}
if (-not $Version) {
    $v = (Get-Content "$root\KSPChatMod\KSPChatBridge.version" -Raw | ConvertFrom-Json).VERSION
    $Version = "$($v.MAJOR).$($v.MINOR).$($v.PATCH)"
}
Push-Location "$root\KSPChatMod"
try { dotnet build -c Release -nologo -v q -p:SkipInstall=true -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -p:Deterministic=true | Out-Host } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$stage = Join-Path $root "dist\stage"
Assert-BuildPath $stage
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$mod = Join-Path $stage "GameData\KSPChatBridge"
New-Item -ItemType Directory -Force "$mod\Plugins" | Out-Null
Copy-Item "$root\KSPChatMod\bin\Release\KSPChatBridge.dll" "$mod\Plugins\"
Copy-Item "$root\KSPChatMod\AICS_RPM.cfg" $mod
Copy-Item "$root\KSPChatMod\AICS_IVA.cfg" $mod
New-Item -ItemType Directory -Force "$mod\Defaults\charts" | Out-Null; Copy-Item "$root\tools\charts\*.json" "$mod\Defaults\charts\"   # default chart variants (copied into PluginData/charts on first run)
Copy-Item "$root\KSPChatMod\KSPChatBridge.version", "$root\LICENSE", "$root\README.md", "$root\personalities.txt" $mod
New-Item -ItemType Directory -Force "$mod\templates" | Out-Null
Copy-Item "$root\templates\env.example", "$root\playstyle_notes.example.md" "$mod\templates\"
$stray = Get-ChildItem $stage -Recurse -File -Include *.dll, *.pyd | Where-Object { $_.FullName -ne "$mod\Plugins\KSPChatBridge.dll" }
if ($stray) { throw "KSP would try to load these as plugins: $($stray.FullName -join ', ')" }
$relPaths = @(Get-ChildItem $stage -Recurse -File | ForEach-Object {
    $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
})
$gguf = @($relPaths | Where-Object { $_ -match '\.gguf$' })
if ($gguf.Count -gt 0) { throw "Release must not bundle model weights (.gguf): $($gguf -join ', ')" }
$forbidden = @($relPaths | Where-Object {
    $_ -match '\.dll$' -and -not (
        $_ -match '/Plugins/' -and [IO.Path]::GetFileName($_) -ieq 'KSPChatBridge.dll'
    )
})
if ($forbidden.Count -gt 0) { throw "Disallowed DLLs in package: $($forbidden -join ', ')" }
$bundledModels = @($relPaths | Where-Object {
    $n = $_.ToLowerInvariant()
    $n.Contains('/models/') -and ($n.EndsWith('.gguf') -or $n.EndsWith('.bin'))
})
if ($bundledModels.Count -gt 0) { throw "Release must not bundle models under PluginData/models: $($bundledModels -join ', ')" }
if (@(Get-ChildItem $stage -Recurse -File -Include *.exe).Count -gt 0) { throw "Package must not contain an exe" }
$zip = Join-Path $root "dist\KSPChatBridge-$Version$Suffix.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# entries with forward slashes (Windows PowerShell's Compress-Archive writes backslashes, which breaks CKAN / unzip)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $z.Dispose() }
# P5-1.11: detectors always re-run on the finished zip (not just the stage dir); fail and delete the zip on any hit.
$zr = [System.IO.Compression.ZipFile]::OpenRead($zip)
try { $entries = @($zr.Entries | ForEach-Object { $_.FullName }) } finally { $zr.Dispose() }
$badZip = @($entries | Where-Object {
    $n = $_.ToLowerInvariant()
    ($n.EndsWith('.dll') -and $n -ne 'gamedata/kspchatbridge/plugins/kspchatbridge.dll') -or $n.EndsWith('.gguf') -or $n.EndsWith('.pyd') -or
    $n.EndsWith('.so') -or $n.EndsWith('.dylib') -or $n.Contains('/pluginData/models/'.ToLowerInvariant()) -or $n.Contains('/plugindata/native/') -or $n.EndsWith('.env')
})
if ($badZip.Count -gt 0) { Remove-Item $zip -Force; throw "Package detector failed on zip: $($badZip -join ', ')" }
$sums = @("$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())  KSPChatBridge-$Version$Suffix.zip")
Set-Content (Join-Path $root "dist\SHA256SUMS-$Version$Suffix.txt") $sums -Encoding ascii
Remove-Item $stage -Recurse -Force
$sums | Out-Host
"package: $zip"
