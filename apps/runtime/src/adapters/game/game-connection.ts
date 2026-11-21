import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { EventEmitter } from "node:events";
import WebSocket from "ws";
import type { JsonObject } from "../../application/model.js";
import { object } from "../../application/model.js";
import { classifyReceipt, gameProtocolVersion, isTerminalState } from "./action-receipt.js";
import type { GameBinding } from "./action-receipt.js";

interface Discovery extends GameBinding {
  protocolVersion: string;
  port: number;
  sessionToken: string;
}
interface Envelope extends GameBinding {
  protocolVersion: string;
  messageType: string;
  messageId: string;
  correlationId?: string;
  worldRevision: number;
  payload: JsonObject;
}
interface Pending {
  match: (message: Envelope) => boolean;
  finish: (value: Envelope | Error) => void;
  observe?: (message: Envelope) => void;
}

/** The chat socket carries player input and ownership. The command socket owns native action receipts. */
export class GameConnection extends EventEmitter {
  private descriptor?: Discovery;
  private chat?: WebSocket;
  private commands?: WebSocket;
  private timer?: NodeJS.Timeout;
  private polling = false;
  private stopped = false;
  private connecting = false;
  private generation = 0;
  private welcomed = false;
  private helloId?: string;
  private sequence = 0;
  private revision = 0;
  private readonly instanceId = randomUUID();
  private readonly pending = new Set<Pending>();
  private readonly settled = new Set<string>();
  snapshot?: JsonObject;
  state: JsonObject = {};
  actorMode = "player";

  constructor(private readonly modDirectory: string) { super(); }

  get binding(): GameBinding | undefined {
    if (!this.descriptor || !this.welcomed || !this.snapshot || this.chat?.readyState !== WebSocket.OPEN
      || typeof this.state.recoveryPending !== "boolean") return;
    return { saveId: this.descriptor.saveId, gameSessionId: this.descriptor.gameSessionId };
  }

  start(): void {
    void this.poll();
    this.timer = setInterval(() => void this.poll(), 500);
  }

  stop(): void {
    this.stopped = true;
    clearInterval(this.timer);
    this.disconnect();
  }

  private async poll(): Promise<void> {
    if (this.stopped || this.polling) return;
    this.polling = true;
    try {
      let next: Discovery | undefined;
      try {
        next = JSON.parse(await readFile(join(this.modDirectory, "data", "transport-discovery.json"), "utf8"));
      } catch { /* The game removes discovery when returning to title. */ }
      if (this.stopped) return;
      if (!next || next.protocolVersion !== gameProtocolVersion || typeof next.sessionToken !== "string"
        || typeof next.saveId !== "string" || typeof next.gameSessionId !== "string"
        || !Number.isInteger(next.port) || next.port < 1 || next.port > 65535) {
        if (this.descriptor) this.disconnect();
        return;
      }
      if (this.descriptor?.gameSessionId !== next.gameSessionId || this.descriptor?.sessionToken !== next.sessionToken) {
        this.disconnect();
        this.descriptor = next;
      }
      if (!this.connecting && !this.commands && !this.chat) this.connect(next);
    } finally { this.polling = false; }
  }

  private connect(descriptor: Discovery): void {
    this.connecting = true;
    const generation = ++this.generation;
    const options = { headers: { Authorization: `Bearer ${descriptor.sessionToken}` },
      handshakeTimeout: 3000, maxPayload: 8 * 1024 * 1024 };
    const address = `ws://127.0.0.1:${descriptor.port}`;
    const commands = this.commands = new WebSocket(address + "/", options);
    const chat = this.chat = new WebSocket(address + "/chat", options);
    for (const [socket, commandChannel] of [[commands, true], [chat, false]] as const) {
      socket.on("error", () => { if (generation === this.generation) this.disconnect(true); });
      socket.on("close", () => { if (generation === this.generation) this.disconnect(true); });
      socket.on("message", data => {
        if (generation !== this.generation) return;
        try { this.receive(JSON.parse(data.toString()), descriptor, commandChannel); }
        catch { /* Malformed native events cannot be treated as receipts. */ }
      });
    }
    commands.on("open", () => {
      if (generation !== this.generation) return;
      this.helloId = randomUUID();
      this.send(commands, "runtime.hello", {
        supportedProtocolVersions: [gameProtocolVersion], runtimeVersion: "0.1.0", sessionToken: null, features: {},
      }, this.helloId);
    });
    chat.on("open", () => {
      if (generation !== this.generation) return;
      if (this.binding) this.emit("changed");
    });
  }

