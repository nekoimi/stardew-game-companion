import { createModels, InMemoryCredentialStore } from "@earendil-works/pi-ai";
import type { AssistantMessage, Message, ThinkingLevel } from "@earendil-works/pi-ai";
import { deepseekProvider } from "@earendil-works/pi-ai/providers/deepseek";
import { openaiProvider } from "@earendil-works/pi-ai/providers/openai";
import type {
  JsonObject, ModelAdapter, ModelContext, ModelErrorCode, ModelEvent, ModelMessage, ModelSelection,
} from "../../application/model.js";

const providers = { deepseek: deepseekProvider, openai: openaiProvider };
const thinkingLevels: readonly ThinkingLevel[] = ["minimal", "low", "medium", "high", "xhigh", "max"];

export class ModelError extends Error {
  constructor(readonly code: ModelErrorCode, message: string) { super(message); }
}

export function listModelProviders() {
  return Object.values(providers).map(factory => {
    const provider = factory();
    return { id: provider.id, name: provider.name, models: provider.getModels().map(model => ({
      id: model.id, name: model.name, reasoning: model.reasoning
        ? thinkingLevels.filter(level => model.thinkingLevelMap?.[level] !== null) : [],
    })) };
  });
}

function classifyFailure(text: string | undefined, cancelled: boolean): ModelError {
  if (cancelled) return new ModelError("cancelled", "模型请求已取消。");
  // Provider errors may include request data. Only the classification leaves this adapter.
  const detail = text ?? "";
  if (/\b(401|403)\b|unauthorized|api.key|authentication|permission.denied/i.test(detail)) {
    return new ModelError("authentication", "模型鉴权失败，请核对 API Key 和模型权限。");
  }
  if (/\b429\b|rate.limit|quota/i.test(detail)) return new ModelError("rate-limited", "模型请求受限，请稍后重试或检查额度。");
  if (/\b404\b|model.not.found|unknown.model/i.test(detail)) return new ModelError("model-unavailable", "模型不可用，请核对模型名称。");
  if (/\b(500|502|503|504)\b|network|fetch|timeout|econn|enotfound|premature|connection/i.test(detail)) {
    return new ModelError("network", "模型连接中断或超时。");
  }
  return new ModelError("provider", "模型服务未完成请求。");
}

export class PiModelAdapter implements ModelAdapter {
  open(selection: ModelSelection, apiKey: string): ModelContext {
    const factory = Object.hasOwn(providers, selection.provider) ? providers[selection.provider as keyof typeof providers] : undefined;
    if (!factory || selection.baseUrl) throw new ModelError("unsupported", "该模型服务商或端点暂不支持。");
    if (!apiKey.trim()) throw new ModelError("authentication", "请填写 API Key。");
    const provider = factory();
    const credentials = new InMemoryCredentialStore();
    const models = createModels({ credentials });
    models.setProvider(provider);
    const model = models.getModel(provider.id, selection.model);
    if (!model) throw new ModelError("model-unavailable", "模型不在当前服务商目录中。");
    const reasoning = thinkingLevels.find(level => level === selection.reasoning);
    if (selection.reasoning && (!reasoning || !model.reasoning || model.thinkingLevelMap?.[reasoning] === null)) {
      throw new ModelError("unsupported", "该模型不支持所选推理档位。");
    }
    const providerHistory = new WeakMap<ModelMessage, AssistantMessage>();
    let lastResponse: AssistantMessage | undefined;
    return {
      async *generate({ instructions, messages, tools, signal }): AsyncIterable<ModelEvent> {
        try {
          await credentials.modify(provider.id, async () => ({ type: "api_key", key: apiKey.trim() }));
          const transcript: Message[] = messages.map(message => {
            const timestamp = Date.now();
            if (message.role === "user") return { role: "user", content: message.text, timestamp };
            if (message.role === "tool") return { role: "toolResult", toolCallId: message.callId, toolName: message.name,
              content: [{ type: "text", text: JSON.stringify(message.result) }], isError: message.failed, timestamp };
            let raw = providerHistory.get(message);
            if (!raw && message.calls.length && lastResponse) {
              raw = lastResponse;
              providerHistory.set(message, raw);
              lastResponse = undefined;
            }
            return raw ?? {
              role: "assistant", content: [{ type: "text", text: message.text }], timestamp,
              api: model.api, provider: model.provider, model: model.id, stopReason: "stop",
              usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
                cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
            };
          });
          const stream = models.streamSimple(model, { systemPrompt: instructions, messages: transcript, tools: [...tools] },
            { signal, reasoning, maxTokens: 4096 });
          for await (const event of stream) {
            if (event.type === "text_delta") yield { type: "text", delta: event.delta };
            if (event.type === "thinking_delta" || event.type === "toolcall_delta") yield { type: "activity" };
            if (event.type === "error") throw classifyFailure(event.error.errorMessage, signal.aborted || event.reason === "aborted");
            if (event.type !== "done") continue;
            const { message } = event;
            const calls = message.content.filter(part => part.type === "toolCall");
            if (calls.some(call => call.argumentsError || !call.id || !call.name)
              || new Set(calls.map(call => call.id)).size !== calls.length) {
              throw new ModelError("invalid-tool-response", "模型返回了无法解析的工具参数。");
            }
            if (event.reason === "deferred") throw new ModelError("unsupported", "模型返回了延后执行结果。");
            lastResponse = calls.length ? message : undefined;
            const usage = message.usage;
            yield {
              type: "done",
              reason: event.reason === "toolUse" ? "tool-calls" : event.reason,
              text: message.content.filter(part => part.type === "text").map(part => part.text).join(""),
              calls: calls.map(call => ({ id: call.id, name: call.name, arguments: call.arguments as JsonObject })),
              // Pi initializes absent counters to zero. Report only an observed non-empty receipt.
              usage: usage.totalTokens > 0 ? {
                input: usage.input, output: usage.output, cacheRead: usage.cacheRead, cacheWrite: usage.cacheWrite,
              } : undefined,
            };
            return;
          }
          throw new ModelError("network", "模型响应在完成前中断。");
        } catch (error) {
          const failure = error instanceof ModelError ? error : classifyFailure(error instanceof Error ? error.message : undefined, signal.aborted);
          yield { type: "error", code: failure.code, message: failure.message };
        }
      },
    };
  }
}
