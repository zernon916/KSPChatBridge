"""Small local models: lite prompt + tool subset, context-length error, prune echoed errors from history."""
import json
import urllib.error
from io import BytesIO
from types import SimpleNamespace as NS

from kspchat import chat, ksp_actions


def test_is_small_model():
    assert chat.is_small_model("qwen3.5-0.8b")
    assert chat.is_small_model("lfm2.5-1.2b-instruct")
    assert chat.is_small_model("foo_0.8B_bar")
    assert not chat.is_small_model("qwen/qwen3.5-9b")
    assert not chat.is_small_model("gpt-4.1-mini")


def test_lite_prompt_and_tool_subset():
    s = chat.Session("lite")
    full, lite = s.system_prompt(lite=False), s.system_prompt(lite=True)
    assert len(lite) < len(full) // 3
    assert "heli_control" in lite and "Never add extra maneuvers" not in lite
    hide = {f.__name__ for f in ksp_actions.TOOLS if f.__name__ not in chat.LITE_TOOLS}
    names = {t["function"]["name"] for t in ksp_actions.tool_schemas(hide)}
    assert "get_status" in names and "captain_order" in names and "heli_control" in names
    assert "dock_with" not in names and "transfer_to" not in names


def test_prune_error_history():
    hist = [
        {"role": "user", "content": "status"},
        {"role": "assistant", "content": "Altitude 1 km."},
        {"role": "user", "content": "hi"},
        {"role": "assistant", "content": "Chat error: ValueError: boom"},
        {"role": "user", "content": "hi again"},
        {"role": "assistant", "content": chat.CONTEXT_HELP},
    ]
    out = chat.prune_error_history(hist)
    assert out == [{"role": "user", "content": "status"}, {"role": "assistant", "content": "Altitude 1 km."}]


def test_context_length_error_message(monkeypatch):
    s = chat.Session("ctx")
    monkeypatch.setattr(chat.backends, "resolve", lambda b, m: ("http://x/v1", None, "qwen3.5-0.8b"))
    monkeypatch.setattr(ksp_actions, "aircraft_now", lambda: False)
    monkeypatch.setattr(ksp_actions, "pilot_name", lambda: None)

    def boom(url, payload, key=None, timeout=300):
        raise urllib.error.HTTPError(url, 400, "Bad Request", hdrs=None,
                                     fp=BytesIO(b'{"error":"context length exceeded n_ctx=8192"}'))

    monkeypatch.setattr(chat, "_post", boom)
    reply, _ = s.send("what is my status", "local")
    assert "24k" in reply and "context" in reply.lower() and "8k" in reply
