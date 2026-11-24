using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using StardewAI.Companion.Updates;

namespace StardewAI.Companion.Desktop;

internal sealed record ApiModelSettings(string Provider = "deepseek", string Model = "deepseek-flash",
    string Reasoning = "", string BaseUrl = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Protocol = null, bool Stream = true)
{
    internal const string CustomProvider = "custom";
    private const string LegacyProvider = "openai-compatible";
    internal bool IsCustom => Provider == CustomProvider;
    // Keep the original target for saved gateway credentials. The same endpoint
    // may expose both OpenAI protocols and has one credential.
    internal string CredentialScope => IsCustom ? $"{LegacyProvider}/{BaseUrl.Trim().TrimEnd('/')}" : Provider;

    internal ApiModelSettings Upgrade() => Provider == LegacyProvider
        ? this with { Provider = CustomProvider, Protocol = "responses" } : this;
}
internal sealed record DesktopSettings(string GameDirectory = "", string ActorMode = "player",
    ApiModelSettings? ApiModel = null, ApiModelSettings? CustomModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApiModelSettings? CompatibleResponses = null,
    bool CheckPreviewUpdates = false);

internal static class DesktopInstallation
{
    internal static string SettingsFile(string root) => Path.Combine(PortableData.Open(root).Data, "desktop-settings.json");
    internal static string LogDirectory(string root) => Path.Combine(PortableData.Open(root).Data, "logs");

    internal static DesktopSettings LoadSettings(string root)
    {
        var path = SettingsFile(root);
        var settings = File.Exists(path) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path)) ?? new() : new();
        return settings with { ApiModel = settings.ApiModel?.Upgrade(),
            CustomModel = (settings.CustomModel ?? settings.CompatibleResponses)?.Upgrade(), CompatibleResponses = null };
    }

    internal static void SaveSettings(string root, DesktopSettings settings) => WriteJson(SettingsFile(root), settings);

    internal static string ResolveRuntime(string root)
    {
        string node = Path.Combine(root, "runtime", "node.exe");
        if (!File.Exists(node) || !File.Exists(Path.Combine(root, "runtime", "dist", "bootstrap.js")))
            throw new FileNotFoundException("发行包文件不完整，请完整解压 ZIP 后再打开程序。");
        return node;
    }

    internal static void ConfigureGame(string root, DesktopSettings settings)
    {
        var configPath = PortableData.Open(root).ModConfig;
        var config = File.Exists(configPath) ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() : new JsonObject();
        if (config is null) throw new InvalidDataException("伙伴设置文件无效。");
        config["Mode"] = settings.ActorMode;
        WriteJson(configPath, config);
    }

    private static void WriteJson<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, path, overwrite: true);
    }

    internal static string ModDirectory(string root) => Path.Combine(root, "Mods", "StardewAI.Companion.Mod");

    internal static FileStream AcquirePackageLease(string root)
    {
        var directory = PortableData.Open(root).Data;
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
