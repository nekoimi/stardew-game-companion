import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setTimeout } from "node:timers/promises";
import { test } from "node:test";
import { TurnRunner } from "../dist/application/turn-runner.js";
import { CompanionTools } from "../dist/tools/companion-tools.js";
import { CompanionStore } from "../dist/adapters/storage/companion-store.js";
import { actionEvidence } from "../dist/application/work-feedback.js";

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
  for (const receipt of shared.receipts) assert.deepEqual(actionEvidence(receipt), receipt.evidence ?? { quantities: [] });
  const receipts = ["water", "timeout", "pickup"].map(id => shared.receipts.find(row => row.commandId === id));
  const store = new CompanionStore(":memory:");
  store.updateProfile(binding.saveId, { onboarded: true, careFrequency: "chatty" }, 0);
  const game = new FakeGame();
  game.snapshot = { ...game.snapshot, farming: { location: "Farm", groundItems: [{ tile: { x: 41, y: 35 }, isDropped: true }] },
    farmWork: { locationId: "Farm", observationStatus: "observed",
    cropUnwateredTiles: [{ x: 40, y: 35 }] }, world: { year: 1, season: "spring", dayOfMonth: 18, timeOfDay: 900 } };
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
    assert.deepEqual(work.recentExecutions[2].evidence, receipts[2].evidence);
    assert.ok(work.recentExecutions.every(row => row.workContext.turnId && row.workContext.triggerRequestId === "work-request" && row.recordedAt));
    assert.ok(events.some(event => event.type === "status" && event.payload.activeSkillId === "water-zone"));
  } finally { await runner.stop(); store.close(); }
});

test("observation and recovery query actual state without executing or handing control back", async () => {
  const game = new FakeGame();
  game.nativeResult = () => { throw new Error("missing receipt"); };
  const { runner, events } = fixture(game, { async *generate() { yield done([action]); } });
  try {
    await runner.submit(binding, "missing", "走到出口", "send");
    await waitFor(() => runner.status().recoveryPending);
    assert.equal(runner.status().agentPhase, "unresolved");
    const executed = game.executed.length;
    game.state.controlOwner = "human";
    const observed = await runner.control(binding, "observe");
    assert.deepEqual(observed.observation, game.snapshot);
    assert.equal(game.executed.length, executed);
    assert.equal(runner.status().recoveryPending, true);
    game.recovered = { commandId: game.executed[0], skillId: "navigate-to", terminalState: "partially-succeeded", completedCount: 1 };
    await runner.control(binding, "recover");
    assert.equal(runner.status().recoveryPending, false);
    assert.equal(runner.status().agentPhase, "player-control");
    assert.ok(!game.controls.includes("handoff"));
    assert.ok(events.some(event => event.type === "status" && event.payload.agentPhase === "reconciling"));
  } finally { await runner.stop(); }
});

test("messages received during an action apply to following work, not the in-flight action's attribution", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  game.snapshot = { ...game.snapshot, farmWork: { locationId: "Farm", observationStatus: "observed",
    cropUnwateredTiles: [{ x: 1, y: 1 }] } };
  let release, step = 0;
  game.nativeResult = async (commandId, skillId) => {
    if (skillId === "water-zone") await new Promise(resolve => { release = resolve; });
    return { commandId, skillId, terminalState: "succeeded" };
  };
  const runner = new TurnRunner(game, { async *generate() {
    yield done(step++ === 0 ? [{ id: "water", name: "submit_work", arguments: {
      action: { water_tiles: { tiles: [{ x: 1, y: 1 }] } } } }] : step === 2 ? [action] : []);
  } }, () => undefined, "", store);
  try {
    await runner.submit(binding, "first", "先浇水", "send");
    await waitFor(() => release);
    await runner.submit(binding, "update", "再走到出口", "send");
    release();
    await waitFor(() => !runner.status().modelBusy);
    const receipts = store.recentReceipts(binding.saveId);
    assert.equal(receipts.length, 2);
    assert.equal(receipts[0].workContext.requestId, "first");
    assert.equal(receipts[0].workContext.triggerRequestId, "first");
    assert.equal(receipts[1].workContext.requestId, "first");
    assert.equal(receipts[1].workContext.triggerRequestId, "update");
  } finally { await runner.stop(); store.close(); }
});

test("routine requests carry a compact observation while detailed queries return the original facts", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  const soil = Array.from({ length: 30 }, (_, x) => ({ x, y: 5 }));
  const farmWork = { locationId: "Farm", observationStatus: "observed", cropUnwateredCount: soil.length,
    cropUnwateredTiles: soil, cropUnwateredTruncated: false };
  const chests = { items: [{ tile: { x: 7, y: 8 }, slots: [{ itemId: "wood", stack: 15 }] }] };
  game.snapshot = { companion: { locationId: "Farm", waterCanLevel: 6 }, world: { timeOfDay: 800, farmWork },
    farmWork, chests, farming: { groundItems: [{ tile: { x: 2, y: 3 }, isDropped: true }] } };
  const seen = [];
  const runner = new TurnRunner(game, { async *generate({ instructions, context, messages }) {
    seen.push({ instructions, context, last: messages.at(-1) });
    yield seen.length === 1 ? done([{ id: "chest", name: "query_game", arguments: { query: { query_chests: {} } } }]) : done();
  } }, () => undefined, "Game rules", store);
  try {
    await runner.submit(binding, "chests", "看看箱子木材", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(seen.length, 2);
    const { context, instructions } = seen[0];
    assert.equal(instructions, "Game rules");
    assert.equal(context.originalRequest, "看看箱子木材");
    assert.equal(context.observations.companion.waterCanLevel, 6);
    assert.equal(context.observations.farmWork.cropUnwateredCount, 30);
    assert.equal(context.observations.farmWork.cropUnwateredTruncated, true);
    assert.equal(context.observations.farmWork.cropUnwateredTiles.length, 24);
    assert.equal(context.observations.world.farmWork, undefined);
    assert.equal(context.observations.chests, undefined);
    assert.equal(context.observations.farming, undefined);
    assert.deepEqual(seen[1].last.result, chests);
  } finally { await runner.stop(); store.close(); }
});

test("automatic watering accepts observed crops only and rejects invented empty-soil targets", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  game.snapshot = { companion: { locationId: "Farm" }, farmWork: { locationId: "Farm", observationStatus: "observed",
    cropUnwateredTiles: [{ x: 2, y: 3 }] }, world: { farmWork: { locationId: "Farm", observationStatus: "observed",
    cropUnwateredTiles: [{ x: 2, y: 3 }] } } };
  const water = { id: "water", name: "submit_work", arguments: { action: {
    water_tiles: { tiles: [{ x: 2, y: 3 }, { x: 8, y: 9 }] },
  } } };
  const calls = [];
  game.nativeResult = (commandId, skillId, parameters) => {
    calls.push({ skillId, parameters });
    return { commandId, skillId, terminalState: "succeeded", completedCount: parameters.tiles.length };
  };
  const runner = new TurnRunner(game, { async *generate({ messages }) {
    if (messages.at(-1).role === "user") yield done([water]);
    else yield done();
  } }, () => undefined, "", store);
  try {
    await runner.submit(binding, "water-request", "浇水", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(calls.length, 1);
    assert.deepEqual(calls[0].parameters.tiles, [{ x: 2, y: 3 }]);
    assert.equal(calls[0].parameters.includeEmptyTiles, false);
  } finally { await runner.stop(); store.close(); }
});

