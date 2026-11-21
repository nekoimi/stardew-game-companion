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
  async execute(id, turn, skill, parameters) { this.executed.push(id); return this.nativeResult?.(id, skill, parameters); }
  async queryAction() { this.queryCount++; return this.recovered; }
  async observe() { return this.snapshot; }
}

function fixture(game, model) {
  const events = [];
  const runner = new TurnRunner(game, model, (type, payload) => events.push({ type, payload }), "Game rules");
  return { runner, events };
}

async function waitFor(check, attempts = 100) {
  for (let attempt = 0; attempt < attempts; attempt++) {
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
  game.recovered = { commandId: game.executed[0], terminalState: "unknown" };
  await assert.rejects(runner.submit(binding, "second", "继续", "send"), /核对/);
  await assert.rejects(runner.control(binding, "recover"), /尚未确认/);
  assert.equal(runner.status().recoveryPending, true);
  assert.ok(!game.controls.includes("recover"));
  assert.ok(!events.some(e => e.type === "result"));
  game.recovered = { commandId: game.executed[0], terminalState: "succeeded" };
  await runner.control(binding, "recover");
  assert.equal(runner.status().recoveryPending, false);
  assert.equal(events.find(e => e.type === "recovery").payload.hasReceipt, true);
  assert.equal(events.filter(e => e.type === "result").length, 1);
  assert.equal(game.executed.length, 1);
});

