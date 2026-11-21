import { randomUUID } from "node:crypto";
import type { GameConnection } from "../adapters/game/game-connection.js";
import { isTerminalState } from "../adapters/game/action-receipt.js";
import type { GameBinding } from "../adapters/game/action-receipt.js";
import type { CompanionStore } from "../adapters/storage/companion-store.js";
import { CompanionTools, gameDate, gameTools, modelObservation } from "../tools/companion-tools.js";
import { modelResponseTimeout, object } from "./model.js";
import type { JsonObject, JsonValue, ModelContext, ModelEvent, ModelMessage, ModelSelection } from "./model.js";

type Complete = Extract<ModelEvent, { type: "done" }>;
interface Turn {
  id: string;
  requestId: string;
  originalRequestId: string;
  goalId: string;
  binding: GameBinding;
  abort: AbortController;
  nativeAction?: string;
  additional: { requestId: string; text: string }[];
  requests: string[];
  settled: Promise<void>;
}

export class TurnRunner {
  private active?: Turn;
  private uncertain?: { binding: GameBinding; actionId: string };
  private previousBinding?: GameBinding;
  private owner?: JsonValue;
  private readonly messages: ModelMessage[] = [];
  private transition = false;
  private selection?: ModelSelection;
  private readonly tools: CompanionTools;
  private wakeTimer?: NodeJS.Timeout;
  private stopping = false;
  private readonly wakeSignatures = new Map<string, string>();
  private observedOwner = false;

  constructor(private readonly game: GameConnection, private model: ModelContext | undefined,
              private readonly publish: (type: string, payload: JsonObject) => void,
              private readonly rules: string, private readonly store?: CompanionStore, directory = "",
              private readonly trace?: (type: string, payload: JsonObject) => void) {
    this.tools = new CompanionTools(game, directory, store);
    game.on("changed", () => {
      const binding = game.binding;
      if (!binding) this.observedOwner = false;
      if (!binding || (this.previousBinding && this.previousBinding.gameSessionId !== binding.gameSessionId)) {
        this.active?.abort.abort();
      }
      if (binding && this.previousBinding?.saveId !== binding.saveId) this.messages.length = 0;
      if (binding && this.uncertain && (this.uncertain.binding.gameSessionId !== binding.gameSessionId
        || this.uncertain.binding.saveId !== binding.saveId)) this.uncertain = undefined;
      if (binding && !this.active && !this.uncertain) {
        const pending = this.store?.pendingAction(binding);
        if (pending) this.uncertain = { binding, actionId: pending };
      }
      if (binding && game.actorMode === "player" && game.state.controlOwner === "human" && this.owner === "agent") {
        this.active?.abort.abort();
      }
      if (binding && (!this.observedOwner || this.previousBinding?.gameSessionId !== binding.gameSessionId)) {
        this.previousBinding = binding;
        this.syncProfile(binding);
      }
      this.owner = game.state.controlOwner;
      if (this.owner === "human" || this.owner === "agent" || game.actorMode === "companion") this.observedOwner = true;
      this.changed();
    });
  }

  configure(model: ModelContext | undefined, selection?: ModelSelection): void {
    if (this.active || this.transition) throw new Error("请等当前回合结束再切换模型。");
    this.model = model;
    this.selection = selection;
    this.changed();
  }

  status(): JsonObject {
    const binding = this.game.binding;
    const save = binding?.saveId;
    const decisions = save ? this.store?.items(save, "decisions") ?? [] : [];
    return { ...binding, ready: !!binding, actorMode: this.game.actorMode,
      controlOwner: this.game.state.controlOwner ?? (this.game.actorMode === "player" ? "human" : "agent"),
      paused: this.game.state.paused === true, interrupting: this.transition || this.game.state.interrupting === true,
      recoveryPending: !!this.uncertain || this.game.state.recoveryPending === true,
      activeCommandId: this.active?.nativeAction ?? "", activity: this.game.state.activity ?? "idle",
      progress: this.game.state.progress ?? "", clockPauseOwner: this.game.state.clockPauseOwner ?? null,
      thinking: !!this.active && !this.active.nativeAction && !this.active.abort.signal.aborted,
      modelStreaming: this.selection?.stream !== false,
      modelBusy: !!this.active || this.transition,
      connection: { ready: !!this.model && !!binding && !this.uncertain && this.game.state.recoveryPending !== true,
        status: this.model ? "connected" : "waiting", clientName: this.selection?.model ?? "模型 API" },
      work: save ? this.work(save) : {}, decisions: decisions.filter(row => row.status === "pending"),
      closedDecisions: decisions.filter(row => row.status === "dismissed"),
      milestones: save ? this.store?.items(save, "milestones") ?? [] : [],
      inboxRevision: save ? this.store?.reminderRevision(save) ?? 0 : 0, unreadMessages: save ? this.store?.unreadCount(save) ?? 0 : 0 };
  }

