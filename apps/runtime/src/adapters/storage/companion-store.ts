import { randomUUID } from "node:crypto";
import { mkdirSync } from "node:fs";
import { dirname } from "node:path";
import { DatabaseSync } from "node:sqlite";
import type { JsonObject, ModelMessage, ModelUsage } from "../../application/model.js";
import { object } from "../../application/model.js";
import type { GameBinding } from "../game/action-receipt.js";
import type { PluginCommand } from "../../application/plugin-command.js";

const defaultProfile: JsonObject = {
  companionName: "阿星", playStyle: "earn", personality: "gentle", careFrequency: "moderate", bedtime: 2400, onboarded: false,
};
type Section = "profile" | "memory" | "initiative" | "goals" | "milestones" | "decisions" | "todos";
type Item = JsonObject & { id: string };
type UsageContext = { taskId: string; source: "player" | "autonomy" | "bedtime" | "plugin"; retry: boolean };
const usageSources: Record<string, string> = { player: "玩家请求", autonomy: "自动经营", bedtime: "自动就寝", plugin: "插件指令" };
const usageFields = `count(*) AS calls, sum(CASE WHEN json_extract(value, '$.input') IS NOT NULL
  OR json_extract(value, '$.output') IS NOT NULL OR json_extract(value, '$.cacheRead') IS NOT NULL
  OR json_extract(value, '$.cacheWrite') IS NOT NULL THEN 1 ELSE 0 END) AS recorded,
  sum(json_extract(value, '$.input')) AS input, sum(json_extract(value, '$.output')) AS output,
  sum(json_extract(value, '$.cacheRead')) AS cache, sum(json_extract(value, '$.retry')) AS retries`;

function usageText(row: Record<string, unknown>): string {
  const missing = Number(row.calls) - Number(row.recorded ?? 0);
  return `${row.calls} 次请求 · 输入 ${row.input ?? "未记录"} · 输出 ${row.output ?? "未记录"} · 缓存读取 ${row.cache ?? "未记录"}`
    + (Number(row.retries) > 0 ? ` · ${row.retries} 次重试` : "")
    + (missing > 0 ? ` · ${missing} 次未报告用量` : "");
}

function decode<T>(row: Record<string, unknown> | undefined, fallback: T): T {
  return row ? JSON.parse(String(row.value)) as T : fallback;
}

export class CompanionStore {
  private readonly db: DatabaseSync;
  private receiptWrites = 0;

  constructor(path: string, private readonly maintenanceFailed?: () => void) {
    if (path !== ":memory:") mkdirSync(dirname(path), { recursive: true });
    this.db = new DatabaseSync(path);
    this.db.exec(`
      PRAGMA journal_mode = WAL;
      PRAGMA busy_timeout = 3000;
      CREATE TABLE IF NOT EXISTS save_sections (
        save_id TEXT NOT NULL, name TEXT NOT NULL, rev INTEGER NOT NULL, value TEXT NOT NULL,
        PRIMARY KEY(save_id, name)
      );
      CREATE TABLE IF NOT EXISTS conversations (
        seq INTEGER PRIMARY KEY, save_id TEXT NOT NULL, id TEXT NOT NULL, kind TEXT NOT NULL, value TEXT NOT NULL,
        UNIQUE(save_id, id)
      );
      CREATE INDEX IF NOT EXISTS conversations_save ON conversations(save_id, kind, seq);
      CREATE TABLE IF NOT EXISTS receipts (
        seq INTEGER PRIMARY KEY, save_id TEXT NOT NULL, game_id TEXT NOT NULL, action_id TEXT NOT NULL, value TEXT NOT NULL,
        UNIQUE(save_id, game_id, action_id)
      );
      CREATE INDEX IF NOT EXISTS receipts_save_seq ON receipts(save_id, seq DESC);
      CREATE TABLE IF NOT EXISTS usage (
        save_id TEXT NOT NULL, request_id TEXT NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL,
        day TEXT NOT NULL, value TEXT, PRIMARY KEY(save_id, request_id)
      );
      CREATE TABLE IF NOT EXISTS unsettled_actions (
        save_id TEXT NOT NULL, game_id TEXT NOT NULL, action_id TEXT NOT NULL, PRIMARY KEY(save_id, game_id)
      );
      CREATE TABLE IF NOT EXISTS submissions (
        save_id TEXT NOT NULL, game_id TEXT NOT NULL, request_id TEXT NOT NULL,
        signature TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY(save_id, game_id, request_id)
      );
      CREATE TABLE IF NOT EXISTS plugin_commands (
        plugin_id TEXT NOT NULL, request_id TEXT NOT NULL,
        internal_id TEXT NOT NULL UNIQUE, value TEXT NOT NULL, PRIMARY KEY(plugin_id, request_id)
      );
      CREATE TABLE IF NOT EXISTS plugin_actions (
        internal_id TEXT NOT NULL, action_id TEXT NOT NULL, PRIMARY KEY(internal_id, action_id)
      );
      CREATE INDEX IF NOT EXISTS plugin_actions_action ON plugin_actions(action_id);
      CREATE TABLE IF NOT EXISTS action_contexts (
        save_id TEXT NOT NULL, game_id TEXT NOT NULL, action_id TEXT NOT NULL, value TEXT NOT NULL,
        PRIMARY KEY(save_id, game_id, action_id)
      );
    `);
    for (const row of this.db.prepare("SELECT DISTINCT save_id FROM receipts").all()) this.maintainReceipts(String(row.save_id));
  }

