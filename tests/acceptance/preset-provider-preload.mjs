import { createMockDeepSeek, redirectDeepSeek } from "../../apps/runtime-ts/tests/mock-deepseek.mjs";

const work = (name, params) => ({ name: "submit_work", args: { action: { [name]: params } } });
const observe = { name: "get_status", args: {} };
const parse = value => { try { return JSON.parse(value); } catch { return {}; } };
const toolName = message => message?.tool_calls?.[0]?.function?.name;
const operation = message => Object.keys(parse(message?.tool_calls?.[0]?.function?.arguments).action ?? {})[0];

function plan({ messages }) {
  const firstTool = messages.findIndex(row => row.role === "tool");
  const before = firstTool < 0 ? messages : messages.slice(0, firstTool);
  const request = String(before.findLast(row => row.role === "user")?.content ?? "");
  if (firstTool < 0) return observe;
  const lastCall = messages.findLast(row => row.tool_calls?.length);
  if (toolName(lastCall) !== "get_status") {
    const lastResult = parse(messages.findLast(row => row.role === "tool")?.content);
    const action = operation(lastCall);
    // Walking can collect every drop before the explicit pickup reaches it.
    // Read the actual backpack before deciding whether there is work left.
    if (action === "pickup_items" && lastResult.error?.code === "no-item") return observe;
    if (["failed", "cancelled", "rejected"].includes(lastResult.terminalState) || typeof lastResult.error === "string") return {};
    if (["deposit_to_chest", "ship_items"].includes(action)) return {};
    if (action === "withdraw_from_chest") {
      const items = parse(lastCall.tool_calls[0].function.arguments).action.withdraw_from_chest.items;
      return work("ship_items", { items });
    }
    return observe;
  }
  const observed = messages.filter(row => row.role === "tool").map(row => parse(row.content)).filter(row => row.world);
  const { companion, farmWork, farming, chests, inventory } = observed.at(-1).world;
  const previous = messages.filter(row => row.tool_calls?.length).map(operation);
  const position = { x: companion.tileX, y: companion.tileY };
  const farm = { location_id: "Farm" };
  const localChests = chests?.items ?? [];
  const empty = localChests.filter(chest => chest.contentsObserved && chest.contents?.length === 0).sort((a, b) =>
    (a.tile.x - position.x) ** 2 + (a.tile.y - position.y) ** 2 - ((b.tile.x - position.x) ** 2 + (b.tile.y - position.y) ** 2));
  const box = request.includes("取出来") ? localChests.find(chest => chest.contents?.some(item => item.itemId === "(O)24")) : empty[0];
  if (request.includes("浇完水")) {
    if (!farmWork?.cropUnwateredTiles?.length) return {};
    if (!previous.includes("refill_watering_can") && companion.waterCanLevel < farmWork.cropUnwateredTiles.length) {
      return work("refill_watering_can", farm);
    }
    return previous.includes("water_auto") ? {} : work("water_auto", farm);
  }
  if (request.includes("取出来")) {
    const crop = box?.contents?.find(item => item.itemId === "(O)24");
    return crop ? work("withdraw_from_chest", { ...farm, chest_tile: box.tile, items: [{ item_id: "(O)24", count: crop.stack }] }) : {};
  }
  if (request.includes("休息到 80")) {
    if (!previous.includes("rest")) return work("rest", { target_stamina: 80 });
    if (companion.locationId !== "Farm") {
      if (previous.includes("navigate_to")) return {};
      const origin = observed[0].world;
      const workArea = origin.farming?.choppableTrees?.find(tree => tree.kind === "tree" && tree.growthStage >= 5);
      return workArea ? work("navigate_to", { ...farm, tile: workArea.tile }) : {};
    }
    const trees = (farming?.choppableTrees ?? []).filter(tree => tree.kind === "tree" && tree.growthStage >= 5 && !tree.tapped);
    if (trees.length && !previous.includes("chop_tree")) return work("chop_tree", { ...farm, tiles: trees.slice(0, 2).map(tree => tree.tile) });
  } else if (request.includes("成熟的作物") && farmWork?.matureCrops?.length && !previous.includes("harvest_auto")) {
    return work("harvest_auto", farm);
  }
  const drops = new Map((farming?.groundItems ?? []).filter(item => item.isDropped && ["(O)388", "(O)24"].includes(item.itemId))
    .map(item => [`${item.tile.x}:${item.tile.y}`, item.tile]));
  if (drops.size && !previous.includes("pickup_items")) return work("pickup_items", { ...farm, tiles: [...drops.values()] });
  if (box && !previous.includes("deposit_to_chest")) {
    const wanted = request.includes("休息到 80") ? ["(O)388"] : ["(O)388", "(O)24"];
    const itemIds = wanted.filter(id => inventory?.slots?.some(slot => slot.itemId === id && slot.stack > 0));
    if (itemIds.length) return work("deposit_to_chest", { ...farm, chest_tile: box.tile, item_ids: itemIds });
  }
  return {};
}

const server = await createMockDeepSeek(plan);
redirectDeepSeek(server.address);
server.unref();