  private work(save: string): JsonObject {
    const goals = this.store?.items(save, "goals").filter(item => item.status === "active") ?? [];
    const current = goals.find(item => item.id === this.active?.goalId) ?? goals.at(-1);
    const details = this.store?.recentReceipts(save).map(receipt => ({
      gameDate: receipt.gameDate ?? null, taskTitle: receipt.skillId ?? "游戏动作",
      summary: `${receipt.terminalState ?? "待核对"} · 已完成 ${receipt.completedCount ?? "未记录"}`,
    })) ?? [];
    return { mode: this.store?.profile(save).initiativeMode ?? "command", paused: this.game.state.paused === true,
      goal: current?.text ?? null, currentGoalId: current?.id ?? null, activeGoals: goals, recentExecutions: details,
      recentTodos: this.store?.items(save, "todos").slice(-8) ?? [],
      playerDecisions: this.store?.items(save, "decisions").filter(item => item.status === "pending") ?? [],
      activity: { phase: this.active ? "executing" : current ? "waiting" : "idle",
        summary: this.game.state.progress ?? current?.summary ?? "", nextStep: current?.summary ?? "" } };
  }

  changed(): void {
    this.publish("status", this.status());
    if (!this.stopping && this.store && !this.wakeTimer) {
      this.wakeTimer = setTimeout(() => { this.wakeTimer = undefined; this.wake(); }, 750);
    }
  }

  syncProfile(binding: GameBinding, requestId = "", status = "confirmed"): void {
    if (!this.store || !this.game.binding) return;
    try {
      this.game.notify("life.profile.state", { saveId: binding.saveId, requestId, status,
        ...this.store.profile(binding.saveId), work: this.work(binding.saveId) });
    } catch { /* The game will request its profile again after binding. */ }
  }

  async submit(binding: GameBinding, requestId: string, text: string, mode: "send" | "interrupt",
      recordInGame = true, decisionId?: string, preset = false): Promise<void> {
    this.assertBinding(binding);
    if (!this.model) throw new Error("请先配置模型服务。");
    if (this.transition || this.uncertain || this.game.state.recoveryPending === true) {
      throw new Error("请先核对上一项动作。");
    }
    if (preset && this.active) {
      if (this.active.originalRequestId === requestId) return;
      throw new Error("已有其他回合正在执行。");
    }
    if (this.active && mode === "send" && this.active.additional.length >= 8) {
      throw new Error("待处理的补充要求已满，请稍后再试。");
    }
    this.transition = true;
    try {
      const decision = decisionId ? this.store?.items(binding.saveId, "decisions").find(item => item.id === decisionId) : undefined;
      if (decisionId && decision?.status !== "pending") throw new Error("这项选择已结束。");
      if (mode === "interrupt") {
        if (this.game.state.paused === true || (this.game.actorMode === "player" && this.game.state.controlOwner !== "agent")) {
          throw new Error("当前已暂停或由玩家控制，请先恢复游戏工作。");
        }
        await this.interrupt(binding);
      }
      if (recordInGame) await this.game.control("record", { conversationRequestId: requestId, text, ...(decisionId ? { decisionId } : {}) });
      this.assertBinding(binding);
      if (decisionId) {
        this.store?.changeDecision(binding.saveId, decisionId, "answer", text);
        this.syncProfile(binding);
        try { this.game.notify("life.chat.reply", { ...binding, requestId, status: "processing",
          answeredNoticeId: decisionId, profileRevision: this.store?.profile(binding.saveId).profileRevision ?? 0,
          memoryRevision: this.store?.memory(binding.saveId).memoryRevision ?? 0 }); }
        catch { /* The answer was saved in SQLite. */ }
      }
      this.store?.append(binding.saveId, `${requestId}:player`, "player", { speaker: "你", text,
        gameDate: gameDate(this.game.snapshot), ...(decisionId ? { replyToDecisionId: decisionId } : {}),
        ...(preset ? { preset: true } : {}) });
      this.publish("submitted", { ...binding, requestId, text, status: "accepted" });
      const prompt = decision ? `玩家对「${decision.message}」的选择：${text}` : text;
      if (this.active && mode === "send") {
        this.active.additional.push({ requestId, text: prompt });
        this.active.requests.push(requestId);
        return;
      }
      const goals = this.store?.items(binding.saveId, "goals") ?? [];
      const priorPreset = preset ? goals.find(goal => goal.requestId === requestId) : undefined;
      if (priorPreset && this.notifyPreset(binding, requestId, "completed", "", priorPreset.id)) return;
      const current = preset ? priorPreset : goals.findLast(goal => goal.status === "active");
      const goalId = typeof decision?.goalId === "string" ? decision.goalId : current?.id ?? randomUUID();
      const decisionRequest = decision?.requestId;
      const originalRequest = typeof decisionRequest === "string" ? decisionRequest :
        typeof current?.requestId === "string" && mode !== "interrupt" ? current.requestId : requestId;
      if (preset) this.notifyPreset(binding, requestId, "processing", "", goalId);
      this.startTurn(binding, requestId, goalId, prompt, false, originalRequest);
    } finally {
      this.transition = false;
      this.changed();
    }
  }

