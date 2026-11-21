using StardewAI.Companion.Mod.PlayerMode;
using Color = Microsoft.Xna.Framework.Color;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Tools;
using StardewAI.Companion.Mod.Adapters;
using StardewAI.Companion.Mod.Avatar;
using StardewAI.Companion.Mod.Discovery;
using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Execution;
using StardewAI.Companion.Mod.Menus;
using StardewAI.Companion.Mod.Navigation;
using StardewAI.Companion.Mod.Observation;
using StardewAI.Companion.Mod.Transport;

namespace StardewAI.Companion.Mod;

public sealed partial class ModEntry
{
    private bool TryOpenLifeMenuFromInteraction()
    {
        if (PlayerModeEnabled) return false;
        if (_actor == null || !Context.IsWorldReady) return false;

        // Tile coordinates are local to a map. An actor in the farmhouse must
        // not intercept interaction with an unrelated object at the same farm
        // coordinates (or vice versa).
        var location = Game1.currentLocation;
        if (location == null) return false;
        string locationName = !string.IsNullOrWhiteSpace(location.NameOrUniqueName)
            ? location.NameOrUniqueName : location.Name;
        if (!string.Equals(locationName, _actor.LocationName, StringComparison.OrdinalIgnoreCase)) return false;

        // Get player tile and facing
        var player = Game1.player;
        var playerTile = new Domain.TileCoordinate((int)player.Tile.X, (int)player.Tile.Y);
        var cursorTile = new Domain.TileCoordinate(
            (int)Game1.currentCursorTile.X,
            (int)Game1.currentCursorTile.Y);
        var companionTile = _actor.Tile;

        bool interactionPressed = true; // already inside OnButtonPressed guard

        if (_interactionDetector.ShouldOpenLifeMenu(
            playerTile, player.FacingDirection, cursorTile, companionTile, interactionPressed))
        {
            OpenLifeMenu();
            return Game1.activeClickableMenu != null;
        }
        return false;
    }

    /// <summary>
    /// Opens the <see cref="CompanionLifeMenu"/>.
    /// </summary>
    private void OpenLifeMenu()
    {
        if (_lifeMenuUiState.IsOnboarded || _lifeMenuUiState.IsSkipped) OpenCompanionConversation();
        else GetCompanionDialogue().Open();
    }

    private void OpenLifeDirections()
    {
        GetCompanionDialogue().OpenDirections();
    }

    private CompanionDialogueController GetCompanionDialogue()
    {
        _companionDialogue ??= new CompanionDialogueController(
            _lifeMenuUiState, DispatchLifeChat,
            () =>
            {
                DispatchLifeProfileRefresh(); DispatchLifeMilestonesRefresh();
                if (!_lifeMenuUiState.IsOnboarded && !_lifeMenuUiState.IsSkipped) DispatchMemoryListRefresh();
            },
            () =>
            {
                OpenSetupMenu();
                if (Game1.activeClickableMenu != null)
                    Game1.activeClickableMenu.exitFunction = () => _companionDialogue?.ReturnToConversation();
            },
            OpenCompanionMemory, () => _actor?.GameFarmer,
            SendFirstMeetingProfile,
            text => DispatchMemoryEdit("add", null, "preference", text) ? _lifeMenuUiState.PendingMemoryEditRequestId : null,
            () => !_lifeMenuUiState.WorkPaused && !_chatUiState.IsPaused && !_chatUiState.LocalPauseRequested &&
                !_chatUiState.HasActiveCommand && !_chatUiState.HasPendingControl &&
                !_lifeMenuUiState.IsChatPending && (_coordinator == null || _coordinator.GetActivityStatus() == "idle"),
            OpenTaskPanel, GetBridgeConnectionProblem, OpenCompanionConversation,
            () => OpenCompanionHub(0), () => OpenCompanionHub(2), () => OpenCompanionReplyInput(null));
        return _companionDialogue;
    }

    private void OpenTaskPanel()
    {
        OpenCompanionHub(1);
    }

    private void OpenCompanionConversation()
    {
        OpenCompanionConversationForNotice(null);
    }

    private void OpenCompanionConversationForNotice(string? noticeId)
    {
        DispatchLifeProfileRefresh();
        GetCompanionDialogue().OpenConversation((text, id, resolve) => DispatchLifeChat(text, "chat", null, id, resolve),
            () => GetCompanionDialogue().OpenF8(), noticeId);
    }

    private void OpenCompanionReplyInput(string? noticeId)
    {
        GetCompanionDialogue().OpenReplyInput((text, id, resolve) => DispatchLifeChat(text, "chat", null, id, resolve), noticeId);
    }

