using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using StardewAI.Companion.Logging;

namespace StardewAI.Companion.Desktop;

public sealed record TimelineEntry(string Label, string Text);
public sealed record DesktopDecision(string Id, string Title, IReadOnlyList<string> Choices);

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TimelineEntry> _entries = new();
    private readonly HashSet<string> _seenResults = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attentionNotices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenCareMessages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingChatRequests = new(StringComparer.Ordinal);
    private readonly string _root = AppContext.BaseDirectory;
    private readonly FileLogger _logger;
    private DesktopSettings _savedSettings = new();
    private DesktopSession? _session;
    private string? _gameSessionId;
    private string? _saveId;
    private string? _lastGameSessionId;
    private string? _owner;
    private bool _playerMode;
    private bool _paused;
    private bool _interrupting;
    private bool _recoveryPending;
    private bool _reconnecting;
    private bool _closing;
    private string? _decisionSignature;
    private string? _submittingRequestId;
    private bool _submissionAccepted;
    private string _activeCommandId = "";
    private string _chatActivity = "";
    private DateTimeOffset? _feedbackAt;
    private string _feedbackId = "", _feedbackDate = "", _gameDate = "";
    private bool _observingRecent;
    private string _observedFeedbackId = "";
    private DateTimeOffset? _recentObservedAt;

    public MainWindow()
    {
        InitializeComponent();
        _logger = new FileLogger(DesktopInstallation.LogDirectory(_root),
            configuredDirectory: Environment.GetEnvironmentVariable("STARDEW_COMPANION_LOG_DIR"));
        LogDirectoryText.Text = _logger.DirectoryPath;
        _logger.Write("desktop", "info", "desktop_started", "伴侣桌面已启动。");
        Timeline.ItemsSource = _entries;
        InitializeSaveViews();
        try
        {
            _savedSettings = DesktopInstallation.LoadSettings(_root);
            GameFolder.Text = DesktopInstallation.IsGameDirectory(_savedSettings.GameDirectory)
                ? _savedSettings.GameDirectory : DesktopInstallation.DetectGame();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LogError("settings_read_failed", "读取桌面设置失败。", ex);
        }
        Mode.SelectedIndex = _savedSettings.ActorMode == "player" ? 1 : 0;
        InitializeApiModel(_savedSettings.ApiModel ?? new(), _savedSettings.CustomModel);
        Pages.SelectedItem = CommandPage;
        SettingsTabs.SelectedItem = LaunchSettingsTab;
        RefreshControls();
        InitializeTray();
        InitializePlugins();
    }

    private DesktopSettings LaunchSettings() => _savedSettings with
    {
        GameDirectory = GameFolder.Text.Trim(),
        ActorMode = Mode.SelectedIndex == 1 ? "player" : "companion",
    };

    private void SaveDesktopSettings(DesktopSettings settings)
    {
        DesktopInstallation.SaveSettings(_root, settings);
        _savedSettings = settings;
    }

    private async void LaunchClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not null || _closing || _testingModel) return;
        SettingsFeedback.Text = GameFolderError.Text = "";
        var settings = LaunchSettings();
        if (!DesktopInstallation.IsGameDirectory(settings.GameDirectory))
        {
            ShowSettingsError(LaunchSettingsTab, GameFolder, GameFolderError, "请选择包含 StardewModdingAPI.exe 的游戏目录。");
            return;
        }
        string key;
        try
        {
            var selected = ApiSelection();
            key = ResolveApiKey(selected);
            SaveModelSettings(settings, selected, key);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            ShowSettingsError(ModelSettingsTab, ApiSettingsErrorField(), ApiModelFeedback, error.Message);
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowSettingsError(LaunchSettingsTab, GameFolder, SettingsFeedback, "保存失败，请检查解压目录是否可写。");
            LogError("launch_settings_failed", "无法保存启动设置。", error);
            return;
        }
        var session = new DesktopSession(_root, _logger);
        _session = session;
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
        Pages.SelectedItem = CommandPage;
        ConnectionText.Text = "正在检查运行组件";
        ActivityText.Text = "正在启动伴侣和游戏。";
        RefreshControls();
        try
        {
            await session.StartAsync(_savedSettings, key);
            if (_session == session && _gameSessionId is null)
            {
                ConnectionText.Text = "等待进入存档";
                ActivityText.Text = "请在游戏窗口中选择单人存档。";
                RefreshComposer();
            }
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                LogError("session_start_failed", "无法启动伴侣会话。", ex);
                await EndSessionAsync(session, "游戏未启动，请查看设置。");
                if (_closing || _session is not null) return;
                Pages.SelectedItem = SettingsPage;
                SettingsTabs.SelectedItem = LaunchSettingsTab;
                SettingsFeedback.Text = ex.Message;
            }
        }
    }

    private void MarkRuntimeExited()
    {
        _plugins?.DisconnectRuntime();
        ApplyStatus(default);
        ConnectionText.Text = "伙伴服务已退出";
        ActivityText.Text = "游戏仍在运行。点击「重连伙伴服务」后核对旧动作。";
        RefreshComposer();
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
                LogError("session_reconnect_failed", "无法重连伙伴服务。", ex);
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
        _plugins?.DisconnectRuntime();
        _partnerServiceReady = false;
        _gameSessionId = null;
        _saveId = null;
        ResetSaveViews();
        ApplyDecisions(default);
        RefreshControls();
        try { await session.StopAsync(); }
        catch (Exception ex) { LogError("session_stop_failed", "无法停止伙伴服务。", ex); }
        if (_session != session) return;
        _session = null;
        _plugins?.DisconnectRuntime();
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

    private void GameFolderEdited(object sender, TextChangedEventArgs e)
    {
        if (GameFolderError is not null) GameFolderError.Text = "";
    }

    private void ShowSettingsError(TabItem tab, Control field, TextBlock error, string message)
    {
        error.Text = message;
        Pages.SelectedItem = SettingsPage;
        SettingsTabs.SelectedItem = tab;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Pages.SelectedItem != SettingsPage || SettingsTabs.SelectedItem != tab) return;
            field.BringIntoView();
            field.Focus();
            error.BringIntoView();
        });
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        var settings = LaunchSettings();
        if (!DesktopInstallation.IsGameDirectory(settings.GameDirectory))
        {
            ShowSettingsError(LaunchSettingsTab, GameFolder, GameFolderError, "请选择包含 StardewModdingAPI.exe 的游戏目录。");
            return;
        }
        try
        {
            SaveDesktopSettings(settings);
            SettingsFeedback.Text = "启动设置已保存，下次启动生效。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsFeedback.Text = "保存失败，请检查解压目录是否可写。";
            LogError("settings_save_failed", "无法保存启动设置。", ex);
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
        if (_submittingRequestId is not null || _session is not { } session || _gameSessionId is not { } game) return false;
        var text = selection ?? Input.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;
        int view = _saveViewGeneration;
        string requestId = Guid.NewGuid().ToString("N");
        _submittingRequestId = requestId;
        _submissionAccepted = false;
        RefreshComposer();
        try
        {
            await session.PostAsync("/v2/commands", new {
                gameSessionId = game, saveId = _saveId, requestId, text,
                mode = interrupt ? "interrupt" : "send", decisionId,
            });
        }
        catch (Exception ex)
        {
            if (view != _saveViewGeneration) return false;
            if (!_submissionAccepted)
            {
                AddEntry("发送未确认", ex.Message);
                return false;
            }
        }
        finally
        {
            if (_submittingRequestId == requestId)
            {
                _submittingRequestId = null;
                RefreshComposer();
            }
        }
        if (view != _saveViewGeneration) return false;
        if (selection is null && Input.Text.Trim() == text) Input.Clear();
        return true;
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
        try { await session.PostAsync("/v2/control", new { gameSessionId = game, saveId = _saveId, action }); }
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
        var type = Text(message, "type");
        _plugins?.ApplyRuntimeEvent(type, payload);
        switch (type)
        {
            case "status": ApplyStatus(payload); break;
            case "submitted":
                var requestId = Text(payload, "requestId");
                if (requestId == _submittingRequestId) _submissionAccepted = true;
                if (_gameSessionId is not null && requestId.Length > 0) _pendingChatRequests.Add(requestId);
                var pluginSource = Text(payload, "pluginSource");
                AddEntry(pluginSource.Length > 0 ? "插件 · " + pluginSource : "你", Text(payload, "text"));
                RefreshComposer();
                break;
            case "command" when Text(payload, "status") == "interrupting":
                _chatActivity = "正在改派，等待上一项动作停止。";
                AddEntry("正在改派", "等待上一项动作停止并核对结果。");
                RefreshComposer();
                break;
            case "life.care":
                var noticeId = Text(payload, "eventKey");
                if (Text(payload, "kind") != "work-done" && (noticeId.Length == 0 || _seenCareMessages.Add(noticeId)))
                    AddEntry("提醒", Text(payload, "text"));
                break;
            case "chat.reply":
            case "life.chat.reply":
                if (Text(payload, "status") is not ("accepted" or "queued" or "processing"))
                    _pendingChatRequests.Remove(Text(payload, "requestId"));
                RefreshComposer();
                if (Flag(payload, "replyAlreadyDelivered")) break;
                var reply = Text(payload, "replyText");
                var error = Text(payload, "error");
                if (reply.Length > 0 || error.Length > 0)
                    AddEntry(error.Length > 0 ? "未完成" : "伴侣", reply.Length > 0 ? reply : error);
                break;
            case "result": ApplyResult(payload); break;
            case "recovery":
                if (Text(payload, "gameSessionId") != _gameSessionId || Text(payload, "saveId") != _saveId) break;
                var confirmed = payload.TryGetProperty("receipt", out var recovered) && recovered.ValueKind == JsonValueKind.Object
                    ? ActionPresentation.Read(recovered).Text : "已核对旧动作的游戏回执。";
                AddEntry("核对结果", (Flag(payload, "hasReceipt") ? confirmed : "游戏中没有待结算的旧动作。")
                    + "请确认当前控制状态后再继续。");
                break;
        }
    }

    private void ApplyStatus(JsonElement payload)
    {
        bool hadService = _partnerServiceReady;
        ApplyPartnerStatus(payload);
        var previous = _gameSessionId;
        _gameSessionId = payload.ValueKind == JsonValueKind.Object && Flag(payload, "ready") ? Text(payload, "gameSessionId") : null;
        _saveId = _gameSessionId is null ? null : Text(payload, "saveId");
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
        _modelBusy = Flag(payload, "modelBusy");
        _gameDate = Text(payload, "gameDate");
        RefreshRecentFeedback();
        if (_gameSessionId is null)
        {
            if (previous is not null)
            {
                ConnectionText.Text = "等待游戏重新连接";
                ActivityText.Text = "请在游戏中选择单人存档。";
            }
            ApplyDecisions(default);
            RefreshControls();
            if (usageChanged && Pages.SelectedItem == UsagePage) _ = ReadUsageAsync();
            return;
        }
        bool playerControls = _playerMode && _owner != "agent";
        ConnectionText.Text = !_playerMode ? "游戏已连接 · 伙伴模式"
            : playerControls ? "游戏已连接 · 玩家控制" : "游戏已连接 · AI 控制";
        var reportedCommandId = Text(payload, "activeCommandId");
        bool staleAction = reportedCommandId.Length > 0 && _seenResults.Contains(reportedCommandId);
        _activeCommandId = staleAction ? "" : reportedCommandId;
        var action = staleAction ? "idle" : Text(payload, "activity");
        var clockPauseOwner = Text(payload, "clockPauseOwner");
        var skill = Text(payload, "activeSkillId");
        var phase = Text(payload, "agentPhase");
        var execution = phase.Length > 0 ? ActionPresentation.Phase(phase, skill) : ActionPresentation.Activity(skill.Length > 0 ? skill : action);
        var thinking = payload.TryGetProperty("modelStreaming", out var streaming) && streaming.ValueKind == JsonValueKind.False
            ? "等待模型响应" : "伴侣正在规划或回复";
        if (clockPauseOwner == "model-thinking") thinking += " · 游戏时钟已暂停";
        bool isThinking = clockPauseOwner == "model-thinking" || Flag(payload, "thinking");
        _chatActivity = _recoveryPending ? "请先核对旧动作。"
            : _interrupting ? "正在等待旧动作停止并核对结果。"
            : _activeCommandId.Length > 0 ? execution
            : clockPauseOwner == "program-protection" ? "保护暂停 · 游戏时钟已暂停"
            : isThinking ? thinking
            : _paused ? "工作已暂停。"
            : !_partnerReady ? "请在「设置」连接伙伴后发送消息。" : "";
        (AgentStateText.Text, ActivityText.Text) = _recoveryPending ? ("待核对", "已重连。请先核对旧动作，工作保持暂停。")
            : playerControls ? ("玩家控制中", "点击「交给 AI」开始托管。")
            : _paused ? ("已暂停", "可在「今天」继续当前工作。")
            : _interrupting ? ("交接中", "正在确认旧动作终态。")
            : _activeCommandId.Length > 0 ? (phase is "planning-route" ? "正在寻路" : phase is "verifying" or "reconciling" or "waiting-receipt" ? "正在核对" : "正在执行", execution)
            : clockPauseOwner == "program-protection" ? ("已暂停", "保护暂停 · 游戏时钟已暂停")
            : isThinking ? ("正在思考", thinking)
            : !_partnerReady ? ("等待伙伴", "请在「设置」检查伙伴连接。")
            : phase == "needs-player" ? ("需要你确认", ActionPresentation.Phase(phase))
            : action is "" or "idle" ? ("等待指令", "可以在「今天」和伙伴聊聊。") : ("正在工作", ActionPresentation.Activity(action));
        if (_lastGameSessionId is not null && _lastGameSessionId != _gameSessionId)
        {
            _entries.Clear();
            _seenResults.Clear();
        }
        _lastGameSessionId = _gameSessionId;
        if (payload.TryGetProperty("decisions", out var decisions)) ApplyDecisions(decisions);
        if (payload.TryGetProperty("work", out var work)) ApplyWork(work);
        RenderWork(activityOnly: true);
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
        TimelineScroll.ScrollToBottom();
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
        if (_gameSessionId is null || (Text(result, "gameSessionId") is { Length: > 0 } game && game != _gameSessionId)
            || (Text(result, "saveId") is { Length: > 0 } save && save != _saveId)) return;
        var id = Text(result, "commandId");
        if (id.Length == 0 || !_seenResults.Add(id)) return;
        if (_seenResults.Count > 512) _seenResults.Remove(_seenResults.First());
        var display = ActionPresentation.Read(result);
        _feedbackId = id;
        _feedbackDate = display.Date;
        _feedbackAt = DateTimeOffset.TryParse(Text(result, "recordedAt"), out var recordedAt) ? recordedAt : DateTimeOffset.UtcNow;
        RefreshRecentFeedback();
        ActionFeedbackText.Text = display.Text;
        ActionFeedbackPanel.Visibility = Visibility.Visible;
        ActionFeedbackPanel.Background = new System.Windows.Media.SolidColorBrush(display.IsIssue
            ? System.Windows.Media.Color.FromRgb(255, 242, 220) : System.Windows.Media.Color.FromRgb(232, 237, 220));
        if (display.NeedsPlayer && _attentionNotices.Add(display.RepeatKey))
            AddEntry("需要你确认", display.Text + " 可查看农场工作或核对旧动作。");
        if (_attentionNotices.Count > 512) _attentionNotices.Remove(_attentionNotices.First());
        if (_activeCommandId == id)
        {
            _activeCommandId = "";
            if (!_interrupting) _chatActivity = "正在确认游戏结果";
            if (AgentStateText.Text == "正在执行")
            {
                AgentStateText.Text = "已收到回执";
                ActivityText.Text = display.Text;
            }
            RefreshComposer();
        }
    }

    private void RefreshRecentFeedback()
    {
        RecentRecoverButton.Visibility = _recoveryPending ? Visibility.Visible : Visibility.Collapsed;
        RecentRecoverButton.IsEnabled = _gameSessionId is not null && !_interrupting && !_closing;
        RecentObserveButton.IsEnabled = _gameSessionId is not null && !_observingRecent && !_closing;
        if (_feedbackAt is null) return;
        ActionFeedbackCaption.Text = _feedbackDate.Length > 0 && _gameDate.Length > 0 && _feedbackDate != _gameDate
            ? "上次进展 · 来自之前的游戏日期，请以当前观察为准"
            : _observedFeedbackId == _feedbackId && _recentObservedAt is { } observed && DateTimeOffset.UtcNow - observed < TimeSpan.FromMinutes(2)
                ? "已刷新游戏观察 · 下方保留上次操作结果"
            : DateTimeOffset.UtcNow - _feedbackAt.Value > TimeSpan.FromMinutes(2)
                ? "上次进展 · 距今已超过 2 分钟，可重新查看游戏"
                : "最近进展 · 已确认的历史回执";
    }

    private async void ObserveRecentClicked(object sender, RoutedEventArgs e)
    {
        if (_observingRecent || _session is not { } session || _gameSessionId is not { } game) return;
        int view = _saveViewGeneration;
        var id = _feedbackId;
        _observingRecent = true;
        RefreshRecentFeedback();
        try
        {
            await session.PostAsync("/v2/control", new { gameSessionId = game, saveId = _saveId, action = "observe" });
            if (view == _saveViewGeneration && id == _feedbackId)
            {
                _observedFeedbackId = id;
                _recentObservedAt = DateTimeOffset.UtcNow;
                RefreshRecentFeedback();
            }
        }
        catch (Exception ex)
        {
            if (view == _saveViewGeneration) WorkFeedback.Text = "刷新游戏观察失败，请稍后重试。";
            LogError("recent_observe_failed", "刷新游戏观察失败。", ex);
        }
        finally
        {
            if (view == _saveViewGeneration)
            {
                _observingRecent = false;
                RecentObserveButton.IsEnabled = !_closing;
            }
        }
    }

    private void RefreshControls()
    {
        bool connected = _gameSessionId is not null && !_closing;
        if (!connected) AgentStateText.Text = "未连接";
        TimelineHint.Text = !connected ? (_session is null ? "点右上角「启动游戏」，然后在游戏窗口选择单人存档。"
            : "选择单人存档后，就可以和伙伴聊天。")
            : _partnerReady ? "可以请伙伴照料农场，也可以和它聊聊。" : "连接伙伴后即可发送消息。";
        LaunchHeader.IsEnabled = _session is null && !_closing && !_testingModel;
        bool reconnect = _session?.CanReconnect == true;
        LaunchHeader.Visibility = reconnect || _reconnecting ? Visibility.Collapsed : Visibility.Visible;
        ReconnectHeader.Visibility = reconnect || _reconnecting ? Visibility.Visible : Visibility.Collapsed;
        ReconnectHeader.IsEnabled = reconnect && !_reconnecting && !_closing;
        SettingsFields.IsEnabled = LaunchHeader.IsEnabled;
        ApiModelFields.IsEnabled = !_testingModel && !_closing && !_reconnecting;
        ApplyModelButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ApplyModelButton.IsEnabled = _partnerServiceReady && !_interrupting && !_modelBusy;
        TodayOverview.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ComposerPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        AuthorityButton.Visibility = connected && _playerMode ? Visibility.Visible : Visibility.Collapsed;
        RefreshComposer();
        PauseButton.IsEnabled = connected && !_paused && !_interrupting;
        ResumeButton.IsEnabled = connected && !_recoveryPending && (_paused || _interrupting);
        CancelButton.IsEnabled = connected && !_recoveryPending;
        RecoverButton.Visibility = _recoveryPending ? Visibility.Visible : Visibility.Collapsed;
        RecoverButton.IsEnabled = connected && _recoveryPending;
        AuthorityButton.IsEnabled = connected && (_owner == "agent" || (_partnerReady && !_interrupting));
        AuthorityButton.Content = _owner == "agent" ? "玩家接管（F9）" : "交给 AI";
        RefreshSaveViewControls();
    }

    private void RefreshComposer()
    {
        bool connected = _gameSessionId is not null && !_closing;
        bool canChat = connected && _partnerReady;
        bool canInterrupt = !_playerMode || _owner == "agent";
        Input.IsEnabled = DecisionInput.IsEnabled = canChat;
        SendButton.IsEnabled = DecisionReplyButton.IsEnabled = DecisionChoices.IsEnabled = canChat && _submittingRequestId is null;
        InterruptButton.Visibility = canInterrupt ? Visibility.Visible : Visibility.Collapsed;
        InterruptButton.IsEnabled = SendButton.IsEnabled && canInterrupt && !_paused && !_interrupting;
        SendShortcutHint.Visibility = canChat ? Visibility.Visible : Visibility.Collapsed;
        ChatStatus.Text = !connected ? (_session is not null ? ActivityText.Text : "")
            : _submittingRequestId is not null ? "正在提交消息…"
            : _chatActivity.Length > 0 ? _chatActivity
            : _pendingChatRequests.Count > 0 ? "已提交，等待 Agent 处理。" : "";
        OwnershipHint.Text = !connected || !_playerMode || !_partnerReady ? ""
            : _owner != "agent" ? "可以先聊天。让 AI 操作游戏，请先交给 AI。"
            : ChatStatus.Text.Length == 0 ? "AI 正在控制游戏。" : "";
    }

    private void AddEntry(string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _entries.Add(new TimelineEntry(label, text));
        while (_entries.Count > 180) _entries.RemoveAt(0);
        TimelineScroll.ScrollToBottom();
    }

    private void OpenWorkClicked(object sender, RoutedEventArgs e) => Pages.SelectedItem = WorkPage;

    private void OpenChatClicked(object sender, RoutedEventArgs e)
    {
        Pages.SelectedItem = CommandPage;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Input.Focus());
    }

    private void LogError(string eventName, string message, Exception error)
        => _logger.Write("desktop", "warn", eventName, message, new { errorType = error.GetType().Name, error.HResult });

    private void OpenLogsClicked(object sender, RoutedEventArgs e)
    {
        RefreshLogDirectory();
        try
        {
            var directory = LogDirectoryText.Text;
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory, UseShellExecute = true,
            })?.Dispose();
            LogMaintenanceStatus.Text = _logger.StorageError ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogMaintenanceStatus.Text = "无法打开日志目录，请检查目录权限。";
            LogError("log_folder_open_failed", "无法打开日志目录。", error);
        }
    }

    private void RefreshLogDirectory()
    {
        if (_logger is null) return;
        LogDirectoryText.Text = _logger.DirectoryPath;
        LogMaintenanceStatus.Text = _logger.StorageError ?? "";
    }

    private static string Text(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
}
