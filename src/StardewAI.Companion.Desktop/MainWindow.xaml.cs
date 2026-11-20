using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace StardewAI.Companion.Desktop;

public sealed record TimelineEntry(string Label, string Text);
public sealed record DesktopDecision(string Id, string Title, IReadOnlyList<string> Choices);

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TimelineEntry> _entries = new();
    private readonly HashSet<string> _seenResults = new(StringComparer.Ordinal);
    private readonly string _root = AppContext.BaseDirectory;
    private DesktopSession? _session;
    private string? _gameSessionId;
    private string? _lastGameSessionId;
    private string? _owner;
    private bool _playerMode;
    private bool _paused;
    private bool _interrupting;
    private bool _recoveryPending;
    private bool _reconnecting;
    private bool _closing;
    private bool _closed;
    private string? _decisionSignature;

    public MainWindow()
    {
        InitializeComponent();
        Timeline.ItemsSource = _entries;
        InitializeSaveViews();
        DesktopSettings settings = new();
        var path = Path.Combine(_root, "data", "desktop-settings.json");
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path)) ?? new();
            GameFolder.Text = DesktopSession.IsGameDirectory(settings.GameDirectory)
                ? settings.GameDirectory : DesktopSession.DetectGame();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log($"读取桌面设置失败：{ex.Message}");
        }
        Mode.SelectedIndex = settings.ActorMode == "player" ? 1 : 0;
        InitializePartnerSettings(settings);
        if (string.IsNullOrWhiteSpace(GameFolder.Text)) Pages.SelectedItem = SettingsPage;
        RefreshControls();
    }

    private DesktopSettings Settings() => new(GameFolder.Text.Trim(), Mode.SelectedIndex == 1 ? "player" : "companion",
        Backend: _backend, ConnectionSource: SelectedSource, Clients: ReadClientPreferences(),
        OriginalWorkspace: OriginalWorkspace.Text.Trim(), OriginalDataDirectory: OriginalData.Text.Trim(),
        OriginalClient: OriginalClient.Text.Trim(), OriginalName: OriginalName.Text.Trim());

    private async void LaunchClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not null || _closing || _importing) return;
        if (SelectedSource == "builtin" && BackendPicker.SelectedItem is null)
        {
            Pages.SelectedItem = SettingsPage;
            ActivityText.Text = "请选择一个可用的模型客户端。";
            return;
        }
        var settings = Settings();
        if (!DesktopSession.IsGameDirectory(settings.GameDirectory))
        {
            Pages.SelectedItem = SettingsPage;
            ActivityText.Text = "请选择包含 StardewModdingAPI.exe 的游戏目录。";
            return;
        }
        var session = new DesktopSession(_root, Log);
        _session = session;
        _activeSource = settings.ConnectionSource;
        _partnerReady = false;
        session.EventReceived += (generation, message) => Dispatcher.BeginInvoke(() =>
        {
            if (_session == session && session.RuntimeGeneration == generation && !session.CanReconnect) ApplyEvent(message);
        });
        session.RuntimeExited += generation => Dispatcher.BeginInvoke(() =>
        {
            if (_session == session && session.RuntimeGeneration == generation && session.CanReconnect && !_closing)
                MarkRuntimeExited();
        });
        session.GameExited += () => Dispatcher.BeginInvoke(() => _ = EndSessionAsync(session, "游戏已退出，可以再次启动。"));
        Pages.SelectedItem = _activeSource == "external" ? SettingsPage : CommandPage;
        ConnectionText.Text = "正在检查运行组件";
        ActivityText.Text = "正在启动伴侣和游戏。";
        RefreshControls();
        try
        {
            await session.StartAsync(settings);
            if (_session == session && _gameSessionId is null)
            {
                ConnectionText.Text = "等待进入存档";
                ActivityText.Text = "请在游戏窗口中选择单人存档。";
            }
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                Log(ex.Message);
                await EndSessionAsync(session, ex.Message);
            }
        }
    }

    private void MarkRuntimeExited()
    {
        ApplyStatus(default);
        ConnectionText.Text = "伙伴服务已退出";
        ActivityText.Text = "游戏仍在运行。点击「重连伙伴服务」后核对旧动作。";
        AddEntry("连接中断", "上一轮回复或动作结果可能尚未确认。可在连接恢复后核对旧动作，或到历史中查看已保存的对话。");
    }

    private async void ReconnectClicked(object sender, RoutedEventArgs e)
    {
        if (_reconnecting || _closing || _session is not { CanReconnect: true } session) return;
        _reconnecting = true;
        ConnectionText.Text = "正在重连伙伴服务";
        ActivityText.Text = "等待当前游戏重新连接。";
        RefreshControls();
        try { await session.ReconnectAsync(); }
        catch (Exception ex)
        {
            if (_session == session && !_closing)
            {
                ConnectionText.Text = "重连伙伴服务失败";
                ActivityText.Text = ex.Message;
                Log(ex.Message);
            }
        }
        finally
        {
            _reconnecting = false;
            RefreshControls();
        }
    }

    private async Task EndSessionAsync(DesktopSession session, string message)
    {
        if (_session != session) return;
        _partnerServiceReady = false;
        _gameSessionId = null;
        ResetSaveViews();
        ApplyDecisions(default);
        RefreshControls();
        try { await session.StopAsync(); }
        catch (Exception ex) { Log($"停止伴侣服务失败：{ex.Message}"); }
        if (_session != session) return;
        _session = null;
        ResetPartnerConnection();
        ResetUsageView();
        ConnectionText.Text = "等待启动游戏";
        ActivityText.Text = message;
        RefreshControls();
    }

    private void BrowseGame(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择星露谷物语游戏目录" };
        if (Directory.Exists(GameFolder.Text)) picker.InitialDirectory = GameFolder.Text;
        if (picker.ShowDialog(this) == true) GameFolder.Text = picker.FolderName;
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            DesktopSession.WriteJson(Path.Combine(_root, "data", "desktop-settings.json"), Settings());
            ActivityText.Text = "设置已保存，下次启动生效。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityText.Text = "保存失败，请检查解压目录是否可写。";
            Log(ex.Message);
        }
    }

    private async void SendClicked(object sender, RoutedEventArgs e) => await SendAsync(false);
    private async void InterruptClicked(object sender, RoutedEventArgs e) => await SendAsync(true);

    private async void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && SendButton.IsEnabled)
        {
            e.Handled = true;
            await SendAsync(false);
        }
    }

    private async Task<bool> SendAsync(bool interrupt, string? selection = null, string? decisionId = null)
    {
        if (_session is not { } session || _gameSessionId is not { } game) return false;
        var text = selection ?? Input.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;
        int view = _saveViewGeneration;
        try
        {
            await session.PostAsync("/v1/commands", new {
                gameSessionId = game, requestId = Guid.NewGuid().ToString("N"), text,
                mode = interrupt ? "interrupt" : "send", decisionId,
            });
            if (view != _saveViewGeneration) return false;
            if (selection is null && Input.Text.Trim() == text) Input.Clear();
            return true;
        }
        catch (Exception ex)
        {
            if (view == _saveViewGeneration) AddEntry("发送失败", ex.Message);
            return false;
        }
    }

    private async void ControlClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action }) await ControlAsync(action);
    }

    private async void AuthorityClicked(object sender, RoutedEventArgs e) =>
        await ControlAsync(_owner == "agent" ? "takeover" : "handoff");

    private async Task ControlAsync(string action)
    {
        if (_session is not { } session || _gameSessionId is not { } game) return;
        int view = _saveViewGeneration;
        WorkFeedback.Text = "";
        try { await session.PostAsync("/v1/control", new { gameSessionId = game, action }); }
        catch (Exception ex)
        {
            if (view != _saveViewGeneration) return;
            WorkFeedback.Text = ex.Message;
            AddEntry("操作未确认", ex.Message);
        }
    }

    private void ApplyEvent(JsonElement message)
    {
        var payload = message.GetProperty("payload");
        switch (Text(message, "type"))
        {
            case "status": ApplyStatus(payload); break;
            case "submitted": AddEntry("你", Text(payload, "text")); break;
            case "command" when Text(payload, "status") == "interrupting":
                AddEntry("正在改派", "等待上一项动作停止并核对结果。"); break;
            case "life.care": AddEntry("提醒", Text(payload, "text")); break;
            case "chat.reply":
            case "life.chat.reply":
                var reply = Text(payload, "replyText");
                var error = Text(payload, "error");
                if (reply.Length > 0 || error.Length > 0)
                    AddEntry(error.Length > 0 ? "未完成" : "伴侣", reply.Length > 0 ? reply : error);
                break;
            case "result": ApplyResult(payload); break;
            case "recovery":
                AddEntry("连接已恢复", Flag(payload, "hasReceipt") ? "已核对旧动作的游戏回执，工作仍保持暂停。"
                    : "已核对游戏中没有待结算的旧动作，工作仍保持暂停。");
                break;
        }
    }

    private void ApplyStatus(JsonElement payload)
    {
        bool hadService = _partnerServiceReady;
        ApplyPartnerStatus(payload);
        var previous = _gameSessionId;
        _gameSessionId = payload.ValueKind == JsonValueKind.Object && Flag(payload, "ready") ? Text(payload, "gameSessionId") : null;
        bool saveChanged = previous != _gameSessionId;
        bool usageChanged = saveChanged || hadService != _partnerServiceReady;
        if (usageChanged)
        {
            ResetSaveViews();
            ApplyDecisions(default);
            Input.Clear();
        }
        _owner = Text(payload, "controlOwner");
        _playerMode = Text(payload, "actorMode") == "player";
        _paused = Flag(payload, "paused");
        _interrupting = Flag(payload, "interrupting");
        _recoveryPending = Flag(payload, "recoveryPending");
        if (_gameSessionId is null)
        {
            if (previous is not null) ConnectionText.Text = "等待游戏重新连接";
            ApplyDecisions(default);
            RefreshControls();
            if (usageChanged && Pages.SelectedItem == UsagePage) _ = ReadUsageAsync();
            return;
        }
        ConnectionText.Text = _playerMode ? "游戏已连接 · AI 自己玩" : "游戏已连接 · 伙伴模式";
        var action = Text(payload, "activity");
        var progress = Text(payload, "progress");
        ActivityText.Text = _recoveryPending ? "已重连。请先核对旧动作，工作保持暂停。"
            : _paused ? "已暂停"
            : _playerMode && _owner != "agent" ? "玩家控制中。点击「交给 AI」开始托管。"
            : _interrupting ? "正在确认旧动作终态"
            : Text(payload, "activeCommandId").Length > 0 ? $"游戏执行中 · {(progress.Length > 0 ? progress : StatusLabel(action))}"
            : Flag(payload, "thinking") ? "伴侣正在规划或回复"
            : action is "" or "idle" ? "等待指令" : StatusLabel(action);
        if (_lastGameSessionId is not null && _lastGameSessionId != _gameSessionId)
        {
            _entries.Clear();
            _seenResults.Clear();
        }
        _lastGameSessionId = _gameSessionId;
        if (payload.TryGetProperty("decisions", out var decisions)) ApplyDecisions(decisions);
        if (payload.TryGetProperty("work", out var work)) ApplyWork(work);
        ApplyInboxStatus(payload);
        RefreshControls();
        if (usageChanged && Pages.SelectedItem == UsagePage) _ = ReadUsageAsync();
        if (saveChanged) _ = LoadSelectedSaveViewAsync();
    }

    private void ApplyDecisions(JsonElement decisions)
    {
        string signature = decisions.ValueKind == JsonValueKind.Array && decisions.GetArrayLength() > 0
            ? decisions.GetRawText() : "";
        if (signature == _decisionSignature) return;
        _decisionSignature = signature;
        var previous = DecisionPicker.SelectedItem as DesktopDecision;
        var draft = DecisionInput.Text;
        var options = signature.Length == 0 ? Array.Empty<DesktopDecision>() : decisions.EnumerateArray()
            .Select(item => new DesktopDecision(Text(item, "id"), Text(item, "message"),
                Rows(item, "choices").Where(choice => choice.ValueKind == JsonValueKind.String)
                    .Select(choice => choice.GetString()!).ToArray())).ToArray();
        DecisionPicker.ItemsSource = options;
        DecisionPanel.Visibility = options.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DecisionPicker.Visibility = options.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (options.Length == 0) return;
        DecisionPicker.SelectedItem = options.FirstOrDefault(item => item.Id == previous?.Id) ?? options[0];
        if (previous?.Id == (DecisionPicker.SelectedItem as DesktopDecision)?.Id) DecisionInput.Text = draft;
    }

    private void DecisionSelected(object sender, SelectionChangedEventArgs e)
    {
        DecisionInput.Clear();
        DecisionChoices.Children.Clear();
        var decision = DecisionPicker.SelectedItem as DesktopDecision;
        DecisionText.Text = decision?.Title ?? "";
        if (decision is null) return;
        foreach (var label in decision.Choices)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) => await SendAsync(false, label, decision.Id);
            DecisionChoices.Children.Add(button);
        }
    }

    private async void DecisionReplyClicked(object sender, RoutedEventArgs e)
    {
        if (DecisionPicker.SelectedItem is not DesktopDecision decision) return;
        var text = DecisionInput.Text.Trim();
        if (await SendAsync(false, text, decision.Id)
            && (DecisionPicker.SelectedItem as DesktopDecision)?.Id == decision.Id
            && DecisionInput.Text.Trim() == text) DecisionInput.Clear();
    }

    private void ApplyResult(JsonElement result)
    {
        var id = Text(result, "commandId");
        if (id.Length == 0 || !_seenResults.Add(id)) return;
        if (_seenResults.Count > 512) _seenResults.Remove(_seenResults.First());
        var operation = StatusLabel(Text(result, "skillId"));
        var state = StatusLabel(Text(result, "terminalState"));
        int Count(string name) => result.TryGetProperty(name, out var value) && value.TryGetInt32(out int count) ? count : 0;
        string reason = result.TryGetProperty("error", out var error) ? Text(error, "message") : "";
        AddEntry("游戏回执", $"{operation} · {state} · 完成 {Count("completedCount")}，跳过 {Count("skippedCount")}，失败 {Count("failedCount")}"
            + (reason.Length > 0 ? $"\n{reason}" : ""));
    }

    private static string StatusLabel(string value) => value switch
    {
        "water-zone" or "watering" => "浇水",
        "harvest-zone" or "harvesting" => "收获",
        "navigate-to" or "navigating" => "移动",
        "chop-tree" => "采木",
        "clear-debris" => "清障",
        "pickup-items" => "拾取",
        "plant-seeds" => "播种",
        "refill-watering-can" => "补水",
        "succeeded" => "已完成",
        "partial" or "partially-succeeded" => "部分完成",
        "cancelled" => "已取消",
        "failed" or "rejected" => "未完成",
        "" or "idle" => "等待状态",
        _ => value
    };

    private void RefreshControls()
    {
        bool connected = _gameSessionId is not null && !_closing;
        LaunchHeader.IsEnabled = _session is null && !_closing && !_importing;
        bool reconnect = _session?.CanReconnect == true;
        LaunchHeader.Visibility = reconnect || _reconnecting ? Visibility.Collapsed : Visibility.Visible;
        ReconnectHeader.Visibility = reconnect || _reconnecting ? Visibility.Visible : Visibility.Collapsed;
        ReconnectHeader.IsEnabled = reconnect && !_reconnecting && !_closing;
        SettingsFields.IsEnabled = LaunchHeader.IsEnabled;
        RefreshImportControls();
        SendButton.IsEnabled = Input.IsEnabled = connected && _partnerReady;
        DecisionInput.IsEnabled = DecisionReplyButton.IsEnabled = DecisionChoices.IsEnabled = SendButton.IsEnabled;
        InterruptButton.IsEnabled = SendButton.IsEnabled && !_paused && !_interrupting && (!_playerMode || _owner == "agent");
        PauseButton.IsEnabled = connected && !_paused && !_interrupting;
        ResumeButton.IsEnabled = connected && !_recoveryPending && (_paused || _interrupting);
        CancelButton.IsEnabled = connected && !_recoveryPending;
        RecoverButton.Visibility = _recoveryPending ? Visibility.Visible : Visibility.Collapsed;
        RecoverButton.IsEnabled = connected && _recoveryPending;
        AuthorityButton.Visibility = _playerMode ? Visibility.Visible : Visibility.Collapsed;
        AuthorityButton.IsEnabled = connected && (_owner == "agent" || (_partnerReady && !_interrupting));
        AuthorityButton.Content = _owner == "agent" ? "玩家接管（F9）" : "交给 AI";
        RefreshSaveViewControls();
        RefreshPartnerControls();
    }

    private void AddEntry(string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _entries.Add(new TimelineEntry(label, text));
        while (_entries.Count > 180) _entries.RemoveAt(0);
        TimelineScroll.ScrollToBottom();
    }

    private void Log(string line) => Dispatcher.BeginInvoke(() =>
    {
        if (Diagnostics.Text.Length > 50_000) Diagnostics.Text = Diagnostics.Text[^30_000..];
        Diagnostics.AppendText(line + Environment.NewLine);
        Diagnostics.ScrollToEnd();
    });

    private static string Text(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_importing)
        {
            e.Cancel = true;
            return;
        }
        if (_closed || _session is not { } session) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        RefreshControls();
        await EndSessionAsync(session, "伴侣服务已关闭。");
        _closed = true;
        Close();
    }
}