test("empty, stale and missing observations never dispatch crop or pickup actions", async () => {
  const game = new FakeGame();
  const tools = new CompanionTools(game, "");
  const actions = [];
  const inspections = [];
  const context = { binding, turnId: "batch", requestId: "batch", goalId: "batch", signal: new AbortController().signal,
    async native(skill, parameters, observation = false) {
      if (observation) {
        inspections.push(skill);
        return { terminalState: "succeeded", details: { production: {
          locationId: parameters.locationId, groundItems: [], groundItemsTruncated: false,
        } } };
      }
      actions.push({ skill, parameters });
      return { terminalState: "succeeded" };
    }, speak() {}, changed() {} };
  game.snapshot = { companion: { locationId: "Farm" },
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: [],
      cropUnwateredTruncated: false, matureCrops: [], matureCropsTruncated: false },
    farming: { location: "Farm", groundItems: [], groundItemsTruncated: false } };

  const invoke = (action) => tools.invoke(context, "submit_work", { action });
  const noWater = await invoke({ water_auto: { location_id: "Farm" } });
  const noHarvest = await invoke({ harvest_auto: { location_id: "Farm" } });
  const staleWater = await invoke({ water_tiles: { location_id: "Farm", tiles: [{ x: 2, y: 3 }] } });
  const staleHarvest = await invoke({ harvest_tiles: { location_id: "Farm", tiles: [{ x: 2, y: 3 }] } });
  const noPickup = await invoke({ pickup_items: { location_id: "Farm", tiles: [{ x: 8, y: 9 }] } });

  for (const result of [noWater, noHarvest, staleWater, staleHarvest, noPickup]) {
    assert.equal(result.terminalState, "no-observed-targets");
    assert.equal(result.observedTargets, 0);
  }
  assert.ok(noWater.observationComplete);
  assert.ok(noHarvest.observationComplete);
  assert.deepEqual(actions, []);
  assert.deepEqual(inspections, ["inspect-production"]);
});

test("crop-care workflow waters, refills, and resumes from fresh observations", async () => {
  const game = new FakeGame();
  const tools = new CompanionTools(game, "");
  let dry = [{ x: 1, y: 1 }, { x: 2, y: 1 }, { x: 3, y: 1 }];
  let waterLeft = 1;
  let revision = 1;
  const nativeCalls = [];
  game.snapshot = { companion: { locationId: "Farm", waterCanLevel: waterLeft, hasWateringCan: true },
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: dry,
      cropUnwateredCount: dry.length, cropUnwateredTruncated: false }, capturedRevision: 1 };
  game.observe = async () => {
    game.snapshot.companion.waterCanLevel = waterLeft;
    game.snapshot.farmWork.cropUnwateredTiles = dry;
    game.snapshot.farmWork.cropUnwateredCount = dry.length;
    game.snapshot.capturedRevision = revision;
    return game.snapshot;
  };
  const context = { binding, turnId: "care", requestId: "care", goalId: "care", signal: new AbortController().signal,
    async native(skill, parameters, observation = false) {
      nativeCalls.push({ skill, parameters, observation });
      if (skill === "inspect-farm-work") return { commandId: `inspect-${nativeCalls.length}`, terminalState: "succeeded",
        finalWorldRevision: revision, details: { farmWork: { locationId: "Farm", observationStatus: "observed",
          cropUnwateredTiles: dry, cropUnwateredCount: dry.length, cropUnwateredTruncated: false } } };
      if (skill === "water-zone") {
        const targets = parameters.tiles;
        dry = dry.filter(value => !targets.some(target => value.x === target.x && value.y === target.y));
        waterLeft -= targets.length;
        return { commandId: `water-${nativeCalls.length}`, terminalState: "succeeded", completedCount: targets.length,
          effects: targets.map(tile => ({ state: "watered", tile })), finalWorldRevision: ++revision };
      }
      if (skill === "inspect-water-sources") return { terminalState: "succeeded",
        finalWorldRevision: revision, details: { tiles: [{ x: 4, y: 1 }] } };
      if (skill === "refill-watering-can") {
        waterLeft = 2;
        return { commandId: `refill-${nativeCalls.length}`, terminalState: "succeeded",
          finalWorldRevision: ++revision };
      }
      assert.fail(`Unexpected native operation ${skill}`);
    }, speak() {}, changed() {} };

  const result = await tools.invoke(context, "submit_work", { action: { tend_crops: { location_id: "Farm" } } });
  assert.equal(result.status, "completed");
  assert.equal(result.stopReason, "no-dry-crops");
  assert.equal(result.completedCount, 3);
  assert.equal(result.refills, 1);
  assert.deepEqual(dry, []);
  assert.deepEqual(nativeCalls.filter(call => !call.observation).map(call => call.skill),
    ["water-zone", "refill-watering-can", "water-zone"]);
  assert.equal(nativeCalls.filter(call => call.skill === "refill-watering-can").length, 1);
});

test("crop-care stops without a water source and reports the unwatered work", async () => {
  const game = new FakeGame();
  const tools = new CompanionTools(game, "");
  game.snapshot = { companion: { locationId: "Farm", waterCanLevel: 0, hasWateringCan: true }, capturedRevision: 3,
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: [{ x: 2, y: 2 }],
      cropUnwateredTruncated: false } };
  const calls = [];
  const context = { binding, turnId: "care", requestId: "care", goalId: "care", signal: new AbortController().signal,
    async native(skill, _parameters, observation = false) {
      calls.push({ skill, observation });
      return skill === "inspect-water-sources"
        ? { terminalState: "succeeded", details: { tiles: [] } }
        : assert.fail(`Unexpected native operation ${skill}`);
    }, speak() {}, changed() {} };

  const result = await tools.invoke(context, "submit_work", { action: { tend_crops: { location_id: "Farm" } } });
  assert.equal(result.status, "stopped");
  assert.equal(result.stopReason, "no-observed-water-source");
  assert.equal(result.completedCount, 0);
  assert.equal(result.remainingCount, 1);
  assert.deepEqual(calls, [{ skill: "inspect-water-sources", observation: true }]);
});

test("crop-care stops cleanly when the complete observed region has no dry crops", async () => {
  const game = new FakeGame();
  const tools = new CompanionTools(game, "");
  game.snapshot = { companion: { locationId: "Farm", waterCanLevel: 10, hasWateringCan: true }, capturedRevision: 3,
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: [],
      cropUnwateredTruncated: false } };
  const context = { binding, turnId: "care", requestId: "care", goalId: "care", signal: new AbortController().signal,
    async native() { assert.fail("No action or extra observation is valid for a complete empty crop scan."); },
    speak() {}, changed() {} };
  const result = await tools.invoke(context, "submit_work", { action: { tend_crops: { location_id: "Farm" } } });
  assert.equal(result.status, "stopped");
  assert.equal(result.stopReason, "no-observed-targets");
  assert.equal(result.completedCount, 0);
  assert.equal(result.remainingCount, 0);
  assert.equal(result.observationComplete, true);
});

test("crop-care retains settled work and stops for a player update before dispatching again", async () => {
  const game = new FakeGame();
  const tools = new CompanionTools(game, "");
  let blocked = "";
  game.snapshot = { companion: { locationId: "Farm", waterCanLevel: 1, hasWateringCan: true }, capturedRevision: 3,
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: [{ x: 2, y: 2 }],
      cropUnwateredTruncated: false } };
  const calls = [];
  const context = { binding, turnId: "care", requestId: "care", goalId: "care", signal: new AbortController().signal,
    workBlock: () => blocked,
    async native(skill, _parameters) {
      calls.push(skill);
      if (skill === "water-zone") {
        blocked = "player-update";
        return { commandId: "water-1", terminalState: "succeeded", finalWorldRevision: 4,
          effects: [{ state: "watered", tile: { x: 2, y: 2 } }] };
      }
      return assert.fail(`Unexpected native operation ${skill}`);
    }, speak() {}, changed() {} };
  game.observe = async () => game.snapshot;

  const result = await tools.invoke(context, "submit_work", { action: { tend_crops: { location_id: "Farm" } } });
  assert.equal(result.stopReason, "player-update");
  assert.equal(result.completedCount, 1);
  assert.deepEqual(calls, ["water-zone"]);
});