  private notifyPreset(binding: GameBinding, requestId: string, status: string, text: string, goalId: string): boolean {
    if (this.store?.playerRequest(binding.saveId, requestId).preset !== true) return false;
    const goal = this.store.items(binding.saveId, "goals").find(item => item.id === goalId);
    const terminal = goal?.status === "completed" || goal?.status === "cancelled" ? goal.status : undefined;
    if (status !== "completed" || terminal || this.active?.originalRequestId === requestId) {
      try { this.game.notify("chat.reply", { ...binding, requestId, status, replyText: text,
        commandComplete: status !== "processing", ...(goal ? { presetGoalId: goal.id } : {}),
        ...(terminal ? { presetGoalTerminal: terminal } : {}) }); }
      catch { /* A preset can reconcile the persisted goal on reconnect. */ }
    }
    return !!terminal;
  }

  private startTurn(binding: GameBinding, requestId: string, goalId: string, text: string, autonomous = false,
                    originalRequestId = requestId): void {
    if (!this.model) return;
    const turn: Turn = { id: randomUUID(), requestId, originalRequestId, goalId, binding, abort: new AbortController(), additional: [],
      requests: [requestId], settled: Promise.resolve() };
    this.active = turn;
    turn.settled = this.run(turn, this.model, text, autonomous);
    this.changed();
  }

  private wake(): void {
    const binding = this.game.binding;
    if (!binding || !this.store || this.stopping) return;
    const time = object(this.game.snapshot?.world).timeOfDay;
    if (typeof time === "number" && time >= 1900) this.remind(binding, "evening", "", "天色晚了，记得留出回家的时间。");
    if (typeof time === "number" && time < 900 && time >= 600) {
      const dry = object(this.game.snapshot?.farmWork).cropUnwateredTiles;
      if (Array.isArray(dry) && dry.length) this.remind(binding, "morning", "", "田里还有待浇的作物，今天可以先照料它们。");
    }
    if (!this.model || this.active || this.transition || this.uncertain
      || !this.observedOwner || this.game.state.paused === true || this.game.state.interrupting === true
      || this.game.state.recoveryPending === true || (this.game.actorMode === "player" && this.game.state.controlOwner !== "agent")) return;
    const save = binding.saveId;
    if (this.store.items(save, "decisions").some(item => item.status === "pending")) return;
    const goal = this.store.items(save, "goals").findLast(item => item.status === "active");
    const profile = this.store.profile(save);
    if (!goal && (profile.initiativeMode !== "free" || profile.idlePreference === "wait")) return;
    if (goal && goal.continuation !== "continue" && goal.continuation !== "wait") return;
    const date = gameDate(this.game.snapshot);
    if (goal?.continuation === "wait") {
      const due = goal.waitNextDay === true ? date && date !== goal.updatedDay
        : typeof time === "number" && typeof goal.wakeTime === "number" && time >= goal.wakeTime;
      if (!due) return;
    }
    const lastPerformed = this.store.recentReceipts(save).findLast(row => row.terminalState === "succeeded"
      || row.terminalState === "partially-succeeded");
    const revision = goal ? JSON.stringify([goal.id, goal.text, goal.continuation, goal.wakeTime, goal.updatedDay,
      lastPerformed?.commandId]) : date;
    const signature = `${binding.gameSessionId}:${revision}`;
    if (this.wakeSignatures.get(save) === signature) return;
    this.wakeSignatures.set(save, signature);
    const originalRequest = typeof goal?.requestId === "string" ? goal.requestId : "";
    const requestId = `autonomy-${randomUUID()}`;
    this.startTurn(binding, requestId, goal?.id ?? randomUUID(),
      goal ? `继续当前目标：${goal.text}\n按实际观察与回执处理剩余工作。`
        : "按当前存档的主动帮忙偏好，观察当天需要照料的农活，然后处理适合的事项。", true, originalRequest || requestId);
  }

