[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [string]$DotNet = 'dotnet',
    [string]$Go = 'go'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (Test-Path -LiteralPath $Output) { throw 'Use a new sample output directory.' }
$out = (New-Item -ItemType Directory -Path $Output).FullName
$command = (New-Item -ItemType Directory -Path (Join-Path $out 'command/bin')).FullName
$events = (New-Item -ItemType Directory -Path (Join-Path $out 'events/bin')).FullName
& $Go build -trimpath -ldflags '-s -w' -o (Join-Path $command 'CommandSample.exe') (Join-Path $PSScriptRoot 'command/main.go')
if ($LASTEXITCODE) { throw 'Command sample build failed.' }
$goRoot = (& $Go env GOROOT).Trim()
if ($LASTEXITCODE) { throw 'Unable to locate the Go standard library license.' }
Copy-Item -LiteralPath (Join-Path $goRoot 'LICENSE') -Destination (Join-Path $command '../Go-LICENSE.txt')
& $DotNet publish (Join-Path $PSScriptRoot 'events/EventSubscriber.csproj') -c Release -o $events
if ($LASTEXITCODE) { throw 'Event sample build failed.' }

$fixture = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repo 'contracts/plugin-host/examples/v1.json') | ConvertFrom-Json -AsHashtable
foreach ($sample in 'command', 'events') {
    $root = Join-Path $out $sample
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'), (Join-Path $repo 'NOTICE') -Destination $root
    $manifest = $fixture.package.manifest.Clone()
    $manifest.id = "org.example.$sample"
    $manifest.entry = @{ executable = 'bin/CommandSample.exe'; args = @(); workingDirectory = '.' }
    if ($sample -eq 'events') {
        $manifest.name = '事件订阅示例'
        $manifest.entry.executable = 'bin/EventSubscriber.exe'
        $manifest.Remove('settings')
    }
    ConvertTo-Json -InputObject $manifest -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'plugin.json') -Encoding utf8NoBOM
    $files = @(Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{
            path = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    @{ files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'files.json') -Encoding utf8NoBOM
    $archive = Join-Path $out "$sample-$($manifest.version)-win-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($root, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Output $archive
}
