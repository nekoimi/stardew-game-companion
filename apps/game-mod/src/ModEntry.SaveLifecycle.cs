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
    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        => InitializeLoadedWorld();

    private void InitializeLoadedWorld()
    {
        _dayTransition = false;
        if (!_desktopMode) ChineseUiLocale.Apply(Monitor.Log);
        if (PlayerModeEnabled)
        {
            InitializePlayerMode();
            ShowCompanionHud(new HUDMessage($"AI 自己玩：聊天栏随时指导，{_config.TakeoverKey} 开始托管 / 接管。", HUDMessage.newQuest_type));
        }
        else InitializeCompanion();
    }

    private int? _resourceDay;
    private int _overnightBedtime = 2200;
    private bool _dayTransition;
    private string _lastRestState = "awake";
    private readonly LifeControlAcknowledgements _lifeControls = new();

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        if (PlayerModeEnabled) { _thinking?.ReleaseAll("day-ending"); _dayTransition=true; return; }
        _overnightBedtime = _coordinator?.Rest.IsDaytimeRest == false
            ? _coordinator.Rest.SleepStartedAt ?? Game1.timeOfDay : Game1.timeOfDay;
        _dayTransition = true;
        _actor?.Halt();
    }

    private void RecoverCompanionForNewDay()
    {
        if (PlayerModeEnabled) return;
        if (_actor?.GameFarmer is not { } farmer) return;
        int today = Game1.Date.TotalDays;
        if (_resourceDay is not null && today > _resourceDay.Value)
        {
            float before = farmer.Stamina;
            NativeOvernightRecovery.Apply(farmer, _overnightBedtime);
            Monitor.Log($"Companion native overnight recovery: stamina {before} -> {farmer.Stamina}; water remains { _actor.WaterLeft }.", LogLevel.Info);
            _resourceDay = today;
            PersistActorState("native overnight recovery");
        }
        _resourceDay = today;
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (PlayerModeEnabled)
        {
            _resourceDay = Game1.Date.TotalDays;
            _dayTransition = false;
            _coordinator?.OnTimeChanged(Game1.timeOfDay);
            _careHintController.OnDayStarted(GetCurrentGameDate());
            _lifeMenuUiState.MarkAllCareHintsRead();
            return;
        }
        if (_actor == null)
        {
            InitializeCompanion();
        }

        RecoverCompanionForNewDay();
        // The detached companion is not included in the game's farmer reload.
        // Rebind its visual resources after overnight content cleanup, while
        // preserving the same actor, inventory, position and resource values.
        if (_actor?.GameFarmer is { } companionFarmer)
        {
            var previousRenderer = companionFarmer.FarmerRenderer;
            var renderer = new FarmerRenderer(previousRenderer.textureName.Value, companionFarmer);
            // Some appearance values (notably starter shoes without equipped
            // boots) live on the renderer, not in UpdateClothing's equipment.
            renderer.heightOffset.Value = previousRenderer.heightOffset.Value;
            renderer.eyes.Value = previousRenderer.eyes.Value;
            renderer.skin.Value = previousRenderer.skin.Value;
            renderer.shoes.Value = previousRenderer.shoes.Value;
            renderer.shirt.Value = previousRenderer.shirt.Value;
            renderer.pants.Value = previousRenderer.pants.Value;
            companionFarmer.FarmerRenderer = renderer;
            companionFarmer.UpdateClothing();
            _companionDialogue?.InvalidatePortrait();
        }
        _coordinator?.Rest.Wake();
        PersistActorState("woke for new day");
        _dayTransition = false;

        // The native calendar changes before overnight dialogs and saving finish.
        // Publish the new day only after its resource recovery has completed.
        _coordinator?.OnTimeChanged(Game1.timeOfDay);

        // Expire care hints from the previous day (§1.7: unread hints are NOT carried over).
        _careHintController.OnDayStarted(GetCurrentGameDate());
        _lifeMenuUiState.MarkAllCareHintsRead();
    }


    private void ReleasePlayerControl(string reason)
    {
        if (!PlayerModeEnabled) return;
        _playerControlApproved = false;
        _thinking?.ReleaseAll(reason);
        if (reason == "disconnected" && _thinking?.CurrentHold is not { Active: true }
            && _preset is { Enabled: true, Phase: not (PresetGoalPhase.Done or PresetGoalPhase.Cancelled) })
            _thinking?.StopForFailure("独立测试连接中断，已保护暂停；恢复连接或接管后继续。");
        _coordinator?.ActiveMachine?.RequestCancel(reason);
        _actor?.Halt();
        _authority?.Release(reason);
    }

    private void InitializePlayerMode()
    {
        if (Game1.IsMultiplayer) { Monitor.Log("Player mode unsupported in multiplayer.", LogLevel.Error); return; }
        string saveId = Constants.SaveFolderName!;
        _actor = new PlayerActor(Game1.player, Monitor.Log);
        _resourceDay = Game1.Date.TotalDays;
        _avatar = null;
        _knowledge = new PlayerKnowledgeTracker();
        _knowledge.Load(Helper.Data);
        _observer = new PlayerKnownObserver(new GameWorldObserver(Monitor) {
            NativeCollisionActor = () => _actor?.GameFarmer
        }, _knowledge, Monitor.Log);
        _navigator = new SameMapNavigator(_observer);
        _authority = new PlayerControlAuthority(Monitor.Log);
        _playerControlApproved = false;
        NativePlayerIntentHook.Bind((PlayerActor)_actor,_authority);
        _thinking = new DecisionPauseController(TimeSpan.FromSeconds(_config.ThinkingPauseMaxSeconds), Monitor.Log);
        _coordinator = NativeExecutionFactory.Create(Helper, Monitor, _actor, _observer, _navigator);
        _coordinator.PlayerAuthority = _authority;
        _coordinator.ProgramHoldProvider = () =>
            _thinking?.CurrentHold is { Active: true } hold
                ? new ProgramHoldSnapshot(true, hold.Reason, hold.HoldId, hold.SinceUtc)
                : null;
        _coordinator.ClockPauseOwnerProvider = () => Game1.isTimePaused
            ? _thinking?.CurrentHold is { Active: true } ? "program-protection"
                : _thinking?.IsPausing == true && _thinking.ActiveLeaseId != null
                    ? "model-thinking" : "external"
            : Game1.activeClickableMenu != null ? "native-menu" : null;
        var transport = BindGameTransport(saveId, _coordinator);
        transport.Server.ActorMode = "player";
        transport.Server.ActorMultiplayerId = Game1.player.UniqueMultiplayerID;
        transport.Server.SupportsThinkingPause = _config.PauseClockWhileThinking;
        _authority.Changed += (owner, reason) => { _ = transport.Server.SendPlayerAuthorityAsync(owner == PlayerControlOwner.Agent ? "agent" : "human",reason); };
        // F9 takeover/cancel must not restart the preset: releasing to the human only
        // re-arms the one-shot so an explicit re-handoff resumes the already-bound goal.
        _authority.Changed += (owner, reason) => { if (owner == PlayerControlOwner.Human) _preset?.NoteAuthorityHuman(); };
        transport.Server.OnPlayerMessage += (type, payload) => QueueTransportAction(transport.Server, () => {
            if (type == "disconnected") { ReleasePlayerControl("disconnected"); return; }
            if (type == "player.control") {
                if (payload["action"]?.GetValue<string>() == "claim") {
                    if (_playerControlApproved || _preset?.Enabled == true) _authority.RequestAgent("explicit-claim");
                    else _ = transport.Server.SendPlayerAuthorityAsync("human", "awaiting-user");
                }
                else ReleasePlayerControl("runtime-release");
            } else if (type == "agent.thinking") {
                var id = payload["leaseId"]?.GetValue<string>() ?? "";
                // A fresh model turn is proof the program side is alive again: release
                // any failure hold before the new thinking lease takes over the clock.
                if (payload["state"]?.GetValue<string>() == "begin" && _authority.AgentOwns && _config.PauseClockWhileThinking) { _thinking.ClearFailureStop(); _thinking.Begin(id); }
                else if (payload["state"]?.GetValue<string>() == "renew" && _authority.AgentOwns && _config.PauseClockWhileThinking && (_actor as PlayerActor)?.BindingValid == true) _thinking.Renew(id);
                else if (payload["state"]?.GetValue<string>() == "end") _thinking.End(id);
            }
        });
        _conversationSaveId = saveId;
        _lifeMenuUiState.Reset();
        var nativeCheckpoint=Helper.Data.ReadSaveData<CompanionSaveCheckpoint>("player-control-checkpoint-v1");
        if (nativeCheckpoint != null && (nativeCheckpoint.SaveId != saveId || nativeCheckpoint.NativePlayerId != Game1.player.UniqueMultiplayerID))
            throw new InvalidOperationException("Player auxiliary checkpoint identity does not match the native save/player.");
        var previousPlayerConversation=_conversationStore?.LoadHistory(saveId) ?? new List<CompanionConversationEntry>();
        _lifeMenuUiState.RestoreConversation(CompanionSaveCheckpoint.RestoreWithReloadHistory(nativeCheckpoint?.Conversation ?? new(),previousPlayerConversation));
        CompanionSaveCheckpoint.PublishLoad(Helper.DirectoryPath,saveId,transport.Id,GetCurrentGameDate(),nativeCheckpoint,previousPlayerConversation,null);
        AttachDesktopControls(transport.Server);
        transport.Server.Start();
        _lifeControls.Supersede();
        CompanionCommandMenu.TaskState.Reset();
        _companionDialogue?.Reset();
        _careHintController.OnDayStarted(GetCurrentGameDate());

        // Persist companion state on every task completion, not only at game-save time:
        // save/exit/reload durability must not depend on save-event timing.

        _coordinator.OnNotification += OnCompanionNotification;
        _coordinator.OnNativeActionProgress += HandleNativeActionProgress;

        // Independent autostart test (isolated launcher opt-in). Loading binds the
        // stable per-run/save request id and restores any persisted dispatch marker,
        // so a relaunch resumes rather than duplicating the goal.
        _preset = PresetGoalAutostart.Load(
            Environment.GetEnvironmentVariable("STARDEW_AI_PRESET_GOAL_FILE"),
            saveId,
            Path.Combine(Helper.DirectoryPath, "data", "preset-goal-state.json"),
            Monitor.Log);
        if (_preset.Enabled)
        {
            _preset.InitializeNativeDay(Game1.Date.TotalDays);
            _coordinator.PresetBusinessAllowed = () => _preset.BusinessAllowed(Game1.Date.TotalDays);
        }
        if (_preset.Enabled)
            Monitor.Log($"Preset autostart armed: runId={_preset.RunId} requestId={_preset.RequestId} phase={_preset.Phase}", LogLevel.Info);

        // 5. Publish local restricted discovery (never log sessionToken!)
        transport.Publish();
        TryAutoStartChatBridge();


        Monitor.Log($"Player binding active: save={saveId} id={Game1.player.UniqueMultiplayerID}, no companion actor.", LogLevel.Info);
    }

    private void InitializeCompanion()
    {
        if (!Context.IsWorldReady || Game1.currentLocation == null)
            return;

        try
        {
            // 1. Per-save state partition: scope repository strictly by active save folder
            string saveId = Constants.SaveFolderName ?? "default-save";
            string storageDir = Path.Combine(Helper.DirectoryPath, "data", saveId);
            _stateRepository = new JsonActorStateRepository(storageDir);
            var checkpoint = Helper.Data.ReadSaveData<CompanionSaveCheckpoint>("companion-checkpoint-v1");
            if (checkpoint?.SaveId != saveId) checkpoint = null;
            if (checkpoint?.Actor != null) _stateRepository.Save(checkpoint.Actor);

            _observer = new GameWorldObserver(Monitor);
            bool isNewCompanion = !_stateRepository.StateExists("companion-1");
            CompanionActorState activeState;
            GameLocation? targetLocation;
            TileCoordinate activeTile;

            if (isNewCompanion)
            {
                // Fresh-state initialization
                string locName = "Farm";
                targetLocation = Game1.getLocationFromName(locName) ?? Game1.currentLocation;
                if (targetLocation == null)
                {
                    Monitor.Log("Cannot place fresh companion: Farm location not found.", LogLevel.Error);
                    return;
                }

                // Compute a legal initial tile for fresh actor
                var candidateTile = new TileCoordinate(64, 15);
                if (!_observer.IsTilePassable(targetLocation.Name, candidateTile))
                {
                    bool foundLegal = false;
                    for (int r = 1; r <= 4 && !foundLegal; r++)
                    {
                        for (int dx = -r; dx <= r && !foundLegal; dx++)
                        {
                            for (int dy = -r; dy <= r && !foundLegal; dy++)
                            {
                                var testTile = new TileCoordinate(candidateTile.X + dx, candidateTile.Y + dy);
                                if (_observer.IsTilePassable(targetLocation.Name, testTile))
                                {
                                    candidateTile = testTile;
                                    foundLegal = true;
                                }
                            }
                        }
                    }
                    if (!foundLegal)
                    {
                        Monitor.Log($"Cannot place fresh companion: Farm tile {candidateTile} and surroundings are impassable.", LogLevel.Error);
                        return;
                    }
                }

                activeTile = candidateTile;
                activeState = CompanionActorState.CreateDefault("companion-1");
                activeState.Pose = new AuthoritativePose(targetLocation.Name, activeTile, FacingDirection.Down);
                _stateRepository.Save(activeState);
                Monitor.Log($"Fresh companion initialized at legal tile {activeTile} on {targetLocation.Name}.", LogLevel.Info);

            }
            else
            {
                // Restoration of existing persisted companion
                activeState = _stateRepository.LoadOrInitializeDefaults("companion-1");
                targetLocation = Game1.getLocationFromName(activeState.Pose.LocationName);
                string? currLocName = !string.IsNullOrWhiteSpace(Game1.currentLocation?.NameOrUniqueName)
                    ? Game1.currentLocation.NameOrUniqueName
                    : Game1.currentLocation?.Name;
                if (targetLocation == null && Game1.currentLocation != null &&
                    string.Equals(currLocName, activeState.Pose.LocationName, StringComparison.OrdinalIgnoreCase))
                {
                    targetLocation = Game1.currentLocation;
                }

                if (targetLocation == null)
                {
                    Monitor.Log($"Cannot restore companion: Saved location '{activeState.Pose.LocationName}' cannot be resolved.", LogLevel.Error);
                    return;
                }

                activeTile = activeState.Pose.Tile;
                // Preserve strict restoration, except a transparent one-cell escape
                // from a newly occupying collectible ordinary native object.
                string locKey = !string.IsNullOrWhiteSpace(targetLocation.NameOrUniqueName)
                    ? targetLocation.NameOrUniqueName
                    : targetLocation.Name;
                // Waking clears the sleep timestamp while the actor still occupies
                // its native bed. Position validity is independent of sleep state.
                bool savedBedPose =
                    StardewValley.Objects.BedFurniture.GetBedAtTile(targetLocation, activeTile.X, activeTile.Y) != null;
                if (!savedBedPose && !_observer.IsTilePassable(locKey, activeTile))
                {
                    var original = activeTile;
                    var position = new Microsoft.Xna.Framework.Vector2(original.X, original.Y);
                    var blocker = targetLocation.getObjectAtTile(original.X, original.Y);
                    var box = new Microsoft.Xna.Framework.Rectangle(original.X * 64, original.Y * 64, 64, 64);
                    var layer = targetLocation.Map?.Layers.FirstOrDefault();
                    bool otherwiseLegal = layer != null && original.X >= 0 && original.Y >= 0 &&
                        original.X < layer.LayerWidth && original.Y < layer.LayerHeight &&
                        targetLocation.isTilePassable(new xTile.Dimensions.Location(original.X, original.Y), Game1.viewport) &&
                        !targetLocation.buildings.Any(b => b.occupiesTile(position) && !b.isTilePassable(position)) &&
                        !targetLocation.furniture.Any(f => !f.isPassable() && f.GetBoundingBox().Intersects(box)) &&
                        !targetLocation.largeTerrainFeatures.Any(f => !f.isPassable() && f.getBoundingBox().Intersects(box)) &&
                        !targetLocation.resourceClumps.Any(c => c.getBoundingBox().Intersects(box)) &&
                        (!targetLocation.terrainFeatures.TryGetValue(position, out var terrain) || terrain.isPassable()) &&
                        !_observer.IsWarpOrDoorTile(locKey, original);
                    var replacement = AdjacentRestorePlacement.Find(original,
                        blocker != null && blocker.canBeGrabbed.Value && !blocker.bigCraftable.Value,
                        otherwiseLegal,
                        tile => _observer.IsTilePassable(locKey, tile) && !_observer.IsWarpOrDoorTile(locKey, tile) &&
                            !Game1.getAllFarmers().Any(f => ReferenceEquals(f.currentLocation, targetLocation) &&
                                f.GetBoundingBox().Intersects(new Microsoft.Xna.Framework.Rectangle(tile.X * 64, tile.Y * 64, 64, 64))));
                    if (replacement is null)
                    {
                        Monitor.Log($"Cannot restore companion: Persisted tile {activeTile} on '{locKey}' is blocked/impassable; no eligible adjacent collectible-object recovery. Halting placement safely.", LogLevel.Error);
                        return;
                    }
                    activeTile = replacement.Value;
                    activeState.Pose = activeState.Pose with { Tile = activeTile };
                    Monitor.Log($"Companion restore placement on '{locKey}': {original} -> {activeTile}; blocking collectible object '{blocker!.QualifiedItemId}' ({blocker.Name}) retained unchanged.", LogLevel.Warn);
                }
            }

            // 3. Instantiate persistent independent Farmer Mechanics Actor and equipped WateringCan
            var wateringCan = new WateringCan { WaterLeft = activeState.Water };
            _resourceDay = activeState.ResourceDay ?? Game1.Date.TotalDays;
            _overnightBedtime = activeState.OvernightBedtime;
            var gameFarmer = new Farmer();
            gameFarmer.exhausted.Value = activeState.Exhausted;
            gameFarmer.Name = "Companion";
            gameFarmer.UniqueMultiplayerID = 9876543210L;
            gameFarmer.Stamina = activeState.Stamina;
            gameFarmer.maxStamina.Value = (int)activeState.MaxStamina;
            gameFarmer.Position = new Microsoft.Xna.Framework.Vector2(activeTile.X * 64, activeTile.Y * 64);
            gameFarmer.faceDirection((int)activeState.Pose.Facing);
            gameFarmer.currentLocation = targetLocation;

            // Visual configuration: setup clothing, hair, skin, and FakeEventActor flags for rendering
            gameFarmer.isFakeEventActor = true;
            gameFarmer.hidden.Value = false;
            gameFarmer.viewingLocation.Value = null;
            gameFarmer.Sprite = new FarmerSprite("Characters\\Farmer\\farmer_base");
            // Farmer.draw renders through FarmerRenderer (not Farmer.Sprite): without a textured
            // renderer the detached farmer is logically present but completely invisible.
            gameFarmer.FarmerRenderer = new FarmerRenderer("Characters\\Farmer\\farmer_base", gameFarmer);
            gameFarmer.changeGender(true);
            gameFarmer.changeShirt("1000");
            gameFarmer.changePantStyle("Basic");
            gameFarmer.changePantsColor(new Microsoft.Xna.Framework.Color(46, 85, 180));
            gameFarmer.changeSkinColor(0);
            gameFarmer.changeHairStyle(0);
            gameFarmer.changeHairColor(new Microsoft.Xna.Framework.Color(137, 70, 48));
            gameFarmer.changeEyeColor(new Microsoft.Xna.Framework.Color(137, 70, 48));
            gameFarmer.changeShoeColor("2");
            gameFarmer.UpdateClothing();

            // Populate hotbar slots explicitly and verify equipped tool identity
            while (gameFarmer.Items.Count < 12)
            {
                gameFarmer.Items.Add(null);
            }
            var hoe = new Hoe();
            int toolSlot = 0;
            gameFarmer.Items[toolSlot] = wateringCan;
            gameFarmer.Items[1] = hoe;
            gameFarmer.CurrentToolIndex = toolSlot;

            var restoredActor = new FarmerMechanicsActor("companion-1", gameFarmer, wateringCan, targetLocation.Name, activeTile, Monitor.Log, hoe: hoe);
            restoredActor.ApplyPersistentState(activeState);
            // A failed item restore must not leave a partial actor eligible for saving.
            _actor = restoredActor;
            // Grass cutting uses the companion's own native Scythe. Supply the
            // ordinary starter tool on first load and on older saved actors;
            // never borrow or modify the human player's inventory.
            if (!gameFarmer.Items.OfType<MeleeWeapon>().Any(tool => tool.isScythe()))
            {
                var scythe = ItemRegistry.Create("(W)47");
                if (scythe is not MeleeWeapon weapon || !weapon.isScythe())
                    throw new InvalidOperationException("Native starter Scythe (W)47 is unavailable.");
                if (!_actor.TryAddItemToInventory(weapon))
                    Monitor.Log("Companion cannot receive its starter Scythe: inventory is full.", LogLevel.Warn);
            }


            _avatar = new CompanionAvatar(_actor, msg => Monitor.Log(msg, LogLevel.Info));
            _navigator = new SameMapNavigator(_observer);
            _coordinator = NativeExecutionFactory.Create(Helper, Monitor, _actor, _observer, _navigator, _avatar);
            _coordinator.Rest.Bedtime = CompanionBedtime.Normalize(activeState.PreferredBedtime);
            if (activeState.SleepDay == Game1.Date.TotalDays && activeState.SleepStartedAt is { } sleepTime)
                _coordinator.Rest.RestoreSleep(sleepTime, activeState.SleepIsDaytime);
            _lastRestState = _coordinator.Rest.State;

            var transport = BindGameTransport(saveId, _coordinator);

            // Reset life-session state for the new save
            _lifeSaveProfileFetched = false;
            _lifeOnboardingHudShown = false;
            _lifeStartSequence.Abort();
            _lifeMenuUiState.Reset();
            CompanionConversationMenu.DraftText = string.Empty;
            CompanionConversationMenu.DraftDecisionId = null;
            _conversationSaveId = saveId;
            try
            {
                var previous = _conversationStore!.LoadHistory(saveId);
                var history = checkpoint?.Conversation ?? CompanionSaveCheckpoint.LegacyHistoryBeforeDay(
                    previous, GetCurrentGameDate()).ToList();
                string? archive = previous.Count > 0 ? _conversationStore.ArchiveReload(saveId, previous) : null;
                _lifeMenuUiState.RestoreConversation(CompanionSaveCheckpoint.RestoreWithReloadHistory(history, previous));
                PersistConversation();
                CompanionSaveCheckpoint.PublishLoad(Helper.DirectoryPath, saveId, transport.Id,
                    GetCurrentGameDate(), checkpoint, previous, archive);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Could not establish reload state; companion connection remains stopped: {ex.Message}", LogLevel.Error);
                throw;
            }
            AttachDesktopControls(transport.Server);
            transport.Server.Start();
            _lifeControls.Supersede();
            CompanionCommandMenu.TaskState.Reset();
            _companionDialogue?.Reset();
            _careHintController.OnDayStarted(GetCurrentGameDate());

            // Persist companion state on every task completion, not only at game-save time:
            // save/exit/reload durability must not depend on save-event timing.
            _coordinator.OnExecutionCompleted += () => PersistActorState("task completion");
            _coordinator.OnInventoryChanged += () => PersistActorState("nearby pickup");
            _coordinator.OnNotification += OnCompanionNotification;
            _coordinator.OnNativeActionProgress += HandleNativeActionProgress;

            // 5. Publish local restricted discovery (never log sessionToken!)
            transport.Publish();
            TryAutoStartChatBridge();

            Monitor.Log($"Companion Mechanics Actor and Transport online on port {transport.Server.Port} (Save: {saveId}).", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Failed to initialize companion: {ex}", LogLevel.Error);
        }
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (PlayerModeEnabled) {
            // Saving continues a committed native sleep, not a human takeover.
            _thinking?.ReleaseAll("saving"); _knowledge?.SaveIfDirty(Helper.Data);
            if (_conversationSaveId is { } nativeSaveId && _actor is PlayerActor boundPlayer && boundPlayer.BindingValid)
                Helper.Data.WriteSaveData("player-control-checkpoint-v1",new CompanionSaveCheckpoint {
                    SaveId=nativeSaveId,NativePlayerId=Game1.player.UniqueMultiplayerID,
                    Conversation=CompanionConversationStore.Retain(_lifeMenuUiState.Conversation),
                    RuntimePartitions=CompanionSaveCheckpoint.CaptureRuntime(Helper.DirectoryPath,nativeSaveId),
                    Actor=null, // Native SaveGame exclusively owns position, inventory and resources.
                });
            return;
        }
        try
        {
            // Quiesce state machine on saving
            if (_coordinator?.StateMachine != null && _coordinator.StateMachine.IsExecuting)
            {
                _coordinator.StateMachine.RequestPause();
                _actor?.Halt();
                Monitor.Log("Quiesced companion state machine for save event.", LogLevel.Info);
            }

            if (_actor != null && _stateRepository != null)
            {
                PersistActorState("save event");
            }
            if (_conversationSaveId is { } saveId)
            {
                Helper.Data.WriteSaveData("companion-checkpoint-v1", new CompanionSaveCheckpoint
                {
                    SaveId = saveId,
                    Conversation = CompanionConversationStore.Retain(_lifeMenuUiState.Conversation),
                    Actor = _stateRepository?.LoadOrInitializeDefaults("companion-1"),
                    RuntimePartitions = CompanionSaveCheckpoint.CaptureRuntime(Helper.DirectoryPath, saveId),
                });
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"Error during companion saving: {ex}", LogLevel.Error);
        }
    }

    private void PersistActorState(string reason)
    {
        if (PlayerModeEnabled) return;
        try
        {
            if (_actor != null && _stateRepository != null)
            {
                var state = _actor.CapturePersistentState();
                state.ResourceDay = _resourceDay;
                state.OvernightBedtime = _overnightBedtime;
                state.PreferredBedtime = _coordinator?.Rest.Bedtime ?? CompanionBedtime.Default;
                state.SleepStartedAt = _coordinator?.Rest.SleepStartedAt;
                state.SleepIsDaytime = _coordinator?.Rest.IsDaytimeRest == true;
                state.SleepDay = state.SleepStartedAt.HasValue ? Game1.Date.TotalDays : null;
                state.Exhausted = _actor.GameFarmer?.exhausted.Value == true;
                _stateRepository.Save(state);
                Monitor.Log($"Companion actor state persisted after {reason}.", LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"Error persisting companion state ({reason}): {ex}", LogLevel.Error);
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _nativeChatSave = null;
        _nativeChatDecisionSave = null;
        _unsentChatText = null;
        ReleasePlayerControl("unload");
        StopOwnedBridgeLauncher();
        ChineseUiLocale.Restore();
        _dayTransition = false;
        try
        {
            if (_transportSession is { } session) session.Dispose();
            else _discoveryService?.Invalidate();
            _transportSession = null;
            _transportServer = null;
            _mainThreadActions.Clear();
            _decisionEdits.Clear();
            _bridgeStartup.Reset();
            _connectionState.ResetConnection();

            _actor = null;
            _avatar = null;
            _coordinator?.ShutdownExtensions();
            _coordinator = null;
            _preset = null; // re-armed per save load; the persisted marker survives for resume
            _localPauseMachine = null;
            _deferredResumeMachine = null;
            _stateRepository = null;
            _chatUiState.Reset();
            ProjectChatUiState();
            CompanionCommandMenu.DraftText = string.Empty;

            // Life-system reset
            _conversationSaveId = null;
            _lifeMenuUiState.Reset();
            _careHintController.Reset();
            CompanionConversationMenu.DraftText = string.Empty;
            CompanionConversationMenu.DraftDecisionId = null;
            _lifeControls.Supersede();
            CompanionCommandMenu.TaskState.Reset();
            _companionDialogue?.Reset();
            _lifeSaveProfileFetched = false;
            _lifeOnboardingHudShown = false;
            _lifeStartSequence.Abort();

            Monitor.Log("Companion actor and transport shutdown cleanly.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Error during companion shutdown: {ex}", LogLevel.Warn);
        }
    }
}
