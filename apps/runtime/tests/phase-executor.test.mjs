import assert from "node:assert/strict";
import { test } from "node:test";
import { PhaseExecutor } from "../dist/application/phase-executor.js";

function fixture({ updateAt = "", unknown = false, autoPickup = false, latePickup = false, smallPack = false, externalPickup = false, pickupBeforeSnapshot = false, splitPickup = false, fixedFreeSlots } = {}) {
  const binding = { saveId: "farm", gameSessionId: "game", actorMode: "companion", goalId: "goal", requestId: "original",
    instructionVersion: "original", gameDate: "1:spring:5", locationId: "Farm" };
  const trees = [2, 5, 8].map(x => ({ tile: { x, y: 2 }, kind: "tree", growthStage: 5, tapped: false }));
  let wood = 10, stored = 0, drops = [], blocked = "", count = 0, productQueries = 0;
  const actions = [];
  const observation = { ref: "map", binding: { ...binding }, complete: true, revision: 1, scopeRef: "current-map",
    destinations: { box: { tile: { x: 1, y: 1 }, contents: [] } }, facts: { trees, map: { width: 10, fingerprint: "v1" } } };
  const input = { observation_ref: "map", objective: "新增20木材并入库", scope_ref: "current-map",
    completion: { metric: "new-wood-stored", quantity: 20 }, constraints: { stamina_floor: 80, destination_ref: "box", keep: [], include_existing: [] },
    steps: [{ id: "chop", operation: "chop-trees", after: [], inputs: [] },
      { id: "collect", operation: "collect-products", after: ["chop"], inputs: ["chop"] },
      { id: "store", operation: "store-products", after: ["collect"], inputs: ["collect"] }],
    branches: [{ on: "target-unreachable", action: "skip-target" }, { on: "inventory-full", action: "store-and-resume" }] };
  const snapshot = () => ({ capturedRevision: count + 1, companion: { locationId: "Farm", tileX: 0, tileY: 0, stamina: 270 - count * 30 },
    inventory: { freeSlots: fixedFreeSlots ?? (smallPack && wood >= 34 ? 1 : 8), slots: [{ itemId: "(O)388", stack: wood }] } });
  const env = { observation, current: () => binding, block: () => blocked, observe: async () => snapshot(), progress() {},
    async workflow(op, args) {
      assert.ok(["store_items", "ship_inventory"].includes(op));
      const quantity = args.items.reduce((sum, item) => sum + item.count, 0);
      assert.ok(quantity <= wood - 10, "Original materials were selected without authorization");
      wood -= quantity; stored += quantity; actions.push(op === "store_items" ? "deposit-chest" : "ship-items");
      if (latePickup) { wood += 4; latePickup = false; }
      return { status: "completed", receiptIds: ["store"], transferredItems: [{ itemId: "(O)388", count: quantity }] };
    },
    async native(skill, args, read) {
      if (skill === "inspect-route") return { terminalState: "succeeded", details: { route: { reachable: true, walkingTiles: args.tile.x } } };
      if (skill === "inspect-phase-map") return { terminalState: "succeeded", details: { page: {
        fingerprint: "v1", trees: trees.filter(tree => tree.tile.x + 20 === args.scanOffset) } } };
      if (skill === "inspect-production") {
        const observed = structuredClone(drops);
        if (pickupBeforeSnapshot && drops.length && ++productQueries % 2 === 0) {
          wood += drops.reduce((sum, item) => sum + item.stack, 0); drops = [];
        }
        return { terminalState: "succeeded", details: { production: {
          locationId: "Farm", groundItems: observed, groundItemsTruncated: false } } };
      }
      assert.equal(read, false);
      actions.push(skill);
      if (skill === "chop-tree") {
        count++;
        if (autoPickup) wood += 12;
        else {
          drops.push({ tile: args.tiles[0], itemId: "(O)388", stack: 12, isDropped: true, sourceId: `tree-${count}` });
          if (splitPickup) drops.push({ tile: args.tiles[0], itemId: "(O)388", stack: 12, isDropped: true, sourceId: `tree-${count}-second` });
        }
        if (updateAt === skill) blocked = "player-update";
        if (unknown) return {};
        return { terminalState: "succeeded", completedCount: 1, commandId: `chop-${count}` };
      }
      assert.equal(skill, "pickup-items");
      const picked = splitPickup ? drops.splice(0, 1) : drops.splice(0);
      if (!externalPickup) wood += picked.reduce((sum, item) => sum + item.stack, 0);
      return { terminalState: "rejected", commandId: `pickup-${count}`, error: { code: "no-item" } };
    } };
  return { env, input, actions, stored: () => stored, wood: () => wood };
}

