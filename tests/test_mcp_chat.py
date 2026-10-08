"""Offline test of the ChatGPT-desktop (MCP) chat channel: queue -> pending -> reply -> drain, on a temp
queue file (doesn't touch the live chatgpt_chat.json or KSP). Then lists the MCP tools over stdio.
  python tests/test_mcp_chat.py"""
import asyncio
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from kspchat import chat, config, mcp_chat, mcp_server, memory  # noqa: E402


def offline():
    real = config.CHAT_QUEUE_FILE
    with tempfile.TemporaryDirectory() as d:
        config.CHAT_QUEUE_FILE = Path(d) / "q.json"
        mcp_chat.mode = lambda: "mcp"  # don't depend on bridge_settings.json
        memory.log_turn = lambda rec: None  # keep test turns out of logs/sessions-*.jsonl
        try:
            s = chat.Session()
            r, tools = s.send("what is my status", "chatgpt")
            assert "Waiting for ChatGPT desktop" in r and "OPENAI_API_KEY" not in r and not tools, r
            assert s.last_name == "Bridge"
            r2, _ = s.send("then circularize", "ChatGPT (MCP)")
            print("queued:", r2)
            p = mcp_server.game_chat_pending()
            assert "#1" in p and "#2" in p, p
            r3, _ = s.send("one more", "chatgpt")
            assert r3.startswith("Queued for ChatGPT desktop"), r3  # ChatGPT is now "listening"
            print(mcp_server.game_chat_reply("Status: 80 km orbit.", 1))
            assert "1 message(s) still open" in mcp_server.game_chat_reply("Circularizing now.", 2)
            assert mcp_chat.drain() == ["Status: 80 km orbit.", "Circularizing now."]
            assert mcp_chat.drain() == []
            assert "#3" in mcp_server.game_chat_wait(1)
            mcp_chat.clear()
            assert mcp_server.game_chat_wait(1).startswith("No new")
            print("offline round trip OK")
        finally:
            config.CHAT_QUEUE_FILE = real


async def list_tools():
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client
    params = StdioServerParameters(command=sys.executable, args=[str(ROOT / "run_bridge.py"), "mcp"], cwd=str(ROOT))
    async with stdio_client(params) as (r, w):
        async with ClientSession(r, w) as s:
            await s.initialize()
            names = [t.name for t in (await s.list_tools()).tools]
            assert {"game_chat_pending", "game_chat_wait", "game_chat_reply"} <= set(names), names
            print(len(names), "MCP tools incl. game_chat_pending/wait/reply")

offline()
asyncio.run(list_tools())