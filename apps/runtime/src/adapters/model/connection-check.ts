import { randomBytes } from "node:crypto";
import type { ModelContext, ModelEvent, ModelMessage, ToolDefinition } from "../../application/model.js";
import { modelResponseTimeout } from "../../application/model.js";
import { ModelError } from "./model-catalogue.js";

const tools: ToolDefinition[] = [{
  name: "check_connection", description: "Return a short verification code.",
  parameters: { type: "object", properties: {}, additionalProperties: false },
}];

export async function checkModelConnection(model: ModelContext, signal: AbortSignal, stream = true): Promise<void> {
  const instructions = "Call check_connection once with empty arguments. After the tool returns, reply with its verification code.";
  const messages: ModelMessage[] = [{ role: "user", text: "Please run the connection test." }];
  async function complete(): Promise<Extract<ModelEvent, { type: "done" }> | undefined> {
    const request = AbortSignal.any([signal, AbortSignal.timeout(modelResponseTimeout(stream))]);
    for await (const event of model.generate({ instructions, messages, tools, signal: request })) {
      if (request.aborted) throw new ModelError("network", "模型连接测试超时或已停止。");
      if (event.type === "error") {
        throw new ModelError(event.code, event.message);
      }
      if (event.type === "done") return event;
    }
  }

  const first = await complete();
  const call = first?.calls[0];
  if (first?.reason !== "tool-calls" || first.calls.length !== 1 || call?.name !== "check_connection"
      || Object.keys(call.arguments).length !== 0) {
    throw new ModelError("unsupported", "模型未返回有效的测试工具调用。");
  }
  const verificationCode = randomBytes(4).toString("hex");
  messages.push({ role: "assistant", text: first.text, calls: first.calls },
    { role: "tool", callId: call.id, name: call.name, result: { verificationCode }, failed: false });
  const last = await complete();
  if (last?.reason !== "stop" || last.calls.length || !last.text.includes(verificationCode)) {
    throw new ModelError("unsupported", "模型未确认测试工具的回执。");
  }
}
