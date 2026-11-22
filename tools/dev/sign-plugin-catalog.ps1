param(
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$PublisherPrivatePem,
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string[]]$Archives,
    [ValidateRange(1,365)][int]$ValidDays = 30,
    [ValidateRange(1,2147483647)][int]$DataVersion = 1
)
$ErrorActionPreference = 'Stop'
[uri]$base = $null
if (![uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$base) -or $base.Scheme -ne 'https' -or $base.UserInfo -or $base.Query -or $base.Fragment) {
    throw 'BaseUrl must be an HTTPS directory URL.'
}
if (Test-Path -LiteralPath $Output) { throw 'Use a new publishing directory.' }
$format = [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
$sha = [Security.Cryptography.HashAlgorithmName]::SHA256
$publisherKey = [Security.Cryptography.ECDsa]::Create()
try {
    $publisherKey.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $PublisherPrivatePem).Path))
    $curve = [Security.Cryptography.ECCurve+NamedCurves]::nistP256.Oid.Value
    if ($publisherKey.ExportParameters($false).Curve.Oid.Value -ne $curve) {
        throw 'The publisher key must be ECDSA P-256.'
    }
    $entries = foreach ($archive in $Archives) {
        $path = (Resolve-Path -LiteralPath $archive).Path
        $zip = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $manifestEntry = $zip.GetEntry('plugin.json')
            if (!$manifestEntry -or $manifestEntry.Length -gt 65536) { throw 'Plugin manifest missing or too large.' }
            $reader = [IO.StreamReader]::new($manifestEntry.Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
        if ($manifest.publisher -cne $Publisher) { throw 'Manifest publisher does not match the signing publisher.' }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $archiveName = [IO.Path]::GetFileName($path)
        @{
            id = $manifest.id; name = $manifest.name; publisher = $manifest.publisher; version = $manifest.version
            hostProtocol = $manifest.hostProtocol; downloadUrl = $BaseUrl.TrimEnd('/') + '/' + [uri]::EscapeDataString($archiveName)
            sha256 = $hash; packageSignature = [Convert]::ToBase64String($publisherKey.SignData([Convert]::FromHexString($hash), $sha, $format))
            dataVersion = $DataVersion
        }
    }
    if (@($entries).Count -eq 0 -or (@($entries | Group-Object id, version | Where-Object Count -gt 1)).Count -ne 0) {
        throw 'Provide at least one ZIP and only one archive per plugin version.'
    }
    if ((@($Archives | ForEach-Object { [IO.Path]::GetFileName($_) } | Group-Object | Where-Object Count -gt 1)).Count -ne 0) {
        throw 'Archive filenames must be unique.'
    }
    $index = @{
        catalogVersion = 1
        expiresAt = [DateTimeOffset]::UtcNow.AddDays($ValidDays).ToString('O')
        publishers = @(@{ name = $Publisher; publicKey = [Convert]::ToBase64String($publisherKey.ExportSubjectPublicKeyInfo()) })
        plugins = @($entries)
    }
    $destination = (New-Item -ItemType Directory -Path $Output).FullName
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $Archives).Path -Destination $destination
    ConvertTo-Json -InputObject $index -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'catalog.json') -Encoding utf8NoBOM
    $destination
} finally {
    $publisherKey.Dispose()
}
