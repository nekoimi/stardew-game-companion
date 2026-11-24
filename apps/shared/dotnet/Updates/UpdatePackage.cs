using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StardewAI.Companion.Updates;

internal sealed record ReleaseInfo(string Tag, string Version, string Channel, int MinimumUpdaterProtocol,
    DateTimeOffset? BuiltAt = null, int DataProfileVersion = 0);
internal sealed record ReleaseFile(string Path, string Sha256);
internal sealed record ReleaseManifest(int ProtocolVersion, ReleaseFile[] Files);

internal static class UpdatePackage
{
    internal const string Launcher = "星露谷游戏伴侣.exe";
    internal const string Helper = "tools/StardewGameCompanion.Updater.exe";
    internal const string Manifest = "release-manifest.json";
    internal const string Info = "release-info.json";
    internal const int UpdaterProtocol = 1;
    internal const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxFiles = 50_000;
    private static readonly string[] Required = [
        Launcher, Helper, Info, "runtime/node.exe", "runtime/models.json", "runtime/dist/bootstrap.js",
        "Mods/StardewAI.Companion.Mod/manifest.json", "Mods/StardewAI.Companion.Mod/StardewAI.Companion.Mod.dll",
        "Mods/StardewAI.Companion.Mod/docs/agent-ts-instructions.md", "docs/LICENSE", "docs/NOTICE",
    ];
    private static readonly Regex Allowed = new(
        @"\A(星露谷游戏伴侣\.exe|tools/StardewGameCompanion\.Updater\.exe|release-(manifest|info)\.json|docs/(LICENSE|NOTICE|CHANGELOG\.md|desktop-portable\.md|DotNet-(LICENSE|ThirdPartyNotices)\.txt)|runtime/(node\.exe|models\.json|package(-lock)?\.json|licenses/(node/LICENSE|npm\.json)|(dist|node_modules)/.+)|Mods/StardewAI\.Companion\.Mod/(manifest\.json|StardewAI\.Companion\.Mod\.(dll|deps\.json)|docs/(agent-ts-instructions\.md|agent-guidance/[a-z-]+\.md)))\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex Excluded = new(
        @"(^|/)(\.git|\.env)(/|\.|$)|\.(py|pyc|pdb|sqlite|pem|key)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DeviceName = new(
        @"\A(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool ValidHash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);

    internal static string ManifestHash(string root)
    {
        using var input = File.OpenRead(SafePath(root, Manifest));
        return Convert.ToHexString(SHA256.HashData(input));
    }

    internal static bool FileHasHash(string path, string hash)
    {
        using var input = File.OpenRead(path);
        return HasHash(input, hash);
    }

    internal static ReleaseInfo? ReadInfo(string root)
    {
        var path = SafePath(root, Info);
        if (!File.Exists(path)) return null;
        var info = ReadJson<ReleaseInfo>(path, 16 * 1024);
        if (info.Tag is not { Length: > 0 and <= 200 } || info.Tag.Any(char.IsControl)
            || info.Version is not { Length: > 0 and <= 200 }
            || info.Channel is not ("stable" or "preview") || info.MinimumUpdaterProtocol < 1)
            throw new InvalidDataException("发行版本信息无效。");
        return info;
    }

    internal static T ReadJson<T>(string path, int limit = 16 * 1024 * 1024)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException("更新元数据超出大小限制。");
        return JsonSerializer.Deserialize<T>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("更新元数据为空。");
    }

