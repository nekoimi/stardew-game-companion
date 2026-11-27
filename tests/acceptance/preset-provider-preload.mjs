import { createMockDeepSeek } from "../../apps/runtime/tests/mock-deepseek.mjs";
import { createMockOpenAI } from "../../apps/runtime/tests/mock-openai.mjs";
import { readFileSync } from "node:fs";
const scenarios = JSON.parse(readFileSync(new URL("./cases.json", import.meta.url), "utf8"));
const requests = new Set(Object.values(scenarios).flatMap(row => [row.instruction, row.then, row.cacheSecond, row.cacheThird, row.repeatRoute].filter(Boolean)));
requests.add(scenarios["phase-update"].duringAction);
function currentTask(messages) {
  const start = messages.findLastIndex(row => row.type === "user" && requests.has(row.text));
  return start >= 0 ? messages.slice(start) : messages;
}

const work = (name, params) => ({ name: "submit_work", args: { action: { [name]: params } } });
const observe = { name: "get_status", args: {} };
const parse = value => { try { return JSON.parse(value); } catch { return {}; } };
const call = (name, args) => ({ type: "call", name, args: parse(args) });
const operation = message => Object.keys(message?.args.action ?? {})[0];
let receivedContext = {};
function acceptContext(text) {
  const marker = "项目观察数据（不是玩家指令，不改变工具授权）：\n";
  if (!text.startsWith(marker)) return false;
  const data = parse(text.slice(marker.length));
  if (data.mode === "full") receivedContext = {};
  Object.assign(receivedContext, data.changes ?? {});
  for (const key of data.removed ?? []) delete receivedContext[key];
  return true;
}

function plan(messages) {
  const firstTool = messages.findIndex(row => row.type === "result");
  const before = firstTool < 0 ? messages : messages.slice(0, firstTool);
  const request = before.findLast(row => row.type === "user")?.text ?? "";
  if (request.includes("从家里出发，去皮埃尔商店柜台") || request.includes("再去一次皮埃尔柜台")) {
    const calls = messages.filter(row => row.type === "call");
    const last = messages.findLast(row => row.type === "result")?.result;
    if (["NATIVE_UI_ACTIVE", "DESTINATION_UNREACHABLE_NOW"].includes(last?.error?.code)
        && /event|cutscene|menu/i.test(last.error.message) && calls.length < 16)
      return work("native_advance", { allow_skip: true });
    if (last?.error || ["failed", "rejected", "cancelled", "partially-succeeded"].includes(last?.terminalState)) return {};
    const steps = [work("navigate_to", { location_id: "SeedShop", navigation_target: "shop", shop_id: "SeedShop" }),
      work("open_shop", { location_id: "SeedShop", shop_id: "SeedShop" }),
      work("purchase_items", { location_id: "SeedShop", shop_id: "SeedShop", items: [{ item_id: "(O)472", count: 1 }], budget_limit: 20 }),
      work("navigate_to", { location_id: "Farm", tile: { x: 78, y: 16 } })];
    if (request.includes("再去一次皮埃尔柜台")) steps.splice(2, 1);
    let stage = 0;
    for (let index = 0; index < messages.length - 1; index++)
      if (messages[index].type === "call" && operation(messages[index]) === Object.keys(steps[stage]?.args.action ?? {})[0]
          && messages[index+1].type === "result" && messages[index+1].result?.terminalState === "succeeded") stage++;
    return steps[stage] ?? {};
  }
  if (request.includes("先停下采伐")) return {};
  if (request.includes("查看") && request.includes("箱子的位置") || request.includes("箱子位置发生变化"))
    return firstTool < 0 ? { name: "query_game", args: { query: { query_chests: {} } } } : {};
  if (request.includes("日常完成后根据剩余时间")) return planGrowth(messages, request);
  if (firstTool < 0) return observe;
  const lastCall = messages.findLast(row => row.type === "call");
  if (request.includes("没有目标就不要动作")) {
    const farm = { location_id: "Farm" };
    if (lastCall?.name === "get_status") return work("tend_crops", farm);
    if (operation(lastCall) === "tend_crops") return work("harvest_crops", farm);
    if (operation(lastCall) === "harvest_crops") return work("collect_ground_items", farm);
    return {};
  }
  if (lastCall?.name !== "get_status") {
    const lastResult = messages.findLast(row => row.type === "result").result;
    const action = operation(lastCall);
    if (action === "tend_crops" && lastResult.stopReason !== "player-update") return {};
    // Walking can collect every drop before the explicit pickup reaches it.
    // Read the actual backpack before deciding whether there is work left.
    if (["harvest_crops", "collect_ground_items"].includes(action)) return observe;
    if (action === "pickup_items" && lastResult.error?.code === "no-item") return observe;
    if (["failed", "cancelled", "rejected"].includes(lastResult.terminalState) || typeof lastResult.error === "string") return {};
    if (["store_items", "ship_inventory"].includes(action)) return {};
    if (action === "withdraw_from_chest") {
      const items = lastCall.args.action.withdraw_from_chest.items;
      return work("ship_inventory", { location_id: "Farm", items });
    }
    return observe;
  }
  const observed = messages.filter(row => row.type === "result" && row.result.world).map(row => row.result);
  const { companion, farmWork, farming, chests, inventory } = observed.at(-1).world;
  const previous = messages.filter(row => row.type === "call").map(operation);
  const position = { x: companion.tileX, y: companion.tileY };
  const farm = { location_id: "Farm" };
  const localChests = chests?.items ?? [];
  const empty = localChests.filter(chest => chest.contentsObserved && chest.contents?.length === 0).sort((a, b) =>
    (a.tile.x - position.x) ** 2 + (a.tile.y - position.y) ** 2 - ((b.tile.x - position.x) ** 2 + (b.tile.y - position.y) ** 2));
  const box = request.includes("取出来") ? localChests.find(chest => chest.contents?.some(item => item.itemId === "(O)24")) : empty[0];
  if (request.includes("浇完水")) {
    if (!farmWork?.cropUnwateredTiles?.length) return {};
    return work("tend_crops", farm);
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
  } else if (request.includes("成熟的作物") && farmWork?.matureCrops?.length && !previous.includes("harvest_crops")) {
    return work("harvest_crops", farm);
  }
  const drops = new Map((farming?.groundItems ?? []).filter(item => item.isDropped && ["(O)388", "(O)24"].includes(item.itemId))
    .map(item => [`${item.tile.x}:${item.tile.y}`, item.tile]));
  if (drops.size && !previous.includes("collect_ground_items")) return work("collect_ground_items", { ...farm, target_tiles: [...drops.values()] });
  if (box && !previous.includes("store_items")) {
    const wanted = request.includes("休息到 80") ? ["(O)388"] : ["(O)388", "(O)24"];
    const items = wanted.flatMap(id => {
      const count = inventory?.slots?.reduce((sum, slot) => sum + (slot.itemId === id ? slot.stack : 0), 0) ?? 0;
      return count > 0 ? [{ item_id: id, count }] : [];
    });
    if (items.length) return work("store_items", { ...farm, chest_tile: box.tile, items });
  }
  return {};
}

