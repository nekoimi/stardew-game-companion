import assert from "node:assert/strict";
import { test } from "node:test";
import { setTimeout } from "node:timers/promises";
import { PiModelAdapter } from "../dist/adapters/model/pi-model.js";
import { checkModelConnection } from "../dist/adapters/model/connection-check.js";
import { listModelProviders, resolveSelection } from "../dist/adapters/model/model-catalogue.js";
import { createMockDeepSeek, redirectDeepSeek } from "./mock-deepseek.mjs";
import { createMockOpenAI, redirectOpenAI } from "./mock-openai.mjs";

test("catalogue defaults resolve and legacy custom settings preserve the endpoint", () => {
  const catalogue = listModelProviders();
  assert.deepEqual(catalogue.map(item => item.id), ["deepseek", "openai", "custom"]);
  for (const preset of catalogue.slice(0, 2)) {
    assert.ok(preset.models.some(model => model.id === preset.defaultModel));
    assert.equal(resolveSelection({ provider: preset.id, model: preset.defaultModel }).protocol, preset.protocol);
  }
  const old = { provider: "openai-compatible", model: "gateway-model", baseUrl: " https://gateway.invalid/tenant/v1/ " };
  const upgraded = { ...old, provider: "custom", protocol: "responses", stream: true };
  assert.deepEqual(resolveSelection(old), resolveSelection(upgraded));
  assert.equal(resolveSelection(old).baseUrl, "https://gateway.invalid/tenant/v1");
  assert.throws(() => resolveSelection({ ...upgraded, protocol: "unknown" }), { code: "unsupported" });
  assert.throws(() => resolveSelection({ ...upgraded, stream: "false" }), { code: "unsupported" });
  assert.throws(() => resolveSelection({ provider: "openai", model: "gpt-5-mini", protocol: "chat-completions" }), { code: "unsupported" });
});

test("DeepSeek adapter streams tool arguments, classifies failures, and preserves unknown usage", async () => {
  const server = await createMockDeepSeek();
  const restore = redirectDeepSeek(server.address);
  try {
    const adapter = new PiModelAdapter();
    const selection = { provider: "deepseek", model: "deepseek-flash" };
    const options = { instructions: "Check the tools.",
      messages: [{ role: "user", text: "Test" }],
      tools: [{ name: "check_connection", description: "Check tools.",
        parameters: { type: "object", properties: {}, additionalProperties: false } }],
      signal: new AbortController().signal };
    const invoke = async key => (await Array.fromAsync(adapter.open(selection, key).generate(options))).at(-1);
    const result = await invoke("accept-usage");
    assert.equal(result.type, "done");
    assert.equal(result.calls[0].name, "check_connection");
    assert.deepEqual(result.calls[0].arguments, {});
    assert.equal(result.usage.input, 12);
    assert.equal((await invoke("accept-no-usage")).usage, undefined);
    for (const [key, code] of [["accept-401", "authentication"], ["accept-429", "rate-limited"], ["accept-404", "model-unavailable"], ["accept-drop", "network"]]) {
      const result = await invoke(key);
      assert.equal(result.type, "error");
      assert.equal(result.code, code);
      assert.doesNotMatch(result.message, /private|accept-/);
    }
    assert.throws(() => adapter.open({ ...selection, model: " " }, "sample"), { code: "model-unavailable" });
    await checkModelConnection(adapter.open({ ...selection, model: "new-model-fixture" }, "accept-usage"), AbortSignal.timeout(5000));
    assert.equal(server.calls.at(-1).model, "new-model-fixture");
    await checkModelConnection(adapter.open(selection, "accept-usage"), AbortSignal.timeout(5000));
    await checkModelConnection(adapter.open({ ...selection, stream: false }, "accept-usage"), AbortSignal.timeout(5000), false);
    assert.ok(server.calls.slice(-2).every(call => call.stream === false));
  } finally { restore(); await server.close(); }
});