    internal static Dictionary<string, ReleaseFile> ReadManifest(string root, bool currentPackage)
    {
        var manifest = ReadJson<ReleaseManifest>(SafePath(root, Manifest));
        if (manifest.ProtocolVersion != 2 || manifest.Files is not { Length: > 0 and <= MaxFiles })
            throw new InvalidDataException("发行清单版本或文件数量无效。");
        var files = new Dictionary<string, ReleaseFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || !ValidHash(file.Sha256) || file.Path == Manifest
                || !AllowedFile(file.Path) || !files.TryAdd(file.Path, file))
                throw new InvalidDataException("发行清单包含无效或重复的文件。");
        }
        if (!files.ContainsKey(Launcher) || (currentPackage && Required.Any(path => !files.ContainsKey(path))))
            throw new InvalidDataException("发行包缺少必要的程序文件。");
        return files;
    }

    internal static string ReadChecksum(string path, string archiveName)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 4096) throw new InvalidDataException("SHA256 校验文件无效。");
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd().Trim();
        if (text.Length <= 66 || !ValidHash(text[..64]) || text[64..66] != "  "
            || !string.Equals(text[66..], archiveName, StringComparison.Ordinal))
            throw new InvalidDataException("SHA256 校验文件与所选 ZIP 不匹配。");
        return text[..64];
    }

    internal static ReleaseInfo Extract(string archive, string expectedHash, string payload, CancellationToken token)
    {
        using var stream = File.OpenRead(archive);
        if (stream.Length > MaxArchiveBytes || !ValidHash(expectedHash) || !HasHash(stream, expectedHash))
            throw new InvalidDataException("ZIP 的 SHA256 校验失败。");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxFiles) throw new InvalidDataException("ZIP 文件数量超出限制。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long remaining = MaxExpandedBytes;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = entry.FullName;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || ((entry.ExternalAttributes >> 16) & 0xF000) is not (0 or 0x8000 or 0x4000))
                throw new InvalidDataException("ZIP 包含链接或特殊文件。");
            if (path.EndsWith('/'))
            {
                if (!ValidRelativePath(path[..^1]) || entry.Length != 0)
                    throw new InvalidDataException("ZIP 目录路径无效。");
                continue;
            }
            if (!AllowedFile(path) || !paths.Add(path) || entry.Length > remaining)
                throw new InvalidDataException("ZIP 包含无效路径、重复文件或超出大小限制。");
            remaining -= entry.Length;
            string target = SafePath(payload, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[64 * 1024];
            long length = 0;
            for (int read; (read = input.Read(buffer)) > 0;)
            {
                token.ThrowIfCancellationRequested();
                length += read;
                if (length > entry.Length) throw new InvalidDataException("ZIP 解压长度无效。");
                output.Write(buffer, 0, read);
            }
            if (length != entry.Length) throw new InvalidDataException("ZIP 解压长度无效。");
        }
        var files = VerifyPayload(payload, token);
        if (paths.Count != files.Count + 1 || !paths.Contains(Manifest))
            throw new InvalidDataException("ZIP 内容与发行清单不一致。");
        return ReadInfo(payload)!;
    }

    internal static Dictionary<string, ReleaseFile> VerifyPayload(string payload, CancellationToken token)
    {
        var files = ReadManifest(payload, currentPackage: true);
        foreach (var file in files.Values)
        {
            token.ThrowIfCancellationRequested();
            using var input = File.OpenRead(SafePath(payload, file.Path));
            if (!HasHash(input, file.Sha256))
                throw new InvalidDataException($"发行文件校验失败（{file.Path}）。");
        }
        if (ReadInfo(payload) is not { MinimumUpdaterProtocol: <= UpdaterProtocol })
            throw new InvalidDataException("此发行包需要更高版本的更新器。");
        return files;
    }

    internal static bool HasHash(Stream stream, string expected) =>
        Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);

    internal static bool AllowedFile(string? path) =>
        path is not null && ValidRelativePath(path) && Allowed.IsMatch(path) && !Excluded.IsMatch(path);

    private static bool ValidRelativePath(string path) => path is { Length: > 0 and < 1024 }
        && path.Split('/').All(part => part.Length > 0 && part is not ("." or "..")
            && !part.EndsWith('.') && !part.EndsWith(' ')
            && !part.Any(character => char.IsControl(character) || character is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*')
            && !DeviceName.IsMatch(part.Split('.')[0].TrimEnd()));

    internal static string SafePath(string root, string relative)
    {
        if (!ValidRelativePath(relative)) throw new InvalidDataException("更新文件路径无效。");
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        RejectLinks(path);
        foreach (var part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            RejectLinks(path);
        }
        return path;
    }

    internal static void RejectLinks(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新路径包含链接或目录联接。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
