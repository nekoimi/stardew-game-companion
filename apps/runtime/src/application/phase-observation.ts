import { randomUUID } from "node:crypto";
import { object } from "./model.js";
import type { JsonObject } from "./model.js";
import type { GameConnection } from "../adapters/game/game-connection.js";
import type { CompanionStore } from "../adapters/storage/companion-store.js";
import type { PhaseBinding, PhaseObservation } from "./phase-plan.js";

export type PhaseNative = (skill: string, params: JsonObject, observation?: boolean) => Promise<JsonObject>;
export type PhaseMap = JsonObject & { trees: JsonObject[]; waterTiles: JsonObject[]; fixtures: JsonObject[] };

/** Cache only derived map facts. Every reuse first checks an authoritative header. */
export class PhaseObserver {
  private readonly cache = new Map<string, PhaseMap>();
  constructor(private readonly game: GameConnection, private readonly store?: CompanionStore,
    private readonly metrics?: (event: string, details: JsonObject) => void) {}

  async map(binding: PhaseBinding, native: PhaseNative, check: () => void, force = false): Promise<PhaseMap> {
    check();
    const key = JSON.stringify([binding.saveId, binding.actorMode, binding.locationId]);
    const inspect = async (offset: number, limit: number): Promise<JsonObject> => {
      check();
      const result = await native("inspect-phase-map", { locationId: binding.locationId, scanOffset: offset, scanLimit: limit }, true);
      check();
      if (result.terminalState !== "succeeded") throw new Error("阶段地图观察不可用，不能推断地图为空。");
      const page = object(object(result.details).page);
      if (page.locationId !== binding.locationId || typeof page.fingerprint !== "string" || !page.fingerprint
          || !Number.isSafeInteger(page.width) || !Number.isSafeInteger(page.height) || Number(page.width) < 1 || Number(page.height) < 1
          || page.offset !== offset || !Number.isSafeInteger(page.nextOffset) || !Number.isSafeInteger(page.end)
          || !Array.isArray(page.trees) || !Array.isArray(page.waterTiles) || !Array.isArray(page.fixtures))
        throw new Error("阶段地图返回不完整或格式错误。");
      return page;
    };
    const header = await inspect(0, 0);
    const cached = this.cache.get(key) ?? this.store?.mapCache(binding.saveId, binding.actorMode, binding.locationId);
    const valid = !force && cached && cached.fingerprint === header.fingerprint && cached.width === header.width && cached.height === header.height
      && cached.complete === true && [cached.trees, cached.waterTiles, cached.fixtures].every(Array.isArray);
    if (valid) {
      this.metrics?.("phase_map_cache", { hit: true, scannedTiles: 0 });
      return cached as PhaseMap;
    }
    const map: PhaseMap = { ...header, trees: [], waterTiles: [], fixtures: [], complete: false };
    let offset = 0, scanned = 0;
    const size = Number(header.width) * Number(header.height);
    do {
      const page = await inspect(offset, 512);
      if (page.fingerprint !== header.fingerprint || page.width !== header.width || page.height !== header.height)
        throw new Error("扫描期间地图已变化，请重新准备阶段观察。");
      const end = Number(page.end);
      if (end <= offset || end > size || end - offset > 512 || (page.nextOffset !== -1 && page.nextOffset !== end)
          || page.complete !== (end === size) || (page.nextOffset === -1) !== (end === size))
        throw new Error("地图分页未推进或完整性矛盾。");
      map.trees.push(...page.trees as JsonObject[]); map.waterTiles.push(...page.waterTiles as JsonObject[]);
      map.fixtures.push(...page.fixtures as JsonObject[]); scanned += end - offset;
      offset = end;
    } while (offset < size);
    map.complete = true;
    this.cache.set(key, map);
    this.store?.saveMapCache(binding.saveId, binding.actorMode, binding.locationId, map);
    this.metrics?.("phase_map_cache", { hit: false, scannedTiles: scanned });
    return map;
  }

  async prepare(binding: PhaseBinding, native: PhaseNative, check: () => void): Promise<PhaseObservation> {
    const map = await this.map(binding, native, check);
    check();
    const snapshot = await this.game.observe();
    check();
    if (object(snapshot.companion).locationId !== binding.locationId) throw new Error("角色地图已经变化。");
    const productionResult = await native("inspect-production", { locationId: binding.locationId }, true);
    check();
    const production = object(object(productionResult.details).production);
    const groundObserved = productionResult.terminalState === "succeeded" && production.locationId === binding.locationId
      && production.groundItemsTruncated === false && Array.isArray(production.groundItems);
    const ground = groundObserved ? production.groundItems as JsonObject[] : [];
    const existingGround = new Map<string, { itemId: string; count: number; displayName: string }>();
    for (const item of ground) if ((item.isDropped === true || item.canBeGrabbed === true) && typeof item.itemId === "string") {
      const row = existingGround.get(item.itemId) ?? { itemId: item.itemId, count: 0, displayName: String(item.name ?? item.itemId) };
      row.count += Number(item.stack ?? 1); existingGround.set(item.itemId, row);
    }
    const destinations: Record<string, JsonObject> = {};
    const chests = object(snapshot.chests);
    for (const [index, value] of (Array.isArray(chests.items) ? chests.items : []).entries()) {
      const chest = object(value);
      if (binding.locationId === "Farm" && chest.contentsObserved === true && Array.isArray(chest.contents))
        destinations[`chest-${index + 1}`] = { ...chest, nativeIdentity: object(map.fixtures.find(fixture =>
          fixture.kind === "chest" && object(fixture.tile).x === object(chest.tile).x && object(fixture.tile).y === object(chest.tile).y)).identity ?? null };
    }
    const trees = map.trees.filter(tree => tree.kind === "tree" && Number(tree.growthStage) >= 5 && tree.tapped !== true);
    return { ref: randomUUID(), binding: { ...binding }, revision: Number(snapshot.capturedRevision ?? 0),
      complete: map.complete === true, scopeRef: "current-map", destinations,
      facts: { locationId: binding.locationId, map, trees, world: snapshot.world ?? {}, companion: snapshot.companion ?? {},
        inventory: snapshot.inventory ?? {}, farmWork: snapshot.farmWork ?? {}, chests: destinations,
        summary: { matureTrees: trees.length, waterSources: map.waterTiles.length, destinationRefs: Object.keys(destinations),
          existingGround: [...existingGround.values()], existingGroundStatus: groundObserved ? "complete" : "unknown",
          originalBackpack: (Array.isArray(object(snapshot.inventory).slots) ? object(snapshot.inventory).slots as JsonObject[] : [])
            .filter(slot => slot.isTool !== true).map(slot => ({ itemId: slot.itemId ?? null, count: slot.stack ?? 0,
              displayName: slot.displayName ?? slot.name ?? null })),
          note: "Select player-authorized originalBackpack and existingGround materials in constraints.include_existing in this same phase. Unselected existing materials are retained. Unknown ground is not empty." } } };
  }
}