function backpack(f) {
  f.env.observation.destinations = {};
  f.input.completion.metric = "new-wood-collected";
  delete f.input.constraints.destination_ref;
  f.input.steps.pop();
  f.input.branches = [{ on: "target-unreachable", action: "skip-target" }];
  return f;
}

test("a no-chest phase selects multiple trees and keeps confirmed products in the backpack", async () => {
  const f = backpack(fixture());
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.deepEqual(f.actions, ["chop-tree", "pickup-items", "chop-tree", "pickup-items"]);
  assert.equal(f.wood(), 34);
  assert.deepEqual(result.storedItems, []);
  assert.deepEqual(result.shippedItems, []);
  assert.equal(result.backpackItems[0].count, 24);
});

test("a full backpack without a preplanned chest stops without selling or dropping inventory", async () => {
  const f = backpack(fixture({ fixedFreeSlots: 1 }));
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.stopReason, "inventory-full");
  assert.deepEqual(f.actions, []);
  assert.equal(f.wood(), 10);
});

test("authorized shipping is part of a no-chest phase and is accounted separately from storage", async () => {
  const f = backpack(fixture());
  f.input.constraints.ship_item_ids = ["(O)388"];
  f.input.steps.push({ id: "ship", operation: "ship-products", after: ["collect"], inputs: ["collect"] });
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(f.wood(), 10);
  assert.deepEqual(result.storedItems, []);
  assert.equal(result.shippedItems[0].count, 24);
  assert.deepEqual(result.backpackItems, []);
  assert.equal(f.actions.at(-1), "ship-items");
});

test("shipping cannot select an unlisted product or the original wood inventory", async () => {
  const f = backpack(fixture());
  f.input.constraints.ship_item_ids = ["(O)24"];
  f.input.steps.push({ id: "ship", operation: "ship-products", after: ["collect"], inputs: ["collect"] });
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(f.wood(), 34);
  assert.deepEqual(result.shippedItems, []);
  assert.ok(!f.actions.includes("ship-items"));
});

test("a combined daily phase has a finite tree step and does not consume all map candidates", async () => {
  const f = backpack(fixture());
  f.input.completion = { metric: "daily-care-completed", quantity: 1 };
  f.input.steps[0].target_quantity = 1;
  f.input.steps.unshift({ id: "care", operation: "tend-crops", after: [], inputs: [] });
  f.input.steps[1].after = ["care"];
  const workflow = f.env.workflow;
  f.env.workflow = async (op, args) => op === "tend_crops" ? { status: "completed" } : workflow(op, args);
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(f.actions.filter(skill => skill === "chop-tree").length, 1);
  assert.equal(result.backpackItems[0].count, 12);
});

test("one phase cares, harvests and ships only crops before gathering a bounded amount of wood into its backpack", async () => {
  const f = backpack(fixture());
  let crops = 0;
  const observe = f.env.observe;
  f.env.observe = async () => {
    const snapshot = await observe();
    snapshot.inventory.slots.push({ itemId: "(O)24", stack: crops, displayName: "防风草" });
    snapshot.farmWork = { locationId: "Farm", matureCrops: crops ? [] : [{ x: 1, y: 1 }] };
    return snapshot;
  };
  f.input.completion = { metric: "daily-care-shipped", quantity: 1 };
  f.input.constraints.ship_item_ids = ["(O)24"];
  f.input.steps = [
    { id: "care", operation: "tend-crops", after: [], inputs: [] },
    { id: "harvest", operation: "harvest-crops", after: ["care"], inputs: [] },
    { id: "crops", operation: "collect-products", after: ["harvest"], inputs: ["harvest"] },
    { id: "ship", operation: "ship-products", after: ["crops"], inputs: ["crops"] },
    { id: "wood", operation: "chop-trees", after: ["ship"], inputs: [], target_quantity: 1 },
    { id: "wood-products", operation: "collect-products", after: ["wood"], inputs: ["wood"] }];
  f.env.workflow = async (op, args) => {
    if (op === "tend_crops") return { status: "completed" };
    if (op === "harvest_crops") { crops = 3; return { status: "completed", inventoryGains: [{ itemId: "(O)24", count: 3 }] }; }
    assert.equal(op, "ship_inventory");
    assert.deepEqual(args.items, [{ item_id: "(O)24", count: 3 }]);
    crops = 0;
    return { status: "completed", transferredItems: [{ itemId: "(O)24", count: 3 }] };
  };
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(result.steps.length, 6);
  assert.deepEqual(result.shippedItems.map(item => [item.itemId, item.count]), [["(O)24", 3]]);
  assert.deepEqual(result.backpackItems.map(item => [item.itemId, item.count]), [["(O)388", 12]]);
  assert.equal(f.wood(), 22);
});

