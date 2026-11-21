import { object } from "../../application/model.js";
import type { JsonObject, JsonValue } from "../../application/model.js";

export const invalidCompleteResponse = "Invalid non-streaming model response";

function invalid(): never {
  throw new Error(invalidCompleteResponse);
}

function chatEvents(response: JsonObject): JsonObject[] {
  const choice = object(Array.isArray(response.choices) ? response.choices[0] : undefined);
  const message = object(choice.message);
  if (!Object.keys(message).length || typeof choice.finish_reason !== "string"
      || (message.content != null && typeof message.content !== "string")
      || (message.tool_calls != null && !Array.isArray(message.tool_calls))) invalid();
  const toolCalls = Array.isArray(message.tool_calls) ? message.tool_calls.map((value, index) => {
    const call = object(value);
    const fn = object(call.function);
    if (call.type !== "function" || typeof call.id !== "string" || !call.id
        || typeof fn.name !== "string" || !fn.name || typeof fn.arguments !== "string") invalid();
    return { ...call, index };
  }) : undefined;
  const delta = { ...message };
  if (toolCalls) delta.tool_calls = toolCalls;
  return [{ ...response, object: "chat.completion.chunk",
    choices: [{ index: 0, finish_reason: choice.finish_reason, usage: choice.usage ?? null, delta }] }];
}

function responsesEvents(response: JsonObject): JsonObject[] {
  if (response.status === "failed") return [{ type: "response.failed", response }];
  if (!Array.isArray(response.output) || !["completed", "incomplete"].includes(String(response.status))) invalid();
  const events = response.output.flatMap((value, outputIndex): JsonObject[] => {
    const item = object(value);
    if (item.type !== "message" && item.type !== "reasoning" && item.type !== "function_call") return [];
    if (item.type === "function_call" && (typeof item.call_id !== "string" || !item.call_id
        || typeof item.id !== "string" || !item.id || typeof item.name !== "string" || !item.name
        || typeof item.arguments !== "string" || (item.status != null && item.status !== "completed"))) invalid();
    return [
      { type: "response.output_item.added", output_index: outputIndex, item },
      { type: "response.output_item.done", output_index: outputIndex, item },
    ];
  });
  events.push({ type: response.status === "incomplete" ? "response.incomplete" : "response.completed", response });
  return events;
}

/** Use the SDK's request and replay format for both modes. Only the HTTP
 * transport changes here. The SDK receives terminal events after the full body. */
export function completeResponseFetch(protocol: "chat-completions" | "responses"): typeof fetch {
  return async (input, init) => {
    const request = new Request(input, init);
    const body = await request.json() as JsonObject;
    body.stream = false;
    delete body.stream_options;
    const headers = new Headers(request.headers);
    headers.set("accept", "application/json");
    headers.delete("content-length");
    const response = await fetch(new Request(request, { headers, body: JSON.stringify(body) }));
    if (!response.ok) return response;
    const result = await response.json().catch(() => invalid()) as JsonValue;
    request.signal.throwIfAborted();
    const events = protocol === "responses" ? responsesEvents(object(result)) : chatEvents(object(result));
    const content = events.map(event => `data: ${JSON.stringify(event)}\n\n`).join("") + "data: [DONE]\n\n";
    return new Response(content, { headers: { "content-type": "text/event-stream" } });
  };
}
