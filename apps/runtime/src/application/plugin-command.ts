import type { GameBinding } from "../adapters/game/action-receipt.js";
import type { JsonObject } from "./model.js";

export interface PluginRequest {
  pluginId: string;
  instanceId: string;
  requestId: string;
  sessionId: string;
  text: string;
  correlationId?: string;
}

export type PluginStatus = "accepted" | "running" | "waiting_player" | "completed" | "partial"
  | "failed" | "cancelled" | "recovery_pending";

export interface PluginCommand {
  input: PluginRequest;
  binding: GameBinding;
  internalId: string;
  goalId: string;
  signature: string;
  status: PluginStatus;
  turnEnded: boolean;
  goalCompleted: boolean;
}

export class PluginCommandError extends Error {
  constructor(readonly code: string, message: string, readonly httpStatus = 409, readonly retryAfterMs?: number) {
    super(message);
  }
}

export function pluginAction(receipt: JsonObject): JsonObject {
  return { actionId: receipt.commandId ?? "", skillId: receipt.skillId ?? "", terminalState: receipt.terminalState ?? "",
    ...(typeof receipt.completedCount === "number" ? { completedCount: receipt.completedCount } : {}) };
}
