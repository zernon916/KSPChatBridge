"""stdio MCP server exposing the same KSP tools (stdio pattern:
the ChatGPT/Codex desktop app launches this process from ~/.codex/config.toml and talks over stdin/stdout).
Logs go to stderr only - stdout is the protocol channel."""
import functools
import logging
import sys

from mcp.server.mcpserver import MCPServer

import time

from . import ksp_actions, mcp_chat

CHAT_RULES = ("Act with the KSP tools, then answer with game_chat_reply(text, reply_to=id). Do only what Luke asked "
              "(orbit-changing actions only if he clearly asked now; prefer MechJeb tools). Reply 1-4 short plain sentences.")


def _fmt(msgs):
    now = time.time()
    return "\n".join(f"#{m['id']} ({int(now - m['ts'])}s ago): {m['text']}" for m in msgs) + "\n" + CHAT_RULES


def game_chat_pending() -> str:
    """Luke's messages from the in-game KSP chat waiting for you (ChatGPT backend, MCP mode - no API key).
    Call when Luke says "check KSP chat" (or periodically), act with the tools, then game_chat_reply."""
    msgs = mcp_chat.pending()
    return _fmt(msgs) if msgs else "No pending in-game messages."


def game_chat_wait(timeout_s: int = 45) -> str:
    """Wait up to timeout_s seconds (max 55) for a new in-game KSP chat message, then return it like
    game_chat_pending. Loop wait -> act -> game_chat_reply to keep answering the game chat ("keep answering KSP chat")."""
    end = time.time() + max(1, min(int(timeout_s or 45), 55))
    msgs = mcp_chat.pending()
    while not msgs and time.time() < end:
        time.sleep(1)
        if mcp_chat.has_new():
            msgs = mcp_chat.pending()
    return _fmt(msgs) if msgs else "No new in-game messages yet."


def game_chat_reply(text: str, reply_to: int = 0) -> str:
    """Show your answer in Luke's in-game KSP chat window (within ~3 s). reply_to = message id from
    game_chat_pending (0 = closes all open messages). Plain text, 1-4 sentences, no markdown."""
    return mcp_chat.reply(text, reply_to)


def build():
    server = MCPServer("ksp_bridge", instructions=(
        "Controls Luke's active Kerbal Space Program vessel via kRPC + MechJeb. "
        "Call get_status first. mechjeb_ascent returns immediately; poll get_status to follow the flight. "
        "In-game chat (no API key): when Luke types in the KSP chat with the ChatGPT backend, his message is queued; "
        "get it with game_chat_pending (or game_chat_wait), act, and answer with game_chat_reply."))
    for f in (game_chat_pending, game_chat_wait, game_chat_reply):
        server.tool(name=f.__name__, description=f.__doc__)(f)
    for f in ksp_actions.TOOLS:
        @functools.wraps(f)
        def wrapper(*a, __f=f, **kw):
            return ksp_actions.call_tool(__f.__name__, dict(zip(__f.__code__.co_varnames, a), **kw))
        server.tool(name=f.__name__, description=f.__doc__)(wrapper)
    return server


def main():
    logging.basicConfig(stream=sys.stderr, level=logging.WARNING)
    build().run("stdio")


if __name__ == "__main__":
    main()
