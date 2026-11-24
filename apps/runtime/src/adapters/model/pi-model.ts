import { createModels, createProvider, InMemoryCredentialStore } from "@earendil-works/pi-ai";
import type { AssistantMessage, Message, Model } from "@earendil-works/pi-ai";
import { openAICompletionsApi } from "@earendil-works/pi-ai/api/openai-completions.lazy";
import { openAIResponsesApi } from "@earendil-works/pi-ai/api/openai-responses.lazy";
import type {
  JsonObject, ModelAdapter, ModelContext, ModelEvent, ModelMessage, ModelSelection,
} from "../../application/model.js";
import { completeResponseFetch, invalidCompleteResponse } from "./complete-response.js";
import { ModelError, presets, resolveSelection, thinkingLevels } from "./model-catalogue.js";

function classifyFailure(text: string | undefined, cancelled: boolean): ModelError {
  if (cancelled) return new ModelError("cancelled", "模型请求已取消。");
  // Provider errors may include request data. Only the classification leaves this adapter.
  const detail = text ?? "";
  if (detail.includes(invalidCompleteResponse)) {
    return new ModelError("invalid-tool-response", "模型未返回有效的非流式结果，请检查协议和流式设置。");
  }
  if (/\b(401|403)\b|unauthorized|api.key|authentication|permission.denied/i.test(detail)) {
    return new ModelError("authentication", "模型鉴权失败，请核对 API Key 和模型权限。");
  }
  if (/\b402\b|insufficient[_ -](?:balance|quota|credits)|billing[_ -]hard[_ -]limit|credit[_ -]balance[_ -]too[_ -]low/i.test(detail)) {
    return new ModelError("quota-exhausted", "模型服务余额或额度不足，请检查服务商账户。");
  }
  if (/\b429\b|rate.limit|too many requests|quota/i.test(detail)) return new ModelError("rate-limited", "模型服务请求频率受限。");
  if (/\b404\b|model.not.found|unknown.model/i.test(detail)) return new ModelError("model-unavailable", "模型不可用，请核对模型名称。");
  if (/stream.*(?:unsupported|not.support|invalid)|(?:unsupported|not.support).*stream/i.test(detail)) {
    return new ModelError("unsupported", "接口拒绝当前流式设置，请调整流式开关后重试。");
  }
  if (/unfinished tool call/i.test(detail)) return new ModelError("invalid-tool-response", "模型工具调用未完整返回。");
  if (/stream ended|without finish_reason/i.test(detail)) return new ModelError("network", "模型响应在完成前中断，请检查接口与流式设置。");
  if (/\b(500|502|503|504)\b|network|fetch|timeout|econn|enotfound|premature|connection/i.test(detail)) {
    return new ModelError("network", "模型连接中断或超时。");
  }
  return new ModelError("provider", "模型服务未完成请求。");
}

export class PiModelAdapter implements ModelAdapter {
  open(selection: ModelSelection, apiKey: string): ModelContext {
    const resolved = resolveSelection(selection);
    const key = apiKey.trim();
    if (!key) throw new ModelError("authentication", "请填写 API Key。");
    const chat = resolved.protocol === "chat-completions";
    const preset = resolved.preset ?? (chat ? presets.deepseek : presets.openai);
    const official = preset.factory();
    const api = chat ? "openai-completions" : "openai-responses";
    const catalogue = official.getModels();
    const template = catalogue.find(entry => entry.id === preset.models[0] && entry.api === api);
    if (!template) throw new ModelError("unsupported", "模型目录缺少协议配置。");
    const known = resolved.preset ? catalogue.find(entry => entry.id === resolved.model && entry.api === api) : undefined;
    const baseUrl = resolved.baseUrl ?? official.baseUrl ?? template.baseUrl;
    const model: Model<typeof api> = known ? { ...known, baseUrl } : {
      ...template, api, provider: resolved.baseUrl ? "custom" : official.id, baseUrl,
      id: resolved.model, name: resolved.model, reasoning: false,
      compat: resolved.baseUrl ? (chat ? { supportsStore: false, supportsUsageInStreaming: false, maxTokensField: "max_tokens" } : {}) : template.compat,
    };
    const provider = resolved.baseUrl ? createProvider({
      id: model.provider, baseUrl, auth: { apiKey: official.auth.apiKey },
      models: [model], api: chat ? openAICompletionsApi() : openAIResponsesApi(),
    }) : official;
    const credentials = new InMemoryCredentialStore();
    const models = createModels({ credentials });
    models.setProvider(provider);
    const reasoning = thinkingLevels.find(level => level === selection.reasoning);
    if (selection.reasoning && (!reasoning || !model.reasoning || model.thinkingLevelMap?.[reasoning] === null)) {
      throw new ModelError("unsupported", "该模型不支持所选推理档位。");
    }
    const providerHistory = new WeakMap<ModelMessage, AssistantMessage>();
    const fetchComplete = resolved.stream ? undefined : completeResponseFetch(resolved.protocol);
    let lastResponse: AssistantMessage | undefined;
    return {
      async *generate({ instructions, messages, tools, signal }): AsyncIterable<ModelEvent> {
        try {
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
            { apiKey: key, signal, reasoning, maxTokens: 4096, maxRetries: 0,
              fetch: fetchComplete });
          for await (const event of stream) {
            if (resolved.stream && event.type === "text_delta") yield { type: "text", delta: event.delta };
            if (resolved.stream && (event.type === "thinking_delta" || event.type === "toolcall_delta")) yield { type: "activity" };
            if (event.type === "error") throw classifyFailure(event.error.errorMessage, signal.aborted || event.reason === "aborted");
            if (event.type !== "done") continue;
            const { message } = event;
            const calls = message.content.filter(part => part.type === "toolCall");
            if (calls.some(call => call.argumentsError || !call.id || !call.name
                || !call.arguments || typeof call.arguments !== "object" || Array.isArray(call.arguments))
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