test("OpenAI Responses replays only its own tool call and normalizes reported usage", async () => {
  const server = await createMockOpenAI(({ input }) => input.some(item => item.type === "function_call_output")
    ? {} : { name: "get_status", args: { detail: "farm" } });
  const restore = redirectOpenAI(server.address);
  try {
    const model = new PiModelAdapter().open({ provider: "openai", model: "gpt-5-mini" }, "test-only");
    const messages = [{ role: "user", text: "查看农场" }];
    const generate = async () => (await Array.fromAsync(model.generate({ instructions: "Test live tool shape.",
      messages, tools: [{ name: "get_status", description: "Observe the farm.",
        parameters: { type: "object", properties: { detail: { type: "string" } }, additionalProperties: false } }],
      signal: new AbortController().signal }))).at(-1);
    const first = await generate();
    assert.equal(first.reason, "tool-calls");
    assert.equal(first.calls[0].name, "get_status");
    assert.deepEqual(first.calls[0].arguments, { detail: "farm" });
    assert.equal(first.usage.cacheRead, 2);
    const call = first.calls[0];
    messages.push({ role: "assistant", text: first.text, calls: first.calls },
      { role: "tool", callId: call.id, name: call.name, result: { location: "Farm" }, failed: false });
    const second = await generate();
    assert.match(second.text, /OpenAI/);
    const replay = server.requests[1].input;
    assert.equal(replay.find(item => item.type === "function_call_output").call_id,
      replay.find(item => item.type === "function_call").call_id);
    await checkModelConnection(model, AbortSignal.timeout(5000));
    const complete = new PiModelAdapter().open({ provider: "openai", model: "gpt-5-mini", stream: false }, "test-only");
    await checkModelConnection(complete, AbortSignal.timeout(5000), false);
    assert.ok(server.requests.slice(-2).every(request => request.stream === false));
  } finally { restore(); await server.close(); }
});

test("OpenAI Responses reports cancellation while the response is still streaming", async () => {
  const { promise: closed, resolve: onResponseClose } = Promise.withResolvers();
  const server = await createMockOpenAI(() => ({ onResponseClose }));
  const restore = redirectOpenAI(server.address);
  try {
    const model = new PiModelAdapter().open({ provider: "openai", model: "gpt-5-mini" }, "test-only");
    const controller = new AbortController();
    const events = [];
    for await (const event of model.generate({ instructions: "Reply as text.",
      messages: [{ role: "user", text: "等待接管" }], tools: [],
      signal: AbortSignal.any([controller.signal, AbortSignal.timeout(5000)]) })) {
      events.push(event);
      if (event.type === "text") controller.abort();
    }
    assert.deepEqual(events.map(event => event.type), ["text", "error"]);
    assert.equal(events[1].code, "cancelled");
    assert.equal(server.requests.length, 1);
    assert.equal(await Promise.race([closed.then(() => true), setTimeout(5000, false, { ref: false })]), true,
      "cancelled HTTP response remained open");
  } finally { restore(); await server.close(); }
});

test("OpenAI-compatible Responses uses the configured model ID and keeps the endpoint prefix", async () => {
  const server = await createMockOpenAI();
  const original = globalThis.fetch;
  const redirected = [];
  globalThis.fetch = (input, options) => {
    const request = new Request(input, options);
    const url = new URL(request.url);
    assert.equal(url.hostname, "gateway.invalid");
    assert.equal(request.headers.get("Authorization"), "Bearer gateway-fixture");
    redirected.push(url.pathname);
    return original(new Request(server.address + url.pathname, request));
  };
  try {
    const adapter = new PiModelAdapter();
    const selection = { provider: "openai-compatible", model: "custom-model", baseUrl: "https://gateway.invalid/tenant/v1/" };
    await checkModelConnection(adapter.open(selection, "gateway-fixture"), AbortSignal.timeout(5000));
    assert.deepEqual(redirected, ["/tenant/v1/responses", "/tenant/v1/responses"]);
    assert.ok(server.requests.every(request => request.model === selection.model));
    assert.throws(() => adapter.open({ ...selection, provider: "deepseek" }, "fixture"), { code: "unsupported" });
    assert.throws(() => adapter.open({ ...selection, baseUrl: "http://gateway.invalid/v1" }, "fixture"), { code: "unsupported" });
  } finally { globalThis.fetch = original; await server.close(); }
});