    private void OpenCompanionHub(int tab)
    {
        DispatchLifeProfileRefresh();
        var actions = new CompanionHubActions(RequestPauseMenuAction, RequestResumeMenuAction, RequestCancelMenuAction,
            ToggleAutonomyMode, SendLifeProfileSetOnly, OpenCompanionMemoryForDashboard, DispatchDecisionVisibility, OpenGameSettings);
        GetCompanionDialogue().OpenDashboard(actions, OpenCompanionReplyInput, tab);
    }

    private void OpenCompanionMemoryForDashboard()
    {
        OpenCompanionMemory();
        if (Game1.activeClickableMenu is CompanionLifeMenu)
            GetCompanionDialogue().OwnF8Child(() => GetCompanionDialogue().ReturnToDashboard(() => OpenCompanionHub(2)));
    }

    private readonly Dictionary<string, Dictionary<string, string>> _decisionEdits = new();
    private DateTime _decisionEditSentAt;
    private bool DispatchDecisionVisibility(IReadOnlyList<string> ids, bool dismiss)
    {
        if (_transportServer?.IsChatConnected != true || ids.Count == 0 || _decisionEdits.Count > 0) return false;
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string request = Guid.NewGuid().ToString("N")[..8];
        var previous = _lifeMenuUiState.Conversation.Where(e => e.DecisionId != null && ids.Contains(e.DecisionId))
            .ToDictionary(e => e.DecisionId!, e => e.DecisionStatus ?? "pending");
        _decisionEdits[request] = previous;
        _decisionEditSentAt = DateTime.UtcNow;
        foreach (string id in ids) _lifeMenuUiState.SetDecisionStatus(id, dismiss ? "dismissed" : "pending");
        var payload = new LifeChatSubmitPayload(request, saveId, "chat", dismiss ? "删除待决定事项" : "撤销删除",
            NoticeAction: dismiss ? "dismiss" : "restore", NoticeIds: ids);
        var server = _transportServer;
        _ = Task.Run(async () =>
        {
            try { if (await server.SendLifeChatSubmitAsync(payload).ConfigureAwait(false)) return; }
            catch (Exception ex) { Monitor.Log($"Decision visibility send failed: {ex.Message}", LogLevel.Warn); }
            HandleLifeChatReplyReceived(new LifeChatReplyPayload(request, saveId, "failed", 0, 0, ReplyText: "删除未确认，已恢复原显示。"));
        });
        return true;
    }

    private void OpenCompanionMemory()
    {
        if (Game1.activeClickableMenu is CompanionLifeMenu) return;

        Game1.activeClickableMenu = new CompanionLifeMenu(
            _lifeMenuUiState,
            onSubmitLifeChat: (text, mode) => DispatchLifeChat(text, mode),
            onOpenCommandMenu: prefill =>
            {
                CompanionConversationMenu.DraftText = prefill;
                OpenCompanionConversation();
            },
            onOpenSetup: OpenSetupMenu,
            onRefreshWork: DispatchLifeProfileRefresh,
            onRefreshMemory: DispatchMemoryListRefresh,
            onRefreshMilestones: DispatchLifeMilestonesRefresh,
            onMemoryEdit: (op, id, kind, text) => DispatchMemoryEdit(op, id, kind, text),
            onOpenDirections: OpenLifeDirections);

        if (Game1.activeClickableMenu is CompanionLifeMenu memoryMenu)
        {
            memoryMenu.OpenMemoryOnly();
            memoryMenu.exitFunction = () => _companionDialogue?.ReturnToConversation();
        }
        Monitor.Log("Companion memory panel opened.", LogLevel.Info);
    }

    /// <summary>
    /// Opens the <see cref="CompanionSetupMenu"/>.
    /// </summary>
    private void OpenSetupMenu()
    {
        Game1.activeClickableMenu = new CompanionSetupMenu(
            companionName: _lifeMenuUiState.CompanionName,
            playStyle: _lifeMenuUiState.PlayStyle,
            personality: _lifeMenuUiState.Personality,
            careFrequency: _lifeMenuUiState.CareFrequency,
            currentWorkMode: _lifeMenuUiState.WorkMode,
            liveState: _lifeMenuUiState,
            onSave: (name, style, pers, freq) => SendLifeProfileSetOnly(name, style, pers, freq),
            onStart: (name, style, pers, freq) => GetCompanionDialogue().SaveSettingsAndPlan(name, style, pers, freq),
            onSkip: () => SendLifeProfileSkip());
    }