test("crop-care reconciles the lost receipt before issuing the next action", async () => {
  const game = new FakeGame();
  const crops = [{ x: 2, y: 2 }, { x: 3, y: 2 }];
  game.snapshot = { companion: { locationId: "Farm", waterCanLevel: 1, hasWateringCan: true }, capturedRevision: 1,
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: crops,
      cropUnwateredTruncated: false } };
  const actions = [];
  game.nativeResult = (commandId, skillId, parameters) => {
    if (skillId === "inspect-water-sources")
      return { commandId, terminalState: "succeeded", details: { tiles: [{ x: 4, y: 2 }] } };
    actions.push(skillId);
    const receipt = { commandId, skillId, terminalState: "succeeded", finalWorldRevision: ++game.snapshot.capturedRevision };
    if (skillId === "refill-watering-can") {
      assert.equal(game.queryCount, 1);
      game.snapshot.companion.waterCanLevel = 4;
      return receipt;
    }
    game.snapshot.farmWork.cropUnwateredTiles = game.snapshot.farmWork.cropUnwateredTiles.slice(1);
    game.snapshot.companion.waterCanLevel--;
    const settled = { ...receipt, completedCount: 1, effects: [{ state: "watered", tile: parameters.tiles[0] }] };
    if (actions.length !== 1) return settled;
    game.recovered = settled;
    throw new Error("lost acknowledgement");
  };
  let calls = 0;
  let summary;
  const { runner } = fixture(game, { async *generate({ messages, tools }) {
    assert.equal(tools.length, 8);
    if (++calls === 1) {
      yield done([{ id: "care", name: "submit_work", arguments: { action: { tend_crops: { location_id: "Farm" } } } }]);
    } else {
      summary = messages.at(-1).result;
      yield done();
    }
  } });
  try {
    await runner.submit(binding, "care-recovered", "给田里的作物浇水", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.deepEqual(actions, ["water-zone", "refill-watering-can", "water-zone"]);
    assert.equal(game.queryCount, 1);
    assert.equal(calls, 2);
    assert.equal(summary.completedCount, 2);
    assert.equal(summary.remainingCount, 0);
  } finally { await runner.stop(); }
});

function fixture(game, model) {
  const events = [];
  const runner = new TurnRunner(game, model, (type, payload) => events.push({ type, payload }), "Game rules");
  return { runner, events };
}

test("resting indoors removes the Farm phase reference and returning prepares a fresh observation", async () => {
  const game = new FakeGame();
  game.supportsPhaseMap = true;
  const store = new CompanionStore(":memory:");
  const seen = [];
  game.nativeResult = (commandId, skillId, params) => {
    if (skillId === "inspect-phase-map") return { commandId, skillId, terminalState: "succeeded", details: { page: {
      locationId: "Farm", fingerprint: "farm-map", width: 1, height: 1, offset: 0,
      end: params.scanLimit ? 1 : 0, nextOffset: params.scanLimit ? -1 : 0, complete: !!params.scanLimit,
      trees: [], waterTiles: [], fixtures: [] } } };
    game.snapshot = { ...game.snapshot, companion: { locationId: skillId === "rest" ? "FarmHouse" : "Farm" } };
    return { commandId, skillId, terminalState: "succeeded" };
  };
  const runner = new TurnRunner(game, { async *generate({ context }) {
    seen.push(structuredClone(context));
    yield seen.length === 1 ? done([{ id: "rest", name: "submit_work", arguments: {
      action: { rest: { target_stamina: 200 } } } }]) : seen.length === 2 ? done([action]) : done();
  } }, () => {}, "", store);
  try {
    await runner.submit(binding, "rest-and-return", "先休息，然后回农场", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(seen.length, 3);
    assert.ok(seen[0].phaseObservation?.ref);
    assert.equal(seen[1].observations.companion.locationId, "FarmHouse");
    assert.equal(seen[1].phaseObservation, undefined);
    assert.equal(seen[2].observations.companion.locationId, "Farm");
    assert.ok(seen[2].phaseObservation?.ref);
    assert.notEqual(seen[2].phaseObservation.ref, seen[0].phaseObservation.ref);
  } finally { await runner.stop(); store.close(); }
});

test("a verified phase and finite goal confirmation finish in two model requests without an extra summary request", async () => {
  const game = new FakeGame();
  game.supportsPhaseMap = true;
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm", stamina: 270, waterCanLevel: 40, hasWateringCan: true },
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: [{ x: 0, y: 0 }], cropUnwateredTruncated: false },
    inventory: { slots: [], freeSlots: 12 }, chests: { items: [{ tile: { x: 0, y: 0 }, contentsObserved: true, contents: [] }] } };
  const store = new CompanionStore(":memory:");
  let requests = 0;
  const skills = [], events = [];
  game.nativeResult = (commandId, skillId, params) => {
    if (skillId === "inspect-phase-map") return { commandId, skillId, terminalState: "succeeded", details: { page: {
      locationId: "Farm", fingerprint: "care-map", width: 1, height: 1, offset: 0,
      end: params.scanLimit ? 1 : 0, nextOffset: params.scanLimit ? -1 : 0, complete: !!params.scanLimit,
      trees: [], waterTiles: [], fixtures: [] } } };
    if (skillId === "inspect-production") return { commandId, skillId, terminalState: "succeeded",
      details: { production: { locationId: "Farm", groundItems: [], groundItemsTruncated: false } } };
    skills.push(skillId);
    game.snapshot.farmWork.cropUnwateredTiles = [];
    return { commandId, skillId, terminalState: "succeeded", completedCount: 1,
      effects: [{ tile: { x: 0, y: 0 }, state: "watered" }] };
  };
  const runner = new TurnRunner(game, { async *generate({ context }) {
    if (++requests === 1) {
      const observed = context.phaseObservation;
      yield done([{ id: "care", name: "submit_work", arguments: { action: { execute_phase: {
        observation_ref: observed.ref, scope_ref: observed.scopeRef, objective: "照料当天作物", completion: { metric: "daily-care-stored", quantity: 1 },
        constraints: { stamina_floor: 80, destination_ref: "chest-1", keep: [], include_existing: [] },
        steps: [{ id: "care", operation: "tend-crops", after: [], inputs: [] },
          { id: "collect", operation: "collect-products", after: ["care"], inputs: ["care"] },
          { id: "store", operation: "store-products", after: ["collect"], inputs: ["collect"] }], branches: [] } } } }]);
    } else if (requests === 2) yield done([{ id: "complete", name: "manage_arrangements", arguments: {
      change: { manage_goal: { action: "complete" } } } }], "");
    else yield done([], "不应额外请求这条总结。");
  } }, (type, payload) => events.push({ type, payload }), "", store);
  try {
    await runner.submit(binding, "one-phase", "照料完今天的作物就结束", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.deepEqual(skills, ["water-zone"]);
    assert.equal(requests, 2, JSON.stringify(store.items(binding.saveId, "goals")));
    assert.equal(store.items(binding.saveId, "goals")[0].status, "completed");
    assert.ok(events.some(event => event.type === "life.chat.reply" && event.payload.replyText === "这一阶段已完成，成果已核对。"));
  } finally { await runner.stop(); store.close(); }
});

