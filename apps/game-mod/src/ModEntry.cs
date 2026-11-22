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
using StardewAI.Companion.Mod.Hosting;
using StardewAI.Companion.Mod.Menus;
using StardewAI.Companion.Mod.Navigation;
using StardewAI.Companion.Mod.Observation;
using StardewAI.Companion.Mod.Transport;

namespace StardewAI.Companion.Mod;

/// <summary>
/// Main SMAPI Mod entry point.
/// Connects SMAPI lifecycle events to the Mechanics Actor, tick-driven
/// state machine, visible avatar rendering, per-save partition persistence,
/// and WebSocket transport server with local endpoint discovery.
/// </summary>
public sealed partial class ModEntry : StardewModdingAPI.Mod
{
    private DiscoveryService? _discoveryService;
    private IActorStateRepository? _stateRepository;
    private IFarmerActor? _actor;
    private ModConfig _config = new();
    private PlayerControlAuthority? _authority;
    private bool _playerControlApproved;
    private DecisionPauseController? _thinking;
    private PlayerKnowledgeTracker? _knowledge;
    private bool PlayerModeEnabled => _config.Mode == "player";
    private CompanionAvatar? _avatar;
    private IWorldObserver? _observer;
    private SameMapNavigator? _navigator;
    private CompanionMechanicsCoordinator? _coordinator;
    private GameTransportSession? _transportSession;
    private WebSocketTransportServer? _transportServer;
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly ChatCommandUiState _chatUiState = new();
    private ISkillExecutionMachine? _localPauseMachine;
    private ISkillExecutionMachine? _deferredResumeMachine;
    private DateTime _autonomySentAt;
    private DateTime _chatActivityAt = DateTime.UtcNow;
    private string? _watchedChatRequest;
    private Microsoft.Xna.Framework.Rectangle _companionHudBounds;

    // -----------------------------------------------------------------------
    // Life-system fields
    // -----------------------------------------------------------------------

    private readonly LifeMenuUiState _lifeMenuUiState = new();
    private CompanionConversationStore? _conversationStore;
    private string? _conversationSaveId;
    private CompanionDialogueController? _companionDialogue;
    private readonly CareHintController _careHintController = new();
    private readonly CompanionInteractionDetector _interactionDetector = new();
    private bool _lifeSaveProfileFetched;   // true once life.profile.get was sent for this save
    private bool _lifeOnboardingHudShown;   // avoid repeated HUD nudge

    // §1.8 start-together sequence, ack-chained: ① life.profile.set ②
    // set_preferences(goal) ③ set_mode(free). Each step waits for its
    // confirmation; a failure or timeout aborts with the real state kept, so
    // the player can retry from setup. Ordering lives in LifeStartSequence.
    private readonly LifeStartSequence _lifeStartSequence = new();

    // Independent real-player autostart test (isolated launcher opt-in only). Null
    // unless player mode loads a save with STARDEW_AI_PRESET_GOAL_FILE configured.
    private PresetGoalAutostart? _preset;

    public override void Entry(IModHelper helper)
    {
        Monitor.Log("Stardew Game Companion initializing game events and transport.", LogLevel.Info);
        InitializeGameLog(helper.DirectoryPath);
        LogDesktopGameEvent("mod_initialized", "游戏 Mod 已初始化。");

        _config = Helper.ReadConfig<ModConfig>();
        _savedMode = _config.Mode;
        if (!_desktopMode && !PlayerModeEnabled) CompanionMenuClock.Install();
        _discoveryService = new DiscoveryService(helper.DirectoryPath, Monitor);
        _conversationStore = new CompanionConversationStore(Path.Combine(helper.DirectoryPath, "data", "conversations"));
        _lifeMenuUiState.ConversationChanged += PersistConversation;

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.SaveCreated += (_, _) => {
            if (PlayerModeEnabled && _actor == null) InitializeLoadedWorld();
        };
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        if (!_desktopMode) helper.Events.GameLoop.UpdateTicking += (_, _) => TickNativeChatInput();
        helper.Events.GameLoop.TimeChanged += OnTimeChanged;
        CompanionWorldDraw.Install(() => PlayerModeEnabled ? null : _avatar);
        if (!_desktopMode) helper.Events.Display.RenderedHud += OnRenderedHud;
        helper.Events.Display.MenuChanged += (_, e) => {
            if (PlayerModeEnabled && Context.IsWorldReady && e.NewMenu is StardewValley.Menus.ItemGrabMenu menu && menu.context is StardewValley.Objects.Chest chest)
                _knowledge?.NoteChestObserved(Game1.currentLocation.NameOrUniqueName,(int)chest.TileLocation.X,(int)chest.TileLocation.Y);
        };
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        if (!_desktopMode) InstallNativeChat();
        else EnableDesktopFocusPolicy();

        helper.ConsoleCommands.Add("ai_water",
            "Waters tilled unwatered tiles centered at <x> <y> with optional [radius] (0-2, default 0) on the Farm.\nUsage: ai_water <x> <y> [radius]",
            OnCommandWater);

        helper.ConsoleCommands.Add("ai_pause",
            "Pauses the currently executing companion task.\nUsage: ai_pause",
            OnCommandPause);

        helper.ConsoleCommands.Add("ai_resume",
            "Resumes a paused companion task.\nUsage: ai_resume",
            OnCommandResume);

        helper.ConsoleCommands.Add("ai_cancel",
            "Cancels the currently executing companion task.\nUsage: ai_cancel",
            OnCommandCancel);

        helper.ConsoleCommands.Add("ai_status",
            "Displays the current status, position, resources, and progress of the companion.\nUsage: ai_status",
            OnCommandStatus);

        helper.ConsoleCommands.Add("ai_chat",
            "Sends a natural language chat message to the companion agent.\nUsage: ai_chat <text>",
            (cmd, args) => ExecuteInGameCommand(string.Join(" ", args)));
    }

