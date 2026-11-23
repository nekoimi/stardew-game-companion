import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { CompanionStore } from "../dist/adapters/storage/companion-store.js";

test("save-scoped identity, conversation, goals and recovery survive a process restart", async () => {
  const dir = await mkdtemp(join(tmpdir(), "companion-state-"));
  const file = join(dir, "companion.sqlite");
  const firstGame = { saveId: "farm", gameSessionId: "day-one" };
  let store = new CompanionStore(file);
  try {
    store.updateProfile("farm", { companionName: "阿栗" }, 0);
    assert.throws(() => store.updateProfile("farm", { companionName: "覆盖" }, 0), /已变化/);
    store.editMemory("farm", "add", "", "agreement", "保留幼树", "player", 0);
    assert.equal(store.append("farm", "first:player", "player", { text: "请先浇水" }), true);
    assert.equal(store.append("farm", "first:player", "player", { text: "重复请求" }), false);
    store.updateItem("farm", "goals", "goal", { text: "先浇水", status: "active" });
    store.updateItem("farm", "decisions", "decision", { status: "pending", message: "是否留种" });
    const context = { goalId: "goal", requestId: "first", locationId: "Farm", target: { x: 12, y: 8 } };
    store.beginAction(firstGame, "walk", undefined, context);
    store.close();
    store = new CompanionStore(file);
    assert.equal(store.profile("farm").profile.companionName, "阿栗");
    assert.equal(store.profile("other").profile.companionName, "阿星");
    assert.equal(store.memory("farm").entries[0].text, "保留幼树");
    assert.equal(store.memory("other").entries.length, 0);
    assert.equal(store.recentConversation("farm")[0].text, "请先浇水");
    assert.equal(store.items("farm", "goals")[0].status, "active");
    assert.equal(store.items("farm", "decisions")[0].status, "pending");
    assert.equal(store.pendingAction(firstGame), "walk");
    assert.deepEqual(store.actionContext(firstGame, "walk"), context);
    assert.deepEqual(store.actionContext({ ...firstGame, gameSessionId: "reloaded" }, "walk"), {});
    assert.equal(store.pendingAction({ ...firstGame, gameSessionId: "reloaded" }), undefined);
    assert.equal(store.recordReceipt(firstGame, "walk", { terminalState: "succeeded" }), true);
    assert.equal(store.recordReceipt(firstGame, "walk", { terminalState: "cancelled" }), false);
    assert.equal(store.pendingAction(firstGame), undefined);
    assert.equal(store.recentReceipts("farm")[0].terminalState, "succeeded");
  } finally { store.close(); await rm(dir, { recursive: true, force: true }); }
});

test("legacy action-completion notices do not consume reminder quota or unread counts", () => {
  const store = new CompanionStore(":memory:");
  try {
    store.updateProfile("farm", { onboarded: true, careFrequency: "moderate" }, 0);
    store.append("farm", "legacy-done", "care", { kind: "work-done", unread: true, gameDate: "1:spring:18", gameMinute: 590, text: "旧完成通知" });
    assert.equal(store.unreadCount("farm"), 0);
    assert.deepEqual(store.history("farm").entries, []);
    assert.deepEqual(store.history("farm", undefined, true).entries, []);
    assert.ok(store.saveReminder("farm", "1:spring:18", 1000, "morning", "", "还有作物需要浇水。"));
    assert.equal(store.unreadCount("farm"), 1);
    assert.equal(store.history("farm", undefined, true).entries.length, 1);
    assert.ok(store.saveReminder("farm", "1:spring:18", 1900, "evening", "", "记得留出返程时间。"));
    assert.equal(store.unreadCount("farm"), 2);
  } finally { store.close(); }
});
