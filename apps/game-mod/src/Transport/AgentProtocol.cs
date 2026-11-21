namespace StardewAI.Companion.Mod.Transport;

public static class AgentProtocol
{
    public const string Version = "2";
    public static bool IsTerminalState(string? state) =>
        state is "succeeded" or "partially-succeeded" or "failed" or "cancelled" or "rejected";

    public static string ClassifyReceipt(string version, string messageType, string? saveId, string? gameSessionId,
        string? actionId, string? terminalState, string activeSaveId, string activeGameSessionId,
        string? expectedActionId, IReadOnlySet<string> settledActions)
    {
        if (version != Version) return "version-mismatch";
        if (saveId != activeSaveId || gameSessionId != activeGameSessionId) return "stale-game";
        if (!string.IsNullOrEmpty(actionId) && settledActions.Contains(actionId)) return "duplicate";
        if (string.IsNullOrEmpty(actionId) || actionId != expectedActionId) return "unrelated";
        if (messageType == "skill.result.status") return "pending";
        if (messageType != "skill.result") return "unrelated";
        return IsTerminalState(terminalState) ? "terminal" : "pending";
    }
}