  close(): void { this.db.close(); }

  private section<T>(save: string, name: Section, fallback: T): { revision: number; value: T } {
    const row = this.db.prepare("SELECT rev, value FROM save_sections WHERE save_id = ? AND name = ?").get(save, name);
    return { revision: Number(row?.rev ?? 0), value: decode(row, fallback) };
  }

  private update<T>(save: string, name: Section, fallback: T, change: (value: T) => T, expected?: number) {
    this.db.exec("BEGIN IMMEDIATE");
    try {
      const previous = this.section(save, name, fallback);
      if (expected !== undefined && expected !== previous.revision) throw new Error("数据已变化，请重新读取后再编辑。");
      const value = change(previous.value);
      let revision = previous.revision;
      if (JSON.stringify(value) !== JSON.stringify(previous.value)) {
        revision++;
        this.db.prepare(`
          INSERT INTO save_sections(save_id, name, rev, value) VALUES (?, ?, ?, ?)
          ON CONFLICT(save_id, name) DO UPDATE SET rev=excluded.rev, value=excluded.value
        `).run(save, name, revision, JSON.stringify(value));
      }
      this.db.exec("COMMIT");
      return { revision, value };
    } catch (error) { this.db.exec("ROLLBACK"); throw error; }
  }

  profile(save: string): JsonObject {
    const profile = this.section(save, "profile", defaultProfile);
    const initiative = this.section<JsonObject>(save, "initiative", { initiativeMode: "command", idlePreference: "autonomous" });
    return { profileRevision: profile.revision, profile: profile.value, ...initiative.value };
  }

  updateProfile(save: string, patch: JsonObject, revision: number): JsonObject {
    for (const [field, value] of Object.entries(patch)) {
      const choices: Record<string, string[]> = {
        playStyle: ["earn", "workhorse", "community", "decor"],
        personality: ["gentle", "lively", "calm", "tsundere"],
        careFrequency: ["quiet", "moderate", "chatty"],
      };
      const valid = field === "companionName" ? typeof value === "string" && value.trim().length > 0 && value.trim().length <= 12
        : field === "bedtime" ? typeof value === "number" && Number.isInteger(value) && value >= 1800 && value <= 2550 && value % 100 <= 50 && value % 10 === 0
        : field === "onboarded" ? typeof value === "boolean"
        : typeof value === "string" && Object.hasOwn(choices, field) && choices[field].includes(value);
      if (!valid) throw new Error("伙伴设定包含不支持的值。");
    }
    this.update(save, "profile", defaultProfile, profile => ({ ...profile, ...patch }), revision);
    return this.profile(save);
  }

