using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.PlayerMode;
using StardewAI.Companion.Mod.Transport;
using StardewModdingAPI;
using StardewValley;

namespace StardewAI.Companion.Mod;

public sealed partial class ModEntry
{
    private readonly bool _desktopMode = Environment.GetEnvironmentVariable("STARDEW_DESKTOP_MODE") == "1";
    private bool _desktopPaused;
    private bool _desktopInterrupting;
    private bool _desktopRecoveryPending;
    private string? _desktopInterruptedCommandId;
    private long _desktopSnapshotGeneration;
    private long _desktopHandledDisconnectRevision;
    private GameLocation? _desktopSnapshotLocation;
    private StardewValley.Menus.IClickableMenu? _desktopSnapshotMenu;
    private int _desktopSnapshotTime;
    private long _desktopPreviousTick;
    private long _desktopTickStarted;
    private long _desktopPhaseStarted;
    private long _desktopSlowestPhaseTicks;
    private long _desktopLastSlowTickLog;
    private double _desktopTickIntervalMs;
    private double _desktopPreviousModMs;
    private string _desktopSlowestPhase = "";

    private bool DesktopActionsAllowed => !_desktopPaused && !_desktopInterrupting
        && _desktopHandledDisconnectRevision == _transportServer?.DesktopDisconnectRevision;

    private void EnableDesktopFocusPolicy()
    {
        try
        {
            DesktopFocusPolicy.Install(() => Context.IsWorldReady && !Context.IsMultiplayer
                && _transportServer?.IsChatConnected == true && (!PlayerModeEnabled || _authority?.AgentOwns == true));
        }
        catch (Exception ex)
        {
            Monitor.Log($"Desktop background play could not be enabled. Adjust the native out-of-focus pause option if needed: {ex.Message}", LogLevel.Warn);
        }
    }

    private void ShowCompanionHud(HUDMessage message)
    {
        if (!_desktopMode) Game1.addHUDMessage(message);
    }

    private void AttachDesktopControls(WebSocketTransportServer server)
    {
        _desktopPaused = _desktopInterrupting = _desktopRecoveryPending = false;
        _desktopInterruptedCommandId = null;
        _desktopSnapshotGeneration = -1;
        _desktopSnapshotLocation = null;
        _desktopSnapshotMenu = null;
        _desktopSnapshotTime = 0;
        _desktopHandledDisconnectRevision = 0;
        server.DesktopClientEnabled = true;
        server.OnDesktopControl += payload =>
        {
            long generation = server.ChatGeneration;
            _mainThreadActions.Enqueue(() =>
            {
                if (ReferenceEquals(_transportServer, server) && server.IsChatConnected && generation == server.ChatGeneration)
                    HandleDesktopControl(server, payload, generation);
            });
        };
        server.OnPlayerMessage += (type, _) =>
        {
            if (type != "disconnected") return;
            long revision = server.DesktopDisconnectRevision;
            _mainThreadActions.Enqueue(() =>
            {
                if (!ReferenceEquals(_transportServer, server)) return;
                _desktopInterruptedCommandId ??= server.LatestDesktopActionId;
                _desktopPaused = _desktopInterrupting = _desktopRecoveryPending = true;
                CancelDesktopAction();
                if (_lifeMenuUiState.PendingChatRequestIds.Any())
                    ShowChatChannelProblem(null, null, "伙伴服务连接中断，回复尚未确认。");
                if (revision > _desktopHandledDisconnectRevision)
                    _desktopHandledDisconnectRevision = revision;
            });
        };
    }

