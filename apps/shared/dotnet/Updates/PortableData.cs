using System.IO;

namespace StardewAI.Companion.Updates;

internal sealed record DataLocation(string Directory);
internal sealed record DataProfile(int Version, string CredentialRoot);

internal sealed class PortableData
{
    private const string LocationFile = "data-location.json";
    private const string ProfileFile = "profile.json";
    private const string Mod = "Mods/StardewAI.Companion.Mod";

    private PortableData(string root, string? external, string credentialRoot)
    {
        Root = root;
        External = external;
        CredentialRoot = credentialRoot;
    }

    internal string Root { get; }
    internal string? External { get; }
    internal string CredentialRoot { get; }
    internal string Data => UpdatePackage.SafePath(External ?? Root, "data");
    internal string Plugins => UpdatePackage.SafePath(External ?? Root, "plugins");
    internal string ModData => External is null ? UpdatePackage.SafePath(Root, $"{Mod}/data")
        : UpdatePackage.SafePath(External, "mod/data");
    internal string ModConfig => External is null ? UpdatePackage.SafePath(Root, $"{Mod}/config.json")
        : UpdatePackage.SafePath(External, "mod/config.json");

    internal static PortableData Open(string root)
    {
        root = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        string location = UpdatePackage.SafePath(root, LocationFile);
        return File.Exists(location)
            ? OpenExternal(root, UpdatePackage.ReadJson<DataLocation>(location, 16 * 1024).Directory)
            : new(root, null, root);
    }

    private static PortableData OpenExternal(string root, string directory)
    {
        directory = ValidateExternal(root, directory);
        string metadata = UpdatePackage.SafePath(directory, ProfileFile);
        if (!File.Exists(metadata))
            throw new InvalidDataException("数据目录不可用，请恢复原目录后重试。");
        var profile = UpdatePackage.ReadJson<DataProfile>(metadata, 16 * 1024);
        if (profile.Version != 1 || profile.CredentialRoot is null || !Path.IsPathFullyQualified(profile.CredentialRoot))
            throw new InvalidDataException("数据目录信息无效。");
        return new(root, directory, profile.CredentialRoot);
    }

    internal FileStream? AcquireExternalLease() => External is null ? null
        : File.Open(UpdatePackage.SafePath(External, "profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    internal FileStream AcquireSessionLease()
    {
        Directory.CreateDirectory(Data);
        return File.Open(UpdatePackage.SafePath(Data, "desktop.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal FileStream AcquirePluginLease()
    {
        Directory.CreateDirectory(Data);
        return File.Open(UpdatePackage.SafePath(Data, "plugin-host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal static void Link(string root, string directory)
    {
        var profile = OpenExternal(root, directory);
        using var lease = profile.AcquireExternalLease();
        UpdateTransaction.WriteJson(UpdatePackage.SafePath(root, LocationFile), new DataLocation(profile.External!));
    }

    internal static void CreateFrom(PortableData source, string target)
    {
        target = ValidateExternal(source.Root, target);
        if (source.External is { } external && Overlaps(external, target))
            throw new InvalidOperationException("新数据目录须独立于当前数据目录。");
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("目标数据目录已存在，请选择新的位置。");
        string stage = $"{target}.tmp-{Guid.NewGuid():N}";
        try
        {
            source.Backup(stage, profileLayout: true);
            UpdateTransaction.WriteJson(UpdatePackage.SafePath(stage, ProfileFile), new DataProfile(1, source.CredentialRoot));
            Directory.Move(stage, target);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    internal void Backup(string destination, bool profileLayout = false)
    {
        Directory.CreateDirectory(destination);
        CopyTree(Data, UpdatePackage.SafePath(destination, "data"));
        CopyTree(Plugins, UpdatePackage.SafePath(destination, "plugins"));
        string mod = !profileLayout && External is null ? Mod : "mod";
        CopyTree(ModData, UpdatePackage.SafePath(destination, $"{mod}/data"));
        if (File.Exists(ModConfig))
            UpdateTransaction.CopyFile(ModConfig, UpdatePackage.SafePath(destination, $"{mod}/config.json"));
    }

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            string name = Path.GetFileName(path);
            if (name is "desktop.lock" or "plugin-host.lock" or "transport-discovery.json") continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("用户数据目录包含链接，无法建立完整副本。");
            string target = Path.Combine(destination, name);
            if ((attributes & FileAttributes.Directory) != 0) CopyTree(path, target);
            else UpdateTransaction.CopyFile(path, target);
        }
    }

    private static string ValidateExternal(string root, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidDataException("数据目录须使用绝对路径。");
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Overlaps(root, directory))
            throw new InvalidOperationException("数据目录须独立于绿色包目录。");
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            UpdatePackage.RejectLinks(parent.FullName);
        return directory;
    }

    private static bool Overlaps(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)) + Path.DirectorySeparatorChar;
        second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)) + Path.DirectorySeparatorChar;
        return first.StartsWith(second, StringComparison.OrdinalIgnoreCase) || second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
    }
}
