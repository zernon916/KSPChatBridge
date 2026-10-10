# Build the mod (Release, WITHOUT copying into the live GameData) plus the standalone bridge exe, and zip a CKAN /
# manual-install package:
#   dist/KSPChatBridge-<version>.zip
#     GameData/KSPChatBridge/Plugins/KSPChatBridge.dll
#     GameData/KSPChatBridge/Bridge/AICSBridge.exe   PyInstaller one-file build of run_bridge.py
#     (one-file on purpose: KSP tries to load EVERY *.dll under GameData as a plugin and hangs at LOADING PARTS)
#     GameData/KSPChatBridge/KSPChatBridge.version   (KSP-AVC)
#     GameData/KSPChatBridge/LICENSE, README.md
# PluginData (per-user window/bridge settings, .env, logs) is never packaged; no PDB / build path is embedded in the DLL.
# The exe is built from this repo's source in a throw-away venv (dist/.buildvenv, or -Python <python.exe> of an
# existing venv) with requirements.txt + PyInstaller. Writes SHA-256 sums to dist/SHA256SUMS-<version>.txt.
# Nothing is installed, tagged or uploaded.
param([string]$Version = "", [string]$Python = "", [switch]$SkipExe, [switch]$BridgeFree, [string]$Suffix = "")
# P5-6: -BridgeFree = release candidate without AICSBridge.exe (implies -SkipExe), ships templates + docs/BRIDGE_FREE.md.
if ($BridgeFree) { $SkipExe = $true; if (-not $Suffix) { $Suffix = "-bridgefree-rc" } }
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

$pyi = Join-Path $root "dist\pyi"
Assert-BuildPath $pyi
if (-not $SkipExe) {
    if (-not $Python) {
        $venv = Join-Path $root "dist\.buildvenv"
        if (-not (Test-Path "$venv\Scripts\python.exe")) { python -m venv $venv; if ($LASTEXITCODE -ne 0) { throw "venv failed" } }
        $Python = "$venv\Scripts\python.exe"
    }
    & $Python -m pip install -q --disable-pip-version-check -r "$root\requirements.txt" "pyinstaller==6.22.3" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "pip install failed" }
    if (Test-Path $pyi) { Remove-Item $pyi -Recurse -Force }
    # one-file (no .dll/.pyd under GameData), windowed (no console: the mod starts it hidden; logs go to PluginData/logs/bridge.log)
    & $Python -m PyInstaller --noconfirm --clean --log-level WARN --onefile --windowed --name AICSBridge `
        --icon "$root\assets\AICSBridge.ico" --add-data "$root\playstyle_notes.example.md;." `
        --collect-submodules kspchat `
        --distpath "$pyi\dist" --workpath "$pyi\build" --specpath "$pyi" "$root\run_bridge.py" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed" }
}

$stage = Join-Path $root "dist\stage"
Assert-BuildPath $stage
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$mod = Join-Path $stage "GameData\KSPChatBridge"
New-Item -ItemType Directory -Force "$mod\Plugins" | Out-Null
Copy-Item "$root\KSPChatMod\bin\Release\KSPChatBridge.dll" "$mod\Plugins\"
Copy-Item "$root\KSPChatMod\AICS_RPM.cfg" $mod
Copy-Item "$root\KSPChatMod\KSPChatBridge.version", "$root\LICENSE", "$root\README.md", "$root\personalities.txt" $mod
New-Item -ItemType Directory -Force "$mod\templates" | Out-Null
Copy-Item "$root\templates\env.example", "$root\playstyle_notes.example.md" "$mod\templates\"
if ($BridgeFree) { Copy-Item "$root\docs\BRIDGE_FREE.md" $mod }
if (-not $SkipExe) { New-Item -ItemType Directory -Force "$mod\Bridge" | Out-Null; Copy-Item "$pyi\dist\AICSBridge.exe" "$mod\Bridge\" }
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
if ($BridgeFree -and @(Get-ChildItem $stage -Recurse -File -Include *.exe).Count -gt 0) { throw "Bridge-free package must not contain an exe" }
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
if (-not $SkipExe) { $sums += "$((Get-FileHash "$mod\Bridge\AICSBridge.exe" -Algorithm SHA256).Hash.ToLower())  GameData/KSPChatBridge/Bridge/AICSBridge.exe" }
Set-Content (Join-Path $root "dist\SHA256SUMS-$Version$Suffix.txt") $sums -Encoding ascii
Remove-Item $stage -Recurse -Force
$sums | Out-Host
"package: $zip"
