import assert from "node:assert/strict";
import { once } from "node:events";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { WebSocketServer } from "ws";
import { classifyReceipt } from "../dist/adapters/game/action-receipt.js";
import { GameConnection } from "../dist/adapters/game/game-connection.js";
import { TurnRunner } from "../dist/application/turn-runner.js";

const samples = JSON.parse(await readFile(
  new URL("../../../contracts/game-bridge/examples/v2/action-lifecycle.json", import.meta.url), "utf8"));
for (const sample of samples) {
  test(sample.name, () => {
    assert.equal(classifyReceipt(sample.message, { saveId: "farm", gameSessionId: sample.gameSessionId },
      sample.expectedActionId, new Set(sample.settledActionIds)), sample.expected);
  });
}

test("reconnection waits for the game's recovery state before exposing a binding", async t => {
  const dir = await mkdtemp(join(tmpdir(), "companion-game-"));
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  const game = new GameConnection(dir);
  t.after(async () => {
    game.stop();
    await new Promise(resolve => server.close(resolve));
    await rm(dir, { recursive: true, force: true });
  });
  const runner = new TurnRunner(game, {}, () => undefined, "");
  const binding = { saveId: "farm", gameSessionId: "spring-5" };
  const envelope = { ...binding, protocolVersion: "2", worldRevision: 1 };
  await once(server, "listening");
  await mkdir(join(dir, "data"));
  await writeFile(join(dir, "data", "transport-discovery.json"), JSON.stringify({
    ...envelope, port: server.address().port, sessionToken: "local-test",
  }));
  const chatReady = new Promise(resolve => {
    server.on("connection", (socket, request) => {
      if (request.url === "/chat") { resolve(socket); return; }
      socket.once("message", raw => {
        socket.send(JSON.stringify({ ...envelope, messageType: "mod.welcome", correlationId: JSON.parse(raw).messageId,
          payload: { selectedProtocolVersion: "2" } }));
        socket.send(JSON.stringify({ ...envelope, messageType: "world.snapshot", payload: { controlOwner: "human" } }));
      });
    });
  });

  const snapshot = once(game, "changed", { signal: AbortSignal.timeout(5000) });
  game.start();
  await snapshot;
  const chat = await chatReady;
  const chatMessage = once(game, "player-event", { signal: AbortSignal.timeout(5000) });
  chat.send(JSON.stringify({ ...envelope, messageType: "life.profile.get", payload: {} }));
  await chatMessage;
  assert.equal(game.binding, undefined);
  assert.equal(runner.status().connection.ready, false);

  const state = once(game, "changed", { signal: AbortSignal.timeout(5000) });
  chat.send(JSON.stringify({ ...envelope, messageType: "desktop.control.result",
    payload: { recoveryPending: true, paused: true, interrupting: true } }));
  await state;
  assert.deepEqual(game.binding, binding);
  assert.equal(runner.status().recoveryPending, true);
  assert.equal(runner.status().connection.ready, false);
});