    /// <summary>
    /// Late-night unattended guard (player mode): when the agent owns the player but no
    /// model turn holds a thinking lease and no machine is executing, an idle farmer
    /// would collapse at 02:00 and roll the day without any business. Hold the clock
    /// instead; a fresh turn (processing reply / thinking begin) or a human takeover
    /// releases the hold. Daytime waits (shop hours, crop growth) are never frozen.
    /// </summary>
    private void TickUnattendedPassoutGuard()
    {
        if (!PlayerModeEnabled || !Context.IsWorldReady || Game1.IsMultiplayer) return;
        if (_authority is not { AgentOwns: true }) return;
        if (Game1.timeOfDay < 2500 || _dayTransition || Game1.newDay || Game1.showingEndOfNightStuff) return;
        if (_thinking is null || _thinking.FailureReason != null || _thinking.ActiveLeaseId != null) return;
        if (_coordinator is null || _coordinator.GetActivityStatus() != "idle") return;
        _thinking.StopForFailure("深夜无执行动作且无模型回合，已保护暂停防止空站昏倒；新回合或接管自动恢复。");
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!_desktopMode || !Context.IsWorldReady)
        {
            _desktopPreviousTick = 0;
            UpdateGame(e);
            return;
        }

        BeginDesktopTick();
        try { UpdateGame(e); }
        finally { EndDesktopTick(); }
    }

    private void UpdateGame(UpdateTickedEventArgs e)
    {
        while (_mainThreadActions.TryDequeue(out var action)) action();
        MeasureDesktopPhase("callbacks");
        _thinking?.Tick();
        TickUnattendedPassoutGuard();
        if (!_desktopMode && PlayerModeEnabled && e.IsMultipleOf(30) && _coordinator != null && _transportServer != null)
            _ = _transportServer.SendSnapshotAsync(_coordinator.CaptureCurrentSnapshot(_observer!.WorldRevision), _observer.WorldRevision);
        if (e.IsMultipleOf(30)) PublishDesktopControlState(e.IsMultipleOf(120));
        MeasureDesktopPhase("snapshot");
        if (PlayerModeEnabled && _actor != null) _knowledge?.NoteLocationVisited(_actor.LocationName);
        if (_decisionEdits.Count > 0 && DateTime.UtcNow - _decisionEditSentAt > TimeSpan.FromSeconds(30))
        {
            foreach (var previous in _decisionEdits.Values)
                foreach (var entry in previous) _lifeMenuUiState.SetDecisionStatus(entry.Key, entry.Value);
            _decisionEdits.Clear();
            DispatchLifeProfileRefresh();
            ShowCompanionHud(new HUDMessage("删除尚未确认，正在重新核对。", HUDMessage.error_type));
        }
        if (Context.IsWorldReady) _companionDialogue?.Update();
        if (_watchedChatRequest != _chatUiState.CommandId)
        {
            _watchedChatRequest = _chatUiState.CommandId;
            _chatActivityAt = DateTime.UtcNow;
        }
        if (_chatUiState.PendingControlId != null && DateTime.UtcNow - _autonomySentAt > TimeSpan.FromSeconds(30))
            ShowChatChannelProblem(_chatUiState.PendingControlId, null, "控制确认超时，服务端结果未知；可重试控制。游戏中的动作保持本地实际状态。");
        if (_chatUiState.ShouldReportInactivity(DateTime.UtcNow, _chatActivityAt))
        {
            _chatActivityAt = DateTime.UtcNow;
            ShowChatChannelProblem(_chatUiState.CommandId, null, "120秒未收到伙伴进度，结果未确认；请检查连接或取消原任务。");
        }
        // 0. Defensive initialization: ensure companion is initialized as soon as the world is ready,
        // even if SaveLoaded/DayStarted fired under edge-case timing or custom loaders.
        if (_actor == null && Context.IsWorldReady && Game1.currentLocation != null)
        {
            if (PlayerModeEnabled) InitializePlayerMode(); else InitializeCompanion();
        }

        // Overnight dialogs expose tomorrow's date while maps and soil still
        // belong to yesterday. Keep native commands queued and actors still
        // until DayStarted has completed recovery for that same date.
        if (!Context.IsWorldReady || (!PlayerModeEnabled && (_dayTransition || (_actor != null && _resourceDay != Game1.Date.TotalDays))))
            return;

        // 1. Process transport messages (cancellation, pause, disconnect) FIRST
        MeasureDesktopPhase("before-transport");
        _transportServer?.Update();
        MeasureDesktopPhase("transport");
        if (PlayerModeEnabled) {
            if (_coordinator?.ActiveMachine?.IsExecuting == true) {
                _thinking?.SuspendForAction();
                if (Game1.activeClickableMenu is CompanionConversationMenu or CompanionCommandMenu or CompanionDashboardMenu or CompanionLifeMenu) Game1.activeClickableMenu.exitThisMenu();
            } else _thinking?.ResumeAfterAction();
        }
        var connectionProblem = GetBridgeConnectionProblem();
        if (CompanionCommandMenu.TaskState.ConnectionProblem != connectionProblem) ProjectBridgeConnection();

        // A native command can arrive after the player requested pause but
        // before its machine existed. Apply the shared gate to the new machine
        // before any coordinator tick, not merely at the control button click.
        var activeMachine = _coordinator?.ActiveMachine;
        if (_chatUiState.ApplyNativePauseGate(activeMachine))
        {
            _localPauseMachine = activeMachine;
            _deferredResumeMachine = null;
        }

        // 2. Only advance game action if not cancelled or paused
        if (_coordinator != null) _coordinator.Rest.Paused = _chatUiState.ShouldPauseNative || _desktopPaused || _desktopInterrupting;
        MeasureDesktopPhase("before-native-action");
        _coordinator?.Update(Game1.currentGameTime, e.Ticks);
        MeasureDesktopPhase("native-action");
        if (_coordinator != null && _lastRestState != _coordinator.Rest.State)
        {
            _lastRestState = _coordinator.Rest.State;
            if (!_coordinator.Rest.IsDaytimeRest && _coordinator.Rest.SleepStartedAt is { } asleepAt) _overnightBedtime = asleepAt;
            PersistActorState("bedtime transition");
        }
        if (e.IsMultipleOf(15) && _coordinator != null)
        {
            var water = _coordinator.StateMachine;
            var harvest = _coordinator.HarvestMachine;
            if (water.IsExecuting)
                CompanionCommandMenu.TaskState.NativeProgress("浇水", water.IsPaused ? "已暂停" : water.CurrentState.ToString() == "Navigating" ? "前往作物" : "照料作物", water.WateredCount, water.TotalTargets);
            else if (harvest?.IsExecuting == true)
                CompanionCommandMenu.TaskState.NativeProgress("收获", harvest.IsPaused ? "已暂停" : harvest.CurrentState.ToString() == "Navigating" ? "前往作物" : "收获作物", harvest.HarvestedCount, harvest.TotalTargets);
        }
        if (!_chatUiState.ShouldPauseNative && _deferredResumeMachine != null && _deferredResumeMachine.IsPaused)
        {
            _deferredResumeMachine.Resume();
            _deferredResumeMachine = null;
            _localPauseMachine = null;
            ProjectChatUiState();
        }
        if (_localPauseMachine != null && !_localPauseMachine.IsExecuting)
        {
            _localPauseMachine = null;
            _deferredResumeMachine = null;
            _chatUiState.NoteLocalResumed();
            ProjectChatUiState();
        }

        // 2b. Life profile auto-fetch: once transport is ready and not yet fetched
        if (!_lifeSaveProfileFetched && _transportServer?.IsChatConnected == true && _actor != null)
        {
            _lifeSaveProfileFetched = true;
            string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
            string reqId = Guid.NewGuid().ToString("N")[..8];
            _ = Task.Run(async () =>
            {
                try
                {
                    await _transportServer!.SendLifeProfileGetAsync(
                        new LifeProfileGetPayload(reqId, saveId)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"life.profile.get send failed: {ex.Message}", LogLevel.Warn);
                }
            });
        }

        // 2b2. Independent autostart test: dispatch the preset once readiness holds.
        TryDispatchPresetGoal();
        _preset?.NoteNativeActivity(_coordinator?.GetActivityStatus() is { } activity && activity != "idle");

        // 2c. §1.8 start-together timeout: abort with real state kept and retry hint.
        if (_lifeStartSequence.Step != LifeStartStep.Idle &&
            DateTime.UtcNow - _lifeStartSequence.StepSentAtUtc > TimeSpan.FromSeconds(30))
            AbortLifeStart("等待确认超时。");

        // 2d. Drain deferred care hints once nothing blocks the HUD.
        if (!_desktopMode) PumpNativeChat();
        if (Game1.activeClickableMenu == null && !Game1.eventUp)
        {
            foreach (var hint in _careHintController.TryDrainDeferred())
                if (_lifeMenuUiState.Conversation.Any(e => e.Id == "care:" + hint.EventKey && e.Unread))
                    ShowCompanionHud(new HUDMessage($"{_lifeMenuUiState.CompanionName}有新消息 · 请看聊天栏"));
        }

        if (Game1.activeClickableMenu is CompanionCommandMenu && !_chatUiState.HasActiveCommand && !_chatUiState.HasPendingControl && _coordinator != null && _coordinator.GetActivityStatus() != "idle")
        {
            CompanionCommandMenu.CurrentStatusText = _coordinator.GetActivityStatus() switch
            {
                "idle" => "待命",
                "paused" => "已暂停",
                var status => status
            };
        }
        if (_coordinator?.Rest.IsResting == true)
            CompanionCommandMenu.TaskState.SetRestState(_coordinator.Rest.State, _coordinator.Rest.Reason, _coordinator.Rest.IsDaytimeRest);
        ProjectChatUiAvailability();
        if (Game1.activeClickableMenu is CompanionCommandMenu && !_chatUiState.HasActiveCommand && !_chatUiState.HasPendingControl && !_chatUiState.IsPaused && !_chatUiState.LocalPauseRequested && _transportServer?.IsChatConnected != true)
            CompanionCommandMenu.CurrentStatusText = GetBridgeConnectionProblem() ?? "桥接未连接，输入会保留";
    }

    private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        if (!Context.IsWorldReady || (!PlayerModeEnabled && (_dayTransition || _resourceDay != Game1.Date.TotalDays)))
            return;
        _coordinator?.OnTimeChanged(e.NewTime);
    }

    private void OnCompanionNotification(string message)
    {
        if (Context.IsWorldReady)
        {
            ShowCompanionHud(new HUDMessage(message));
        }
    }

    /// <summary>
    /// Structured progress from the native execution state machine (Q5). F8 gets a
    /// real action/phase/completed/total line; when the action has no countable
    /// targets (total 0) no percentage is shown.
    /// </summary>
    private void HandleNativeActionProgress(StardewAI.Companion.Mod.Execution.NativeActionProgress progress)
    {
        if (progress is null)
            return;

        string actionName = progress.Action switch
        {
            "RefillWateringCan" or "refill-watering-can" => "加水", "ApplyFertilizer" => "施肥", "ClearDebris" => "清理杂物",
            "PickupItems" => "拾取", "InsertMachine" => "投放机器", "CollectMachine" => "收取机器",
            "PetAnimal" => "抚摸动物", "FeedAnimals" => "喂养动物", "ToggleAnimalDoor" => "开关畜舍门",
            "CollectAnimalProduce" => "收取畜产品", "ChopTree" => "砍树",
            "build-building" => "建造建筑", "upgrade-building" => "升级建筑",
            "purchase-animal" => "购买动物", "cut-grass" => "割草", "eat-food" => "吃东西",
            _ => progress.Action
        };
        string phaseName = progress.Phase switch
        {
            "Navigating" => "前往目标", "Facing" => "面向目标", "Acting" => "执行",
            "Verifying" => "核对结果", "Paused" => "已暂停", "Cancelling" => "取消中",
            "completed" => "已完成", "partial" => "部分完成", "failed" or "rejected" => "未完成",
            _ => progress.Phase
        };
        string text = $"行动 {actionName} · 阶段 {phaseName}";
        if (progress.Total > 0)
            text += $" · 完成 {progress.Completed}/{progress.Total}";
        if (!string.IsNullOrEmpty(progress.ReasonCode))
            text += $" · {progress.ReasonCode}";

        CompanionCommandMenu.StructuredProgressText = text;
        CompanionCommandMenu.CurrentToolName = progress.Action;
        CompanionCommandMenu.TaskState.NativeProgress(actionName, phaseName, progress.Completed, progress.Total);
    }


}