test("forestry rejects a protected or unobserved tree before any native action", async () => {
  const game = new FakeGame();
  const tools = new CompanionTools(game, "");
  game.snapshot = { companion: { locationId: "Farm" }, farming: { location: "Farm", choppableTrees: [
    { tile: { x: 2, y: 2 }, kind: "tree", growthStage: 2, tapped: false },
    { tile: { x: 3, y: 2 }, kind: "tree", growthStage: 5, tapped: true },
  ] } };
  const context = { binding, turnId: "forestry", goalId: "forestry", requestId: "forestry",
    signal: new AbortController().signal, native() { assert.fail("Protected trees must not be dispatched"); }, speak() {}, changed() {} };
  for (const x of [2, 3, 4]) await assert.rejects(tools.invoke(context, "submit_work", {
    action: { chop_tree: { location_id: "Farm", tiles: [{ x, y: 2 }] } },
  }), /成熟/);
});

test("ongoing farming retains newer player authorization beyond chat history and executes another branch before waiting", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  store.append(binding.saveId, "original:player", "player", { text: "先照料农田" });
  store.append(binding.saveId, "expanded:player", "player", { text: "持续经营农场直到我叫停，日常之后自主制作仓储设施，保留幼树" });
  for (let i = 0; i < 40; i++) store.append(binding.saveId, "summary-" + i, "assistant", { text: "日常已完成，下一日再规划" });
  store.updateItem(binding.saveId, "goals", "farm-goal", { text: "春24收尾，下一日再说", status: "active",
    continuation: "continue", requestId: "original" });
  game.snapshot = { companion: { locationId: "Farm", stamina: 230 }, capturedRevision: 1,
    world: { year: 1, season: "spring", dayOfMonth: 24, timeOfDay: 900 },
    inventory: { slots: [{ itemId: "(O)388", stack: 100 }], freeSlots: 8 } };
  const native = [];
  game.nativeResult = (commandId, skillId) => {
    native.push(skillId);
    return { commandId, skillId, terminalState: skillId === "navigate-to" ? "failed" : "succeeded",
      error: skillId === "navigate-to" ? { code: "ROUTE_PLANNING_TIMEOUT" } : null, completedCount: 1 };
  };
  let calls = 0;
  const arrange = change => ({ id: "plan-" + calls, name: "manage_arrangements", arguments: { change } });
  const seen = [];
  const runner = new TurnRunner(game, { async *generate({ context, messages }) {
    seen.push(context);
    switch (++calls) {
      case 1: yield done([arrange({ manage_goal: { action: "update", text: "今天收尾，明天再说", summary: "日常完成" } })]); break;
      case 2: yield done([], "今天照料结束，明天继续。"); break;
      case 3:
        assert.match(messages.at(-1).text, /持续经营接续检查/);
        yield done([{ id: "shop", name: "submit_work", arguments: { action: {
          navigate_to: { location_id: "SeedShop", tile: { x: 4, y: 19 } } } } }]); break;
      case 4: yield done([
        arrange({ set_day_plan: { focus: "补充仓储", next_step: "用现有木材制作箱子",
          alternatives: ["商店路线暂时受阻，先做农场内可行制作"],
          blocked_branches: [{ branch: "购种", reason: "已确认路线规划超时", command_id: game.executed[0] }] } }),
        { id: "craft", name: "submit_work", arguments: { action: {
          craft_items: { location_id: "Farm", recipe_name: "Chest", item_count: 1 } } } },
      ]); break;
      case 5: yield done([arrange({ set_day_plan: { focus: "今日设施制作完成", next_step: "等待商店窗口或下一天",
        alternatives: ["没有种子，暂停补种；制作目标已达成；保留幼树，不盲目耗体力"], blocked_branches: [] } }),
        arrange({ set_goal_continuation: { status: "wait", next_day: true, reason: "已完成当前设施制作并核对其他方向" } })]); break;
      default: yield done([], "设施已制作，等待下一次经营窗口。");
    }
  } }, () => {}, "", store);
  try {
    await runner.submit(binding, "retry", "继续", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.deepEqual(native, ["navigate-to", "craft-items"]);
    const [goal] = store.items(binding.saveId, "goals");
    assert.match(goal.text, /持续经营农场直到我叫停/);
    assert.equal(goal.currentFocus, "今天收尾，明天再说");
    assert.equal(goal.status, "active");
    assert.equal(goal.horizon, "ongoing");
    assert.equal(goal.continuation, "wait");
    assert.ok(seen[0].goalInstructions.some(item => item.requestId === "expanded"));
    assert.match(seen[0].planning.longTermObjective, /自主制作仓储设施/);
    assert.equal(seen[0].planning.resources.stamina, 230);
    assert.equal(seen[0].planning.resources.inventory.items[0].count, 100);
    assert.equal(calls, 6);
  } finally { await runner.stop(); store.close(); }
});

test("an ongoing goal cannot end or wait without a current alternatives assessment and never spins on summaries", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  store.append(binding.saveId, "original:player", "player", { text: "持续经营农场，直到我叫停" });
  store.updateItem(binding.saveId, "goals", "goal", { text: "经营农场", requestId: "original",
    horizon: "ongoing", status: "active", continuation: "continue" });
  game.snapshot = { companion: { locationId: "Farm", stamina: 220 },
    world: { year: 1, season: "spring", dayOfMonth: 1, timeOfDay: 1000 } };
  const context = { binding, turnId: "test", requestId: "original", goalId: "goal",
    signal: new AbortController().signal, native() { assert.fail("No native action expected"); }, speak() {}, changed() {} };
  const tools = new CompanionTools(game, "", store);
  await assert.rejects(tools.invoke(context, "manage_arrangements", { change: { manage_goal: { action: "complete" } } }), /持续目标/);
  await assert.rejects(tools.invoke(context, "manage_arrangements", { change: {
    set_goal_continuation: { status: "wait", reason: "日常结束", next_day: true } } }), /替代工作/);
  await assert.rejects(tools.invoke(context, "manage_arrangements", { change: {
    set_goal_continuation: { status: "wait", reason: "时间已过", next_time: 900 } } }), /尚未到达/);
  let calls = 0;
  const { runner, events } = (() => {
    const events = [];
    return { events, runner: new TurnRunner(game, { async *generate() { calls++; yield done([], "明天再安排"); } },
      (type, payload) => events.push({ type, payload }), "", store) };
  })();
  try {
    game.emit("changed");
    await runner.submit(binding, "continue", "继续经营", "send");
    await waitFor(() => !runner.status().modelBusy);
    await setTimeout(850);
    assert.equal(calls, 3);
    assert.ok(events.some(row => row.type === "life.chat.reply" && row.payload.status === "failed"
      && row.payload.error.includes("下一步")));
    assert.equal(store.items(binding.saveId, "goals")[0].planningIssue, "missing-next-action");
    assert.equal(game.executed.length, 0);
  } finally { await runner.stop(); store.close(); }
});

test("an explicit ongoing request is bound even when the model never calls manage_goal", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  let calls = 0;
  const runner = new TurnRunner(game, { async *generate({ context }) {
    calls++;
    assert.equal(context.planning.horizon, "ongoing");
    assert.match(context.planning.longTermObjective, /持续经营直到我叫停/);
    yield done([], "明天继续");
  } }, () => {}, "", store);
  try {
    await runner.submit(binding, "direct-goal", "持续经营直到我叫停", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(calls, 3);
    const [goal] = store.items(binding.saveId, "goals");
    assert.equal(goal.horizon, "ongoing");
    assert.equal(goal.planningIssue, "missing-next-action");
  } finally { await runner.stop(); store.close(); }
});