    private void PublishDesktopControlState(bool periodicPlayerSnapshot)
    {
        if (_transportServer is not { IsChatConnected: true } server || _coordinator is not { } coordinator) return;
        long generation = server.ChatGeneration;
        if (_desktopHandledDisconnectRevision != server.DesktopDisconnectRevision) return;
        bool newConnection = _desktopSnapshotGeneration != generation;
        bool shouldRefresh = PlayerModeEnabled && (periodicPlayerSnapshot
            || !ReferenceEquals(_desktopSnapshotLocation, Game1.currentLocation)
            || !ReferenceEquals(_desktopSnapshotMenu, Game1.activeClickableMenu));
        if (periodicPlayerSnapshot && Game1.timeOfDay != _desktopSnapshotTime)
            shouldRefresh = true;
        if (_observer is not null && (newConnection || shouldRefresh))
        {
            _desktopSnapshotGeneration = generation;
            _desktopSnapshotLocation = Game1.currentLocation;
            _desktopSnapshotMenu = Game1.activeClickableMenu;
            _desktopSnapshotTime = Game1.timeOfDay;
            _ = server.SendSnapshotAsync(coordinator.CaptureCurrentSnapshot(_observer.WorldRevision), _observer.WorldRevision);
        }
        _ = server.SendDesktopStateAsync("desktop.control.result", new JsonObject
        {
            ["paused"] = _desktopPaused,
            ["interrupting"] = _desktopInterrupting,
            ["recoveryPending"] = _desktopRecoveryPending,
            ["interruptedCommandId"] = _desktopInterruptedCommandId,
            ["activeCommandId"] = server.ActiveCommandId,
            ["activity"] = coordinator.GetActivityStatus(),
            ["progress"] = coordinator.GetActivityLabel(),
            ["clockPauseOwner"] = coordinator.ClockPauseOwnerProvider?.Invoke(),
            ["inboxRevision"] = _lifeMenuUiState.ConversationRevision,
            ["unreadMessages"] = CountUnreadDesktopMessages()
        }, generation);
    }

    private void BeginDesktopTick()
    {
        long now = Stopwatch.GetTimestamp();
        _desktopTickIntervalMs = _desktopPreviousTick == 0 ? 0 : TickMilliseconds(now - _desktopPreviousTick);
        _desktopPreviousTick = _desktopTickStarted = _desktopPhaseStarted = now;
        _desktopSlowestPhaseTicks = 0;
    }

    private void MeasureDesktopPhase(string phase)
    {
        if (_desktopTickStarted == 0) return;
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _desktopPhaseStarted;
        _desktopPhaseStarted = now;
        if (elapsed <= _desktopSlowestPhaseTicks) return;
        _desktopSlowestPhase = phase;
        _desktopSlowestPhaseTicks = elapsed;
    }

    private void EndDesktopTick()
    {
        MeasureDesktopPhase("after-native-action");
        long now = Stopwatch.GetTimestamp();
        double modMs = TickMilliseconds(now - _desktopTickStarted);
        _desktopTickStarted = 0;
        double previousModMs = _desktopPreviousModMs;
        _desktopPreviousModMs = modMs;
        if ((modMs < 20 && _desktopTickIntervalMs < 50) || TickMilliseconds(now - _desktopLastSlowTickLog) < 5000) return;
        _desktopLastSlowTickLog = now;
        Monitor.Log($"[游戏帧耗时] 更新间隔 {_desktopTickIntervalMs:F1} ms，伙伴处理 {previousModMs:F1} → {modMs:F1} ms，"
            + $"当前帧最慢阶段 {_desktopSlowestPhase} {TickMilliseconds(_desktopSlowestPhaseTicks):F1} ms，"
            + $"活动 {_coordinator?.GetActivityStatus() ?? "idle"}，时钟 {_coordinator?.ClockPauseOwnerProvider?.Invoke() ?? "running"}。", LogLevel.Warn);
    }

    private static double TickMilliseconds(long ticks) => ticks * (1000.0 / Stopwatch.Frequency);

    private static bool IsDesktopMessage(CompanionConversationEntry entry) => entry.Id.StartsWith("care:", StringComparison.Ordinal);

    private static bool IsUnreadDesktopMessage(CompanionConversationEntry entry, string gameDate) =>
        IsDesktopMessage(entry) && entry.Unread && !entry.RolledBack && entry.GameDate == gameDate;

    private int CountUnreadDesktopMessages()
    {
        string gameDate = GetCurrentGameDate();
        return _lifeMenuUiState.Conversation.Count(entry => IsUnreadDesktopMessage(entry, gameDate));
    }

