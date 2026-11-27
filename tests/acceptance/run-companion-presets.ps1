[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ReleaseDir,
    [string]$SavePath = '',
    [ValidateSet('navigation-shop','water-refill','harvest-store','rest-water','mature-trees','walk-pickup','walk-pickup-full','care-guidance','harvest-logistics','rest-forestry','empty-farm','farm-growth','phase-forestry','phase-daily','phase-remote','phase-cache','phase-update','phase-takeover','phase-backpack','phase-shipping')]
    [string[]]$Cases = @('care-guidance','harvest-logistics','rest-forestry'),
    [ValidateRange(60,3600)][int]$TimeoutSeconds = 600,
    [ValidateSet('deepseek','openai','openai-compatible','custom')][string]$Provider = 'deepseek',
    [ValidateSet('responses','chat-completions')][string]$Protocol,
    [switch]$NonStreaming,
    [string]$Model,
    [switch]$Simulated,
    [switch]$PlayerMode,
    [string]$Python
)
$ErrorActionPreference = 'Stop'
# Surface the external runner's non-zero result even if a caller's finally
# block runs another cmdlet; failures must not look like successful acceptance.
$script:acceptanceExit = 0
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$release = (Resolve-Path -LiteralPath $ReleaseDir).Path
if ($Provider -eq 'custom' -and !$Protocol) { throw 'Custom acceptance requires -Protocol.' }
if ($Protocol -and $Provider -ne 'custom') { throw 'Use -Protocol with -Provider custom.' }
if ($Provider -in 'custom','openai-compatible' -and (!$Model -or
        (!$Simulated -and [string]::IsNullOrWhiteSpace($env:OPENAI_COMPATIBLE_BASE_URL)))) {
    throw 'Custom acceptance requires -Model and a real run requires OPENAI_COMPATIBLE_BASE_URL.'
}
if (!$Python) { $Python = 'python' }
if (!$Model) { $Model = if ($Provider -eq 'openai') { 'gpt-5-mini' } else { 'deepseek-flash' } }
$required = switch ($Provider) {
    'openai' { 'OPENAI_API_KEY' }
    'openai-compatible' { 'OPENAI_COMPATIBLE_API_KEY' }
    'custom' { 'OPENAI_COMPATIBLE_API_KEY' }
    default { 'DEEPSEEK_API_KEY' }
}
if (!$Simulated -and [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($required))) {
    throw "Set $required in the current process before starting formal acceptance."
}
$shell = (Get-Process -Id $PID).Path
if (Get-Process -Name 'Stardew Valley','StardewModdingAPI' -ErrorAction SilentlyContinue) {
    throw '请先退出正在运行的游戏；验收只启动和关闭自己的隔离实例。'
}
& (Join-Path $root 'packaging/windows/verify-node-release.ps1') -ReleaseDir $release
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
$launchArgs += @('-ApiProvider', $Provider, '-ApiModel', $Model)
if ($Protocol) { $launchArgs += @('-ApiProtocol', $Protocol) }
if ($NonStreaming) { $launchArgs += '-ApiNonStreaming' }
if ($Simulated) { $launchArgs += '-ApiSimulated' }
if ($PlayerMode) { $launchArgs += '-PlayerMode' }
try {
    $launcher = Start-Process -FilePath $shell -ArgumentList $launchArgs -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $suite 'launch.log') -RedirectStandardError (Join-Path $suite 'launch.err')
    $runnerArgs = @('--run-dir', $run, '--suite') + $Cases + @('--timeout', $TimeoutSeconds, '--cases-file', (Join-Path $suite 'cases.json'))
    if ($Simulated) { $runnerArgs += '--simulated' }
    & $Python (Join-Path $PSScriptRoot 'runner.py') @runnerArgs
    $runnerExit = $LASTEXITCODE
    $launcher.WaitForExit()
    Write-Host "伙伴实机回归报告：$run/summary.md"
    if ($runnerExit -ne 0 -or $launcher.ExitCode -ne 0) { $script:acceptanceExit = 1 }
} finally {
    Remove-Item Env:STARDEW_AI_ENV_STARDEW_AI_HARNESS_FIXTURE -ErrorAction SilentlyContinue
    Remove-Item Env:STARDEW_AI_ENV_STARDEW_AI_HARNESS_STAMINA -ErrorAction SilentlyContinue
    Remove-Item Env:STARDEW_AI_ENV_STARDEW_AI_HARNESS_WATER -ErrorAction SilentlyContinue
    Remove-Item Env:STARDEW_AI_ENV_STARDEW_AI_OBSERVE_SECONDS -ErrorAction SilentlyContinue
}
if ($script:acceptanceExit -ne 0) { throw "伙伴实机回归失败。查看 $run/summary.md" }
