[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [string]$Cache,
    [string]$RuntimeProject
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$Cache) { $Cache = Join-Path $repo 'artifacts/downloads' }
if (!$RuntimeProject) { $RuntimeProject = Join-Path $repo 'apps/runtime' }
$RuntimeProject = (Resolve-Path $RuntimeProject).Path
$version = '22.22.2'
$nodeDirName = "node-v$version-win-x64"
$nodeArchiveHash = '7c93e9d92bf68c07182b471aa187e35ee6cd08ef0f24ab060dfff605fcc1c57c'
$archive = Join-Path (New-Item -ItemType Directory -Force $Cache).FullName "$nodeDirName.zip"
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -UseBasicParsing "https://nodejs.org/dist/v$version/$nodeDirName.zip" -OutFile $archive -TimeoutSec 120
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $nodeArchiveHash) {
    throw "Invalid Node archive checksum: $archive"
}
if (Test-Path -LiteralPath $Output) { throw "Use a new output directory: $Output" }
$stage = (New-Item -ItemType Directory -Path $Output).FullName
$workspace = (New-Item -ItemType Directory -Path (Join-Path $stage 'build')).FullName
$runtime = (New-Item -ItemType Directory -Path (Join-Path $stage 'runtime')).FullName
Expand-Archive -LiteralPath $archive -DestinationPath $workspace
$nodeTools = Join-Path $workspace $nodeDirName
$node = Join-Path $nodeTools 'node.exe'
$npm = Join-Path $nodeTools 'node_modules/npm/bin/npm-cli.js'
$manifest = Get-Content (Join-Path $RuntimeProject 'package.json') -Raw | ConvertFrom-Json
if ((& $node --version) -ne "v$($manifest.engines.node)" -or (& $node $npm --version) -ne $manifest.engines.npm) {
    throw 'Bundled Node and npm do not match the runtime project.'
}
Copy-Item -LiteralPath (Join-Path $RuntimeProject 'package.json'), (Join-Path $RuntimeProject 'package-lock.json'), (Join-Path $RuntimeProject 'tsconfig.json') -Destination $workspace
Copy-Item -LiteralPath (Join-Path $RuntimeProject 'src') -Destination $workspace -Recurse
Copy-Item -LiteralPath (Join-Path $RuntimeProject 'package.json'), (Join-Path $RuntimeProject 'package-lock.json') -Destination $runtime

Push-Location $workspace
try {
    & $node $npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE) { throw 'Locked build dependency installation failed.' }
    & $node (Join-Path $workspace 'node_modules/typescript/bin/tsc') -p tsconfig.json
    if ($LASTEXITCODE) { throw 'Runtime compilation failed.' }
} finally { Pop-Location }

Copy-Item -LiteralPath (Join-Path $workspace 'dist') -Recurse -Destination $runtime
Copy-Item -LiteralPath $node -Destination $runtime
$nodeLicense = New-Item -ItemType Directory -Path (Join-Path $runtime 'licenses/node')
Copy-Item -LiteralPath (Join-Path $nodeTools 'LICENSE') -Destination $nodeLicense.FullName
Push-Location $runtime
try {
    & $node $npm ci --omit=dev --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE) { throw 'Locked production dependency installation failed.' }
    $packages = (Get-Content -LiteralPath (Join-Path $runtime 'package-lock.json') -Raw | ConvertFrom-Json -AsHashtable).packages
    $licenses = foreach ($entry in $packages.GetEnumerator()) {
        if (!$entry.Key -or !(Test-Path -LiteralPath (Join-Path $runtime $entry.Key))) { continue }
        [pscustomobject]@{ path = $entry.Key; version = $entry.Value.version; license = $entry.Value.license }
    }
    ConvertTo-Json -InputObject @($licenses) -Depth 4 | Set-Content -LiteralPath (Join-Path $runtime 'licenses/npm.json') -Encoding utf8
    $savedPath = $env:PATH
    try {
        $env:PATH = "$env:SystemRoot\System32"
        & (Join-Path $runtime 'node.exe') --disable-warning=ExperimentalWarning (Join-Path $runtime 'dist/bootstrap.js') --check
        if ($LASTEXITCODE) { throw 'Portable runtime self-check failed.' }
        & (Join-Path $runtime 'node.exe') --disable-warning=ExperimentalWarning (Join-Path $runtime 'dist/bootstrap.js') --catalog |
            Set-Content -LiteralPath (Join-Path $runtime 'models.json') -Encoding utf8NoBOM
        if ($LASTEXITCODE) { throw 'Could not generate the bundled model catalogue.' }
    } finally { $env:PATH = $savedPath }
} finally { Pop-Location }

Write-Output $runtime