test("ongoing replanning uses the latest player restriction and invalidates yesterday's waiting plan", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  store.append(binding.saveId, "original:player", "player", { text: "持续经营直到我叫停，允许采木" });
  store.updateItem(binding.saveId, "goals", "farm-goal", { text: "经营农场", requestId: "original",
    horizon: "ongoing", status: "active", continuation: "wait", waitNextDay: true, updatedDay: "1:spring:1",
    dayPlan: { gameDate: "1:spring:1", nextStep: "明天查看农田", alternatives: ["今天已经没有合理工作"] } });
  game.snapshot = { companion: { locationId: "Farm" }, world: { year: 1, season: "spring", dayOfMonth: 2, timeOfDay: 600 } };
  let release, calls = 0;
  const runner = new TurnRunner(game, { async *generate({ context, messages }) {
    if (++calls === 1) {
      assert.equal(context.planning.dayPlanCurrent, false);
      await new Promise(resolve => { release = resolve; });
      yield done([action]);
    } else if (calls === 2) {
      assert.equal(context.goalInstructions.at(-1).text, "不要再采木，只照料现有作物并等待明天");
      assert.equal(messages.at(-1).text, "不要再采木，只照料现有作物并等待明天");
      yield done([{ id: "today", name: "manage_arrangements", arguments: { change: {
        set_day_plan: { focus: "按新要求只照料作物", next_step: "明天复查",
          alternatives: ["玩家暂停采木及其他扩展，当前没有待照料作物"], blocked_branches: [] },
      } } }, { id: "wait", name: "manage_arrangements", arguments: { change: {
        set_goal_continuation: { status: "wait", next_day: true, reason: "遵守玩家最新范围" },
      } } }]);
    } else yield done([], "按新要求等待明天。");
  } }, () => {}, "", store);
  try {
    await runner.submit(binding, "continue", "继续", "send");
    await waitFor(() => release);
    await runner.submit(binding, "restriction", "不要再采木，只照料现有作物并等待明天", "send");
    release();
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(game.executed.length, 0);
    assert.equal(store.items(binding.saveId, "goals")[0].dayPlan.gameDate, "1:spring:2");
  } finally { await runner.stop(); store.close(); }
});

test("takeover prevents the ongoing final-response checkpoint from calling the model again", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  let finish, calls = 0;
  const runner = new TurnRunner(game, { async *generate() {
    calls++;
    await new Promise(resolve => { finish = resolve; });
    yield done([], "明天再安排");
  } }, () => {}, "", store);
  try {
    await runner.submit(binding, "farm", "持续经营直到我叫停", "send");
    await waitFor(() => finish);
    game.state.controlOwner = "human";
    game.emit("changed");
    finish();
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(calls, 1);
    assert.equal(game.executed.length, 0);
    assert.notEqual(store.items(binding.saveId, "goals")[0].planningIssue, "missing-next-action");
  } finally { await runner.stop(); store.close(); }
});

test("a reached waiting condition cannot justify ending an ongoing turn without fresh planning", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  game.snapshot = { companion: { locationId: "Farm" }, world: { year: 1, season: "spring", dayOfMonth: 2, timeOfDay: 930 } };
  store.append(binding.saveId, "original:player", "player", { text: "持续经营直到我叫停" });
  store.updateItem(binding.saveId, "goals", "farm-goal", { text: "经营农场", requestId: "original",
    horizon: "ongoing", status: "active", continuation: "wait", wakeTime: 900, updatedDay: "1:spring:2",
    dayPlan: { gameDate: "1:spring:2", nextStep: "九点查看商店", alternatives: ["开门前暂缓购种"] } });
  let calls = 0;
  const runner = new TurnRunner(game, { async *generate() { calls++; yield done([], "继续等待九点"); } }, () => {}, "", store);
  try {
    await runner.submit(binding, "wake", "继续", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(calls, 3);
    assert.equal(store.items(binding.saveId, "goals")[0].planningIssue, "missing-next-action");
  } finally { await runner.stop(); store.close(); }
});

test("harvest stage limits the request and distinguishes multi-yield inventory gains from crop tiles", async () => {
  const game = new FakeGame();
  const tools = new CompanionTools(game, "");
  const tile = { x: 3, y: 2 };
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm" },
    farmWork: { locationId: "Farm", observationStatus: "observed", matureCropsTruncated: false,
      matureCrops: [tile, { x: 4, y: 2 }] },
    inventory: { capacity: 12, freeSlots: 3, slots: [{ itemId: "(O)388", stack: 3 }] }, farming: { location: "Farm", groundItems: [] } };
  const calls = [];
  const context = { binding, turnId: "harvest", requestId: "harvest", goalId: "harvest", signal: new AbortController().signal,
    async native(skill, parameters) {
      calls.push({ skill, parameters });
      game.snapshot = { ...game.snapshot, capturedRevision: 2,
        farmWork: { ...game.snapshot.farmWork, matureCrops: [{ x: 4, y: 2 }] },
        inventory: { ...game.snapshot.inventory, slots: [{ itemId: "(O)258", stack: 3 }, { itemId: "(O)388", stack: 3 }] } };
      return { commandId: "harvest-1", terminalState: "succeeded", finalWorldRevision: 2,
        effects: [{ state: "harvested", tile, itemId: "(O)258", stack: 1 }] };
    }, speak() {}, changed() {} };
  const invoke = (args) => tools.invoke(context, "submit_work", { action: { harvest_crops: args } });
  const result = await invoke({ location_id: "Farm", limit: 1 });
  assert.equal(result.completedCount, 1);
  assert.equal(result.stopReason, "requested-limit-reached");
  assert.deepEqual(result.inventoryGains, [{ itemId: "(O)258", count: 3 }]);
  assert.deepEqual(result.inventory.items, [{ itemId: "(O)258", count: 3 }, { itemId: "(O)388", count: 3 }]);
  assert.deepEqual(calls, [{ skill: "harvest-zone", parameters: { locationId: "Farm", tiles: [tile] } }]);
  game.snapshot.inventory.freeSlots = 0;
  const full = await invoke({ location_id: "Farm" });
  assert.equal(full.stopReason, "inventory-full");
  assert.equal(full.remainingCount, 1);
  assert.equal(calls.length, 1);
});

test("pickup stage verifies auto-collection and ignores placed objects at the next observation", async () => {
  const game = new FakeGame();
  const tools = new CompanionTools(game, "");
  const drop = { x: 8, y: 4 };
  const obstacle = { tile: { x: 9, y: 4 }, itemId: "(BC)130", isDropped: false, canBeGrabbed: false };
  let groundItems = [{ tile: drop, itemId: "(O)388", isDropped: true }, obstacle];
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm" },
    inventory: { capacity: 12, freeSlots: 3, slots: [] }, farming: { location: "Farm", groundItems } };
  const calls = [];
  const context = { binding, turnId: "pickup", requestId: "pickup", goalId: "pickup", signal: new AbortController().signal,
    async native(skill, parameters, observation) {
      if (observation) return { terminalState: "succeeded", finalWorldRevision: game.snapshot.capturedRevision,
        details: { production: { locationId: "Farm", groundItems, groundItemsTruncated: false } } };
      calls.push({ skill, parameters });
      groundItems = [obstacle];
      game.snapshot = { ...game.snapshot, capturedRevision: 2, farming: { location: "Farm", groundItems },
        inventory: { ...game.snapshot.inventory, slots: [{ itemId: "(O)388", stack: 3 }] } };
      return { commandId: "pickup-1", terminalState: "rejected", finalWorldRevision: 2, error: { code: "no-item" } };
    }, speak() {}, changed() {} };
  const invoke = () => tools.invoke(context, "submit_work", { action: { collect_ground_items: { location_id: "Farm" } } });
  const result = await invoke();
  assert.deepEqual(result.collectedItems, [{ itemId: "(O)388", count: 3 }]);
  assert.equal(result.stopReason, "no-more-ground-items");
  const empty = await invoke();
  assert.equal(empty.stopReason, "no-observed-targets");
  assert.deepEqual(empty.inventory.items, [{ itemId: "(O)388", count: 3 }]);
  assert.deepEqual(calls, [{ skill: "pickup-items", parameters: { locationId: "Farm", tiles: [drop] } }]);
});