test("unreachable optional forestry does not prevent later preplanned care or become a completed phase", async () => {
  const f = backpack(fixture());
  f.input.completion = { metric: "daily-care-completed", quantity: 1 };
  f.input.steps[0].target_quantity = 1;
  f.input.steps.push({ id: "care", operation: "tend-crops", after: ["collect"], inputs: [] });
  const native = f.env.native;
  f.env.native = (skill, args, read) => skill === "inspect-route"
    ? { terminalState: "succeeded", details: { route: { reachable: false } } } : native(skill, args, read);
  let care = 0;
  f.env.workflow = async op => { assert.equal(op, "tend_crops"); care++; return { status: "completed" }; };
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(care, 1);
  assert.equal(result.stopReason, "phase-objective-unmet");
  assert.equal(result.steps[0].status, "partial");
  assert.equal(result.steps.at(-1).status, "completed");
});

test("late falling-tree debris beyond the immediate trunk area is collected inside the same phase", async () => {
  const f = backpack(fixture());
  f.input.completion = { metric: "trees-collected", quantity: 1 };
  const native = f.env.native;
  let queriesAfterCut = 0, cut = false, late = false;
  f.env.native = async (skill, args, read) => {
    const result = await native(skill, args, read);
    if (skill === "chop-tree") cut = true;
    if (skill === "inspect-production" && cut && ++queriesAfterCut >= 3 && !late) {
      late = true;
      // The source is first observed after the immediate post-chop query.
      result.details.production.groundItems.push({ sourceId: "late-tree-product", tile: { x: 8, y: 2 },
        itemId: "(O)92", stack: 1, isDropped: true });
    }
    return result;
  };
  // Preserve evidence that the late target was dispatched; then simulate its
  // absorption in the authoritative snapshot rather than inventing a receipt.
  let sap = 0;
  const observe = f.env.observe;
  f.env.observe = async () => {
    const result = await observe(); result.inventory.slots.push({ itemId: "(O)92", stack: sap }); return result;
  };
  const withLate = f.env.native;
  f.env.native = async (skill, args, read) => {
    const result = await withLate(skill, args, read);
    if (skill === "pickup-items" && args.tiles[0].x === 8) sap = 1;
    return result;
  };
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(result.backpackItems.find(item => item.itemId === "(O)92").count, 1);
  assert.equal(f.actions.filter(skill => skill === "chop-tree").length, 1);
});

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

test("mid-phase storage does not reset material progress or select original stock", async () => {
  const f = fixture({ smallPack: true });
  f.input.completion.quantity = 36;
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(f.stored(), 36);
  assert.equal(f.actions.filter(action => action === "chop-tree").length, 3);
  assert.equal(f.actions.filter(action => action === "deposit-chest").length, 2);
  assert.equal(f.wood(), 10);
});

test("a drop removed by another actor cannot become a successful phase product", async () => {
  const f = fixture({ externalPickup: true });
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "stopped");
  assert.equal(result.stopReason, "product-collection-unconfirmed");
  assert.equal(f.stored(), 0);
});

test("a replaced destination is rejected before transferring any material", async () => {
  const f = fixture();
  f.env.observation.destinations.box.nativeIdentity = "old-chest";
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.stopReason, "destination-replaced");
  assert.equal(f.stored(), 0);
});