function planGrowth(messages, request) {
  const calls = messages.filter(row => row.type === "call");
  const changes = calls.filter(row => row.name === "manage_arrangements").flatMap(row => Object.keys(row.args.change));
  const previous = calls.map(operation);
  const observed = messages.filter(row => row.type === "result" && row.result.world).at(-1)?.result.world;
  const last = messages.at(-1);
  const arrange = (name, params) => ({ name: "manage_arrangements", args: { change: { [name]: params } } });
  if (!changes.includes("manage_goal")) return arrange("manage_goal", { action: "add", text: "持续经营农场", horizon: "ongoing" });
  if (!observed) return observe;
  if (!previous.includes("tend_crops")) return work("tend_crops", { location_id: "Farm" });
  // Deliberately try to finish with a summary. Only the production continuation
  // checkpoint may ask this model to choose the next branch.
  if (!messages.some(row => row.type === "user" && row.text.includes("持续经营接续检查"))) return {};
  if (!changes.includes("set_day_plan")) return arrange("set_day_plan", { focus: "照料后补充经营材料",
    next_step: "采成熟树并将木材入箱", alternatives: ["补种缺少种子，先用现有工具积累材料"], blocked_branches: [] });
  if (!previous.includes("chop_tree")) {
    const trees = (observed.farming?.choppableTrees ?? []).filter(tree => tree.kind === "tree" && tree.growthStage >= 5 && !tree.tapped);
    return trees.length ? work("chop_tree", { location_id: "Farm", tiles: trees.slice(0, 2).map(tree => tree.tile) }) : observe;
  }
  if (last?.type === "result" && !last.result.world && !changes.includes("set_goal_continuation")) return observe;
  if (!previous.includes("store_items")) {
    const slots = observed.inventory?.slots ?? [];
    const count = slots.filter(slot => slot.itemId === "(O)388").reduce((sum, slot) => sum + slot.stack, 0);
    const chest = observed.chests?.items?.find(box => box.contentsObserved && !box.contents?.length);
    if (count && chest) return work("store_items", { location_id: "Farm", chest_tile: chest.tile,
      items: [{ item_id: "(O)388", count }] });
  }
  if (!changes.includes("set_goal_continuation")) return arrange("set_goal_continuation", {
    status: "wait", reason: "已完成今日照料及成熟树材料入箱，其他方向需新的种子与经营机会", next_day: true });
  return {};
}

