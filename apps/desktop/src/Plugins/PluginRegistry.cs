using System.IO;
using System.Text.Json;

namespace StardewAI.Companion.Desktop.Plugins;

internal sealed record InstalledPlugin(PluginManifest Manifest, bool Enabled, bool AutoStart);

internal sealed class PluginRegistry
{
    private readonly string _packages;
    private readonly string _data;
    private readonly string _registryPath;
    private Dictionary<string, InstalledPlugin> _entries;

    internal PluginRegistry(string root)
    {
        _packages = Path.Combine(root, "plugins");
        _data = Path.Combine(root, "data", "plugins");
        _registryPath = Path.Combine(root, "data", "plugin-registry.json");
        _entries = File.Exists(_registryPath)
            ? JsonSerializer.Deserialize<Dictionary<string, InstalledPlugin>>(File.ReadAllText(_registryPath), PluginPackages.Json)
                ?? throw new InvalidDataException("插件安装记录损坏。")
            : new();
        foreach (var (id, entry) in _entries)
        {
            if (!PluginPackages.ValidId(id) || entry?.Manifest?.Id != id || !PluginPackages.ValidVersion(entry.Manifest.Version))
                throw new InvalidDataException("插件安装记录损坏。");
        }
    }

    internal InstalledPlugin[] List() => _entries.Values.OrderBy(plugin => plugin.Manifest.Name, StringComparer.CurrentCulture).ToArray();
    internal InstalledPlugin Find(string id) => _entries.TryGetValue(id, out var plugin)
        ? plugin : throw new InvalidOperationException("插件未安装。");
    internal string DataDirectory(string id) => Path.Combine(_data, id);
    internal string ProgramDirectory(InstalledPlugin plugin) => Path.Combine(_packages, plugin.Manifest.Id, plugin.Manifest.Version);

    internal async Task<InstalledPlugin?> InstallAsync(string zip, Func<PluginManifest, Task<bool>> confirm, CancellationToken token)
    {
        string staging = Path.Combine(_packages, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await PluginPackages.ExtractAsync(zip, staging, token).ConfigureAwait(false);
            var manifest = await PluginPackages.ValidateAsync(staging, token).ConfigureAwait(false);
            if (_entries.ContainsKey(manifest.Id)) throw new InvalidOperationException("此插件已安装。");
            if (!await confirm(manifest).ConfigureAwait(false)) return null;
            var plugin = new InstalledPlugin(manifest, false, false);
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

    internal void Uninstall(string id, bool keepData)
    {
        var plugin = Find(id);
        string directory = ProgramDirectory(plugin);
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
    }

    private void Replace(string id, InstalledPlugin? entry)
    {
        var copy = new Dictionary<string, InstalledPlugin>(_entries);
        if (entry is null) copy.Remove(id);
        else copy[id] = entry;
        Directory.CreateDirectory(_data);
        string pending = _registryPath + ".tmp";
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, copy, PluginPackages.Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, _registryPath, overwrite: true);
        _entries = copy;
    }

    private static void DeleteStaging(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
