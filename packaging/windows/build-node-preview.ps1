[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$DesktopSource,
    [Parameter(Mandatory)][string]$ModSource,
    [string]$Cache
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$created = & (Join-Path $PSScriptRoot 'stage-node-runtime.ps1') -Output $Output -Cache $Cache
$created | Write-Output
$runtime = $created[-1]
$root = (New-Item -ItemType Directory -Path (Join-Path $Output 'green')).FullName
Move-Item -LiteralPath $runtime -Destination (Join-Path $root 'runtime')
$mod = (New-Item -ItemType Directory -Path (Join-Path $root 'Mods/StardewAI.Companion.Mod')).FullName
$modDocs = (New-Item -ItemType Directory -Path (Join-Path $mod 'docs')).FullName
$docs = (New-Item -ItemType Directory -Path (Join-Path $root 'docs')).FullName

foreach ($name in 'manifest.json', 'StardewAI.Companion.Mod.dll', 'StardewAI.Companion.Mod.deps.json') {
    Copy-Item -LiteralPath (Join-Path $ModSource $name) -Destination $mod
}
Copy-Item -LiteralPath (Join-Path $DesktopSource '星露谷游戏伴侣.exe') -Destination $root
foreach ($name in 'DotNet-LICENSE.txt', 'DotNet-ThirdPartyNotices.txt') {
    Copy-Item -LiteralPath (Join-Path $DesktopSource $name) -Destination $docs
}
foreach ($name in 'LICENSE', 'NOTICE') { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $docs }
Copy-Item -LiteralPath (Join-Path $repo 'resources/agent/prompts/agent-ts-instructions.md') -Destination $modDocs
Copy-Item -LiteralPath (Join-Path $repo 'resources/agent/prompts/agent-guidance') -Destination $modDocs -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'resources/agent/skills') -Destination (Join-Path $mod 'agent-skills') -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'docs/desktop-api-preview.md') -Destination $docs
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\','/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
ConvertTo-Json -InputObject ([ordered]@{ protocolVersion = 2; files = $files }) -Depth 5 |
    Set-Content -LiteralPath (Join-Path $root 'release-manifest.json') -Encoding utf8
Write-Output $root
