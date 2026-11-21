import { randomUUID } from "node:crypto";
import type { GameConnection } from "../adapters/game/game-connection.js";
import type { GameBinding } from "../adapters/game/action-receipt.js";
import type { JsonObject, JsonValue, ModelContext, ModelEvent, ModelMessage, ToolDefinition } from "./model.js";

const tools: ToolDefinition[] = [
  { name: "get_status", description: "Read the current native game observation and control state.",
    parameters: { type: "object", properties: {}, additionalProperties: false } },
  { name: "submit_work", description: "Execute one native action immediately and wait for its result. For this build, navigate_to is available.",
    parameters: { type: "object", required: ["action"], additionalProperties: false, properties: {
      action: { type: "object", required: ["navigate_to"], additionalProperties: false, properties: {
        navigate_to: { type: "object", required: ["location_id", "tile"], additionalProperties: false, properties: {
          location_id: { type: "string" }, tile: { type: "object", required: ["x", "y"], additionalProperties: false,
            properties: { x: { type: "integer", minimum: 0 }, y: { type: "integer", minimum: 0 } } },
        } },
      } },
    } } },
];

type Complete = Extract<ModelEvent, { type: "done" }>;
interface Turn {
  id: string;
  requestId: string;
  binding: GameBinding;
  abort: AbortController;
  nativeAction?: string;
  additional: { requestId: string; text: string }[];
  requests: string[];
  settled: Promise<void>;
}

export function object(value: JsonValue | undefined): JsonObject {
  return value && typeof value === "object" && !Array.isArray(value) ? value : {};
}

export class TurnRunner {
  private active?: Turn;
  private uncertain?: { binding: GameBinding; actionId: string };
  private previousBinding?: GameBinding;
  private owner?: JsonValue;
  private readonly messages: ModelMessage[] = [];
  private transition = false;

  constructor(private readonly game: GameConnection, private model: ModelContext | undefined,
              private readonly publish: (type: string, payload: JsonObject) => void,
              private readonly rules: string) {
    game.on("changed", () => {
      const binding = game.binding;
      if (!binding || (this.previousBinding && this.previousBinding.gameSessionId !== binding.gameSessionId)) {
        this.active?.abort.abort();
      }
      if (binding && this.previousBinding?.saveId !== binding.saveId) this.messages.length = 0;
      if (binding && this.uncertain && this.uncertain.binding.gameSessionId !== binding.gameSessionId) this.uncertain = undefined;
      if (binding && game.actorMode === "player" && game.state.controlOwner === "human" && this.owner === "agent") {
        this.active?.abort.abort();
      }
      if (binding) this.previousBinding = binding;
      this.owner = game.state.controlOwner;
      this.changed();
    });
  }

  configure(model: ModelContext | undefined): void {
    if (this.active || this.transition) throw new Error("请等当前回合结束再切换模型。");
    this.model = model;
    this.changed();
  }

  status(): JsonObject {
    const binding = this.game.binding;
    return { ...binding, ready: !!binding, actorMode: this.game.actorMode,
      controlOwner: this.game.state.controlOwner ?? (this.game.actorMode === "player" ? "human" : "agent"),
      paused: this.game.state.paused === true, interrupting: this.transition || this.game.state.interrupting === true,
      recoveryPending: !!this.uncertain || this.game.state.recoveryPending === true,
      activeCommandId: this.active?.nativeAction ?? "", activity: this.game.state.activity ?? "idle",
      progress: this.game.state.progress ?? "", clockPauseOwner: this.game.state.clockPauseOwner ?? null,
      thinking: !!this.active && !this.active.nativeAction && !this.active.abort.signal.aborted,
      connection: { ready: !!this.model && !!binding && !this.uncertain && this.game.state.recoveryPending !== true,
        source: "builtin", status: this.model ? "connected" : "waiting", clientName: "模型 API", ownedAdapter: false },
      work: { activeGoals: [], recentTodos: [], recentExecutions: [] }, decisions: [], closedDecisions: [], milestones: [],
      unreadMessages: 0 };
  }

  changed(): void { this.publish("status", this.status()); }

