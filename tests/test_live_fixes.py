"""Offline: fixes from the 2026-10-08 17:40 live test - one-step eject (+ the /events path the mod polls), the no-tool
claim guard, hold-takeoff climb integrator / handoff, and calmer emergency handling of an engine stop.
In-memory only; the /events check runs an in-process server on an ephemeral port (never the live bridge's 8765)."""
import http.client
import sys
import threading
import time
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, emergency, hold, http_server, ksp_actions, orders, science, speedcap  # noqa: E402


@pytest.mark.parametrize("text,confirmed", [
    ("EJECT CONFIRM!", True), ("eject confirm", True), ("confirm eject", True), ("eject now", True),
    ("Eject the pilot, confirm", True), ("Now eject!", True), ("eject!", False), ("EJECT", False), ("eject eject!!", False),
])
def test_eject_forms(text, confirmed):
    assert orders.parse(text) == ("eject_kerbal", {"confirmed": True} if confirmed else {})


def test_eject_questions_are_not_orders():
    assert orders.parse("eject?") is None and orders.parse("should I eject") is None


@pytest.fixture
def v(monkeypatch):
    vessel = SimpleNamespace(crew=[SimpleNamespace(name="Sidry Kerman")])
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: vessel)
    monkeypatch.setattr(ksp_actions, "pilot_name", lambda: "Sidry")
    monkeypatch.setattr(science, "_cmd_seen", [time.time()])
    speedcap._pending.clear()
    yield vessel
    speedcap._pending.clear()


def test_one_step_eject_runs_and_reaches_the_mod_poll(v, monkeypatch):
    monkeypatch.setattr(science, "_events", [])
    monkeypatch.setattr(science, "_ev_id", 0)

    def post_event(text):  # science.post_event as written (test_flightplan swaps it module-wide)
        science._ev_id += 1
        science._events.append((science._ev_id, text))
    monkeypatch.setattr(science, "post_event", post_event)
    reply, tools = chat.Session("ingame").send("EJECT CONFIRM!", "local")
    assert reply.startswith("Ejecting Sidry Kerman") and not speedcap._pending, reply
    # what the mod's PollEvents GETs (ChatWindow.cs): events?format=text&chat=1&cmd=1&since=<id>
    srv = http_server._Server(("127.0.0.1", 0), http_server.Handler)
    th = threading.Thread(target=srv.serve_forever, daemon=True)
    th.start()
    try:
        c = http.client.HTTPConnection("127.0.0.1", srv.server_address[1], timeout=5)
        c.request("GET", "/events?format=text&chat=1&cmd=1&since=0")
        body = c.getresponse().read().decode("utf-8")
        c.request("GET", "/events?format=text&chat=1&since=0")   # an old mod build never sees mod commands
        old = c.getresponse().read().decode("utf-8")
    finally:
        srv.shutdown()
        srv.server_close()
    line = next(ln for ln in body.splitlines() if "\t!cmd " in ln)
    ev_id, text = line.split("\t", 1)
    assert int(ev_id) >= 1 and text.startswith("!cmd ") and "!cmd" not in old
    # ChatWindow.RunCommand: cmd = text.Substring(5).Split(' ', 3) -> ["eject", "<unix t>", "<crew name>"]
    p = text[5:].strip().split(" ", 2)
    assert p[0] == "eject" and abs(float(p[1]) - time.time()) < 30 and p[2] == "Sidry Kerman"


@pytest.mark.parametrize("text", ["Deorbit!", "deorbit", "deorbit burn", "do a deorbit burn", "de-orbit now"])
def test_deorbit_is_a_direct_order(text):
    assert orders.parse(text) == ("deorbit_burn", {})


def test_model_cannot_skip_the_yes(v):
    assert ksp_actions.captain_order("eject confirm").startswith("needs_confirm: Eject Sidry Kerman?")


def test_claim_guard_catches_action_verbs():
    for s in ("Ejecting Kerbal... (AICS mod spawns EVA)", "Chutes deployed.", "Staging now!", "Engaging autoland.",
              "Deploying the parachutes.", "Deorbit burn initiated at next apoapsis, lowering periapsis to 30 km."):
        assert chat.claims_action(s), s
    for s in ("The runway is 2.4 km long.", "Sidry is ready when you are.", "Speed is 230 m/s."):
        assert not chat.claims_action(s), s


def test_takeoff_integrator_and_handoff():
    i = 0.0
    for _ in range(200):                      # 9 deg short of the climb attitude for 10 s, airborne
        i = hold.takeoff_pitch_i(i, 9.0, 0.05, True)
    assert i == pytest.approx(hold.TO_PITCH_ILIM)  # bounded nose-up trim builds
    assert hold.takeoff_pitch_i(0.2, 9.0, 0.05, False) == 0.0   # never on the ground roll
    h = hold.takeoff_handoff
    assert h(10, None, 50, 5, True, None, 0) is None                       # climbing out, gear still down
    assert h(10, 0, 150, 5, False, None, 0) == "climbing"
    assert h(40, 10, 30, 0.5, True, None, 0) == "airborne"                  # skimming the water 30 s: hand off
    assert h(15, 10, 30, 0.5, True, 12.0, 0) == "pitch order"              # Luke's PULL UP gets flown
    assert h(15, 10, 10, 0.5, True, 12.0, 0) is None                        # not below 20 m


def test_engine_shutdown_alert_unchanged():  # Luke shut it down himself: the tamper alert was a correct detection
    d = emergency.Detector()
    cfg = {"engines": [("Whiplash", True, ""), ("Whiplash", True, "")], "surfaces": []}
    base = {"vid": 1, "sit": "flying", "speed": 300.0, "throttle": 0.3, "crew": 1, "ctrl": True}
    d.tick(dict(base, t=0, cfg=cfg))
    evs = d.tick(dict(base, t=3, cfg={"engines": [("Whiplash", False, ""), ("Whiplash", True, "")], "surfaces": []}))
    assert len(evs) == 1 and evs[0]["tag"] == "engine_off" and evs[0]["actions"] == ["level", "relight_off"]
    assert emergency.compose(evs[0], "Sidry", []).startswith("Sidry: Who shut down an engine?!")