  initiative(save: string, mode: string, preference: string): JsonObject {
    if (!["command", "free"].includes(mode) || !["autonomous", "clear", "forage", "wood", "wait"].includes(preference)) {
      throw new Error("主动帮忙选项无效。");
    }
    this.update<JsonObject>(save, "initiative", {}, previous => ({ ...previous, initiativeMode: mode, idlePreference: preference }));
    return this.profile(save);
  }

  memory(save: string): { memoryRevision: number; entries: Item[] } {
    const data = this.section<Item[]>(save, "memory", []);
    return { memoryRevision: data.revision, entries: data.value };
  }

  editMemory(save: string, op: string, id: string, kind: string, text: string, source: string, expected?: number): JsonObject {
    this.update<Item[]>(save, "memory", [], entries => {
      if (op === "add") {
        if (!["preference", "agreement", "event"].includes(kind) || !text.trim() || text.length > 200) throw new Error("记忆内容或类型无效。");
        if (entries.length >= 500) throw new Error("记忆已满，请先整理已有记录。");
        return [...entries, { id: randomUUID(), kind, text, source, gameDate: "unknown", createdAt: new Date().toISOString() }];
      }
      const previous = entries.find(item => item.id === id);
      if (!previous) throw new Error("这条记忆已不存在。");
      if (op === "delete") return entries.filter(item => item !== previous);
      if (op !== "correct" || previous.kind === "event" || !text.trim() || text.length > 200) throw new Error("记忆内容或操作无效。");
      return entries.map(item => item === previous ? { ...item, text } : item);
    }, expected);
    return this.memory(save);
  }

  append(save: string, id: string, kind: "player" | "assistant" | "care", data: JsonObject): boolean {
    return this.db.prepare("INSERT OR IGNORE INTO conversations(save_id, id, kind, value) VALUES (?, ?, ?, ?)")
      .run(save, id, kind, JSON.stringify({ ...data, id })).changes === 1;
  }

  history(save: string, before?: number, reminders = false): JsonObject {
    const where = reminders ? " AND kind = 'care' AND coalesce(json_extract(value, '$.kind'), '') <> 'work-done'"
      : " AND (kind <> 'care' OR coalesce(json_extract(value, '$.kind'), '') <> 'work-done')";
    const rows = this.db.prepare(`SELECT seq, value FROM conversations
      WHERE save_id = ? AND seq < ? ${where} ORDER BY seq DESC LIMIT 31`)
      .all(save, before ?? Number.MAX_SAFE_INTEGER);
    return {
      entries: rows.slice(0, 30).map(row => decode(row, {})),
      nextBefore: rows.length > 30 ? Number(rows[29].seq) : null,
    };
  }

  markRead(save: string, id: string): void {
    const row = this.db.prepare("SELECT value FROM conversations WHERE save_id = ? AND id = ? AND kind = 'care'").get(save, id);
    if (!row) throw new Error("这条提醒已不存在。");
    this.db.prepare("UPDATE conversations SET value = ? WHERE save_id = ? AND id = ?")
      .run(JSON.stringify({ ...decode(row, {}), unread: false }), save, id);
  }

  unreadCount(save: string): number {
    return Number(this.db.prepare(`SELECT count(*) AS count FROM conversations WHERE save_id = ?
      AND kind = 'care' AND json_extract(value, '$.unread') = 1
      AND coalesce(json_extract(value, '$.kind'), '') <> 'work-done'`).get(save)?.count);
  }

  reminderRevision(save: string): number {
    return Number(this.db.prepare("SELECT max(seq) AS revision FROM conversations WHERE save_id = ? AND kind = 'care'")
      .get(save)?.revision ?? 0);
  }

  saveReminder(save: string, date: string, time: number, kind: string, ref: string, text: string): JsonObject | undefined {
    const profile = object(this.profile(save).profile);
    const limit = profile.careFrequency === "quiet" ? 0 : profile.careFrequency === "chatty" ? 4 : 2;
    if (!limit || profile.onboarded !== true) return;
    const day = this.db.prepare(`SELECT count(*) AS count, max(json_extract(value, '$.gameMinute')) AS latest
      FROM conversations WHERE save_id = ? AND kind = 'care' AND json_extract(value, '$.gameDate') = ?
      AND coalesce(json_extract(value, '$.kind'), '') <> 'work-done'`).get(save, date);
    const minute = Math.floor(time / 100) * 60 + time % 100;
    if (Number(day?.count) >= limit || (day?.latest != null && minute - Number(day.latest) < 120)) return;
    const eventKey = `${kind}:${date}:${ref}`;
    const entry = { eventKey, gameDate: date, kind, text, unread: true, gameMinute: minute,
      speaker: String(profile.companionName ?? "伙伴") };
    if (this.append(save, `care:${eventKey}`, "care", entry)) return entry;
  }

