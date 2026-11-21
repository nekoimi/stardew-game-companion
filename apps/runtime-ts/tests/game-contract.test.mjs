import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { classifyReceipt } from "../dist/adapters/game/action-receipt.js";

const samples = JSON.parse(await readFile(
  new URL("../../../contracts/game-bridge/examples/v2/action-lifecycle.json", import.meta.url), "utf8"));
for (const sample of samples) {
  test(sample.name, () => {
    assert.equal(classifyReceipt(sample.message, { saveId: "farm", gameSessionId: sample.gameSessionId },
      sample.expectedActionId, new Set(sample.settledActionIds)), sample.expected);
  });
}
