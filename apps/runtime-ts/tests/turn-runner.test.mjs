import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { setTimeout } from "node:timers/promises";
import { test } from "node:test";
import { TurnRunner } from "../dist/application/turn-runner.js";
import { CompanionStore } from "../dist/adapters/storage/companion-store.js";

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
  async observe() { return this.snapshot; }
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

test("a reply still streaming when another save is loaded can neither act nor enter history", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  let finish;
  const events = [];
  const runner = new TurnRunner(game, { async *generate() {
    await new Promise(resolve => { finish = resolve; });
    yield done([action], "旧存档回复");
  } }, (type, payload) => events.push({ type, payload }), "", store);
  try {
    game.emit("changed");
    await runner.submit(binding, "first", "走一格", "send");
    await waitFor(() => finish);
    game.binding = { saveId: "other", gameSessionId: "new-game" };
    game.emit("changed");
    finish();
    await runner.stop();
    assert.equal(game.executed.length, 0);
    assert.ok(!events.some(e => e.type === "life.chat.reply"));
    assert.equal(store.history("other").entries.length, 0);
    assert.equal(store.history("farm").entries.length, 1);
  } finally { await runner.stop(); store.close(); }
});

test("a tool-boundary message supersedes an old proposed action and is passed to the next model step", async () => {
  const game = new FakeGame();
  let finish;
  let passed = false;
  const { runner, events } = fixture(game, {
    async *generate({ messages }) {
      if (!finish) { await new Promise(resolve => { finish = resolve; }); yield done([action]); }
      else { passed = messages.at(-1).text === "先别动"; yield done(); }
    },
  });
  await runner.submit(binding, "first", "走一格", "send");
  await waitFor(() => finish);
  await runner.submit(binding, "second", "先别动", "send");
  finish();
  await waitFor(() => events.some(e => e.type === "life.chat.reply" && e.payload.requestId === "second"));
  assert.equal(passed, true);
  assert.equal(game.executed.length, 0);
  assert.equal(events.filter(e => e.type === "life.chat.reply" && e.payload.replyText).length, 1);
});

test("switching models preserves companion identity, submitted conversation and an active goal", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  store.updateProfile(binding.saveId, { companionName: "阿栗" }, 0);
  store.editMemory(binding.saveId, "add", "", "agreement", "保留幼树", "player", 0);
  const events = [];
  const runner = new TurnRunner(game, undefined, (type, payload) => events.push({ type, payload }), "Game rules", store);
  try {
    let first = true;
    runner.configure({ async *generate() {
      if (first) {
        first = false;
        yield done([{ id: "goal", name: "manage_arrangements", arguments: { change: {
          manage_goal: { action: "add", text: "安排今日农活" },
        } } }]);
      } else yield done();
    } }, { provider: "deepseek", model: "deepseek-flash" });
    await runner.submit(binding, "first", "安排今日农活", "send");
    await waitFor(() => events.some(e => e.type === "life.chat.reply" && e.payload.requestId === "first"));
    runner.configure({ async *generate({ instructions, messages }) {
      assert.match(instructions, /阿栗/);
      assert.match(instructions, /保留幼树/);
      assert.match(instructions, /安排今日农活/);
      assert.equal(messages[0].text, "安排今日农活");
      yield done([], "新模型继续。");
    } }, { provider: "openai", model: "gpt-5-mini" });
    await runner.submit(binding, "second", "继续看看", "send");
    await waitFor(() => events.some(e => e.type === "life.chat.reply" && e.payload.requestId === "second"));
    assert.equal(events.find(e => e.type === "life.chat.reply" && e.payload.requestId === "second").payload.replyText, "新模型继续。");
  } finally { await runner.stop(); store.close(); }
});
