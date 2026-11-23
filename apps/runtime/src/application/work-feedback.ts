import { object } from "./model.js";
import type { JsonObject, JsonValue } from "./model.js";

/** Evidence for presentation, never a replacement for the original game receipt. */
export function actionEvidence(receipt: JsonObject): JsonObject {
  const effects = Array.isArray(receipt.effects) ? receipt.effects.map(object) : [];
  const quantities: JsonObject[] = [];
  const add = (kind: string, value: number) => {
    if (Number.isSafeInteger(value) && value > 0) quantities.push({ kind, value });
  };
  const states: Record<string, [string, string[]]> = {
    "water-zone": ["watered-tiles", ["watered"]],
    "harvest-zone": ["harvested-tiles", ["harvested"]],
    "hoe-tiles": ["hoed-tiles", ["hoed"]],
    "plant-seeds": ["planted-tiles", ["planted"]],
    "chop-tree": ["felled-trees", ["chopped-tree"]],
  };
  const counted = states[String(receipt.skillId)];
  if (counted) add(counted[0], effects.filter(effect => counted[1].includes(String(effect.state))).length);
  const amount = (value: JsonValue | undefined) => typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : 0;
  const transfer = { "deposit-chest": "deposited", "withdraw-chest": "withdrawn", "ship-items": "shipped" }[String(receipt.skillId)];
  if (transfer) add(transfer + "-items", effects.filter(effect => effect.state === transfer)
    .reduce((sum, effect) => sum + amount(effect.stack), 0));
  const details = object(receipt.details);
  if (["pickup-items", "harvest-zone", "chop-tree", "clear-debris", "cut-grass"].includes(String(receipt.skillId))) {
    if (details.inventoryDeltaObserved === true && Array.isArray(details.inventoryDelta)) {
      // This is the measured command interval; do not add collectedItems a second time.
      add("inventory-gained", details.inventoryDelta.map(object).reduce((sum, row) => sum + amount(row.delta), 0));
    } else if (receipt.skillId === "pickup-items") {
      add("collected-items", effects.filter(effect => effect.state === "picked-up").reduce((sum, effect) =>
        sum + (Array.isArray(effect.collectedItems)
          ? effect.collectedItems.map(object).reduce((count, item) => count + amount(item.stack), 0) : amount(effect.stack)), 0));
    }
  }
  return { quantities };
}
