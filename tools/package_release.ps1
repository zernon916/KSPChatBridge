# Build the mod (Release, WITHOUT copying into the live GameData) and zip a CKAN / manual-install package:
#   dist/KSPChatBridge-<version>.zip
#     GameData/KSPChatBridge/Plugins/KSPChatBridge.dll
#     GameData/KSPChatBridge/KSPChatBridge.version   (KSP-AVC)
#     GameData/KSPChatBridge/LICENSE, README.md
# PluginData (per-user window/bridge settings) is never packaged; no PDB / build path is embedded in the DLL. Nothing is installed, tagged or uploaded.
param([string]$Version = "")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $v = (Get-Content "$root\KSPChatMod\KSPChatBridge.version" -Raw | ConvertFrom-Json).VERSION
    $Version = "$($v.MAJOR).$($v.MINOR).$($v.PATCH)"
}
Push-Location "$root\KSPChatMod"
try { dotnet build -c Release -nologo -v q -p:SkipInstall=true -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -p:Deterministic=true | Out-Host } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$stage = Join-Path $root "dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$mod = Join-Path $stage "GameData\KSPChatBridge"
New-Item -ItemType Directory -Force "$mod\Plugins" | Out-Null
Copy-Item "$root\KSPChatMod\bin\Release\KSPChatBridge.dll" "$mod\Plugins\"
Copy-Item "$root\KSPChatMod\KSPChatBridge.version", "$root\LICENSE", "$root\README.md" $mod
$zip = Join-Path $root "dist\KSPChatBridge-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# entries with forward slashes (Windows PowerShell's Compress-Archive writes backslashes, which breaks CKAN / unzip)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, $rel)
    }
} finally { $z.Dispose() }
Remove-Item $stage -Recurse -Force
"package: $zip"
