"""Offline smoke test of backend switching (no network, no KSP, no keys needed):
aliases, Gemini model-id normalizing, clean "set X" errors when a key is missing, .env loading,
and one mocked Gemini tool-call round trip through chat.Session.
  python tests/test_backends.py"""
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
KEYS = ("GEMINI_API_KEY", "GOOGLE_API_KEY", "OPENAI_API_KEY", "CUSTOM_AI_KEY", "GROQ_API_KEY", "OPENROUTER_API_KEY",
        "HF_TOKEN", "HUGGINGFACE_API_KEY", "ANTHROPIC_API_KEY", "XAI_API_KEY", "GROK_API_KEY")
sys.path.insert(0, str(ROOT))
for k in KEYS:
    os.environ.pop(k, None)  # before import: .env must not leak a real key into this test
from kspchat import backends, chat, config, ksp_actions, mcp_chat, memory  # noqa: E402

for k in KEYS:
    os.environ.pop(k, None)
config.CUSTOM_AI_URL = config.CUSTOM_AI_MODEL = ""
memory.log_turn = lambda rec: None
mcp_chat.mode = lambda: "api"

# aliases
for name, want in [("Gemini", "gemini"), ("google", "gemini"), ("LM Studio", "local"), ("openai", "chatgpt"),
                   ("Custom (OpenAI-compatible)", "custom"), ("anthropic", "claude"), ("Grok Bot (soon)", "grokbot"), (None, "local")]:
    assert backends.normalize(name) == want, (name, backends.normalize(name))
assert backends.normalize_model("models/gemini-2.5-flash") == "gemini-2.5-flash"
assert backends.normalize_model("gemini-flash-latest") == "gemini-flash-latest"
assert set(backends.LABELS) == set(backends.BACKENDS)

# missing keys -> clean BackendError, no network
for b, needle in [("gemini", "GEMINI_API_KEY"), ("chatgpt", "OPENAI_API_KEY"), ("custom", "CUSTOM_AI_URL"),
                  ("groq", "GROQ_API_KEY"), ("openrouter", "OPENROUTER_API_KEY"), ("huggingface", "HF_TOKEN"),
                  ("claude", "ANTHROPIC_API_KEY"), ("grokbot", "XAI_API_KEY")]:
    try:
        backends.resolve(b)
        raise AssertionError(b + " resolved without config")
    except backends.BackendError as e:
        assert needle in str(e), (b, str(e))
assert backends.configured() == []
s = chat.Session()
r, tools = s.send("status?", "gemini")
assert "GEMINI_API_KEY" in r and not tools and s.last_name == "Bridge", r

# claude/grokbot are wired (P5-1), not stubs: OpenAI-compatible endpoints resolve with a key
os.environ["ANTHROPIC_API_KEY"] = "ant-test"
assert backends.resolve("claude") == (config.CLAUDE_URL, "ant-test", config.CLAUDE_MODEL)
assert backends.resolve("Claude (soon)") == (config.CLAUDE_URL, "ant-test", config.CLAUDE_MODEL)  # alias kept
os.environ.pop("ANTHROPIC_API_KEY")
os.environ["XAI_API_KEY"] = "xai-test"
assert backends.resolve("grokbot") == (config.XAI_URL, "xai-test", config.XAI_MODEL)
assert backends.resolve("grok") == (config.XAI_URL, "xai-test", config.XAI_MODEL)  # alias kept
os.environ["GROK_API_KEY"] = "grok-alt"
os.environ.pop("XAI_API_KEY")
assert backends.resolve("grokbot")[1] == "grok-alt"  # GROK_API_KEY accepted as an alternative
os.environ.pop("GROK_API_KEY")
assert backends.configured() == []

# key set (GOOGLE_API_KEY fallback) -> OpenAI-compatible Gemini endpoint
os.environ["GOOGLE_API_KEY"] = "test-key"
url, key, model = backends.resolve("gemini", "models/gemini-2.5-pro")
assert url == config.GEMINI_URL and key == "test-key" and model == "gemini-2.5-pro", (url, model)
assert backends.configured() == ["gemini"]
backends._get = lambda u, k=None, timeout=10: {"data": [{"id": "models/gemini-2.5-flash"}, {"id": "models/gemini-embedding-001"},
                                                         {"id": "models/gemini-2.5-flash-image"}, {"id": "models/gemini-2.5-pro"}]}
