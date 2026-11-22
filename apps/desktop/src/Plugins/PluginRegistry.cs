using System.IO;
using System.Text.Json;

namespace StardewAI.Companion.Desktop.Plugins;

internal sealed record PluginBackup(PluginManifest Manifest, int DataVersion, string Snapshot, bool HadData);
internal sealed record InstalledPlugin(PluginManifest Manifest, bool Enabled, bool AutoStart,
    string? PublisherKey = null, int DataVersion = 1, PluginBackup? Backup = null);

internal sealed class PluginRegistry
{
    private readonly string _packages;
    private readonly string _data;
    private readonly string _registryPath;
    private readonly string _snapshotRoot;
    private readonly string _updatePath;
    private volatile Dictionary<string, InstalledPlugin> _entries;

    internal PluginRegistry(string root)
    {
        _packages = Path.Combine(root, "plugins");
        _data = Path.Combine(root, "data", "plugins");
        _registryPath = Path.Combine(root, "data", "plugin-registry.json");
        _snapshotRoot = Path.Combine(root, "data", "plugin-backups");
        _updatePath = Path.Combine(_snapshotRoot, "transaction.json");
        _entries = File.Exists(_registryPath)
            ? JsonSerializer.Deserialize<Dictionary<string, InstalledPlugin>>(File.ReadAllText(_registryPath), PluginPackages.Json)
                ?? throw new InvalidDataException("插件安装记录损坏。")
            : new();
        foreach (var (id, entry) in _entries)
        {
            if (!PluginPackages.ValidId(id) || entry?.Manifest?.Id != id || !PluginPackages.ValidVersion(entry.Manifest.Version)
                || entry.Backup is { } backup && (backup.Manifest.Id != id || !PluginPackages.ValidVersion(backup.Manifest.Version)
                    || !Guid.TryParseExact(backup.Snapshot, "N", out _)))
                throw new InvalidDataException("插件安装记录损坏。");
        }
        if (File.Exists(_updatePath)) RecoverUpdate();
    }

    internal InstalledPlugin[] List() => _entries.Values.OrderBy(plugin => plugin.Manifest.Name, StringComparer.CurrentCulture).ToArray();
    internal InstalledPlugin Find(string id) => _entries.TryGetValue(id, out var plugin)
        ? plugin : throw new InvalidOperationException("插件未安装。");
    internal string DataDirectory(string id) => Path.Combine(_data, id);
    internal string ProgramDirectory(InstalledPlugin plugin) => Path.Combine(_packages, plugin.Manifest.Id, plugin.Manifest.Version);