  private disconnect(keepDescriptor = false): void {
    const hadBinding = !!this.binding || !!this.snapshot;
    this.generation++;
    const sockets = [this.commands, this.chat];
    this.commands = this.chat = undefined;
    this.connecting = this.welcomed = false;
    this.snapshot = undefined;
    this.state = {};
    this.revision = 0;
    if (!keepDescriptor) {
      this.descriptor = undefined;
      this.settled.clear();
    }
    for (const socket of sockets) socket?.terminate();
    for (const pending of this.pending) pending.finish(new Error("游戏连接中断，动作结果待核对。"));
    if (hadBinding) this.emit("changed");
  }

  private receive(message: Envelope, descriptor: Discovery, commandChannel: boolean): void {
    if (message.protocolVersion !== gameProtocolVersion || message.saveId !== descriptor.saveId
      || message.gameSessionId !== descriptor.gameSessionId || !message.payload) return;
    if (message.messageType === "mod.welcome" && commandChannel) {
      if (message.payload.selectedProtocolVersion !== gameProtocolVersion || message.correlationId !== this.helloId) return;
      this.welcomed = true;
      this.connecting = false;
      this.actorMode = String(message.payload.actorMode ?? "player");
    }
    if (!this.welcomed) return;
    if (message.messageType === "world.snapshot" && message.worldRevision >= this.revision) {
      this.revision = message.worldRevision;
      this.snapshot = message.payload;
      if (message.payload.controlOwner) this.state.controlOwner = message.payload.controlOwner;
      this.emit("changed");
    } else if (message.messageType === "desktop.control.result") {
      for (const [key, value] of Object.entries(message.payload)) {
        if (!["requestId", "result", "error", "observation"].includes(key)) this.state[key] = value;
      }
      this.emit("changed");
    } else if (message.messageType === "player.authority") {
      this.state.controlOwner = message.payload.owner;
      this.emit("changed");
    }
    for (const pending of this.pending) {
      if (pending.match(message)) pending.finish(message);
      else pending.observe?.(message);
    }
    if (message.messageType === "life.chat.submit" || message.messageType === "chat.submit") {
      this.emit("player-message", message.messageType, message.payload);
    } else if (["life.profile.get", "life.profile.set", "life.memory.list", "life.memory.edit",
      "life.milestones.get", "autonomy.control", "chat.cancel"].includes(message.messageType)) {
      this.emit("player-event", message.messageType, message.payload);
    }
  }

  private send(socket: WebSocket | undefined, messageType: string, payload: JsonObject, messageId: string = randomUUID(),
      action?: { idempotencyKey: string; expiresAt: string }): void {
    if (!this.descriptor || socket?.readyState !== WebSocket.OPEN) throw new Error("尚未连接当前游戏。");
    const { saveId, gameSessionId } = this.descriptor;
    socket.send(JSON.stringify({ protocolVersion: gameProtocolVersion, messageType, messageId,
      senderInstanceId: this.instanceId, sequenceNumber: this.sequence++, worldRevision: this.revision,
      sentAt: new Date().toISOString(), saveId, gameSessionId, payload, ...action }));
  }