  private remind(binding: GameBinding, kind: string, ref: string, text: string): void {
    const time = object(this.game.snapshot?.world).timeOfDay;
    const date = gameDate(this.game.snapshot);
    if (!date || typeof time !== "number") return;
    const entry = this.store?.saveReminder(binding.saveId, date, time, kind, ref, text);
    if (!entry) return;
    try { this.game.notify("life.care", { ...entry, saveId: binding.saveId }); }
    catch { /* The desktop can still read the persisted reminder. */ }
    this.publish("status", this.status());
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

  async control(binding: GameBinding, action: string, goalId?: string): Promise<JsonObject> {
    this.assertBinding(binding);
    if (this.transition && action !== "takeover") throw new Error("请等待旧动作停止。");
    if (action === "takeover") this.active?.abort.abort();
    if (action === "cancel") {
      this.transition = true;
      try {
        const selected = goalId ?? this.active?.goalId ?? this.store?.items(binding.saveId, "goals").findLast(goal => goal.status === "active")?.id;
        if (!this.active || !goalId || this.active.goalId === goalId) {
          await this.interrupt(binding);
        }
        if (selected && this.store?.items(binding.saveId, "goals").some(goal => goal.id === selected)) {
          const goal = this.store.updateItem(binding.saveId, "goals", selected, { status: "cancelled" }, true);
          if (typeof goal.requestId === "string") this.notifyPreset(binding, goal.requestId, "cancelled", "", goal.id);
          this.syncProfile(binding);
        }
      }
      finally { this.transition = false; this.changed(); }
      return this.status();
    }
    if (action === "recover") {
      this.transition = true;
      try {
        let confirmedActionId: string | undefined;
        if (this.uncertain) {
          const { actionId } = this.uncertain;
          const result = await this.game.queryAction(actionId);
          this.assertBinding(binding);
          if (!result || result.commandId !== actionId || !isTerminalState(result.terminalState)) {
            throw new Error("游戏尚未确认旧动作的终态。");
          }
          this.settleReceipt(binding, actionId, result);
          confirmedActionId = actionId;
          this.uncertain = undefined;
        }
        const result = await this.game.control("recover");
        this.assertBinding(binding);
        const receipt = object(result.result);
        if (typeof receipt.commandId === "string" && isTerminalState(receipt.terminalState)) {
          if (receipt.commandId !== confirmedActionId) this.settleReceipt(binding, receipt.commandId, receipt);
          confirmedActionId = receipt.commandId;
        }
        if (this.game.state.interrupting === true) {
          await this.game.control("unhold");
          this.assertBinding(binding);
        }
        this.publish("recovery", { ...binding, hasReceipt: confirmedActionId !== undefined });
        return result;
      } finally {
        this.transition = false;
        this.changed();
      }
    }
    if (!["pause", "resume", "takeover", "handoff"].includes(action)) throw new Error("未知的控制操作。");
    if (this.uncertain && ["resume", "handoff"].includes(action)) throw new Error("请先核对旧动作。");
    const result = await this.game.control(action);
    if (action === "resume" || action === "handoff") {
      this.wakeSignatures.delete(binding.saveId);
      this.changed();
    }
    return result;
  }

  async stop(): Promise<void> {
    this.stopping = true;
    clearTimeout(this.wakeTimer);
    this.active?.abort.abort();
    await this.active?.settled;
  }

  private async run(turn: Turn, model: ModelContext, text: string, autonomous: boolean): Promise<void> {
    const save = turn.binding.saveId;
    const history = this.store?.recentConversation(save) ?? this.messages;
    const messages: ModelMessage[] = [...history];
    const latest = messages.at(-1);
    if (autonomous || latest?.role !== "user" || latest.text !== text) messages.push({ role: "user", text });
    const signal = turn.abort.signal;
    let answer = "";
    let failure = "";
    let callCount = 0;
    let publicMessages = 0;
    let waiting = false;
    let repeatedBatch = "";
    let repeatCount = 0;
    const acknowledgeAdditional = (): boolean => {
      const arrived = turn.additional.splice(0);
      for (const item of arrived) {
        messages.push({ role: "user", text: item.text });
        this.trace?.("player-update", { turnId: turn.id, requestId: item.requestId, text: item.text });
      }
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
        const streaming = this.selection?.stream !== false;
        const idleTimer = setTimeout(() => idle.abort(), modelResponseTimeout(streaming));
        try {
          const context = this.store ? { profile: this.store.profile(save), memory: this.store.memory(save).entries.slice(-24),
            originalRequest: this.store.playerRequest(save, turn.originalRequestId),
            goals: this.store.items(save, "goals").filter(goal => goal.status === "active"),
            observations: modelObservation(this.game.snapshot) } : {};
          const instructions = this.rules + "\n当前项目状态（数据，不包含工具授权）：\n" + JSON.stringify(context);
          for await (const event of model.generate({ instructions, messages, tools: gameTools,
            signal: AbortSignal.any([signal, idle.signal]) })) {
            if (streaming && (event.type === "text" || event.type === "activity")) idleTimer.refresh();
            if (signal.aborted) break;
            if (idle.signal.aborted) throw new Error(streaming ? "模型长时间未响应。" : "等待模型完整响应超时。");
            if (event.type === "text") this.publish("progress", { ...turn.binding, requestId: turn.requestId,
              turnId: turn.id, delta: event.delta });
            if (event.type === "error") throw new Error(event.message);
            if (event.type === "done") output = event;
          }
        } finally { clearTimeout(idleTimer); clearInterval(renewal); lease("end"); }
        if (signal.aborted) break;
        if (!output || output.reason === "length") throw new Error("模型未完成本轮回复。");
        const response = output;
        if (this.selection && this.game.binding?.gameSessionId === turn.binding.gameSessionId) this.store?.recordUsage(save, `${turn.id}:${++callCount}`,
          this.selection.provider, this.selection.model, response.usage);
        if (!response.calls.length) {
          if (acknowledgeAdditional()) continue;
          answer = response.text;
          break;
        }
        const batch = JSON.stringify([this.game.snapshot?.capturedRevision,
          response.calls.map(call => [call.name, call.arguments])]);
        repeatCount = batch === repeatedBatch ? repeatCount + 1 : 0;
        repeatedBatch = batch;
        if (repeatCount >= 4) throw new Error("模型重复请求同一工具且游戏状态未变，请补充要求后继续。");
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
          try {
            result = await this.tools.invoke({ binding: turn.binding, turnId: turn.id, requestId: turn.originalRequestId,
              goalId: turn.goalId, signal, native: (skill, params, observation) => this.executeNative(turn, skill, params, observation),
              speak: spoken => this.deliver(turn, spoken, `${turn.requestId}:public:${++publicMessages}`),
              changed: () => { this.syncProfile(turn.binding); this.changed(); },
            }, call.name, call.arguments);
            if (call.name === "request_player_decision") waiting = true;
          }
          catch (error) { failed = true; result = { error: error instanceof Error ? error.message : "工具执行失败。" }; }
          this.trace?.("tool", { turnId: turn.id, requestId: turn.requestId, name: call.name, input: call.arguments, result, error: failed });
          if (this.uncertain) throw new Error("旧动作终态待核对，已停止继续派工。");
          messages.push({ role: "tool", callId: call.id, name: call.name, result, failed });
          if (waiting) break;
        }
        if (waiting) break;
      }
    } catch (error) {
      failure = error instanceof Error ? error.message : "Agent 回合未完成。";
    } finally {
      if (signal.aborted) failure = "本轮已停止。";
      const current = this.game.binding;
      if (current?.gameSessionId === turn.binding.gameSessionId) {
        if (!this.store && !failure && answer) {
          this.messages.push(...messages.slice(this.messages.length).filter(message => message.role === "user"),
            { role: "assistant", text: answer, calls: [] });
          if (this.messages.length > 32) this.messages.splice(0, this.messages.length - 32);
        }
        if (!failure && answer) this.store?.append(save, `${turn.requests.at(-1)}:assistant`, "assistant",
          { speaker: String(object(this.store.profile(save).profile).companionName ?? "伙伴"), text: answer, gameDate: gameDate(this.game.snapshot) });
        this.notifyPreset(turn.binding, turn.originalRequestId, signal.aborted ? "cancelled" : failure ? "failed" : "completed",
          failure || answer, turn.goalId);
        for (const requestId of turn.requests) {
          const primary = requestId === turn.requests.at(-1);
          const reply = { ...turn.binding, requestId, turnId: turn.id,
            status: failure ? "failed" : "completed", replyText: primary ? answer || null : null,
            error: primary ? failure || null : null,
            ...(this.trace && this.selection ? { provider: this.selection.provider, model: this.selection.model } : {}),
            profileRevision: this.store?.profile(save).profileRevision ?? 0, memoryRevision: this.store?.memory(save).memoryRevision ?? 0 };
          try {
            this.game.notify("life.chat.reply", { ...reply, replyAlreadyDelivered: !primary || (waiting && !answer) });
            if (autonomous && primary && answer) this.game.notify("chat.reply", { ...reply, status: "public-message" });
          } catch { /* The desktop receives the same result. */ }
          this.publish("life.chat.reply", reply);
        }
      }
      if (this.active === turn) this.active = undefined;
      this.changed();
    }
  }

  private deliver(turn: Turn, text: string, id: string): void {
    this.assertBinding(turn.binding);
    const save = turn.binding.saveId;
    if (this.store && !this.store.append(save, id, "assistant", {
      speaker: String(object(this.store.profile(save).profile).companionName ?? "伙伴"), text, gameDate: gameDate(this.game.snapshot),
    })) return;
    const reply = { ...turn.binding, requestId: id, status: "completed", replyText: text,
      profileRevision: this.store?.profile(save).profileRevision ?? 0, memoryRevision: this.store?.memory(save).memoryRevision ?? 0 };
    this.game.notify("chat.reply", { ...reply, status: "public-message" });
    this.publish("life.chat.reply", reply);
  }

  private settleReceipt(binding: GameBinding, actionId: string, receipt: JsonObject): void {
    if (!this.store || this.store.recordReceipt(binding, actionId, { ...receipt, gameDate: gameDate(this.game.snapshot) })) {
      this.publish("result", { ...binding, ...receipt });
      if (receipt.terminalState === "succeeded" && Number(receipt.completedCount) > 0 && receipt.skillId !== "navigate-to") {
        this.remind(binding, "work-done", actionId, `刚核对了游戏回执，这项工作已完成 ${receipt.completedCount} 项。`);
      }
    }
  }

  private async executeNative(turn: Turn, skill: string, parameters: JsonObject, observation = false): Promise<JsonObject> {
    this.assertBinding(turn.binding);
    if (turn.abort.signal.aborted || this.uncertain || (!observation && (this.game.state.paused === true
      || this.game.state.interrupting === true || this.game.state.recoveryPending === true
      || (this.game.actorMode === "player" && this.game.state.controlOwner !== "agent")))) {
      throw new Error("当前控制权或暂停状态不允许执行动作。");
    }
    const actionId = turn.nativeAction = randomUUID();
    this.changed();
    try {
      if (!observation) this.store?.beginAction(turn.binding, actionId);
      let receipt: JsonObject | undefined;
      try { receipt = await this.game.execute(actionId, turn.id, skill, parameters); }
      catch {
        try {
          this.assertBinding(turn.binding);
          receipt = await this.game.queryAction(actionId);
          this.assertBinding(turn.binding);
          await this.game.control("status");
        } catch { /* Retain the unresolved action for explicit recovery. */ }
      }
      if (!receipt || receipt.commandId !== actionId || !isTerminalState(receipt.terminalState)) {
        const current = this.game.binding;
        if (!observation && (!current || current.gameSessionId === turn.binding.gameSessionId && current.saveId === turn.binding.saveId)) {
          this.uncertain = { binding: turn.binding, actionId };
        }
        throw new Error("游戏动作的终态待核对。");
      }
      this.assertBinding(turn.binding);
      if (!observation) this.settleReceipt(turn.binding, actionId, receipt);
      return receipt;
    } finally { turn.nativeAction = undefined; this.changed(); }
  }
}
