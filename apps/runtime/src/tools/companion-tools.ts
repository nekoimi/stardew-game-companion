import { createHash, randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { GameConnection } from "../adapters/game/game-connection.js";
import type { GameBinding } from "../adapters/game/action-receipt.js";
import type { CompanionStore } from "../adapters/storage/companion-store.js";
import { object } from "../application/model.js";
import type { JsonObject, JsonValue, ToolDefinition } from "../application/model.js";
import { actions, fields, nativeParameters, oneOperation, queries, validate, workflows } from "./native-tools.js";

const str = (maxLength = 200): JsonObject => ({ type: "string", minLength: 1, maxLength });
const operationSchemas = (values: Record<string, [string, JsonObject]>) =>
  Object.fromEntries(Object.entries(values).map(([name, [, schema]]) => [name, schema]));
const references = {
  read_guidance: fields({ topic: str(60) }),
  recall_memory: fields({ query: str() }, []),
  query_wiki: fields({ query: str(120) }),
};
const adjustments = {
  manage_goal: fields({ action: str(20), text: str(500), summary: str(240) }, ["action"]),
  set_goal_continuation: fields({ status: str(20), reason: str(240), next_time: { type: "integer", minimum: 600, maximum: 2600 },
    next_day: { type: "boolean" }, when_farm_changes: { type: "boolean" } }, ["status", "reason"]),
  manage_todo: fields({ action: str(20), text: str(240), id: str(120) }, ["action"]),
  manage_milestones: fields({ title: str(120), summary: str(240), target_date: str(40) }),
};
const memories = {
  save_memory: fields({ text: str(), kind: str(20) }, ["text"]),
  forget_memory: fields({ note_id: str(120) }),
  say_to_player: fields({ text: str(500) }),
};
const querySchemas = {
  ...operationSchemas(queries),
  query_inventory: fields({}),
  query_chests: fields({}),
  observe_farming_helpers: fields({}),
  get_work_overview: fields({}),
};
const definitions = [
  { name: "get_status", description: "Read the current native game observation and control state.", parameters: fields({}) },
  { name: "query_game", description: "Query one game observation. Route checks validate the route and standing tile.",
    parameters: fields({ query: oneOperation(querySchemas) }) },
  { name: "submit_work", description: "Run a native action or a supported work stage immediately. Use observed targets and verify results.",
    parameters: fields({ action: oneOperation({ ...operationSchemas(actions), ...workflows }) }) },
  { name: "manage_arrangements", description: "Maintain the bound goal, reminders and milestones. Set continuation or a real game wait.",
    parameters: fields({ change: oneOperation(adjustments) }) },
  { name: "remember", description: "Save a lasting preference or agreement, remove a memory, or speak to the player.",
    parameters: fields({ entry: oneOperation(memories) }) },
  { name: "request_player_decision", description: "Ask the player to choose when a real tradeoff prevents progress.",
    parameters: fields({ message: str(500), choices: { type: "array", minItems: 2, maxItems: 5, items: str(100) } }) },
  { name: "consult", description: "Read packaged guidance, remember a preference or consult the Stardew Valley wiki.",
    parameters: fields({ reference: oneOperation(references) }) },
  { name: "read_reload_history", description: "Read retained player context after the current game was loaded.", parameters: fields({}) },
] satisfies ToolDefinition[];

export const gameTools: readonly ToolDefinition[] = definitions;

const topics = new Set(["crop-selection", "farm-region", "livestock-processing", "procurement-travel",
  "wiki-lookup", "daily-rhythm", "player-rhythm", "execution"]);
const skillsWithMaterialObservations = new Set(["harvest-zone", "clear-debris", "chop-tree", "cut-grass", "pickup-items"]);

type ToolContext = {
  binding: GameBinding;
  turnId: string;
  requestId: string;
  goalId: string;
  signal: AbortSignal;
  native: (skill: string, parameters: JsonObject, observation?: boolean) => Promise<JsonObject>;
  workBlock?: () => string | undefined;
  speak: (text: string) => void;
  changed: () => void;
  pluginArrangement?: (operation: string, params: JsonObject) => JsonObject;
};

export function gameDate(snapshot: JsonObject | undefined): string {
  const world = object(snapshot?.world);
  return typeof world.year === "number" && typeof world.season === "string" && typeof world.dayOfMonth === "number"
    ? `${world.year}:${world.season}:${world.dayOfMonth}` : "";
}

/** Excludes revision, clock, pose and progress text: none implies new farm work. */
export function farmWorkSignature(snapshot: JsonObject | undefined): string {
  const farm = object(snapshot?.farmWork ?? object(snapshot?.world).farmWork);
  if (farm.observationStatus !== "observed" || typeof farm.locationId !== "string"
      || !Array.isArray(farm.cropUnwateredTiles) || !Array.isArray(farm.matureCrops)) return "";
  return createHash("sha256").update(JSON.stringify({
    locationId: farm.locationId, cropUnwateredCount: farm.cropUnwateredCount ?? null,
    matureCropCount: farm.matureCropCount ?? null, totalCropCount: farm.totalCropCount ?? null,
    dry: farm.cropUnwateredTiles.map(tileKey).sort(), mature: farm.matureCrops.map(tileKey).sort(),
    dryTruncated: farm.cropUnwateredTruncated ?? null, matureTruncated: farm.matureCropsTruncated ?? null,
  })).digest("hex");
}

function collectible(value: JsonValue): boolean {
  const item = object(value);
  return item.isDropped === true || item.canBeGrabbed === true;
}

function matchingTiles(requested: JsonValue, observed: JsonValue[]): JsonValue[] {
  const keys = new Set(observed.map(value => {
    const { x, y } = object(value);
    return `${x}:${y}`;
  }));
  return Array.isArray(requested) ? requested.filter(value => {
    const { x, y } = object(value);
    return keys.has(`${x}:${y}`);
  }) : [];
}

function noObservedTargets(truncated = false): JsonObject {
  return { terminalState: "no-observed-targets", observedTargets: 0, observationComplete: !truncated,
    message: truncated ? "当前观察不完整，尚未确认可执行的目标。" : "当前没有观察到可执行的目标。" };
}

function tileKey(value: JsonValue | undefined): string {
  const { x, y } = object(value);
  return typeof x === "number" && Number.isSafeInteger(x) && x >= 0
    && typeof y === "number" && Number.isSafeInteger(y) && y >= 0 ? `${x}:${y}` : "";
}

function observedTiles(values: JsonValue | undefined): JsonObject[] {
  const tiles = new Map<string, JsonObject>();
  if (Array.isArray(values)) for (const value of values) {
    const key = tileKey(value);
    if (key) {
      const { x, y } = object(value);
      tiles.set(key, { x, y });
    }
  }
  return [...tiles.values()];
}

function inRegion(tile: JsonObject, value: JsonValue | undefined): boolean {
  if (!value) return true;
  const region = object(value);
  return Number(tile.x) >= Number(region.x) && Number(tile.x) < Number(region.x) + Number(region.width)
    && Number(tile.y) >= Number(region.y) && Number(tile.y) < Number(region.y) + Number(region.height);
}

function itemCounts(values: JsonValue | undefined): Map<string, number> {
  const counts = new Map<string, number>();
  if (!Array.isArray(values)) return counts;
  for (const value of values) {
    const { itemId, stack, isTool } = object(value);
    if (typeof itemId === "string" && typeof stack === "number" && stack > 0 && isTool !== true) {
      counts.set(itemId, (counts.get(itemId) ?? 0) + stack);
    }
  }
  return counts;
}

function inventoryDifference(before: JsonObject, after: JsonObject, totals: JsonObject[] = []): JsonObject[] {
  if (!Array.isArray(before.slots) || !Array.isArray(after.slots)) return totals;
  const previous = itemCounts(before.slots);
  const gains = new Map(totals.map(item => [String(item.itemId), Number(item.count)]));
  for (const [itemId, count] of itemCounts(after.slots)) {
    const increase = count - (previous.get(itemId) ?? 0);
    if (increase > 0) gains.set(itemId, (gains.get(itemId) ?? 0) + increase);
  }
  return [...gains].map(([itemId, count]) => ({ itemId, count }));
}

function summarizeGround(ground: JsonValue | undefined): JsonValue {
  const { locationId, groundItems, truncated } = object(ground);
  return Array.isArray(groundItems) ? { locationId, count: groundItems.length, truncated, items: groundItems.slice(0, 8) } : null;
}

function observedChest(snapshot: JsonObject, tile: JsonObject): JsonObject | undefined {
  const chests = object(snapshot.chests);
  // The game snapshot's chests section is scoped to Farm.
  if (object(snapshot.companion).locationId !== "Farm" || !Array.isArray(chests.items)) return undefined;
  return chests.items.map(object).find(chest =>
    tileKey(chest.tile) === tileKey(tile) && chest.contentsObserved === true && Array.isArray(chest.contents));
}

export function modelObservation(snapshot: JsonObject | undefined): JsonObject {
  if (!snapshot) return {};
  const farming = object(snapshot.farming);
  if (!Array.isArray(farming.groundItems)) return snapshot;
  return { ...snapshot, farming: {
    ...farming,
    groundItems: farming.groundItems.filter(collectible),
    groundObjects: farming.groundItems.filter(item => !collectible(item)),
    groundObjectsTruncated: farming.groundItemsTruncated ?? true,
  } };
}

function pick(value: JsonValue | undefined, keys: readonly string[]): JsonObject {
  const source = object(value);
  return Object.fromEntries(keys.filter(key => Object.hasOwn(source, key)).map(key => [key, source[key]]));
}

export function decisionObservation(snapshot: JsonObject | undefined): JsonObject {
  if (!snapshot) return {};
  const companion = pick(snapshot.companion, ["locationId", "tileX", "tileY", "stamina", "maxStamina", "waterCanLevel",
    "maxWaterCanLevel", "hasWateringCan", "activity", "availableMoney", "moneyStatus", "restState", "bedtime", "bedtimeSchedule"]);
  const world = pick(snapshot.world, ["currentLocation", "timeOfDay", "season", "dayOfMonth", "year", "isRaining", "weatherIcon",
    "playerStamina", "playerMaxStamina", "native"]);
  const farm = object(snapshot.farmWork ?? object(snapshot.world).farmWork);
  const farmWork = pick(farm, ["locationId", "observationStatus", "scope",
    "tilledUnwateredCount", "matureCropCount", "cropUnwateredCount", "deadCropCount", "cultivatedPlotCount", "totalCropCount",
    "isTruncated", "matureCropsTruncated", "cropUnwateredTruncated", "cultivatedPlotsTruncated"]);
  for (const [key, truncated] of [["matureCrops", "matureCropsTruncated"], ["cropUnwateredTiles", "cropUnwateredTruncated"],
    ["tilledUnwateredTiles", "isTruncated"], ["cultivatedPlots", "cultivatedPlotsTruncated"]]) {
    const targets = farm[key];
    if (!Array.isArray(targets)) continue;
    farmWork[key] = targets.slice(0, 24);
    if (targets.length > 24) farmWork[truncated] = true;
  }
  return { capturedRevision: snapshot.capturedRevision ?? null, actorMode: snapshot.actorMode ?? null,
    controlOwner: snapshot.controlOwner ?? null, companion, world, ...(Object.keys(farmWork).length ? { farmWork } : {}) };
}

function choose(args: JsonValue | undefined): [string, JsonObject] {
  const [entry] = Object.entries(object(args));
  return [entry[0], object(entry[1])];
}

export class CompanionTools {
  constructor(private readonly game: GameConnection, private readonly directory: string,
              private readonly store?: CompanionStore) {}

  async invoke(context: ToolContext, name: string, args: JsonObject): Promise<JsonValue> {
    const definition = definitions.find(item => item.name === name);
    if (!definition) throw new Error("游戏工具不存在。");
    validate(definition.parameters, args);
    const { signal, binding } = context;
    signal.throwIfAborted();
    const save = binding.saveId;
    const actor = object(this.game.snapshot?.companion);
    const locationId = typeof actor.locationId === "string" ? actor.locationId : "Farm";
    switch (name) {
      case "get_status": {
        const world = modelObservation(await this.game.observe());
        return { world, controlOwner: this.game.state.controlOwner ?? "human" };
      }
      case "submit_work": {
        const [operation, params] = choose(args.action);
        if (operation === "tend_crops") return this.tendCrops(context, params);
        if (operation === "harvest_crops") return this.harvestCrops(context, params);
        if (operation === "collect_ground_items") return this.collectGroundItems(context, params);
        if (operation === "store_items" || operation === "ship_inventory") {
          return this.transferInventory(context, params, operation === "store_items");
        }
        const [skill] = actions[operation];
        const native: JsonObject = { locationId, ...nativeParameters(params) };
        if (operation === "water_auto" || operation === "harvest_auto" ||
            (operation === "refill_watering_can" && params.tiles === undefined)) {
          const snapshot = await this.game.observe();
          const farm = object(snapshot.farmWork ?? object(snapshot.world).farmWork);
          const farming = object(snapshot.farming);
          if (operation !== "refill_watering_can" && (farm.observationStatus !== "observed"
              || (typeof farm.locationId === "string" && farm.locationId !== native.locationId))) {
            throw new Error("当前没有该地块的作物观察，请先到目标地图或查询地块。");
          }
          if (operation === "refill_watering_can" && farming.location !== native.locationId) {
            throw new Error("当前没有该地图的水源观察。");
          }
          let source = operation === "water_auto" ? farm.cropUnwateredTiles
            : operation === "harvest_auto" ? farm.matureCrops : farming.refillWaterTiles;
          if (operation === "refill_watering_can" && Array.isArray(source) && !source.length) {
            const result = await context.native("inspect-water-sources", { locationId: native.locationId }, true);
            if (result.terminalState !== "succeeded") return result;
            source = object(result.details).tiles;
          }
          if (!Array.isArray(source)) throw new Error("尚未取得目标观察。");
          const all = source.flatMap(value => {
            const { x, y } = object(value);
            return typeof x === "number" && Number.isSafeInteger(x) && typeof y === "number" && Number.isSafeInteger(y)
              ? [{ x, y }] : [];
          });
          const selected = Array.isArray(params.target_tiles)
            ? all.filter(value => (params.target_tiles as JsonValue[]).some(target =>
                object(value).x === object(target).x && object(value).y === object(target).y)) : all;
          native.tiles = selected.slice(0, Number(params.max_tiles ?? (operation === "refill_watering_can" ? 4 : 100)));
          delete native.maxTiles;
          delete native.targetTiles;
          if (!native.tiles.length) return noObservedTargets(operation === "water_auto"
            ? farm.cropUnwateredTruncated === true : operation === "harvest_auto" && farm.matureCropsTruncated === true);
        }
        if (operation === "chop_tree") {
          const farming = object((await this.game.observe()).farming);
          if (farming.location !== native.locationId) throw new Error("请先观察目标地图的树木。");
          const observed = farming.choppableTrees;
          const trees = Array.isArray(observed) ? observed : [];
          const requested = Array.isArray(params.tiles) ? params.tiles : [];
          if (requested.some(target => !trees.some(entry => {
            const tree = object(entry);
            const tile = object(tree.tile);
            return tile.x === object(target).x && tile.y === object(target).y && Number(tree.growthStage) >= 5 && tree.tapped !== true;
          }))) {
            throw new Error("请先核对成熟且未装树液采集器的树，幼树和价值较高的树默认保留。");
          }
        }
        if (operation === "water_tiles") {
          native.includeEmptyTiles = native.includeEmptyTiles === true;
        }
        if (operation === "harvest_tiles" || (operation === "water_tiles" && native.includeEmptyTiles !== true)) {
          const snapshot = await this.game.observe();
          const farm = object(snapshot.farmWork ?? object(snapshot.world).farmWork);
          const watering = operation === "water_tiles";
          // The routine crop snapshot covers the main farm. Other locations
          // still use the native harvest validation for explicit targets.
          if (watering || farm.locationId === native.locationId) {
            const source = watering ? farm.cropUnwateredTiles : farm.matureCrops;
            if (farm.observationStatus !== "observed" || farm.locationId !== native.locationId || !Array.isArray(source)) {
              throw new Error("当前没有该区域的作物观察，请先查看农场状态。");
            }
            native.tiles = matchingTiles(native.tiles, source);
            if (!native.tiles.length) return noObservedTargets((watering ? farm.cropUnwateredTruncated : farm.matureCropsTruncated) === true);
          }
        }
        if (operation === "pickup_items") {
          const farming = object((await this.game.observe()).farming);
          const local = farming.location === native.locationId && Array.isArray(farming.groundItems)
            ? matchingTiles(native.tiles, farming.groundItems.filter(collectible).map(item => object(item).tile)) : [];
          if (local.length !== (native.tiles as JsonValue[]).length) {
            const result = await context.native("inspect-production", { locationId: native.locationId }, true);
            const production = object(object(result.details).production);
            if (result.terminalState !== "succeeded" || production.locationId !== native.locationId || !Array.isArray(production.groundItems)) {
              throw new Error("当前没有该区域的拾取物观察，请先查看游戏状态。");
            }
            native.tiles = matchingTiles(native.tiles, production.groundItems.filter(collectible).map(item => object(item).tile));
            if (!native.tiles.length) return noObservedTargets(production.groundItemsTruncated !== false);
          }
        }
        const receipt = await context.native(skill, native);
        if (!skillsWithMaterialObservations.has(skill)) return receipt;
        return { ...receipt, afterWork: await this.observeAfterWork(context, native.locationId, receipt.finalWorldRevision) };
      }
      case "query_game": {
        const [operation, params] = choose(args.query);
        if (operation === "get_work_overview") return {
          goals: this.store?.items(save, "goals") ?? [], todos: this.store?.items(save, "todos") ?? [],
          receipts: this.store?.recentReceipts(save) ?? [],
        };
        if (Object.hasOwn(queries, operation)) {
          const [skill] = queries[operation];
          const result = await context.native(skill, { locationId, ...nativeParameters(params) }, true);
          const details = object(result.details);
          return Object.values(details).length === 1 ? Object.values(details)[0] : result;
        }
        const snapshot = modelObservation(await this.game.observe());
        const key = { query_inventory: "inventory", query_chests: "chests", observe_farming_helpers: "farming" }[operation];
        return key ? snapshot[key] ?? { observationStatus: "unknown" } : {};
      }
      case "manage_arrangements": {
        const [operation, params] = choose(args.change);
        if (context.pluginArrangement) return context.pluginArrangement(operation, params);
        const store = this.requireStore();
        const goal = store.items(save, "goals").find(row => row.id === context.goalId);
        let result: JsonObject;
        if (operation === "manage_goal") {
          const action = String(params.action);
          if (!["add", "update", "complete", "cancel"].includes(action)) throw new Error("目标操作不支持。");
          if (["complete", "cancel"].includes(action) && !goal) throw new Error("当前没有可完成或取消的目标。");
          result = store.updateItem(save, "goals", context.goalId,
            { text: params.text ?? goal?.text ?? "当前玩家目标", summary: params.summary ?? goal?.summary ?? "",
              requestId: context.requestId, status: action === "complete" ? "completed" : action === "cancel" ? "cancelled" : "active",
              continuation: goal?.continuation ?? "continue" });
        } else if (operation === "set_goal_continuation") {
          if (params.status !== "continue" && params.status !== "wait") throw new Error("续作状态无效。");
          if (params.status === "wait" && params.next_time === undefined && params.next_day !== true && params.when_farm_changes !== true) {
            throw new Error("等待需要真实的游戏时间、下一天或农田变化条件。");
          }
          const farmSignature = params.when_farm_changes === true ? farmWorkSignature(await this.game.observe()) : "";
          if (params.when_farm_changes === true && !farmSignature) throw new Error("没有有效的农田观察，暂不能等待农田变化。");
          result = store.updateItem(save, "goals", context.goalId,
            { text: goal?.text ?? params.reason, requestId: context.requestId, status: "active",
              continuation: params.status, summary: params.reason, wakeTime: params.next_time ?? null,
              waitNextDay: params.next_day === true, waitFarmSignature: farmSignature || null,
              updatedDay: gameDate(this.game.snapshot) });
        } else if (operation === "manage_milestones") {
          result = store.updateItem(save, "milestones", randomUUID(), {
            title: params.title, summary: params.summary, targetDate: params.target_date, status: "suggested", prepItems: [],
          });
        } else {
          if (params.action !== "add" && params.action !== "complete") throw new Error("待办操作不支持。");
          if (params.action === "complete" && typeof params.id !== "string") throw new Error("请选择已有待办。");
          result = store.updateItem(save, "todos", String(params.id ?? randomUUID()), { intent: params.text ?? "",
            status: params.action === "complete" ? "completed" : "pending" }, params.action === "complete");
        }
        context.changed();
        return result;
      }
      case "remember": {
        const [operation, params] = choose(args.entry);
        if (operation === "say_to_player") { context.speak(String(params.text)); return { delivered: true }; }
        if (context.pluginArrangement) throw new Error("存档记忆由玩家回合维护。");
        const kind = String(params.kind ?? "preference");
        if (kind === "event") throw new Error("共同经历只从原生回执生成。");
        return this.requireStore().editMemory(save, operation === "save_memory" ? "add" : "delete",
          String(params.note_id ?? ""), kind, String(params.text ?? ""), "companion");
      }
      case "request_player_decision": {
        const result = this.requireStore().updateItem(save, "decisions", randomUUID(), {
          message: args.message, choices: args.choices, requestId: context.requestId, goalId: context.goalId,
          status: "pending", gameDate: gameDate(this.game.snapshot),
        });
        context.changed();
        return result;
      }
      case "read_reload_history":
        return { gameDate: gameDate(this.game.snapshot), goals: this.store?.items(save, "goals") ?? [],
          conversation: this.store?.history(save).entries ?? [] };
      case "consult": {
        const [operation, params] = choose(args.reference);
        if (operation === "recall_memory") {
          const all = this.store?.memory(save).entries ?? [];
          const search = String(params.query ?? "").toLowerCase();
          return all.filter(entry => String(entry.text).toLowerCase().includes(search)).slice(-20);
        }
        if (operation === "query_wiki") return lookupWiki(String(params.query), signal);
        let topic = String(params.topic);
        if (!topics.has(topic)) throw new Error("请使用项目已打包的指导主题。");
        if (topic === "daily-rhythm" && this.game.actorMode === "player") topic = "player-rhythm";
        const path = join(this.directory, "docs", "agent-guidance", `${topic}.md`);
        return { topic, text: await readFile(path, { encoding: "utf8", signal }) };
      }
      default: throw new Error("游戏工具不存在。");
    }
  }

  private async tendCrops(context: ToolContext, params: JsonObject): Promise<JsonObject> {
    const locationId = String(params.location_id);
    const region = params.region;
    const receipts: string[] = [];
    const watered = new Set<string>();
    let skipped = 0;
    let failed = 0;
    let refills = 0;
    let remaining: number | null = null;
    let finalRevision = 0;
    let lastRefillProgress = -1;
    let lastErrorCode = "";
    let stopReason = "";
    let step = "observe";

    try {
      for (;;) {
        stopReason = context.workBlock?.() ?? "";
        if (stopReason) break;
        step = "observe";
        let snapshot = await this.game.observe();
        let farm = object(snapshot.farmWork ?? object(snapshot.world).farmWork);
        if (farm.locationId !== locationId || farm.observationStatus !== "observed"
            || (region && farm.cropUnwateredTruncated !== false)
            || (finalRevision && Number(snapshot.capturedRevision ?? -1) < finalRevision)) {
          const receipt = await context.native("inspect-farm-work", { locationId, ...(region ? { region } : {}) }, true);
          if (receipt.terminalState !== "succeeded" || Number(receipt.finalWorldRevision) < finalRevision) {
            remaining = null;
            stopReason = "crop-observation-unavailable";
            break;
          }
          farm = object(object(receipt.details).farmWork);
          snapshot = await this.game.observe();
        }
        if (farm.locationId !== locationId || farm.observationStatus !== "observed" || !Array.isArray(farm.cropUnwateredTiles)) {
          remaining = null;
          stopReason = "crop-observation-unavailable";
          break;
        }

        const all = observedTiles(farm.cropUnwateredTiles);
        const observed = all.filter(tile => inRegion(tile, region));
        const targets = observed.filter(tile => !watered.has(tileKey(tile)));
        const complete = farm.cropUnwateredTruncated === false && all.length === farm.cropUnwateredTiles.length;
        remaining = complete ? observed.length : null;
        if (!targets.length) {
          stopReason = !complete ? "crop-observation-truncated" : observed.length ? "no-progress"
            : watered.size ? "no-dry-crops" : "no-observed-targets";
          break;
        }
        if (Number(snapshot.capturedRevision ?? -1) < finalRevision) {
          stopReason = "actor-observation-unavailable";
          break;
        }
        const actor = object(snapshot.companion);
        if (actor.locationId !== locationId) { stopReason = "actor-location-changed"; break; }
        const level = actor.waterCanLevel;
        if (actor.hasWateringCan === false) { stopReason = "missing-watering-can"; break; }
        if (typeof level !== "number" || !Number.isSafeInteger(level) || level < 0) {
          stopReason = "watering-can-observation-unavailable";
          break;
        }
        step = level === 0 ? "refill" : "water";
        if (step === "refill" && lastRefillProgress === watered.size) { stopReason = "refill-no-progress"; break; }
        const batch = targets.slice(0, Math.min(level, 24));
        stopReason = context.workBlock?.() ?? "";
        if (stopReason) break;
        const receipt = step === "refill"
          ? await this.refillCan(context, locationId)
          : await context.native("water-zone", { locationId, tiles: batch, includeEmptyTiles: false });
        if (typeof receipt.commandId === "string") receipts.push(receipt.commandId);
        if (typeof receipt.finalWorldRevision === "number") finalRevision = receipt.finalWorldRevision;
        const result = receipt.terminalState;
        lastErrorCode = String(object(receipt.error).code ?? "");
        if (typeof receipt.commandId === "string") remaining = null;

        if (step === "refill") {
          if (result !== "succeeded") {
            failed += typeof receipt.commandId === "string" ? 1 : 0;
            stopReason = context.workBlock?.() ?? String(receipt.stopReason ?? "refill-failed");
            break;
          }
          lastRefillProgress = watered.size;
          const checked = await this.game.observe();
          const refreshed = object(checked.companion);
          if (Number(checked.capturedRevision ?? -1) < finalRevision || refreshed.locationId !== locationId
              || typeof refreshed.waterCanLevel !== "number" || refreshed.waterCanLevel <= level) {
            stopReason = "refill-no-progress";
            break;
          }
          refills++;
          continue;
        }

        const sent = new Set(batch.map(tileKey));
        let progress = false;
        for (const value of Array.isArray(receipt.effects) ? receipt.effects : []) {
          const effect = object(value);
          const key = tileKey(effect.tile);
          if (effect.state === "watered" && sent.has(key) && !watered.has(key)) {
            watered.add(key);
            progress = true;
          }
        }
        skipped += Number(receipt.skippedCount ?? 0);
        failed += Number(receipt.failedCount ?? 0);
        const blocked = context.workBlock?.();
        if (blocked || result === "cancelled" || receipt.playerActionRequired === true) {
          stopReason = blocked ?? "interrupted";
          break;
        }
        if (lastErrorCode === "CAN_EMPTY") {
          const checked = await this.game.observe();
          if (Number(checked.capturedRevision ?? -1) >= finalRevision && object(checked.companion).waterCanLevel === 0) continue;
        }
        if (result === "failed" || result === "rejected" || failed > 0) { stopReason = "watering-failed"; break; }
        if (!progress && Number(receipt.skippedCount ?? 0) === 0) { stopReason = "no-progress"; break; }
        if (!progress) {
          // Skips can be due to the player tending the same tiles. Confirm the
          // selected set actually changed before any further dispatch.
          step = "observe";
          const checked = await this.game.observe();
          const current = object(checked.farmWork ?? object(checked.world).farmWork);
          if (Number(checked.capturedRevision ?? -1) < finalRevision || current.observationStatus !== "observed"
              || current.locationId !== locationId || current.cropUnwateredTruncated !== false || !Array.isArray(current.cropUnwateredTiles)
              || current.cropUnwateredTiles.some(tile => sent.has(tileKey(tile)))) {
            stopReason = "no-progress";
            break;
          }
        }
      }
    } catch {
      stopReason = context.workBlock?.() ?? (step === "observe" ? "crop-observation-unavailable" : "action-unavailable");
      remaining = null;
    }

    return { status: stopReason === "no-dry-crops" ? "completed" : "stopped", stopReason,
      completedCount: watered.size, skippedCount: skipped, failedCount: failed, refills, remainingCount: remaining,
      observationComplete: remaining !== null, receiptCount: receipts.length, receiptIds: receipts.slice(-8),
      needsReconciliation: stopReason === "recovery-pending" || stopReason === "game-changed",
      ...(lastErrorCode ? { errorCode: lastErrorCode } : {}) };
  }

  private async refillCan(context: ToolContext, locationId: string): Promise<JsonObject> {
    const result = await context.native("inspect-water-sources", { locationId }, true);
    if (result.terminalState !== "succeeded") return { stopReason: "water-source-observation-unavailable" };
    const tiles = observedTiles(object(result.details).tiles);
    if (!tiles.length) return { stopReason: "no-observed-water-source" };
    const blocked = context.workBlock?.();
    if (blocked) return { stopReason: blocked };
    return context.native("refill-watering-can", { locationId, tiles: tiles.slice(0, 16) });
  }

  private async harvestCrops(context: ToolContext, params: JsonObject): Promise<JsonObject> {
    const locationId = String(params.location_id);
    const region = params.region;
    const limit = Number(params.limit ?? 4096);
    const harvested = new Set<string>();
    const receipts: string[] = [];
    let skipped = 0;
    let failed = 0;
    let stopReason = "";
    let remaining: number | null = null;
    let finalRevision = 0;
    let inventoryGains: JsonObject[] = [];
    let ground: JsonValue = null;
    try { while (harvested.size < limit) {
      stopReason = context.workBlock?.() ?? "";
      if (stopReason) break;
      let snapshot = await this.game.observe();
      let farm = object(snapshot.farmWork ?? object(snapshot.world).farmWork);
      if (farm.locationId !== locationId || farm.observationStatus !== "observed" || farm.matureCropsTruncated !== false
          || (finalRevision && Number(snapshot.capturedRevision ?? -1) < finalRevision)) {
        const inspection = await context.native("inspect-farm-work", { locationId, ...(region ? { region } : {}) }, true);
        if (inspection.terminalState !== "succeeded" || Number(inspection.finalWorldRevision) < finalRevision) {
          stopReason = "crop-observation-unavailable";
          break;
        }
        farm = object(object(inspection.details).farmWork);
        snapshot = await this.game.observe();
      }
      if (farm.locationId !== locationId || farm.observationStatus !== "observed" || !Array.isArray(farm.matureCrops)) {
        stopReason = "crop-observation-unavailable";
        break;
      }
      const all = observedTiles(farm.matureCrops).filter(tile => inRegion(tile, region));
      const targets = all.filter(tile => !harvested.has(tileKey(tile)));
      remaining = farm.matureCropsTruncated === false ? all.length : null;
      if (!targets.length) {
        stopReason = farm.matureCropsTruncated === false
          ? all.length ? "no-progress" : harvested.size ? "no-mature-crops" : "no-observed-targets" : "crop-observation-truncated";
        break;
      }
      if (Number(snapshot.capturedRevision ?? -1) < finalRevision) { stopReason = "crop-observation-unavailable"; break; }
      if (object(snapshot.companion).locationId !== locationId) { stopReason = "actor-location-changed"; break; }
      const inventory = object(snapshot.inventory);
      if (!Array.isArray(inventory.slots) || Number(inventory.capacity) <= 0) { stopReason = "inventory-observation-unavailable"; break; }
      if (!Number(inventory.freeSlots)) { stopReason = "inventory-full"; break; }
      const batch = targets.slice(0, Math.min(24, limit - harvested.size));
      stopReason = context.workBlock?.() ?? "";
      if (stopReason) break;
      const receipt = await context.native("harvest-zone", { locationId, tiles: batch });
      if (typeof receipt.commandId === "string") receipts.push(receipt.commandId);
      finalRevision = Number(receipt.finalWorldRevision ?? finalRevision);
      const effects = Array.isArray(receipt.effects) ? receipt.effects.map(object) : [];
      const dispatched = new Set(batch.map(tileKey));
      const completed = effects.filter(effect => effect.state === "harvested" && dispatched.has(tileKey(effect.tile)));
      for (const effect of completed) harvested.add(tileKey(effect.tile));
      skipped += Number(receipt.skippedCount ?? 0);
      failed += Number(receipt.failedCount ?? 0);
      remaining = null;
      const blocked = context.workBlock?.();
      if (blocked || receipt.terminalState === "cancelled" || receipt.playerActionRequired === true) {
        stopReason = blocked ?? "interrupted";
        break;
      }
      const after = await this.observeAfterWork(context, locationId, finalRevision);
      if (after.status === "unavailable") { stopReason = "harvest-observation-unavailable"; break; }
      inventoryGains = inventoryDifference(inventory, object(after.inventory), inventoryGains);
      ground = after.ground;
      if (String(object(receipt.error).code).toLowerCase() === "inventory_full" || object(receipt.details).inventoryFull === true) {
        stopReason = "inventory-full";
        break;
      }
      if (receipt.terminalState === "failed" || receipt.terminalState === "rejected" || failed > 0) {
        stopReason = String(object(receipt.error).code ?? "harvest-failed");
        break;
      }
      if (!completed.length) {
        stopReason = "no-progress";
        break;
      }
    } } catch {
      stopReason = context.workBlock?.() ?? "harvest-observation-or-action-unavailable";
      remaining = null;
    }
    if (!stopReason) stopReason = "requested-limit-reached";
    return { status: ["no-mature-crops", "requested-limit-reached"].includes(stopReason) ? "completed" : "stopped", stopReason,
      completedCount: harvested.size, skippedCount: skipped, failedCount: failed, remainingCount: remaining,
      observationComplete: remaining !== null, receiptCount: receipts.length, receiptIds: receipts.slice(-8),
      inventoryGains, ground: summarizeGround(ground),
      ...(stopReason === "no-observed-targets" && !harvested.size ? { message: "当前没有观察到成熟作物。" } : {}) };
  }

  private async collectGroundItems(context: ToolContext, params: JsonObject): Promise<JsonObject> {
    const locationId = String(params.location_id);
    const region = params.region;
    const requested = Array.isArray(params.target_tiles) ? new Set(params.target_tiles.map(tileKey)) : undefined;
    const collected = new Map<string, number>();
    const attempted = new Set<string>();
    const receipts: string[] = [];
    let failed = 0;
    let stopReason = "";
    let observationComplete = false;
    let finalRevision = 0;
    try { for (;;) {
      stopReason = context.workBlock?.() ?? "";
      if (stopReason) break;
      const inspection = await context.native("inspect-production", { locationId }, true);
      if (inspection.terminalState !== "succeeded" || Number(inspection.finalWorldRevision) < finalRevision) {
        stopReason = "ground-observation-unavailable";
        break;
      }
      const production = object(object(inspection.details).production);
      if (production.locationId !== locationId || !Array.isArray(production.groundItems)) {
        stopReason = "ground-observation-unavailable";
        break;
      }
      observationComplete = production.groundItemsTruncated === false;
      const candidates = production.groundItems.map(object).filter(item => tileKey(item.tile) && collectible(item)
        && inRegion(object(item.tile), region)
        && (!requested || requested.has(tileKey(item.tile))));
      if (!candidates.length) {
        stopReason = observationComplete ? collected.size ? "no-more-ground-items" : "no-observed-targets" : "ground-observation-truncated";
        break;
      }
      const target = candidates.find(item => !attempted.has(tileKey(item.tile)));
      if (!target) { stopReason = "no-progress"; break; }
      const before = await this.game.observe();
      if (object(before.companion).locationId !== locationId) { stopReason = "actor-location-changed"; break; }
      if (!Array.isArray(object(before.inventory).slots)) { stopReason = "inventory-observation-unavailable"; break; }
      stopReason = context.workBlock?.() ?? "";
      if (stopReason) break;
      attempted.add(tileKey(target.tile));
      const receipt = await context.native("pickup-items", { locationId, tiles: [target.tile] });
      if (typeof receipt.commandId === "string") receipts.push(receipt.commandId);
      finalRevision = Number(receipt.finalWorldRevision ?? finalRevision);
      const code = String(object(receipt.error).code ?? "");
      const blocked = context.workBlock?.();
      if (blocked || receipt.terminalState === "cancelled" || receipt.playerActionRequired === true) {
        stopReason = blocked ?? "interrupted";
        break;
      }
      const after = await this.observeAfterWork(context, locationId, finalRevision);
      if (after.status === "unavailable" || !Array.isArray(object(after.inventory).slots)) {
        stopReason = "pickup-observation-unavailable";
        break;
      }
      const candidateIds = new Set(candidates.map(item => item.itemId));
      for (const gain of inventoryDifference(object(before.inventory), object(after.inventory))) {
        if (typeof gain.itemId === "string" && candidateIds.has(gain.itemId)) {
          collected.set(gain.itemId, (collected.get(gain.itemId) ?? 0) + Number(gain.count));
        }
      }
      if (["failed", "rejected"].includes(String(receipt.terminalState)) && code !== "no-item") {
        failed++;
        stopReason = code || "pickup-failed";
        break;
      }
    } } catch {
      stopReason = context.workBlock?.() ?? "pickup-observation-or-action-unavailable";
      observationComplete = false;
    }
    return { status: stopReason === "no-more-ground-items" ? "completed" : "stopped", stopReason,
      completedCount: [...collected.values()].reduce((sum, value) => sum + value, 0),
      collectedItems: [...collected].map(([itemId, count]) => ({ itemId, count })),
      failedCount: failed, observationComplete, receiptCount: receipts.length, receiptIds: receipts.slice(-8) };
  }

  private async transferInventory(context: ToolContext, params: JsonObject, storeItems: boolean): Promise<JsonObject> {
    const locationId = String(params.location_id);
    const chestTile = object(params.chest_tile);
    const requested = new Map<string, number>();
    for (const item of (params.items as JsonValue[]).map(object)) {
      const itemId = String(item.item_id);
      if (requested.has(itemId)) throw new Error("请合并同一种物品的数量。");
      requested.set(itemId, Number(item.count));
    }
    const keep = new Map<string, number>();
    for (const value of Array.isArray(params.keep) ? params.keep : []) {
      const item = object(value);
      const itemId = String(item.item_id);
      keep.set(itemId, Math.max(keep.get(itemId) ?? 0, Number(item.count)));
    }
    const receiptIds: string[] = [];
    const moved = new Map<string, number>();
    const totalRequested = [...requested.values()].reduce((sum, count) => sum + count, 0);
    let stopReason = "";
    let finalRevision = 0;
    try { while (!stopReason) {
      stopReason = context.workBlock?.() ?? "";
      if (stopReason) break;
      const before = await this.game.observe();
      const backpack = object(before.inventory).slots;
      if (!Array.isArray(backpack) || Number(before.capturedRevision ?? -1) < finalRevision) {
        stopReason = "inventory-observation-unavailable";
        break;
      }
      if (object(before.companion).locationId !== locationId) { stopReason = "actor-location-changed"; break; }
      const chest = storeItems ? observedChest(before, chestTile) : undefined;
      if (storeItems && !chest) { stopReason = "chest-observation-unavailable"; break; }
      const available = itemCounts(backpack);
      const items = [...requested].flatMap(([itemId, count]) => {
        const remaining = count - (moved.get(itemId) ?? 0);
        const movable = (available.get(itemId) ?? 0) - (keep.get(itemId) ?? 0);
        const take = Math.min(remaining, movable, 999);
        return take > 0 ? [{ itemId, count: take }] : [];
      });
      if (!items.length) {
        stopReason = moved.size === 0 ? "no-observed-targets" : "requested-quantity-unavailable";
        break;
      }
      stopReason = context.workBlock?.() ?? "";
      if (stopReason) break;
      const receipt = await context.native(storeItems ? "deposit-chest" : "ship-items",
        { locationId, ...(storeItems ? { chestTile } : {}), items });
      if (typeof receipt.commandId === "string") receiptIds.push(receipt.commandId);
      finalRevision = Number(receipt.finalWorldRevision ?? finalRevision);
      const blocked = context.workBlock?.();
      if (blocked === "game-changed" || blocked === "recovery-pending" || context.signal.aborted) {
        stopReason = blocked ?? "interrupted";
        break;
      }
      const after = await this.game.observe();
      const afterInventory = object(after.inventory).slots;
      const afterChest = storeItems ? observedChest(after, chestTile) : undefined;
      if (!Array.isArray(afterInventory) || (storeItems && !afterChest) || Number(after.capturedRevision ?? -1) < finalRevision) {
        stopReason = "transfer-observation-unavailable";
        break;
      }
      const settled = itemCounts(afterInventory);
      const previousStock = itemCounts(chest?.contents);
      const currentStock = itemCounts(afterChest?.contents);
      const effects = Array.isArray(receipt.effects) ? receipt.effects.map(object) : [];
      let verified = 0;
      for (const { itemId, count } of items) {
        const nativeCount = effects.filter(effect => effect.state === (storeItems ? "deposited" : "shipped") && effect.itemId === itemId)
          .reduce((sum, effect) => sum + Number((storeItems ? effect.stack : effect.count) ?? 0), 0);
        const removed = (available.get(itemId) ?? 0) - (settled.get(itemId) ?? 0);
        const destinationGain = storeItems ? (currentStock.get(itemId) ?? 0) - (previousStock.get(itemId) ?? 0) : nativeCount;
        const confirmed = Math.min(count, nativeCount, removed, destinationGain);
        if (confirmed > 0) {
          moved.set(itemId, (moved.get(itemId) ?? 0) + confirmed);
          verified += confirmed;
        }
        if (nativeCount !== confirmed || removed !== nativeCount || destinationGain !== nativeCount) {
          stopReason = "transfer-observation-mismatch";
        }
      }
      if (blocked || receipt.terminalState === "cancelled" || receipt.playerActionRequired === true) {
        stopReason = blocked ?? "interrupted";
      } else if (!stopReason && (receipt.terminalState !== "succeeded" || !verified)) {
        stopReason = String(object(receipt.error).code ?? "transfer-no-progress");
      } else if (!stopReason && [...moved.values()].reduce((sum, count) => sum + count, 0) === totalRequested) {
        stopReason = "requested-items-transferred";
      }
    } } catch {
      stopReason = context.workBlock?.() ?? "transfer-observation-or-action-unavailable";
    }
    const completedCount = [...moved.values()].reduce((sum, count) => sum + count, 0);
    return { status: stopReason === "requested-items-transferred" ? "completed" : "stopped", stopReason, completedCount,
      transferredItems: [...moved].map(([itemId, count]) => ({ itemId, count })),
      remainingCount: totalRequested - completedCount, receiptCount: receiptIds.length, receiptIds: receiptIds.slice(-8) };
  }

  private async observeAfterWork(context: ToolContext, location: JsonValue, finalRevision: JsonValue | undefined): Promise<JsonObject> {
    try {
      context.signal.throwIfAborted();
      const snapshot = modelObservation(await this.game.observe());
      const revision = snapshot.capturedRevision;
      const current = this.game.binding;
      if (context.signal.aborted || current?.saveId !== context.binding.saveId || current.gameSessionId !== context.binding.gameSessionId
          || typeof revision !== "number" || (typeof finalRevision === "number" && revision < finalRevision)) {
        return { status: "unavailable" };
      }
      const farming = object(snapshot.farming);
      const ground = farming.location === location && Array.isArray(farming.groundItems) ? {
        locationId: location,
        scope: "near-companion",
        groundItems: farming.groundItems,
        truncated: farming.groundItemsTruncated ?? true,
      } : null;
      const inventory = Array.isArray(object(snapshot.inventory).slots) ? snapshot.inventory : null;
      if (!ground && !inventory) return { status: "unavailable" };
      return { status: ground && inventory ? "observed" : "partial", capturedRevision: revision, ground, inventory };
    } catch {
      return { status: "unavailable" };
    }
  }

  private requireStore(): CompanionStore {
    if (!this.store) throw new Error("当前存档数据尚未打开。");
    return this.store;
  }
}

async function lookupWiki(query: string, signal: AbortSignal): Promise<JsonObject> {
  const base = /[\u3400-\u9fff]/u.test(query) ? "https://wiki.biligame.com/stardewvalley" : "https://stardewvalleywiki.com/mediawiki";
  const parameters = new URLSearchParams({ action: "query", list: "search", srsearch: query, srlimit: "3", format: "json" });
  const response = await fetch(`${base}/api.php?${parameters}`, {
    signal: AbortSignal.any([signal, AbortSignal.timeout(6000)]), redirect: "error",
  });
  if (!response.ok) throw new Error("Wiki 未返回参考内容，请重新查询。");
  const result = object(object(await response.json()).query);
  const rows = Array.isArray(result.search) ? result.search : [];
  return { source: base, searchResults: rows.slice(0, 3).map(row => {
    const item = object(row);
    return { title: String(item.title ?? "").slice(0, 120), snippet: String(item.snippet ?? "").replace(/<[^>]*>/g, "").slice(0, 800) };
  }), evidence: "Wiki 搜索摘要，仅供参考，游戏观察优先。" };
}
