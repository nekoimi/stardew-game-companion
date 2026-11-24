import { randomUUID, timingSafeEqual } from "node:crypto";
import { appendFileSync } from "node:fs";
import { createServer } from "node:http";
import type { IncomingMessage, ServerResponse } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { join } from "node:path";
import { GameConnection } from "../game/game-connection.js";
import { PiModelAdapter } from "../model/pi-model.js";
import { listModelProviders, ModelError, resolveSelection } from "../model/model-catalogue.js";
import { TurnRunner } from "../../application/turn-runner.js";
import { object } from "../../application/model.js";
import { CompanionStore } from "../storage/companion-store.js";
import type { JsonObject, ModelSelection } from "../../application/model.js";
import { checkModelConnection } from "../model/connection-check.js";
import { PluginCommandError } from "../../application/plugin-command.js";
import { DesktopEventStream } from "./event-stream.js";

export async function startDesktopService(
  runDirectory: string, secret: string, stopFile: string, ownerPid: number, modelTestOnly = false,
  dataDirectory = join(runDirectory, "..", "..", "data"),
): Promise<void> {
  if (!secret || !stopFile || !Number.isSafeInteger(ownerPid) || ownerPid < 1) throw new Error("Invalid desktop launch parameters");
  const diagnostic = (event: string, details: JsonObject = {}) =>
    process.stderr.write("STARDEW_COMPANION_LOG " + JSON.stringify({ event, ...details }) + "\n");
  const game = new GameConnection(runDirectory);
  const store = modelTestOnly ? undefined : new CompanionStore(join(dataDirectory, "companion.sqlite"),
    () => { diagnostic("receipt_retention_failed"); });
  const provider = new PiModelAdapter();
  const streams = new Set<DesktopEventStream>();
  const events: { id: number; data: string }[] = [];
  const submissions = new Set<string>();
  const acceptanceTrace = process.env.STARDEW_AGENT_ACCEPTANCE_TRACE;
  const trace = acceptanceTrace ? (type: string, payload: JsonObject) => {
    appendFileSync(acceptanceTrace, JSON.stringify({ type, payload }) + "\n");
  } : undefined;
  let sequence = 0;
  let lastStatus = "";
  let activeGameId: string | undefined;
  const publish = (type: string, payload: JsonObject) => {
    if (type === "submitted" || type === "life.chat.reply" || type === "result") trace?.(type, payload);
    if (type === "result") {
      diagnostic("action_result", { gameSessionId: payload.gameSessionId ?? null, commandId: payload.commandId ?? null,
        skillId: payload.skillId ?? null, status: payload.terminalState ?? null, completedCount: payload.completedCount ?? null,
        code: object(payload.error).code ?? null });
    }
    if (type === "life.chat.reply" && payload.status === "failed" && payload.error) {
      diagnostic("turn_failed", { gameSessionId: payload.gameSessionId ?? null, requestId: payload.requestId ?? null,
        code: payload.errorCode ?? null });
    }
    const data = JSON.stringify({ type, payload });
    if (type === "status") {
      if (lastStatus === data) return;
      lastStatus = data;
    }
    if (type === "life.chat.reply" && typeof payload.requestId === "string") {
      const id = `${payload.gameSessionId}:${payload.requestId}`;
      if (typeof payload.saveId === "string" && typeof payload.gameSessionId === "string") {
        store?.settleSubmission({ saveId: payload.saveId, gameSessionId: payload.gameSessionId }, payload.requestId, payload);
      }
      submissions.delete(id);
    }
    const frame = { id: ++sequence, data };
    events.push(frame);
    if (events.length > 256) events.shift();
    for (const stream of streams) stream.send(frame);
  };
  const core = await readFile(join(runDirectory, "docs", "agent-ts-instructions.md"), "utf8");
  const runner = new TurnRunner(game, undefined, publish,
    core + "\n按最新观察和工具回执推进。", store, runDirectory, trace, diagnostic);
  game.on("changed", () => {
    const binding = game.binding;
    if (activeGameId !== binding?.gameSessionId) {
      diagnostic(binding ? "game_connected" : "game_disconnected", { gameSessionId: binding?.gameSessionId ?? activeGameId ?? null });
      activeGameId = binding?.gameSessionId;
      submissions.clear();
    }
  });
  game.on("player-message", (type: string, payload: JsonObject) => {
    const binding = game.binding;
    if (!binding || typeof payload.requestId !== "string" || typeof payload.text !== "string") return;
    const preset = type === "chat.submit";
    if (!preset && store && Array.isArray(payload.noticeIds)
        && (payload.noticeAction === "dismiss" || payload.noticeAction === "restore")) {
      const action = payload.noticeAction;
      const expected = action === "dismiss" ? "pending" : "dismissed";
      const available = new Set(store.items(binding.saveId, "decisions").filter(item => item.status === expected).map(item => item.id));
      const ids = [...new Set(payload.noticeIds)];
      const valid = ids.length > 0 && ids.every(id => typeof id === "string" && available.has(id));
      if (valid) for (const id of ids) store.changeDecision(binding.saveId, String(id), action);
      runner.syncProfile(binding);
      runner.changed();
      game.notify("life.chat.reply", { ...binding, requestId: payload.requestId, status: valid ? "completed" : "failed",
        error: valid ? null : "这项选择已经变化。", profileRevision: store.profile(binding.saveId).profileRevision,
        memoryRevision: store.memory(binding.saveId).memoryRevision, replyAlreadyDelivered: true });
      return;
    }
    const decisionId = store?.items(binding.saveId, "decisions").some(item => item.id === payload.replyToNoticeId)
      ? String(payload.replyToNoticeId) : undefined;
    const requestId = payload.requestId;
    const id = `${binding.gameSessionId}:${requestId}`;
    if (!preset && store) {
      const signature = JSON.stringify([payload.text, "send", decisionId ?? null]);
      const existing = readSubmission(binding, requestId);
      if (existing) {
        if (existing.signature !== signature) return;
        if (existing.state.status === "unknown") {
          game.notify("life.chat.reply", { ...existing.state, status: "failed",
            profileRevision: store.profile(binding.saveId).profileRevision,
            memoryRevision: store.memory(binding.saveId).memoryRevision });
        }
        return;
      }
      store.trackSubmission(binding, requestId, signature, { ...binding, requestId, status: "accepted" });
      submissions.add(id);
    }
    void runner.submit(binding, requestId, payload.text, "send", false, decisionId, preset).catch(error => {
      const result = { ...binding, requestId, status: "failed", error: error.message,
        replyText: preset ? error.message : null, profileRevision: 0, memoryRevision: 0 };
      try { game.notify(preset ? "chat.reply" : "life.chat.reply", result); } catch { /* The binding has already changed. */ }
      publish("life.chat.reply", result);
    });
  });
  game.on("player-event", (type: string, payload: JsonObject) => {
    const binding = game.binding;
    if (!binding || !store) return;
    const requestId = typeof payload.requestId === "string" ? payload.requestId : "";
    const save = binding.saveId;
    if (type === "chat.cancel") {
      void runner.control(binding, "cancel").catch(() => undefined);
      return;
    }
    if (type === "autonomy.control") {
      void inGameControl(binding, requestId, String(payload.action ?? ""), object(payload.parameters));
      return;
    }
    try {
      if (type === "life.profile.get" || type === "life.profile.set") {
        if (type === "life.profile.set") store.updateProfile(save, object(payload.patch), Number(payload.expectedRevision));
        runner.syncProfile(binding, requestId);
      } else if (type === "life.milestones.get") {
        game.notify("life.milestones.state", { saveId: save, requestId, status: "ok", nodes: store.items(save, "milestones") });
      } else {
        if (type === "life.memory.edit") store.editMemory(save, String(payload.op ?? ""), String(payload.id ?? ""),
          String(payload.kind ?? ""), String(payload.text ?? ""), "player", Number(payload.expectedRevision));
        game.notify("life.memory.state", { saveId: save, requestId, status: "confirmed", ...store.memory(save) });
      }
    } catch {
      if (type.startsWith("life.profile")) runner.syncProfile(binding, requestId, "failed");
      else if (type.startsWith("life.memory")) {
        game.notify("life.memory.state", { saveId: save, requestId, status: "failed", reason: "请重新读取后再操作。", ...store.memory(save) });
      }
    }
  });

  async function inGameControl(binding: { saveId: string; gameSessionId: string }, requestId: string, action: string, params: JsonObject) {
    if (!store) return;
    const save = binding.saveId;
    let reason: string | null = null;
    try {
      const current = store.profile(save);
      if (action === "set_mode" || action === "set_preferences") {
        store.initiative(save, String(params.mode ?? current.initiativeMode ?? "command"),
          String(params.idle_preference ?? current.idlePreference ?? "autonomous"));
        const goal = typeof params.goal === "string" ? params.goal.trim().slice(0, 500) : "";
        if (goal && !store.items(save, "goals").some(item => item.status === "active" && item.text === goal)) {
          store.append(save, `${requestId}:player`, "player", { speaker: "你", text: goal });
          store.updateItem(save, "goals", randomUUID(), { text: goal, requestId, status: "active", continuation: "continue" });
        }
        runner.changed();
      } else {
        await runner.control(binding, action);
      }
    } catch (error) { reason = error instanceof Error ? error.message : "控制操作未确认。"; }
    if (game.binding?.gameSessionId !== binding.gameSessionId) return;
    const profile = store.profile(save);
    const intake = params.goal == null ? null : String(params.goal).trim().slice(0, 500);
    try {
      game.notify("autonomy.state", { ...binding, requestId, controlAction: action,
        mode: profile.initiativeMode ?? "command", paused: game.state.paused === true, dailySpend: 0,
        preferences: { idle_preference: profile.idlePreference ?? "autonomous", ...(intake ? { goal: intake } : {}) },
        status: reason ? "failed" : "confirmed", reason, ...store.usage(save) });
    } catch { /* The game will refresh state after reconnecting. */ }
    runner.syncProfile(binding);
  }
  const token = Buffer.from(`Bearer ${secret}`);
  const server = createServer((req, res) => void route(req, res));
  server.requestTimeout = 30_000;
  let closing = false;
  const shutDown = () => {
    if (closing) return;
    closing = true;
    clearInterval(heartbeat);
    const stopping = runner.stop();
    game.stop();
    for (const stream of streams) stream.close();
    server.close();
    void stopping.finally(() => store?.close());
  };
  const heartbeat = setInterval(async () => {
    for (const stream of streams) stream.heartbeat();
    try { process.kill(ownerPid, 0); } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ESRCH") return shutDown();
    }
    try { if ((await stat(stopFile)).isFile()) shutDown(); } catch { /* No stop has been requested. */ }
  }, 500);
  process.once("SIGINT", shutDown);
  process.once("SIGTERM", shutDown);
  server.once("close", () => { process.removeListener("SIGINT", shutDown); process.removeListener("SIGTERM", shutDown); });
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("No desktop HTTP port");
  process.stdout.write(JSON.stringify({ port: address.port, protocolVersion: 2 }) + "\n");
  if (!modelTestOnly) game.start();

  async function route(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const header = Buffer.from(req.headers.authorization ?? "");
    if (header.length !== token.length || !timingSafeEqual(header, token)) return respond(res, 401, { error: "未授权。" });
    const url = req.url ?? "";
    try {
      if (req.method === "GET") {
        if (url === "/v2/status") return respond(res, 200, runner.status());
        if (url === "/v2/model/providers") return respond(res, 200, { providers: listModelProviders() });
        if (url === "/v2/events") {
          res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-store" });
          const cursor = Number(req.headers["last-event-id"] ?? 0);
          const replay = Number.isSafeInteger(cursor) && cursor >= (events[0]?.id ?? 1) - 1
            ? events.filter(frame => frame.id > cursor) : [];
          replay.push({ id: sequence, data: JSON.stringify({ type: "status", payload: runner.status() }) });
          const stream = new DesktopEventStream(res, undefined, replay);
          streams.add(stream);
          res.on("close", () => streams.delete(stream));
          return;
        }
        if (url.startsWith("/v2/commands/")) {
          const binding = game.binding;
          const id = url.slice("/v2/commands/".length);
          const result = binding ? readSubmission(binding, id)?.state : undefined;
          return result ? respond(res, 200, result) : respond(res, 404, { error: "找不到指令。" });
        }
      }
      if (req.method !== "POST") return respond(res, 404, { error: "找不到操作。" });
      const body = await readBody(req);
      if (url.startsWith("/v2/plugins/")) {
        const { pluginId, instanceId, requestId, sessionId, correlationId } = body;
        if (closing || !store) return respond(res, 503, { code: "runtime_unavailable", message: "伴侣运行时暂未连接。" });
        if (typeof pluginId !== "string" || pluginId.length > 72 || !/^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$/.test(pluginId))
          throw new PluginCommandError("invalid_request", "插件标识无效。", 400);
        if (url === "/v2/plugins/close" && (instanceId == null || pluginIdentifier(instanceId))) {
          await runner.closePlugin(pluginId, typeof instanceId === "string" ? instanceId : undefined);
          return respond(res, 200, { closed: true });
        }
        if (url === "/v2/plugins/query" && pluginIdentifier(requestId))
          return respond(res, 200, await runner.queryPlugin(pluginId, requestId));
        const text = typeof body.text === "string" ? body.text.trim() : "";
        if (url === "/v2/plugins/submit" && pluginIdentifier(requestId) && pluginIdentifier(sessionId)
          && pluginIdentifier(instanceId) && text.length > 0 && text.length <= 500
          && (correlationId == null || pluginIdentifier(correlationId))) {
          const { duplicate, result } = runner.submitPlugin({ pluginId, instanceId, requestId, sessionId, text,
            ...(typeof correlationId === "string" ? { correlationId } : {}) });
          return respond(res, duplicate ? 200 : 202, result);
        }
        throw new PluginCommandError("invalid_request", "插件指令参数无效。", 400);
      }
      if (url === "/v2/model/test" || url === "/v2/model/configure") {
        const selection = object(body.selection);
        if (typeof selection.provider !== "string" || typeof selection.model !== "string" || typeof body.apiKey !== "string"
          || (selection.baseUrl !== undefined && typeof selection.baseUrl !== "string")
          || (selection.reasoning !== undefined && typeof selection.reasoning !== "string")
          || (selection.protocol !== undefined && typeof selection.protocol !== "string")
          || (selection.stream !== undefined && typeof selection.stream !== "boolean")) {
          return respond(res, 400, { error: "请填写服务商、API Key 和模型。" });
        }
        const configured = selection as unknown as ModelSelection;
        const { model: modelId, protocol, stream } = resolveSelection(configured);
        const effective = { ...configured, model: modelId, protocol, stream };
        const model = provider.open(effective, body.apiKey);
        if (url === "/v2/model/configure") {
          runner.configure(model, effective);
          diagnostic("model_configured", { provider: effective.provider, protocol });
          return respond(res, 200, { ready: true });
        }
        const disconnected = new AbortController();
        const abort = () => disconnected.abort();
        res.once("close", abort);
        try {
          await checkModelConnection(model, disconnected.signal, stream);
        } finally { res.off("close", abort); }
        diagnostic("model_test_passed", { provider: effective.provider, protocol });
        return respond(res, 200, { ready: true, toolCalling: true });
      }
      const binding = game.binding;
      if (url === "/v2/usage" && body.gameSessionId == null && body.saveId == null) return respond(res, 200, store?.usage() ?? {});
      if (!binding || body.saveId !== binding.saveId || body.gameSessionId !== binding.gameSessionId) {
        return respond(res, 409, { error: "游戏会话已变化，请重新操作。" });
      }
      if (url === "/v2/control" && typeof body.action === "string") {
        return respond(res, 200, await runner.control(binding, body.action,
          typeof body.goalId === "string" ? body.goalId : undefined));
      }
      if (store) {
        const save = binding.saveId;
        const before = typeof body.before === "number" && Number.isSafeInteger(body.before) ? body.before : undefined;
        switch (url) {
          case "/v2/usage": return respond(res, 200, store.usage(save));
          case "/v2/profile": {
            if (!body.patch) return respond(res, 200, store.profile(save));
            if (typeof body.expectedRevision !== "number") return respond(res, 400, { error: "缺少设定版本。" });
            const profile = store.updateProfile(save, object(body.patch), body.expectedRevision);
            runner.syncProfile(binding);
            runner.changed();
            return respond(res, 200, profile);
          }
          case "/v2/memory": {
            if (!body.op) return respond(res, 200, store.memory(save));
            if (typeof body.expectedRevision !== "number" || (body.op === "add" && !["preference", "agreement"].includes(String(body.kind)))) {
              return respond(res, 400, { error: "记忆内容或操作无效。" });
            }
            const result = store.editMemory(save, String(body.op), String(body.id ?? ""), String(body.kind ?? ""),
              typeof body.text === "string" ? body.text.trim() : "", "player", body.expectedRevision);
            return respond(res, 200, result);
          }
          case "/v2/initiative": {
            const result = store.initiative(save, String(body.mode ?? ""), String(body.preference ?? ""));
            runner.changed();
            return respond(res, 200, result);
          }
          case "/v2/history": return respond(res, 200, store.history(save, before));
          case "/v2/messages":
            if (typeof body.readId === "string") { store.markRead(save, body.readId); runner.changed(); }
            return respond(res, 200, store.history(save, before, true));
          case "/v2/notices":
            if (typeof body.noticeId === "string" && (body.action === "dismiss" || body.action === "restore")) {
              const result = store.changeDecision(save, body.noticeId, body.action);
              runner.syncProfile(binding);
              runner.changed();
              return respond(res, 200, result);
            }
            return respond(res, 400, { error: "待确认事项或操作无效。" });
          case "/v2/milestones": {
            const current = store.items(save, "milestones").find(item => item.id === body.nodeId);
            if (!current || current.updatedAt !== body.updatedAt || (body.action !== "adopt" && body.action !== "defer")) {
              return respond(res, 409, { error: "计划已变化，请重新读取。" });
            }
            const result = store.updateItem(save, "milestones", current.id,
              { status: body.action === "adopt" ? "adopted" : "deferred" }, true);
            runner.changed();
            return respond(res, 200, result);
          }
        }
      }
      if (url === "/v2/commands") {
        const { requestId, text, mode = "send" } = body;
        if (typeof requestId !== "string" || !/^[a-zA-Z0-9_-]{1,80}$/.test(requestId)
          || typeof text !== "string" || !text.trim() || text.length > 500 || (mode !== "send" && mode !== "interrupt")
          || (body.decisionId != null && (typeof body.decisionId !== "string" || body.decisionId.length > 120 || mode !== "send"))) {
          return respond(res, 400, { error: "指令参数无效。" });
        }
        const id = `${binding.gameSessionId}:${requestId}`;
        const signature = JSON.stringify([text, mode, body.decisionId ?? null]);
        const existing = readSubmission(binding, requestId);
        if (existing) return existing.signature === signature
          ? respond(res, 200, existing.state) : respond(res, 409, { error: "请求编号已用于其他指令。" });
        if (!store) return respond(res, 409, { error: "当前进程仅用于测试模型连接。" });
        const accepted = { ...binding, requestId, status: "accepted" };
        store.trackSubmission(binding, requestId, signature, accepted);
        submissions.add(id);
        try { await runner.submit(binding, requestId, text.trim(), mode, true,
          typeof body.decisionId === "string" ? body.decisionId : undefined); }
        catch (error) { submissions.delete(id); store.forgetSubmission(binding, requestId); throw error; }
        return respond(res, 202, readSubmission(binding, requestId)?.state ?? accepted);
      }
      return respond(res, 404, { error: "找不到操作。" });
    } catch (error) {
      if (url === "/v2/model/test" || url === "/v2/model/configure")
        diagnostic("model_request_failed", { code: error instanceof ModelError ? error.code : "unexpected_error" });
      if (error instanceof PluginCommandError)
        return respond(res, error.httpStatus, { code: error.code, message: error.message,
          ...(error.retryAfterMs ? { retryAfterMs: error.retryAfterMs } : {}) });
      if (url.startsWith("/v2/plugins/")) return respond(res, 503, { code: "runtime_unavailable", message: "插件指令暂不可用。" });
      if (error instanceof ModelError) return respond(res, 409, { error: error.message, code: error.code });
      if (error instanceof SyntaxError) return respond(res, 400, { error: "请求格式无效。" });
      return respond(res, 409, { error: error instanceof Error ? error.message : "操作未完成。" });
    }
  }

  function readSubmission(binding: { saveId: string; gameSessionId: string }, id: string) {
    const record = store?.submission(binding, id);
    if (record?.state.status === "accepted" && !submissions.has(`${binding.gameSessionId}:${id}`)) {
      return { ...record, state: { ...record.state, status: "unknown", error: "上次连接中断，指令结果待核对。" } };
    }
    return record;
  }
}

function pluginIdentifier(value: unknown): value is string {
  return typeof value === "string" && /^[A-Za-z0-9_.-]{1,120}$/.test(value);
}

function respond(res: ServerResponse, status: number, body: JsonObject): void {
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
  res.end(JSON.stringify(body));
}

async function readBody(req: IncomingMessage): Promise<JsonObject> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > 16_384) throw new SyntaxError();
    chunks.push(chunk);
  }
  const body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
  if (body === null || typeof body !== "object" || Array.isArray(body)) throw new SyntaxError();
  return body;
}
