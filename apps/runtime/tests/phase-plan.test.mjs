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
  constraints: { stamina_floor: 80, destination_ref: "box", keep: [{ item_id: "(O)388", count: 10 }] },
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