  recentConversation(save: string): ModelMessage[] {
    return this.db.prepare(`SELECT kind, value FROM conversations WHERE save_id = ? AND kind IN ('player', 'assistant')
      ORDER BY seq DESC LIMIT 32`).all(save).reverse().map(row => {
      const text = String(decode<JsonObject>(row, {}).text ?? "");
      return row.kind === "player" ? { role: "user", text } : { role: "assistant", text, calls: [] };
    });
  }

  playerRequest(save: string, requestId: string): JsonObject {
    return decode(this.db.prepare("SELECT value FROM conversations WHERE save_id = ? AND id = ? AND kind = 'player'")
      .get(save, `${requestId}:player`), {});
  }

  submission(binding: GameBinding, requestId: string): { signature: string; state: JsonObject } | undefined {
    const row = this.db.prepare("SELECT signature, value FROM submissions WHERE save_id = ? AND game_id = ? AND request_id = ?")
      .get(binding.saveId, binding.gameSessionId, requestId);
    return row ? { signature: String(row.signature), state: decode(row, {}) } : undefined;
  }

  trackSubmission(binding: GameBinding, requestId: string, signature: string, state: JsonObject): void {
    this.db.prepare("INSERT INTO submissions VALUES (?, ?, ?, ?, ?)")
      .run(binding.saveId, binding.gameSessionId, requestId, signature, JSON.stringify(state));
  }

  settleSubmission(binding: GameBinding, requestId: string, state: JsonObject): void {
    this.db.prepare("UPDATE submissions SET value = ? WHERE save_id = ? AND game_id = ? AND request_id = ?")
      .run(JSON.stringify(state), binding.saveId, binding.gameSessionId, requestId);
  }

  forgetSubmission(binding: GameBinding, requestId: string): void {
    this.db.prepare("DELETE FROM submissions WHERE save_id = ? AND game_id = ? AND request_id = ?")
      .run(binding.saveId, binding.gameSessionId, requestId);
  }

  items(save: string, section: "goals" | "milestones" | "decisions" | "todos"): Item[] {
    return this.section<Item[]>(save, section, []).value;
  }

  updateItem(save: string, section: "goals" | "milestones" | "decisions" | "todos", id: string, patch: JsonObject,
             requireExisting = false): Item {
    let result!: Item;
    this.update<Item[]>(save, section, [], rows => {
      const previous = rows.find(item => item.id === id);
      if (!previous && requireExisting) throw new Error("这项安排已经变化，请重新读取。");
      if (previous && Object.entries(patch).every(([key, value]) => JSON.stringify(previous[key]) === JSON.stringify(value))) {
        result = previous;
        return rows;
      }
      result = { ...previous, ...patch, id, updatedAt: Date.now() / 1000 };
      if (!previous && rows.length >= 500) throw new Error("安排记录已满。");
      return previous ? rows.map(item => item === previous ? result : item) : [...rows, result];
    });
    return result;
  }

  changeDecision(save: string, id: string, action: "answer" | "dismiss" | "restore", answer?: string): Item {
    const current = this.items(save, "decisions").find(item => item.id === id);
    if (!current || current.status !== (action === "restore" ? "dismissed" : "pending")) throw new Error("这项选择已经变化。");
    return this.updateItem(save, "decisions", id, { status: action === "answer" ? "answered" : action === "dismiss" ? "dismissed" : "pending",
      ...(answer !== undefined ? { answer } : {}) }, true);
  }

  pluginCommand(pluginId: string, requestId: string): PluginCommand | undefined {
    const row = this.db.prepare("SELECT value FROM plugin_commands WHERE plugin_id = ? AND request_id = ?").get(pluginId, requestId);
    return decode<PluginCommand | undefined>(row, undefined);
  }

