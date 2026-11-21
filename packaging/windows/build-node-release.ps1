[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [string]$GamePath = $env:STARDEW_GAME_PATH,
    [string]$DotNet = 'dotnet',
    [string]$Cache
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$GamePath -or !(Test-Path -LiteralPath (Join-Path $GamePath 'StardewModdingAPI.dll') -PathType Leaf)) {
    throw 'Set STARDEW_GAME_PATH or -GamePath to a Stardew Valley installation with SMAPI.'
}
if (Test-Path -LiteralPath $Output) { throw 'Use a new output directory.' }
$destination = [IO.Path]::GetFullPath($Output)
$desktop = Join-Path $destination 'desktop-build'
$payload = Join-Path $destination 'payload'
$mod = Join-Path $destination 'mod-build'

& $DotNet publish (Join-Path $repository 'apps/desktop/src') -c Release -o $desktop
if ($LASTEXITCODE) { throw 'Desktop publish failed.' }
$game = (Resolve-Path -LiteralPath $GamePath).Path
& $DotNet build (Join-Path $repository 'apps/game-mod/src') -c Release -o $mod "-p:StardewGamePath=$game"
if ($LASTEXITCODE) { throw 'Mod build failed.' }
$built = & (Join-Path $PSScriptRoot 'build-node-preview.ps1') -Output $payload -DesktopSource $desktop -ModSource $mod -Cache $Cache
$built | Write-Output
$root = $built[-1]

$version = (Get-Content -LiteralPath (Join-Path $mod 'manifest.json') -Raw | ConvertFrom-Json).Version
$archive = Join-Path $destination "StardewGameCompanion-$version-api-preview-windows-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($root, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
& (Join-Path $PSScriptRoot 'verify-node-release.ps1') -Archive $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding utf8NoBOM
Write-Output $archive
