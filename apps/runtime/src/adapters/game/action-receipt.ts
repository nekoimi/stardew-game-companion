export const gameProtocolVersion = "2";
const terminalStates = new Set(["succeeded", "partially-succeeded", "failed", "cancelled", "rejected"]);

export function isTerminalState(state: unknown): boolean {
  return typeof state === "string" && terminalStates.has(state);
}

export interface GameBinding {
  saveId: string;
  gameSessionId: string;
}

export function classifyReceipt(
  message: {
    protocolVersion: string;
    messageType: string;
    saveId?: string;
    gameSessionId?: string;
    payload: { commandId?: unknown; terminalState?: unknown };
  },
  binding: GameBinding,
  expectedActionId: string | undefined,
  settledActions: ReadonlySet<string>,
): "version-mismatch" | "stale-game" | "duplicate" | "pending" | "unrelated" | "terminal" {
  if (message.protocolVersion !== gameProtocolVersion) return "version-mismatch";
  if (message.saveId !== binding.saveId || message.gameSessionId !== binding.gameSessionId) return "stale-game";
  const id = message.payload.commandId;
  if (typeof id !== "string" || !id) return "unrelated";
  if (settledActions.has(id)) return "duplicate";
  if (id !== expectedActionId) return "unrelated";
  if (message.messageType === "skill.result.status") return "pending";
  if (message.messageType !== "skill.result") return "unrelated";
  return isTerminalState(message.payload.terminalState) ? "terminal" : "pending";
}