  acceptPluginCommand(command: PluginCommand): void {
    this.db.prepare("INSERT INTO plugin_commands VALUES (?, ?, ?, ?)")
      .run(command.input.pluginId, command.input.requestId, command.internalId, JSON.stringify(command));
  }

  savePluginCommand(command: PluginCommand): void {
    this.db.prepare("UPDATE plugin_commands SET value = ? WHERE internal_id = ?").run(JSON.stringify(command), command.internalId);
  }

  pluginActions(command: PluginCommand): { actionId: string; receipt?: JsonObject }[] {
    return this.db.prepare(`
      SELECT links.action_id, receipts.value FROM plugin_actions AS links
      LEFT JOIN receipts ON receipts.action_id = links.action_id AND receipts.save_id = ? AND receipts.game_id = ?
      WHERE links.internal_id = ? ORDER BY links.rowid
    `).all(command.binding.saveId, command.binding.gameSessionId, command.internalId)
      .map(row => ({ actionId: String(row.action_id), receipt: row.value === null ? undefined : decode<JsonObject>(row, {}) }));
  }

  beginAction(binding: GameBinding, actionId: string, pluginRequest?: string, context?: JsonObject): void {
    this.db.exec("BEGIN IMMEDIATE");
    try {
      this.db.prepare("INSERT INTO unsettled_actions VALUES (?, ?, ?)").run(binding.saveId, binding.gameSessionId, actionId);
      if (context) this.db.prepare("INSERT INTO action_contexts VALUES (?, ?, ?, ?)")
        .run(binding.saveId, binding.gameSessionId, actionId, JSON.stringify(context));
      if (pluginRequest) {
        this.db.prepare("INSERT INTO plugin_actions VALUES (?, ?)").run(pluginRequest, actionId);
      }
      this.db.exec("COMMIT");
    } catch (error) {
      this.db.exec("ROLLBACK");
      throw error;
    }
  }

  actionContext(binding: GameBinding, actionId: string): JsonObject {
    return decode(this.db.prepare("SELECT value FROM action_contexts WHERE save_id = ? AND game_id = ? AND action_id = ?")
      .get(binding.saveId, binding.gameSessionId, actionId), {});
  }

  pendingAction(binding: GameBinding): string | undefined {
    const row = this.db.prepare("SELECT action_id FROM unsettled_actions WHERE save_id = ? AND game_id = ?")
      .get(binding.saveId, binding.gameSessionId);
    return typeof row?.action_id === "string" ? row.action_id : undefined;
  }

  recordReceipt(binding: GameBinding, actionId: string, receipt: JsonObject): boolean {
    let inserted = false;
    this.db.exec("BEGIN IMMEDIATE");
    try {
      const { changes } = this.db.prepare("INSERT OR IGNORE INTO receipts(save_id, game_id, action_id, value) VALUES (?, ?, ?, ?)")
        .run(binding.saveId, binding.gameSessionId, actionId, JSON.stringify(receipt));
      this.db.prepare("DELETE FROM unsettled_actions WHERE save_id = ? AND game_id = ? AND action_id = ?")
        .run(binding.saveId, binding.gameSessionId, actionId);
      this.db.exec("COMMIT");
      inserted = changes === 1;
    } catch (error) { this.db.exec("ROLLBACK"); throw error; }
    if (inserted && ++this.receiptWrites % 128 === 0) this.maintainReceipts(binding.saveId);
    return inserted;
  }

  private maintainReceipts(save: string): void {
    try { this.pruneReceipts(save); }
    catch { this.maintenanceFailed?.(); } // Cleanup cannot invalidate a committed native receipt.
  }