    private void CancelDesktopAction()
    {
        _coordinator?.CancelRouteObservation("Desktop control");
        if (_coordinator?.CancelRestTask() != true)
            _coordinator?.ActiveMachine?.RequestCancel("Desktop control");
    }

    private void HandleDesktopControl(WebSocketTransportServer server, JsonObject payload, long generation)
    {
        if (!Context.IsWorldReady || _coordinator is not { } coordinator || _observer is null
            || payload["requestId"] is not JsonValue key || !key.TryGetValue<string>(out var requestId))
            return;

        string? action = payload["action"]?.GetValue<string>();
        if (_desktopHandledDisconnectRevision != server.DesktopDisconnectRevision) return;
        if (action is "history" or "inbox")
        {
            int? before = payload["before"] is JsonValue cursor && cursor.TryGetValue<int>(out int index) ? index : null;
            _ = SendDesktopHistoryAsync(server, requestId, before, action == "inbox");
            return;
        }

        string? error = null;
        if (_desktopRecoveryPending && action is not ("status" or "observe" or "read-message" or "recover" or "takeover"))
            error = "RECOVERY_PENDING";
        SkillResultPayload? receipt = null;
        string? receiptId = payload["commandId"]?.GetValue<string>();
        if (action == "recover") receiptId = _desktopInterruptedCommandId;
        if (receiptId is { Length: > 0 } commandId)
        {
            server.Idempotency.TryGetCompletedResult(commandId, out receipt);
            if (receipt is null && coordinator.LastSkillResultPayload is { } last && last.CommandId == commandId)
                receipt = last;
        }
        bool active = server.ActiveCommandId is not null || coordinator.ActiveMachine is not null || coordinator.HasRestTask;
        if (error is null)
        switch (action)
        {
            case "status": break;
            case "observe": break;
            case "recover":
                if (!_desktopRecoveryPending) break;
                if (active) error = "ACTION_STILL_RUNNING";
                else if (receiptId is not null && (receipt is null
                    || !AgentProtocol.IsTerminalState(receipt.TerminalState)))
                    error = "TERMINAL_UNKNOWN";
                else
                {
                    _desktopRecoveryPending = _desktopInterrupting = false;
                    _desktopInterruptedCommandId = null;
                    server.LatestDesktopActionId = null;
                }
                break;
            case "read-message":
                var id = payload["entryId"]?.GetValue<string>();
                if (!_lifeMenuUiState.Conversation.Any(entry => entry.Id == id && IsDesktopMessage(entry)))
                    error = "MESSAGE_NOT_AVAILABLE";
                else
                {
                    _lifeMenuUiState.MarkConversationRead(id);
                    _careHintController.MarkRead(id!["care:".Length..]);
                }
                break;
            case "interrupt":
                _desktopInterruptedCommandId ??= server.ActiveCommandId;
                _desktopInterrupting = true;
                CancelDesktopAction();
                break;
            case "unhold":
                if (server.ActiveCommandId is null && coordinator.ActiveMachine is null && !coordinator.HasRestTask)
                {
                    _desktopInterrupting = false;
                    _desktopInterruptedCommandId = null;
                }
                else error = "ACTION_STILL_RUNNING";
                break;
            case "pause":
                _desktopPaused = true;
                if (coordinator.HasRestTask) coordinator.PauseRestTask();
                else coordinator.ActiveMachine?.RequestPause();
                break;
            case "resume":
                if (_desktopInterrupting)
                    error = "ACTION_STILL_SETTLING";
                else
                {
                    _desktopPaused = false;
                    if (coordinator.HasRestTask) coordinator.ResumeRestTask();
                    else coordinator.ActiveMachine?.Resume();
                }
                break;
            case "takeover" when PlayerModeEnabled:
                ReleasePlayerControl("human-takeover");
                break;
            case "handoff" when PlayerModeEnabled && _authority is not null:
                if (_desktopInterrupting || coordinator.ActiveMachine is not null || coordinator.HasRestTask)
                    error = "ACTION_STILL_SETTLING";
                else
                {
                    _playerControlApproved = true;
                    _authority.RequestAgent("human-explicit-handoff");
                }
                break;
            case "record" when payload["text"]?.GetValue<string>() is { Length: > 0 and <= 500 } message:
                string conversationRequest = payload["conversationRequestId"]?.GetValue<string>() ?? requestId;
                if (!_lifeMenuUiState.BeginChat(conversationRequest, "chat")) error = "CHAT_BUSY";
                else
                    _lifeMenuUiState.RecordConversation(conversationRequest + ":player", "你", message, GetCurrentGameDate(),
                        isPlayer: true, replyToDecisionId: payload["decisionId"]?.GetValue<string>());
                break;
            default: error = "CONTROL_UNAVAILABLE"; break;
        }

        var reply = new JsonObject
        {
            ["requestId"] = requestId,
            ["error"] = error,
            ["activeCommandId"] = server.ActiveCommandId,
            ["activity"] = coordinator.GetActivityStatus(),
            ["progress"] = coordinator.GetActivityLabel(),
            ["clockPauseOwner"] = coordinator.ClockPauseOwnerProvider?.Invoke(),
            ["inboxRevision"] = _lifeMenuUiState.ConversationRevision,
            ["unreadMessages"] = CountUnreadDesktopMessages(),
            ["interruptedCommandId"] = _desktopInterruptedCommandId,
            ["active"] = server.ActiveCommandId is not null || coordinator.ActiveMachine is not null || coordinator.HasRestTask,
            ["paused"] = _desktopPaused,
            ["interrupting"] = _desktopInterrupting,
            ["recoveryPending"] = _desktopRecoveryPending,
            ["controlOwner"] = PlayerModeEnabled ? _authority?.AgentOwns == true ? "agent" : "human" : null,
            ["result"] = receipt is null ? null : JsonSerializer.SerializeToNode(receipt, WebSocketTransportServer.JsonOptions)
        };
        if (action == "observe" && error is null)
            reply["observation"] = JsonSerializer.SerializeToNode(coordinator.CaptureCurrentSnapshot(_observer.WorldRevision), WebSocketTransportServer.JsonOptions);
        else if (action is not ("status" or "read-message"))
            _ = server.SendSnapshotAsync(coordinator.CaptureCurrentSnapshot(_observer.WorldRevision), _observer.WorldRevision);
        _ = server.SendDesktopStateAsync("desktop.control.result", reply, generation);
    }

