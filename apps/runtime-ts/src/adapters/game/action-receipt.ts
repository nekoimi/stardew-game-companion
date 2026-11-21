export const gameProtocolVersion = "2";

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
    payload: { commandId?: string; terminalState?: string };
  },
  binding: GameBinding,
  expectedActionId: string | undefined,
  settledActions: ReadonlySet<string>,
): "version-mismatch" | "stale-game" | "duplicate" | "pending" | "unrelated" | "terminal" {
  if (message.protocolVersion !== gameProtocolVersion) return "version-mismatch";
  if (message.saveId !== binding.saveId || message.gameSessionId !== binding.gameSessionId) return "stale-game";
  const id = message.payload.commandId;
  if (id && settledActions.has(id)) return "duplicate";
  if (!id || id !== expectedActionId) return "unrelated";
  if (message.messageType === "skill.result.status") return "pending";
  if (message.messageType !== "skill.result") return "unrelated";
  return message.payload.terminalState ? "terminal" : "pending";
}
