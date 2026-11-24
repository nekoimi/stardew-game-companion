using System.Text.Json;
using StardewAI.Companion.Mod.Menus;
using StardewModdingAPI;
using StardewValley;

namespace StardewAI.Companion.Mod;

public sealed partial class ModEntry
{
    private string _savedMode = "companion";
    private static readonly JsonSerializerOptions ConfigJson = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    private ModConfig ReadModConfig() => _desktopConfigPath is { } path
        ? (File.Exists(path) ? JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path), ConfigJson) ?? new() : new())
        : Helper.ReadConfig<ModConfig>();

    private void WriteModConfig(ModConfig config)
    {
        if (_desktopConfigPath is not { } path)
        {
            Helper.WriteConfig(config);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(config, ConfigJson));
        File.Move(pending, path, overwrite: true);
    }

    private void OpenGameSettings()
    {
        Action back = () => { if (Context.IsWorldReady) OpenCompanionHub(2); else Game1.exitActiveMenu(); };
        string mode = _savedMode == "player" ? "AI 自己玩" : "伙伴模式";
        string message = $"游戏模式：{mode}" + (_savedMode != _config.Mode ? "（保存并重启游戏后生效）" : "") +
            $"\n{GetBridgeConnectionProblem() ?? "伙伴服务已连接。"}\nF8 随时指导。自玩模式按 {_config.TakeoverKey} 接管或交还。";
        var choices = new List<(string Label, Action Click)>();
        if (PlayerModeEnabled && _authority != null)
            choices.Add((_authority.AgentOwns ? "接管玩家" : "开始托管", () => {
                if (_authority.AgentOwns) ReleasePlayerControl("human-takeover");
                else { _playerControlApproved = true; _authority.RequestAgent("human-explicit-handoff"); }
                OpenGameSettings();
            }));
        choices.Add(("切换为" + (_savedMode == "player" ? "伙伴模式" : "AI 自己玩"), ChangeGameMode));
        choices.Add(("返回", back));
        Game1.activeClickableMenu = new CompanionOptionsMenu("游戏模式", message, back, choices.ToArray());
    }

    private void ChangeGameMode()
    {
        if (Context.IsMultiplayer && _savedMode != "player")
        {
            Game1.activeClickableMenu = new CompanionOptionsMenu("游戏模式", "AI 自己玩仅支持单人存档。", OpenGameSettings, ("返回", OpenGameSettings));
            return;
        }

        try
        {
            // Keep the running actor binding unchanged until the next process.
            var saved = ReadModConfig();
            saved.Mode = _savedMode == "player" ? "companion" : "player";
            WriteModConfig(saved);
            _savedMode = saved.Mode;
            OpenGameSettings();
        }
        catch (Exception ex)
        {
            Monitor.Log($"Save mode: {ex.Message}", LogLevel.Warn);
            Game1.activeClickableMenu = new CompanionOptionsMenu("设置未保存", "请检查 Mod 目录是否可写，再重试。", OpenGameSettings, ("返回", OpenGameSettings));
        }
    }
}
