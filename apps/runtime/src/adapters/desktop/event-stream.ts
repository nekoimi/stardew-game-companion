import type { ServerResponse } from "node:http";

export interface DesktopEvent { id: number; data: string }

/** One ordered writer for replay, live events and heartbeats. A false write
 * means the frame was accepted: wait for drain before sending another frame. */
export class DesktopEventStream {
  private readonly queue: string[] = [];
  private bytes = 0;
  private blocked = false;
  private closed = false;
  private stalled?: NodeJS.Timeout;
  private replay?: Iterator<DesktopEvent>;

  constructor(private readonly response: ServerResponse,
              private readonly limits = { bytes: 8 * 1024 * 1024, frames: 512, stallMs: 30_000 },
              replay: Iterable<DesktopEvent> = []) {
    this.replay = replay[Symbol.iterator]();
    response.on("drain", this.drain);
    response.once("close", this.cleanup);
    response.once("error", this.cleanup);
    this.flush();
  }

  send(event: DesktopEvent): void { this.enqueue(this.format(event)); }

  heartbeat(): void {
    // Any queued data already keeps the connection active. Never build a
    // heartbeat backlog behind a slow reader.
    if (!this.blocked && !this.replay && !this.queue.length) this.enqueue(": heartbeat\n\n");
  }

  close(): void {
    if (this.closed) return;
    this.cleanup();
    this.response.end();
  }

  private enqueue(frame: string): void {
    if (this.closed || this.response.destroyed || this.response.writableEnded) return;
    const size = Buffer.byteLength(frame);
    if (this.queue.length >= this.limits.frames
        || this.bytes + this.response.writableLength + size > this.limits.bytes) {
      this.disconnect();
      return;
    }
    this.queue.push(frame);
    this.bytes += size;
    this.flush();
  }

  private flush(): void {
    while (!this.closed && !this.blocked) {
      let frame: string;
      if (this.replay) {
        const next = this.replay.next();
        if (next.done) { this.replay = undefined; continue; }
        frame = this.format(next.value);
        // History may be larger than the live queue limit. Materialize and send
        // one frame at a time so reconnect cannot overflow before it can drain.
        if (Buffer.byteLength(frame) + this.response.writableLength > this.limits.bytes) {
          this.disconnect();
          return;
        }
      } else {
        if (!this.queue.length) return;
        frame = this.queue.shift()!;
        this.bytes -= Buffer.byteLength(frame);
      }
      try {
        if (!this.response.write(frame)) {
          this.blocked = true;
          this.stalled = setTimeout(() => this.disconnect(), this.limits.stallMs);
          this.stalled.unref();
        }
      } catch { this.disconnect(); }
    }
  }

  private readonly drain = (): void => {
    clearTimeout(this.stalled);
    this.stalled = undefined;
    this.blocked = false;
    this.flush();
  };

  private readonly cleanup = (): void => {
    this.closed = true;
    clearTimeout(this.stalled);
    this.queue.length = 0;
    this.bytes = 0;
    this.replay?.return?.();
    this.replay = undefined;
    this.response.off("drain", this.drain);
    this.response.off("close", this.cleanup);
    this.response.off("error", this.cleanup);
  };

  private disconnect(): void {
    this.cleanup();
    this.response.destroy(); // Only a stalled or bounded-out reader is disconnected.
  }

  private format(event: DesktopEvent): string { return `id: ${event.id}\ndata: ${event.data}\n\n`; }
}
