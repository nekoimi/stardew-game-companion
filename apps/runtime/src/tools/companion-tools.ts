import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { GameConnection } from "../adapters/game/game-connection.js";
import type { GameBinding } from "../adapters/game/action-receipt.js";
import type { CompanionStore } from "../adapters/storage/companion-store.js";
import { object } from "../application/model.js";
import type { JsonObject, JsonValue, ToolDefinition } from "../application/model.js";
import { actions, fields, nativeParameters, oneOperation, queries, validate } from "./native-tools.js";

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
    next_day: { type: "boolean" } }, ["status", "reason"]),
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
  { name: "submit_work", description: "Run one native action immediately. Use observed targets and wait for its native result.",
    parameters: fields({ action: oneOperation(operationSchemas(actions)) }) },
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
  speak: (text: string) => void;
  changed: () => void;
  pluginArrangement?: (operation: string, params: JsonObject) => JsonObject;
};

export function gameDate(snapshot: JsonObject | undefined): string {
  const world = object(snapshot?.world);
  return typeof world.year === "number" && typeof world.season === "string" && typeof world.dayOfMonth === "number"
    ? `${world.year}:${world.season}:${world.dayOfMonth}` : "";
}

function collectible(value: JsonValue): boolean {
  const item = object(value);
  return item.isDropped === true || item.canBeGrabbed === true;
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
          if (!native.tiles.length) return { terminalState: "no-observed-targets", observedTargets: 0 };
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
          if (params.status === "wait" && params.next_time === undefined && params.next_day !== true) {
            throw new Error("等待需要真实的游戏时间或下一天条件。");
          }
          result = store.updateItem(save, "goals", context.goalId,
            { text: goal?.text ?? params.reason, requestId: context.requestId, status: "active",
              continuation: params.status, summary: params.reason, wakeTime: params.next_time ?? null,
              waitNextDay: params.next_day === true, updatedDay: gameDate(this.game.snapshot) });
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
