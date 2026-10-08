"""Smoke test: start the MCP server over stdio, list tools, call get_status."""
import asyncio
import sys
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

ROOT = Path(__file__).resolve().parent.parent


async def main():
    params = StdioServerParameters(command=sys.executable, args=[str(ROOT / "run_bridge.py"), "mcp"], cwd=str(ROOT))
    async with stdio_client(params) as (r, w):
        async with ClientSession(r, w) as s:
            await s.initialize()
            tools = (await s.list_tools()).tools
            print(len(tools), "tools:", ", ".join(t.name for t in tools))
            res = await s.call_tool("get_status", {})
            print("get_status ->", str(res.content[0].text)[:300])
            res = await s.call_tool("set_throttle", {"value": 0})
            print("set_throttle ->", res.content[0].text)

asyncio.run(main())
