import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { PhaseObserver } from "../dist/application/phase-observation.js";
import { CompanionStore } from "../dist/adapters/storage/companion-store.js";
import { CompanionTools } from "../dist/tools/companion-tools.js";

const sample = JSON.parse(readFileSync(new URL("../../../contracts/game-bridge/examples/phase-map-page.json", import.meta.url), "utf8"));
const binding = { saveId: "save", actorMode: "companion", gameSessionId: "game", goalId: "goal", requestId: "request",
  instructionVersion: "v1", gameDate: "1:spring:5", locationId: "Farm" };
const game = { async observe() { return { capturedRevision: 7, companion: { locationId: "Farm" }, chests: { items: [] } }; } };

test("full map pagination discovers remote targets, validates cache headers and rebuilds stale persisted maps", async () => {
  const store = new CompanionStore(":memory:");
  let fingerprint = sample.fingerprint, scans = 0;
  const native = async (_skill, { scanOffset: offset, scanLimit: limit }) => {
    if (_skill === "inspect-production") return { terminalState: "succeeded", details: { production: {
      locationId: "Farm", groundItemsTruncated: false, groundItems: [{ itemId: "(O)388", stack: 3, isDropped: true, name: "木材" }] } } };
    if (limit) scans++;
    const end = Math.min(1200, offset + limit);
    const page = offset === 1024 ? { ...sample, fingerprint } : { ...sample, fingerprint, offset, end,
      nextOffset: end < 1200 ? end : -1, complete: end === 1200, trees: [], waterTiles: [], fixtures: [], scannedTiles: end - offset };
    return { terminalState: "succeeded", details: { page } };
  };
  try {
    const observer = new PhaseObserver(game, store);
    const first = await observer.prepare(binding, native, () => {});
    assert.equal(first.facts.trees[0].tile.x, 599);
    assert.deepEqual(first.facts.summary.existingGround, [{ itemId: "(O)388", count: 3, displayName: "木材" }]);
    assert.equal(scans, 3);
    await new PhaseObserver(game, store).prepare(binding, native, () => {});
    assert.equal(scans, 3);
    fingerprint = "reloaded-map";
    await observer.prepare(binding, native, () => {});
    assert.equal(scans, 6);
    await observer.prepare({ ...binding, saveId: "another-save" }, native, () => {});
    assert.equal(scans, 9);
  } finally { store.close(); }
});

test("failed, changing or contradictory map pages never become a complete empty observation", async () => {
  const observer = new PhaseObserver(game);
  await assert.rejects(observer.prepare(binding, async () => ({ terminalState: "failed" }), () => {}), /不可用/);
  await assert.rejects(observer.prepare(binding, async () => ({ terminalState: "succeeded", details: { page: {
    ...sample, offset: 0, end: 0, nextOffset: 0, complete: false, trees: [], waterTiles: [], fixtures: [],
  } } }), () => {}), /未推进/);
  let calls = 0;
  await assert.rejects(observer.prepare(binding, async () => ({ terminalState: "succeeded", details: { page: {
    ...sample, offset: 0, end: 0, nextOffset: 0, complete: false, fingerprint: `revision-${calls++}`,
  } } }), () => {}), /变化/);
});

test("crop care reuses water cache, rescans a failed shore and refills without a model round", async () => {
  let fingerprint = "shore-one", scans = 0, dry = true, water = 0, refillCalls = 0;
  const snapshot = { capturedRevision: 1, companion: { locationId: "Farm", tileX: 1, tileY: 1, waterCanLevel: 0, hasWateringCan: true },
    farmWork: { locationId: "Farm", observationStatus: "observed", cropUnwateredTiles: [{ x: 1, y: 1 }], cropUnwateredTruncated: false } };
  const nativeGame = { supportsPhaseMap: true, actorMode: "companion", snapshot, async observe() {
    snapshot.companion.waterCanLevel = water;
    snapshot.farmWork.cropUnwateredTiles = dry ? [{ x: 1, y: 1 }] : [];
    return snapshot;
  } };
  const tools = new CompanionTools(nativeGame, "");
  let failed = false;
  const context = { binding, turnId: "care", goalId: "care", requestId: "care", signal: new AbortController().signal,
    speak() {}, changed() {}, async native(skill, params) {
      if (skill === "inspect-phase-map") {
        if (params.scanLimit) scans++;
        return { terminalState: "succeeded", details: { page: { locationId: "Farm", width: 3, height: 2,
          fingerprint, offset: 0, end: params.scanLimit ? 6 : 0, nextOffset: params.scanLimit ? -1 : 0,
          complete: !!params.scanLimit, trees: [], fixtures: [], waterTiles: params.scanLimit ? [{ x: fingerprint === "shore-one" ? 2 : 0, y: 1 }] : [] } } };
      }
      if (skill === "refill-watering-can") {
        refillCalls++;
        if (failed) {
          failed = false; fingerprint = "shore-two";
          return { terminalState: "failed", error: { code: "unreachable" } };
        }
        water = 40;
        return { terminalState: "succeeded", completedCount: 1, finalWorldRevision: snapshot.capturedRevision };
      }
      assert.equal(skill, "water-zone");
      dry = false; water--; snapshot.capturedRevision++;
      return { terminalState: "succeeded", commandId: `water-${snapshot.capturedRevision}`, completedCount: 1,
        finalWorldRevision: snapshot.capturedRevision, effects: [{ state: "watered", tile: { x: 1, y: 1 } }] };
    } };
  const care = () => tools.invoke(context, "submit_work", { action: { tend_crops: { location_id: "Farm" } } });
  await nativeGame.observe();
  assert.equal((await care()).completedCount, 1);
  assert.equal(scans, 1);
  dry = true; water = 0; await nativeGame.observe();
  await care();
  assert.equal(scans, 1);
  dry = true; water = 0; failed = true; await nativeGame.observe();
  assert.equal((await care()).completedCount, 1);
  assert.equal(scans, 2);
  assert.equal(refillCalls, 4);
});
