import { timingSafeEqual } from "node:crypto";
import { createServer } from "node:http";
import type { IncomingMessage, ServerResponse } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { join } from "node:path";
import { GameConnection } from "../game/game-connection.js";
import { listModelProviders, ModelError, PiModelAdapter } from "../model/pi-model.js";
import { TurnRunner, object } from "../../application/turn-runner.js";
import type { JsonObject, ModelSelection } from "../../application/model.js";

export async function startDesktopService(
  runDirectory: string, secret: string, stopFile: string, ownerPid: number, modelTestOnly = false,
): Promise<void> {
  if (!secret || !stopFile || !Number.isSafeInteger(ownerPid) || ownerPid < 1) throw new Error("Invalid desktop launch parameters");
  const game = new GameConnection(runDirectory);
  const provider = new PiModelAdapter();
  const streams = new Set<ServerResponse>();
  const events: { id: number; data: string }[] = [];
  const submissions = new Map<string, { text: string; mode: string; state: JsonObject }>();
  let sequence = 0;
  let lastStatus = "";
  let activeGameId: string | undefined;
  const publish = (type: string, payload: JsonObject) => {
    const data = JSON.stringify({ type, payload });
    if (type === "status") {
      if (lastStatus === data) return;
      lastStatus = data;
    }
    if (type === "life.chat.reply" && typeof payload.requestId === "string") {
      const id = `${payload.gameSessionId}:${payload.requestId}`;
      const record = submissions.get(id);
      if (record) record.state = payload;
    }
    const frame = { id: ++sequence, data };
    events.push(frame);
    if (events.length > 256) events.shift();
    for (const stream of streams) if (!stream.write(sse(frame))) stream.destroy();
  };
  // Preserve the current guidance in the closed loop. Capability-specific limits are explicit for this stage.
  const core = await readFile(join(runDirectory, "docs", "agent-core-instructions-draft.md"), "utf8");
  const runner = new TurnRunner(game, undefined, publish,
    core + "\n当前运行时支持 get_status 与 submit_work.action.navigate_to。按最新观察选择可站立的邻近格，工具回执确认后汇报。");
  game.on("changed", () => {
    const binding = game.binding;
    if (binding && activeGameId !== binding.gameSessionId) {
      activeGameId = binding.gameSessionId;
      submissions.clear();
    }
  });
  game.on("player-message", payload => {
    const binding = game.binding;
    if (!binding || typeof payload.requestId !== "string" || typeof payload.text !== "string") return;
    void runner.submit(binding, payload.requestId, payload.text, "send", false).catch(error => {
      const result = { ...binding, requestId: payload.requestId, status: "failed", error: error.message,
        profileRevision: 0, memoryRevision: 0 };
      try { game.notify("life.chat.reply", result); } catch { /* The binding has already changed. */ }
      publish("life.chat.reply", result);
    });
  });
  const token = Buffer.from(`Bearer ${secret}`);
  const server = createServer((req, res) => void route(req, res));
  server.requestTimeout = 30_000;
  let closing = false;
  const shutDown = () => {
    if (closing) return;
    closing = true;
    clearInterval(heartbeat);
    runner.stop();
    game.stop();
    for (const stream of streams) stream.end();
    server.close();
  };
  const heartbeat = setInterval(async () => {
    for (const stream of streams) if (!stream.write(": heartbeat\n\n")) stream.destroy();
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
          if (Number.isSafeInteger(cursor) && cursor >= (events[0]?.id ?? 1) - 1) {
            for (const frame of events) if (frame.id > cursor) res.write(sse(frame));
          }
          res.write(sse({ id: sequence, data: JSON.stringify({ type: "status", payload: runner.status() }) }));
          streams.add(res);
          res.on("close", () => streams.delete(res));
          return;
        }
        if (url.startsWith("/v2/commands/")) {
          const binding = game.binding;
          const id = url.slice("/v2/commands/".length);
          const result = binding ? submissions.get(`${binding.gameSessionId}:${id}`)?.state : undefined;
          return result ? respond(res, 200, result) : respond(res, 404, { error: "找不到指令。" });
        }
      }
      if (req.method !== "POST") return respond(res, 404, { error: "找不到操作。" });
      const body = await readBody(req);
      if (url === "/v2/model/test" || url === "/v2/model/configure") {
        const selection = object(body.selection);
        if (typeof selection.provider !== "string" || typeof selection.model !== "string" || typeof body.apiKey !== "string") {
          return respond(res, 400, { error: "请填写服务商、API Key 和模型。" });
        }
        const model = provider.open(selection as unknown as ModelSelection, body.apiKey);
        if (url === "/v2/model/configure") {
          runner.configure(model);
          return respond(res, 200, { ready: true });
        }
        const signal = AbortSignal.timeout(20_000);
        for await (const event of model.generate({ instructions: "Call the check_connection tool once.",
          messages: [{ role: "user", text: "Check connection." }],
          tools: [{ name: "check_connection", description: "Confirm this model can call a tool.",
            parameters: { type: "object", properties: {}, additionalProperties: false } }], signal })) {
          if (event.type === "error") throw new ModelError(event.code, event.message);
          if (event.type === "done" && event.calls.some(call => call.name === "check_connection")) {
            return respond(res, 200, { ready: true, toolCalling: true });
          }
        }
        throw new ModelError("unsupported", "模型已响应，工具调用尚未通过测试。");
      }
      const binding = game.binding;
      if (!binding || body.saveId !== binding.saveId || body.gameSessionId !== binding.gameSessionId) {
        return respond(res, 409, { error: "游戏会话已变化，请重新操作。" });
      }
      if (url === "/v2/control" && typeof body.action === "string") {
        return respond(res, 200, await runner.control(binding, body.action));
      }
      if (url === "/v2/commands") {
        const { requestId, text, mode = "send" } = body;
        if (typeof requestId !== "string" || !/^[a-zA-Z0-9_-]{1,80}$/.test(requestId)
          || typeof text !== "string" || !text.trim() || text.length > 500 || (mode !== "send" && mode !== "interrupt")
          || body.decisionId != null) return respond(res, 400, { error: "指令参数无效。" });
        const id = `${binding.gameSessionId}:${requestId}`;
        const existing = submissions.get(id);
        if (existing) return existing.text === text && existing.mode === mode
          ? respond(res, 200, existing.state) : respond(res, 409, { error: "请求编号已用于其他指令。" });
        const record = { text, mode, state: { ...binding, requestId, status: "accepted" } };
        submissions.set(id, record);
        try { await runner.submit(binding, requestId, text.trim(), mode); }
        catch (error) { submissions.delete(id); throw error; }
        if (submissions.size > 2048) submissions.delete(submissions.keys().next().value!);
        return respond(res, 202, record.state);
      }
      return respond(res, 404, { error: "找不到操作。" });
    } catch (error) {
      if (error instanceof ModelError) return respond(res, 409, { error: error.message, code: error.code });
      if (error instanceof SyntaxError) return respond(res, 400, { error: "请求格式无效。" });
      return respond(res, 409, { error: error instanceof Error ? error.message : "操作未完成。" });
    }
  }
}

function sse(event: { id: number; data: string }): string { return `id: ${event.id}\ndata: ${event.data}\n\n`; }

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
