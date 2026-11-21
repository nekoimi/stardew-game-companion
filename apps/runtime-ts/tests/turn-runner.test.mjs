import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { setTimeout } from "node:timers/promises";
import { test } from "node:test";
import { TurnRunner } from "../dist/application/turn-runner.js";

const binding = { saveId: "farm", gameSessionId: "game" };
const action = { id: "model-action", name: "submit_work", arguments: {
  action: { navigate_to: { location_id: "Farm", tile: { x: 12, y: 8 } } },
} };
const done = (calls = [], text = "已完成。") => ({ type: "done", reason: calls.length ? "tool-calls" : "stop", calls, text });

class FakeGame extends EventEmitter {
  binding = binding;
  state = { controlOwner: "agent" };
  actorMode = "player";
  snapshot = { companion: { locationId: "Farm" } };
  executed = [];
  controls = [];
  queryCount = 0;
  notify() {}
  async control(action) { this.controls.push(action); return {}; }
  async execute(id) { this.executed.push(id); return this.nativeResult?.(); }
  async queryAction() { this.queryCount++; return this.recovered; }
}

function fixture(game, model) {
  const events = [];
  const runner = new TurnRunner(game, model, (type, payload) => events.push({ type, payload }), "Game rules");
  return { runner, events };
}

async function waitFor(check) {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (check()) return;
    await setTimeout(5);
  }
  assert.fail("Expected game or model event");
}

test("takeover makes a delayed model call ineligible to act", async () => {
  const game = new FakeGame();
  let finish;
  const { runner, events } = fixture(game, {
    async *generate() { await new Promise(resolve => { finish = resolve; }); yield done([action]); },
  });
  game.emit("changed");
  await runner.submit(binding, "first", "走一格", "send");
  await waitFor(() => finish);
  game.state.controlOwner = "human";
  game.emit("changed");
  finish();
  await waitFor(() => events.some(e => e.type === "life.chat.reply"));
  assert.deepEqual(game.executed, []);
  assert.equal(runner.status().controlOwner, "human");
});

test("replacement waits for native settlement and keeps the completed receipt", async () => {
  const game = new FakeGame();
  let finish;
  game.nativeResult = () => new Promise(resolve => { finish = resolve; });
  const { runner, events } = fixture(game, {
    async *generate({ messages }) { yield messages.at(-1).text === "走一格" ? done([action]) : done(); },
  });
  await runner.submit(binding, "first", "走一格", "send");
  await waitFor(() => finish);
  const replacement = runner.submit(binding, "second", "改成等待", "interrupt");
  await waitFor(() => game.controls.includes("interrupt"));
  assert.ok(!game.controls.includes("unhold"));
  finish({ commandId: game.executed[0], terminalState: "cancelled", completedCount: 1 });
  await replacement;
  await waitFor(() => events.some(e => e.type === "life.chat.reply" && e.payload.requestId === "second"));
  assert.deepEqual(game.controls, ["record", "interrupt", "unhold", "record"]);
  assert.equal(events.find(e => e.type === "result").payload.completedCount, 1);
});

test("an unknown terminal action blocks replacement until a real receipt is found", async () => {
  const game = new FakeGame();
  game.nativeResult = () => { throw new Error("missed receipt"); };
  const { runner, events } = fixture(game, { async *generate() { yield done([action]); } });
  await runner.submit(binding, "first", "走一格", "send");
  await waitFor(() => runner.status().recoveryPending);
  assert.equal(game.queryCount, 1);
  assert.ok(game.controls.includes("status"));
  await assert.rejects(runner.submit(binding, "second", "继续", "send"), /核对/);
  await assert.rejects(runner.control(binding, "recover"), /尚未确认/);
  game.recovered = { commandId: game.executed[0], terminalState: "succeeded" };
  await runner.control(binding, "recover");
  assert.equal(runner.status().recoveryPending, false);
  assert.equal(events.filter(e => e.type === "result").length, 1);
  assert.equal(game.executed.length, 1);
});
