[CmdletBinding()]
param(
    [string]$GamePath = $env:STARDEW_GAME_PATH,
    [string]$DotNet = $env:STAR_DOTNET_PATH,
    [string]$Python = 'python'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$DotNet) { $DotNet = (Get-Command dotnet).Source }
if (!$GamePath -or !(Test-Path -LiteralPath (Join-Path $GamePath 'StardewModdingAPI.dll'))) {
    throw 'Set STARDEW_GAME_PATH or pass -GamePath with the SMAPI game directory.'
}
$env:STARDEW_GAME_PATH = (Resolve-Path -LiteralPath $GamePath).Path
$env:STAR_DOTNET_PATH = (Get-Command $DotNet).Source
$runtime = Join-Path $root 'apps/runtime'

& npm --prefix $runtime ci --ignore-scripts
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& npm --prefix $runtime test
if ($LASTEXITCODE) { exit $LASTEXITCODE }
foreach ($name in 'StardewAI.Companion.Transport.Tests', 'StardewAI.Companion.Mod.Tests') {
    & $env:STAR_DOTNET_PATH test (Join-Path $root "apps/game-mod/tests/$name") -c Release --nologo
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
& $Python -m pytest (Join-Path $root 'tests/acceptance/test_runner.py') -q
exit $LASTEXITCODE
