import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
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

test("work feedback preserves native evidence and bound goals without per-action completion reminders", async () => {
  const shared = JSON.parse(readFileSync(new URL("../../../contracts/desktop-api/examples/work-feedback.json", import.meta.url), "utf8"));
  const receipts = ["water", "timeout", "pickup"].map(id => shared.receipts.find(row => row.commandId === id));
  const store = new CompanionStore(":memory:");
  store.updateProfile(binding.saveId, { onboarded: true, careFrequency: "chatty" }, 0);
  const game = new FakeGame();
  game.snapshot = { ...game.snapshot, world: { year: 1, season: "spring", dayOfMonth: 18, timeOfDay: 900 } };
  let index = 0;
  game.nativeResult = (id, skill) => ({ ...receipts[index++], commandId: id, skillId: skill });
  const events = [];
  let observedPickup;
  const runner = new TurnRunner(game, { async *generate({ messages }) {
    if (messages.at(-1).role === "user") yield done([
      { id: "goal", name: "manage_arrangements", arguments: { change: { manage_goal: { action: "add", text: "照料和拾取" } } } },
      { id: "water", name: "submit_work", arguments: { action: { water_tiles: { tiles: [{ x: 40, y: 35 }] } } } },
      action,
      { id: "pickup", name: "submit_work", arguments: { action: { pickup_items: { tiles: [{ x: 41, y: 35 }] } } } },
    ]);
    else { observedPickup = messages.at(-1).result; yield done([], "已完成部分工作，需要重新确认剩余拾取目标。"); }
  } }, (type, payload) => events.push({ type, payload }), "", store);
  try {
    game.emit("changed");
    await runner.submit(binding, "work-request", "先照料再拾取", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(game.executed.length, 3);
    assert.equal(observedPickup.completedCount, 2);
    assert.equal(observedPickup.skippedCount, 12);
    assert.equal(observedPickup.error.message, receipts[2].error.message);
    assert.equal(events.filter(event => event.type === "result").length, 3);
    assert.ok(!events.some(event => event.type === "life.care" && event.payload.kind === "work-done"));
    assert.equal(store.unreadCount(binding.saveId), 0);
    const work = runner.status().work;
    assert.equal(work.recentExecutions.length, 3);
    assert.ok(work.recentExecutions.every(row => row.workContext.goalId === work.currentGoalId && row.workContext.requestId === "work-request"));
    assert.ok(work.recentExecutions.every(row => row.gameSessionId === binding.gameSessionId && row.commandId && !Object.hasOwn(row, "taskTitle")));
    assert.deepEqual(work.recentExecutions[1].workContext.target, { x: 12, y: 8 });
    assert.equal(work.recentExecutions[2].error.code, "no-item");
    assert.ok(events.some(event => event.type === "status" && event.payload.activeSkillId === "water-zone"));
  } finally { await runner.stop(); store.close(); }
});

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

const pluginRequest = { pluginId: "org.demo.command", instanceId: "instance", requestId: "pick-two",
  sessionId: binding.gameSessionId, text: "收获两格成熟作物" };

test("plugin submission is isolated, idempotent and keeps partial native results across runtime restart and save switch", async () => {
  const directory = mkdtempSync(join(tmpdir(), "companion-plugin-command-"));
  const db = join(directory, "companion.sqlite");
  let store = new CompanionStore(db);
  const game = new FakeGame();
  game.snapshot = { ...game.snapshot, farmWork: { locationId: "Farm", observationStatus: "observed",
    matureCrops: [{ x: 5, y: 6, cropId: "parsnip" }, { x: 7, y: 6, cropId: "parsnip" }] } };
  const existing = store.updateItem(binding.saveId, "goals", "player-goal",
    { text: "玩家稍后处理的目标", status: "active", continuation: "wait", wakeTime: 1700 });
  let finish;
  game.nativeResult = id => new Promise(resolve => { finish = () => resolve({
    commandId: id, skillId: "harvest-zone", terminalState: "partially-succeeded", completedCount: 1,
  }); });
  const events = [];
  let runner = new TurnRunner(game, { async *generate({ messages }) {
    if (messages.at(-1).role === "user") yield done([{ id: "harvest", name: "submit_work",
      arguments: { action: { harvest_auto: {} } } }]);
    else yield done([], "确认收获一格，还有剩余。");
  } }, (type, payload) => events.push({ type, payload }), "", store);
  try {
    game.emit("changed");
    assert.equal(runner.submitPlugin(pluginRequest).duplicate, false);
    assert.equal(runner.submitPlugin(pluginRequest).duplicate, true);
    assert.throws(() => runner.submitPlugin({ ...pluginRequest, text: "不同指令" }), { code: "request_conflict" });
    assert.throws(() => runner.submitPlugin({ ...pluginRequest, requestId: "concurrent" }), { code: "busy" });
    await assert.rejects(runner.queryPlugin("org.demo.other", pluginRequest.requestId), { code: "not_found" });
    await waitFor(() => finish);
    finish();
    await waitFor(() => events.some(event => event.type === "life.chat.reply"));
    assert.deepEqual(store.items(binding.saveId, "goals"), [existing]);
    const expected = await runner.queryPlugin(pluginRequest.pluginId, pluginRequest.requestId);
    assert.equal(expected.status, "partial");
    assert.equal(expected.turnEnded, true);
    assert.deepEqual(expected.actions, [{ actionId: game.executed[0], skillId: "harvest-zone",
      terminalState: "partially-succeeded", completedCount: 1 }]);
    await runner.stop();
    store.close();
    store = new CompanionStore(db);
    const restartedGame = new FakeGame();
    restartedGame.binding = { saveId: "other-farm", gameSessionId: "new-game" };
    runner = new TurnRunner(restartedGame, undefined, () => {}, "", store);
    restartedGame.emit("changed");
    assert.deepEqual(await runner.queryPlugin(pluginRequest.pluginId, pluginRequest.requestId), expected);
    assert.deepEqual(runner.submitPlugin(pluginRequest), { duplicate: true, result: expected });
    assert.throws(() => runner.submitPlugin({ ...pluginRequest, requestId: "old-game" }), { code: "session_changed" });
    assert.equal(game.executed.length, 1);
    assert.equal(restartedGame.executed.length, 0);
  } finally {
    await runner.stop();
    store.close();
    rmSync(directory, { recursive: true, force: true });
  }
});

test("plugin admission respects player gates and takeover cancels a running plugin task", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  let finish;
  const runner = new TurnRunner(game, { async *generate() {
    await new Promise(resolve => { finish = resolve; });
    yield done([action]);
  } }, () => {}, "", store);
  try {
    game.emit("changed");
    game.state.paused = true;
    assert.throws(() => runner.submitPlugin(pluginRequest), { code: "paused" });
    game.state.paused = false;
    game.state.controlOwner = "human";
    game.emit("changed");
    assert.throws(() => runner.submitPlugin(pluginRequest), { code: "player_control" });
    game.state.controlOwner = "agent";
    game.emit("changed");
    store.updateItem(binding.saveId, "goals", "player", { status: "active", continuation: "continue" });
    assert.throws(() => runner.submitPlugin(pluginRequest), { code: "busy" });
    store.updateItem(binding.saveId, "goals", "player", { status: "completed" });
    store.updateItem(binding.saveId, "decisions", "decision", { status: "pending" });
    assert.throws(() => runner.submitPlugin(pluginRequest), { code: "waiting_player" });
    store.changeDecision(binding.saveId, "decision", "dismiss");
    game.snapshot.companion.bedtimeSchedule = { returnDue: true };
    assert.throws(() => runner.submitPlugin(pluginRequest), { code: "waiting_player" });
    game.snapshot.companion.bedtimeSchedule.returnDue = false;
    runner.submitPlugin(pluginRequest);
    await waitFor(() => finish);
    game.state.controlOwner = "human";
    game.emit("changed");
    finish();
    await waitFor(() => !runner.status().modelBusy);
    assert.equal((await runner.queryPlugin(pluginRequest.pluginId, pluginRequest.requestId)).status, "cancelled");
    assert.deepEqual(game.executed, []);
  } finally {
    await runner.stop();
    store.close();
  }
});