    // -----------------------------------------------------------------------
    // Life-chat dispatch
    // -----------------------------------------------------------------------

    private void DispatchLifeProfileRefresh()
    {
        if (_transportServer == null || !_transportServer.IsChatConnected) return;
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string reqId = Guid.NewGuid().ToString("N")[..8];
        _ = Task.Run(async () =>
        {
            try { await _transportServer.SendLifeProfileGetAsync(new LifeProfileGetPayload(reqId, saveId)).ConfigureAwait(false); }
            catch (Exception ex) { Monitor.Log($"life.profile.get refresh failed: {ex.Message}", LogLevel.Warn); }
        });
    }

    private void DispatchLifeMilestonesRefresh()
    {
        if (_transportServer == null || !_transportServer.IsChatConnected) return;
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string reqId = Guid.NewGuid().ToString("N")[..8];
        _ = Task.Run(async () =>
        {
            try { await _transportServer.SendLifeMilestonesGetAsync(new LifeMilestonesGetPayload(reqId, saveId)).ConfigureAwait(false); }
            catch (Exception ex) { Monitor.Log($"life.milestones.get send failed: {ex.Message}", LogLevel.Warn); }
        });
    }

    private bool DispatchLifeChat(string text, string mode, string? acceptedNodeId = null)
        => DispatchLifeChat(text, mode, acceptedNodeId, null);

