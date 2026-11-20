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
    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        DrawNativeChatSend(e.SpriteBatch);
        _companionHudBounds = Microsoft.Xna.Framework.Rectangle.Empty;
        if (!Context.IsWorldReady || _actor == null || _coordinator == null)
            return;
        if (Game1.activeClickableMenu != null || Game1.eventUp)
            return;

        // Compact status panel instead of one long line: detailed instructions are
        // shown once in the readiness HUDMessage, so this only carries the live
        // state plus the two discoverable keys (F8 chat, takeover key).
        string stateLine;
        string hintLine;
        if (PlayerModeEnabled)
        {
            bool agentOwns = _authority?.AgentOwns == true;
            string activity = _coordinator.GetActivityStatus();
            string? problem = GetBridgeConnectionProblem();
            // Distinguish: launcher/bridge failure, starting, not connected, working,
            // conversation in flight, idle waiting for a goal, human-owned pause.
            string state = problem != null ? problem.TrimEnd('。')
                : activity != "idle" ? "执行中 " + activity
                : _lifeMenuUiState.IsChatPending ? "对话处理中，请稍候"
                : agentOwns ? "等待目标"
                : "已接管，AI 暂停行动";
            if (_preset is { Enabled: true } preset)
            {
                // The autostart test never sits at a bare "waiting for a goal": surface
                // its real lifecycle (preparing/planning/acting/done/failed) instead.
                state = preset.Phase switch
                {
                    PresetGoalPhase.Done => preset.HudLabel(),
                    PresetGoalPhase.Failed => preset.HudLabel() + (preset.FailureReason != null ? "：" + preset.FailureReason : ""),
                    _ when !agentOwns => $"自动测试·已暂停（按 {_config.TakeoverKey} 交还 AI 继续）",
                    _ when problem != null => "自动测试·准备中（" + problem.TrimEnd('。') + "）",
                    _ when activity != "idle" => "自动测试·执行中 " + activity,
                    _ => preset.HudLabel(),
                };
            }
            if (_thinking?.FailureReason is string failureReason)
                state = "程序停止·时间已保护暂停（" + failureReason + "）";
            stateLine = (agentOwns ? "玩家由 AI 控制" : "玩家由你控制") + " · " + state;
            hintLine = agentOwns ? $"F8 记录 · {_config.TakeoverKey} 接管" : $"F8 记录 · {_config.TakeoverKey} 交还 AI";
        }
        else
        {
            string activity = _coordinator.GetActivityStatus();
            var task = CompanionCommandMenu.TaskState;
            string? problem = GetBridgeConnectionProblem();
            string state = problem != null ? problem.TrimEnd('。')
                : _chatUiState.ShouldPauseNative || _lifeMenuUiState.WorkPaused ? "工作已暂停"
                : _coordinator.Rest.IsResting ? _coordinator.GetActivityLabel()
                : activity != "idle" ? _coordinator.GetActivityLabel()
                : _lifeMenuUiState.IsChatPending ? _lifeMenuUiState.PendingChatDescription
                : _lifeMenuUiState.UnreadReplyCount > 0 ? "有新回复"
                : "等你安排";
            stateLine = _lifeMenuUiState.CompanionName + " · " + state;
            hintLine = "点击交谈 · F8";
        }
        if (_lifeMenuUiState.UnreadReplyCount > 0)
            hintLine = (hintLine.Length > 0 ? hintLine + " · " : "") + $"{_lifeMenuUiState.UnreadReplyCount} 条新回复";

        // Keep the panel clear of the native HUD at any viewport size. Prefer the
        // actual on-screen menu bounds; fall back to the known layout (top toolbar
        // centered, 800px wide in UI space; clock/money box top-right).
        const int margin = 16;
        const int padding = 12;
        const int toolbarHalfWidth = 400;
        const int fallbackToolbarHeight = 144;
        const int minTextWidth = 200;
        var font = Game1.smallFont;
        int viewportW = Game1.uiViewport.Width;
        int viewportH = Game1.uiViewport.Height;
        var toolbar = Game1.onScreenMenus.OfType<StardewValley.Menus.Toolbar>().FirstOrDefault();
        var moneyBox = Game1.onScreenMenus.OfType<StardewValley.Menus.DayTimeMoneyBox>().FirstOrDefault();
        int toolbarLeft = toolbar?.xPositionOnScreen ?? viewportW / 2 - toolbarHalfWidth;
        int toolbarBottom = toolbar != null ? toolbar.yPositionOnScreen + toolbar.height : fallbackToolbarHeight;

        string text = hintLine.Length > 0 ? stateLine + "\n" + hintLine : stateLine;

        // Wide viewports (e.g. 1920): compact panel top-left, left of the toolbar.
        // Narrow viewports where the toolbar leaves no usable left strip (e.g. 1024:
        // toolbar starts at x112 but the panel needs 16..240): drop below the
        // toolbar instead and stay clear of the clock/money box on the right.
        int boxY = margin;
        int maxTextWidth = toolbarLeft - margin * 2 - padding * 2;
        if (maxTextWidth < minTextWidth)
        {
            boxY = toolbarBottom + margin;
            int rightLimit = moneyBox != null && moneyBox.xPositionOnScreen > margin + minTextWidth + padding * 2
                ? moneyBox.xPositionOnScreen - margin
                : viewportW - margin;
            maxTextWidth = rightLimit - margin - padding * 2;
        }
        maxTextWidth = Math.Max(64, Math.Min(maxTextWidth, viewportW - margin * 2 - padding * 2));
        int lineHeight = Math.Max(1, (int)Math.Ceiling((double)font.LineSpacing));
        if (viewportH - boxY - margin < padding * 2 + lineHeight)
            boxY = margin; // viewport too short to sit below the toolbar
        int maxTextHeight = Math.Max(lineHeight, viewportH - boxY - margin - padding * 2);

        // Wrap per-character under the active zh locale, then bound the height:
        // very long failure reasons are clipped to the lines that fit the
        // viewport, with an ellipsis marking the truncation.
        text = Game1.parseText(text, font, maxTextWidth);
        string[] lines = text.Split('\n');
        int maxLines = Math.Max(1, maxTextHeight / lineHeight);
        if (lines.Length > maxLines)
        {
            string last = lines[maxLines - 1];
            while (last.Length > 0 && font.MeasureString(last + "…").X > maxTextWidth)
                last = last.Substring(0, last.Length - 1);
            string[] kept = lines.Take(maxLines).ToArray();
            kept[maxLines - 1] = last + "…";
            text = string.Join("\n", kept);
        }

        Microsoft.Xna.Framework.Vector2 textSize = font.MeasureString(text);
        int boxWidth = (int)Math.Ceiling(textSize.X) + padding * 2;
        int boxHeight = (int)Math.Ceiling(textSize.Y) + padding * 2;
        _companionHudBounds = new Microsoft.Xna.Framework.Rectangle(margin, boxY, boxWidth, boxHeight);

        StardewValley.Menus.IClickableMenu.drawTextureBox(e.SpriteBatch, Game1.menuTexture,
            new Microsoft.Xna.Framework.Rectangle(0, 256, 60, 60), margin, boxY, boxWidth, boxHeight, Color.White * 0.75f, 1f, false);
        Utility.drawTextWithShadow(e.SpriteBatch, text, font, new Microsoft.Xna.Framework.Vector2(margin + padding, boxY + padding), Color.White);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!_desktopMode && Context.IsWorldReady && !Context.IsMultiplayer && e.Button == SButton.T
            && Game1.activeClickableMenu == null && !Game1.eventUp && Game1.chatBox?.isActive() == false)
        {
            Game1.chatBox.activate();
            Helper.Input.Suppress(e.Button);
            return;
        }

        if (!_desktopMode && Context.IsWorldReady && (PlayerModeEnabled || (_actor != null && _coordinator != null))
            && Game1.activeClickableMenu == null && !Game1.eventUp && Game1.displayHUD
            && e.Button == SButton.MouseLeft
            && _companionHudBounds.Contains(Game1.getMouseX(true), Game1.getMouseY(true)))
        {
            Helper.Input.Suppress(e.Button);
            Game1.chatBox?.activate();
            return;
        }
        if (PlayerModeEnabled && Context.IsWorldReady && e.Button.ToString() == _config.TakeoverKey) {
            // Dedicated takeover toggle: the ONLY input that transfers player authority.
            if (_authority?.AgentOwns == true)
            {
                ReleasePlayerControl("human-takeover");
                ShowCompanionHud(new HUDMessage($"已接管玩家：AI 行动已停止；再按 {_config.TakeoverKey} 交还 AI。", HUDMessage.newQuest_type));
            }
            else if (!_desktopMode && _authority != null)
            {
                _playerControlApproved = true;
                _authority.RequestAgent("human-explicit-handoff");
                ShowCompanionHud(new HUDMessage("已交还控制权给 AI：在聊天栏告诉它今天要做什么。", HUDMessage.newQuest_type));
            }
            Helper.Input.Suppress(e.Button); return;
        }
        // While the AI owns the player, world-directed human input (tool/action clicks,
        // movement keys) is swallowed instead of secretly acting alongside the AI or
        // revoking its authority. Menus and other UI keep working normally.
        if (PlayerModeEnabled && Context.IsWorldReady && _authority?.AgentOwns == true && Game1.activeClickableMenu == null
            && (e.Button.IsUseToolButton() || e.Button.IsActionButton() || e.Button is SButton.W or SButton.A or SButton.S or SButton.D or SButton.Up or SButton.Down or SButton.Left or SButton.Right))
        {
            Helper.Input.Suppress(e.Button);
            return;
        }
        if (_desktopMode) return;
        if (Context.IsWorldReady && e.Button.IsActionButton())
            Monitor.Log($"[CompanionInteraction] button={e.Button} playerMap={Game1.currentLocation?.NameOrUniqueName} playerPos={Game1.player.Position} playerTile={Game1.player.Tile} actorMap={_actor?.LocationName} actorTile={_actor?.Tile} cursor={Game1.currentCursorTile} free={Game1.activeClickableMenu == null}", LogLevel.Trace);
        if (e.Button == SButton.Escape && Context.IsWorldReady && _companionDialogue?.HandleEscape() == true)
        {
            Helper.Input.Suppress(e.Button);
            return;
        }

        // Life menu: detect player interacting with companion
        if (e.Button.IsActionButton() && Context.IsWorldReady && _actor != null && Game1.activeClickableMenu == null)
        {
            if (TryOpenLifeMenuFromInteraction())
            {
                Helper.Input.Suppress(e.Button);
                return;
            }
        }

        if (e.Button == SButton.F8)
        {
            if (!Context.IsWorldReady || _actor == null || _coordinator == null)
            {
                return;
            }

            if (Game1.activeClickableMenu != null)
            {
                if (_companionDialogue?.HandleF8() == true)
                {
                    Helper.Input.Suppress(e.Button);
                }
                else if (Game1.activeClickableMenu is CompanionConversationMenu)
                {
                    ((CompanionConversationMenu)Game1.activeClickableMenu).OpenRecords();
                    Helper.Input.Suppress(e.Button);
                }
                else if (Game1.activeClickableMenu is CompanionDashboardMenu)
                {
                    Game1.activeClickableMenu.exitThisMenu(playSound: false);
                    Helper.Input.Suppress(e.Button);
                }
                else if (Game1.activeClickableMenu is CompanionCommandMenu)
                {
                    ((CompanionCommandMenu)Game1.activeClickableMenu).PreserveDraft();
                    Game1.activeClickableMenu.exitThisMenu(playSound: false);
                }
                return;
            }

            Helper.Input.Suppress(e.Button);
            OpenCompanionOptions("F8");
        }
    }

    private void OpenCompanionOptions(string source)
    {
        TryAutoStartChatBridge();
        GetCompanionDialogue().OpenF8();
        Monitor.Log($"Companion options opened ({source}).", LogLevel.Info);
        ProjectBridgeConnection();
    }

    private bool ExecuteInGameCommand(string rawInput)
    {
        Monitor.Log($"In-game command submitted: '{rawInput}'", LogLevel.Info);

        if (CompanionCommandHandler.TryParseInGameCommand(rawInput, out var parsed, out _))
        {
            switch (parsed!.Type)
            {
                case InGameCommandType.Water:
                    if (DispatchWaterZone(parsed.X, parsed.Y, parsed.Radius, "menu", out string waterMsg))
                    {
                        ShowCompanionHud(new HUDMessage(waterMsg));
                        Monitor.Log(waterMsg, LogLevel.Info);
                        CompanionCommandMenu.ChatHistory.Add(new ChatMessage("伙伴", waterMsg, Microsoft.Xna.Framework.Color.DarkGreen));
                    }
                    else
                    {
                        ShowCompanionHud(new HUDMessage(waterMsg));
                        Monitor.Log(waterMsg, LogLevel.Warn);
                        CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", waterMsg, Microsoft.Xna.Framework.Color.Red));
                    }
                    CompanionCommandMenu.IsProcessing = false;
                    CompanionCommandMenu.CurrentStatusText = "原生指令处理结果";
                    break;

                case InGameCommandType.Pause:
                    RequestPauseMenuAction();
                    break;

                case InGameCommandType.Resume:
                    RequestResumeMenuAction();
                    break;

                case InGameCommandType.Cancel:
                    RequestCancelMenuAction();
                    break;

                case InGameCommandType.Status:
                    string summary = GetStatusSummary();
                    ShowCompanionHud(new HUDMessage(summary));
                    CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", summary, Microsoft.Xna.Framework.Color.Teal));
                    CompanionCommandMenu.IsProcessing = false;
                    CompanionCommandMenu.CurrentStatusText = "就绪";
                    break;
            }
            return true;
        }

        // Natural language chat command dispatched to runtime agent bridge
        return DispatchNaturalLanguageChat(rawInput);
    }

    private bool DispatchNaturalLanguageChat(string rawInput)
    {
        // Player messages share F8's durable conversation/control path. The
        // legacy work-command slot can remain occupied across several jobs;
        // it must never prevent the player from changing that work.
        return DispatchLifeChat(rawInput, "chat");
    }

    // Independent autostart test dispatch. Sends the preset through the exact
    // production chat pipeline (chat.submit) with a stable per-run/save request id,
    // bypassing the F8 command UI slot so the run keeps its own lifecycle and never
    // trips the F8 inactivity watchdog. The runtime plans and acts; this only fires.
    private void TryDispatchPresetGoal()
    {
        if (_preset is not { Enabled: true } preset || preset.RequestId == null)
            return;
        if (!preset.ShouldDispatchNow())
            return;
        if (!Context.IsWorldReady || _actor == null || _coordinator == null)
            return;
        if (_transportServer?.IsChatConnected != true)
            return; // model tool connection not ready yet; stay in preparing
        if (PlayerModeEnabled && _authority?.AgentOwns != true)
            return; // never dispatch while the human owns the player (e.g. after F9)
        DispatchPresetGoal(preset);
    }

    private void DispatchPresetGoal(PresetGoalAutostart preset)
    {
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        var payload = new ChatSubmitPayload(preset.RequestId!, preset.GoalText, "text", saveId);
        preset.MarkDispatched();
        Monitor.Log($"Dispatching preset goal [{preset.RequestId}] for run {preset.RunId}.", LogLevel.Info);
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await _transportServer!.SendChatSubmitAsync(payload).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Monitor.Log($"Preset goal send failed: {ex}", LogLevel.Warn);
                ok = false;
            }
            if (!ok) _mainThreadActions.Enqueue(() =>
                preset.MarkDispatchFailed("预设目标发送失败：伙伴服务连接中断；恢复连接后会自动重试。"));
        });
    }

    private void HandleChatReplyReceived(ChatReplyPayload reply)
    {
        string currentSave = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        if (string.Equals(reply.SaveId, currentSave, StringComparison.Ordinal) &&
            _conversationSaveId == currentSave && !string.IsNullOrWhiteSpace(reply.ReplyText) &&
            reply.DeliveryOutboxIds is not { Count: > 0 } &&
            reply.Status is "public-message" or "selected" or "decision-completed" or "completed" or "failed" or "partial" or "unknown")
        {
            _lifeMenuUiState.RecordCompanionReply(reply.RequestId,
                CompanionDialogueInbox.PlainText(reply.ReplyText), GetCurrentGameDate());
            PersistConversation();
        }
        if (reply.Status == "public-message") return;
        // Independent autostart test replies keep the common freshness/outbox
        // processing below (long model planning must read fresh snapshots); only the
        // F8 command-slot UI routing is bypassed further down.
        PresetGoalAutostart? presetReply = _preset is { Enabled: true } presetMatch && presetMatch.Matches(reply.RequestId) &&
            string.Equals(reply.SaveId, currentSave, StringComparison.Ordinal) ? presetMatch : null;
        if (PlayerModeEnabled && string.Equals(reply.SaveId, currentSave, StringComparison.Ordinal))
        {
            // Player mode: a fresh turn clears the failure hold; a failed turn while
            // idle means nobody is driving, so hold the clock instead of letting the
            // unattended farmer stand into the 02:00 collapse.
            if (reply.Status == "processing") _thinking?.ClearFailureStop();
            else if (reply.Status == "failed" && _coordinator?.GetActivityStatus() == "idle")
                _thinking?.StopForFailure("模型/程序停止：" + reply.ReplyText);
        }
        // Tool-start progress is delivered on the game thread. Long model turns
        // can outlive the submission snapshot, so publish a real observation at
        // this existing read boundary, including while the game clock is held.
        if (reply.Status == "processing" && Context.IsWorldReady &&
            string.Equals(reply.SaveId, currentSave, StringComparison.Ordinal) &&
            _coordinator is not null && _observer is not null && _transportServer?.IsChatConnected == true)
            _ = _transportServer.SendSnapshotAsync(_coordinator.CaptureCurrentSnapshot(_observer.WorldRevision),
                _observer.WorldRevision);
        if (reply.DeliveryOutboxIds is { Count: > 0 } && Context.IsWorldReady &&
            string.Equals(reply.SaveId, currentSave, StringComparison.Ordinal))
        {
            // NPC work results own a durable inbox receipt, independent of the
            // legacy F8 command state and of any newer player chat submission.
            if (_conversationSaveId == currentSave &&
                _lifeMenuUiState.ReceiveWorkResult(reply.RequestId, reply.ReplyText, GetCurrentGameDate()))
            {
                _ = _transportServer?.SendChatDeliveryAckAsync(reply.RequestId, currentSave, reply.DeliveryOutboxIds);
                Monitor.Log($"Work delivery received: turn={reply.RequestId}, outbox={string.Join(",", reply.DeliveryOutboxIds)}", LogLevel.Info);
            }
            // The durable inbox and the command UI have separate ownership.
            // A matching command still needs its terminal reply to release the
            // input slot; unrelated deliveries must not settle a newer command.
            if (presetReply is null && !_chatUiState.MatchesReply(reply.RequestId, reply.CommandId, reply.SaveId))
                return;
        }
        if (presetReply is not null)
        {
            // Independent autostart test owns its lifecycle replies; route them away from
            // the F8 command slot so the run neither claims nor is cleared by the manual
            // command. Common freshness/outbox processing above already ran.
            presetReply.NoteReply(reply.Status, reply.CommandComplete, reply.PresetGoalId, reply.PresetGoalTerminal);
            if (!string.IsNullOrWhiteSpace(reply.ReplyText) && reply.Status is not "processing")
                CompanionCommandMenu.ChatHistory.Add(new ChatMessage("自动测试", reply.ReplyText,
                    presetReply.Phase is PresetGoalPhase.Failed or PresetGoalPhase.Cancelled ? Color.Red : Color.DarkGreen));
            Monitor.Log($"Preset reply [{reply.RequestId}]: status={reply.Status} complete={reply.CommandComplete} goal={reply.PresetGoalId} terminal={reply.PresetGoalTerminal}", LogLevel.Info);
            return;
        }
        if (reply.Status == "player-decision")
        {
            if (Context.IsWorldReady && _lifeMenuUiState.TryAddDecisionNotice(currentSave, reply.SaveId,
                reply.RequestId, GetCurrentGameDate(), CompanionDialogueInbox.PlainText(reply.ReplyText)))
                ShowCompanionHud(new HUDMessage("伙伴有件重要的事想和你商量，和伙伴对话查看消息。"));
            return;
        }
        if (reply.Status == "processing" && !_chatUiState.HasActiveCommand &&
            !_chatUiState.HasPendingControl && CompanionCommandMenu.AutonomyMode == "free" &&
            string.Equals(reply.SaveId, currentSave, StringComparison.Ordinal))
            _chatUiState.BeginCommand(reply.CommandId ?? reply.RequestId, currentSave, "模型正在思考");

        // NPC preparation jobs share the panel without pretending they were F8 commands.
        if (string.Equals(reply.SaveId, currentSave, StringComparison.Ordinal) && reply.Status.StartsWith("job-", StringComparison.Ordinal))
        {
            CompanionCommandMenu.TaskState.ApplyChatReply(reply);
        }
        string? previousTurn = _chatUiState.TurnId;
        bool requestWorkResume = _chatUiState.ShouldRequestWorkResume(reply.RequestId, reply.CommandId,
            reply.SaveId, reply.Status, reply.ResumeWorkRequested);
        if (!_chatUiState.ApplyReply(reply.RequestId, reply.CommandId, reply.SaveId, reply.Status, reply.CommandComplete))
            return;
        if (requestWorkResume) RequestResumeMenuAction();
        CompanionCommandMenu.TaskState.ApplyChatReply(reply);
        _chatActivityAt = DateTime.UtcNow;
        ProjectChatUiState();
        if (previousTurn != null && previousTurn != reply.RequestId)
        {
            CompanionCommandMenu.CurrentActionText = null;
            CompanionCommandMenu.CurrentToolName = null;
            CompanionCommandMenu.StructuredProgressText = null;
        }
        Monitor.Log($"Chat reply received: Status={reply.Status}, Command={reply.CommandId}, Turn={reply.RequestId}, Tokens={reply.TokensUsed}", LogLevel.Info);

        if (reply.Status.StartsWith("job-", StringComparison.OrdinalIgnoreCase))
        {
            CompanionCommandMenu.CurrentActionText = reply.ReplyText;
            if (reply.Status is "job-completed" or "job-failed")
                CompanionCommandMenu.ChatHistory.Add(new ChatMessage("执行结果", reply.ReplyText,
                    reply.Status == "job-completed" ? Color.DarkGreen : Color.Red));
            return;
        }
        if (reply.Status is "selected" or "decision-completed")
        {
            CompanionCommandMenu.CurrentActionText = reply.Status == "selected" ? "短作业等待原生执行" : null;
            CompanionCommandMenu.ChatHistory.Add(new ChatMessage("伙伴", reply.ReplyText, Color.DarkGreen, BuildUsageLine(reply, "本轮用量")));
            if (reply.CommandComplete == true) ShowCompanionHud(new HUDMessage("AI 伙伴已回复，请看聊天栏。"));
            return;
        }
        if (reply.Status == "processing")
        {
            if (!string.IsNullOrWhiteSpace(reply.ReplyText)) CompanionCommandMenu.CurrentActionText = reply.ReplyText;
            if (!string.IsNullOrWhiteSpace(reply.ToolName)) CompanionCommandMenu.CurrentToolName = reply.ToolName;
            return;
        }

        CompanionCommandMenu.CurrentActionText = null;
        string? tokenInfo = BuildUsageLine(reply, reply.Status == "cancelled" ? "已中止用量" : "本轮用量");
        CompanionCommandMenu.LastTokenInfo = tokenInfo;
        if (reply.Status == "cancelled")
        {
            CompanionCommandMenu.ChatHistory.Add(new ChatMessage("伙伴", reply.ReplyText, Color.DarkGoldenrod, tokenInfo));
            ShowCompanionHud(new HUDMessage("AI 伙伴任务已取消，请看聊天栏。"));
        }
        else if (reply.Status == "completed")
        {
            CompanionCommandMenu.ChatHistory.Add(new ChatMessage("伙伴", reply.ReplyText, Color.DarkGreen, tokenInfo));
            if (reply.CommandComplete != false) ShowCompanionHud(new HUDMessage("AI 伙伴已回复，请看聊天栏。"));
        }
        else
        {
            string err = !string.IsNullOrEmpty(reply.Error) ? reply.Error : reply.ReplyText;
            CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", $"任务未完成: {err}", Color.Red, tokenInfo));
            ShowCompanionHud(new HUDMessage("AI 伙伴任务未完成，请看聊天栏。", HUDMessage.error_type));
        }
    }

    /// <summary>
    /// Builds the F8 usage line from the real provider fields only: input, cache
    /// read, cache write, output, total and model calls. Unknown stays "未知";
    /// no cost or saving ratio is invented.
    /// </summary>
    private static string? BuildUsageLine(ChatReplyPayload reply, string prefix)
    {
        if (!reply.TokensUsed.HasValue &&
            !reply.PromptTokens.HasValue &&
            !reply.OutputTokens.HasValue &&
            !reply.CachedTokens.HasValue)
        {
            string missingTag = !string.IsNullOrEmpty(reply.UsageSource) ? $" ({reply.UsageSource})" : "";
            return $"用量: 客户端未提供{missingTag}";
        }

        string input = reply.PromptTokens?.ToString("N0") ?? "未知";
        string cacheRead = (reply.CacheReadTokens ?? reply.CachedTokens)?.ToString("N0") ?? "未知";
        string cacheWrite = reply.CacheWriteTokens?.ToString("N0") ?? "未知";
        string output = reply.OutputTokens?.ToString("N0") ?? "未知";
        string total = reply.TokensUsed?.ToString("N0") ?? "未知";
        string calls = reply.ModelCalls?.ToString() ?? "未知";
        string srcTag = !string.IsNullOrEmpty(reply.UsageSource) ? $" [{reply.UsageSource}]" : "";
        return $"{prefix}: {total} tokens (输入: {input}, 缓存读: {cacheRead}, 缓存写: {cacheWrite}, 输出: {output}, 模型调用: {calls}){srcTag}";
    }

    private void ProjectChatUiState()
    {
        CompanionCommandMenu.CurrentRequestId = _chatUiState.PausedChatRequestId ?? _chatUiState.CommandId;
        CompanionCommandMenu.CurrentRequestSaveId = _chatUiState.PausedChatSaveId ?? _chatUiState.SaveId;
        CompanionCommandMenu.IsProcessing = _chatUiState.HasActiveCommand;
        CompanionCommandMenu.ControlPending = _chatUiState.HasPendingControl;
        CompanionCommandMenu.PendingControlAction = _chatUiState.PendingControlAction;
        CompanionCommandMenu.ModeChangePending = _chatUiState.PendingControlAction == "set_mode";
        CompanionCommandMenu.AutonomyPaused = _chatUiState.IsPaused;
        CompanionCommandMenu.CurrentStatusText = _chatUiState.StatusText;
        ProjectChatUiAvailability();
    }

    private void ProjectChatUiAvailability()
    {
        CompanionCommandMenu.CanSubmitText = _chatUiState.CanSubmit;
        CompanionCommandMenu.SubmissionBlockReason = _chatUiState.HasPendingControl
            ? "正在等待控制确认，请稍候。"
            : _chatUiState.PausedChatRequestId is not null ? "正在回复聊天，请稍候。"
            : _chatUiState.LocalPauseRequested && !_chatUiState.IsPaused ? "游戏动作仍在等待暂停确认，请稍候。"
            : _chatUiState.HasActiveCommand ? "当前指令仍在执行，请稍候或点击[取消]。"
            : null;
    }

    private void ShowChatChannelProblem(string? requestId, string? saveId, string problem)
    {
        if (!string.IsNullOrEmpty(saveId) && saveId != (Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString())) return;
        // A channel loss affects every queued exchange, not just the latest
        // bubble. Otherwise orphaned requests permanently consume the 8 slots.
        foreach (var lifeRequest in _lifeMenuUiState.PendingChatRequestIds.Where(id => requestId == null || requestId == id))
            HandleLifeChatReplyReceived(new LifeChatReplyPayload(lifeRequest,
                Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString(), "failed",
                _lifeMenuUiState.ProfileRevision, _lifeMenuUiState.MemoryRevision,
                ReplyText: requestId == null
                    ? "连接中断，这条消息的回复尚未确认。连接恢复后可查看工作记录或重新说明要求。"
                    : problem));
        bool pendingControl = requestId != null && requestId == _chatUiState.PendingControlId;
        bool activeCommand = requestId != null && (requestId == _chatUiState.CommandId || requestId == _chatUiState.TurnId
            || requestId == _chatUiState.PausedChatRequestId);
        if (requestId != null && !pendingControl && !activeCommand) return;
        bool controlProblem = pendingControl || requestId == null && _chatUiState.HasPendingControl;
        if (controlProblem)
            _chatUiState.FailControl(_chatUiState.PendingControlId!);
        else _chatUiState.ShowConnectionProblem("连接/回复异常，请检查连接");
        ProjectChatUiState();
        string detail = controlProblem && _chatUiState.LocalPauseRequested
            ? problem + " 游戏动作的暂停请求已发出；仍可点击[继续]请求恢复。"
            : problem;
        CompanionCommandMenu.CurrentActionText = detail;
        CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", detail, Color.Red));
        // Keep an active command correlated until a real terminal or confirmed cancel.
    }

    private void ToggleAutonomyMode()
    {
        if (_chatUiState.HasPendingControl) return;
        string next = CompanionCommandMenu.AutonomyMode == "free" ? "command" : "free";
        _ = SendAutonomyControl("set_mode", new JsonObject { ["mode"] = next });
    }

    private void OpenAutonomySettings()
    {
        _ = SendAutonomyControl("set_preferences", new JsonObject
        {
            ["box_preference"] = CompanionCommandMenu.BoxPreference,
            ["idle_preference"] = CompanionCommandMenu.IdlePreference
        });
    }

    private async Task SendAutonomyControl(string action, JsonObject parameters)
    {
        if (_chatUiState.HasPendingControl) return;
        if (_transportServer == null || !_transportServer.IsChatConnected)
        {
            ShowChatChannelProblem(null, null, "伙伴服务未连接，请启动原有伙伴服务后重试。");
            return;
        }
        string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
        string requestId = Guid.NewGuid().ToString("N")[..8];
        if (action is "pause" or "resume" or "cancel" && _chatUiState.CommandId is string commandId)
            parameters["commandId"] = commandId;
        if (!_chatUiState.BeginControl(requestId, action)) return;
        _lifeControls.Supersede();
        _autonomySentAt = DateTime.UtcNow;
        ProjectChatUiState();
        try
        {
            bool sent = await _transportServer.SendAutonomyControlAsync(
                new AutonomyControlPayload(requestId, saveId, action, parameters)).ConfigureAwait(false);
            if (!sent) _mainThreadActions.Enqueue(() => ShowChatChannelProblem(requestId, saveId, "控制发送失败，服务端未确认；请检查连接后重试。"));
        }
        catch (Exception ex)
        {
            _mainThreadActions.Enqueue(() => ShowChatChannelProblem(requestId, saveId, $"控制发送失败：{ex.Message}；请重试。"));
        }
    }

    private void HandleAutonomyStateReceived(AutonomyStatePayload state)
    {
        _mainThreadActions.Enqueue(() =>
        {
            // §1.8 start-together acks are tracked outside the F8 pending-control flow.
            AdvanceLifeStartAfterControl(state.RequestId, state.Status);

            string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
            if (!string.Equals(state.SaveId, saveId, StringComparison.Ordinal)) return;
            // Read-only usage telemetry may refresh without acknowledging a
            // control. Mode and pause still require the matching pending ACK.
            CompanionCommandMenu.TaskState.ApplyUsageToday(state.UsageTodayText);
            CompanionCommandMenu.TaskState.ApplySaveUsage(state.UsageSaveTodayText, state.UsageSaveTotalText);
            if (!string.Equals(state.RequestId, _chatUiState.PendingControlId, StringComparison.Ordinal))
            {
                if (!_lifeControls.TryAccept(state, _lifeMenuUiState.PendingChatRequestId, _chatUiState.HasPendingControl)) return;
                if (!_chatUiState.BeginControl(state.RequestId, state.ControlAction!)) return;
            }
            string action = _chatUiState.PendingControlAction ?? "unknown";
            Monitor.Log($"[AutonomyAck] requestId={state.RequestId} action={action} status={state.Status} mode={state.Mode} paused={state.Paused} saveId={state.SaveId}", LogLevel.Info);
            ApplyAutonomyStateOnMainThread(state, action);
        });
    }

    private void ApplyAutonomyStateOnMainThread(AutonomyStatePayload state, string action)
    {
        if (!string.Equals(state.RequestId, _chatUiState.PendingControlId, StringComparison.Ordinal)) return;
        bool confirmed = string.Equals(state.Status, "confirmed", StringComparison.OrdinalIgnoreCase) && state.Mode is "free" or "command";
        _chatUiState.ApplyControlAck(state.RequestId, confirmed, state.Paused);
        if (confirmed)
        {
            if (action == "pause" && state.Paused) RequestPause(out _);
            if (action == "resume" && !state.Paused) RequestResume(out _);
            if (action == "cancel") RequestCancel("confirmed conversation", out _);
            if (!state.Paused) _chatUiState.NoteLocalResumed();
            CompanionCommandMenu.TaskState.ApplyConfirmedPause(state.Paused);
            // A confirmed resume starts a fresh progress window; time deliberately
            // spent paused is not evidence of a missing response.
            _chatActivityAt = DateTime.UtcNow;
        }
        ProjectChatUiState();
        if (!confirmed)
        {
            string reason = "服务未确认" + (string.IsNullOrWhiteSpace(state.Reason) ? "。" : "：" + state.Reason);
            CompanionCommandMenu.CurrentActionText = reason + " 游戏中的动作保持本地实际状态，可重试控制。";
            CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", CompanionCommandMenu.CurrentActionText, Color.Red));
            return;
        }
        string controlResult = action switch
        {
            "set_mode" => state.Mode == "free" ? "空闲时会主动帮忙" : "空闲时会等你安排",
            "pause" => state.Paused ? "已确认：伙伴已暂停" : "已确认：暂停请求已处理",
            "resume" => state.Paused ? "已确认：伙伴仍处于暂停" : "已确认：伙伴已继续",
            "cancel" => "已确认：当前安排已取消",
            "set_preferences" => "已确认：设置已保存",
            _ => "控制已确认"
        };
        CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", controlResult, Color.DarkGreen));
        CompanionCommandMenu.AutonomyMode = state.Mode;
        if (state.Preferences != null)
        {
            if (state.Preferences.TryGetPropertyValue("boxPreference", out var box) && !string.IsNullOrWhiteSpace(box?.ToString()))
                CompanionCommandMenu.BoxPreference = box!.ToString();
            if (state.Preferences.TryGetPropertyValue("idlePreference", out var idle) && !string.IsNullOrWhiteSpace(idle?.ToString()))
                CompanionCommandMenu.IdlePreference = idle!.ToString();
        }
        if (action == "set_mode") CompanionCommandMenu.CurrentStatusText = state.Paused ? "已暂停" : (state.Mode == "free" ? "主动帮忙已开启" : "按你的安排工作");
        if (action == "cancel")
        {
            CompanionCommandMenu.TaskState.ApplyConfirmedCancel();
            CompanionCommandMenu.CurrentActionText = null;
            CompanionCommandMenu.CurrentToolName = null;
            CompanionCommandMenu.StructuredProgressText = null;
            CompanionCommandMenu.PlanWaitReason = null;
            CompanionCommandMenu.WaitingConditions = Array.Empty<string>();
        }
        // F8 fixed progress lines: last plan action, wait reason and waiting conditions
        // come from the real bridge payload.
        if (action != "cancel")
        {
            CompanionCommandMenu.PlanWaitReason = state.PlanWaitReason;
            CompanionCommandMenu.WaitingConditions = state.WaitingConditions?.Select(condition => condition.DisplayText).ToArray() ?? Array.Empty<string>();
            if (state.LastPlanAction != null)
            {
                string? operation = state.LastPlanAction.TryGetPropertyValue("operation", out var op) ? op?.ToString() : null;
                string? outcome = state.LastPlanAction.TryGetPropertyValue("outcome", out var oc) ? oc?.ToString() : null;
                if (!string.IsNullOrEmpty(operation))
                    CompanionCommandMenu.CurrentActionText = $"计划动作 {operation}（{outcome ?? "unknown"}）";
            }
            if (!string.IsNullOrEmpty(state.PlanWaitReason) && string.IsNullOrEmpty(CompanionCommandMenu.CurrentActionText))
                CompanionCommandMenu.CurrentActionText = "等待：" + state.PlanWaitReason;
        }

    }

    private void RequestPauseMenuAction()
    {
        if (_chatUiState.HasPendingControl) return;
        bool local = RequestPause(out string msg);
        _chatUiState.NoteLocalPauseRequested();
        if (local)
        {
            _localPauseMachine = _coordinator?.ActiveMachine;
            _deferredResumeMachine = null;
        }
        _ = SendAutonomyControl("pause", new JsonObject());
        if (local) CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", "游戏中的动作已请求暂停；等待伙伴服务确认。", Color.DarkGoldenrod));
    }

    private void RequestResumeMenuAction()
    {
        if (_chatUiState.HasPendingControl) return;
        _ = SendAutonomyControl("resume", new JsonObject());
        CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", "正在请求继续工作；等待伙伴服务确认。", Color.SeaGreen));
    }

    private void RequestCancelMenuAction()
    {
        if (_chatUiState.HasPendingControl) return;
        // Keep newly arriving native commands stopped until the service has
        // retired the old arrangement, even when there is no current machine.
        _chatUiState.NoteLocalPauseRequested();
        bool local = RequestCancel("menu", out string msg);
        _ = SendAutonomyControl("cancel", new JsonObject());
        CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", local
            ? "游戏中的动作已收到取消请求；等待伙伴服务确认。"
            : "正在请求取消当前安排；等待伙伴服务确认。", Color.Firebrick));
    }

    private bool DispatchWaterZone(int x, int y, int radius, string source, out string message)
    {
        if (!Context.IsWorldReady || _actor == null || _coordinator == null || _observer == null)
        {
            message = "Companion is not initialized or world is not ready.";
            return false;
        }

        if (!string.Equals(_actor.LocationName, "Farm", StringComparison.OrdinalIgnoreCase))
        {
            message = $"Companion is on '{_actor.LocationName}'. Watering is restricted to Farm in Stage 0.";
            return false;
        }

        if (_actor.ActiveTaskId != null || _coordinator.StateMachine.IsExecuting)
        {
            string currentTask = _actor.ActiveTaskId ?? _coordinator.StateMachine.CurrentRequest?.TaskId ?? "active";
            message = $"Companion is busy executing task '{currentTask}'. Use pause, cancel, or status.";
            return false;
        }

        var targets = CompanionCommandHandler.FindTilledUnwateredTiles(_observer, "Farm", x, y, radius);
        if (targets.Count == 0)
        {
            message = $"No tilled unwatered tiles found in area centered at ({x}, {y}) with radius {radius}.";
            return false;
        }

        string cmdId = $"cmd-{source}-" + Guid.NewGuid().ToString("N")[..8];
        string taskId = $"task-{source}-" + Guid.NewGuid().ToString("N")[..8];

        var request = new WaterZoneRequest(
            CommandId: cmdId,
            TaskId: taskId,
            LocationId: "Farm",
            TargetTiles: targets,
            MaxStamina: _actor.Stamina,
            MaxWater: _actor.WaterLeft,
            MaxGameMinutes: 0,
            CancelPolicy: "safe-point",
            IdempotencyKey: cmdId,
            ExpectedWorldRevision: _observer.WorldRevision
        );

        if (!_coordinator.TryStartWaterZone(request, envelope: null, out var earlyResult, out var rejectReason))
        {
            message = $"Failed to start watering task: {rejectReason ?? earlyResult?.ErrorMessage ?? "Rejected"}.";
            return false;
        }

        message = $"Dispatched watering task '{taskId}' for {targets.Count} tile(s).";
        return true;
    }

    private bool RequestPause(out string message)
    {
        if (Context.IsWorldReady && _coordinator?.HasRestTask == true)
        {
            bool changed = _coordinator.PauseRestTask();
            message = changed ? "Pause requested for active task." : "Task is already paused.";
            return changed;
        }
        var machine = Context.IsWorldReady ? _coordinator?.ActiveMachine : null;
        if (machine == null)
        {
            message = "No active companion task to pause.";
            return false;
        }

        if (machine.IsPaused)
        {
            message = "Task is already paused.";
            return false;
        }

        machine.RequestPause();
        message = "Pause requested for active task.";
        return true;
    }

    private bool RequestResume(out string message)
    {
        if (Context.IsWorldReady && _coordinator?.HasRestTask == true)
        {
            bool changed = _coordinator.ResumeRestTask();
            message = changed ? "Resumed active task." : "Task is not paused.";
            return changed;
        }
        var machine = Context.IsWorldReady ? _coordinator?.ActiveMachine : null;
        if (machine == null)
        {
            message = "No active companion task to resume.";
            return false;
        }

        if (!machine.IsPaused && ReferenceEquals(machine, _localPauseMachine))
        {
            _deferredResumeMachine = machine;
            message = "Resume requested; waiting for the native safe pause point.";
            return true;
        }

        if (!machine.IsPaused)
        {
            message = "Task is not paused.";
            return false;
        }

        machine.Resume();
        if (ReferenceEquals(machine, _localPauseMachine))
        {
            _localPauseMachine = null;
            _deferredResumeMachine = null;
        }
        message = "Resumed active task.";
        return true;
    }

    private bool RequestCancel(string source, out string message)
    {
        if (Context.IsWorldReady && _coordinator?.CancelRestTask() == true)
        {
            message = "Cancellation requested for active task.";
            return true;
        }
        var machine = Context.IsWorldReady ? _coordinator?.ActiveMachine : null;
        if (machine == null)
        {
            message = "No active companion task to cancel.";
            return false;
        }

        machine.RequestCancel($"Cancelled via {source}.");
        message = "Cancellation requested for active task.";
        return true;
    }

    private string GetStatusDetails()
    {
        if (!Context.IsWorldReady || _actor == null)
            return "Companion is not initialized or world is not ready.";

        return CompanionCommandHandler.FormatStatusDetails(_actor, _coordinator?.StateMachine);
    }

    private string GetStatusSummary()
    {
        if (!Context.IsWorldReady || _actor == null)
            return "Companion is not initialized or world is not ready.";

        string line = CompanionCommandHandler.FormatStatusLine(
            _coordinator?.StateMachine.IsExecuting == true,
            _coordinator?.StateMachine.IsPaused == true,
            _coordinator?.StateMachine.CurrentTargetIndex ?? 0,
            _coordinator?.StateMachine.TotalTargets ?? 0
        );

        return $"Companion: ({_actor.Tile.X}, {_actor.Tile.Y}) | Stamina: {_actor.Stamina:F0} | Water: {_actor.WaterLeft} | {line}";
    }

    private void OnCommandWater(string command, string[] args)
    {
        if (!CompanionCommandHandler.ParseWaterCommand(args, out int x, out int y, out int radius, out string? error))
        {
            Monitor.Log(error!, LogLevel.Warn);
            return;
        }

        if (DispatchWaterZone(x, y, radius, "console", out string message))
        {
            Monitor.Log($"{message} Use 'ai_status' to monitor progress.", LogLevel.Info);
        }
        else
        {
            Monitor.Log(message, LogLevel.Warn);
        }
    }

    private void OnCommandPause(string command, string[] args)
    {
        RequestPauseMenuAction();
    }

    private void OnCommandResume(string command, string[] args)
    {
        RequestResumeMenuAction();
    }

    private void OnCommandCancel(string command, string[] args)
    {
        RequestCancelMenuAction();
    }

    private void OnCommandStatus(string command, string[] args)
    {
        Monitor.Log(GetStatusDetails(), LogLevel.Info);
    }

    // =========================================================================
    // Life-system methods
    // =========================================================================

    /// <summary>
    /// Checks if the player is close enough to the companion and facing it,
    /// then opens the life menu.
    /// </summary>
}