assert backends.list_models("gemini") == ["gemini-2.5-flash", "gemini-2.5-pro"], backends.list_models("gemini")

# mocked round trip: Gemini asks for get_status, then answers
calls = []
def fake_post(u, payload, key=None, timeout=300):
    calls.append((u, payload["model"], key))
    if len(calls) == 1:
        return {"choices": [{"message": {"content": None, "tool_calls": [
            {"id": "c1", "type": "function", "function": {"name": "get_status", "arguments": "{}"}}]}}]}
    assert payload["messages"][-1]["role"] == "tool" and payload["messages"][-1]["tool_call_id"] == "c1"
    return {"choices": [{"message": {"content": "In orbit at 80 km."}}]}
chat._post = fake_post
ksp_actions.call_tool = lambda name, args: "alt 80 km"
r, tools = s.send("status?", "Gemini")
assert r == "In orbit at 80 km." and [t["tool"] for t in tools] == ["get_status"], (r, tools)
assert calls[0] == (config.GEMINI_URL + "/chat/completions", config.GEMINI_MODEL, "test-key"), calls[0]
assert s.last_model == config.GEMINI_MODEL
os.environ.pop("GOOGLE_API_KEY")

# free clouds: key -> configured, OpenRouter lists only ':free' models
os.environ["GROQ_API_KEY"] = os.environ["OPENROUTER_API_KEY"] = "k"
assert backends.configured() == ["groq", "openrouter"], backends.configured()
assert backends.resolve("Groq")[0] == "https://api.groq.com/openai/v1"
assert backends.resolve("open router")[2] == config.OPENROUTER_MODEL
backends._get = lambda u, k=None, timeout=10: {"data": [{"id": "openrouter/free"}, {"id": "meta-llama/llama-3.3-70b-instruct:free"},
                                                         {"id": "openai/gpt-5"}]}
assert backends.list_models("openrouter") == ["meta-llama/llama-3.3-70b-instruct:free", "openrouter/free"]
os.environ.pop("GROQ_API_KEY"); os.environ.pop("OPENROUTER_API_KEY")

# 429 from a free tier: honour Retry-After once, then succeed; friendly message when it keeps failing
import importlib, io, json, urllib.error  # noqa: E401,E402
chat = importlib.reload(chat)  # undo the _post mock
memory.log_turn = lambda rec: None
seen, sleeps = [], []
class _Resp(io.BytesIO):
    def __enter__(self): return self
    def __exit__(self, *a): return False
def fake_urlopen(req, timeout=0):
    seen.append(1)
    if len(seen) == 1:
        raise urllib.error.HTTPError(req.full_url, 429, "Too Many", {"Retry-After": "2"}, io.BytesIO(b"{}"))
    return _Resp(json.dumps({"ok": 1}).encode())
chat.urllib.request.urlopen, chat.time.sleep = fake_urlopen, sleeps.append
assert chat._post("http://x/chat/completions", {}) == {"ok": 1} and sleeps == [2.0], sleeps
def always_429(req, timeout=0):
    raise urllib.error.HTTPError(req.full_url, 429, "Too Many", {"Retry-After": "1"}, io.BytesIO(b"{}"))
chat.urllib.request.urlopen = always_429
os.environ["GROQ_API_KEY"] = "k"
r, _ = chat.Session().send("status?", "groq")
assert "rate limit" in r and "AICS" in r, r
os.environ.pop("GROQ_API_KEY")

# custom backend + .env loader
config.CUSTOM_AI_URL, config.CUSTOM_AI_MODEL = "http://localhost:9999/v1", "some-model"
assert backends.resolve("custom") == ("http://localhost:9999/v1", None, "some-model")
with tempfile.TemporaryDirectory() as d:
    p = Path(d) / ".env"
    p.write_text('# comment\nexport KCB_TEST_A="x1"\nKCB_TEST_B=y2\n', encoding="utf-8")
    os.environ["KCB_TEST_B"] = "real"
    config._load_dotenv(p)
    assert os.environ.get("KCB_TEST_A") == "x1" and os.environ["KCB_TEST_B"] == "real"
    os.environ.pop("KCB_TEST_A"); os.environ.pop("KCB_TEST_B")
print("backends smoke OK:", ", ".join(f"{b}={backends.LABELS[b]}" for b in backends.BACKENDS))