[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ReleaseDir,
    [string]$SavePath = '',
    [ValidateSet('water-refill','harvest-store','rest-water','mature-trees','walk-pickup','walk-pickup-full','care-guidance','harvest-logistics','rest-forestry')]
    [string[]]$Cases = @('care-guidance','harvest-logistics','rest-forestry'),
    [ValidateRange(60,3600)][int]$TimeoutSeconds = 600,
    [ValidateSet('deepseek','openai')][string]$Provider = 'deepseek',
    [string]$Model,
    [switch]$Simulated,
    [string]$Python
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$release = (Resolve-Path -LiteralPath $ReleaseDir).Path
$apiRelease = Test-Path -LiteralPath (Join-Path $release 'runtime/dist/bootstrap.js') -PathType Leaf
if (!$Python) {
    $Python = Join-Path $root 'apps/runtime/.venv/Scripts/python.exe'
    if (!(Test-Path -LiteralPath $Python -PathType Leaf)) { $Python = 'python' }
}
if (!$apiRelease -and ($Simulated -or $Model -or $Provider -ne 'deepseek')) { throw 'API options require a v2 green package.' }
if ($Simulated -and $Provider -ne 'deepseek') { throw 'The fixed farm fixture currently speaks simulated DeepSeek.' }
if ($apiRelease) {
    if (!$Model) { $Model = if ($Provider -eq 'openai') { 'gpt-5-mini' } else { 'deepseek-flash' } }
    $required = if ($Provider -eq 'openai') { 'OPENAI_API_KEY' } else { 'DEEPSEEK_API_KEY' }
    if (!$Simulated -and ![Environment]::GetEnvironmentVariable($required)) {
        throw "Set $required in the current process before starting formal acceptance."
    }
}
$shell = (Get-Process -Id $PID).Path
if (Get-Process -Name 'Stardew Valley','StardewModdingAPI' -ErrorAction SilentlyContinue) {
    throw '请先退出正在运行的游戏；验收只启动和关闭自己的隔离实例。'
}
& (Join-Path $PSScriptRoot 'build-testdriver.ps1') -Configuration Debug
if ($LASTEXITCODE -ne 0) { throw 'TestDriver build failed.' }
$suiteId = 'presets-' + (Get-Date -Format 'MMdd-HHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0,4))
$suite = Join-Path $root ".test-runs/$suiteId"
$runId = "$suiteId-game"
$run = Join-Path $root ".test-runs/$runId"
New-Item -ItemType Directory -Path $suite | Out-Null
$specPath = Join-Path $PSScriptRoot 'cases.json'
$specs = Get-Content $specPath -Raw | ConvertFrom-Json
Copy-Item -LiteralPath $specPath -Destination (Join-Path $suite 'cases.json')
$first = $specs.($Cases[0])
$env:STARDEW_AI_ENV_STARDEW_AI_HARNESS_FIXTURE = if ($first.fixture) { $first.fixture } else { $Cases[0] }
$env:STARDEW_AI_ENV_STARDEW_AI_HARNESS_STAMINA = [string]$first.stamina
$env:STARDEW_AI_ENV_STARDEW_AI_HARNESS_WATER = [string]$first.water
$budget = $TimeoutSeconds * $Cases.Count
$env:STARDEW_AI_ENV_STARDEW_AI_OBSERVE_SECONDS = [string]($budget + 120)
$launchArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',
    ('"' + (Join-Path $PSScriptRoot 'launch-game.ps1') + '"'),
    '-Mode','scenario','-Scenario','free-mode-control','-RunId',$runId,
    '-FrozenProdDir',('"' + $release + '"'),'-PresetAcceptance',
    '-TimeoutSeconds',($budget + 420))
if ($SavePath) { $launchArgs += @('-CustomSavePath',('"' + (Resolve-Path -LiteralPath $SavePath).Path + '"')) }
if ($apiRelease) {
    $launchArgs += @('-ApiProvider', $Provider, '-ApiModel', $Model)
    if ($Simulated) { $launchArgs += '-ApiSimulated' }
}
try {
    $launcher = Start-Process -FilePath $shell -ArgumentList $launchArgs -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $suite 'launch.log') -RedirectStandardError (Join-Path $suite 'launch.err')
    $runnerArgs = @('--run-dir', $run, '--suite') + $Cases + @('--timeout', $TimeoutSeconds, '--cases-file', (Join-Path $suite 'cases.json'))
    if ($apiRelease) { $runnerArgs += '--api-runtime' }
    & $Python (Join-Path $PSScriptRoot 'runner.py') @runnerArgs
    $runnerExit = $LASTEXITCODE
    $launcher.WaitForExit()
    Write-Host "伙伴实机回归报告：$run/summary.md"
    if ($runnerExit -ne 0 -or $launcher.ExitCode -ne 0) { exit 1 }
} finally {
    Remove-Item Env:STARDEW_AI_ENV_STARDEW_AI_HARNESS_FIXTURE -ErrorAction SilentlyContinue
    Remove-Item Env:STARDEW_AI_ENV_STARDEW_AI_HARNESS_STAMINA -ErrorAction SilentlyContinue
    Remove-Item Env:STARDEW_AI_ENV_STARDEW_AI_HARNESS_WATER -ErrorAction SilentlyContinue
    Remove-Item Env:STARDEW_AI_ENV_STARDEW_AI_OBSERVE_SECONDS -ErrorAction SilentlyContinue
}