    private bool DispatchLifeChat(string text, string mode, string? acceptedNodeId, string? replyToNoticeId, bool resolveNotice = false)
    {
        bool Reject(string reason)
        {
            CompanionCommandMenu.DraftText = text;
            CompanionCommandMenu.CurrentStatusText = reason;
            CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", reason, Color.Red));
            ShowCompanionHud(new HUDMessage(reason, HUDMessage.error_type));
            Monitor.Log($"Player message not sent: {reason}", LogLevel.Warn);
            return false;
        }
        if (_transportServer == null || !_transportServer.IsChatConnected)
        {
                return Reject("消息尚未发送：伙伴服务正在连接，原文已保留。");
        }
        if (_coordinator == null || _observer == null)
            return Reject("消息尚未发送：请等待存档载入，原文已保留。");
        if (string.IsNullOrWhiteSpace(text)) return Reject("请先输入要对伙伴说的话。");
        // This callback runs on the game thread. Observe before handing the
        // conversation to a background provider, even while the game clock is held.
        var observation = _coordinator.CaptureCurrentSnapshot(_observer.WorldRevision);
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string reqId = Guid.NewGuid().ToString("N")[..8];
        if (!_lifeMenuUiState.BeginChat(reqId, mode, WorkControlDirective.IsPriority(text)))
            return Reject("待回复的消息较多，这条尚未发送，原文已保留。仍可使用暂停或取消。");
        _lifeMenuUiState.RecordConversation(reqId + ":player", "你", text, GetCurrentGameDate(), isPlayer: true, replyToDecisionId: replyToNoticeId);
        _lifeControls.Begin(reqId);
        CompanionCommandMenu.CurrentStatusText = "消息已发送，等待伙伴接收";
        ShowCompanionHud(new HUDMessage("消息已发送，回复会保留在历史记录里。"));
        Monitor.Log($"Player message sent through life chat: requestId={reqId} mode={mode}", LogLevel.Info);

        var payload = new LifeChatSubmitPayload(reqId, saveId, mode, text, AcceptedNodeId: acceptedNodeId, ReplyToNoticeId: replyToNoticeId, ResolveNotice: resolveNotice);
        int profileRevision = _lifeMenuUiState.ProfileRevision;
        int memoryRevision = _lifeMenuUiState.MemoryRevision;
        if (mode == "plan") CompanionCommandMenu.TaskState.Begin(acceptedNodeId == null ? "正在结合农场现状商量方案。" : "正在保存你认可的方案。", planning: true);
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _transportServer.SendLifeChatSubmitAsync(payload, observation).ConfigureAwait(false)) return;
            }
            catch (Exception ex) { Monitor.Log($"life.chat.submit send failed: {ex.Message}", LogLevel.Warn); }
            HandleLifeChatReplyReceived(new LifeChatReplyPayload(reqId, saveId, "failed", profileRevision, memoryRevision,
                ReplyText: "这条消息没能送到伙伴服务。连接恢复后可以重试。"));
        });
        return true;
    }

    // -----------------------------------------------------------------------
    // Memory edit dispatch
    // -----------------------------------------------------------------------

    private void DispatchMemoryListRefresh()
    {
        if (_transportServer == null || !_transportServer.IsChatConnected) return;
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string reqId = Guid.NewGuid().ToString("N")[..8];
        _ = Task.Run(async () =>
        {
            try { await _transportServer.SendLifeMemoryListAsync(new LifeMemoryListPayload(reqId, saveId)).ConfigureAwait(false); }
            catch (Exception ex) { Monitor.Log($"life.memory.list send failed: {ex.Message}", LogLevel.Warn); }
        });
    }

    private bool DispatchMemoryEdit(string op, string? id, string? kind, string? text)
    {
        if (_transportServer == null || !_transportServer.IsChatConnected) return false;
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string reqId = Guid.NewGuid().ToString("N")[..8];
        _lifeMenuUiState.BeginMemoryEdit(reqId);

        var payload = new LifeMemoryEditPayload(reqId, saveId, _lifeMenuUiState.MemoryRevision, op, id, kind, text);
        _ = Task.Run(async () =>
        {
            try { await _transportServer.SendLifeMemoryEditAsync(payload).ConfigureAwait(false); }
            catch (Exception ex) { Monitor.Log($"life.memory.edit send failed: {ex.Message}", LogLevel.Warn); }
        });
        return true;
    }

    // -----------------------------------------------------------------------
    // Profile set helpers
    // -----------------------------------------------------------------------

    private void SendLifeProfileSetOnly(string name, string style, string personality, string careFreq)
    {
        if (_transportServer == null) return;
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string reqId = Guid.NewGuid().ToString("N")[..8];
        int expected = _lifeMenuUiState.ProfileRevision;
        _lifeMenuUiState.BeginProfileSet(reqId, expected);

        var patch = new LifeProfilePatchDto(
            CompanionName: name,
            PlayStyle: style,
            Personality: personality,
            CareFrequency: careFreq);
        var payload = new LifeProfileSetPayload(reqId, saveId, expected, patch);
        _ = Task.Run(async () =>
        {
            try { await _transportServer.SendLifeProfileSetAsync(payload).ConfigureAwait(false); }
            catch (Exception ex) { Monitor.Log($"life.profile.set send failed: {ex.Message}", LogLevel.Warn); }
        });
    }

    private string? SendFirstMeetingProfile(LifeProfilePatchDto patch)
    {
        if (_transportServer?.IsChatConnected != true || !_lifeMenuUiState.HasProfileState ||
            _lifeMenuUiState.PendingProfileSetRequestId != null) return null;
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string reqId = Guid.NewGuid().ToString("N")[..8];
        int expected = _lifeMenuUiState.ProfileRevision;
        _lifeMenuUiState.BeginProfileSet(reqId, expected);
        _ = Task.Run(async () =>
        {
            bool sent;
            try { sent = await _transportServer.SendLifeProfileSetAsync(new LifeProfileSetPayload(reqId, saveId, expected, patch)).ConfigureAwait(false); }
            catch (Exception ex) { Monitor.Log($"First meeting save failed: {ex.Message}", LogLevel.Warn); sent = false; }
            if (!sent) _mainThreadActions.Enqueue(() =>
            {
                _lifeMenuUiState.EndProfileSet(reqId);
                _companionDialogue?.ReceiveProfile(reqId, "failed");
            });
        });
        return reqId;
    }

    private void SendLifeProfileSkip()
    {
        if (_transportServer == null) return;
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string reqId = Guid.NewGuid().ToString("N")[..8];
        int expected = _lifeMenuUiState.ProfileRevision;
        _lifeMenuUiState.BeginProfileSet(reqId, expected);

        var patch = new LifeProfilePatchDto(Skipped: true);
        var payload = new LifeProfileSetPayload(reqId, saveId, expected, patch);
        _ = Task.Run(async () =>
        {
            try { await _transportServer!.SendLifeProfileSetAsync(payload).ConfigureAwait(false); }
            catch (Exception ex) { Monitor.Log($"life.profile.set(skip) send failed: {ex.Message}", LogLevel.Warn); }
        });
    }

    /// <summary>
    /// §1.8 start sequence, ack-chained: ① persist profile (onboarded=true), wait for
    /// life.profile.state confirmed; ② autonomy set_preferences(goal), wait for ack;
    /// ③ autonomy set_mode(free), wait for ack. The goal is confirmed before free
    /// mode so autonomy never dispatches work under a stale goal. The final HUD
    /// celebration only fires after all three confirmations; any rejection/timeout
    /// aborts with the real state kept and a retry hint (setup menu can re-trigger).
    /// Ordering/stepping lives in <see cref="LifeStartSequence"/>; this method only
    /// wires sends and rendering.
    /// </summary>
    private void SendLifeStart(string name, string style, string personality, string careFreq)
    {
        if (_transportServer == null) return;
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string reqId = Guid.NewGuid().ToString("N")[..8];
        int expected = _lifeMenuUiState.ProfileRevision;
        _lifeMenuUiState.BeginProfileSet(reqId, expected);

        _lifeStartSequence.Begin(saveId, name, style, reqId);
        _lifeStartSequence.StepSentAtUtc = DateTime.UtcNow;

        var patch = new LifeProfilePatchDto(
            Onboarded: true,
            Skipped: false,
            CompanionName: name,
            PlayStyle: style,
            Personality: personality,
            CareFrequency: careFreq);
        var profilePayload = new LifeProfileSetPayload(reqId, saveId, expected, patch);

        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await _transportServer.SendLifeProfileSetAsync(profilePayload).ConfigureAwait(false);
                if (!ok) _mainThreadActions.Enqueue(() => AbortLifeStart("设置保存未送达，请检查连接。"));
            }
            catch (Exception ex)
            {
                Monitor.Log($"life.profile.set(start) send failed: {ex.Message}", LogLevel.Warn);
                _mainThreadActions.Enqueue(() => AbortLifeStart("设置保存发送失败，请检查连接。"));
            }
        });
    }

    private void AdvanceLifeStartAfterProfile(string requestId, string status, string? reason)
    {
        var result = _lifeStartSequence.ApplyProfileState(requestId, status, reason);
        switch (result)
        {
            case LifeStartAdvance.Ignore:
                return;
            case LifeStartAdvance.Abort:
                AbortLifeStart(_lifeStartSequence.AbortReason ?? "设置未确认。");
                return;
            case LifeStartAdvance.SendGoalPreference:
                SendLifeStartControl(result, new JsonObject { ["goal"] = _lifeStartSequence.Goal }, "目标设置");
                return;
        }
    }

    private void AdvanceLifeStartAfterControl(string requestId, string status)
    {
        var result = _lifeStartSequence.ApplyControlAck(requestId, status);
        switch (result)
        {
            case LifeStartAdvance.Ignore:
                return;
            case LifeStartAdvance.Abort:
                AbortLifeStart(_lifeStartSequence.AbortReason ?? "设置未确认。");
                return;
            case LifeStartAdvance.SendModeFree:
                SendLifeStartControl(result, new JsonObject { ["mode"] = "free" }, "主动帮忙开启");
                return;
            case LifeStartAdvance.Complete:
                ShowCompanionHud(new HUDMessage($"和{_lifeStartSequence.CompanionName}一起生活开始了！"));
                // The control ack is authoritative, but the setup/life menus read
                // their mode and plan from life.profile.state. Refresh that view now.
                DispatchLifeProfileRefresh();
                return;
        }
    }

    /// <summary>
    /// Sends one §1.8 autonomy control step. The sequence has already advanced to
    /// the matching await-step; a send failure aborts with the real state kept.
    /// </summary>
    private void SendLifeStartControl(LifeStartAdvance step, JsonObject parameters, string failureNoun)
    {
        string action = step == LifeStartAdvance.SendGoalPreference ? "set_preferences" : "set_mode";
        string reqId = step == LifeStartAdvance.SendGoalPreference
            ? _lifeStartSequence.GoalRequestId!
            : _lifeStartSequence.ModeRequestId!;
        var payload = new AutonomyControlPayload(reqId, _lifeStartSequence.SaveId ?? string.Empty, action, parameters);
        _lifeStartSequence.StepSentAtUtc = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await _transportServer!.SendAutonomyControlAsync(payload).ConfigureAwait(false);
                if (!ok) _mainThreadActions.Enqueue(() => AbortLifeStart($"{failureNoun}未送达，请检查连接。"));
            }
            catch (Exception ex)
            {
                Monitor.Log($"life.start {action} send failed: {ex.Message}", LogLevel.Warn);
                _mainThreadActions.Enqueue(() => AbortLifeStart($"{failureNoun}发送失败，请检查连接。"));
            }
        });
    }

    private void AbortLifeStart(string reason)
    {
        if (_lifeStartSequence.Step == LifeStartStep.Idle) return;
        _lifeStartSequence.Abort();
        ShowCompanionHud(new HUDMessage($"开始一起生活未完成：{reason} 游戏内状态保持真实结果，可在「伙伴设置」中重试。", HUDMessage.error_type));
    }

    // -----------------------------------------------------------------------
    // Life event handlers (called from main thread via event dispatch in Update)
    // -----------------------------------------------------------------------

    private void HandleLifeChatReplyReceived(LifeChatReplyPayload reply)
    {
        _mainThreadActions.Enqueue(() =>
        {
            if (_conversationSaveId != reply.SaveId) return;
            if (_decisionEdits.Remove(reply.RequestId, out var previous))
            {
                if (reply.Status != "completed")
                {
                    foreach (var entry in previous) _lifeMenuUiState.SetDecisionStatus(entry.Key, entry.Value);
                    ShowCompanionHud(new HUDMessage(reply.ReplyText ?? "删除未确认，已恢复显示。", HUDMessage.error_type));
                }
                DispatchLifeProfileRefresh();
                return;
            }
            if (reply.AnsweredNoticeId != null)
                _lifeMenuUiState.ConfirmDecisionReply(reply.AnsweredNoticeId);
            bool accepted = _lifeMenuUiState.ApplyChatReply(
                reply.RequestId,
                reply.Status,
                reply.ReplyText,
                reply.QueuePosition,
                reply.Error,
                reply.ProfileRevision,
                reply.MemoryRevision);
            if (accepted)
            {
                _companionDialogue?.Receive(reply.RequestId, reply.Status, reply.ReplyText,
                    reply.ProposalReady, reply.ProposalNodeId, reply.ReplyAlreadyDelivered);
                if (reply.Status == "queued")
                    ShowCompanionHud(new HUDMessage("伙伴已收到消息，正在排队处理；不用重复发送。"));
                if (reply.Status is "completed" or "failed")
                {
                    bool visible = Game1.activeClickableMenu is CompanionConversationMenu or CompanionLifeMenu { IsChatVisible: true };
                    if (reply.Status == "completed" && reply.AnsweredNoticeId != null)
                        _lifeMenuUiState.ConfirmDecisionReply(reply.AnsweredNoticeId);
                    string text = !string.IsNullOrWhiteSpace(reply.ReplyText) ? reply.ReplyText
                        : !string.IsNullOrWhiteSpace(reply.Error) ? $"这次没能回复：{reply.Error}" : "这次没有收到回复，请再试一次。";
                    if (!reply.ReplyAlreadyDelivered && _lifeMenuUiState.RecordCompanionReply(reply.RequestId,
                        CompanionDialogueInbox.PlainText(text), GetCurrentGameDate()))
                    {
                        if (!_desktopMode && !visible)
                        {
                            Game1.playSound("newArtifact");
                            ShowCompanionHud(new HUDMessage($"{_lifeMenuUiState.CompanionName}回复了你 · 请看聊天栏", HUDMessage.newQuest_type));
                        }
                    }
                    DispatchLifeProfileRefresh();
                }
                if (reply.Activity != null)
                    CompanionCommandMenu.TaskState.ApplyActivity(reply.Activity.Phase, reply.Activity.Summary, reply.Activity.NextStep);
            }

            Monitor.Log($"life.chat.reply: status={reply.Status} reqId={reply.RequestId}", LogLevel.Debug);
        });
    }

    private void PersistConversation()
    {
        if (_conversationSaveId == null || _conversationStore == null) return;
        try { _conversationStore.Save(_conversationSaveId, _lifeMenuUiState.Conversation); }
        catch (Exception ex) { Monitor.Log($"Conversation history could not be saved: {ex.Message}", LogLevel.Warn); }
    }

    private void HandleLifeProfileStateReceived(LifeProfileStatePayload state)
    {
        _mainThreadActions.Enqueue(() =>
        {
            string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
            if (!string.Equals(state.SaveId, saveId, StringComparison.Ordinal)) return;

            bool isStandaloneSaveReply = _lifeStartSequence.Step == LifeStartStep.Idle &&
                state.RequestId == _lifeMenuUiState.PendingProfileSetRequestId;
            _lifeMenuUiState.MarkProfileStateReceived();
            _lifeMenuUiState.ApplyWorkProjection(
                CompanionTaskPanelState.CurrentGoalText(state.Work),
                state.Work.ActiveGoals?.Where(CompanionTaskPanelState.IsActiveGoal).Select(CompanionTaskPanelState.GoalText) ?? Enumerable.Empty<string>(),
                state.Work.RecentTodos?.Select(todo => todo.Intent) ?? Enumerable.Empty<string>(),
                state.Work.WaitingConditions ?? Enumerable.Empty<string>(),
                state.Work.PlanWaitReason, state.Work.Mode, state.Work.Paused);
            if (state.Work.PlayerDecisions != null)
            {
                _lifeMenuUiState.ReconcileDecisionIds(state.Work.PlayerDecisions.Select(d => d.Id));
                _nativeChatDecisionSave = saveId;
            }
            foreach (var decision in state.Work.PlayerDecisions ?? Enumerable.Empty<PlayerDecisionDto>())
            {
                if (decision.Status == "pending")
                    _lifeMenuUiState.TryAddDecisionNotice(saveId, saveId, decision.Id, GetCurrentGameDate(), decision.Message, decision.Choices);
                if (!_decisionEdits.Values.Any(p => p.ContainsKey(decision.Id)))
                    _lifeMenuUiState.SetDecisionStatus(decision.Id, decision.Status);
            }
            CompanionCommandMenu.AutonomyMode = state.Work.Mode;
            if (state.Status is "ok" or "confirmed")
            {
                bool wasPaused = _chatUiState.IsPaused;
                if (_chatUiState.ApplyWorkPausedProjection(state.Work.Paused) && wasPaused != _chatUiState.IsPaused)
                    _chatActivityAt = DateTime.UtcNow;
                ProjectChatUiState();
            }

            if (state.Profile != null)
            {
                bool bedtimeChanged = _lifeMenuUiState.ApplyBedtime(state.Profile.Bedtime, _coordinator?.Rest.Bedtime);
                if (_coordinator != null) _coordinator.Rest.Bedtime = _lifeMenuUiState.Bedtime;
                if (bedtimeChanged) PersistActorState("bedtime preference");
                _lifeMenuUiState.ApplyProfileState(
                    state.Profile.Onboarded,
                    state.Profile.Skipped,
                    state.Profile.CompanionName,
                    state.Profile.PlayStyle,
                    state.Profile.Personality,
                    state.Profile.CareFrequency,
                    state.ProfileRevision,
                    state.Work.Mode,
                    state.Work.Paused,
                    // Keep the memory revision already learned from life.memory.state;
                    // profile.state does not carry it.
                    memoryRevision: _lifeMenuUiState.MemoryRevision,
                    requestId: state.RequestId);

                // HUD onboarding nudge: not onboarded and not skipped
                if (!state.Profile.Onboarded && !state.Profile.Skipped && !_lifeOnboardingHudShown)
                {
                    _lifeOnboardingHudShown = true;
                    ShowCompanionHud(new HUDMessage("新伙伴想认识你，走近按互动键聊聊吧。"));
                }
            }
            else
            {
                // profile null = not onboarded yet
                if (!_lifeOnboardingHudShown)
                {
                    _lifeOnboardingHudShown = true;
                    ShowCompanionHud(new HUDMessage("新伙伴想认识你，走近按互动键聊聊吧。"));
                }
            }

            AdvanceLifeStartAfterProfile(state.RequestId, state.Status, state.Reason);
            CompanionCommandMenu.TaskState.ApplyProjection(_lifeMenuUiState.PlayStyle,
                CompanionTaskPanelState.CurrentGoalText(state.Work),
                state.Work.WaitingConditions ?? new List<string>(), state.Work.PlanWaitReason, _chatUiState.IsPaused,
                state.Work.PauseReason, state.Work.UpdatedAt,
                state.Work.ActiveGoals?.Where(CompanionTaskPanelState.IsActiveGoal).Select(CompanionTaskPanelState.GoalText));
            CompanionCommandMenu.TaskState.ApplyExecutionHistory(state.Work.RecentExecutions);
            if (state.Work.Activity != null)
                CompanionCommandMenu.TaskState.ApplyActivity(state.Work.Activity.Phase, state.Work.Activity.Summary, state.Work.Activity.NextStep);
            _lifeMenuUiState.EndProfileSet(state.RequestId);
            _companionDialogue?.ReceiveProfile(state.RequestId, state.Status);

            if (isStandaloneSaveReply)
            {
                string message = state.Status == "confirmed"
                    ? "伙伴设置已保存。"
                    : state.Reason == "STALE_REVISION"
                        ? "伙伴设置已变化，请重新打开设置后重试。"
                        : "伙伴设置未保存，请重试。";
                ShowCompanionHud(new HUDMessage(message,
                    state.Status == "confirmed" ? HUDMessage.newQuest_type : HUDMessage.error_type));
            }

            Monitor.Log($"life.profile.state received: revision={state.ProfileRevision} onboarded={state.Profile?.Onboarded}", LogLevel.Debug);
        });
    }

    private void HandleLifeMemoryStateReceived(LifeMemoryStatePayload state)
    {
        _mainThreadActions.Enqueue(() =>
        {
            string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
            if (!string.Equals(state.SaveId, saveId, StringComparison.Ordinal)) return;

            bool isEditReply = state.RequestId == _lifeMenuUiState.PendingMemoryEditRequestId;
            _lifeMenuUiState.ApplyMemoryState(
                state.MemoryRevision,
                state.Entries.Select(e => new Menus.MemoryEntrySnapshot(
                    e.Id, e.Kind, e.Text, e.Source, e.GameDate, e.CreatedAt)),
                state.RequestId, state.Status, state.Reason);
            _companionDialogue?.ReceiveMemory(state.RequestId, state.Status);
            if (isEditReply && _lifeMenuUiState.MemoryEditFeedback != null)
                ShowCompanionHud(new HUDMessage(_lifeMenuUiState.MemoryEditFeedback,
                    state.Status == "confirmed" ? HUDMessage.newQuest_type : HUDMessage.error_type));

            Monitor.Log($"life.memory.state received: revision={state.MemoryRevision} count={state.Entries.Count}", LogLevel.Debug);
        });
    }

    private void HandleLifeMilestonesStateReceived(LifeMilestonesStatePayload state)
    {
        _mainThreadActions.Enqueue(() =>
        {
            string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
            if (!string.Equals(state.SaveId, saveId, StringComparison.Ordinal)) return;

            if (!string.Equals(state.Status, "ok", StringComparison.Ordinal))
            {
                // Keep the previous snapshot; a failed refresh must not wipe known nodes.
                Monitor.Log($"life.milestones.state failed: {state.Error}", LogLevel.Debug);
                return;
            }

            _lifeMenuUiState.ApplyMilestoneState((state.Nodes ?? new List<MilestoneNodeDto>())
                .Select(n => new Menus.MilestoneNodeSnapshot(
                    n.Id,
                    n.Title,
                    n.Status,
                    n.Verification,
                    n.TargetDate,
                    n.DaysUntil,
                    n.Summary,
                    n.SourceUrl,
                    (n.PrepItems ?? new List<MilestonePrepItemDto>())
                        .Select(p => new Menus.MilestonePrepItemSnapshot(p.Key, p.Label, p.Support, p.Status, p.Note))
                        .ToArray(),
                    n.ReservedFunds,
                    n.PlannedCount,
                    n.TermsNote,
                    n.UpdatedAt)));

            Monitor.Log($"life.milestones.state received: gameDate={state.GameDate} count={state.Nodes?.Count ?? 0} proactive={string.IsNullOrEmpty(state.RequestId)}", LogLevel.Debug);
        });
    }

    private void HandleLifeCareReceived(LifeCarePayload care)
    {
        _mainThreadActions.Enqueue(() =>
        {
            string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
            if (!string.Equals(care.SaveId, saveId, StringComparison.Ordinal)) return;

            bool menuOpen = Game1.activeClickableMenu != null;
            bool eventPlaying = Game1.eventUp;
            bool blocked = menuOpen || eventPlaying;

            var hint = _careHintController.ReceiveCareHint(
                care.EventKey, care.GameDate, care.Kind, care.Text, blocked);

            if (hint != null)
            {
                // Add to life-menu unread list
                _lifeMenuUiState.AddUnreadCareHint(hint);
                // Show low-intrusion HUD message
                if (_lifeMenuUiState.Conversation.Any(e => e.Id == "care:" + hint.EventKey && e.Unread))
                    ShowCompanionHud(new HUDMessage($"{_lifeMenuUiState.CompanionName}有新消息 · 请看聊天栏"));
            }
            else if (blocked && _careHintController.PendingHints.Any(h => h.EventKey == care.EventKey))
            {
                // Still add to pending-read list even if deferred display
                _lifeMenuUiState.AddUnreadCareHint(
                    new Domain.PendingCareHint(care.EventKey, care.GameDate, care.Kind, care.Text));
            }

            Monitor.Log($"life.care received: eventKey={care.EventKey} kind={care.Kind} blocked={blocked}", LogLevel.Debug);
        });
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private static string GetCurrentGameDate()
    {
        if (!Context.IsWorldReady) return string.Empty;
        return $"{Game1.year}:{Game1.currentSeason}:{Game1.dayOfMonth}";
    }
}
