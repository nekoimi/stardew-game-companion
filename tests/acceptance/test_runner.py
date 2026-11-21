"""Acceptance must reject false positives even if a model says it completed."""
import importlib.util
import asyncio
from pathlib import Path

import pytest

spec = importlib.util.spec_from_file_location("preset_runner", Path(__file__).with_name("runner.py"))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def test_world_evidence_rejects_missing_remaining_and_day_rollover():
    before = {"year": 1, "season": "spring", "day": 1, "harnessFixtureTargetCount": 2,
              "harnessFixtureTargets": [{"x": 1, "y": 2}, {"x": 2, "y": 2}], "companionWater": 1}
    after = dict(before, harnessFixtureDryCropCount=0, harnessFixtureCropCount=2,
                 harnessFixtureWateredCount=2, companionRestState="awake", companionWater=20,
                 companionMachineExecuting=None, companionActiveTaskId=None)
    assert all(runner.evaluate("water-refill", before, after, [after]).values())
    for invalid in ({}, dict(after, harnessFixtureDryCropCount=1), dict(after, day=2),
                    dict(after, companionMachineExecuting=True), dict(after, companionActiveTaskId="running")):
        assert not all(runner.evaluate("water-refill", before, invalid, [invalid]).values())


def test_harvest_disappearance_does_not_prove_pickup_or_storage():
    before = {"year": 1, "season": "spring", "day": 1, "harnessFixtureTargetCount": 1,
              "harnessFixtureTargets": [{"x": 1, "y": 2}]}
    after = dict(before, harnessFixtureCropCount=0, companionRestState="awake",
                 companionMachineExecuting=None, companionActiveTaskId=None,
                 acceptanceGroundDropRemaining=False, companionItems=[],
                 acceptanceChestItems=[{"id": "(O)24", "stack": 1}, {"id": "(O)388", "stack": 3}])
    assert all(runner.evaluate("harvest-store", before, after, []).values())
    assert not all(runner.evaluate("harvest-store", before, dict(after, acceptanceChestItems=[]), []).values())
    assert not all(runner.evaluate("harvest-store", before, dict(after, companionItems=[{"id": "(O)24", "stack": 1}]), []).values())


def test_forestry_counts_new_wood_in_any_farm_chest():
    before = {"year": 1, "season": "spring", "day": 1, "acceptanceMatureTreesRemaining": 2,
              "acceptanceSaplingStage": 2, "acceptanceFarmChestWood": 34}
    after = dict(before, acceptanceMatureTreesRemaining=0, acceptanceFarmChestWood=68, acceptanceChestItems=[],
                 companionRestState="awake", companionActiveTaskId=None, companionMachineExecuting=None, companionItems=[])
    assert all(runner.evaluate("mature-trees", before, after, []).values())
    assert not runner.evaluate("mature-trees", before, dict(after, acceptanceFarmChestWood=34), [])["wood_stored"]
    assert not runner.evaluate("mature-trees", before, dict(after, acceptanceFarmChestWood=None), [])["wood_stored"]


def test_trace_retains_native_tool_failures():
    trace = [{"type": "tool", "payload": {"name": "submit_work", "input": {"action": {"water_auto": {}}},
                                         "result": {"error": "旧动作的终态待核对。"}, "error": True}}]
    summary = runner.summarize_trace(trace)
    assert summary["errorCount"] == 1
    assert summary["toolCalls"][0]["result"]["error"] == "旧动作的终态待核对。"


def test_api_startup_failure_surfaces_before_game_ready(tmp_path):
    case = runner.Runner(tmp_path, "care-guidance", 60)
    message = "Model test failed for deepseek/deepseek-flash (authentication)."
    runner.write(case.evidence / "agent-startup-error.json", {"error": message})
    with pytest.raises(RuntimeError, match="authentication"):
        asyncio.run(case.wait(lambda: None, "No isolated game", timeout=0.1))


def test_failed_simulated_launch_remains_labeled_as_simulation(tmp_path):
    runner.write(tmp_path / "evidence" / "agent-startup-error.json", {"error": "Model failed before startup"})
    assert asyncio.run(runner.run_suite(tmp_path, ["care-guidance"], 60, None, simulated=True)) == 1
    summary = (tmp_path / "summary.md").read_text(encoding="utf-8")
    assert summary.startswith("# 伙伴实机回归 · 模拟 API\n")
    assert "care-guidance: error" in summary


def test_suite_keeps_game_open_and_preserves_failed_scene(tmp_path, monkeypatch):
    calls = []
    evidence = tmp_path / "evidence"
    evidence.mkdir()

    class FakeRunner:
        def __init__(self, run, case, timeout, cases_file, **options):
            calls.append((case, options))
            self.report = {"case": case, "status": "passed" if case == "care" else "failed", "elapsedSeconds": 1}

        async def execute(self):
            for filename in ("preset-report.json", "preset-report.md", "preset-trace.json"):
                (evidence / filename).write_text(self.report["case"])
            assert not (evidence / "free-mode-control-done.json").exists()
            return self.report["status"] != "passed"

    monkeypatch.setattr(runner, "Runner", FakeRunner)
    assert asyncio.run(runner.run_suite(tmp_path, ["care", "harvest", "rest"], 60, None)) == 1
    assert calls == [("care", {"keep_open": True, "reset_fixture": False, "simulated": False}),
                     ("harvest", {"keep_open": True, "reset_fixture": True, "simulated": False})]
    assert (evidence / "care/preset-report.md").read_text() == "care"
    assert runner.read(evidence / "free-mode-control-done.json") == {"success": False}
    assert "rest：未执行" in (tmp_path / "summary.md").read_text(encoding="utf-8")
