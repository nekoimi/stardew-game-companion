import assert from "node:assert/strict";
import { test } from "node:test";
import { PhaseExecutor } from "../dist/application/phase-executor.js";

function fixture({ updateAt = "", unknown = false, autoPickup = false, latePickup = false } = {}) {
  const binding = { saveId: "farm", gameSessionId: "game", actorMode: "companion", goalId: "goal", requestId: "original",
    instructionVersion: "original", gameDate: "1:spring:5", locationId: "Farm" };
  const trees = [2, 5, 8].map(x => ({ tile: { x, y: 2 }, kind: "tree", growthStage: 5, tapped: false }));
  let wood = 10, stored = 0, drops = [], blocked = "", count = 0;
  const actions = [];
  const observation = { ref: "map", binding: { ...binding }, complete: true, revision: 1, scopeRef: "current-map",
    destinations: { box: { tile: { x: 1, y: 1 }, contents: [] } }, facts: { trees, map: { width: 10, fingerprint: "v1" } } };
  const input = { observation_ref: "map", objective: "新增20木材并入库", scope_ref: "current-map",
    completion: { metric: "new-wood-stored", quantity: 20 }, constraints: { stamina_floor: 80, destination_ref: "box", keep: [] },
    steps: [{ id: "chop", operation: "chop-trees", after: [], inputs: [] },
      { id: "collect", operation: "collect-products", after: ["chop"], inputs: ["chop"] },
      { id: "store", operation: "store-products", after: ["collect"], inputs: ["collect"] }],
    branches: [{ on: "target-unreachable", action: "skip-target" }, { on: "inventory-full", action: "store-and-resume" }] };
  const snapshot = () => ({ capturedRevision: count + 1, companion: { locationId: "Farm", tileX: 0, tileY: 0, stamina: 270 - count * 30 },
    inventory: { freeSlots: 8, slots: [{ itemId: "(O)388", stack: wood }] } });
  const env = { observation, current: () => binding, block: () => blocked, observe: async () => snapshot(), progress() {},
    async workflow(op, args) {
      assert.equal(op, "store_items");
      const quantity = args.items.reduce((sum, item) => sum + item.count, 0);
      assert.ok(quantity <= wood - 10, "Original materials were selected without authorization");
      wood -= quantity; stored += quantity; actions.push("deposit-chest");
      if (latePickup) { wood += 4; latePickup = false; }
      return { status: "completed", receiptIds: ["store"], transferredItems: [{ itemId: "(O)388", count: quantity }] };
    },
    async native(skill, args, read) {
      if (skill === "inspect-route") return { terminalState: "succeeded", details: { route: { reachable: true, walkingTiles: args.tile.x } } };
      if (skill === "inspect-phase-map") return { terminalState: "succeeded", details: { page: {
        fingerprint: "v1", trees: trees.filter(tree => tree.tile.x + 20 === args.scanOffset) } } };
      if (skill === "inspect-production") return { terminalState: "succeeded", details: { production: {
        locationId: "Farm", groundItems: structuredClone(drops), groundItemsTruncated: false } } };
      assert.equal(read, false);
      actions.push(skill);
      if (skill === "chop-tree") {
        count++;
        if (autoPickup) wood += 12;
        else drops.push({ tile: args.tiles[0], itemId: "(O)388", stack: 12, isDropped: true });
        if (updateAt === skill) blocked = "player-update";
        if (unknown) return {};
        return { terminalState: "succeeded", completedCount: 1, commandId: `chop-${count}` };
      }
      assert.equal(skill, "pickup-items");
      wood += drops.reduce((sum, item) => sum + item.stack, 0); drops = [];
      return { terminalState: "rejected", commandId: `pickup-${count}`, error: { code: "no-item" } };
    } };
  return { env, input, actions, stored: () => stored, wood: () => wood };
}

test("one phase collects actual scattered yields before selecting more trees and keeps original materials", async () => {
  const f = fixture();
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.deepEqual(f.actions, ["chop-tree", "pickup-items", "chop-tree", "pickup-items", "deposit-chest"]);
  assert.equal(f.stored(), 24);
  assert.equal(f.wood(), 10);
  assert.equal(result.executionModelRequests, 0);
});

test("automatic pickup counts toward the phase and needs no stale explicit pickup", async () => {
  const f = fixture({ autoPickup: true });
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.deepEqual(f.actions, ["chop-tree", "chop-tree", "deposit-chest"]);
});

test("materials absorbed while walking to storage are reconciled and transferred before declaring completion", async () => {
  const f = fixture({ latePickup: true });
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(f.stored(), 28);
  assert.equal(f.wood(), 10);
  assert.equal(f.actions.filter(action => action === "deposit-chest").length, 2);
});

test("new requirements and an unknown native result prevent all following phase work", async () => {
  for (const option of [{ updateAt: "chop-tree" }, { unknown: true }]) {
    const f = fixture(option);
    const result = await new PhaseExecutor().execute(f.input, f.env);
    assert.deepEqual(f.actions, ["chop-tree"]);
    assert.notEqual(result.status, "completed");
    assert.equal(f.stored(), 0);
    if (option.unknown) assert.equal(result.needsReconciliation, true);
  }
});
