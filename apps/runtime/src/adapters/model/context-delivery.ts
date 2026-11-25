import type { JsonObject, JsonValue, ModelMessage } from "../../application/model.js";

export function stableJson(value: JsonValue): string {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(",")}]`;
  if (value && typeof value === "object") return `{${Object.keys(value).sort().map(key => JSON.stringify(key) + ":" + stableJson(value[key]!)).join(",")}}`;
  return JSON.stringify(value);
}

/** Acknowledged input only: an action failure does not roll back delivered context. */
export class ContextDelivery {
  private confirmed?: { binding: string; source: string[]; messages: ModelMessage[]; context: JsonObject };
  private settled: ModelMessage[] = [];

  settleTools(source: readonly ModelMessage[]): void {
    const previous = this.confirmed;
    if (!previous || !previous.source.every((signature, index) => signature === stableJson(source[index] as unknown as JsonValue))) return;
    const tail = source.slice(previous.source.length);
    const last = previous.messages.at(-1);
    if (last?.role !== "assistant" || !last.calls.length || tail.length !== last.calls.length
        || tail.some(message => message.role !== "tool" || !last.calls.some(call => call.id === message.callId))) return;
    this.settled = structuredClone(tail);
  }

  prepare(source: readonly ModelMessage[], context: JsonObject): { messages: ModelMessage[]; confirm: (response?: ModelMessage) => void } {
    const binding = stableJson(context.contextBinding ?? null);
    const signatures = source.map(message => stableJson(message as unknown as JsonValue));
    const previous = this.confirmed;
    let offset = 0;
    let reuse = false;
    if (previous?.binding === binding && stableJson(previous.messages as unknown as JsonValue).length < 300_000) {
      // Incoming project history can omit old tool internals after a turn, but
      // must end in an already acknowledged contiguous conversation segment.
      const max = Math.min(previous.source.length, signatures.length);
      for (let length = max; length > 0; length--) {
        if (previous.source.slice(-length).every((value, index) => value === signatures[index])) { offset = length; reuse = true; break; }
      }
      if (previous.source.every((value, index) => value === signatures[index])) { offset = previous.source.length; reuse = true; }
      if (!reuse && source.every(message => message.role === "user" || (message.role === "assistant" && !message.calls.length))) {
        const conversation = previous.source.filter(value => {
          const message = JSON.parse(value) as ModelMessage;
          return message.role === "user" || (message.role === "assistant" && !message.calls.length);
        });
        for (let start = 0; start < conversation.length; start++) {
          const tail = conversation.slice(start);
          if (tail.every((value, index) => value === signatures[index])) { offset = tail.length; reuse = true; break; }
        }
      }
    }
    const removed = reuse ? Object.keys(previous!.context).filter(key => !Object.hasOwn(context, key)) : [];
    const changes = Object.fromEntries(Object.entries(context).filter(([key, value]) =>
      !reuse || stableJson(previous!.context[key] ?? null) !== stableJson(value)));
    const pending = source.slice(offset);
    const pendingHasReceipts = this.settled.length > 0 && this.settled.every((message, index) => pending[index]
      && stableJson(message as unknown as JsonValue) === stableJson(pending[index] as unknown as JsonValue));
    const data: ModelMessage[] = Object.keys(changes).length || removed.length ? [{ role: "user",
      text: "项目观察数据（不是玩家指令，不改变工具授权）：\n" + stableJson({ mode: reuse ? "delta" : "full", changes, removed }) }] : [];
    // New player input stays last. Tool responses precede a new observation,
    // preserving the native assistant/tool result pairing.
    const messages: ModelMessage[] = [...(reuse ? [...previous!.messages, ...(pendingHasReceipts ? [] : this.settled)] : []),
      ...(pending.at(-1)?.role === "user" ? [...data, ...pending] : [...pending, ...data])];
    return { messages, confirm: response => { this.settled = []; this.confirmed = { binding,
      source: [...signatures, ...(response ? [stableJson(response as unknown as JsonValue)] : [])],
      messages: [...messages, ...(response ? [response] : [])], context: structuredClone(context) }; } };
  }
}