test("inventory stage verifies both chest and backpack changes and ships only counts above the reserve", async () => {
  const game = new FakeGame();
  const tools = new CompanionTools(game, "");
  const chest = { x: 5, y: 4 };
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm" },
    inventory: { slots: [{ itemId: "(O)24", stack: 8 }, { itemId: "(O)388", stack: 3 }] },
    chests: { items: [{ tile: chest, contentsObserved: true, contents: [] }] } };
  const calls = [];
  const context = { binding, turnId: "transfer", requestId: "transfer", goalId: "transfer", signal: new AbortController().signal,
    async native(skill, parameters) {
      calls.push({ skill, parameters });
      const [{ itemId, count }] = parameters.items;
      const slots = game.snapshot.inventory.slots.map(slot => slot.itemId === itemId ? { ...slot, stack: slot.stack - count } : slot);
      game.snapshot = { ...game.snapshot, capturedRevision: game.snapshot.capturedRevision + 1, inventory: { slots },
        chests: skill === "deposit-chest" ? { items: [{ tile: chest, contentsObserved: true, contents: [{ itemId, stack: count }] }] }
          : game.snapshot.chests };
      return { commandId: skill, terminalState: "succeeded", finalWorldRevision: game.snapshot.capturedRevision,
        effects: [skill === "deposit-chest" ? { state: "deposited", itemId, stack: count } : { state: "shipped", itemId, count }] };
    }, speak() {}, changed() {} };
  const invoke = action => tools.invoke(context, "submit_work", { action });
  const store = await invoke({ store_items: { location_id: "Farm", chest_tile: chest, items: [{ item_id: "(O)388", count: 3 }] } });
  assert.equal(store.status, "completed");
  assert.equal(store.completedCount, 3);
  const ship = await invoke({ ship_inventory: { location_id: "Farm", items: [{ item_id: "(O)24", count: 8 }],
    keep: [{ item_id: "(O)24", count: 2 }] } });
  assert.equal(ship.completedCount, 6);
  assert.equal(ship.remainingCount, 2);
  assert.equal(ship.stopReason, "requested-quantity-unavailable");
  assert.equal(game.snapshot.inventory.slots[0].stack, 2);
  assert.deepEqual(calls.map(call => call.skill), ["deposit-chest", "ship-items"]);
});

test("inventory stage stops when the native receipt and inventory disagree", async () => {
  const game = new FakeGame();
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm" },
    inventory: { slots: [{ itemId: "(O)24", stack: 5 }] } };
  let calls = 0;
  const tools = new CompanionTools(game, "");
  const result = await tools.invoke({ binding, turnId: "mismatch", requestId: "mismatch", goalId: "mismatch",
    signal: new AbortController().signal, speak() {}, changed() {},
    async native() { calls++; return { commandId: "ship", terminalState: "succeeded", finalWorldRevision: 1,
      effects: [{ state: "shipped", itemId: "(O)24", count: 2 }] }; },
  }, "submit_work", { action: { ship_inventory: { location_id: "Farm", items: [{ item_id: "(O)24", count: 2 }] } } });
  assert.equal(result.stopReason, "transfer-observation-mismatch");
  assert.equal(result.completedCount, 0);
  assert.equal(result.remainingCount, 2);
  assert.equal(calls, 1);
});

test("harvest stage retains confirmed tiles when a later observation fails", async () => {
  const game = new FakeGame();
  const tile = { x: 2, y: 3 };
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm" },
    farmWork: { locationId: "Farm", observationStatus: "observed", matureCrops: [tile], matureCropsTruncated: false },
    inventory: { capacity: 12, freeSlots: 2, slots: [] } };
  let dispatched = 0;
  game.observe = async () => { if (dispatched) throw new Error("Disconnected"); return game.snapshot; };
  const result = await new CompanionTools(game, "").invoke({ binding, turnId: "harvest", requestId: "harvest",
    goalId: "harvest", signal: new AbortController().signal, speak() {}, changed() {},
    async native() { dispatched++; return { commandId: "harvest", terminalState: "succeeded", finalWorldRevision: 2,
      effects: [{ state: "harvested", tile }] }; },
  }, "submit_work", { action: { harvest_crops: { location_id: "Farm" } } });
  assert.equal(result.completedCount, 1);
  assert.equal(result.remainingCount, null);
  assert.equal(result.stopReason, "harvest-observation-unavailable");
  assert.equal(dispatched, 1);
});

test("a batch summary persists with the goal and farm-change waits ignore clock and pose updates", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm" },
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: [],
      matureCrops: [], cropUnwateredTruncated: false, matureCropsTruncated: false },
    world: { year: 1, season: "spring", dayOfMonth: 1, timeOfDay: 900 } };
  let calls = 0;
  const arrange = change => ({ id: "arrange-" + calls, name: "manage_arrangements", arguments: { change } });
  const runner = new TurnRunner(game, { async *generate() {
    calls++;
    if (calls === 1) yield done([
      arrange({ manage_goal: { action: "add", text: "持续照料农田" } }),
      { id: "care", name: "submit_work", arguments: { action: { tend_crops: { location_id: "Farm" } } } },
      arrange({ set_day_plan: { focus: "每日农田照料", next_step: "等待农田变化或次日",
        alternatives: ["玩家仅授权照料农田，当前无其他农田工作"], blocked_branches: [] } }),
      arrange({ set_goal_continuation: { status: "wait", reason: "当天暂无缺水作物，次日或农田变化后复查",
        next_day: true, when_farm_changes: true } }),
    ]);
    else if (calls === 2) yield done();
    else if (calls === 3) yield done([
      arrange({ set_day_plan: { focus: "已经根据新的农田观察重新安排", next_step: "等待下一天",
        alternatives: ["本轮仅验证接续；玩家保留具体农田执行"], blocked_branches: [] } }),
      arrange({ set_goal_continuation: { status: "wait", reason: "已经根据新的农田观察重新安排", next_day: true } }),
    ]);
    else yield done();
  } }, () => {}, "", store);
  try {
    game.emit("changed");
    await runner.submit(binding, "farm-care", "每天照料农田", "send");
    await waitFor(() => !runner.status().modelBusy);
    const [goal] = store.items(binding.saveId, "goals");
    assert.equal(goal.lastStage.stopReason, "no-observed-targets");
    assert.equal(goal.lastStage.requestId, "farm-care");
    assert.equal(game.executed.length, 0);
    game.snapshot = { ...game.snapshot, capturedRevision: 9, companion: { locationId: "Farm", tileX: 7 },
      world: { ...game.snapshot.world, timeOfDay: 1400 } };
    game.emit("changed");
    await setTimeout(850);
    assert.equal(calls, 2);
    game.snapshot.farmWork = { ...game.snapshot.farmWork, cropUnwateredTiles: [{ x: 3, y: 4 }] };
    game.emit("changed");
    await waitFor(() => store.items(binding.saveId, "goals")[0].summary === "已经根据新的农田观察重新安排", 300);
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(calls, 4);
  } finally { await runner.stop(); store.close(); }
});

