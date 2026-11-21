[CmdletBinding(DefaultParameterSetName='Archive')]
param(
    [Parameter(Mandatory, ParameterSetName='Archive', Position=0)][string]$Archive,
    [Parameter(Mandatory, ParameterSetName='Directory')][string]$ReleaseDir
)

$ErrorActionPreference = 'Stop'
$entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
$zip = $null
$allowed = '^(星露谷游戏伴侣\.exe|release-manifest\.json|docs/(LICENSE|NOTICE|CHANGELOG\.md|desktop-portable\.md|DotNet-(LICENSE|ThirdPartyNotices)\.txt)|runtime/(node\.exe|models\.json|package(-lock)?\.json|licenses/(node/LICENSE|npm\.json)|(dist|node_modules)/.+)|Mods/StardewAI\.Companion\.Mod/(manifest\.json|StardewAI\.Companion\.Mod\.(dll|deps\.json)|docs/(agent-ts-instructions\.md|agent-guidance/[a-z-]+\.md)))$'

function Add-ReleaseEntry([string]$Name, $Entry) {
    if ($Name -match '(^/|\\|:|(^|/)\.\.?(/|$)|//)' -or
        $Name -notmatch $allowed -or
        $Name -match '(^|/)(\.git|\.env)(/|\.|$)|\.(py|pyc|pdb|sqlite|pem|key)$') {
        throw "Unexpected release path: $Name"
    }
    if (!$entries.TryAdd($Name, $Entry)) { throw "Duplicate release path: $Name" }
}

function Open-ReleaseEntry($Entry) {
    if ($Entry -is [IO.FileInfo]) { return $Entry.OpenRead() }
    return $Entry.Open()
}

try {
    if ($Archive) {
        $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
        foreach ($entry in $zip.Entries) {
            if (!$entry.FullName.EndsWith('/')) { Add-ReleaseEntry $entry.FullName $entry }
        }
        $source = 'archive'
    } else {
        $root = Get-Item -LiteralPath $ReleaseDir -Force
        if ($root -isnot [IO.DirectoryInfo] -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Use a release directory from a fresh extraction.'
        }
        foreach ($entry in Get-ChildItem -LiteralPath $root.FullName -Force -Recurse) {
            $name = [IO.Path]::GetRelativePath($root.FullName, $entry.FullName).Replace('\', '/')
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected release link: $name" }
            if (!$entry.PSIsContainer) { Add-ReleaseEntry $name $entry }
        }
        $source = 'extracted release'
    }
    foreach ($required in @('星露谷游戏伴侣.exe', 'release-manifest.json', 'docs/LICENSE', 'docs/NOTICE',
        'docs/DotNet-LICENSE.txt', 'docs/DotNet-ThirdPartyNotices.txt', 'docs/desktop-portable.md', 'docs/CHANGELOG.md', 'runtime/node.exe',
        'runtime/dist/bootstrap.js', 'runtime/models.json', 'runtime/licenses/node/LICENSE',
        'runtime/licenses/npm.json', 'runtime/package-lock.json', 'Mods/StardewAI.Companion.Mod/manifest.json',
        'Mods/StardewAI.Companion.Mod/StardewAI.Companion.Mod.dll', 'Mods/StardewAI.Companion.Mod/docs/agent-ts-instructions.md',
        'Mods/StardewAI.Companion.Mod/docs/agent-guidance/execution.md')) {
        if (!$entries.ContainsKey($required)) { throw "Missing release file: $required" }
    }
    $reader = [IO.StreamReader]::new((Open-ReleaseEntry $entries['release-manifest.json']))
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.protocolVersion -ne 2 -or $manifest.files.Count -ne $entries.Count - 1) { throw 'Invalid release manifest.' }
    $verified = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if ($file.path -eq 'release-manifest.json' -or !$verified.Add($file.path) -or !$entries.ContainsKey($file.path)) {
            throw "Unexpected manifest entry: $($file.path)"
        }
        $stream = Open-ReleaseEntry $entries[$file.path]
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($hash -ne $file.sha256) { throw "Checksum mismatch: $($file.path)" }
    }
    "Verified $($entries.Count) $source files and SHA256 manifest."
} finally { if ($zip) { $zip.Dispose() } }
