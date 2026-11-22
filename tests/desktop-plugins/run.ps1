param(
    [string]$DotNet = 'dotnet',
    [string]$Go = 'go'
)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("companion-plugin-fixture-" + [guid]::NewGuid().ToString("N") + ".exe")
try {
    & $Go build -o $fixture "$PSScriptRoot/fixture/main.go"
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    & $DotNet run --project "$PSScriptRoot/PluginHostChecks.csproj" -c Release -- $fixture "$PSScriptRoot/../../contracts/plugin-host/examples/v1.json"
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $fixture -ErrorAction SilentlyContinue
}
