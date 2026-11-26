import { object } from "./model.js";
import type { JsonObject } from "./model.js";

const text = (maxLength = 160): JsonObject => ({ type: "string", minLength: 1, maxLength });
const enumeration = (...values: string[]): JsonObject => ({ type: "string", enum: values });
const fields = (properties: JsonObject, required = Object.keys(properties)): JsonObject =>
  ({ type: "object", properties, required, additionalProperties: false });
const list = (items: JsonObject, maxItems: number, minItems = 0): JsonObject => ({ type: "array", items, minItems, maxItems });

/** A bounded decision structure, not a native command queue. Identity is never model supplied. */
export const phasePlanSchema: JsonObject = fields({
  observation_ref: text(), objective: text(240), scope_ref: text(),
  completion: fields({ metric: enumeration("new-wood-stored", "wood-stock-at-least", "trees-stored", "daily-care-stored",
    "new-wood-collected", "trees-collected", "daily-care-completed", "daily-care-shipped"),
    quantity: { type: "integer", minimum: 1, maximum: 9999 } }),
  constraints: fields({ stamina_floor: { type: "number", minimum: 1, maximum: 1000 },
    destination_ref: { ...text(), description: "Required only for chest storage. Omit when retaining products in the backpack or shipping." },
    ship_item_ids: { ...list(text(), 36, 1), description: "Only these explicitly player-authorized item IDs may be shipped; all other products stay in the backpack." },
    keep: list(fields({ item_id: text(), count: { type: "integer", minimum: 0, maximum: 999999 } }), 36),
    include_existing: { ...list(fields({ item_id: text(), count: { type: "integer", minimum: 1, maximum: 999999 } }), 36),
      description: "Explicitly select player-authorized existing materials for this same phase. Check originalBackpack and observed ground items; already auto-picked materials are existing inventory. Use [] only when all existing materials must stay untouched." } }, ["stamina_floor", "keep", "include_existing"]),
  steps: list(fields({ id: text(40), operation: enumeration("tend-crops", "harvest-crops", "chop-trees", "collect-products", "store-products", "ship-products"),
    after: list(text(40), 7), inputs: list(text(40), 7),
    target_quantity: { type: "integer", minimum: 1, maximum: 9999, description: "Tree count for this chop step; required when a daily phase includes forestry." } },
    ["id", "operation", "after", "inputs"]), 8, 1),
  branches: list(fields({ on: enumeration("target-unreachable", "inventory-full", "destination-unavailable", "insufficient-resources"),
    action: enumeration("skip-target", "store-and-resume", "replan"), destination_ref: text() }, ["on", "action"]), 4),
});

export interface PhaseBinding {
  saveId: string; gameSessionId: string; actorMode: string; goalId: string; requestId: string;
  instructionVersion: string; gameDate: string; locationId: string;
}
export interface PhaseObservation {
  ref: string;
  binding: PhaseBinding;
  revision: number;
  complete: boolean;
  scopeRef: string;
  destinations: Record<string, JsonObject>;
  facts: JsonObject;
}
export interface PhaseStep { id: string; operation: string; after: string[]; inputs: string[]; targetQuantity?: number }
export interface PhasePlan {
  objective: string; metric: string; quantity: number; staminaFloor: number; destinationRef: string;
  shipItems: Set<string>;
  keep: Map<string, number>; existing: Map<string, number>; steps: PhaseStep[]; branches: { on: string; action: string; destinationRef?: string }[];
}