test("several harvest batches need one model dispatch and one compact summary without a receipt wake loop", async () => {
  const game = new FakeGame();
  const store = new CompanionStore(":memory:");
  const crops = Array.from({ length: 65 }, (_, x) => ({ x, y: 3 }));
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm" },
    farmWork: { locationId: "Farm", observationStatus: "observed", matureCrops: crops, matureCropsTruncated: false },
    farming: { location: "Farm", groundItems: [], groundItemsTruncated: false },
    inventory: { capacity: 12, freeSlots: 3, slots: [] },
    world: { year: 1, season: "spring", dayOfMonth: 1, timeOfDay: 900 } };
  const batches = [];
  game.nativeResult = (commandId, skill, parameters) => {
    assert.equal(skill, "harvest-zone");
    batches.push(parameters.tiles);
    const count = crops.length - game.snapshot.farmWork.matureCrops.length + parameters.tiles.length;
    game.snapshot = { ...game.snapshot, capturedRevision: game.snapshot.capturedRevision + 1,
      farmWork: { ...game.snapshot.farmWork, matureCrops: game.snapshot.farmWork.matureCrops.slice(parameters.tiles.length) },
      inventory: { ...game.snapshot.inventory, slots: [{ itemId: "(O)24", stack: count }] } };
    game.emit("changed");
    return { commandId, terminalState: "succeeded", finalWorldRevision: game.snapshot.capturedRevision,
      effects: parameters.tiles.map(tile => ({ tile, state: "harvested" })) };
  };
  let calls = 0, summary;
  const metrics = [];
  const runner = new TurnRunner(game, { async *generate({ messages }) {
    calls++;
    if (calls === 1) yield done([
      { id: "goal", name: "manage_arrangements", arguments: { change: { manage_goal: { action: "add", text: "收获农田" } } } },
      { id: "harvest", name: "submit_work", arguments: { action: { harvest_crops: { location_id: "Farm" } } } },
    ]);
    else { summary = messages.at(-1).result; yield done(); }
  } }, () => {}, "", store, "", undefined, (event, details) => metrics.push({ event, details }));
  try {
    game.emit("changed");
    await runner.submit(binding, "large-harvest", "收一下成熟作物", "send");
    await waitFor(() => !runner.status().modelBusy);
    await setTimeout(850);
    assert.equal(calls, 2);
    assert.deepEqual(batches.map(batch => batch.length), [24, 24, 17]);
    assert.equal(summary.completedCount, 65);
    assert.equal(summary.remainingCount, 0);
    assert.deepEqual(summary.inventoryGains, [{ itemId: "(O)24", count: 65 }]);
    assert.ok(JSON.stringify(summary).length < 1500);
    assert.equal(store.recentReceipts(binding.saveId).length, 3);
    assert.equal(store.items(binding.saveId, "goals")[0].lastStage.completedCount, 65);
    const stage = metrics.find(row => row.event === "work_stage_completed").details;
    assert.equal(stage.nativeActions, 3);
    assert.equal(stage.targetCount, 65);
    assert.equal(stage.modelRequestsDuringStage, 0);
    assert.equal(stage.requestId, "large-harvest");
    const turn = metrics.find(row => row.event === "turn_completed").details;
    assert.equal(turn.modelRequests, 2);
    assert.equal(turn.stages, 1);
    assert.ok(!JSON.stringify(metrics).includes("收一下成熟作物"));
  } finally { await runner.stop(); store.close(); }
});

test("shared C# farm inspection contract drives only the observed crop inside the requested region", async () => {
  const sample = JSON.parse(readFileSync(new URL("../../../contracts/game-bridge/examples/batched-farm-work.json", import.meta.url)));
  const game = new FakeGame();
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm", hasWateringCan: true, waterCanLevel: 2 },
    farmWork: { locationId: "Farm", observationStatus: "unavailable" } };
  const calls = [];
  const context = { binding, turnId: "contract", requestId: "contract", goalId: "contract",
    signal: new AbortController().signal, speak() {}, changed() {},
    async native(skill, parameters, observation) {
      calls.push({ skill, parameters, observation });
      if (observation) return { terminalState: "succeeded", finalWorldRevision: 1, details: { farmWork: sample.farmWork } };
      game.snapshot = { ...game.snapshot, farmWork: { ...sample.farmWork, cropUnwateredTiles: [], cropUnwateredCount: 0 } };
      return { commandId: "water-contract", terminalState: "succeeded", finalWorldRevision: 1,
        effects: [{ tile: { x: 2, y: 3 }, state: "watered" }] };
    } };
  const result = await new CompanionTools(game, "").invoke(context, "submit_work", { action: {
    tend_crops: { location_id: sample.parameters.locationId, region: sample.parameters.region } } });
  assert.equal(result.completedCount, 1);
  assert.deepEqual(calls.map(row => row.skill), ["inspect-farm-work", "water-zone"]);
  assert.deepEqual(calls[1].parameters.tiles, sample.farmWork.cropUnwateredTiles);
});

test("crop-care cannot dispatch its refill after an unconfirmed watering action", async () => {
  const game = new FakeGame();
  game.snapshot = { capturedRevision: 1, companion: { locationId: "Farm", waterCanLevel: 1, hasWateringCan: true },
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTruncated: false,
      cropUnwateredTiles: [{ x: 2, y: 3 }, { x: 3, y: 3 }] } };
  game.nativeResult = () => { throw new Error("No receipt"); };
  let calls = 0;
  const { runner } = fixture(game, { async *generate() {
    calls++;
    yield done([{ id: "care", name: "submit_work", arguments: { action: { tend_crops: { location_id: "Farm" } } } }]);
  } });
  try {
    await runner.submit(binding, "uncertain-care", "浇水，缺水就补水", "send");
    await waitFor(() => !runner.status().modelBusy);
    assert.equal(runner.status().recoveryPending, true);
    assert.equal(game.queryCount, 1);
    assert.equal(game.executed.length, 1);
    assert.equal(calls, 1);
  } finally { await runner.stop(); }
});

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

