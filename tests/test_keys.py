"""Offline test of in-game API key entry (keys.py + POST /api_key, GET /api_keys): .env write/clear, live apply,
masking (never the full key in replies/status), Custom URL/model. Uses a temp .env; no network.
  python tests/test_keys.py"""
import json
import os
import sys
import tempfile
import threading
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from kspchat import backends, config, http_server, keys, mcp_chat  # noqa: E402

for b, (_, names, _, _) in backends.KEYED.items():
    for n in names:
        os.environ.pop(n, None)
config.CUSTOM_AI_URL = config.CUSTOM_AI_MODEL = ""
tmp = Path(tempfile.mkdtemp()) / ".env"
tmp.write_text("# mine\nOTHER=1\nGROQ_API_KEY=old-groq-key-0000\n", encoding="utf-8")
keys.ENV_FILE = tmp
os.environ["GROQ_API_KEY"] = "old-groq-key-0000"
backends._get = lambda u, k=None, timeout=10: {"data": [{"id": "openai/gpt-oss-120b"}, {"id": "whisper-large"}]}
mcp_chat.mode = lambda: "mcp"
SECRET = "gsk_supersecretvalue1234"

assert keys.mask(SECRET) == "••••1234" and keys.mask("") == "" and keys.mask("short") == "••••"
r = keys.set_key("groq", "  Bearer " + SECRET + "\n")
assert SECRET not in r and "••••1234" in r and "Verified" in r, r
assert os.environ["GROQ_API_KEY"] == SECRET and "groq" in backends.configured()
env = tmp.read_text(encoding="utf-8")
assert f"GROQ_API_KEY={SECRET}" in env and "old-groq" not in env and "OTHER=1" in env and "# mine" in env, env
assert SECRET not in keys.status_text() and "groq\t1\t••••1234" in keys.status_text()

r = keys.set_key("groq", clear=True)
assert "cleared" in r and "GROQ_API_KEY" not in tmp.read_text() and "groq" not in backends.configured(), r
assert "spaces" in keys.set_key("gemini", "two words") and "paste a key" in keys.set_key("gemini", "")
assert "no API key" in keys.set_key("local", "x")

# Custom: URL + model (+ optional key), live
assert "http" in keys.set_key("custom", url="ftp://x")
r = keys.set_key("custom", url="https://api.example.com/v1/", model="")
assert "needs: model" in r and config.CUSTOM_AI_URL == "https://api.example.com/v1", r
r = keys.set_key("custom", model="m-1")
assert "custom" in backends.configured() and backends.resolve("custom") == ("https://api.example.com/v1", None, "m-1"), r
assert "custom_url\thttps://api.example.com/v1" in keys.status_text()

# ChatGPT key in MCP mode -> hint
r = keys.set_key("openai", "sk-test-abcdefgh9876")
assert "MCP mode" in r and "••••9876" in r, r

# over HTTP (JSON only; key never echoed)
config.HTTP_PORT = 0
srv = http_server._Server(("127.0.0.1", 0), http_server.Handler)
threading.Thread(target=srv.serve_forever, daemon=True).start()
base = f"http://127.0.0.1:{srv.server_address[1]}"
def post(body, ctype="application/json"):
    req = urllib.request.Request(base + "/api_key", json.dumps(body).encode(), {"Content-Type": ctype})
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
code, txt = post({"backend": "gemini", "key": "AIzaTESTKEY-xyz5678"})
assert code == 200 and "••••5678" in txt and "AIzaTESTKEY" not in txt, (code, txt)
assert post({"backend": "gemini", "key": "zzzzzzzzzzzz"}, "text/plain")[0] == 415
with urllib.request.urlopen(base + "/api_keys") as r:
    st = r.read().decode()
assert "gemini\t1\t••••5678" in st and "AIzaTESTKEY" not in st, st
srv.shutdown()
print("test_keys OK")