  async submit(binding: GameBinding, requestId: string, text: string, mode: "send" | "interrupt",
      recordInGame = true): Promise<void> {
    this.assertBinding(binding);
    if (!this.model) throw new Error("请先配置模型服务。");
    if (this.transition || this.uncertain || this.game.state.recoveryPending === true) {
      throw new Error("请先核对上一项动作。");
    }
    if (this.active && mode === "send" && this.active.additional.length >= 8) {
      throw new Error("待处理的补充要求已满，请稍后再试。");
    }
    this.transition = true;
    try {
      if (mode === "interrupt") {
        if (this.game.state.paused === true || (this.game.actorMode === "player" && this.game.state.controlOwner !== "agent")) {
          throw new Error("当前已暂停或由玩家控制，请先恢复游戏工作。");
        }
        await this.interrupt(binding);
      }
      if (recordInGame) await this.game.control("record", { conversationRequestId: requestId, text });
      this.assertBinding(binding);
      this.publish("submitted", { ...binding, requestId, text, status: "accepted" });
      if (this.active && mode === "send") {
        this.active.additional.push({ requestId, text });
        this.active.requests.push(requestId);
        return;
      }
      const turn: Turn = { id: randomUUID(), requestId, binding, abort: new AbortController(), additional: [],
        requests: [requestId], settled: Promise.resolve() };
      const model = this.model;
      this.active = turn;
      turn.settled = this.run(turn, model, text);
    } finally {
      this.transition = false;
      this.changed();
    }
  }

  private assertBinding(binding: GameBinding): void {
    const current = this.game.binding;
    if (!current || current.saveId !== binding.saveId || current.gameSessionId !== binding.gameSessionId) {
      throw new Error("游戏会话已变化，请重新操作。");
    }
  }

  private async interrupt(binding: GameBinding): Promise<void> {
    const old = this.active;
    old?.abort.abort();
    await this.game.control("interrupt");
    await old?.settled;
    this.assertBinding(binding);
    if (this.uncertain) throw new Error("旧动作的终态待核对。");
    await this.game.control("unhold");
  }

  async control(binding: GameBinding, action: string): Promise<JsonObject> {
    this.assertBinding(binding);
    if (this.transition && action !== "takeover") throw new Error("请等待旧动作停止。");
    if (action === "takeover") this.active?.abort.abort();
    if (action === "cancel") {
      this.transition = true;
      try { await this.interrupt(binding); }
      finally { this.transition = false; this.changed(); }
      return this.status();
    }
    if (action === "recover") {
      if (this.uncertain) {
        const result = await this.game.queryAction(this.uncertain.actionId);
        if (!result) throw new Error("游戏尚未确认旧动作的终态。");
        this.uncertain = undefined;
        this.publish("result", { ...binding, ...result });
      }
      const result = await this.game.control("recover");
      if (this.game.state.interrupting === true) await this.game.control("unhold");
      this.publish("recovery", { ...binding, hasReceipt: !!result.result });
      this.changed();
      return result;
    }
    if (!["pause", "resume", "takeover", "handoff"].includes(action)) throw new Error("未知的控制操作。");
    if (this.uncertain && ["resume", "handoff"].includes(action)) throw new Error("请先核对旧动作。");
    return this.game.control(action);
  }

  stop(): void { this.active?.abort.abort(); }

