using System.Text.Json.Nodes;
using StardewAI.Companion.Mod.Execution;
using StardewAI.Companion.Mod.Hosting;
using StardewAI.Companion.Mod.Menus;
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
        LogDesktopGameEvent("save_loaded", "游戏已加载存档。", new JsonObject { ["mode"] = PlayerModeEnabled ? "player" : "companion" });
        coordinator.SetTransportServer(server);
        coordinator.ConnectionActionsAllowed = () => DesktopActionsAllowed;
        if (_desktopMode)
            coordinator.OnExecutionCompleted += () =>
            {
                if (coordinator.LastSkillResultPayload is { } result)
                    LogDesktopGameEvent("action_completed", "Mod 已结算原生动作。", new JsonObject
                    {
                        ["commandId"] = result.CommandId, ["skillId"] = result.SkillId,
                        ["status"] = result.TerminalState, ["code"] = result.Error?.Code,
                        ["completedCount"] = result.CompletedCount,
                    });
            };

        void InSession(Action action) => QueueTransportAction(server, action);
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

    private string? GetBridgeConnectionProblem() => _transportServer?.IsChatConnected == true ? null
        : "伙伴服务尚未连接，请检查伴侣后台和模型配置。";

    private void ProjectBridgeConnection()
    {
        if (GetBridgeConnectionProblem() is { } problem)
            CompanionCommandMenu.TaskState.Disconnected(problem, false);
        else CompanionCommandMenu.TaskState.Connected();
    }
}