test("moving old debris does not authorize its pickup even when its item matches the new tree products", async () => {
  const f = fixture();
  const native = f.env.native;
  let treeCut = false;
  f.env.native = async (skill, args, read) => {
    if (skill === "pickup-items") assert.notEqual(args.tiles[0].x, 3, "Old moving debris was selected");
    const result = await native(skill, args, read);
    if (skill === "chop-tree") treeCut = true;
    if (skill === "inspect-production") result.details.production.groundItems.push({
      sourceId: "pre-existing-drop", tile: { x: treeCut ? 3 : 9, y: 2 }, itemId: "(O)388", stack: 30, isDropped: true });
    return result;
  };
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(f.stored(), 24);
});

test("a product tile shared with an unauthorized item stops instead of broadening collection", async () => {
  const f = fixture();
  const native = f.env.native;
  f.env.native = async (skill, args, read) => {
    const result = await native(skill, args, read);
    if (skill === "inspect-production") result.details.production.groundItems.push({
      sourceId: "foreign-drop", tile: { x: 2, y: 2 }, itemId: "(O)390", stack: 1, isDropped: true });
    return result;
  };
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.stopReason, "product-source-unconfirmed");
  assert.deepEqual(f.actions, ["chop-tree"]);
  assert.equal(f.stored(), 0);
});

test("a product absorbed before the pickup snapshot is reconciled without falsely reporting external collection", async () => {
  const f = fixture({ pickupBeforeSnapshot: true });
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(f.stored(), 24);
});

test("selecting three existing materials cannot authorize a larger pre-existing ground stack", async () => {
  const f = fixture();
  f.input.constraints.include_existing = [{ item_id: "(O)390", count: 3 }];
  const native = f.env.native;
  f.env.native = async (skill, args, read) => {
    if (skill === "pickup-items") assert.notEqual(args.tiles[0].x, 9);
    const result = await native(skill, args, read);
    if (skill === "inspect-production") result.details.production.groundItems.push({
      sourceId: "existing-stone", tile: { x: 9, y: 2 }, itemId: "(O)390", stack: 10, isDropped: true });
    return result;
  };
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "stopped");
  assert.equal(result.stopReason, "phase-objective-unmet");
  assert.ok(!result.storedItems.some(item => item.itemId === "(O)390"));
});

test("different product sources sharing a tile can be collected in sequence while inventory progresses", async () => {
  const f = fixture({ splitPickup: true });
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(f.stored(), 24);
  assert.deepEqual(f.actions, ["chop-tree", "pickup-items", "pickup-items", "deposit-chest"]);
});

test("stock-at-least uses the final observed destination rather than stock removed by another actor", async () => {
  const f = fixture();
  f.input.completion = { metric: "wood-stock-at-least", quantity: 20 };
  f.env.observation.destinations.box.contents = [{ itemId: "(O)388", stack: 20 }];
  const observe = f.env.observe;
  f.env.observe = async () => ({ ...await observe(), chests: { items: [{ tile: { x: 1, y: 1 }, contentsObserved: true, contents: [] }] } });
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "stopped");
  assert.equal(result.stopReason, "phase-objective-unmet");
  assert.equal(f.stored(), 0);
});

test("pre-existing wood absorbed during tree work is retained instead of counted and stored as tree production", async () => {
  const f = fixture({ autoPickup: true });
  const native = f.env.native, observe = f.env.observe;
  let oldDropPresent = true;
  f.env.native = async (skill, args, read) => {
    const result = await native(skill, args, read);
    if (skill === "chop-tree") oldDropPresent = false;
    if (skill === "inspect-production" && oldDropPresent) result.details.production.groundItems.push({
      sourceId: "old-wood", tile: { x: 2, y: 3 }, itemId: "(O)388", stack: 30, isDropped: true });
    return result;
  };
  f.env.observe = async () => {
    const snapshot = await observe();
    if (!oldDropPresent) snapshot.inventory.slots[0].stack += 30;
    return snapshot;
  };
  const result = await new PhaseExecutor().execute(f.input, f.env);
  assert.equal(result.status, "completed");
  assert.equal(f.stored(), 24);
  assert.equal(result.acquiredItems.find(item => item.itemId === "(O)388").count, 24);
});
