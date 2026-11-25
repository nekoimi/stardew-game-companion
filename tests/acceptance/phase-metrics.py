"""Read-only phase comparison metrics from actual isolated reports and usage.

Never infers model cache hits from local map caches or mock token counters.
"""
import argparse
import json
from pathlib import Path
import sqlite3


def collect(run):
    run = Path(run).resolve()
    reports = json.loads((run / "summary.json").read_text(encoding="utf-8-sig"))
    simulated = (run / "summary.md").read_text(encoding="utf-8-sig").startswith("# 伙伴实机回归 · 模拟 API")
    usage = []
    database = run / "data/companion.sqlite"
    if database.exists():
        with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as db:
            usage = [(request, json.loads(value or "{}")) for request, value in db.execute("SELECT request_id,value FROM usage")]
    rows = []
    for report in reports:
        trace_path = run / "evidence" / report["case"] / "preset-trace.json"
        trace = json.loads(trace_path.read_text(encoding="utf-8")) if trace_path.exists() else []
        turns = {row["payload"].get("turnId") for row in trace if row.get("type") == "tool"}
        turns.discard(None)
        counters = [value for request, value in usage if request.rsplit(":", 1)[0] in turns]
        tool_calls = [row["payload"] for row in trace if row.get("type") == "tool"]
        native = [row["payload"] for row in trace if row.get("type") == "result"]
        metrics = [row["payload"] for row in trace if row.get("type") == "model-metrics"]
        transport_path = run / "evidence/model-transport-metrics.jsonl"
        transport = [json.loads(line) for line in transport_path.read_text(encoding="utf-8").splitlines()] if transport_path.exists() else []
        # Transport evidence is run-wide; expose it per scene only for a single
        # scene experiment rather than assigning another scene's requests.
        transport_wait = sum(row["elapsedMs"] for row in transport) if len(reports) == 1 and transport else None
        totals = {key: sum(value[key] for value in counters if isinstance(value.get(key), (int, float)))
                  if counters and all(isinstance(value.get(key), (int, float)) for value in counters) else None
                  for key in ("input", "output", "cacheRead", "cacheWrite")}
        usage_coverage = {key: {"reportedRequests": sum(isinstance(value.get(key), (int, float)) for value in counters),
                                "totalRequests": len(counters),
                                "knownSum": sum(value[key] for value in counters if isinstance(value.get(key), (int, float)))}
                          for key in ("input", "output", "cacheRead", "cacheWrite")}
        rows.append({"case": report["case"], "status": report["status"], "source": "simulated" if simulated else "formal",
                     "backend": report.get("backend"), "elapsedSeconds": report.get("elapsedSeconds"),
                     "modelRequests": len(metrics) if metrics else len(counters),
                     "requestCountSource": "request-metrics" if metrics else "usage-records",
                     "retries": sum(row.get("requestKind") == "retry" for row in metrics) if metrics else sum(value.get("retry") is True for value in counters),
                     "modelWaitMs": sum(row.get("elapsedMs", 0) for row in metrics) if metrics else None,
                     "transportWaitMs": transport_wait, "transportRequests": len(transport) if transport_wait is not None else None,
                     "scanCacheState": "instrumented" if any(row.get("type") == "phase_map_cache" for row in trace) else "not-instrumented",
                     "modelRequestKinds": [row.get("requestKind") for row in metrics],
                     "supplierCacheState": "mock" if simulated else "provider-reported-not-controlled",
                     "usage": None if simulated else totals, "usageCoverage": None if simulated else usage_coverage,
                     "nativeActions": len(native),
                     "nativeFailures": sum(row.get("terminalState") not in ("succeeded", "partially-succeeded") for row in native),
                     "modelObservationCalls": sum(row.get("name") in ("get_status", "query_game") for row in tool_calls),
                     "multiTargetChop": any(len(((row.get("input") or {}).get("action") or {}).get("chop_tree", {}).get("tiles", [])) > 1 for row in tool_calls),
                     "checks": report.get("checks", {}), "error": report.get("error"), "evidence": str(run)})
    return rows


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runs", nargs="+")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    result = [row for run in args.runs for row in collect(run)]
    Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    for row in result:
        print(f"{row['source']} {row['case']}: {row['status']}, model={row['modelRequests']}, native={row['nativeActions']}, seconds={row['elapsedSeconds']}")
