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
    private GameTransportSession BindGameTransport(string saveId, CompanionMechanicsCoordinator coordinator)
    {
        var session = new GameTransportSession(Helper.DirectoryPath, saveId, coordinator, _discoveryService, Monitor);
        var server = session.Server;
        _transportSession = session;
        _transportServer = server;
        coordinator.SetTransportServer(server);
        coordinator.ConnectionActionsAllowed = () => !_connectionState.IsSwitching && DesktopActionsAllowed;

        void InSession(Action action) => QueueTransportAction(server, action);
        server.OnConnectionStateReceived += payload => InSession(() => HandleConnectionState(payload));
        server.OnChatReplyReceived += payload => InSession(() => HandleChatReplyReceived(payload));
        server.OnAutonomyStateReceived += payload => InSession(() => HandleAutonomyStateReceived(payload));
        server.OnChatChannelProblem += (request, save, problem) =>
            InSession(() => ShowChatChannelProblem(request, save, problem));
        server.OnLifeChatReplyReceived += payload => InSession(() => HandleLifeChatReplyReceived(payload));
        server.OnLifeProfileStateReceived += payload => InSession(() => HandleLifeProfileStateReceived(payload));
        server.OnLifeMemoryStateReceived += payload => InSession(() => HandleLifeMemoryStateReceived(payload));
        server.OnLifeCareReceived += payload => InSession(() => HandleLifeCareReceived(payload));
        server.OnLifeMilestonesStateReceived += payload => InSession(() => HandleLifeMilestonesStateReceived(payload));
        return session;
    }

    private void QueueTransportAction(WebSocketTransportServer server, Action action)
    {
        _mainThreadActions.Enqueue(() =>
        {
            if (ReferenceEquals(_transportServer, server)) action();
        });
    }

    private void TryAutoStartChatBridge()
    {
        if (_desktopMode) return;
        // Isolated acceptance and manually managed developer sessions own their bridge.
        if (Environment.GetEnvironmentVariable("STARDEW_AI_MANUAL_BRIDGE") == "1") return;
        if (!_config.ConnectionSourceChosen) return;
        if (_transportServer?.IsChatConnected == true || !_bridgeStartup.CanStart(DateTime.UtcNow)) return;
        var start = ReleaseBridgeLauncher.CreateStartInfo(Helper.DirectoryPath, _connectionState.PendingSource ?? _config.ConnectionSource);
        if (start == null) return; // Source builds use the documented developer launcher.
        lock (_bridgeLaunchGate)
        {
            if (_gameExiting) return;
            _ownedBridgeLaunch?.Dispose();
            _ownedBridgeLaunch = ReleaseBridgeLauncher.Start(start);
            _bridgeStartup.Begin(_ownedBridgeLaunch.Completion, DateTime.UtcNow);
        }
        Monitor.Log("Started installed companion service launcher; waiting for connection.", LogLevel.Info);
        ProjectBridgeConnection();
    }

    private void OnGameProcessExit(object? sender, EventArgs e) => StopOwnedBridgeLauncher(gameExiting: true);

    private void StopOwnedBridgeLauncher(bool gameExiting = false)
    {
        // ProcessExit can arrive while the game thread is publishing a startup.
        // Keep publication and cancellation together, and never inspect unrelated processes.
        lock (_bridgeLaunchGate)
        {
            _gameExiting |= gameExiting;
            _ownedBridgeLaunch?.Dispose();
            _ownedBridgeLaunch = null;
        }
    }

    private void UpdateBridgeStartup()
    {
        string? failure = _bridgeStartup.Update(_transportServer?.IsChatConnected == true, DateTime.UtcNow);
        if (failure != null)
        {
            string message = "伙伴服务启动失败：" + failure;
            Monitor.Log(message, LogLevel.Error);
            ShowCompanionHud(new HUDMessage(message, HUDMessage.error_type));
            CompanionCommandMenu.ChatHistory.Add(new ChatMessage("系统", message, Color.Red));
        }
        string? problem = GetBridgeConnectionProblem();
        if (CompanionCommandMenu.TaskState.ConnectionProblem != problem) ProjectBridgeConnection();
    }

    private string? GetBridgeConnectionProblem()
    {
        if (_connectionState.IsSwitching) return _connectionState.Description;
        if (_config.ConnectionSource == "external" && (_transportServer?.IsChatConnected != true || _connectionState.Status != "connected"))
            return "等待已有 AI 伙伴从原会话连接；游戏内消息会保留。";
        if (_transportServer?.IsChatConnected == true) return null;
        if (_bridgeStartup.Problem != null) return "伙伴服务启动失败：" + _bridgeStartup.Problem;
        return _bridgeStartup.IsStarting ? "伙伴服务正在启动，等待连接。"
            : "伙伴服务尚未连接，请检查伙伴后台和模型配置。";
    }

    private void ProjectBridgeConnection()
    {
        _lifeMenuUiState.ApplyConnectionName(_connectionState.Source, _connectionState.ConversationName);
        if (GetBridgeConnectionProblem() is { } problem)
            CompanionCommandMenu.TaskState.Disconnected(problem, _bridgeStartup.Problem != null);
        else CompanionCommandMenu.TaskState.Connected();
    }
}
