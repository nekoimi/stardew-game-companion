import assert from "node:assert/strict";
import { test } from "node:test";
import { PiModelAdapter } from "../dist/adapters/model/pi-model.js";
import { createMockDeepSeek, redirectDeepSeek } from "./mock-deepseek.mjs";

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
    for (const [key, code] of [["accept-401", "authentication"], ["accept-429", "rate-limited"], ["accept-drop", "network"]]) {
      const result = await invoke(key);
      assert.equal(result.type, "error");
      assert.equal(result.code, code);
      assert.doesNotMatch(result.message, /private|accept-/);
    }
    assert.throws(() => adapter.open({ ...selection, model: "missing" }, "sample"), { code: "model-unavailable" });
  } finally { restore(); await server.close(); }
});
