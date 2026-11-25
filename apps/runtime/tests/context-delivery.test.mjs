import assert from "node:assert/strict";
import { test } from "node:test";
import { ContextDelivery } from "../dist/adapters/model/context-delivery.js";
import { PiModelAdapter } from "../dist/adapters/model/pi-model.js";
import { createMockDeepSeek } from "./mock-deepseek.mjs";
import { createMockOpenAI } from "./mock-openai.mjs";
import { observeUsageFetch } from "../dist/adapters/model/usage-observer.js";

const ctx = stamina => ({ contextBinding: { saveId: "farm", gameSessionId: "game" }, stamina, profile: { name: "伙伴" } });
const source = [{ role: "user", text: "照料作物" }];
const data = message => JSON.parse(message.text.split("\n")[1]);

test("unconfirmed delivery never advances context, action failure keeps its acknowledged baseline, reload rebuilds", () => {
  const delivery = new ContextDelivery();
  const failed = delivery.prepare(source, ctx(270));
  assert.equal(data(failed.messages[0]).mode, "full");
  const retry = delivery.prepare(source, ctx(260));
  assert.equal(data(retry.messages[0]).mode, "full");
  const assistant = { role: "assistant", text: "", calls: [{ id: "water", name: "submit_work", arguments: {} }] };
  retry.confirm(assistant);
  const next = [...source, assistant, { role: "tool", callId: "water", name: "submit_work", result: { terminalState: "failed" }, failed: true }];
  const second = delivery.prepare(next, ctx(250));
  assert.deepEqual(second.messages.slice(0, retry.messages.length), retry.messages);
  assert.deepEqual(data(second.messages.at(-1)).changes, { stamina: 250 });
  second.confirm();
  const reload = delivery.prepare(source, { ...ctx(270), contextBinding: { saveId: "farm", gameSessionId: "new-load" } });
  assert.equal(data(reload.messages[0]).mode, "full");
});

test("usage observation preserves missing cache counters without delaying streamed data", async () => {
  let observed;
  const fetch = observeUsageFetch(async () => new Response('data: {"usage":{"prompt_tokens":17,"completion_tokens":3}}\n\ndata: [DONE]\n\n'), value => { observed = value; });
  const response = await fetch("https://fixture.invalid");
  assert.ok((await response.text()).includes('[DONE]'));
  assert.equal(observed.prompt_tokens, 17);
  assert.equal(observed.prompt_tokens_details, undefined);
});

test("finishing locally after tool settlement preserves receipts for the next turn without confirming an unsent delta", () => {
  const delivery = new ContextDelivery();
  const assistant = { role: "assistant", text: "", calls: [{ id: "complete", name: "manage_arrangements", arguments: {} }] };
  const first = delivery.prepare(source, ctx(270));
  first.confirm(assistant);
  const receipt = { role: "tool", callId: "complete", name: "manage_arrangements", result: { status: "completed" }, failed: false };
  delivery.settleTools([...source, assistant, receipt]);
  assert.equal(delivery.prepare([...source, assistant, receipt], ctx(260)).messages.filter(row => row.role === "tool").length, 1);
  const nextSource = [...source, { role: "assistant", text: "本阶段完成", calls: [] }, { role: "user", text: "继续" }];
  const retry = delivery.prepare(nextSource, ctx(250));
  assert.deepEqual(retry.messages.slice(0, first.messages.length), first.messages);
  assert.deepEqual(retry.messages.slice(first.messages.length, first.messages.length + 2), [assistant, receipt]);
  assert.deepEqual(delivery.prepare(nextSource, ctx(240)).messages.filter(row => row.role === "tool"), [receipt]);
  assert.equal(data(delivery.prepare(nextSource, ctx(240)).messages.findLast(row => row.role === "user" && row.text.startsWith("项目观察数据"))).changes.stamina, 240);
  retry.confirm();
  assert.equal(delivery.prepare(nextSource, ctx(230)).messages.filter(row => row.role === "tool").length, 1);
});

test("Chat and Responses preserve a stable system and acknowledged prefix across planning turns in both stream modes", async () => {
  const chat = await createMockDeepSeek(() => ({}));
  const responses = await createMockOpenAI(() => ({}));
  const original = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (input, options) => {
    const request = new Request(input, options);
    const body = await request.clone().json();
    requests.push(body);
    const url = new URL(request.url);
    return original(new Request((url.pathname.endsWith("responses") ? responses : chat).address + url.pathname, request));
  };
  try {
    for (const protocol of ["chat-completions", "responses"]) for (const stream of [true, false]) {
      requests.length = 0;
      const model = new PiModelAdapter().open({ provider: "custom", model: "fixture", protocol, stream, baseUrl: "https://gateway.invalid/v1" }, "fixture");
      const messages = source.slice();
      const generate = async context => (await Array.fromAsync(model.generate({ instructions: "Stable game rules", context,
        messages, tools: [], signal: AbortSignal.timeout(5000) }))).at(-1);
      const first = await generate(ctx(270));
      assert.equal(first.type, "done");
      messages.push({ role: "assistant", text: first.text, calls: first.calls }, { role: "user", text: "继续经营" });
      assert.equal((await generate(ctx(250))).type, "done");
      if (protocol === "responses") {
        assert.equal(requests[0].instructions, requests[1].instructions);
        assert.deepEqual(requests[1].input.slice(0, requests[0].input.length), requests[0].input);
      } else {
        assert.equal(requests[0].messages[0].content, "Stable game rules");
        assert.deepEqual(requests[1].messages.slice(0, requests[0].messages.length), requests[0].messages);
      }
      const serialized = JSON.stringify(requests[1]);
      assert.ok(serialized.includes('delta'));
      assert.equal((serialized.match(/\\"profile\\"/g) ?? []).length, 1);
    }
  } finally { globalThis.fetch = original; await chat.close(); await responses.close(); }
});