    private async Task SendDesktopHistoryAsync(WebSocketTransportServer server, string requestId, int? before, bool inbox)
    {
        var store = _conversationStore;
        var saveId = _conversationSaveId;
        long generation = server.ChatGeneration;
        string gameDate = GetCurrentGameDate();
        var unread = inbox ? _lifeMenuUiState.Conversation.Where(entry => IsUnreadDesktopMessage(entry, gameDate))
            .Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal) : null;
        var reply = new JsonObject { ["requestId"] = requestId };
        try
        {
            if (store is null || saveId is null) throw new InvalidOperationException("No conversation store for this save");
            var page = await Task.Run(() =>
            {
                var history = store.LoadHistory(saveId);
                if (inbox) history = history.Where(IsDesktopMessage).ToArray();
                int end = Math.Clamp(before ?? history.Count, 0, history.Count);
                int start = Math.Max(0, end - 30);
                var entries = history.Skip(start).Take(end - start).Reverse();
                return new
                {
                    entries = (unread is null ? entries : entries.Select(entry => entry with { Unread = unread.Contains(entry.Id) })).ToArray(),
                    nextBefore = start > 0 ? (int?)start : null
                };
            }).ConfigureAwait(false);
            reply["history"] = JsonSerializer.SerializeToNode(page, WebSocketTransportServer.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Monitor.Log($"Desktop history unavailable: {ex.Message}", LogLevel.Warn);
            reply["error"] = "HISTORY_UNAVAILABLE";
        }
        if (server.ChatGeneration == generation && server.IsChatConnected)
            await server.SendDesktopStateAsync("desktop.control.result", reply, generation).ConfigureAwait(false);
    }
}
