import { object } from "./model.js";
import type { JsonObject, JsonValue } from "./model.js";
import { bindPhasePlan } from "./phase-plan.js";
import type { PhaseBinding, PhaseObservation } from "./phase-plan.js";
import type { PhaseNative } from "./phase-observation.js";

interface Execution {
  observation: PhaseObservation; current: () => PhaseBinding; native: PhaseNative;
  observe: () => Promise<JsonObject>; block: () => string | undefined;
  workflow: (operation: string, params: JsonObject) => Promise<JsonObject>;
  progress: (state: JsonObject) => void;
}
const rows = (value: JsonValue | undefined): JsonObject[] => Array.isArray(value) ? value.map(object) : [];
const tileKey = (value: JsonValue | undefined) => `${object(value).x}:${object(value).y}`;
const counts = (inventory: JsonValue | undefined): Map<string, number> => {
  const result = new Map<string, number>();
  for (const slot of rows(object(inventory).slots)) if (slot.isTool !== true && typeof slot.itemId === "string")
    result.set(slot.itemId, (result.get(slot.itemId) ?? 0) + Number(slot.stack ?? 0));
  return result;
};
class PhaseStop extends Error { constructor(readonly reason: string) { super(reason); } }

/** Interprets a bounded semantic plan; creates only the next immediate native action. */
export class PhaseExecutor {
  async execute(input: JsonObject, env: Execution): Promise<JsonObject> {
    const plan = bindPhasePlan(input, env.observation, env.current());
    const initial = await env.observe();
    const baseline = counts(initial.inventory), acquired = new Map<string, number>(), stored = new Map<string, number>();
    const allowed = new Set<string>(), receipts: string[] = [], stepResults: JsonObject[] = [];
    const names = new Map<string, string>();
    const sourceItems = new Map<string, Set<string>>();
    const productSources = new Set<string>();
    const existingCollected = new Map<string, number>();
    const protectedGround = new Map<string, { itemId: string; count: number }>();
    const ambiguousGains = new Map<string, number>();
    let activeItems = new Set<string>();
    const rememberNames = (snapshot: JsonObject) => { for (const slot of rows(object(snapshot.inventory).slots))
      if (typeof slot.itemId === "string" && typeof slot.displayName === "string") names.set(slot.itemId, slot.displayName); };
    const initialDest = object(env.observation.destinations[plan.destinationRef]);
    const initialStock = rows(initialDest.contents).filter(row => row.itemId === "(O)388").reduce((sum, row) => sum + Number(row.stack ?? 0), 0);
    let chopped = 0, current = initial, stop = "", dryComplete = false;
    let destination = plan.destinationRef;
    const blocks = new Set<string>();
    const failedDestinations = new Set<string>();
    let previousStoreRemainder = "";
    let eligibleItems: Set<string> | undefined;
    const check = () => {
      const reason = env.block();
      if (reason) throw new PhaseStop(reason);
      const identity = env.current();
      if (Object.keys(env.observation.binding).some(key => env.observation.binding[key as keyof PhaseBinding] !== identity[key as keyof PhaseBinding]))
        throw new PhaseStop("phase-context-changed");
    };
    const record = (before: JsonObject, after: JsonObject, itemIds: Set<string>) => {
      const a = counts(before.inventory), b = counts(after.inventory);
      for (const id of itemIds) {
        const gain = (b.get(id) ?? 0) - (a.get(id) ?? 0);
        if (gain > 0) {
          acquired.set(id, Math.max(0, (b.get(id) ?? 0) - (baseline.get(id) ?? 0) + (stored.get(id) ?? 0)
            - (ambiguousGains.get(id) ?? 0) - (existingCollected.get(id) ?? 0)));
          allowed.add(id); activeItems.add(id);
        }
      }
    };
    const native = async (skill: string, params: JsonObject, observation = false): Promise<JsonObject> => {
      check();
      const result = await env.native(skill, params, observation);
      if (typeof result.commandId === "string" && !observation) receipts.push(result.commandId);
      if (!observation && !["succeeded", "partially-succeeded", "failed", "rejected", "cancelled"].includes(String(result.terminalState)))
        throw new PhaseStop("recovery-pending");
      if (result.terminalState === "cancelled") throw new PhaseStop(env.block() ?? "interrupted");
      return result;
    };
    const ground = async (): Promise<JsonObject[]> => {
      const result = await native("inspect-production", { locationId: env.observation.binding.locationId }, true);
      const production = object(object(result.details).production);
      if (result.terminalState !== "succeeded" || !Array.isArray(production.groundItems)
          || production.locationId !== env.observation.binding.locationId || production.groundItemsTruncated !== false)
        throw new PhaseStop("ground-observation-incomplete");
      const items = rows(production.groundItems).filter(item => item.isDropped === true || item.canBeGrabbed === true);
      // A pre-existing drop can be magnet-collected during another action. Its
      // disappearance is not proof that the same-item inventory gain was ours.
      // Keep that amount out of phase production even if another actor took it.
      const missing = new Map<string, number>();
      for (const [source, prior] of protectedGround) {
        const remaining = items.filter(item => item.sourceId === source && item.itemId === prior.itemId)
          .reduce((sum, item) => sum + Number(item.stack ?? 0), 0);
        missing.set(prior.itemId, (missing.get(prior.itemId) ?? 0) + Math.max(0, prior.count - remaining));
      }
      for (const [id, quantity] of missing) ambiguousGains.set(id, Math.max(ambiguousGains.get(id) ?? 0, quantity));
      return items;
    };
    const newProducts = (before: JsonObject[], after: JsonObject[], tiles: JsonObject[]): JsonObject[] => {
      const prior = new Set(before.map(item => item.sourceId));
      return after.filter(item => typeof item.sourceId === "string" && !prior.has(item.sourceId) && tiles.some(tile =>
        Math.abs(Number(object(item.tile).x) - Number(tile.x)) <= 5 && Math.abs(Number(object(item.tile).y) - Number(tile.y)) <= 5));
    };
    const store = async (): Promise<void> => {
      check(); current = await env.observe();
      rememberNames(current);
      const box = env.observation.destinations[destination];
      if (!box) throw new PhaseStop("destination-unavailable");
      if (typeof box.nativeIdentity === "string") {
        const tile = object(box.tile), map = object(env.observation.facts.map);
        const inspection = await native("inspect-phase-map", { locationId: env.observation.binding.locationId,
          scanOffset: Number(tile.y) * Number(map.width) + Number(tile.x), scanLimit: 1 }, true);
        const identity = rows(object(object(inspection.details).page).fixtures).find(fixture => tileKey(fixture.tile) === tileKey(tile))?.identity;
        if (identity !== box.nativeIdentity) throw new PhaseStop("destination-replaced");
      }
      const available = counts(current.inventory);
      // Auto-collection can happen while walking to a stale drop. Reconcile
      // the complete current product inventory before choosing transfer items;
      // the first observation is the only baseline for pre-existing stock.
      for (const id of allowed) {
        const observed = Math.max(0, (available.get(id) ?? 0) - (baseline.get(id) ?? 0)
          - (ambiguousGains.get(id) ?? 0) + (stored.get(id) ?? 0));
        acquired.set(id, observed);
      }
      const items = [...new Set([...acquired.keys(), ...plan.existing.keys()])].flatMap(id => {
        if (eligibleItems && !eligibleItems.has(id) && !plan.existing.has(id)) return [];
        // Original stock is retained unless explicitly selected in this plan.
        const originalReserve = Math.max(0, (baseline.get(id) ?? 0) - (plan.existing.get(id) ?? 0));
        const quantity = Math.min((acquired.get(id) ?? 0) + (plan.existing.get(id) ?? 0) - (stored.get(id) ?? 0),
          (available.get(id) ?? 0) - Math.max(originalReserve, plan.keep.get(id) ?? 0));
        return quantity > 0 ? [{ item_id: id, count: quantity }] : [];
      });
      if (!items.length) return;
      let result = await env.workflow("store_items", { location_id: env.observation.binding.locationId, chest_tile: box.tile!, items });
      for (const transfer of rows(result.transferredItems)) stored.set(String(transfer.itemId),
        (stored.get(String(transfer.itemId)) ?? 0) + Number(transfer.count));
      for (const id of Array.isArray(result.receiptIds) ? result.receiptIds : []) receipts.push(String(id));
      if (result.status !== "completed") {
        failedDestinations.add(destination);
        const alternate = plan.branches.find(branch => branch.on === "destination-unavailable" && branch.destinationRef
          && !failedDestinations.has(branch.destinationRef))?.destinationRef;
        if (alternate && env.observation.destinations[alternate]) {
          destination = alternate;
          await store(); return;
        }
        throw new PhaseStop(String(result.stopReason ?? "destination-unavailable"));
      }
      current = await env.observe();
      const after = counts(current.inventory);
      const remainder = [...allowed].filter(id => (!eligibleItems || eligibleItems.has(id)) && (after.get(id) ?? 0) > Math.max(
        (baseline.get(id) ?? 0) - (plan.existing.get(id) ?? 0), plan.keep.get(id) ?? 0))
        .map(id => `${id}:${after.get(id)}`).join("|");
      if (remainder) {
        if (remainder === previousStoreRemainder) throw new PhaseStop("storage-no-progress");
        previousStoreRemainder = remainder; await store();
      }
    };
    const ensureSpace = async (): Promise<void> => {
      current = await env.observe();
      if (Number(object(current.inventory).freeSlots ?? 0) >= 2) return;
      if (!plan.branches.some(branch => branch.on === "inventory-full" && branch.action === "store-and-resume"))
        throw new PhaseStop("inventory-full");
      await store();
      if (Number(object(current.inventory).freeSlots ?? 0) < 2) throw new PhaseStop("inventory-full");
    };
    const collect = async (): Promise<void> => {
      const attempted = new Set<string>();
      for (;;) {
        check();
        const observed = await ground();
        const isProduct = (item: JsonObject) => (!eligibleItems || eligibleItems.has(String(item.itemId)))
          && allowed.has(String(item.itemId)) && typeof item.sourceId === "string" && productSources.has(item.sourceId);
        const existingBudget = (id: string) => Math.max(0, (plan.existing.get(id) ?? 0) - (baseline.get(id) ?? 0) - (existingCollected.get(id) ?? 0));
        const authorized = (item: JsonObject) => isProduct(item) || (Number(item.stack ?? 1) > 0
          && Number(item.stack ?? 1) <= existingBudget(String(item.itemId)));
        const candidates = observed.filter(authorized);
        if (!candidates.length) return;
        await ensureSpace();
        const actor = object(current.companion);
        candidates.sort((a, b) => distance(actor, object(a.tile)) - distance(actor, object(b.tile)));
        const attemptKey = (item: JsonObject) => `${item.sourceId}:${item.itemId}:${tileKey(item.tile)}:${item.stack}:${counts(current.inventory).get(String(item.itemId)) ?? 0}`;
        const target = candidates.find(item => !attempted.has(attemptKey(item)));
        if (!target) throw new PhaseStop("collection-no-progress");
        // Native pickup targets a tile. Do not use a valid product to authorize
        // another pre-existing or foreign item that happens to share that tile.
        if (observed.some(item => tileKey(item.tile) === tileKey(target.tile) && !authorized(item)))
          throw new PhaseStop("product-source-unconfirmed");
        const existingOnTile = observed.filter(item => tileKey(item.tile) === tileKey(target.tile) && !isProduct(item));
        for (const id of new Set(existingOnTile.map(item => String(item.itemId))))
          if (existingOnTile.filter(item => item.itemId === id).reduce((sum, item) => sum + Number(item.stack ?? 1), 0) > existingBudget(id))
            throw new PhaseStop("existing-material-quantity-exceeded");
        attempted.add(attemptKey(target));
        const before = await env.observe();
        const result = await native("pickup-items", { locationId: env.observation.binding.locationId, tiles: [target.tile!] });
        if (result.playerActionRequired === true) throw new PhaseStop("player-action-required");
        current = await env.observe();
        record(before, current, new Set(allowed));
        for (const id of new Set(existingOnTile.map(item => String(item.itemId)))) {
          const gain = Math.max(0, (counts(current.inventory).get(id) ?? 0) - (counts(before.inventory).get(id) ?? 0));
          existingCollected.set(id, (existingCollected.get(id) ?? 0) + Math.min(gain, existingBudget(id)));
        }
        const beforeCount = counts(before.inventory).get(String(target.itemId)) ?? 0;
        const afterCount = counts(current.inventory).get(String(target.itemId)) ?? 0;
        if (afterCount <= beforeCount && !(await ground()).some(item => item.itemId === target.itemId && item.sourceId === target.sourceId)) {
          // Magnet pickup can occur between the ground query and the pre-action
          // inventory snapshot. Reconcile the unaccounted real gain instead of
          // mistaking a rejected stale pickup for a missing product.
          current = await env.observe();
          const id = String(target.itemId);
          const unaccounted = (counts(current.inventory).get(id) ?? 0) - (baseline.get(id) ?? 0)
            + (stored.get(id) ?? 0) - (acquired.get(id) ?? 0) - (existingCollected.get(id) ?? 0);
          if (unaccounted < Number(target.stack ?? 1)) throw new PhaseStop("product-collection-unconfirmed");
          const gain = Math.min(unaccounted, Number(target.stack ?? 1));
          if (isProduct(target)) { acquired.set(id, (acquired.get(id) ?? 0) + gain); activeItems.add(id); }
          else existingCollected.set(id, (existingCollected.get(id) ?? 0) + gain);
        }
        if (["failed", "rejected"].includes(String(result.terminalState)) && object(result.error).code !== "no-item") {
          if (plan.branches.some(branch => branch.on === "target-unreachable" && branch.action === "skip-target")) continue;
          throw new PhaseStop(String(object(result.error).code ?? "pickup-failed"));
        }
      }
    };
    const woodSatisfied = () => plan.metric === "wood-stock-at-least"
      ? initialStock + Math.min(baseline.get("(O)388") ?? 0, plan.existing.get("(O)388") ?? 0) + (acquired.get("(O)388") ?? 0) >= plan.quantity
      : plan.metric === "new-wood-stored" && (acquired.get("(O)388") ?? 0) >= plan.quantity;
    try {
      check();
      for (const item of await ground()) if (typeof item.sourceId === "string" && !plan.existing.has(String(item.itemId))) {
        const prior = protectedGround.get(item.sourceId);
        protectedGround.set(item.sourceId, { itemId: String(item.itemId), count: (prior?.count ?? 0) + Number(item.stack ?? 1) });
      }
      // Recheck the map header once before executing a plan assembled before a model wait.
      const header = await native("inspect-phase-map", { locationId: env.observation.binding.locationId, scanOffset: 0, scanLimit: 0 }, true);
      if (object(object(header.details).page).fingerprint !== object(env.observation.facts.map).fingerprint)
        throw new PhaseStop("phase-map-changed");
      for (const step of plan.steps) {
        const dependencies = new Set(step.inputs.flatMap(id => [...(sourceItems.get(id) ?? [])]));
        activeItems = new Set(dependencies);
        eligibleItems = ["collect-products", "store-products"].includes(step.operation) ? dependencies : undefined;
        check(); env.progress({ objective: plan.objective, step: step.operation, status: "executing", chopped,
          acquiredItems: [...acquired].map(([itemId, count]) => ({ itemId, count })) });
        if (step.operation === "chop-trees") {
          const pending = rows(env.observation.facts.trees).slice();
          while (pending.length && (plan.metric !== "trees-stored" || chopped < plan.quantity) && !woodSatisfied()) {
            check(); await ensureSpace(); current = await env.observe();
            if (Number(object(current.companion).stamina ?? 0) < plan.staminaFloor + 30) throw new PhaseStop("stamina-reserve");
            const actor = object(current.companion);
            pending.sort((a, b) => distance(actor, object(a.tile)) - distance(actor, object(b.tile)));
            // Compare actual interaction-route costs for the nearest few candidates.
            // Native route search is bounded; failure of one tree does not block the map.
            const options: { tree: JsonObject; cost: number }[] = [];
            for (const tree of pending.slice(0, 4)) {
              const t = object(tree.tile);
              const standing = [[0, 1], [0, -1], [-1, 0], [1, 0]].map(([x, y]) => ({ x: Number(t.x) + x!, y: Number(t.y) + y! }))
                .filter(tile => tile.x >= 0 && tile.y >= 0).sort((a, b) => distance(actor, a) - distance(actor, b));
              let reached = false;
              for (const tile of standing) {
                const query = await native("inspect-route", { locationId: env.observation.binding.locationId, tile }, true);
                const route = object(object(query.details).route);
                if (query.terminalState === "succeeded" && route.reachable === true) {
                  options.push({ tree, cost: Number(route.walkingTiles ?? distance(actor, tile)) }); reached = true; break;
                }
              }
              if (!reached) {
                pending.splice(pending.indexOf(tree), 1); blocks.add("target-unreachable");
                if (!plan.branches.some(branch => branch.on === "target-unreachable" && branch.action === "skip-target"))
                  throw new PhaseStop("target-unreachable");
              }
            }
            if (!options.length) continue;
            options.sort((a, b) => a.cost - b.cost);
            const candidate = options[0]!.tree, tile = object(candidate.tile);
            pending.splice(pending.indexOf(candidate), 1);
            // A one-tile fresh native read preserves protected trees after a player edit.
            const map = object(env.observation.facts.map);
            const inspection = await native("inspect-phase-map", { locationId: env.observation.binding.locationId,
              scanOffset: Number(tile.y) * Number(map.width) + Number(tile.x), scanLimit: 1 }, true);
            const live = rows(object(object(inspection.details).page).trees).find(tree => tileKey(tree.tile) === tileKey(tile));
            if (!live || live.kind !== "tree" || Number(live.growthStage) < 5 || live.tapped === true) continue;
            const before = await env.observe(), beforeDrops = await ground();
            const result = await native("chop-tree", { locationId: env.observation.binding.locationId, tiles: [tile] });
            current = await env.observe();
            if (Number(result.completedCount) > 0) {
              chopped++;
              const drops = await ground();
              const produced = newProducts(beforeDrops, drops, [tile]);
              produced.forEach(item => productSources.add(String(item.sourceId)));
              const productIds = new Set(produced.map(item => String(item.itemId)));
              // Native auto pickup may precede the first debris observation.
              for (const id of ["(O)388", "(O)92", "(O)309", "(O)310", "(O)311"]) productIds.add(id);
              productIds.forEach(id => { allowed.add(id); activeItems.add(id); });
              record(before, current, productIds);
              await collect(); // Count actual material before choosing another tree.
            } else {
              const reason = String(object(result.error).code ?? "tree-no-progress"); blocks.add(reason);
              if (!["unreachable", "no-tree", "NO_TREE", "ROUTE_PLANNING_TIMEOUT"].includes(reason)
                || !plan.branches.some(branch => branch.on === "target-unreachable" && branch.action === "skip-target")) throw new PhaseStop(reason);
            }
          }
        } else if (step.operation === "collect-products") await collect();
        else if (step.operation === "store-products") await store();
        else {
          const before = await env.observe();
          const beforeGround = step.operation === "harvest-crops" ? await ground() : [];
          const mature = rows(object(before.farmWork).matureCrops);
          const result = await env.workflow(step.operation === "tend-crops" ? "tend_crops" : "harvest_crops", { location_id: env.observation.binding.locationId });
          current = await env.observe();
          for (const id of Array.isArray(result.receiptIds) ? result.receiptIds : []) receipts.push(String(id));
          if (step.operation === "tend-crops") dryComplete = result.status === "completed" || result.stopReason === "no-observed-targets";
          for (const item of rows(result.inventoryGains)) { allowed.add(String(item.itemId)); activeItems.add(String(item.itemId)); }
          if (step.operation === "harvest-crops") {
            for (const item of newProducts(beforeGround, await ground(), mature.map(tile => ({ x: tile.x!, y: tile.y! })))) {
              productSources.add(String(item.sourceId)); allowed.add(String(item.itemId)); activeItems.add(String(item.itemId));
            }
          }
          record(before, current, allowed);
          if (result.status !== "completed" && result.stopReason !== "no-observed-targets") throw new PhaseStop(String(result.stopReason));
        }
        sourceItems.set(step.id, activeItems);
        stepResults.push({ id: step.id, operation: step.operation, status: "completed" });
      }
      current = await env.observe();
      const unstored = [...acquired].some(([id, quantity]) => (stored.get(id) ?? 0) < Math.max(0, quantity - (plan.keep.get(id) ?? 0)));
      const missingExisting = [...plan.existing].some(([id, quantity]) => (stored.get(id) ?? 0) < quantity);
      const observedBox = rows(object(current.chests).items).find(box => tileKey(box.tile) === tileKey(env.observation.destinations[destination]?.tile));
      const confirmedWoodStock = observedBox?.contentsObserved === true && Array.isArray(observedBox.contents)
        ? rows(observedBox.contents).filter(item => item.itemId === "(O)388").reduce((sum, item) => sum + Number(item.stack ?? 0), 0) : undefined;
      const achieved = plan.metric === "trees-stored" ? chopped >= plan.quantity && (stored.get("(O)388") ?? 0) > 0
        : plan.metric === "new-wood-stored" ? (stored.get("(O)388") ?? 0) >= plan.quantity
        : plan.metric === "wood-stock-at-least" ? confirmedWoodStock !== undefined && confirmedWoodStock >= plan.quantity : dryComplete;
      if (!achieved || unstored || missingExisting) stop = "phase-objective-unmet";
    } catch (error) { stop = error instanceof PhaseStop ? error.reason : env.block() || "phase-action-or-observation-unavailable"; }
    const status = !stop ? "completed" : stop === "recovery-pending" ? "recovery-pending"
      : ["interrupted", "player-update", "phase-context-changed", "rest-or-new-day", "game-changed"].includes(stop) ? "interrupted" : "stopped";
    const result: JsonObject = { status, objective: plan.objective, metric: plan.metric, requestedQuantity: plan.quantity,
      stopReason: stop || "phase-objective-achieved", completedCount: chopped, executionModelRequests: 0,
      remainingQuantity: !stop ? 0 : plan.metric === "trees-stored" ? Math.max(0, plan.quantity - chopped)
        : plan.metric === "new-wood-stored" ? Math.max(0, plan.quantity - (stored.get("(O)388") ?? 0)) : null,
      acquiredItems: [...acquired].map(([itemId, count]) => ({ itemId, count })),
      storedItems: [...stored].map(([itemId, count]) => ({ itemId, count, displayName: names.get(itemId) ?? "材料" })),
      steps: stepResults, blockedBranches: [...blocks], receiptIds: receipts.slice(-16), receiptCount: receipts.length,
      needsReconciliation: status === "recovery-pending" };
    env.progress(result); return result;
  }
}

function distance(actor: JsonObject, tile: JsonObject): number {
  const x = Math.abs(Number(actor.tileX ?? 0) - Number(tile.x)), y = Math.abs(Number(actor.tileY ?? 0) - Number(tile.y));
  return Math.max(x, y) + Math.min(x, y) * 0.414;
}