test("custom protocols complete the tool round trip with and without streaming", async () => {
  const chat = await createMockDeepSeek();
  const responses = await createMockOpenAI();
  const original = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (input, options) => {
    const request = new Request(input, options);
    const url = new URL(request.url);
    assert.equal(url.hostname, "gateway.invalid");
    assert.equal(request.headers.get("Authorization"), "Bearer gateway-fixture");
    const body = await request.clone().json();
    requests.push({ path: url.pathname, body });
    const server = url.pathname.endsWith("/responses") ? responses : chat;
    return original(new Request(server.address + url.pathname, request));
  };
  try {
    const adapter = new PiModelAdapter();
    for (const [protocol, route] of [["chat-completions", "chat/completions"], ["responses", "responses"]]) {
      for (const stream of [true, false]) {
        const selection = { provider: "custom", model: "fixture-model", protocol, stream,
          baseUrl: "https://gateway.invalid/tenant/v1/" };
        requests.length = 0;
        await checkModelConnection(adapter.open(selection, "gateway-fixture"), AbortSignal.timeout(5000), stream);
        assert.deepEqual(requests.map(({ path, body }) => [path, body.model, body.stream]), [
          ["/tenant/v1/" + route, "fixture-model", stream], ["/tenant/v1/" + route, "fixture-model", stream],
        ]);
        const replay = requests[1].body;
        if (protocol === "responses") {
          const call = replay.input.find(item => item.type === "function_call");
          assert.equal(replay.input.find(item => item.type === "function_call_output").call_id, call.call_id);
        } else {
          const call = replay.messages.find(item => item.tool_calls).tool_calls[0];
          assert.equal(replay.messages.find(item => item.role === "tool").tool_call_id, call.id);
          if (!stream) assert.equal(replay.stream_options, undefined);
        }
      }
    }
  } finally { globalThis.fetch = original; await chat.close(); await responses.close(); }
});

test("non-streaming cancellation closes an incomplete HTTP body without emitting text or calls", async () => {
  const { promise: closed, resolve: onResponseClose } = Promise.withResolvers();
  const arriving = Promise.withResolvers();
  const server = await createMockOpenAI(() => { arriving.resolve(); return { onResponseClose }; });
  const restore = redirectOpenAI(server.address);
  try {
    const model = new PiModelAdapter().open({ provider: "openai", model: "gpt-5-mini", stream: false }, "test-only");
    const controller = new AbortController();
    const events = Array.fromAsync(model.generate({ instructions: "Reply as text.", messages: [{ role: "user", text: "Test" }],
      tools: [], signal: AbortSignal.any([controller.signal, AbortSignal.timeout(5000)]) }));
    await arriving.promise;
    controller.abort();
    assert.deepEqual((await events).map(event => [event.type, event.code]), [["error", "cancelled"]]);
    assert.equal(await Promise.race([closed.then(() => true), setTimeout(5000, false, { ref: false })]), true);
    assert.equal(server.requests.length, 1);
  } finally { restore(); await server.close(); }
});

test("model calls fail without falling back on stream rejection or malformed complete arguments", async () => {
  const server = await createMockDeepSeek();
  const restore = redirectDeepSeek(server.address);
  const selection = { provider: "deepseek", model: "deepseek-flash" };
  try {
    const adapter = new PiModelAdapter();
    const request = { instructions: "Run the tool.", messages: [{ role: "user", text: "Test" }],
      tools: [{ name: "get_status", description: "Observe", parameters: { type: "object", properties: {} } }],
      signal: AbortSignal.timeout(5000) };
    const rejected = await Array.fromAsync(adapter.open(selection, "reject-stream").generate(request));
    assert.deepEqual(rejected.map(event => [event.type, event.code]), [["error", "unsupported"]]);
    const malformed = await Array.fromAsync(adapter.open({ ...selection, stream: false }, "invalid-arguments").generate(request));
    assert.deepEqual(malformed.map(event => [event.type, event.code]), [["error", "invalid-tool-response"]]);
    const interrupted = await Array.fromAsync(adapter.open(selection, "interrupted-tool").generate(request));
    assert.equal(interrupted.at(-1).code, "network");
    assert.ok(!interrupted.some(event => event.type === "done"));
    assert.deepEqual(server.calls.map(call => call.stream), [true, false, true]);
  } finally { restore(); await server.close(); }
});

test("model connection check rejects a tool call with no confirmed tool result", async () => {
  const model = { async *generate({ messages }) {
    if (messages.length === 1) {
      yield { type: "done", reason: "tool-calls", text: "",
        calls: [{ id: "tool", name: "check_connection", arguments: {} }] };
      return;
    }
    yield { type: "done", reason: "stop", text: "连接成功。", calls: [] };
  } };
  await assert.rejects(checkModelConnection(model, AbortSignal.timeout(5000)), { code: "unsupported" });
});
