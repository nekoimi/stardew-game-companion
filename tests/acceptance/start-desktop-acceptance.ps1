[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDir,
    [Parameter(Mandatory)][string]$SavePath,
    [string]$GamePath = $env:STARDEW_GAME_PATH,
    [switch]$Simulated
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (Get-Process -Name 'Stardew Valley','StardewModdingAPI' -ErrorAction SilentlyContinue) {
    throw '请先退出正在运行的游戏。'
}
$release = (Resolve-Path -LiteralPath $ReleaseDir).Path
& (Join-Path $repo 'packaging/windows/verify-node-release.ps1') -ReleaseDir $release | Out-Host
if (!$GamePath) { $GamePath = (& (Join-Path $repo 'tools/dev/detect-game.ps1') | Out-String).Trim() }
$game = (Resolve-Path -LiteralPath $GamePath).Path
foreach ($name in 'StardewModdingAPI.exe','Stardew Valley.dll','smapi-internal/0Harmony.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $game $name) -PathType Leaf)) { throw "游戏目录缺少 $name。" }
}
$save = Get-Item -LiteralPath $SavePath
if (!$save.PSIsContainer -or !(Test-Path -LiteralPath (Join-Path $save.FullName $save.Name) -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path $save.FullName 'SaveGameInfo') -PathType Leaf)) {
    throw '请选择包含主存档文件和 SaveGameInfo 的单个存档目录。'
}
if (($save.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-ChildItem -LiteralPath $save.FullName -Force -Recurse |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1)) {
    throw '隔离验收需要普通存档目录和文件。'
}

$runsPath = Join-Path $repo '.test-runs'
if (!(Test-Path -LiteralPath $runsPath)) { $null = New-Item -ItemType Directory -Path $runsPath }
$runs = Get-Item -LiteralPath $runsPath -Force
if ($runs -isnot [IO.DirectoryInfo] -or ($runs.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw '验收需要普通运行目录。'
}
& (Join-Path $PSScriptRoot 'build-testdriver.ps1') -GamePath $game | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'TestDriver build failed.' }
$runId = 'desktop-' + (Get-Date -Format 'MMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 4)
$run = Join-Path $runs.FullName $runId
$evidence = Join-Path $run 'evidence'
$saves = Join-Path $run 'saves'
$desktop = Join-Path $run 'desktop'
$driver = Join-Path $desktop 'Mods/StardewAI.Companion.TestDriver'
$driverBuild = Join-Path $repo 'artifacts/bin/StardewAI.Companion.TestDriver/Debug/net6.0'
$null = New-Item -ItemType Directory -Path $evidence, $saves
Copy-Item -LiteralPath $release -Destination $desktop -Recurse
Copy-Item -LiteralPath $save.FullName -Destination $saves -Recurse
$null = New-Item -ItemType Directory -Path $driver, (Join-Path $desktop 'data')
foreach ($name in 'StardewAI.Companion.TestDriver.dll','manifest.json') {
    Copy-Item -LiteralPath (Join-Path $driverBuild $name) -Destination $driver
}

$session = Join-Path $run 'test-session.json'
@{ runId = $runId; testMode = 'interactive'; scenario = 'free-mode-control'; isolatedSavesPath = $saves;
    evidenceDir = $evidence; evidenceOutputPath = (Join-Path $evidence 'test-driver.json') } |
    ConvertTo-Json | Set-Content -LiteralPath $session -Encoding utf8NoBOM
@{ GameDirectory = $game; ActorMode = 'player'; ApiModel = @{ Provider = 'deepseek'; Model = 'deepseek-flash' } } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $desktop 'data/desktop-settings.json') -Encoding utf8NoBOM
@{
    runId = $runId
    simulated = $Simulated.IsPresent
    gameVersion = (Get-Item -LiteralPath (Join-Path $game 'Stardew Valley.dll')).VersionInfo.FileVersion
    smapiVersion = (Get-Item -LiteralPath (Join-Path $game 'StardewModdingAPI.dll')).VersionInfo.FileVersion
    packageManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $desktop 'release-manifest.json')).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'desktop-package.json') -Encoding utf8NoBOM

$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $desktop '星露谷游戏伴侣.exe'))
$start.UseShellExecute = $false
$start.WorkingDirectory = $desktop
$start.Environment['PATH'] = "$env:SystemRoot/System32;$env:SystemRoot"
$start.Environment['STARDEW_AI_SESSION_CONFIG'] = $session
$start.Environment['STARDEW_AGENT_ACCEPTANCE_TRACE'] = Join-Path $evidence 'agent-runtime-events.jsonl'
foreach ($name in 'DEEPSEEK_API_KEY','OPENAI_API_KEY','OPENAI_COMPATIBLE_API_KEY','OPENAI_COMPATIBLE_BASE_URL','NODE_OPTIONS') {
    $null = $start.Environment.Remove($name)
}
if ($Simulated) {
    $preload = [uri]::new((Join-Path $PSScriptRoot 'desktop-provider-preload.mjs')).AbsoluteUri
    $start.Environment['NODE_OPTIONS'] = "--import=$preload"
}
$process = [Diagnostics.Process]::Start($start)
[pscustomobject]@{ RunDirectory = $run; DesktopProcessId = $process.Id }
$process.Dispose()
