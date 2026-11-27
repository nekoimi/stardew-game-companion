import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { actions, queries, nativeParameters, oneOperation, validate } from "../dist/tools/native-tools.js";

test("navigation targets enforce semantic requirements at the nested tool boundary", () => {
  const schema = oneOperation({ navigate_to: actions.navigate_to[1] });
  const samples = JSON.parse(readFileSync(new URL("../../../contracts/game-bridge/examples/navigation-goals.json", import.meta.url)));
  for (const sample of samples) {
    const input = Object.fromEntries(Object.entries(sample.parameters).map(([name, value]) => [name.replace(/[A-Z]/g, c => "_" + c.toLowerCase()), value]));
    const check = () => validate(schema, { navigate_to: input });
    if (sample.valid) assert.doesNotThrow(check, sample.name);
    else assert.throws(check, /不符合工具定义/, sample.name);
  }
  const shop = { location_id: "SeedShop", navigation_target: "shop", shop_id: "SeedShop" };
  validate(schema, { navigate_to: shop });
  assert.deepEqual(nativeParameters(shop), { locationId: "SeedShop", navigationTarget: "shop", shopId: "SeedShop" });
  validate(queries.query_route[1], shop);
  validate(schema, { navigate_to: { location_id: "Farm", navigation_target: "interact", tile: { x: 4, y: 2 } } });
  validate(schema, { navigate_to: { location_id: "Farm", tile: { x: 4, y: 2 } } });
  assert.throws(() => validate(schema, { navigate_to: { location_id: "Farm", navigation_target: "interact" } }), /不符合工具定义/);
});
