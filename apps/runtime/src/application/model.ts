export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };
export function object(value: JsonValue | undefined): JsonObject {
  return value && typeof value === "object" && !Array.isArray(value) ? value : {};
}

export interface ModelSelection {
  provider: string;
  model: string;
  reasoning?: string;
  baseUrl?: string;
}

export interface ToolDefinition {
  name: string;
  description: string;
  parameters: JsonObject;
}

export interface ToolCall {
  id: string;
  name: string;
  arguments: JsonObject;
}

export type ModelMessage =
  | { role: "user"; text: string }
  | { role: "assistant"; text: string; calls: ToolCall[] }
  | { role: "tool"; callId: string; name: string; result: JsonValue; failed: boolean };

export type ModelUsage = Partial<Record<"input" | "output" | "cacheRead" | "cacheWrite", number>>;
export type ModelErrorCode = "authentication" | "model-unavailable" | "rate-limited" | "network"
  | "invalid-tool-response" | "cancelled" | "unsupported" | "provider";

export type ModelEvent =
  | { type: "text"; delta: string }
  | { type: "activity" }
  | { type: "done"; reason: "stop" | "tool-calls" | "length"; text: string; calls: ToolCall[]; usage?: ModelUsage }
  | { type: "error"; code: ModelErrorCode; message: string };

/** One provider-bound context. A new context is assembled from project data after a model change. */
export interface ModelContext {
  generate(request: {
    instructions: string;
    messages: readonly ModelMessage[];
    tools: readonly ToolDefinition[];
    signal: AbortSignal;
  }): AsyncIterable<ModelEvent>;
}

export interface ModelAdapter {
  open(selection: ModelSelection, apiKey: string): ModelContext;
}
