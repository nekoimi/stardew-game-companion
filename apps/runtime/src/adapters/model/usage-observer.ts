import { object } from "../../application/model.js";
import type { JsonObject, JsonValue } from "../../application/model.js";

/** Inspect usage fields without buffering/replaying a whole response or logging it. */
export function observeUsageFetch(transport: typeof fetch, found: (usage: JsonObject) => void): typeof fetch {
  return async (input, init) => {
    const response = await transport(input, init);
    if (!response.ok || !response.body) return response;
    let pending = "";
    const decoder = new TextDecoder();
    const stream = response.body.pipeThrough(new TransformStream<Uint8Array, Uint8Array>({
      transform(chunk, controller) {
        controller.enqueue(chunk);
        pending += decoder.decode(chunk, { stream: true });
        let end;
        while ((end = pending.indexOf("\n")) >= 0) {
          const line = pending.slice(0, end).trim(); pending = pending.slice(end + 1);
          if (!line.startsWith("data:") || line === "data: [DONE]") continue;
          try {
            const event = JSON.parse(line.slice(5)) as JsonValue;
            const payload = object(event), choice = object(Array.isArray(payload.choices) ? payload.choices[0] : undefined);
            const usage = object(payload.usage ?? object(payload.response).usage ?? choice.usage);
            if (Object.keys(usage).length) found(usage);
          } catch { /* The SDK owns protocol error classification. */ }
        }
        if (pending.length > 1024 * 1024) pending = "";
      },
    }));
    return new Response(stream, { status: response.status, statusText: response.statusText, headers: response.headers });
  };
}
