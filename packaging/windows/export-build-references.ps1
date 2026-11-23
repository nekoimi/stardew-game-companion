[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [string]$GamePath = $env:STARDEW_GAME_PATH,
    [string]$Refasmer = 'refasmer'
)

$ErrorActionPreference = 'Stop'
if (!$GamePath) { throw 'Set STARDEW_GAME_PATH or pass -GamePath with the SMAPI game directory.' }
$game = (Resolve-Path -LiteralPath $GamePath).Path
$outputZip = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if ([IO.Path]::GetExtension($outputZip) -ne '.zip') { throw 'Output must be a ZIP path.' }
if (Test-Path -LiteralPath $outputZip) { throw 'Use a new output ZIP path.' }
$tool = (Get-Command $Refasmer -CommandType Application).Source

$files = @(
    'Stardew Valley.dll', 'StardewValley.GameData.dll', 'StardewModdingAPI.dll',
    'xTile.dll', 'MonoGame.Framework.dll', 'smapi-internal/SMAPI.Toolkit.CoreInterfaces.dll', 'smapi-internal/0Harmony.dll'
)
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $game $file) -PathType Leaf)) { throw "Missing build reference: $file" }
}

$stage = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "stardew-build-refs-$([guid]::NewGuid().ToString('N'))")).FullName
try {
    New-Item -ItemType Directory -Path (Join-Path $stage 'smapi-internal') | Out-Null
    foreach ($file in $files) {
        & $tool --all -o (Join-Path $stage $file) (Join-Path $game $file)
        if ($LASTEXITCODE) { throw "Could not export compile-time reference: $file" }
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputZip)) | Out-Null
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $outputZip)
} finally {
    foreach ($file in $files) {
        $reference = Join-Path $stage $file
        if (Test-Path -LiteralPath $reference -PathType Leaf) { Remove-Item -LiteralPath $reference }
    }
    $internal = Join-Path $stage 'smapi-internal'
    if (Test-Path -LiteralPath $internal) { [IO.Directory]::Delete($internal) }
    [IO.Directory]::Delete($stage)
}
Get-FileHash -LiteralPath $outputZip -Algorithm SHA256
