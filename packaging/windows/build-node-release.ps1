[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [string]$GamePath = $env:STARDEW_GAME_PATH,
    [string]$DotNet = 'dotnet',
    [string]$Cache,
    [string]$ReleaseTag,
    [string]$UpdatePublicKeyFile,
    [ValidateSet('stable', 'preview')][string]$Channel
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$version = (Get-Content -LiteralPath (Join-Path $repository 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid product version in VERSION: $version" }
if (!$ReleaseTag) { $ReleaseTag = "v$version" }
if (!$Channel) { $Channel = if ($ReleaseTag -match '^v?\d+\.\d+\.\d+-') { 'preview' } else { 'stable' } }
$releaseVersion = if ($ReleaseTag -match '^v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$') { $Matches[1] } else { $version }
if (!$GamePath -or !(Test-Path -LiteralPath (Join-Path $GamePath 'StardewModdingAPI.dll') -PathType Leaf)) {
    throw 'Set STARDEW_GAME_PATH or -GamePath to a Stardew Valley installation with SMAPI.'
}
if (Test-Path -LiteralPath $Output) { throw 'Use a new output directory.' }
$destination = [IO.Path]::GetFullPath($Output)
$desktop = Join-Path $destination 'desktop-build'
$payload = Join-Path $destination 'payload'
$mod = Join-Path $destination 'mod-build'
$updater = Join-Path $destination 'updater-build'

$desktopProperties = @()
if ($UpdatePublicKeyFile) {
    $publicKey = (Resolve-Path -LiteralPath $UpdatePublicKeyFile).Path
    $desktopProperties += "-p:UpdatePublicKeyFile=$publicKey"
}
& $DotNet publish (Join-Path $repository 'apps/desktop/src') -c Release -o $desktop @desktopProperties
if ($LASTEXITCODE) { throw 'Desktop publish failed.' }
& $DotNet publish (Join-Path $repository 'apps/updater') -c Release -o $updater
if ($LASTEXITCODE) { throw 'Updater publish failed.' }
$game = (Resolve-Path -LiteralPath $GamePath).Path
& $DotNet build (Join-Path $repository 'apps/game-mod/src') -c Release -o $mod "-p:StardewGamePath=$game" -p:EnableModDeploy=false
if ($LASTEXITCODE) { throw 'Mod build failed.' }
$built = & (Join-Path $PSScriptRoot 'stage-windows-release.ps1') -Output $payload -DesktopSource $desktop -ModSource $mod `
    -UpdaterSource $updater -ReleaseInfo @{tag=$ReleaseTag; version=$releaseVersion; channel=$Channel; minimumUpdaterProtocol=1; builtAt=[DateTimeOffset]::UtcNow.ToString('O'); dataProfileVersion=1} -Cache $Cache
$built | Write-Output
$root = $built[-1]

$manifestVersion = (Get-Content -LiteralPath (Join-Path $mod 'manifest.json') -Raw | ConvertFrom-Json).Version
if ($manifestVersion -ne $version) { throw "Built Mod version '$manifestVersion' does not match VERSION '$version'." }
$archive = Join-Path $destination "StardewGameCompanion-$version-windows-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($root, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
& (Join-Path $PSScriptRoot 'verify-node-release.ps1') -Archive $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding utf8NoBOM
Write-Output $archive
