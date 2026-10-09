"""KSPChatBridge entry point.

  python run_bridge.py serve                 # HTTP endpoint for the in-game chat window (default)
  python run_bridge.py mcp                   # stdio MCP server (launched by the ChatGPT desktop app)
  python run_bridge.py chat "msg" [--model local|ollama|chatgpt|gemini|custom]   # one-shot test
  python run_bridge.py repl [--model local]  # terminal chat
  python run_bridge.py tools                 # print the OpenAI tool schema
Release build: AICSBridge.exe takes the same commands (no argument = serve). Port: KSPCHAT_PORT (default 8765).
"""
import argparse
import json
import logging
import os
import sys
from logging.handlers import RotatingFileHandler

from kspchat import config

__version__ = "0.1.1"


def _no_console_streams():
    """Windowed exe (AICSBridge.exe) started without a console: sys.stdout/stderr are None, so print() and the
    stderr log handler would fail. Send them to devnull; logs/bridge.log still gets everything."""
    for name in ("stdout", "stderr"):
        if getattr(sys, name) is None:
            setattr(sys, name, open(os.devnull, "w", encoding="utf-8"))


def setup_logging(to_stderr=True):
    config.LOG_DIR.mkdir(parents=True, exist_ok=True)
    handlers = [RotatingFileHandler(config.LOG_DIR / "bridge.log", maxBytes=1_000_000, backupCount=2, encoding="utf-8")]
    if to_stderr:
        handlers.append(logging.StreamHandler(sys.stderr))
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=handlers)
    def unhandled(kind, value, traceback):
        logging.critical("unhandled bridge exception", exc_info=(kind, value, traceback))
    sys.excepthook = unhandled
    import threading
    threading.excepthook = lambda args: unhandled(args.exc_type, args.exc_value, args.exc_traceback)


def main():
    ap = argparse.ArgumentParser(description="KSPChatBridge %s (data: %s)" % (__version__, config.ROOT))
    ap.add_argument("cmd", nargs="?", default="serve", choices=["serve", "mcp", "chat", "repl", "tools"])
    ap.add_argument("message", nargs="?")
    ap.add_argument("--model", default="local")
    _no_console_streams()
    a = ap.parse_args()
    if a.cmd == "mcp":
        from kspchat import mcp_server
        return mcp_server.main()
    setup_logging()
    if a.cmd == "serve":
        from kspchat import http_server
        return http_server.serve()
    if a.cmd == "tools":
        from kspchat import ksp_actions
        return print(json.dumps(ksp_actions.tool_schemas(), indent=1))
    from kspchat.chat import Session
    s = Session()
    if a.cmd == "chat":
        reply, tools = s.send(a.message or "what is my ship status", a.model)
        for t in tools:
            print(f"[tool] {t['tool']}({t['args']}) -> {t['result'][:200]}")
        return print("[reply]", reply)
    while True:
        try:
            msg = input("you> ").strip()
        except (EOFError, KeyboardInterrupt):
            return
        if msg:
            reply, tools = s.send(msg, a.model)
            for t in tools:
                print(f"  [tool] {t['tool']} -> {t['result'][:150]}")
            print("bot>", reply)


if __name__ == "__main__":
    main()