test("plugin continuation creates a player decision and never creates an autonomous goal", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  const runner = new TurnRunner(game, { async *generate() {
    yield done([{ id: "wait", name: "manage_arrangements", arguments: { change: {
      set_goal_continuation: { status: "wait", reason: "第二天继续", next_day: true },
    } } }, action]);
  } }, () => {}, "", store);
  try {
    game.emit("changed");
    runner.submitPlugin(pluginRequest);
    await waitFor(() => !runner.status().modelBusy);
    const outcome = await runner.queryPlugin(pluginRequest.pluginId, pluginRequest.requestId);
    assert.equal(outcome.status, "waiting_player");
    assert.equal(outcome.goalOutcome, "unknown");
    assert.equal(store.items(binding.saveId, "decisions").length, 1);
    assert.deepEqual(store.items(binding.saveId, "goals"), []);
    assert.deepEqual(game.executed, []);
  } finally {
    await runner.stop();
    store.close();
  }
});

test("an interrupted plugin receipt can be queried and recovered without a second execution", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  game.nativeResult = () => { throw new Error("lost connection"); };
  const runner = new TurnRunner(game, { async *generate() { yield done([action]); } }, () => {}, "", store);
  try {
    game.emit("changed");
    runner.submitPlugin(pluginRequest);
    await waitFor(() => !runner.status().modelBusy);
    const pending = await runner.queryPlugin(pluginRequest.pluginId, pluginRequest.requestId);
    assert.equal(pending.status, "recovery_pending");
    assert.deepEqual(pending.actions, []);
    game.recovered = { commandId: game.executed[0], skillId: "navigate-to", terminalState: "succeeded" };
    const settled = await runner.queryPlugin(pluginRequest.pluginId, pluginRequest.requestId);
    assert.notEqual(settled.status, "recovery_pending");
    assert.equal(settled.actions.length, 1);
    assert.ok(!("completedCount" in settled.actions[0]));
    assert.equal(game.executed.length, 1);
  } finally {
    await runner.stop();
    store.close();
  }
});

