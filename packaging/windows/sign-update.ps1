[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$PublicKeyFile
)

$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
$publicPath = (Resolve-Path -LiteralPath $PublicKeyFile).Path
$privateBytes = $null
$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    if (!$env:STARDEW_UPDATE_SIGNING_KEY) {
        throw 'Set the STARDEW_UPDATE_SIGNING_KEY secret before publishing an update.'
    }
    $privateBytes = [Convert]::FromBase64String($env:STARDEW_UPDATE_SIGNING_KEY)
    [int]$bytesRead = 0
    $key.ImportPkcs8PrivateKey($privateBytes, [ref]$bytesRead)
    $expected = [Convert]::ToBase64String([IO.File]::ReadAllBytes($publicPath))
    if ($bytesRead -ne $privateBytes.Length -or $key.KeySize -ne 256 -or
        [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()) -cne $expected) {
        throw 'The update signing key must match the embedded P-256 public key.'
    }
    $hash = [Convert]::FromHexString((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash)
    $signature = $key.SignHash($hash, [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    [IO.File]::WriteAllBytes("$archivePath.sig", $signature)
}
finally {
    if ($privateBytes) { [Array]::Clear($privateBytes) }
    $key.Dispose()
}
