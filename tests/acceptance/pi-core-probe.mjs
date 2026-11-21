import assert from "node:assert/strict";
import { Agent } from "@earendil-works/pi-agent-core";
import { createAssistantMessageEventStream, createFauxCore, fauxAssistantMessage, fauxToolCall } from "@earendil-works/pi-ai";

const model = createFauxCore({}).getModel();
const response = (...calls) => fauxAssistantMessage(calls.map((name, i) => fauxToolCall(name, {}, { id: "call_" + i })));
const toolResult = (text, terminate) => ({ content: [{ type: "text", text }], details: {}, terminate });
const user = content => ({ role: "user", content, timestamp: Date.now() });
const tool = (name, execute) => ({ name, label: name, description: name, parameters: { type: "object", properties: {} }, execute });
function setup(steps, tools, opts = {}) {
  const requests = [];
  const agent = new Agent({ initialState: { model, tools }, toolExecution: "sequential", ...opts,
    streamFn: async (_, context, options) => {
      requests.push(context.messages.slice());
      const stream = createAssistantMessageEventStream();
      const message = await steps[requests.length - 1](options?.signal);
      stream.end(message);
      return stream;
    } });
  return { agent, requests };
}

{
  const order = [];
  const { agent, requests } = setup([() => response("move", "old_action"), () => fauxAssistantMessage("收到改派")],
    [tool("move", async () => {
      order.push("move:start");
      agent.steer(user("请暂停后续动作"));
      await new Promise(resolve => setTimeout(resolve, 15));
      order.push("move:receipt");
      return toolResult("succeeded");
    }), tool("old_action", async () => { order.push("old_action"); return toolResult("succeeded"); })],
    { beforeToolCall: async ({ toolCall }) => { order.push("preflight:" + toolCall.name); } });
  await agent.prompt("先移动");
  assert.deepEqual(order, ["preflight:move", "move:start", "move:receipt", "preflight:old_action", "old_action"]);
  assert.ok(requests[1].some(message => message.role === "user" && message.content === "请暂停后续动作"));
  console.log("PASS sequential receipt, steering arrives after remaining tools in the same model response");
}

{
  const forbidden = () => assert.fail("Native tool was called after revocation");
  const { agent, requests } = setup([() => response("move", "old_action")],
    [tool("move", async () => { agent.abort(); return toolResult("cancelled"); }), tool("old_action", forbidden)],
    { finishTurn: () => ({ action: "end" }) });
  await agent.prompt("尝试接管");
  assert.equal(requests.length, 1);
  console.log("PASS abort after native settlement, later tool suppressed");
}

{
  const forbidden = () => assert.fail("Native tool ran with unknown receipt or pending decision");
  const { agent, requests } = setup([() => response("observe", "ask", "move")],
    [tool("observe", async () => toolResult("receipt unconfirmed")),
      tool("ask", async () => toolResult("awaiting choice", true)), tool("move", forbidden)],
    { beforeToolCall: async ({ toolCall }) => toolCall.name === "move"
        ? { block: true, reason: "pending choice", terminate: true } : undefined,
      finishTurn: () => ({ action: "end" }) });
  await agent.prompt("等玩家选择");
  assert.equal(requests.length, 1);
  console.log("PASS external preflight and finish hooks can impose decision/receipt gate");
}
