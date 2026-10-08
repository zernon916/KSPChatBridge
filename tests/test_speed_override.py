"""Offline: speed-cap override authority (kspchat/speedcap.py) through the tool layer (plane_hold with mocked kRPC),
the chat session (plain yes/no, /override speed on|off) and a Flight Plan step. No KSP, no network."""
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, flightplan as fp, ksp_actions, speedcap, takeoff as tko  # noqa: E402


@pytest.fixture(autouse=True)
def clean():
    for k in ("speed", "altitude"):
        speedcap.set_authority(False, k)
    tko._CEIL_CACHE.clear()
    yield
    for k in ("speed", "altitude"):
        speedcap.set_authority(False, k)


@pytest.fixture
def world(monkeypatch):
    """plane_hold against a fake flying jet at 500 m (sea-level TWR 0.4 -> safe ceiling 11 km): records
    hold.set_targets calls."""
    calls = []
    flight = SimpleNamespace(mean_altitude=500.0)
    jet = SimpleNamespace(propellant_names=["LiquidFuel", "IntakeAir"], max_thrust_at=lambda p: 39240.0, max_thrust=39240.0)
    v = SimpleNamespace(situation="VesselSituation.flying", name="Test Jet", mass=10000.0,
                        parts=SimpleNamespace(wheels=[1], engines=[jet]), resources=SimpleNamespace(max=lambda n: 2.0),
                        orbit=SimpleNamespace(body=SimpleNamespace(reference_frame="bref", surface_gravity=9.81)),
                        flight=lambda ref=None: flight)
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions.guard, "busy", lambda: None)
    monkeypatch.setattr(ksp_actions, "_nearest_runway_elev", lambda v: None)
    monkeypatch.setattr(ksp_actions.hold, "active", lambda: True)
    monkeypatch.setattr(ksp_actions.hold, "set_targets", lambda **kw: calls.append(kw))
    return calls


def test_eas_model_and_limits():
    assert speedcap.limits() == (200.0, 220.0)
    assert abs(speedcap.eas_at(300, 0) - 300) < 1e-9 and speedcap.eas_at(360, 12000) < 200
    assert abs(speedcap.tas_at(speedcap.eas_at(250, 7000), 7000) - 250) < 1e-6
    assert speedcap.check("plane_hold", {}, 215, 0) is None          # under the cap
    assert speedcap.check("plane_hold", {}, 320, 8000) is None       # fast but thin air (EAS ~157)


def test_tool_asks_instead_of_flying(world):
    r = ksp_actions.call_tool("plane_hold", {"speed": 300})
    assert r.startswith("needs_override:") and "220 m/s low-altitude cap" in r and "Grant override authority? (yes/no)" in r
    assert world == []                                               # nothing applied
    assert speedcap.pending("ingame")["eas"] > 220 and speedcap.pending("other") is None
    assert ksp_actions.call_tool("plane_hold", {"speed": 180})[:5] == "Holds" and world[-1]["speed"] == 180.0


def test_chat_yes_grants_and_applies(world):
    s = chat.Session("ingame")
    with speedcap.use_session("ingame"):
        assert ksp_actions.call_tool("plane_hold", {"speed": 300, "heading": 90}).startswith("needs_override")
    reply, tools = s.send("yes", "local")
    assert reply.startswith("Override authority granted") and "Holds updated" in reply, reply
    assert world[-1]["speed"] == 300.0 and world[-1]["heading"] == 90.0 and tools[0]["tool"] == "plane_hold"
    tgt, cap = speedcap.limits()
    assert tgt >= speedcap.eas_at(300, 500) - 0.01 and cap > 220 and speedcap.pending("ingame") is None
    assert s.history[-1]["content"] == reply
    assert s.send("/override speed off", "local")[0].startswith("Speed override OFF") and speedcap.limits() == (200.0, 220.0)


def test_chat_no_keeps_cap(world):
    s = chat.Session("ingame")
    with speedcap.use_session("ingame"):
        ksp_actions.call_tool("plane_hold", {"speed": 300})
    reply, _ = s.send("No.", "local")
    assert "keeping the 220 m/s cap" in reply and world == [] and speedcap.limits() == (200.0, 220.0)
    assert speedcap.pending("ingame") is None


def test_pending_is_per_session(world):
    with speedcap.use_session("other"):
        ksp_actions.call_tool("plane_hold", {"speed": 300})
    assert speedcap.pending("other") and speedcap.pending("ingame") is None
    assert speedcap.grant("ingame") is None and speedcap.limits()[1] == 220.0