test("explicit stop cancels a plugin action even after its source process has gone away", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  let finish;
  game.nativeResult = id => new Promise(resolve => {
    finish = () => resolve({ commandId: id, skillId: "navigate-to", terminalState: "cancelled" });
  });
  const runner = new TurnRunner(game, { async *generate() { yield done([action]); } }, () => {}, "", store);
  try {
    game.emit("changed");
    runner.submitPlugin(pluginRequest);
    await waitFor(() => finish);
    const stopping = runner.closePlugin(pluginRequest.pluginId);
    assert.ok(game.controls.includes("interrupt"));
    finish();
    await stopping;
    const result = await runner.queryPlugin(pluginRequest.pluginId, pluginRequest.requestId);
    assert.equal(result.status, "cancelled");
    assert.equal(result.actions[0].terminalState, "cancelled");
    assert.throws(() => runner.submitPlugin({ ...pluginRequest, requestId: "late-request" }), { code: "invalid_instance" });
    assert.deepEqual(game.controls.slice(-2), ["interrupt", "unhold"]);
  } finally {
    await runner.stop();
    store.close();
  }
});

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

test("the saved player bedtime triggers native sleep, then replans the same waiting goal from a fresh morning observation", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  const save = binding.saveId;
  store.updateProfile(save, { bedtime: 2200 }, 0);
  store.updateItem(save, "goals", "irrigation", { status: "active", text: "照料作物", requestId: "farm-request",
    summary: "复查剩余待浇作物", continuation: "wait", waitNextDay: true, updatedDay: "1:spring:3" });
  game.snapshot = { companion: { locationId: "Town", bedtime: 2200, bedtimeSchedule: { latestDeparture: 2100, returnDue: true } },
    world: { year: 1, season: "spring", dayOfMonth: 3, timeOfDay: 2100, native: { playerCanMove: true } } };
  game.state.controlOwner = "human";
  const calls = [];
  const observations = [];
  game.nativeResult = (commandId, skill, parameters) => {
    calls.push(skill);
    assert.deepEqual(calls, ["sleep"]);
    assert.equal(parameters.locationId, "Town");
    return { commandId, skillId: skill, terminalState: "succeeded", completedCount: 1 };
  };
  game.observe = async () => {
    game.snapshot = { companion: { locationId: "FarmHouse", bedtime: 2200 },
      world: { year: 1, season: "spring", dayOfMonth: 4, timeOfDay: 600, isRaining: true } };
    return game.snapshot;
  };
  const runner = new TurnRunner(game, { async *generate(input) { observations.push(input); yield done(); } }, () => undefined, "", store);
  try {
    game.emit("changed");
    await setTimeout(800);
    assert.deepEqual(calls, []);
    game.state.controlOwner = "agent";
    game.emit("changed");
    await waitFor(() => observations.length === 1, 450);
    assert.deepEqual(calls, ["sleep"]);
    assert.match(observations[0].messages.at(-1).text, /照料作物.*复查剩余待浇作物/s);
    assert.match(observations[0].instructions, /"dayOfMonth":4.*"isRaining":true/);
    assert.equal(store.items(save, "goals")[0].id, "irrigation");
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
