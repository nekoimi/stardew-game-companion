import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createInterface } from "node:readline";
import { setTimeout } from "node:timers/promises";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { WebSocketServer } from "ws";
import { CompanionStore } from "../dist/adapters/storage/companion-store.js";

test("desktop service replays and streams large work states without losing its game binding", async t => {
  // A fake game socket plus a disposable runtime; this never launches a game.
  const dir = await mkdtemp(join(tmpdir(), "companion-events-"));
  const mod = join(dir, "Mods", "fixture");
  await mkdir(join(mod, "docs"), { recursive: true });
  await mkdir(join(mod, "data"));
  await writeFile(join(mod, "docs", "agent-ts-instructions.md"), "Isolated transport fixture");
  const binding = { saveId: "farm", gameSessionId: "game" };
  const store = new CompanionStore(join(dir, "data", "companion.sqlite"));
  for (let index = 0; index < 40; index++) store.recordReceipt(binding, `action-${index}`, {
    commandId: `action-${index}`, ...binding, skillId: "water-zone", terminalState: "succeeded",
    workContext: { goalId: "care", requestId: "request", target: Array.from({ length: 20 }, (_, x) => ({ x, y: 12 })) },
  });
  store.close();
  const game = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await once(game, "listening");
  const envelope = { ...binding, protocolVersion: "2", worldRevision: 1 };
  await writeFile(join(mod, "data", "transport-discovery.json"), JSON.stringify({
    ...envelope, port: game.address().port, sessionToken: "isolated-test",
  }));
  let command, chat, welcomed = false;
  const update = progress => chat.send(JSON.stringify({ ...envelope, messageType: "desktop.control.result",
    payload: { recoveryPending: false, progress, controlOwner: "human" } }));
  function welcome() {
    if (!command || !chat || !welcomed) return;
    command.send(JSON.stringify({ ...envelope, messageType: "world.snapshot",
      payload: { companion: { locationId: "Farm" }, world: { year: 1, season: "spring", dayOfMonth: 5, timeOfDay: 600 } } }));
    update("ready");
  }
  game.on("connection", (socket, request) => {
    if (request.url === "/chat") { chat = socket; welcome(); return; }
    command = socket;
    socket.once("message", raw => {
      socket.send(JSON.stringify({ ...envelope, messageType: "mod.welcome", correlationId: JSON.parse(raw).messageId,
        payload: { selectedProtocolVersion: "2" } }));
      welcomed = true;
      welcome();
    });
  });
  const stop = join(dir, "stop");
  const child = spawn(process.execPath, ["--disable-warning=ExperimentalWarning",
    fileURLToPath(new URL("../dist/bootstrap.js", import.meta.url)), "--desktop", mod, String(process.pid)], {
    env: { ...process.env, NODE_OPTIONS: "", STARDEW_MODEL_TEST_ONLY: "", STARDEW_AGENT_ACCEPTANCE_TRACE: "",
      STARDEW_DESKTOP_TOKEN: "test-events", STARDEW_BRIDGE_STOP_FILE: stop },
    stdio: ["ignore", "pipe", "ignore"], windowsHide: true,
  });
  const exited = once(child, "exit");
  t.after(async () => {
    await writeFile(stop, "stop");
    await Promise.race([exited, setTimeout(3000, undefined, { ref: false })]);
    if (child.exitCode === null) { child.kill(); await exited; }
    for (const socket of game.clients) socket.terminate();
    await new Promise(resolve => game.close(resolve));
    await rm(dir, { recursive: true, force: true });
  });
  const lines = createInterface({ input: child.stdout });
  const [line] = await once(lines, "line", { signal: AbortSignal.timeout(10_000) });
  const address = `http://127.0.0.1:${JSON.parse(line).port}`;
  const headers = { authorization: "Bearer test-events" };
  async function status(progress) {
    for (let attempt = 0; attempt < 300; attempt++) {
      const value = await (await fetch(address + "/v2/status", { headers })).json();
      if (value.ready && value.progress === progress) return value;
      await setTimeout(10);
    }
    throw new Error("Isolated game binding did not receive the expected state");
  }
  const initial = await status("ready");
  assert.ok(Buffer.byteLength(JSON.stringify(initial)) > 16_384);
  for (let i = 0; i < 80; i++) update(`history-${i}`);
  await status("history-79");
  const response = await fetch(address + "/v2/events", { headers, signal: AbortSignal.timeout(10_000) });
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "", liveSeen = false;
  const ids = [];
  update("live");
  while (!liveSeen) {
    const chunk = await reader.read();
    assert.equal(chunk.done, false, "Large status/replay destroyed the production SSE response");
    buffer += decoder.decode(chunk.value, { stream: true });
    let boundary;
    while ((boundary = buffer.indexOf("\n\n")) >= 0) {
      const frame = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      if (!frame.startsWith("id: ")) continue;
      const [id, data] = frame.split("\n");
      const event = JSON.parse(data.slice(6));
      ids.push(Number(id.slice(4)));
      if (event.type === "status" && event.payload.progress === "live") {
        assert.equal(event.payload.ready, true);
        assert.equal(event.payload.work.recentExecutions.length, 40);
        liveSeen = true;
      }
    }
  }
  assert.ok(ids.length >= 80, "Reconnect history was not exercised");
  assert.deepEqual(ids, [...ids].sort((a, b) => a - b));
  await reader.cancel();
  assert.equal((await status("live")).gameSessionId, binding.gameSessionId);
});