  /** Keep recent evidence plus unresolved, plugin-referenced and active-goal evidence.
   * Freed SQLite pages are reused; no blocking VACUUM on the live game connection. */
  pruneReceipts(save: string, retain = 2000): number {
    if (!Number.isSafeInteger(retain) || retain < 1) throw new Error("回执保留数量无效。");
    this.db.exec("BEGIN IMMEDIATE");
    try {
      const { changes } = this.db.prepare(`DELETE FROM receipts AS r WHERE r.save_id = ?
        AND json_extract(r.value, '$.terminalState') IN ('succeeded', 'partially-succeeded', 'partial', 'cancelled', 'failed', 'rejected')
        AND r.seq NOT IN (SELECT seq FROM receipts WHERE save_id = ? ORDER BY seq DESC LIMIT ?)
        AND NOT EXISTS (SELECT 1 FROM unsettled_actions u
          WHERE u.save_id = r.save_id AND u.game_id = r.game_id AND u.action_id = r.action_id)
        AND NOT EXISTS (SELECT 1 FROM plugin_actions p WHERE p.action_id = r.action_id)
        AND NOT EXISTS (SELECT 1 FROM action_contexts c
          JOIN save_sections s ON s.save_id = c.save_id AND s.name = 'goals'
          JOIN json_each(s.value) g
          WHERE c.save_id = r.save_id AND c.game_id = r.game_id AND c.action_id = r.action_id
            AND json_extract(g.value, '$.status') = 'active'
            AND json_extract(g.value, '$.id') = json_extract(c.value, '$.goalId'))`)
        .run(save, save, retain);
      this.db.prepare(`DELETE FROM action_contexts AS c WHERE c.save_id = ?
        AND NOT EXISTS (SELECT 1 FROM receipts r
          WHERE r.save_id = c.save_id AND r.game_id = c.game_id AND r.action_id = c.action_id)
        AND NOT EXISTS (SELECT 1 FROM unsettled_actions u
          WHERE u.save_id = c.save_id AND u.game_id = c.game_id AND u.action_id = c.action_id)
        AND NOT EXISTS (SELECT 1 FROM plugin_actions p WHERE p.action_id = c.action_id)`)
        .run(save);
      this.db.exec("COMMIT");
      return Number(changes);
    } catch (error) { this.db.exec("ROLLBACK"); throw error; }
  }

  recentReceipts(save: string, limit = 8): JsonObject[] {
    return this.db.prepare("SELECT value FROM receipts WHERE save_id = ? ORDER BY seq DESC LIMIT ?")
      .all(save, limit).reverse().map(row => decode<JsonObject>(row, {}));
  }

  recordUsage(save: string, requestId: string, provider: string, model: string, value?: ModelUsage, context?: UsageContext): void {
    const now = new Date();
    const day = `${now.getFullYear()}-${now.getMonth() + 1}-${now.getDate()}`;
    this.db.prepare("INSERT OR IGNORE INTO usage VALUES (?, ?, ?, ?, ?, ?)")
      .run(save, requestId, provider, model, day, value || context ? JSON.stringify({ ...value, ...context }) : null);
  }

  usage(save?: string): JsonObject {
    const now = new Date();
    const day = `${now.getFullYear()}-${now.getMonth() + 1}-${now.getDate()}`;
    const summarize = (sql: string, ...params: string[]): string =>
      usageText(this.db.prepare(`SELECT ${usageFields} FROM usage ${sql}`).get(...params)!);
    return { usageTodayText: summarize("WHERE day = ?", day),
      ...(save ? { usageSaveTodayText: summarize("WHERE day = ? AND save_id = ?", day, save),
        usageSaveTotalText: summarize("WHERE save_id = ?", save), recentTasks: this.taskUsage(save) } : {}) };
  }

  private taskUsage(save: string): JsonObject[] {
    const goals = new Map(this.items(save, "goals").map(goal => [goal.id, goal.text]));
    return this.db.prepare(`SELECT json_extract(value, '$.taskId') AS task, model, provider,
      max(json_extract(value, '$.source')) AS source, ${usageFields}
      FROM usage WHERE save_id = ? AND task IS NOT NULL GROUP BY task, model, provider
      ORDER BY max(rowid) DESC LIMIT 10`).all(save).map(row => {
      const task = String(row.task);
      const title = this.playerRequest(save, task).text ?? goals.get(task) ?? usageSources[String(row.source)] ?? "模型调用";
      return { title: String(title).replace(/\s+/g, " ").slice(0, 64), provider: String(row.provider), model: String(row.model), text: usageText(row) };
    });
  }
}