  private request(socket: WebSocket | undefined, type: string, payload: JsonObject,
      match: (message: Envelope) => boolean, timeoutMs: number, id = randomUUID(),
      action?: { idempotencyKey: string; expiresAt: string }, progressing?: (message: Envelope) => boolean): Promise<Envelope> {
    return new Promise((resolve, reject) => {
      const pending: Pending = { match, finish: value => {
        clearTimeout(timer);
        this.pending.delete(pending);
        if (value instanceof Error) reject(value); else resolve(value);
      }, observe: progressing ? message => { if (progressing(message)) timer.refresh(); } : undefined };
      const timer = setTimeout(() => pending.finish(new Error("游戏响应超时，结果待核对。")), timeoutMs);
      this.pending.add(pending);
      try { this.send(socket, type, payload, id, action); }
      catch (error) { pending.finish(error instanceof Error ? error : new Error("无法发送游戏请求。")); }
    });
  }

  async control(action: string, extra: JsonObject = {}): Promise<JsonObject> {
    const requestId = randomUUID();
    const reply = await this.request(this.chat, "desktop.control", { requestId, action, ...extra },
      message => message.messageType === "desktop.control.result" && message.payload.requestId === requestId, 5000);
    if (reply.payload.error) throw new Error(`游戏未确认操作（${reply.payload.error}）。`);
    return reply.payload;
  }

  notify(type: string, payload: JsonObject): void {
    if (this.binding) this.send(this.chat, type, payload);
  }

  async observe(): Promise<JsonObject> {
    const response = await this.control("observe");
    const observed = object(response.observation);
    if (!Object.keys(observed).length) throw new Error("游戏尚未返回当前观察。");
    const revision = Number(observed.capturedRevision ?? 0);
    if (Number.isSafeInteger(revision) && revision >= this.revision) {
      this.snapshot = observed;
      this.revision = revision;
    }
    return this.snapshot ?? observed;
  }

  async execute(actionId: string, turnId: string, skill: string, parameters: JsonObject): Promise<JsonObject> {
    const binding = this.binding;
    if (!binding) throw new Error("尚未连接当前游戏。");
    const expectedRevision = this.revision;
    const maxWater = skill === "water-zone" && Array.isArray(parameters.tiles) ? parameters.tiles.length : 0;
    let progress = "";
    const reply = await this.request(this.commands, "skill.execute", {
      commandId: actionId, taskId: turnId, skillId: skill, skillVersion: "0.1", expectedWorldRevision: expectedRevision,
      parameters, budgets: { maxGameMinutes: 0, maxStamina: 50, maxWater },
      cancelPolicy: "safe-point", policyDecisionId: turnId,
    }, message => this.isReceipt(message, binding, actionId), 30_000, randomUUID(),
    { idempotencyKey: `${binding.gameSessionId}:${actionId}`, expiresAt: new Date(Date.now() + 10_000).toISOString() },
    message => {
      if (this.state.activeCommandId !== actionId) return false;
      if (this.state.paused === true) return true;
      if (message.messageType !== "world.snapshot" && message.messageType !== "desktop.control.result") return false;
      const actor = object(this.snapshot?.companion);
      const state = JSON.stringify([this.state.progress, actor.tileX, actor.tileY, actor.stamina, actor.waterCanLevel]);
      if (state === progress) return false;
      progress = state;
      return true;
    });
    this.settled.add(actionId);
    if (this.settled.size > 512) this.settled.delete(this.settled.values().next().value!);
    return reply.payload;
  }

  private isReceipt(message: Envelope, binding: GameBinding, actionId: string): boolean {
    return classifyReceipt(message, binding, actionId, this.settled) === "terminal";
  }

  async queryAction(actionId: string): Promise<JsonObject | undefined> {
    const binding = this.binding;
    if (!binding) return;
    const id = randomUUID();
    const reply = await this.request(this.commands, "skill.result.query", { commandId: actionId }, message =>
      message.correlationId === id && (message.messageType === "skill.result.status" || message.messageType === "skill.result"),
    5000, id);
    if (reply.messageType === "skill.result" && reply.payload.commandId === actionId && isTerminalState(reply.payload.terminalState)) {
      return reply.payload;
    }
  }
}