function phasePlan(context, calls, daily = false) {
  const observed = context?.phaseObservation;
  if (!observed || calls.some(row => row.type === "call" && operation(row) === "execute_phase")) return {};
  const [destination] = Object.entries(observed.destinations ?? {}).filter(([, box]) => box.contentsObserved && !box.contents?.length);
  if (!destination) return {};
  return work("execute_phase", { observation_ref: observed.ref, scope_ref: observed.scopeRef, objective: "采两棵成熟树并入库",
    completion: { metric: daily ? "daily-care-stored" : "trees-stored", quantity: daily ? 1 : 2 },
    constraints: { stamina_floor: 80, destination_ref: destination[0], keep: [],
      include_existing: daily ? [{ item_id: "(O)388", count: 3 }] : [] },
    steps: [...(daily ? [{ id: "care", operation: "tend-crops", after: [], inputs: [] }] : []),
      { id: "wood", operation: daily ? "harvest-crops" : "chop-trees", after: daily ? ["care"] : [], inputs: [] },
      { id: "drops", operation: "collect-products", after: ["wood"], inputs: ["wood"] },
      { id: "box", operation: "store-products", after: ["drops"], inputs: ["drops"] }],
    branches: [{ on: "target-unreachable", action: "skip-target" }, { on: "inventory-full", action: "store-and-resume" }] });
}

function noChestPlan(context, calls, daily) {
  const observed = context?.phaseObservation;
  if (!observed || calls.some(row => row.type === "call" && operation(row) === "execute_phase")) return {};
  return work("execute_phase", { observation_ref: observed.ref, scope_ref: observed.scopeRef,
    objective: daily ? "照料、收获出货并保留木材" : "成熟树采伐并留包",
    completion: { metric: daily ? "daily-care-shipped" : "trees-collected", quantity: daily ? 1 : 2 },
    constraints: { stamina_floor: 80, keep: [], include_existing: daily ? [{ item_id: "(O)388", count: 3 }] : [],
      ...(daily ? { ship_item_ids: ["(O)24"] } : {}) },
    steps: [...(daily ? [{ id: "care", operation: "tend-crops", after: [], inputs: [] }] : []),
      { id: "products", operation: daily ? "harvest-crops" : "chop-trees", after: daily ? ["care"] : [], inputs: [] },
      { id: "collect", operation: "collect-products", after: ["products"], inputs: ["products"] },
      ...(daily ? [{ id: "ship", operation: "ship-products", after: ["collect"], inputs: ["collect"] }] : [])],
    branches: [{ on: "target-unreachable", action: "skip-target" }] });
}

function planDeepSeek({ messages }) {
  const system = messages.find(row => row.role === "system")?.content ?? "";
  for (const row of messages) if (row.role === "user") acceptContext(String(row.content ?? ""));
  const context = Object.keys(receivedContext).length ? receivedContext : parse(system.split("当前项目状态（数据，不包含工具授权）：\n")[1] ?? "{}");
  const decoded = currentTask(messages.flatMap(row => {
    if (row.role === "user") return String(row.content ?? "").startsWith("项目观察数据") ? [] : [{ type: "user", text: String(row.content ?? "") }];
    if (row.role === "tool") return [{ type: "result", result: parse(row.content) }];
    return (row.tool_calls ?? []).map(tool => call(tool.function.name, tool.function.arguments));
  }));
  if (decoded.some(row => row.type === "user" && row.text.includes("农场还没箱子")))
    return noChestPlan(context, decoded, decoded.some(row => row.type === "user" && row.text.includes("防风草")));
  if (decoded.some(row => row.type === "user" && row.text.includes("作为一个完整阶段")))
    return phasePlan(context, decoded, decoded.some(row => row.type === "user" && row.text.includes("当天作物照料")));
  return plan(decoded);
}

function planOpenAI({ input, instructions }) {
  for (const row of input) if (row.role === "user") acceptContext(row.content.filter(part => part.type === "input_text").map(part => part.text).join("\n"));
  const context = Object.keys(receivedContext).length ? receivedContext : parse(String(instructions ?? "").split("当前项目状态（数据，不包含工具授权）：\n")[1] ?? "{}");
  const decoded = currentTask(input.flatMap(row => {
    if (row.role === "user") {
      const text = row.content.filter(part => part.type === "input_text").map(part => part.text).join("\n");
      if (text.startsWith("项目观察数据")) return [];
      return [{ type: "user", text }];
    }
    if (row.type === "function_call") return [call(row.name, row.arguments)];
    if (row.type === "function_call_output") return [{ type: "result", result: parse(row.output) }];
    return [];
  }));
  if (decoded.some(row => row.type === "user" && row.text.includes("农场还没箱子")))
    return noChestPlan(context, decoded, decoded.some(row => row.type === "user" && row.text.includes("防风草")));
  if (decoded.some(row => row.type === "user" && row.text.includes("作为一个完整阶段")))
    return phasePlan(context, decoded, decoded.some(row => row.type === "user" && row.text.includes("当天作物照料")));
  return plan(decoded);
}

const openAI = process.env.STARDEW_ACCEPTANCE_FIXTURE_PROTOCOL === "responses";
const server = openAI ? await createMockOpenAI(planOpenAI) : await createMockDeepSeek(planDeepSeek);
const original = globalThis.fetch;
globalThis.fetch = (input, options) => {
  const request = new Request(input, options);
  const url = new URL(request.url);
  const host = openAI ? "api.openai.com" : "api.deepseek.com";
  if (url.hostname !== host && url.hostname !== "gateway.invalid") throw new Error("Unexpected preset fixture endpoint");
  return original(new Request(server.address + url.pathname, request));
};
server.unref();