/** Schema validation is performed by the tool boundary before this semantic preflight. */
export function bindPhasePlan(input: JsonObject, observation: PhaseObservation, current: PhaseBinding): PhasePlan {
  if (input.observation_ref !== observation.ref || input.scope_ref !== observation.scopeRef
      || Object.keys(observation.binding).some(key => observation.binding[key as keyof PhaseBinding] !== current[key as keyof PhaseBinding]))
    throw new Error("阶段观察已过期或不属于当前要求，请重新观察后规划。");
  if (!observation.complete) throw new Error("阶段观察不完整，不能把未知范围当作完整目标。");
  const constraints = object(input.constraints), completion = object(input.completion);
  const daily = String(completion.metric).startsWith("daily-care-");
  const storedMetric = ["new-wood-stored", "wood-stock-at-least", "trees-stored", "daily-care-stored"].includes(String(completion.metric));
  const keep = new Map<string, number>();
  for (const item of (constraints.keep as JsonObject[])) {
    if (keep.has(String(item.item_id))) throw new Error("保留量必须按物品合并。");
    keep.set(String(item.item_id), Number(item.count));
  }
  const seen = new Map<string, PhaseStep>();
  const steps = (input.steps as JsonObject[]).map(row => ({ id: String(row.id), operation: String(row.operation),
    after: row.after as string[], inputs: row.inputs as string[],
    ...(row.target_quantity !== undefined ? { targetQuantity: Number(row.target_quantity) } : {}) }));
  for (const step of steps) {
    if (seen.has(step.id) || step.after.some(id => !seen.has(id))) throw new Error("阶段步骤需按依赖顺序排列，不能重复、循环或引用未来步骤。");
    const ancestors = new Set<string>();
    const include = (id: string): void => { if (ancestors.has(id)) return; ancestors.add(id); seen.get(id)?.after.forEach(include); };
    step.after.forEach(include);
    if (step.inputs.some(id => !ancestors.has(id) || !seen.has(id))) throw new Error("阶段结果只能引用本计划的前置步骤。");
    if (["collect-products", "store-products", "ship-products"].includes(step.operation) && !step.inputs.length)
      throw new Error("收集和入库必须绑定前置步骤的真实产物范围。");
    if (step.targetQuantity !== undefined && step.operation !== "chop-trees") throw new Error("树木数量只用于采伐步骤。");
    if (daily && step.operation === "chop-trees" && step.targetQuantity === undefined)
      throw new Error("日常阶段采木必须指定本步骤的树木数量，不能默认砍完整图。");
    seen.set(step.id, step);
  }
  const producing = steps.filter(step => ["chop-trees", "harvest-crops"].includes(step.operation));
  const storage = steps.filter(step => step.operation === "store-products");
  const shipping = steps.filter(step => step.operation === "ship-products");
  if (storedMetric && shipping.length) throw new Error("入库阶段不能把要求入库的产物改为出售。");
  if ((storedMetric || storage.length || (input.branches as JsonObject[]).some(row => row.action === "store-and-resume"))
      && !Object.hasOwn(observation.destinations, String(constraints.destination_ref))) throw new Error("入库设施未在本阶段观察中确认。");
  const shipItems = new Set<string>(Array.isArray(constraints.ship_item_ids) ? constraints.ship_item_ids.map(String) : []);
  if (shipping.length && (!shipItems.size || observation.binding.locationId !== "Farm"))
    throw new Error("阶段出货需要农场观察及明确授权出售的物品清单。");
  const sourceOf = (step: PhaseStep, id: string): boolean => step.inputs.includes(id)
    || step.inputs.some(input => sourceOf(seen.get(input)!, id));
  if (!daily && !steps.some(step => step.operation === "chop-trees"))
    throw new Error("木材或树木成果要求包含采伐步骤。");
  if (daily && !steps.some(step => step.operation === "tend-crops"))
    throw new Error("日常阶段必须安排作物照料。");
  if (daily && (Number(object(observation.facts.farmWork).matureCropCount ?? 0) > 0
      || (Array.isArray(object(observation.facts.farmWork).matureCrops) && (object(observation.facts.farmWork).matureCrops as unknown[]).length > 0))
      && !steps.some(step => step.operation === "harvest-crops")) throw new Error("当前成熟作物尚未纳入日常收获计划。");
  if (storedMetric && (!storage.length || producing.some(source => !storage.some(step => sourceOf(step, source.id)))))
    throw new Error("产物入库判据需要覆盖每个产物步骤，不能只安排采集。");
  if (completion.metric === "daily-care-shipped" && (!shipping.length
      || producing.filter(source => source.operation === "harvest-crops").some(source => !shipping.some(step => sourceOf(step, source.id)))))
    throw new Error("日常出货判据需要关联实际收获的出货步骤。");
  if (producing.some(source => !steps.some(step => step.operation === "collect-products" && sourceOf(step, source.id)
      && (!storedMetric || storage.some(store => sourceOf(store, step.id))))))
    throw new Error("产物入库前需要安排关联的收集步骤，不能遗漏散落物。");
  const branches = (input.branches as JsonObject[]).map(row => ({ on: String(row.on), action: String(row.action),
    ...(typeof row.destination_ref === "string" ? { destinationRef: row.destination_ref } : {}) }));
  if (new Set(branches.map(branch => branch.on)).size !== branches.length) throw new Error("同一异常不能指定多个冲突分支。");
  for (const branch of branches) {
    if (branch.destinationRef && !Object.hasOwn(observation.destinations, branch.destinationRef)) throw new Error("备用箱子尚未确认。");
    if ((branch.action === "skip-target" && branch.on !== "target-unreachable")
        || (branch.action === "store-and-resume" && branch.on !== "inventory-full")) throw new Error("阶段异常处理不适用于该条件。");
  }
  if (daily && completion.quantity !== 1) throw new Error("日常完成指标的数量固定为 1。");
  const existing = new Map<string, number>();
  for (const row of Array.isArray(constraints.include_existing) ? constraints.include_existing.map(object) : []) {
    const id = String(row.item_id);
    if (existing.has(id)) throw new Error("原有物品数量必须合并。");
    existing.set(id, Number(row.count));
  }
  return { objective: String(input.objective), metric: String(completion.metric), quantity: Number(completion.quantity),
    staminaFloor: Number(constraints.stamina_floor), destinationRef: String(constraints.destination_ref ?? ""), shipItems, keep, existing, steps, branches };
}
