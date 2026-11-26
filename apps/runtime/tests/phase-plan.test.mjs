import assert from "node:assert/strict";
import { test } from "node:test";
import { phasePlanSchema, bindPhasePlan } from "../dist/application/phase-plan.js";
import { validate } from "../dist/tools/native-tools.js";

export const binding = { saveId: "save", gameSessionId: "session", actorMode: "companion", goalId: "goal",
  requestId: "request", instructionVersion: "original", gameDate: "1:spring:5", locationId: "Farm" };
export const observation = { ref: "observed", binding, revision: 12, complete: true, scopeRef: "farm",
  destinations: { box: { tile: { x: 2, y: 2 } } }, facts: {} };
export const plan = () => ({ observation_ref: "observed", objective: "采木并收好", scope_ref: "farm",
  completion: { metric: "new-wood-stored", quantity: 60 },
  constraints: { stamina_floor: 80, destination_ref: "box", keep: [{ item_id: "(O)388", count: 10 }], include_existing: [] },
  steps: [{ id: "chop", operation: "chop-trees", after: [], inputs: [] },
    { id: "collect", operation: "collect-products", after: ["chop"], inputs: ["chop"] },
    { id: "store", operation: "store-products", after: ["collect"], inputs: ["collect"] }],
  branches: [{ on: "target-unreachable", action: "skip-target" }, { on: "inventory-full", action: "store-and-resume" }] });
const bind = (input, observed = observation, current = binding) => {
  validate(phasePlanSchema, input);
  return bindPhasePlan(input, observed, current);
};

test("phase preflight preserves a complete dependent plan and rejects stale authority before execution", () => {
  assert.equal(bind(plan()).steps.length, 3);
  assert.throws(() => bind(plan(), observation, { ...binding, instructionVersion: "new-message" }), /过期/);
  assert.throws(() => bind(plan(), observation, { ...binding, gameSessionId: "reloaded" }), /过期/);
  assert.throws(() => bind(plan(), { ...observation, complete: false }), /不完整/);
  const forged = plan(); forged.constraints.destination_ref = "other-save-box";
  assert.throws(() => bind(forged), /设施/);
});

test("phase preflight rejects cycles, unbound products and arbitrary recovery commands", () => {
  const cycle = plan(); cycle.steps[0].after = ["store"];
  assert.throws(() => bind(cycle), /依赖/);
  const detached = plan(); detached.steps[2].inputs = ["other-phase-result"];
  assert.throws(() => bind(detached), /前置/);
  const omitted = plan(); omitted.steps.pop();
  assert.throws(() => bind(omitted), /入库/);
  const recovery = plan(); recovery.branches[0].action = "warp-and-retry";
  assert.throws(() => bind(recovery), /工具定义/);
  const unauthorized = plan(); unauthorized.constraints.preserve_saplings = false;
  assert.throws(() => bind(unauthorized), /工具定义/);
});

test("backpack and care phases work without a chest while storage and shipping keep their own prerequisites", () => {
  const collected = plan(); collected.completion.metric = "trees-collected";
  delete collected.constraints.destination_ref;
  collected.steps.pop(); collected.branches = [];
  const noChest = { ...observation, destinations: {} };
  assert.equal(bind(collected, noChest).metric, "trees-collected");
  const care = { ...collected, completion: { metric: "daily-care-completed", quantity: 1 },
    steps: [{ id: "care", operation: "tend-crops", after: [], inputs: [] }] };
  assert.equal(bind(care, noChest).steps.length, 1);
  const missingHarvest = { ...noChest, facts: { farmWork: { matureCrops: [{ x: 1, y: 1 }] } } };
  assert.throws(() => bind(care, missingHarvest), /成熟作物/);
  const shipping = { ...care, completion: { metric: "daily-care-shipped", quantity: 1 }, steps: [...care.steps,
    { id: "ship", operation: "ship-products", after: ["care"], inputs: ["care"] }] };
  assert.throws(() => bind(shipping, noChest), /授权出售/);
  shipping.constraints = { ...care.constraints, ship_item_ids: ["(O)24"] };
  assert.equal(bind(shipping, noChest).shipItems.has("(O)24"), true);
  const unsafe = structuredClone(collected); unsafe.steps[0].target_quantity = 2;
  care.steps.push(unsafe.steps[0], unsafe.steps[1]);
  assert.equal(bind(care, noChest).steps[1].targetQuantity, 2);
  delete care.steps[1].target_quantity;
  assert.throws(() => bind(care, noChest), /树木数量/);
  assert.throws(() => bind(plan(), noChest), /设施/);
});
