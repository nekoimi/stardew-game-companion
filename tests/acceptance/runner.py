"""Repeatable real-game acceptance. TestDriver arranges; the real model acts.

No MCP action calls, work-store edits, model-output completion heuristics, or
retries of failed cases. All output stays in the isolated run's evidence folder.
"""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import time
import uuid
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def read(path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None


def rows(path):
    try:
        lines = Path(path).read_text(encoding="utf-8-sig").splitlines()
    except OSError:
        return []
    result = []
    for line in lines:
        try:
            result.append(json.loads(line))
        except ValueError:
            continue  # A writer can still be appending the final line.
    return result


def write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    tmp.replace(path)


def count(items, item_id):
    return sum(item.get("stack", 0) for item in items or [] if item.get("id") == item_id)


def summarize_trace(trace):
    calls = []
    messages = 0
    for row in trace:
        if row.get("type") == "tool":
            calls.append({**row["payload"], "nonterminal": False})
            continue
        if row.get("type") == "life.chat.reply":
            messages += bool(row["payload"].get("replyText"))
            continue
    return {"toolCalls": calls, "toolCount": len(calls), "errorCount": sum(c["error"] for c in calls),
            "publicMessages": messages}


def native_finished(sample):
    # ActiveMachine is null when the coordinator has no executing skill.
    return all(key in sample for key in ("companionMachineExecuting", "companionActiveTaskId", "companionRestState")) and (
        sample["companionMachineExecuting"] in (None, False)
        and sample["companionActiveTaskId"] is None and sample["companionRestState"] == "awake")


def evaluate(case, before, after, samples):
    """Fail closed on missing native evidence; never infer success from prose."""
    target_count = before.get("harnessFixtureTargetCount")
    checks = {
        "targets_preserved": bool(target_count) and after.get("harnessFixtureTargets") is not None
        and [(x["x"], x["y"]) for x in before.get("harnessFixtureTargets", [])]
        == [(x["x"], x["y"]) for x in after.get("harnessFixtureTargets", [])],
        "same_game_day": all(after.get(k) == before.get(k) and k in before for k in ("year", "season", "day")),
        "native_action_finished": native_finished(after),
    }
    if case in {"walk-pickup", "walk-pickup-full"}:
        checks.pop("targets_preserved")
        checks["ground_drop_matches_capacity"] = after.get("acceptanceGroundDropRemaining") is (case == "walk-pickup-full")
        checks["wood_in_backpack"] = count(after.get("companionItems"), "(O)388") == (0 if case == "walk-pickup-full" else 3)
    elif case == "mature-trees":
        checks.pop("targets_preserved")
        checks["two_mature_trees_cut"] = before.get("acceptanceMatureTreesRemaining") == 2 and after.get("acceptanceMatureTreesRemaining") == 0
        checks["sapling_preserved"] = before.get("acceptanceSaplingStage") == after.get("acceptanceSaplingStage") == 2
        wood_before = before.get("acceptanceFarmChestWood")
        wood_after = after.get("acceptanceFarmChestWood")
        checks["wood_stored"] = (isinstance(wood_before, int) and isinstance(wood_after, int) and wood_after > wood_before
                                 and after.get("companionItems") is not None and count(after["companionItems"], "(O)388") == 0)
    elif case in {"water-refill", "rest-water"}:
        checks["all_target_crops_watered"] = bool(target_count) and (
            after.get("harnessFixtureDryCropCount") == 0
            and after.get("harnessFixtureCropCount") == target_count
            and after.get("harnessFixtureWateredCount") == target_count
        )
        if case == "water-refill":
            checks["native_water_refilled"] = any(
                (s.get("companionWater") or 0) > (before.get("companionWater") or 0) for s in samples
            )
        else:
            checks["rested_on_real_bed"] = any(s.get("companionOnRealBed") is True for s in samples)
            checks["recovered_then_worked_without_full_recovery"] = (
                60 <= (after.get("companionStamina") or 0) < 100
                and any((s.get("companionStamina") or 0) >= 80 for s in samples)
            )
    else:
        checks["harvested_all_targets"] = bool(target_count) and after.get("harnessFixtureCropCount") == 0
        checks["harvest_in_chest"] = count(after.get("acceptanceChestItems"), "(O)24") == target_count
        checks["wood_in_chest"] = count(after.get("acceptanceChestItems"), "(O)388") == 3
        checks["ground_drop_collected"] = after.get("acceptanceGroundDropRemaining") is False
        checks["backpack_unloaded"] = after.get("companionItems") is not None and all(
            count(after["companionItems"], item_id) == 0 for item_id in ("(O)24", "(O)388")
        )
    return checks


class Runner:
    def __init__(self, run, case, timeout, cases_file=None, *, keep_open=False, reset_fixture=False,
                 simulated=False):
        self.run = run.resolve()
        self.evidence = self.run / "evidence"
        self.mod = self.run / "mods" / "StardewAI.Companion.Mod"
        self.case, self.timeout = case, timeout
        self.cases_file = cases_file or Path(__file__).with_name("cases.json")
        self.token = uuid.uuid4().hex[:8]
        self.keep_open, self.reset_fixture = keep_open, reset_fixture
        self.simulated = simulated
        self.trace_file = self.evidence / "agent-runtime-events.jsonl"
        self.number = 0
        self.samples = []
        self.report = {"case": case, "status": "error", "checks": {}, "realGame": True}

    def terminal(self, instruction, trace_offset):
        events = rows(self.trace_file)[trace_offset:]
        submit = next((row["payload"] for row in events if row.get("type") == "submitted"
                       and row["payload"].get("text") == instruction), None)
        if not submit:
            return None
        return next((row["payload"] for row in events if row.get("type") == "life.chat.reply"
                     and row["payload"].get("requestId") == submit["requestId"]), None)

    async def wait(self, predicate, message, timeout=30):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            startup_error = read(self.evidence / "agent-startup-error.json")
            if startup_error and startup_error.get("error"):
                raise RuntimeError(startup_error["error"])
            value = predicate()
            if value:
                return value
            await asyncio.sleep(.5)
        raise TimeoutError(message)

    async def request(self, action, **params):
        self.number += 1
        label = f"preset-{self.token}-{self.number}"
        path = self.evidence / "life-ui-request.json"
        await self.wait(lambda: not path.exists(), "TestDriver request still pending")
        write(path, {"label": label, "action": action, **params})
        result = await self.wait(lambda: read(self.evidence / f"life-ui-{label}.json"), f"TestDriver {action} did not respond")
        if result.get("error"):
            raise RuntimeError(result["error"])
        return result

    async def native(self):
        self.number += 1
        label = f"preset-{self.token}-{self.number}"
        path = self.evidence / "free-mode-native-snapshot-request.txt"
        await self.wait(lambda: not path.exists(), "Native snapshot request still pending")
        tmp = path.with_suffix(".tmp")
        tmp.write_text(label, encoding="utf-8")
        tmp.replace(path)
        value = await self.wait(lambda: read(self.evidence / f"free-mode-native-{label}.json"), "No native snapshot")
        if value.get("runId") != self.run.name or not value.get("companionObserved"):
            raise RuntimeError("Native observation belongs to another run or companion is absent")
        self.samples.append(value)
        return value

    async def execute(self):
        started = time.time()
        try:
            coord = await self.wait(lambda: read(self.evidence / "harness-coordination.json"), "No isolated game", 180)
            await self.wait(lambda: (read(self.evidence / "harness-coordination.json") or {}).get("state") == "READY_FOR_ACT", "Isolated fixture not ready", 260)
            if coord.get("scenario") != "free-mode-control" or coord.get("runId") != self.run.name:
                raise RuntimeError("Runner requires its own isolated free-mode-control game")
            if self.reset_fixture:
                spec = read(self.cases_file)[self.case]
                await self.request("close")
                await self.request("harness-fixture", mode=spec.get("fixture", self.case), stamina=spec["stamina"], water=spec["water"])
                fixture_file = max(self.evidence.glob("harness-fixture-preset-*.json"), key=lambda p: p.stat().st_mtime)
                fixture = read(fixture_file)
            else:
                fixture = read(self.evidence / "harness-fixture-startup.json") or {}
            if fixture.get("mode") != read(self.cases_file)[self.case].get("fixture", self.case):
                raise RuntimeError("Startup fixture does not match requested case")
            actual_saves = Path(fixture["isolatedSavesPath"]).resolve()
            if not actual_saves.is_relative_to(self.run):
                raise RuntimeError("Save root is outside this isolated run")
            self.report["fixture"] = fixture
            spec_path = self.cases_file
            spec = read(spec_path)[self.case]
            self.report["caseDefinitionSha256"] = hashlib.sha256(spec_path.read_bytes()).hexdigest()
            dll = self.mod / "StardewAI.Companion.Mod.dll"
            self.report["modSha256"] = hashlib.sha256(dll.read_bytes()).hexdigest()
            backend = read(self.evidence / "agent-runtime-ready.json") or {}
            if backend.get("simulated") is not self.simulated:
                raise RuntimeError("API evidence does not confirm the requested acceptance mode")
            if backend.get("protocol") not in {"chat-completions", "responses"} or not isinstance(backend.get("stream"), bool):
                raise RuntimeError("API evidence is missing the protocol or streaming setting")
            self.report["backend"] = backend
            package = read(self.evidence / "agent-package.json") or {}
            if any(not package.get(key) for key in ("gameVersion", "smapiVersion", "modVersion", "packageManifestSha256")):
                raise RuntimeError("Missing package or game version evidence")
            self.report.update(package)
            # Only normal chat/menu input; no test-side action dispatch.
            await self.request("close")
            before = await self.native()
            self.report["before"] = before
            if spec.get("fixture", self.case) in {"harvest-store", "mature-trees", "walk-pickup", "walk-pickup-full"}:
                player = before.get("playerTile") or {}
                targets = fixture.get("nativeReachability") or []
                if before.get("playerLocation") != "Farm" or not targets or any(
                    (player.get("x", -1000) - t["target"]["x"]) ** 2
                    + (player.get("y", -1000) - t["target"]["y"]) ** 2 < 100 for t in targets):
                    raise RuntimeError("Observer did not arrive outside the task area; refuse a blocked or pre-collected fixture")
            instruction = spec["instruction"]
            self.report["instruction"] = instruction
            self.report["followupInstructions"] = [spec[key] for key in ("duringAction", "then") if spec.get(key)]
            deadline = time.monotonic() + self.timeout
            while True:
                ui = await self.request("snapshot")
                if ui.get("productionChatConnected") and ui.get("chatStatus") in {"Idle", "Completed", "Failed"}:
                    break
                if time.monotonic() > deadline:
                    raise TimeoutError("Production bridge did not become ready")
                await asyncio.sleep(1)
            trace_offset = len(rows(self.trace_file))
            self.report["traceOffset"] = trace_offset
            await self.request("open-conversation")
            await self.request("text", field="_input", text=instruction)
            await self.request("key", key="Enter")
            await self.request("close")
            action_started = time.time()
            self.report["setupSeconds"] = round(action_started - started, 1)
            deadline = time.monotonic() + self.timeout
            terminal = None
            guidance_sent = False
            while time.monotonic() < deadline:
                sample = await self.native()
                if spec.get("duringAction") and not guidance_sent and sample.get("companionMachineExecuting"):
                    await self.request("open-conversation")
                    await self.request("text", field="_input", text=spec["duringAction"])
                    await self.request("key", key="Enter")
                    await self.request("close")
                    guidance_sent = True
                    self.report["guidanceSentAt"] = time.time()
                terminal = self.terminal(instruction, trace_offset)
                if terminal:
                    break
                await asyncio.sleep(1)
            if not terminal:
                raise TimeoutError("Model round did not finish within the case budget")
            self.report["turn"] = terminal
            self.report["taskSeconds"] = round(time.time() - action_started, 1)
            await self.request("close")
            await self.request("harness-capture")
            after = await self.native()
            self.report["after"] = after
            checks = evaluate(spec.get("fixture", self.case), before, after, self.samples)
            if spec.get("then") and terminal.get("status") == "completed" and native_finished(after):
                self.report["intermediate"] = after
                deadline = time.monotonic() + self.timeout
                await self.request("open-conversation")
                await self.request("text", field="_input", text=spec["then"])
                await self.request("key", key="Enter")
                await self.request("close")
                followup = None
                while time.monotonic() < deadline:
                    await self.native()
                    followup = self.terminal(spec["then"], trace_offset)
                    if followup:
                        break
                    await asyncio.sleep(1)
                after = await self.native()
                self.report["after"] = after
                checks["shipping_round_completed"] = bool(followup and followup.get("status") == "completed")
                checks["harvest_shipped"] = count(after.get("acceptanceShippedItems"), "(O)24") - count(before.get("acceptanceShippedItems"), "(O)24") == before.get("harnessFixtureTargetCount")
                checks["wood_retained"] = count(after.get("acceptanceChestItems"), "(O)388") == 3
                checks["shipping_native_finished"] = native_finished(after)
            if self.case == "rest-forestry":
                checks["rested_to_target"] = any(s.get("companionOnRealBed") is True for s in self.samples) and any((s.get("companionStamina") or 0) >= 80 for s in self.samples)
                checks["worked_before_full_recovery"] = (after.get("companionStamina") or 0) < 100
            backend = self.report["backend"] or {}
            checks["configured_model_round_completed"] = all(terminal.get(key) == backend.get(key)
                for key in ("provider", "model", "protocol", "stream")) and terminal.get("status") == "completed"
            trace = rows(self.trace_file)[trace_offset:]
            summary = summarize_trace(trace)
            self.report["traceSummary"] = summary
            if self.case == "harvest-logistics":
                self.report["extraClearingCalls"] = sum(
                    "clear_debris" in ((c.get("input") or {}).get("action") or {})
                    for c in summary["toolCalls"])
                # This fixture provides a reachable chest and shipping route.
                checks["no_unrequested_clearing"] = self.report["extraClearingCalls"] == 0
            if spec.get("duringAction"):
                delivered = any(row.get("type") == "player-update" and row["payload"].get("text") == spec["duringAction"]
                                and row["payload"].get("turnId") == terminal.get("turnId") for row in trace)
                checks["player_message_delivered_at_tool_boundary"] = guidance_sent and delivered
            operations = {"water_auto", "water_zone", "water_tiles", "refill_watering_can", "harvest_auto", "harvest_crop", "chop_tree",
                          "pickup_items", "deposit_to_chest", "withdraw_from_chest", "ship_items", "rest", "navigate_to"}
            actions = [c for c in summary["toolCalls"] if c["name"] == "submit_work"
                       and operations.intersection((c.get("input") or {}).get("action") or {})]
            checks["real_action_tools_observed"] = any(not c["error"] and isinstance(c["result"], dict) for c in actions)
            checks["action_calls_returned_terminal_results"] = bool(actions) and all(
                not c["nonterminal"] and (c["error"] or isinstance(c["result"], dict)
                    and (c["result"].get("status") in {"executed", "completed", "failed", "cancelled", "rejected", "player_update"}
                         or c["result"].get("status") == "no-work" and c["result"].get("targetCount") == 0
                         or c["result"].get("terminalState") in {"succeeded", "partially-succeeded", "failed", "cancelled", "rejected", "no-observed-targets"})) for c in actions
            )
            if self.case in {"walk-pickup", "walk-pickup-full"}:
                checks["no_explicit_pickup_tool"] = not any("pickup_items" in str(c.get("input")) for c in summary["toolCalls"])
            frames = after.get("animationFrames") or {}
            checks["walking_animation_changes_in_same_direction"] = any(
                key.startswith("walking-facing:") and len(set(value)) >= 2 for key, value in frames.items()
            )
            checks["no_solid_overlap_while_moving"] = (after.get("movementSamples") or 0) > 0 and after.get("solidOverlaps") == []
            self.report["checks"] = checks
            self.report["status"] = "passed" if all(checks.values()) else "failed"
        except Exception as error:
            self.report["error"] = f"{type(error).__name__}: {error}"
        finally:
            self.report["elapsedSeconds"] = round(time.time() - started, 1)
            self.report["samples"] = self.samples
            trace = rows(self.trace_file)[self.report.get("traceOffset", 0):]
            write(self.evidence / "preset-trace.json", trace)
            self.report["traceEvents"] = len(trace)
            write(self.evidence / "preset-report.json", self.report)
            failed = [name for name, passed in self.report["checks"].items() if not passed]
            (self.evidence / "preset-report.md").write_text(
                f"# {self.case}: {self.report['status']}\n\n耗时 {self.report['elapsedSeconds']} 秒；公开 trace {len(trace)} 条。\n\n"
                + "\n".join(f"- {'通过' if ok else '失败'}：{name}" for name, ok in self.report["checks"].items())
                + f"\n\n{self.report.get('error', '')}\n\n证据：preset-report.json、preset-trace.json。\n",
                encoding="utf-8",
            )
            print(json.dumps({"status": self.report["status"], "failed": failed, "error": self.report.get("error"), "report": str(self.evidence / "preset-report.md")}, ensure_ascii=False), flush=True)
            # Native TestDriver exit, not a broad process kill. Save copies remain.
            if not self.keep_open:
                write(self.evidence / "free-mode-control-done.json", {"success": self.report["status"] == "passed"})
        return 0 if self.report["status"] == "passed" else 1


async def run_suite(run, cases, timeout, cases_file, simulated=False):
    import shutil
    reports = []
    try:
        for index, case in enumerate(cases):
            runner = Runner(run, case, timeout, cases_file, keep_open=True, reset_fixture=index > 0,
                            simulated=simulated)
            code = await runner.execute()
            reports.append(runner.report)
            folder = run / "evidence" / case
            folder.mkdir(exist_ok=True)
            for filename in ("preset-report.json", "preset-report.md", "preset-trace.json"):
                shutil.copy2(run / "evidence" / filename, folder / filename)
            write(run / "summary.json", reports)
            if code and not (native_finished(runner.report.get("after") or {})
                             and (runner.report.get("turn") or {}).get("status") == "completed"):
                break  # Archive a completed failed case, but never reset an unresolved action.
    finally:
        success = len(reports) == len(cases) and all(r["status"] == "passed" for r in reports)
        write(run / "evidence/free-mode-control-done.json", {"success": success})
        mode = "模拟 API" if simulated else "厂商 API"
        lines = [f"- {r['case']}: {r['status']} ({r['elapsedSeconds']} 秒)" for r in reports]
        lines.extend(f"- {case}：未执行" for case in cases[len(reports):])
        (run / "summary.md").write_text(f"# 伙伴实机回归 · {mode}\n\n一次启动、同一存档和会话，场景间只重置测试布置。\n\n"
            + "\n".join(lines) + "\n", encoding="utf-8")
    return 0 if success else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--case", choices=list(read(Path(__file__).with_name("cases.json"))))
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--cases-file", type=Path, help="Frozen suite definitions")
    parser.add_argument("--suite", nargs="+")
    parser.add_argument("--simulated", action="store_true")
    args = parser.parse_args()
    raise SystemExit(asyncio.run(
        run_suite(args.run_dir, args.suite, args.timeout, args.cases_file, args.simulated) if args.suite
        else Runner(args.run_dir, args.case, args.timeout, args.cases_file,
                    simulated=args.simulated).execute()))
