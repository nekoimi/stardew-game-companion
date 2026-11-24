import type { JsonObject, JsonValue } from "../application/model.js";
import { object } from "../application/model.js";

type Schema = JsonObject;
const text = (maxLength = 100): Schema => ({ type: "string", minLength: 1, maxLength });
const integer = (minimum: number, maximum: number): Schema => ({ type: "integer", minimum, maximum });
const list = (items: Schema, maxItems = 100): Schema => ({ type: "array", minItems: 1, maxItems, items });
export const fields = (properties: Record<string, Schema>, required = Object.keys(properties)): Schema =>
  ({ type: "object", properties, required, additionalProperties: false });
export const oneOperation = (operations: Record<string, Schema>): Schema => ({
  ...fields(operations, []), minProperties: 1, maxProperties: 1,
});
const bool: Schema = { type: "boolean" };
const tile = fields({ x: integer(0, 4096), y: integer(0, 4096) });
const tiles = list(tile);
const location = { location_id: text() };
const tileFields = { ...location, tiles };
const chest = { ...location, chest_tile: tile };
const items = list(fields({ item_id: text(), count: integer(1, 999999) }), 36);
const region = fields({ x: integer(0, 4096), y: integer(0, 4096), width: integer(1, 512), height: integer(1, 512) });
const withLocation = (properties: Record<string, Schema>, required = Object.keys(properties)) =>
  fields({ ...location, ...properties }, required);

export const actions: Record<string, [string, Schema]> = {
  navigate_to: ["navigate-to", withLocation({ tile }, ["location_id", "tile"])],
  water_tiles: ["water-zone", fields({ ...tileFields, include_empty_tiles: bool }, ["tiles"])],
  water_auto: ["water-zone", withLocation({ max_tiles: integer(1, 100), target_tiles: tiles }, [])],
  refill_watering_can: ["refill-watering-can", withLocation({ tiles }, [])],
  harvest_auto: ["harvest-zone", withLocation({ max_tiles: integer(1, 100), target_tiles: tiles }, [])],
  harvest_tiles: ["harvest-zone", fields(tileFields, ["tiles"])],
  clear_debris: ["clear-debris", fields(tileFields, ["tiles"])],
  cut_grass: ["cut-grass", fields(tileFields, ["tiles"])],
  chop_tree: ["chop-tree", fields(tileFields, ["tiles"])],
  pickup_items: ["pickup-items", fields(tileFields, ["tiles"])],
  hoe_tiles: ["hoe-tiles", fields({ ...tileFields, include_empty_tiles: bool }, ["tiles"])],
  plant_seeds: ["plant-seeds", fields({ ...tileFields, seed_item_id: text() }, ["tiles", "seed_item_id"])],
  apply_fertilizer: ["apply-fertilizer", fields({ ...tileFields, fertilizer_item_id: text() }, ["tiles", "fertilizer_item_id"])],
  deposit_to_chest: ["deposit-chest", fields({ ...chest, item_ids: list(text(), 36), items }, ["chest_tile"])],
  withdraw_from_chest: ["withdraw-chest", fields({ ...chest, items }, ["chest_tile", "items"])],
  organize_chest: ["organize-chest", fields(chest, ["chest_tile"])],
  open_chest: ["open-chest", fields(chest, ["chest_tile"])],
  ship_items: ["ship-items", withLocation({ items })],
  rest: ["rest", fields({ target_stamina: { type: "number", minimum: 1, maximum: 1000 } })],
  sleep: ["sleep", fields({})],
  place_items: ["place-items", fields({ ...tileFields, item_id: text() }, ["tiles", "item_id"])],
  remove_items: ["remove-items", fields({ ...tileFields, item_id: text() }, ["tiles", "item_id"])],
  craft_items: ["craft-items", withLocation({ recipe_name: text(), item_count: integer(1, 64) }, ["recipe_name"])],
  eat_food: ["eat-food", withLocation({ item_id: text() })],
  open_shop: ["open-shop", withLocation({ shop_id: text() })],
  purchase_items: ["purchase-items", withLocation({ shop_id: text(), items, budget_limit: integer(0, 99999999) })],
  build_building: ["build-building", withLocation({ building_type: text(), tile, budget_limit: integer(0, 99999999) })],
  upgrade_building: ["upgrade-building", withLocation({ building_name: text(), building_type: text(),
    budget_limit: integer(0, 99999999) })],
  move_building: ["move-building", withLocation({ building_name: text(), tile })],
  purchase_animal: ["purchase-animal", withLocation({ building_name: text(), animal_type: text(), animal_name: text(),
    budget_limit: integer(0, 99999999) })],
  insert_machine: ["insert-machine", withLocation({ tile, item_id: text(), item_count: integer(1, 999) }, ["tile", "item_id"])],
  collect_machine: ["collect-machine", fields(tileFields, ["tiles"])],
  pet_animal: ["pet-animal", withLocation({ tiles, animal_name: text() })],
  collect_animal_produce: ["collect-animal-produce", withLocation({ tiles, animal_name: text() })],
  feed_animals: ["feed-animals", withLocation({ building_name: text() })],
  toggle_animal_door: ["toggle-animal-door", withLocation({ tiles })],
  talk_npc: ["talk-npc", withLocation({ npc_name: text() }, ["location_id", "npc_name"])],
  interact_tile: ["interact-tile", withLocation({ tile }, ["location_id", "tile"])],
  attack: ["attack", withLocation({ facing: integer(0, 3) }, ["location_id", "facing"])],
  fish: ["fish", withLocation({ facing: integer(0, 3) }, ["location_id", "facing"])],
  native_menu_select: ["native-menu-select", withLocation({ choice_index: integer(0, 255), menu_text: text(120) }, ["location_id", "choice_index"])],
  native_advance: ["native-advance", fields({ allow_skip: bool }, [])],
  native_answer: ["native-answer", fields({ choice_index: integer(0, 255) })],
};