def test_override_command_on_off(world):
    s = chat.Session("ingame")
    assert "cap 220" in s.send("/override", "local")[0]
    assert s.send("/override speed on", "local")[0].startswith("Override authority ON for the speed cap")
    r = ksp_actions.call_tool("plane_hold", {"speed": 300})
    assert r.startswith("Holds updated") and world[-1]["speed"] == 300.0 and speedcap.limits()[1] > 220
    s.send("/override speed off", "local")
    assert ksp_actions.call_tool("plane_hold", {"speed": 300}).startswith("needs_override")
    assert speedcap.answer("yes please") == "yes" and speedcap.answer("nope") == "no" and speedcap.answer("yes, 300 at 2 km") is None


def _fp_world(monkeypatch, answer):
    """Flight Plan runner pieces: _tool checks the cap like plane_hold; Luke answers during the first wait."""
    calls, said = [], []

    def tool(name, **a):
        calls.append((name, dict(a)))
        return speedcap.check(name, a, a["speed"], 500.0) or "Holds updated."

    def sleep(dt):
        if speedcap.pending("ingame"):
            (speedcap.grant if answer == "yes" else speedcap.deny)("ingame")
    clock = [0.0]
    monkeypatch.setattr(fp, "_tool", tool)
    monkeypatch.setattr(fp, "_say", said.append)
    monkeypatch.setattr(fp, "_sleep", sleep)
    monkeypatch.setattr(fp, "_now", lambda: clock[0])
    monkeypatch.setattr(fp, "_stopped", lambda: False)
    return calls, said


def test_flight_plan_step_waits_for_yes(monkeypatch):
    calls, said = _fp_world(monkeypatch, "yes")
    r = fp._gated_tool("plane_hold", speed=300.0)
    assert r == "Holds updated." and [c[1]["speed"] for c in calls] == [300.0, 300.0]
    assert any("Grant override authority? (yes/no)" in m for m in said)


def test_flight_plan_step_denied_flies_at_cap(monkeypatch):
    calls, said = _fp_world(monkeypatch, "no")
    r = fp._gated_tool("plane_hold", speed=300.0)
    assert r == "Holds updated." and calls[1][1]["speed"] <= 220.0 and speedcap.limits()[1] == 220.0
    assert any("keeping the speed cap" in m for m in said)


def test_altitude_above_ceiling_asks_then_yes_then_off(world):
    r = ksp_actions.call_tool("plane_hold", {"altitude_m": 15000})
    assert r.startswith("needs_override:") and "estimated safe ceiling of 11000 m" in r and "(yes/no)" in r
    assert world == []
    assert ksp_actions.call_tool("plane_hold", {"altitude_m": 9000}).startswith("Holds") and world[-1]["altitude"] == 9000.0
    s = chat.Session("ingame")
    ksp_actions.call_tool("plane_hold", {"altitude_m": 15000})
    reply, _ = s.send("yes", "local")
    assert "safe ceiling raised to 15000 m" in reply and world[-1]["altitude"] == 15000.0, reply
    assert speedcap.limits() == (200.0, 220.0)                 # the speed cap is separate
    assert s.send("/override altitude off", "local")[0].startswith("Altitude override OFF")
    assert ksp_actions.call_tool("plane_hold", {"altitude_m": 15000}).startswith("needs_override")
    reply, _ = s.send("no", "local")
    assert "keeping the 11000 m estimated safe ceiling" in reply and world[-1]["altitude"] == 15000.0


def test_altitude_override_command(world):
    s = chat.Session("ingame")
    assert s.send("/override altitude on", "local")[0].startswith("Override authority ON for the safe ceiling")
    assert ksp_actions.call_tool("plane_hold", {"altitude_m": 16000}).startswith("Holds")
    assert ksp_actions.call_tool("plane_hold", {"speed": 300}).startswith("needs_override")  # speed still gated
    assert "Altitude override ON" in s.send("/override", "local")[0]


def test_flight_plan_altitude_denied_flies_at_ceiling(monkeypatch):
    calls, said = [], []

    def tool(name, **a):
        calls.append(dict(a))
        return speedcap.check_altitude(name, a, a["altitude_m"], 11000.0, given=a["altitude_m"]) or "Holds updated."
    monkeypatch.setattr(fp, "_tool", tool)
    monkeypatch.setattr(fp, "_say", said.append)
    monkeypatch.setattr(fp, "_sleep", lambda dt: speedcap.deny("ingame"))
    monkeypatch.setattr(fp, "_now", lambda: 0.0)
    monkeypatch.setattr(fp, "_stopped", lambda: False)
    assert fp._gated_tool("plane_hold", altitude_m=14000.0) == "Holds updated."
    assert [c["altitude_m"] for c in calls] == [14000.0, 11000.0] and any("safe ceiling" in m for m in said)