test("a pause during native work blocks the next action proposed by the same model response", async () => {
  const game = new FakeGame();
  let finish;
  const skills = [];
  game.nativeResult = async (commandId, skillId) => {
    skills.push(skillId);
    await new Promise(resolve => { finish = resolve; });
    return { commandId, skillId, terminalState: "succeeded" };
  };
  const { runner, events } = fixture(game, { async *generate({ messages }) {
    yield messages.at(-1).role === "user"
      ? done([action, { ...action, id: "second-action" }]) : done();
  } });
  try {
    await runner.submit(binding, "pause-at-boundary", "走到出口再继续", "send");
    await waitFor(() => finish);
    game.state.paused = true;
    game.emit("changed");
    finish();
    await waitFor(() => events.some(event => event.type === "life.chat.reply"));
    assert.deepEqual(skills, ["navigate-to"]);
    assert.equal(runner.status().agentPhase, "paused");
  } finally { await runner.stop(); }
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
  const { runner, events } = fixture(game, { async *generate() { yield done([action, { ...action, id: "next-action" }]); } });
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
  const runner = new TurnRunner(game, { async *generate({ context, messages }) {
    observed.push({ context, message: messages.at(-1) });
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
    assert.equal(observed[0].context.originalRequest, "先打理一下农场");
  } finally { await runner.stop(); store.close(); }
});

test("autonomous wake ignores goal-summary edits and continues other work after an unreachable target", async () => {
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  game.snapshot = { companion: { locationId: "Farm", tileX: 10, tileY: 10 },
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: [{ x: 10, y: 11 }] },
    farming: { location: "Farm", groundItems: [{ tile: { x: 35, y: 57 }, isDropped: true }] },
    world: { year: 1, season: "spring", dayOfMonth: 1, timeOfDay: 900 } };
  const save = binding.saveId;
  store.append(save, "farm-request:player", "player", { text: "收集农场掉落物" });
  store.updateItem(save, "goals", "farm-goal", { text: "收集农场掉落物", requestId: "farm-request",
    status: "active", continuation: "continue", summary: "准备拾取" });
  const pickup = { id: "pickup", name: "submit_work", arguments: { action: {
    pickup_items: { tiles: [{ x: 35, y: 57 }] },
  } } };
  const water = { id: "water", name: "submit_work", arguments: { action: {
    water_tiles: { tiles: [{ x: 10, y: 11 }] },
  } } };
  const modelTurns = [];
  let pickupCalls = 0;
  let waterCalls = 0;
  let retryBlocked = false;
  let blockedReplies = 0;
  game.nativeResult = (commandId, skillId) => {
    if (skillId === "pickup-items") {
      pickupCalls++;
      return { commandId, skillId, terminalState: "rejected", completedCount: 0,
        error: { code: "unreachable", message: "unreachable" } };
    }
    assert.equal(skillId, "water-zone");
    waterCalls++;
    return { commandId, skillId, terminalState: "succeeded", completedCount: 1 };
  };
  const runner = new TurnRunner(game, { async *generate({ messages }) {
    modelTurns.push(messages);
    const last = messages.at(-1);
    if (last.role === "user") yield done([pickup]);
    else if (last.name === "submit_work" && last.failed) {
      retryBlocked = String(last.result.error).includes("避免重复派发");
      blockedReplies++;
      yield done();
    } else if (last.callId === "pickup") {
      store.updateItem(save, "goals", "farm-goal", { summary: "掉落物仍不可达，改做其他可达工作" });
      yield done([pickup, water]);
    } else yield done();
  } }, () => undefined, "", store);
  try {
    game.emit("changed");
    await waitFor(() => modelTurns.length === 5 && !runner.status().modelBusy, 300);
    await setTimeout(850);
    assert.equal(pickupCalls, 1, "The same rejected target must not be sent to the game twice without new observations.");
    assert.equal(modelTurns.length, 5, "Only the actual successful action may trigger the next autonomous turn.");
    assert.equal(blockedReplies, 1, "The model must be told why the repeated target is deferred.");
    assert.equal(retryBlocked, true, "The model must receive the reason the repeated dispatch was blocked.");
    assert.equal(waterCalls, 1, "Other useful work must execute once without causing a retry loop.");
    assert.equal(store.items(save, "goals")[0].summary, "掉落物仍不可达，改做其他可达工作");
    game.snapshot.companion.tileX = 11;
    await runner.submit(binding, "player-retry", "角色移动后重新检查掉落物", "send");
    await waitFor(() => modelTurns.length >= 7 && !runner.status().modelBusy);
    assert.equal(pickupCalls, 2, "A changed actor position must permit a fresh attempt.");
  } finally { await runner.stop(); store.close(); }
});

test("a rate-limited autonomous turn retries the model without repeating a settled action", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  store.append(binding.saveId, "farm-request:player", "player", { text: "打理农场" });
  store.updateItem(binding.saveId, "goals", "farm-goal", { text: "打理农场", requestId: "farm-request",
    status: "active", continuation: "continue" });
  game.nativeResult = (commandId, skillId) => ({ commandId, skillId, terminalState: "succeeded" });
  const requests = [];
  const events = [];
  const runner = new TurnRunner(game, { async *generate({ messages }) {
    requests.push(structuredClone(messages));
    if (requests.length === 1) yield done([action]);
    else if (requests.length === 2) yield { type: "error", code: "rate-limited", message: "频率受限。" };
    else yield { ...done(), usage: { input: 42, output: 7, cacheRead: 3 } };
  } }, (type, payload) => events.push({ type, payload }), "", store);
  try {
    runner.configure(runner.model, { provider: "custom", model: "farm-model" });
    game.emit("changed");
    t.mock.timers.tick(750);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(runner.status().agentPhase, "model-retry");
    assert.deepEqual(runner.status().modelIssue, { code: "rate-limited", message: "频率受限。", attempt: 1, maxAttempts: 3 });
    assert.equal(runner.status().thinking, false);
    game.state.paused = true;
    game.emit("changed");
    t.mock.timers.tick(5000);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(requests.length, 2);
    game.state.paused = false;
    game.emit("changed");
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(requests.length, 3);
    assert.deepEqual(requests[2], requests[1]);
    assert.equal(game.executed.length, 1);
    assert.equal(runner.status().modelIssue, undefined);
    assert.ok(events.some(event => event.type === "life.chat.reply" && event.payload.status === "completed"));
    assert.deepEqual(store.usage(binding.saveId).recentTasks, [{ title: "打理农场", provider: "custom", model: "farm-model",
      text: "3 次请求 · 输入 42 · 输出 7 · 缓存读取 3 · 1 次重试 · 2 次未报告用量" }]);
  } finally { await runner.stop(); store.close(); }
});

test("network retries stop after three attempts and persistent model errors block automatic requests", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const store = new CompanionStore(":memory:");
  const game = new FakeGame();
  store.updateItem(binding.saveId, "goals", "farm-goal", { text: "打理农场", status: "active", continuation: "continue" });
  let code = "network";
  let calls = 0;
  const events = [];
  const runner = new TurnRunner(game, { async *generate() {
    calls++;
    yield { type: "error", code, message: "模型服务请求失败。" };
  } }, (type, payload) => events.push({ type, payload }), "", store);
  try {
    game.emit("changed");
    for (const elapsed of [750, 2000, 5000, 10_000]) {
      t.mock.timers.tick(elapsed);
      await new Promise(resolve => setImmediate(resolve));
    }
    assert.equal(calls, 4);
    assert.equal(runner.status().agentPhase, "model-error");
    assert.match(runner.status().modelIssue.message, /自动重试未成功/);
    game.snapshot.world = { year: 1, season: "spring", dayOfMonth: 2, timeOfDay: 900 };
    game.emit("changed");
    t.mock.timers.tick(30_000);
    assert.equal(calls, 4);
    assert.equal(store.items(binding.saveId, "goals")[0].status, "active");
    code = "quota-exhausted";
    await runner.submit(binding, "manual-retry", "继续当前工作", "send");
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(calls, 5);
    assert.equal(runner.status().agentPhase, "model-error");
    assert.equal(runner.status().modelIssue.code, "quota-exhausted");
    assert.ok(events.some(event => event.type === "life.chat.reply" && event.payload.errorCode === "quota-exhausted"));
  } finally { await runner.stop(); store.close(); }
});

test("taking over during model backoff cancels the pending retry", async t => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const game = new FakeGame();
  let calls = 0;
  const { runner } = fixture(game, { async *generate() {
    calls++;
    yield { type: "error", code: "network", message: "连接中断。" };
  } });
  try {
    await runner.submit(binding, "request", "照料作物", "send");
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(runner.status().agentPhase, "model-retry");
    await runner.control(binding, "takeover");
    game.state.controlOwner = "human";
    game.emit("changed");
    t.mock.timers.tick(30_000);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(calls, 1);
    assert.equal(runner.status().agentPhase, "player-control");
    assert.equal(runner.status().modelIssue, undefined);
  } finally { await runner.stop(); }
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
    assert.equal(observations[0].context.observations.world.dayOfMonth, 4);
    assert.equal(observations[0].context.observations.world.isRaining, true);
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
    runner.configure({ async *generate({ context, messages }) {
      assert.equal(context.profile.profile.companionName, "阿栗");
      assert.equal(context.memory[0].text, "保留幼树");
      assert.equal(context.originalRequest, "安排今日农活");
      assert.equal(messages[0].text, "安排今日农活");
      yield done([], "新模型继续。");
    } }, { provider: "openai", model: "gpt-5-mini" });
    await runner.submit(binding, "second", "继续看看", "send");
    await waitFor(() => events.some(e => e.type === "life.chat.reply" && e.payload.requestId === "second"));
    assert.equal(events.find(e => e.type === "life.chat.reply" && e.payload.requestId === "second").payload.replyText, "新模型继续。");
  } finally { await runner.stop(); store.close(); }
});