Object.assign(actions.water_tiles[1], { description: "Water observed crop tiles. Set include_empty_tiles=true only when the player explicitly asks to water empty tilled soil." });
Object.assign(actions.water_auto[1], { description: "Use observed dry crop tiles only, optionally a subset. Refill separately if water is insufficient." });
Object.assign(actions.harvest_auto[1], { description: "Use observed mature crops. Pick up loose drops separately." });
Object.assign(actions.chop_tree[1], { description: "Prefer observed mature wild trees. Preserve saplings and fruit trees." });
Object.assign(actions.pickup_items[1], { description: "Collect existing loose drops or directly collectible ground items." });
Object.assign(actions.clear_debris[1], { description: "Clear explicitly requested or obstructing debris, preserving valuable objects and crops." });
Object.assign(actions.rest[1], { description: "Use the native bed until the companion reaches the requested stamina, then get up." });

export const queries: Record<string, [string, Schema]> = {
  query_route: ["inspect-route", withLocation({ tile }, ["location_id", "tile"])],
  observe_farm_space: ["inspect-location", fields({ ...location, region }, ["location_id"])],
  query_planting_options: ["inspect-planting", fields({ ...location, region }, ["location_id"])],
  observe_production: ["inspect-production", fields(location)],
  observe_machines: ["inspect-machines", fields(location)],
  observe_livestock: ["inspect-livestock", fields(location)],
  observe_building_services: ["inspect-building-services", fields(location)],
  observe_crafting: ["inspect-crafting", fields(location, [])],
  observe_player_activities: ["inspect-player-activities", fields({})],
  query_shop: ["inspect-shop", withLocation({ shop_id: text() })],
};

export function validate(schema: Schema, value: JsonValue | undefined, path = "参数"): void {
  const invalid = () => { throw new Error(`${path}不符合工具定义。`); };
  if (schema.type === "object") {
    if (!value || typeof value !== "object" || Array.isArray(value)) return invalid();
    const props = object(schema.properties);
    const required = Array.isArray(schema.required) ? schema.required : [];
    const keys = Object.keys(value);
    if ((typeof schema.minProperties === "number" && keys.length < schema.minProperties)
      || (typeof schema.maxProperties === "number" && keys.length > schema.maxProperties)
      || required.some(key => !Object.hasOwn(value, String(key)))) return invalid();
    for (const [key, child] of Object.entries(value)) {
      if (!Object.hasOwn(props, key)) return invalid();
      validate(object(props[key]), child, `${path}.${key}`);
    }
  } else if (schema.type === "array") {
    if (!Array.isArray(value) || value.length < Number(schema.minItems) || value.length > Number(schema.maxItems)) return invalid();
    for (const child of value) validate(object(schema.items), child, path);
  } else if (schema.type === "string") {
    if (typeof value !== "string" || value.trim().length < Number(schema.minLength) || value.length > Number(schema.maxLength)) return invalid();
  } else if (schema.type === "integer" || schema.type === "number") {
    if (typeof value !== "number" || !Number.isFinite(value) || (schema.type === "integer" && !Number.isSafeInteger(value))
      || value < Number(schema.minimum) || value > Number(schema.maximum)) return invalid();
  } else if (schema.type === "boolean") {
    if (typeof value !== "boolean") return invalid();
  } else invalid();
}

export function nativeParameters(args: JsonObject): JsonObject {
  return Object.fromEntries(Object.entries(args).map(([name, value]) => [
    name.replace(/_([a-z])/g, (_, letter: string) => letter.toUpperCase()),
    Array.isArray(value)
      ? value.map(item => item && typeof item === "object" && !Array.isArray(item) ? nativeParameters(item) : item)
      : value && typeof value === "object" ? nativeParameters(value) : value,
  ]));
}
