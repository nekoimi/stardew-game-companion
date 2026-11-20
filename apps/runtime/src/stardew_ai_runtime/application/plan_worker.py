from __future__ import annotations

import asyncio
import logging
import time
import uuid
from typing import Any

from stardew_ai_runtime.domain.player_clock import player_mode_clock_gate
from stardew_ai_runtime.job_feedback import compact_job_feedback, compact_task_feedback
from stardew_ai_runtime.plan_executor import (
    DispatchDeferred,
    PlanExecutor,
    StepExecution,
    merge_reconciled_operation_result,
    normalise_native_result,
)
from stardew_ai_runtime.work_state import WorkStore, bind_execution_scope_params

logger = logging.getLogger(__name__)


class PlanWorker:
    """Advances committed short plans without one model turn per step.

    Wakes on new native snapshots, after provider turns, and after new player
    instructions. It claims ready steps through the shared ``PlanExecutor`` (same
    store, same commit discipline as the MCP ``run_next_step`` tool), chains
    successful steps without any LLM call, and only reports a model wake when a
    step deviates (partial/unknown), when a dependency is blocked, or when a
    waiting condition requires a decision.
    """

    def __init__(
        self,
        store: WorkStore,
        client: Any,
        *,
        worker_id: str | None = None,
        idle_seconds: float = 5.0,
        step_limit: int = 200,
    ):
        self.store = store
        self.client = client
        self.worker_id = worker_id or f"bridge-{uuid.uuid4().hex[:8]}"
        self.idle_seconds = idle_seconds
        self.step_limit = step_limit
        self._wake = asyncio.Event()
        self._task: asyncio.Task | None = None
        self._dirty = False
        self._loop: asyncio.AbstractEventLoop | None = None
        self._provider_active = False
        self._executing = False
        self.supplied_save_id: str | None = None
        self.last_result: StepExecution | None = None
        self.progress_callback = None
        self.terminal_callback: Any | None = None
        self.completed_steps = 0
        self.attempted_steps = 0
        self.pending_reasons: list[str] = []
        self.pending_decisions: list[dict[str, Any]] = []
        # Latest native snapshot used to gate explicit waiting conditions. The
        # bridge refreshes it on every world.snapshot; the worker never invents it.
        self.snapshot_provider: Any | None = None
        self.business_gate: Any | None = None

    def _fresh_state(self) -> tuple[dict[str, Any] | None, dict[str, Any] | None]:
        if self.snapshot_provider is None:
            return None, None
        try:
            return self.snapshot_provider()
        except Exception:
            logger.debug("Plan worker snapshot provider failed", exc_info=True)
            return None, None

    # ----------------------------------------------------------- lifecycle
    def start(self) -> None:
        if self._task is None or self._task.done():
            self._loop = asyncio.get_running_loop()
            self._task = asyncio.create_task(self._run())

    def notify(self, *, dirty: bool = True) -> None:
        """Signal new potential work (snapshot, provider turn end, player input)."""
        if dirty:
            self._dirty = True
        loop = self._loop
        if loop is not None and not loop.is_closed():
            try:
                loop.call_soon_threadsafe(self._wake.set)
                return
            except RuntimeError:
                pass
        self._wake.set()

    def set_provider_active(self, active: bool) -> None:
        self._provider_active = active

    async def stop(self) -> None:
        task, self._task = self._task, None
        if task is not None and not task.done():
            task.cancel()
            try:
                await task
            except (asyncio.CancelledError, Exception):
                pass

    # -------------------------------------------------------------- work
    async def _run(self) -> None:
        while True:
            try:
                await asyncio.wait_for(self._wake.wait(), timeout=self.idle_seconds)
            except TimeoutError:
                pass
            self._wake.clear()
            if self._provider_active:
                continue
            try:
                await self.evaluate()
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.warning("Plan worker evaluation failed", exc_info=True)

    async def evaluate(self) -> list[StepExecution]:
        """Own the native socket only for this execution batch, never while idle."""
        self._executing = True
        try:
            return await self._evaluate_ready_steps()
        finally:
            # External Codex/observation turns do not pass through the bridge's
            # _claim_execution. Release here after native reconciliation settles,
            # so a finished worker cannot monopolize the sole command socket.
            try:
                await self._release_idle_session()
            finally:
                self._executing = False

    async def _release_idle_session(self) -> None:
        close = getattr(self.client, "close", None)
        if callable(close) and getattr(self.client, "connected", True):
            try:
                await close()
            except Exception:
                logger.warning("Plan worker could not release its idle MCP session", exc_info=True)

    async def _evaluate_ready_steps(self) -> list[StepExecution]:
        """Run ready steps until none remain (or a deviation needs the model)."""
        self._dirty = False
        executions: list[StepExecution] = []
        for _ in range(self.step_limit):
            if self._provider_active:
                break
            save_id = await self._resolve_save_id()
            if not save_id:
                break
            # A day rollover can close the native socket after it accepts a
            # command but before the worker receives its terminal.  An expired
            # running lease is otherwise invisible to has_ready_step forever.
            if await self._recover_expired_step(save_id):
                break
            # Receipt reconciliation is read-only and must remain possible under
            # a disconnect hold or human pause. The gate still blocks dispatch;
            # otherwise a stale running lease can prevent the recovery decision
            # that would release a program hold from ever becoming eligible.
            if self.business_gate is not None and not self.business_gate(save_id):
                break
            # Read-only readiness check against the shared store; it never touches
            # the Mod, so an idle plan costs no provider and no LLM call. Waiting
            # steps stay parked until their explicit native condition is met.
            try:
                snapshot, game_date = self._fresh_state()
                if not self.store.has_ready_step(save_id, snapshot=snapshot, game_date=game_date):
                    break
            except Exception:
                break
            executor = PlanExecutor(
                self.store,
                dispatch=self._dispatch,
                reconcile=self._reconcile,
                snapshot_provider=self._fresh_state,
            )
            execution = await executor.run_once(save_id, self.worker_id)
            if execution.status == "idle":
                break
            executions.append(execution)
            self.last_result = execution
            if self.progress_callback:
                await self.progress_callback(execution)
            # ``completed_steps`` counts only steps the harness actually confirmed
            # complete. A parked (waiting) or failed/unknown dispatch is NOT
            # progress: counting it was the round6 false-progress bug that let the
            # acceptance script report a failed plan as multi-step success.
            self.attempted_steps += 1
            if execution.status == "executed" and execution.outcome == "completed":
                self.completed_steps += 1
            if execution.recovery_decisions:
                self.pending_decisions.extend(execution.recovery_decisions)
            if execution.task_status == "completed" or execution.needs_model or execution.outcome in {"failed", "partial", "unknown", "rejected", "cancelled"} or (execution.outcome == "waiting" and (execution.result or {}).get("waitCondition", {}).get("type") != "transportRetry"):
                completed_task = next((t for t in self.store.state(save_id).tasks if t.id == execution.task_id), None)
                feedback = compact_task_feedback(completed_task,
                    {**(execution.result or {}), "effects": execution.effects,
                     "reasonCode": execution.reason_code, "commandId": execution.command_id,
                     "message": execution.message},
                    operation=execution.operation or "", status=execution.outcome)
                feedback["effectCount"] = sum(len(step.effects) for step in completed_task.steps) if completed_task else len(execution.effects)
                # Publish finished only after releasing the native owner: an
                # external client may immediately observe upon seeing this flag.
                await self._release_idle_session()
                self.store.finish_job(save_id, feedback, task_id=execution.task_id)
                self.pending_reasons.append("SHORT_JOB_TERMINAL")
                if self.terminal_callback:
                    try:
                        res = self.terminal_callback(save_id, execution, "SHORT_JOB_TERMINAL")
                        if asyncio.iscoroutine(res):
                            await res
                    except Exception:
                        logger.warning("Plan worker terminal callback failed", exc_info=True)
                break
            if execution.needs_model:
                reason = execution.reason_code or execution.outcome or "STEP_DEVIATION"
                self.pending_reasons.append(reason)
                if self.terminal_callback:
                    try:
                        res = self.terminal_callback(save_id, execution, reason)
                        if asyncio.iscoroutine(res):
                            await res
                    except Exception:
                        logger.warning("Plan worker terminal callback failed", exc_info=True)
                break
        if executions:
            # Re-check on the next tick: a completed step may unlock the next one.
            self._dirty = True
        return executions

    async def _recover_expired_step(self, save_id: str) -> bool:
        now = time.monotonic()
        state = self.store.state(save_id)
        for task in state.tasks:
            for step in task.steps:
                if step.status != "running" or step.lease_until > now:
                    continue
                native = None
                if step.command_id:
                    try:
                        native = await asyncio.wait_for(self._reconcile(step.command_id), timeout=10.0)
                    except Exception:
                        logger.warning("Could not reconcile expired native command %s", step.command_id, exc_info=True)
                goal = next((goal for goal in state.goals if goal.id == task.goal_id), None)
                native = merge_reconciled_operation_result(step.operation, step.feedback, native,
                    params=bind_execution_scope_params(step.operation, step.params, goal.constraints if goal else {}))
                outcome, reason, effects, revision = normalise_native_result(native)
                if outcome is None:
                    # The existing store recovery records an explicit unknown
                    # once; it never re-dispatches the unconfirmed command.
                    decisions = self.store.recover(save_id, now=now)
                    if not any(item.get("stepId") == step.id for item in decisions):
                        return True
                    outcome, reason, effects = "unknown", "NATIVE_TERMINAL_UNCONFIRMED", []
                else:
                    self.store.commit_step_result(
                        save_id, task_id=task.id, step_id=step.id,
                        outcome=outcome, effects=effects, reason_code=reason,
                        snapshot_revision=revision, command_id=step.command_id,
                        game_date=self._fresh_state()[1],
                        feedback=compact_job_feedback(native, operation=step.operation, status=outcome, params=step.params),
                    )
                recovered_task = next((item for item in self.store.state(save_id).tasks if item.id == task.id), None)
                execution = StepExecution(
                    status="executed", task_id=task.id, goal_id=task.goal_id,
                    step_id=step.id, operation=step.operation,
                    command_id=step.command_id, outcome=outcome,
                    task_status=recovered_task.status if recovered_task else "unknown",
                    reason_code=reason, effects=effects, result=native,
                )
                if outcome == "completed" and execution.task_status == "pending":
                    # This receipt closes one step; the same decision still owns
                    # the remaining serial work. Do not send a job terminal.
                    self._dirty = True
                    return True
                feedback = compact_task_feedback(recovered_task,
                    {**(native if isinstance(native, dict) else {}), "commandId": step.command_id, "reasonCode": reason,
                     "terminalState": native.get("terminalState", "unknown") if isinstance(native, dict) else "unknown",
                     "effects": effects}, operation=step.operation, status=outcome,
                )
                await self._release_idle_session()
                self.store.finish_job(save_id, feedback, task_id=task.id)
                self.pending_reasons.append(reason or "NATIVE_RECOVERED")
                if self.terminal_callback:
                    try:
                        callback = self.terminal_callback(save_id, execution, reason or "NATIVE_RECOVERED")
                        if asyncio.iscoroutine(callback):
                            await callback
                    except Exception:
                        logger.warning("Plan worker recovery callback failed", exc_info=True)
                return True
        return False

    async def _dispatch(self, operation: str, params: dict[str, Any], command_id: str) -> Any:
        # The harness-only internal tool runs the identical validated plan
        # operation implementation ``run_next_step`` uses and forwards the stable
        # persisted command id to the real scheduler/native command. It must not go
        # through ``call_capability``: that model-facing schema has no
        # ``command_id`` and rejected the step (round6 STEP_DISPATCH_FAILED).
        if self.progress_callback:
            state = self.store.state(self.supplied_save_id or await self._resolve_save_id())
            task = next((t for t in state.tasks if any(s.command_id == command_id for s in t.steps)), None)
            await self.progress_callback(StepExecution(status="dispatching", task_id=task.id if task else None,
                operation=operation, command_id=command_id, outcome="running"))
        save_id = self.supplied_save_id or await self._resolve_save_id()
        state = self.store.state(save_id)
        task = next((t for t in state.tasks if any(s.command_id == command_id for s in t.steps)), None)
        snapshot, _ = self.snapshot_provider() if self.snapshot_provider else (None, None)
        companion = (snapshot or {}).get("companion") or {}
        world = (snapshot or {}).get("world") or {}
        bedtime = companion.get("bedtime", 2400)
        clock = world.get("timeOfDay", 600)
        if (snapshot or {}).get("actorMode") == "player":
            # Player mode: no companion profile bedtime. Late gates only steer
            # new dispatches toward native sleep so the player is not idled
            # into collapse (≥00:30 non-sleep defers, ≥01:30 sleep only).
            gate = player_mode_clock_gate(operation, clock, params)
            if gate:
                # No native send has occurred. Park this exact unfinished step
                # until the next real day and return the model slot for sleep.
                # DispatchDeferred is for human/revoked authority only: retrying
                # a clock-gated selection each snapshot deadlocks recovery.
                year, season, day = world.get("year"), world.get("season"), world.get("dayOfMonth")
                seasons = ("spring", "summer", "fall", "winter")
                if type(year) is not int or type(day) is not int or season not in seasons or not 1 <= day <= 28:
                    raise RuntimeError("Late action deferred; native calendar unavailable. Re-observe before selecting recovery.")
                if day < 28:
                    next_date = {"year": year, "season": season, "day": day + 1}
                else:
                    index = seasons.index(season)
                    next_date = {"year": year + (index == 3), "season": seasons[(index + 1) % 4], "day": 1}
                return {"status": "waiting", "commandId": command_id,
                        "reasonCode": "PLAYER_LATE_ACTION_DEFERRED", "message": gate,
                        "nativeDispatched": False,
                        "waitCondition": {"type": "gameDay", "params": next_date,
                                          "reasonCode": "PLAYER_LATE_ACTION_DEFERRED"}, "effects": []}
        else:
            resting = companion.get("restState") in {"winding-down", "returning-home", "waiting-for-bed", "sleeping"}
            if resting or (isinstance(clock, int) and isinstance(bedtime, int)
                    and clock // 100 * 60 + clock % 100 >= bedtime // 100 * 60 + bedtime % 100 - 60):
                raise DispatchDeferred("bedtime: keep this step for tomorrow")
        if (state.paused or task is None or state.decision.get("taskId") != task.id
                or state.decision.get("finished")):
            logger.warning("Native dispatch authority deferred: save=%s command=%s task=%s decisionTask=%s paused=%s finished=%s",
                           save_id, command_id, task.id if task else None,
                           state.decision.get("taskId"), state.paused, state.decision.get("finished"))
            raise DispatchDeferred("paused or decision revoked before native send")
        try:
            return await self.client.call_tool(
                "dispatch_plan_operation",
                {"operation": operation, "params": dict(params), "command_id": command_id},
            )
        except Exception as ex:
            if "UNAUTHORIZED_JOB_COMMAND" in str(ex):
                # A rejected persisted claim is a real fault, not an idle wait.
                # Commit it so the model can recover instead of silently
                # reattempting the same unauthorized command forever.
                raise RuntimeError(str(ex)) from ex
            raise

    async def _reconcile(self, command_id: str) -> Any:
        reconcile = getattr(self.client, "reconcile", None)
        if callable(reconcile):
            result = await reconcile(command_id)
            if isinstance(result, dict) and "found" in result:
                return result.get("native") if result.get("found") else None
            return result
        return None

    async def _resolve_save_id(self) -> str | None:
        if self.supplied_save_id:
            return self.supplied_save_id
        resolver = getattr(self.client, "current_save_id", None)
        if callable(resolver):
            try:
                return await resolver()
            except Exception:
                logger.debug("Plan worker could not resolve a save id", exc_info=True)
        return None
