import type { ThinkingLevel } from "@earendil-works/pi-ai";
import { deepseekProvider } from "@earendil-works/pi-ai/providers/deepseek";
import { openaiProvider } from "@earendil-works/pi-ai/providers/openai";
import type { ModelErrorCode, ModelSelection } from "../../application/model.js";

export const presets = {
  deepseek: { factory: deepseekProvider, protocol: "chat-completions", models: ["deepseek-flash", "deepseek-v4-pro"] },
  openai: { factory: openaiProvider, protocol: "responses", models: ["gpt-5-mini"] },
} as const;
export const thinkingLevels: readonly ThinkingLevel[] = ["minimal", "low", "medium", "high", "xhigh", "max"];

export class ModelError extends Error {
  constructor(readonly code: ModelErrorCode, message: string) { super(message); }
}

export function listModelProviders() {
  const official = (Object.keys(presets) as (keyof typeof presets)[]).map(id => {
    const preset = presets[id];
    const provider = preset.factory();
    const models = provider.getModels();
    return { id, name: provider.name, protocol: preset.protocol, defaultModel: preset.models[0], stream: true,
      models: preset.models.flatMap(modelId => {
        const model = models.find(entry => entry.id === modelId);
        if (!model) throw new Error(`Model catalogue is missing ${modelId}`);
        return [{ id: model.id, name: model.name, reasoning: model.reasoning
          ? thinkingLevels.filter(level => model.thinkingLevelMap?.[level] !== null) : [] }];
      }) };
  });
  return [...official, { id: "custom", name: "自定义接口", protocol: "responses", defaultModel: "", stream: true, models: [] }];
}

export function resolveSelection(selection: ModelSelection) {
  const legacy = selection.provider === "openai-compatible";
  const custom = legacy || selection.provider === "custom";
  const preset = !custom && Object.hasOwn(presets, selection.provider) ? presets[selection.provider as keyof typeof presets] : undefined;
  const protocol = preset?.protocol ?? (legacy ? "responses" : selection.protocol);
  if ((!custom && !preset) || (protocol !== "responses" && protocol !== "chat-completions")
      || (selection.protocol !== undefined && selection.protocol !== protocol) || (preset && selection.baseUrl)) {
    throw new ModelError("unsupported", "请核对模型服务商、协议与地址。");
  }
  if (selection.stream !== undefined && typeof selection.stream !== "boolean") {
    throw new ModelError("unsupported", "流式输出设置无效。");
  }
  const model = selection.model.trim();
  if (!model || model.length > 200 || /[\u0000-\u001f\u007f]/.test(model)) {
    throw new ModelError("model-unavailable", "请填写有效的模型 ID。");
  }
  if (custom && selection.reasoning) throw new ModelError("unsupported", "自定义接口使用服务端默认推理设置。");
  return { preset, protocol, model, stream: selection.stream ?? true,
    baseUrl: custom ? customBaseUrl(selection.baseUrl) : undefined } as const;
}

function customBaseUrl(value: string | undefined): string {
  try {
    const input = (value ?? "").trim();
    const url = new URL(input);
    if (/^https:\/\//i.test(input) && !/[\\\s?#]/.test(input) && url.protocol === "https:"
        && !url.username && !url.password) {
      const base = url.href.replace(/\/+$/, "");
      if (!/\/(responses|chat\/completions)$/i.test(base)) return base;
    }
  } catch { /* Keep the address out of errors. */ }
  throw new ModelError("unsupported", "请填写 HTTPS Base URL，保留接口前缀，例如 /v1，去掉具体接口路径。");
}
