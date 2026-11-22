using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StardewAI.Companion.Desktop.Plugins;

internal sealed record PluginProtocol(int Min, int Max);
internal sealed record PluginEntry(string Executable, string[] Args, string WorkingDirectory);
internal sealed record PluginSettings(string Kind);
internal sealed record PluginManifest(int ManifestVersion, string Id, string Name, string Publisher,
    string Version, string Platform, PluginProtocol HostProtocol, PluginEntry Entry, PluginSettings? Settings,
    string FileManifest);

internal static partial class PluginPackages
{
    internal const int ProtocolVersion = 1;
    private const long MaxBytes = 512L * 1024 * 1024;
    private const int MaxEntries = 8192;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    [GeneratedRegex(@"\A[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PluginId();
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[a-zA-Z0-9]+(?:[.-][a-zA-Z0-9]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumber();
    [GeneratedRegex(@"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³])\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();

    internal static async Task ExtractAsync(string archivePath, string staging, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("插件包项目数超出限制。");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string relative = entry.FullName;
            bool directory = relative.EndsWith('/');
            if (directory) relative = relative[..^1];
            ValidatePath(relative);
            if (!seen.Add(relative) || (entry.ExternalAttributes & 0x400) != 0
                || (entry.ExternalAttributes & unchecked((int)0xf0000000)) == unchecked((int)0xa0000000))
                throw new InvalidDataException("插件包包含重复路径或链接。");

            var destination = Resolve(staging, relative);
            total = checked(total + entry.Length);
            if (total > MaxBytes || directory && entry.Length != 0)
                throw new InvalidDataException("插件包解压大小超出限制。");
            if (directory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            var buffer = new byte[81920];
            long remaining = entry.Length;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                remaining -= count;
                if (remaining < 0) throw new InvalidDataException("插件文件大小与 ZIP 清单不一致。");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (remaining != 0) throw new InvalidDataException("插件文件已截断。");
        }
    }

    internal static PluginManifest ReadManifest(string directory)
    {
        var path = Resolve(directory, "plugin.json");
        var manifest = Read<PluginManifest>(path, 64 * 1024);
        if (manifest.ManifestVersion != ProtocolVersion || manifest.HostProtocol is not { Min: >= 1 } protocol
            || protocol.Max < protocol.Min || protocol.Min > ProtocolVersion || protocol.Max < ProtocolVersion)
            throw new InvalidDataException("插件协议版本不兼容。");
        if (!ValidId(manifest.Id) || !ValidVersion(manifest.Version) || !ValidText(manifest.Name, 80)
            || !ValidText(manifest.Publisher, 120) || manifest.Platform != "win-x64"
            || manifest.FileManifest != "files.json" || manifest.Settings is { Kind: not "local-web" }
            || manifest.Entry is not { Args.Length: <= 64 } entry
            || entry.Args.Any(arg => arg is null || arg.Length > 2048 || arg.Contains('\0')))
            throw new InvalidDataException("插件清单内容无效。");
        ValidatePath(entry.Executable);
        if (!entry.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件入口须为 Windows EXE。");
        if (entry.WorkingDirectory != ".") ValidatePath(entry.WorkingDirectory);
        return manifest;
    }

    internal static async Task<PluginManifest> ValidateAsync(string directory, CancellationToken token)
    {
        var manifest = ReadManifest(directory);
        var fileManifest = Read<FileManifest>(Resolve(directory, manifest.FileManifest), 4 * 1024 * 1024);
        if (fileManifest.Files is not { Length: >= 2 and <= MaxEntries })
            throw new InvalidDataException("插件文件清单无效。");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in fileManifest.Files)
        {
            if (item is null) throw new InvalidDataException("插件文件清单无效。");
            ValidatePath(item.Path);
            if (item.Sha256 is null || !Sha256().IsMatch(item.Sha256) || !hashes.TryAdd(item.Path, item.Sha256))
                throw new InvalidDataException("插件文件哈希无效或重复。");
        }
        if (!hashes.ContainsKey("plugin.json") || !hashes.ContainsKey(manifest.Entry.Executable)
            || !File.Exists(Resolve(directory, manifest.Entry.Executable))
            || !Directory.Exists(Resolve(directory, manifest.Entry.WorkingDirectory)))
            throw new InvalidDataException("插件入口或工作目录缺失。");

        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var path in EnumeratePackage(directory))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            ValidatePath(relative);
            if (entries.Count >= MaxEntries || !entries.Add(relative))
                throw new InvalidDataException("插件包项目数超出限制或路径重复。");
            if (Directory.Exists(path)) continue;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous);
            total = checked(total + stream.Length);
            if (total > MaxBytes) throw new InvalidDataException("插件目录大小超出限制。");
            if (relative == manifest.FileManifest) continue;
            if (!hashes.Remove(relative, out string? hash)
                || !Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(hash, StringComparison.Ordinal))
                throw new InvalidDataException("插件文件缺少哈希或校验失败。");
        }
        if (hashes.Count != 0) throw new InvalidDataException("插件缺少清单列出的文件。");
        return manifest;
    }

    private static IEnumerable<string> EnumeratePackage(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("插件目录包含链接。");
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                var attr = File.GetAttributes(path);
                if (attr.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("插件目录包含链接。");
                if (attr.HasFlag(FileAttributes.Directory)) pending.Push(path);
                yield return path;
            }
        }
    }

    internal static string Resolve(string directory, string relative)
    {
        if (relative == ".") return directory;
        ValidatePath(relative);
        return Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 72 } && PluginId().IsMatch(id)
        && id.Split('.').All(part => !ReservedName().IsMatch(part));
    internal static bool ValidVersion(string? version) => version is { Length: > 0 and <= 64 } && VersionNumber().IsMatch(version);

    private static void ValidatePath(string? path)
    {
        if (path is not { Length: > 0 and <= 240 } || path.Contains('\\'))
            throw new InvalidDataException("插件路径无效。");
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => c < ' ' || ":*?\"<>|".Contains(c))
                || ReservedName().IsMatch(segment.Split('.', 2)[0].TrimEnd(' ')))
                throw new InvalidDataException("插件路径越界或包含无效字符。");
        }
    }

    private static T Read<T>(string path, long maximumSize)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("插件清单不能使用链接。");
        using var stream = File.OpenRead(path);
        if (stream.Length > maximumSize) throw new InvalidDataException("插件清单过大。");
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("插件清单无效。");
    }

    private static bool ValidText(string? text, int limit) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= limit && !text.Any(char.IsControl);

    private sealed record FileManifest(PackageFile[] Files);
    private sealed record PackageFile(string Path, string Sha256);
}
