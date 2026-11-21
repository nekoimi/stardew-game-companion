[CmdletBinding()]
param([Parameter(Mandatory)][string]$Archive)

$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $Archive).Path
$zip = [IO.Compression.ZipFile]::OpenRead($path)
try {
    $entries = [Collections.Generic.Dictionary[string, System.IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        $name = $entry.FullName
        if ($name -match '(^/|\\|:|(^|/)\.\.?(/|$)|//)' -or
            $name -notmatch '^(星露谷游戏伴侣\.exe$|release-manifest\.json$|docs/|runtime/|Mods/StardewAI\.Companion\.Mod/)' -or
            $name -match '^Mods/StardewAI\.Companion\.Mod/(data/|config(?:/|\.json$))|(^|/)(\.git|\.env)(/|\.|$)|\.(py|pyc|pdb|sqlite|pem|key)$') {
            throw "Unexpected release path: $name"
        }
        if (!$entries.TryAdd($name, $entry)) { throw "Duplicate archive path: $name" }
    }
    foreach ($required in @('星露谷游戏伴侣.exe', 'release-manifest.json', 'docs/LICENSE', 'docs/NOTICE',
        'docs/DotNet-LICENSE.txt', 'docs/DotNet-ThirdPartyNotices.txt', 'docs/desktop-api-preview.md', 'runtime/node.exe',
        'runtime/dist/bootstrap.js', 'runtime/models.json', 'runtime/licenses/node/LICENSE',
        'runtime/licenses/npm.json', 'runtime/package-lock.json', 'Mods/StardewAI.Companion.Mod/manifest.json',
        'Mods/StardewAI.Companion.Mod/StardewAI.Companion.Mod.dll', 'Mods/StardewAI.Companion.Mod/docs/agent-ts-instructions.md')) {
        if (!$entries.ContainsKey($required)) { throw "Missing release file: $required" }
    }
    $reader = [IO.StreamReader]::new($entries['release-manifest.json'].Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.protocolVersion -ne 2 -or $manifest.files.Count -ne $entries.Count - 1) { throw 'Invalid archive manifest.' }
    $verified = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if ($file.path -eq 'release-manifest.json' -or !$verified.Add($file.path) -or !$entries.ContainsKey($file.path)) {
            throw "Unexpected manifest entry: $($file.path)"
        }
        $stream = $entries[$file.path].Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($hash -ne $file.sha256) { throw "Checksum mismatch: $($file.path)" }
    }
    "Verified $($entries.Count) archive files and SHA256 manifest."
} finally { $zip.Dispose() }
