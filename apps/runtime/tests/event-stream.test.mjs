import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import { Writable } from "node:stream";
import { setTimeout } from "node:timers/promises";
import { test } from "node:test";
import { DesktopEventStream } from "../dist/adapters/desktop/event-stream.js";

test("large history replay and live events share an ordered writer across native HTTP drain", async t => {
  let drains = 0;
  let writer;
  const detail = "农场工作进展".repeat(4000); // The full replay exceeds the live queue limit.
  const server = createServer({ highWaterMark: 1024 }, (_request, response) => {
    response.writeHead(200, { "Content-Type": "text/event-stream" });
    response.on("drain", () => drains++);
    // Match the maximum reconnect replay, then append a fresh snapshot/live reply.
    const replay = Array.from({ length: 256 }, (_, index) => ({ id: index + 1,
      data: JSON.stringify({ type: "status", payload: { id: index + 1, detail } }) }));
    writer = new DesktopEventStream(response, undefined, replay);
    writer.send({ id: 257, data: JSON.stringify({ type: "life.chat.reply", payload: { text: "已完成" } }) });
    writer.heartbeat();
  });
  t.after(() => { writer?.close(); server.closeAllConnections(); server.close(); });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const response = await fetch(`http://127.0.0.1:${server.address().port}`, { signal: AbortSignal.timeout(10_000) });
  await setTimeout(50); // The reader can lag without being classified as disconnected.
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  const received = [];
  let buffer = "";
  while (received.length < 257) {
    const chunk = await reader.read();
    assert.equal(chunk.done, false, "The server destroyed a valid backpressured stream");
    buffer += decoder.decode(chunk.value, { stream: true });
    let boundary;
    while ((boundary = buffer.indexOf("\n\n")) >= 0) {
      const frame = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      if (!frame.startsWith("id: ")) continue;
      const [id, data] = frame.split("\n");
      received.push(Number(id.slice(4)));
      const event = JSON.parse(data.slice(6));
      if (received.length <= 256) assert.equal(event.payload.detail, detail);
      else assert.equal(event.payload.text, "已完成");
    }
  }
  assert.deepEqual(received, Array.from({ length: 257 }, (_, index) => index + 1));
  assert.ok(drains > 0, "Regression did not exercise native backpressure");
  await reader.cancel();
});

class HeldResponse extends Writable {
  constructor() { super({ highWaterMark: 1 }); }
  _write(_chunk, _encoding, _callback) { /* Simulate a reader that never drains. */ }
}

test("a stalled reader is released without affecting other subscribers or leaving drain listeners", async () => {
  const blocked = new HeldResponse();
  const healthy = new Writable({ write(_chunk, _encoding, callback) { callback(); } });
  const slow = new DesktopEventStream(blocked, { bytes: 4096, frames: 8, stallMs: 25 });
  const fast = new DesktopEventStream(healthy);
  slow.send({ id: 1, data: "first" });
  slow.send({ id: 2, data: "second" });
  for (let i = 0; i < 100; i++) slow.heartbeat();
  fast.send({ id: 1, data: "unaffected" });
  await Promise.all([once(blocked, "close"), setTimeout(50)]);
  assert.equal(blocked.destroyed, true);
  assert.equal(blocked.listenerCount("drain"), 0);
  assert.equal(healthy.destroyed, false);
  slow.send({ id: 3, data: "late" });
  fast.close();
});

test("bounded slow-reader queues disconnect cleanly and client cancellation removes queued writers", async () => {
  const overflow = new HeldResponse();
  const stream = new DesktopEventStream(overflow, { bytes: 100, frames: 2, stallMs: 1000 });
  const closed = once(overflow, "close");
  stream.send({ id: 1, data: "x".repeat(40) });
  stream.send({ id: 2, data: "x".repeat(40) });
  await closed;
  assert.equal(overflow.listenerCount("drain"), 0);

  const cancelled = new HeldResponse();
  const pending = new DesktopEventStream(cancelled);
  pending.send({ id: 1, data: "accepted before disconnect" });
  pending.send({ id: 2, data: "queued" });
  const cancellation = once(cancelled, "close");
  cancelled.destroy();
  await cancellation;
  assert.equal(cancelled.listenerCount("drain"), 0);
  pending.heartbeat();
  pending.close();
});
