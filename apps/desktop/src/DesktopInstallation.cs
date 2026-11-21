using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StardewAI.Companion.Desktop;

internal sealed record ClientPreferences(string Model = "", string Path = "", string Effort = "");
internal sealed record ApiModelSettings(string Provider = "deepseek", string Model = "deepseek-flash", string Reasoning = "");
internal sealed record DesktopSettings(string GameDirectory = "", string ActorMode = "player", string Model = "", string PiPath = "",
    string Backend = "pi", string ConnectionSource = "builtin", Dictionary<string, ClientPreferences>? Clients = null,
    string OriginalWorkspace = "", string OriginalDataDirectory = "", string OriginalClient = "", string OriginalName = "",
    ApiModelSettings? ApiModel = null);

internal static class DesktopInstallation
{
    internal static string SettingsFile(string root) => Path.Combine(root, "data", "desktop-settings.json");

    internal static DesktopSettings LoadSettings(string root)
    {
        var path = SettingsFile(root);
        return File.Exists(path) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path)) ?? new() : new();
    }

    internal static void SaveSettings(string root, DesktopSettings settings) => WriteJson(SettingsFile(root), settings);

    internal static string NodeRuntime(string root) => Path.Combine(root, "runtime", "node.exe");
    internal static bool UsesNode(string root) => File.Exists(Path.Combine(root, "runtime", "dist", "bootstrap.js"));

    internal static string ResolveRuntime(string root)
    {
        if (UsesNode(root))
            return File.Exists(NodeRuntime(root)) ? NodeRuntime(root)
                : throw new FileNotFoundException("缺少随包 Node 运行时，请完整解压绿色包。");
        var mod = ModDirectory(root);
        var python = Path.Combine(mod, "runtime", "python", "python.exe");
        if (!File.Exists(python) || !File.Exists(Path.Combine(mod, "release-manifest.json")))
            throw new FileNotFoundException("发行包文件不完整，请完整解压 ZIP 后再打开程序。");
        return python;
    }

    internal static void ConfigureGame(string root, DesktopSettings settings)
    {
        var configPath = Path.Combine(ModDirectory(root), "config.json");
        var config = File.Exists(configPath) ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() : new JsonObject();
        if (config is null) throw new InvalidDataException("伙伴设置文件无效。");
        config["Mode"] = settings.ActorMode;
        config["ConnectionSource"] = UsesNode(root) ? "builtin" : settings.ConnectionSource;
        config["ConnectionSourceChosen"] = true;
        WriteJson(configPath, config);
        SaveSettings(root, settings);
    }

    private static void WriteJson<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, path, overwrite: true);
    }

    internal static string ModDirectory(string root) => Path.Combine(root, "Mods", "StardewAI.Companion.Mod");

    internal static bool ImportPending(string root) => Directory.Exists(Path.Combine(ModDirectory(root), ".desktop-import-pending"));

    internal static async Task<JsonElement> ImportAsync(string root, string? source, bool dataOnly)
    {
        if (DesktopProcesses.AnyGameRunning()) throw new InvalidOperationException("请先在游戏中保存并退出，再导入旧数据。");
        string target = ModDirectory(root);
        string python = Path.Combine(target, "runtime", "python", "python.exe");
        if (!File.Exists(python)) throw new FileNotFoundException("绿色包不完整，请完整解压后再导入。");
        using var lease = AcquirePackageLease(root);
        string? sourceRoot = source is null ? null : Directory.GetParent(source)?.Parent?.FullName;
        using var sourceLease = sourceRoot is not null && File.Exists(Path.Combine(sourceRoot, "星露谷游戏伴侣.exe"))
            ? AcquirePackageLease(sourceRoot) : null;
        if (DesktopProcesses.AnyGameRunning()) throw new InvalidOperationException("游戏正在运行，请保存并退出后重试。");

        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = target, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-B", "-m", "stardew_ai_runtime.desktop_import", "--target", target })
            start.ArgumentList.Add(argument);
        if (source is not null)
        {
            start.ArgumentList.Add("--source");
            start.ArgumentList.Add(source);
        }
        if (dataOnly) start.ArgumentList.Add("--data-only");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动数据导入。");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var json = await output;
        var stderr = await errors;
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException($"数据导入进程未确认结果。{stderr.Trim()}");
        using var result = JsonDocument.Parse(json);
        if (result.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        if (process.ExitCode != 0) throw new InvalidOperationException("数据导入进程未确认结果。");
        return result.RootElement.Clone();
    }

    internal static FileStream AcquirePackageLease(string root)
    {
        var directory = Path.Combine(root, "data");
        Directory.CreateDirectory(directory);
        try { return File.Open(Path.Combine(directory, "desktop.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("所选绿色包已在运行，请退出后重试。", ex); }
    }

    public static bool IsGameDirectory(string? path) => !string.IsNullOrWhiteSpace(path)
        && File.Exists(Path.Combine(path, "StardewModdingAPI.exe"))
        && File.Exists(Path.Combine(path, "Stardew Valley.dll"));

    public static string DetectGame()
    {
        var configured = Environment.GetEnvironmentVariable("STARDEW_GAME_PATH");
        if (IsGameDirectory(configured)) return configured!;
        const string defaultSteam = @"C:\Program Files (x86)\Steam";
        var steam = (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam") is { } key
            ? ReadSteamPath(key) : null) ?? defaultSteam;
        var libraries = new List<string> { steam };
        try
        {
            var libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
                foreach (Match match in Regex.Matches(File.ReadAllText(libraryFile), "\"path\"\\s*\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal));
        }
        catch (IOException) { }
        return libraries.Select(root => Path.Combine(root, "steamapps", "common", "Stardew Valley"))
            .FirstOrDefault(IsGameDirectory) ?? "";
    }

    private static string? ReadSteamPath(RegistryKey key)
    {
        using (key) return key.GetValue("SteamPath") as string;
    }
}
