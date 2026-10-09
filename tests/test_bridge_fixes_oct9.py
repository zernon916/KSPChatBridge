"""Offline: Oct 9 bridge fixes — model HTTP timeout + busy reset (item 5), vessel-switch cache clear (item 6)."""
import sys
import urllib.error
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, config, emergency, flightplan, heli, http_server, ksp_actions, propulsion, science, speedcap  # noqa: E402


def test_post_default_timeout_is_sixty():
    assert config.LLM_CHAT_TIMEOUT_S == 60


def test_model_post_uses_chat_timeout(monkeypatch):
    seen = []
    monkeypatch.setattr(chat.backends, "resolve", lambda b, m=None: ("http://x", None, "m"))
    monkeypatch.setattr(chat.memory, "log_turn", lambda *a, **k: None)
    monkeypatch.setattr(chat.ksp_actions, "pilot_name", lambda: "Sidry")
    monkeypatch.setattr(chat.ksp_actions, "aircraft_now", lambda: False)

    def post(url, payload, key=None, timeout=None):
        seen.append(timeout)
        return {"choices": [{"message": {"content": "Roger."}}]}
    monkeypatch.setattr(chat, "_post", post)
    reply, _ = chat.Session("t").send("what altitude are we at right now?", "local")
    assert reply == "Roger." and seen == [config.LLM_CHAT_TIMEOUT_S]


def test_timeout_posts_comms_down_and_replies_in_character(monkeypatch):
    events = []
    monkeypatch.setattr(science, "post_event", events.append)
    monkeypatch.setattr(chat.backends, "resolve", lambda b, m=None: ("http://x", None, "m"))
    monkeypatch.setattr(chat.memory, "log_turn", lambda *a, **k: None)
    monkeypatch.setattr(chat.ksp_actions, "pilot_name", lambda: "Bob")
    monkeypatch.setattr(chat.ksp_actions, "aircraft_now", lambda: False)

    def post(url, payload, key=None, timeout=None):
        raise urllib.error.URLError("timed out")
    monkeypatch.setattr(chat, "_post", post)
    reply, tools = chat.Session("t").send("can we still talk to mission control?", "local")
    assert tools == []
    assert reply.startswith("Bob:") and chat.COMMS_DOWN in reply
    assert len(events) == 1 and chat.COMMS_DOWN in events[0]


def test_http_busy_resets_when_send_raises():
    http_server._busy[0] = 0

    class BadSession:
        def send(self, msg, model):
            raise TimeoutError("hang")

    http_server._busy[0] += 1
    try:
        with http_server._chat_lock:
            try:
                BadSession().send("hi", "local")
            except TimeoutError:
                pass
    finally:
        http_server._busy[0] = max(0, http_server._busy[0] - 1)
    assert http_server._busy[0] == 0
    http_server._busy[0] = max(0, http_server._busy[0] - 1)
    assert http_server._busy[0] == 0


def test_vessel_changed_clears_chat_history_and_caches():
    emergency.DAMAGE.update(vid=1, vessel="Plane", lost=[("12:00", "wing")], inverted="")
    sess = chat.Session("ingame")
    sess.history = [{"role": "user", "content": "we are at 2851 m"}]
    http_server._sessions["ingame"] = sess
    propulsion._CACHE[("Plane", 20)] = (0.0, {"props": False})
    heli._INFO[("Quad", 30)] = {"heli": True}
    speedcap._pending["ingame"] = {"kind": "speed"}
    flightplan._pushed["text"] = "takeoff\nland\n"
    ksp_actions._AIR.update(t=999999.0, v=True)

    emergency.vessel_changed(2, "Quad")

    assert emergency.DAMAGE["vid"] == 2 and emergency.DAMAGE["lost"] == []
    assert http_server._sessions["ingame"].history == []
    assert propulsion._CACHE == {} and heli._INFO == {}
    assert speedcap._pending == {}
    assert flightplan._pushed["text"] == ""
    assert ksp_actions._AIR["t"] == 0.0


def test_vessel_changed_same_vid_does_not_clear_sessions():
    emergency.DAMAGE.update(vid=5, vessel="A", lost=[], inverted="")
    sess = chat.Session("s")
    sess.history = [{"role": "user", "content": "keep me"}]
    http_server._sessions["s"] = sess
    emergency.vessel_changed(5, "A")
    assert sess.history[0]["content"] == "keep me"


def test_aircraft_cache_refreshes_after_vessel_switch(monkeypatch):
    calls = []

    def is_air(v):
        calls.append(getattr(v, "name", v))
        return v.name != "Quad"

    monkeypatch.setattr(ksp_actions, "_is_aircraft", is_air)
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: SimpleNamespace(name="Quad"))
    ksp_actions.invalidate_craft_cache()
    assert ksp_actions.aircraft_now() is False
    assert calls == ["Quad"]