    internal async Task<InstalledPlugin?> InstallAsync(string zip, Func<PluginManifest, Task<bool>> confirm, CancellationToken token,
        CatalogEntry? online = null, string? publisherKey = null)
    {
        string staging = Path.Combine(_packages, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await PluginPackages.ExtractAsync(zip, staging, token).ConfigureAwait(false);
            var manifest = await PluginPackages.ValidateAsync(staging, token).ConfigureAwait(false);
            if (online is not null) ValidateCatalogManifest(online, manifest);
            if (_entries.ContainsKey(manifest.Id)) throw new InvalidOperationException("此插件已安装。");
            if (!await confirm(manifest).ConfigureAwait(false)) return null;
            var plugin = new InstalledPlugin(manifest, false, false, publisherKey, online?.DataVersion ?? 1);
            string destination = ProgramDirectory(plugin);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                // Recover an interruption between moving the package and publishing its registration.
                var orphan = Path.Combine(_packages, ".staging", Guid.NewGuid().ToString("N"));
                Directory.Move(destination, orphan);
                DeleteStaging(orphan);
            }
            Directory.Move(staging, destination);
            try { Replace(manifest.Id, plugin); }
            catch
            {
                Directory.Move(destination, staging);
                throw;
            }
            return plugin;
        }
        finally { DeleteStaging(staging); }
    }

    internal void SetPreferences(string id, bool enabled, bool autoStart)
        => Replace(id, Find(id) with { Enabled = enabled, AutoStart = enabled && autoStart });

    internal async Task<InstalledPlugin> StageUpdateAsync(string zip, CatalogEntry entry, string publisherKey, CancellationToken token)
    {
        var previous = Find(entry.Id);
        if (previous.Manifest.Publisher != entry.Publisher || previous.PublisherKey is not null && previous.PublisherKey != publisherKey)
            throw new InvalidOperationException("插件更新的发布者身份与已安装版本不一致。");
        if (CompareVersions(entry.Version, previous.Manifest.Version) <= 0 || entry.DataVersion < previous.DataVersion)
            throw new InvalidOperationException("请选择较新且数据版本兼容的插件。");
        string staging = Path.Combine(_packages, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await PluginPackages.ExtractAsync(zip, staging, token).ConfigureAwait(false);
            var manifest = await PluginPackages.ValidateAsync(staging, token).ConfigureAwait(false);
            ValidateCatalogManifest(entry, manifest);
            var next = previous with { Manifest = manifest, PublisherKey = publisherKey, DataVersion = entry.DataVersion, Backup = null };
            string destination = ProgramDirectory(next);
            token.ThrowIfCancellationRequested();
            // A failed earlier attempt can leave a verified but unregistered copy of this version.
            DeleteStaging(destination);
            Directory.Move(staging, destination);
            return next;
        }
        finally { DeleteStaging(staging); }
    }

    internal PluginUpdate BeginUpdate(string id)
    {
        if (File.Exists(_updatePath)) throw new InvalidOperationException("上一次插件更新尚待恢复，请重新打开伴侣程序。");
        var previous = Find(id);
        string snapshot = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(_snapshotRoot);
        string destination = SnapshotDirectory(snapshot);
        string data = DataDirectory(id);
        bool hadData = Directory.Exists(data);
        try
        {
            if (hadData) CopyData(data, destination);
            var transaction = new PluginUpdate(previous, snapshot, hadData);
            WriteAtomic(_updatePath, transaction);
            return transaction;
        }
        catch
        {
            DeleteStaging(destination);
            throw;
        }
    }

    internal void UseVersion(InstalledPlugin next) => Replace(next.Manifest.Id, next);

    internal void CommitUpdate(InstalledPlugin next, PluginUpdate transaction, bool savePrevious)
    {
        var old = transaction.Previous;
        var backup = savePrevious ? new PluginBackup(old.Manifest, old.DataVersion, transaction.Snapshot, transaction.HadData) : null;
        Replace(next.Manifest.Id, next with { Backup = backup });
        File.Delete(_updatePath);
        if (old.Backup is { } retired && retired.Manifest.Version != next.Manifest.Version)
            DeleteStaging(ProgramDirectory(old with { Manifest = retired.Manifest }));
        if (old.Backup is { } snapshot) DeleteStaging(SnapshotDirectory(snapshot.Snapshot));
        if (!savePrevious)
        {
            DeleteStaging(SnapshotDirectory(transaction.Snapshot));
            DeleteStaging(ProgramDirectory(old));
        }
    }

    internal void RestoreBackupData(string id, PluginBackup backup)
        => RestoreData(id, backup.Snapshot, backup.HadData);

    internal void RecoverUpdate()
    {
        var record = JsonSerializer.Deserialize<PluginUpdate>(File.ReadAllText(_updatePath), PluginPackages.Json);
        if (record is null || !Guid.TryParseExact(record.Snapshot, "N", out _)
            || record.Previous?.Manifest is not { } previous || !PluginPackages.ValidId(previous.Id)
            || !PluginPackages.ValidVersion(previous.Version))
            throw new InvalidDataException("插件更新恢复记录损坏。");
        var old = record.Previous;
        RestoreData(old.Manifest.Id, record.Snapshot, record.HadData);
        Replace(old.Manifest.Id, old);
        File.Delete(_updatePath);
        DeleteStaging(SnapshotDirectory(record.Snapshot));
    }

    private void RestoreData(string id, string snapshot, bool existed)
    {
        var target = DataDirectory(id);
        var staged = target + ".restore-" + Guid.NewGuid().ToString("N");
        var retired = target + ".replaced-" + Guid.NewGuid().ToString("N");
        try
        {
            if (existed) CopyData(SnapshotDirectory(snapshot), staged);
            if (Directory.Exists(target)) Directory.Move(target, retired);
            try { if (existed) Directory.Move(staged, target); }
            catch
            {
                if (Directory.Exists(retired)) Directory.Move(retired, target);
                throw;
            }
            DeleteStaging(retired);
        }
        finally { DeleteStaging(staged); }
    }

    private static void CopyData(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("插件数据包含链接，无法建立更新备份。");
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("插件数据包含链接，无法建立更新备份。");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyData(entry, target);
            else File.Copy(entry, target, overwrite: false);
        }
    }

    private string SnapshotDirectory(string snapshot) => Path.Combine(_snapshotRoot, snapshot);

    private static void ValidateCatalogManifest(CatalogEntry entry, PluginManifest manifest)
    {
        if (manifest.Id != entry.Id || manifest.Name != entry.Name || manifest.Version != entry.Version
            || manifest.Publisher != entry.Publisher || manifest.HostProtocol != entry.HostProtocol)
            throw new InvalidDataException("插件包清单与签名目录不一致。");
    }

    private static int CompareVersions(string left, string right)
    {
        var l = left.Split('-', 2);
        var r = right.Split('-', 2);
        var numbers = l[0].Split('.').Zip(r[0].Split('.'));
        foreach (var (a, b) in numbers)
        {
            int comparison = a.Length.CompareTo(b.Length);
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(a, b);
            if (comparison != 0) return comparison;
        }
        if (l.Length != r.Length) return r.Length.CompareTo(l.Length);
        if (l.Length == 1) return 0;
        var leftParts = l[1].Split('.');
        var rightParts = r[1].Split('.');
        foreach (var (a, b) in leftParts.Zip(rightParts))
        {
            bool numericA = a.All(char.IsAsciiDigit);
            bool numericB = b.All(char.IsAsciiDigit);
            int comparison = numericA && numericB ? a.Length.CompareTo(b.Length)
                : numericA != numericB ? (numericA ? -1 : 1) : 0;
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(a, b);
            if (comparison != 0) return comparison;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    internal void Uninstall(string id, bool keepData)
    {
        var plugin = Find(id);
        string directory = Path.GetDirectoryName(ProgramDirectory(plugin))!;
        string data = DataDirectory(id);
        string quarantinedPackage = directory + ".uninstall-" + Guid.NewGuid().ToString("N");
        string quarantinedData = data + ".uninstall-" + Guid.NewGuid().ToString("N");
        bool movedPackage = false;
        bool movedData = false;
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Move(directory, quarantinedPackage);
                movedPackage = true;
            }
            if (!keepData && Directory.Exists(data))
            {
                Directory.Move(data, quarantinedData);
                movedData = true;
            }
            Replace(id, null);
        }
        catch
        {
            if (movedPackage) Directory.Move(quarantinedPackage, directory);
            if (movedData) Directory.Move(quarantinedData, data);
            throw;
        }
        DeleteStaging(quarantinedPackage);
        DeleteStaging(quarantinedData);
        if (plugin.Backup is { } backup) DeleteStaging(SnapshotDirectory(backup.Snapshot));
    }

    private void Replace(string id, InstalledPlugin? entry)
    {
        var copy = new Dictionary<string, InstalledPlugin>(_entries);
        if (entry is null) copy.Remove(id);
        else copy[id] = entry;
        WriteAtomic(_registryPath, copy);
        _entries = copy;
    }

    internal static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + ".tmp";
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, PluginPackages.Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, path, overwrite: true);
    }

    private void DeleteStaging(string path)
    {
        var target = Path.GetFullPath(path);
        if (!new[] { _packages, _data, _snapshotRoot }.Any(root =>
            target.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("插件清理路径越界。");
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed record PluginUpdate(InstalledPlugin Previous, string Snapshot, bool HadData);