test("a lost action from the previous save cannot leave the current save waiting for recovery", async () => {
  const game = new FakeGame();
  let disconnect;
  game.nativeResult = () => new Promise((_, reject) => { disconnect = reject; });
  const { runner, events } = fixture(game, { async *generate({ messages }) {
    yield messages.at(-1).text === "走一格" ? done([action]) : done();
  } });
  game.emit("changed");
  await runner.submit(binding, "first", "走一格", "send");
  await waitFor(() => disconnect);
  game.binding = { saveId: "other", gameSessionId: "new-game" };
  game.emit("changed");
  disconnect(new Error("game disconnected"));
  await waitFor(() => events.some(e => e.type === "status" && e.payload.saveId === "other" && e.payload.activeCommandId === ""));
  assert.equal(runner.status().recoveryPending, false);
  assert.equal(game.queryCount, 0);
  await runner.submit(game.binding, "second", "等我", "send");
  await waitFor(() => events.some(e => e.type === "life.chat.reply" && e.payload.requestId === "second"));
  await runner.stop();
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

test("harvest returns the native receipt and fresh collectible and inventory observations", async () => {
  const game = new FakeGame();
  const weed = { itemId: "(O)674", canBeGrabbed: false, isDropped: false };
  const drop = { itemId: "(O)24", canBeGrabbed: true, isDropped: true };
  game.snapshot = { ...game.snapshot, capturedRevision: 1,
    farming: { location: "Farm", groundItems: [weed], groundItemsTruncated: false },
    inventory: { slots: [] },
    farmWork: { locationId: "Farm", observationStatus: "observed",
      matureCrops: [{ x: 5, y: 6, cropId: "parsnip" }, { cropId: "invalid" }] } };
  const observe = { id: "status", name: "get_status", arguments: {} };
  const harvest = { id: "harvest", name: "submit_work", arguments: { action: { harvest_auto: {} } } };
  let before;
  let result;
  const { runner, events } = fixture(game, { async *generate({ messages }) {
    const last = messages.at(-1);
    if (last.role === "user") { yield done([observe]); return; }
    if (last.callId === "status") { before = last.result.world; yield done([harvest]); return; }
    result = last.result;
    yield done();
  } });
  game.nativeResult = (commandId, skill, parameters) => {
    assert.equal(skill, "harvest-zone");
    assert.deepEqual(parameters.tiles, [{ x: 5, y: 6 }]);
    game.snapshot = { ...game.snapshot, capturedRevision: 2,
      farming: { ...game.snapshot.farming, groundItems: [weed, drop] },
      inventory: { slots: [{ itemId: "(O)24", stack: 1 }] } };
    return { commandId, skillId: skill, terminalState: "succeeded", completedCount: 1, finalWorldRevision: 2 };
  };
  await runner.submit(binding, "harvest", "把成熟的收了", "send");
  await waitFor(() => events.some(event => event.type === "life.chat.reply"));
  assert.deepEqual(before.farming.groundItems, []);
  assert.deepEqual(before.farming.groundObjects, [weed]);
  assert.equal(result.terminalState, "succeeded");
  assert.equal(result.completedCount, 1);
  assert.equal(result.afterWork.capturedRevision, 2);
  assert.deepEqual(result.afterWork.ground.groundItems, [drop]);
  assert.deepEqual(result.afterWork.inventory.slots, [{ itemId: "(O)24", stack: 1 }]);
  await runner.stop();
});

test("a stale post-action snapshot cannot erase a confirmed clearing receipt", async () => {
  const game = new FakeGame();
  game.snapshot.capturedRevision = 1;
  game.nativeResult = commandId => ({ commandId, terminalState: "partially-succeeded", completedCount: 1, finalWorldRevision: 2 });
  const clear = { id: "clear", name: "submit_work", arguments: { action: {
    clear_debris: { tiles: [{ x: 3, y: 4 }, { x: 3, y: 5 }] },
  } } };
  let result;
  const { runner, events } = fixture(game, { async *generate({ messages }) {
    const last = messages.at(-1);
    if (last.role === "user") { yield done([clear]); return; }
    result = last.result;
    yield done();
  } });
  await runner.submit(binding, "clear", "清理路上的障碍", "send");
  await waitFor(() => events.some(event => event.type === "life.chat.reply"));
  assert.equal(result.terminalState, "partially-succeeded");
  assert.equal(result.completedCount, 1);
  assert.deepEqual(result.afterWork, { status: "unavailable" });
  await runner.stop();
});

test("a decision persists immediately and prevents a following native call in the same response", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  const decision = { id: "decision", name: "request_player_decision", arguments: {
    message: "继续做什么？", choices: ["浇水", "等我"],
  } };
  const events = [];
  const runner = new TurnRunner(game, { async *generate({ messages }) {
    yield messages.at(-1).text?.includes("选择：") ? done([], "我会等你。") : done([decision, action]);
  } }, (type, payload) => events.push({ type, payload }), "", store);
  try {
    await runner.submit(binding, "ask", "先问我下一步", "send");
    await waitFor(() => events.some(event => event.type === "life.chat.reply"));
    const pending = store.items(binding.saveId, "decisions")[0];
    assert.equal(pending.status, "pending");
    assert.equal(game.executed.length, 0);
    await runner.submit(binding, "answer", "等我", "send", true, pending.id);
    assert.equal(store.items(binding.saveId, "decisions")[0].status, "answered");
    assert.equal(store.items(binding.saveId, "decisions")[0].answer, "等我");
    await waitFor(() => events.some(event => event.payload.replyText === "我会等你。"));
  } finally { await runner.stop(); store.close(); }
});

test("a persisted goal resumes only after authority is handed back", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  game.state.controlOwner = "human";
  const save = binding.saveId;
  store.append(save, "farm-request:player", "player", { text: "先打理一下农场" });
  store.updateItem(save, "goals", "farm-goal", { text: "先浇水", requestId: "farm-request",
    status: "active", continuation: "continue" });
  const finish = { id: "finished", name: "manage_arrangements", arguments: {
    change: { manage_goal: { action: "complete" } },
  } };
  const observed = [];
  const runner = new TurnRunner(game, { async *generate({ instructions, messages }) {
    observed.push({ instructions, message: messages.at(-1) });
    yield messages.at(-1).role === "tool" ? done() : done([finish]);
  } }, () => undefined, "", store);
  try {
    game.emit("changed");
    await setTimeout(800);
    assert.equal(observed.length, 0);
    game.state.controlOwner = "agent";
    game.emit("changed");
    await waitFor(() => store.items(save, "goals")[0].status === "completed", 300);
    assert.match(observed[0].message.text, /继续当前目标：先浇水/);
    assert.match(observed[0].instructions, /先打理一下农场/);
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