  private async run(turn: Turn, model: ModelContext, text: string): Promise<void> {
    const messages: ModelMessage[] = [...this.messages, { role: "user", text }];
    const signal = turn.abort.signal;
    let answer = "";
    let failure = "";
    const acknowledgeAdditional = (): boolean => {
      const arrived = turn.additional.splice(0);
      for (const item of arrived) messages.push({ role: "user", text: item.text });
      return arrived.length > 0;
    };
    const lease = (state: string) => {
      try {
        this.assertBinding(turn.binding);
        if (this.game.actorMode === "player") {
          this.game.notify("agent.thinking", { state, leaseId: turn.id });
        }
      } catch { /* A game disconnect already releases its thinking lease. */ }
    };
    try {
      while (!signal.aborted) {
        acknowledgeAdditional();
        lease("begin");
        const renewal = setInterval(() => lease("renew"), 3000);
        let output: Complete | undefined;
        const idle = new AbortController();
        const idleTimer = setTimeout(() => idle.abort(), 45_000);
        try {
          for await (const event of model.generate({ instructions: this.rules, messages, tools,
            signal: AbortSignal.any([signal, idle.signal]) })) {
            idleTimer.refresh();
            if (signal.aborted) break;
            if (event.type === "text") this.publish("progress", { ...turn.binding, requestId: turn.requestId,
              turnId: turn.id, delta: event.delta });
            if (event.type === "error") throw new Error(idle.signal.aborted ? "模型长时间未响应。" : event.message);
            if (event.type === "done") output = event;
          }
        } finally { clearTimeout(idleTimer); clearInterval(renewal); lease("end"); }
        if (signal.aborted) break;
        if (!output || output.reason === "length") throw new Error("模型未完成本轮回复。");
        const response = output;
        if (!response.calls.length) {
          if (acknowledgeAdditional()) continue;
          answer = response.text;
          break;
        }
        messages.push({ role: "assistant", text: response.text, calls: response.calls });
        for (const call of response.calls) {
          if (signal.aborted) break;
          if (turn.additional.length) {
            messages.push({ role: "tool", callId: call.id, name: call.name,
              result: { error: "收到玩家的新要求，先按新要求重新安排。" }, failed: true });
            continue;
          }
          this.assertBinding(turn.binding);
          let result: JsonValue;
          let failed = false;
          try { result = await this.callTool(turn, call.name, call.arguments); }
          catch (error) { failed = true; result = { error: error instanceof Error ? error.message : "工具执行失败。" }; }
          if (this.uncertain) throw new Error("旧动作终态待核对，已停止继续派工。");
          messages.push({ role: "tool", callId: call.id, name: call.name, result, failed });
        }
      }
    } catch (error) {
      failure = error instanceof Error ? error.message : "Agent 回合未完成。";
    } finally {
      if (signal.aborted) failure = "本轮已停止。";
      const current = this.game.binding;
      if (current?.gameSessionId === turn.binding.gameSessionId) {
        if (!failure && answer) {
          this.messages.push(...messages.slice(this.messages.length).filter(message => message.role === "user"),
            { role: "assistant", text: answer, calls: [] });
          if (this.messages.length > 32) this.messages.splice(0, this.messages.length - 32);
        }
        for (const requestId of turn.requests) {
          const primary = requestId === turn.requests.at(-1);
          const reply = { ...turn.binding, requestId, turnId: turn.id,
            status: failure ? "failed" : "completed", replyText: primary ? answer || null : null,
            error: primary ? failure || null : null, profileRevision: 0, memoryRevision: 0 };
          try { this.game.notify("life.chat.reply", reply); } catch { /* The desktop receives the same result. */ }
          this.publish("life.chat.reply", reply);
        }
      }
      if (this.active === turn) this.active = undefined;
      this.changed();
    }
  }

  private async callTool(turn: Turn, name: string, args: JsonObject): Promise<JsonValue> {
    if (name === "get_status" && Object.keys(args).length === 0) {
      return { world: this.game.snapshot ?? {}, controlOwner: this.game.state.controlOwner ?? "human" };
    }
    const action = object(args.action);
    const move = object(action.navigate_to);
    const tile = object(move.tile);
    if (name !== "submit_work" || Object.keys(args).length !== 1 || Object.keys(action).length !== 1
      || Object.keys(move).length !== 2 || Object.keys(tile).length !== 2
      || typeof move.location_id !== "string" || !move.location_id || move.location_id.length > 100
      || ![tile.x, tile.y].every(value => typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= 4096)) {
      throw new Error("此版本只接受 get_status 或 submit_work.action.navigate_to，移动需要地图和整数格坐标。");
    }
    if (turn.abort.signal.aborted || this.uncertain || this.game.state.paused === true
      || this.game.state.interrupting === true || this.game.state.recoveryPending === true
      || (this.game.actorMode === "player" && this.game.state.controlOwner !== "agent")) {
      throw new Error("当前控制权或暂停状态不允许执行动作。");
    }
    const actionId = turn.nativeAction = randomUUID();
    this.changed();
    try {
      let receipt: JsonObject | undefined;
      try { receipt = await this.game.execute(actionId, turn.id, "navigate-to", { locationId: move.location_id, tile }); }
      catch {
        try {
          this.assertBinding(turn.binding);
          receipt = await this.game.queryAction(actionId);
          await this.game.control("status");
        } catch { /* Retain the unresolved action for explicit recovery. */ }
      }
      if (!receipt) {
        this.uncertain = { binding: turn.binding, actionId };
        throw new Error("游戏动作的终态待核对。");
      }
      this.publish("result", { ...turn.binding, ...receipt });
      return receipt;
    } finally { turn.nativeAction = undefined; this.changed(); }
  }
}
