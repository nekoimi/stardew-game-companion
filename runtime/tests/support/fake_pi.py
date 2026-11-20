"""A real JSONL subprocess for testing the Pi adapter without model/account use."""
import argparse
import json
import os
import sys
import time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--session")
parser.add_argument("--session-dir")
args, _ = parser.parse_known_args()
session = Path(args.session) if args.session else Path(args.session_dir) / "original.jsonl"
mode = os.environ.get("FAKE_PI_MODE", "normal")
log = Path(os.environ["FAKE_PI_LOG"])


def emit(event):
    print(json.dumps(event, ensure_ascii=False), flush=True)


for line in sys.stdin:
    frame = json.loads(line)
    with log.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(frame, ensure_ascii=False) + "\n")
    kind = frame["type"]
    if kind == "get_state":
        if mode == "mismatch":
            session = session.with_name("substitute.jsonl")
        emit({"type": "response", "id": frame["id"], "success": True,
              "data": {"sessionId": "original", "sessionFile": str(session)}})
    elif kind == "prompt":
        session.write_text('{"type":"session","id":"original"}\n', encoding="utf-8")
        emit({"type": "response", "id": frame["id"], "success": True,
              "data": {"disposition": "started"}})
        if mode == "silent":
            continue
        if mode != "unconfirmed":
            emit({"type": "message_end", "message": {"role": "user",
                  "content": [{"type": "text", "text": frame["message"]}]}})
        # Fill stderr beyond a normal pipe buffer: the adapter must drain it.
        sys.stderr.write("private diagnostic " * 12000)
        sys.stderr.flush()
        emit({"type": "agent_start"})
        if mode == "compact":
            emit({"type": "compaction_start", "reason": "threshold"})
            emit({"type": "compaction_end", "reason": "threshold", "aborted": False,
                  "result": {"summary": "压缩旧对话", "firstKeptEntryId": "kept"}})
        rounds = 7 if mode == "progress" else 1
        for index in range(rounds):
            emit({"type": "tool_execution_start", "toolCallId": str(index),
                  "toolName": "mcp__stardew_companion__get_status"})
            if mode == "progress":
                time.sleep(0.07)
            emit({"type": "tool_execution_end", "toolCallId": str(index),
                  "toolName": "mcp__stardew_companion__get_status", "isError": False})
        if mode == "cancel":
            continue
        if mode == "invalid":
            print("not json", flush=True)
            continue
        message = {"role": "assistant", "content": [{"type": "text", "text": "已核对菜地。\u2028继续照料。"}],
                   "stopReason": "error" if mode == "error" else "stop",
                   "usage": {"input": 50, "cacheRead": 20, "cacheWrite": 10, "output": 8}}
        emit({"type": "message_end", "message": message})
        emit({"type": "agent_end", "messages": [message], "willRetry": mode == "retry"})
        if mode == "retry":
            emit({"type": "agent_start"})
            message = {**message, "content": [{"type": "text", "text": "恢复后继续照料。"}]}
            emit({"type": "message_end", "message": message})
            emit({"type": "agent_end", "messages": [message], "willRetry": False})
        emit({"type": "agent_settled", "aborted": False})
    elif kind == "abort":
        emit({"type": "response", "id": frame["id"], "success": True})
        emit({"type": "agent_settled", "aborted": True})
