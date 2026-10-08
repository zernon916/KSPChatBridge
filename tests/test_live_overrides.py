"""Offline: typo-tolerant orders, speed / altitude / overrides while the landing / fly-to autopilot flies (plane.LIVE),
overrides always accepted (stored when nothing is engaged), 'override speed max', and the airframe / crew watcher
(protect: easing the override on G / heat / breakage, G-force blackout). Mocked kRPC only; no KSP, no live files."""
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, ksp_actions, orders, plane, protect, speedcap  # noqa: E402


@pytest.fixture
def w(monkeypatch):
    """A jet at 9000 m with the landing autopilot en route (plane.active) and the holds off."""
    calls = []
    flight = SimpleNamespace(mean_altitude=9000.0, surface_altitude=8900.0, lift=90000.0, speed=200.0)
    v = SimpleNamespace(situation="VesselSituation.flying", name="Test Jet", mass=10000.0, crew=[],
                        parts=SimpleNamespace(wheels=[1], engines=[], intakes=[1], parachutes=[]),
                        orbit=SimpleNamespace(body=SimpleNamespace(reference_frame="bref", surface_gravity=9.81, name="Kerbin")),
                        flight=lambda ref=None: flight)
    st = {"hold": False, "plane": True}
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions, "pilot_name", lambda: None)
    monkeypatch.setattr(ksp_actions.hold, "active", lambda: st["hold"])
    monkeypatch.setattr(ksp_actions.plane, "active", lambda: st["plane"])
    monkeypatch.setattr(ksp_actions.hold, "set_targets", lambda **kw: calls.append(kw))
    monkeypatch.setattr(ksp_actions.maxspeed, "clamp", lambda v, s, a=None: (min(float(s), 400.0),
                        "Best estimate max is ~400 m/s here; you asked %.0f. Flying 400." % s if s > 400 else ""))
    monkeypatch.setattr(ksp_actions.maxspeed, "estimate", lambda v, a=None: 410.0)
    monkeypatch.setattr(plane, "STATUS", {"phase": "to_entry"})
    monkeypatch.setattr(plane, "LIVE", {})
    monkeypatch.setattr(chat, "_post", lambda *a, **k: pytest.fail("the LLM was called"))
    speedcap.clear_override("all")
    speedcap._pending.clear()
    yield SimpleNamespace(calls=calls, v=v, st=st)
    speedcap.clear_override("all")
    speedcap._pending.clear()


@pytest.mark.parametrize("typed,tool,args", [
    ("Overide speed to 325", "set_override", {"kind": "speed", "value": 325.0, "authority": "ask"}),
    ("overrride bank 40", "set_override", {"kind": "bank", "value": 40.0, "authority": "ask"}),
    ("over ride altitude 20 km", "set_override", {"kind": "altitude", "value": 20000.0, "authority": "ask"}),
    ("authorse all", "authorise_all", {}),
    ("throtle 60", "set_throttle", {"value": 0.6}),
    ("altitdue 5000", "set_altitude", {"altitude_m": 5000.0, "direction": "", "ref": "msl", "urgent": False}),
    ("speed to 325", "set_speed", {"speed": 325.0}),
    ("circel here", "circle_here", {"direction": "left"}),
    ("Overide speed max", "set_override", {"kind": "speed", "value": "max", "authority": "ask"}),
    ("override speed full ***", "set_override", {"kind": "speed", "value": "max", "authority": "pre"}),
])
def test_typo_tolerant_parse(typed, tool, args):
    assert orders.parse(typed) == (tool, args)


def test_typos_leave_other_text_alone():
    assert orders.fix_typos("fly to Island Airfield") == "fly to Island Airfield"
    assert orders.parse("spend 300") is None


def test_overide_speed_during_autoland_en_route(w):
    s = chat.Session("ingame")
    r, _ = s.send("Overide speed to 325", "local")
    assert "Override the speed cap 325 m/s EAS" in r and "(yes/no)" in r, r
    r, _ = s.send("yes", "local")
    assert r.startswith("Override: speed cap 325") and "Cruise speed 325 m/s for the en-route leg" in r, r
    assert plane.LIVE["vcruise"] == 325.0 and speedcap.limits()[1] == 325.0


def test_speed_en_route_final_and_clamp(w):
    s = chat.Session("ingame")
    assert s.send("speed 250", "local")[0].startswith("Cruise speed 250 m/s") and plane.LIVE["vcruise"] == 250.0
    r = s.send("speed 450", "local")[0]
    assert "Best estimate max is ~400" in r and plane.LIVE["vcruise"] == 400.0, r
    plane.STATUS["phase"] = "final"
    r = s.send("speed 150", "local")[0]
    assert "Flying 150 m/s on final as ordered" in r and "Caution" in r and plane.LIVE["v_final"] == 150.0, r
    plane.STATUS["phase"] = "takeoff"
    assert "once en route (now taking off)" in s.send("speed 200", "local")[0]


def test_speed_with_nothing_engaged_engages_holds(w, monkeypatch):
    w.st["plane"] = False
    monkeypatch.setattr(ksp_actions, "plane_hold", lambda **kw: "Holds engaged: speed %.0f." % kw["speed"])
    r = chat.Session("ingame").send("speed 180", "local")[0]
    assert r.startswith("Nothing was engaged, so I engaged the holds: Holds engaged: speed 180"), r


def test_overrides_always_stored(w):
    w.st["plane"] = False
    r = ksp_actions.call_tool("set_override", {"kind": "bank", "value": 60, "authority": "pre"})
    assert "Nothing is engaged - it applies to whatever autopilot engages next" in r and speedcap.bank_limit(100) == 60.0
    w.st["plane"] = True
    plane.STATUS["phase"] = "final"
    r = ksp_actions.call_tool("set_override", {"kind": "bank", "value": 45, "authority": "pre"})
    assert r.startswith("Override: bank limit 45") and "Caution" in r and speedcap.bank_override() == 45.0


def test_override_speed_max(w):
    w.st.update(hold=True, plane=False)
    s = chat.Session("ingame")
    assert "no cap, full throttle" in s.send("override speed max", "local")[0]
    r = s.send("yes", "local")[0]
    assert "No speed cap" in r or "no speed cap" in r, r
    assert w.calls[-1] == {"throttle": 1.0} and "~410 m/s" in r and speedcap.limits()[1] >= 1e5
    assert "Speed override MAX" in speedcap.status_text()
    s.send("override speed off", "local")
    assert speedcap.limits() == (200.0, 220.0)
    w.st.update(hold=False, plane=True)
    r = ksp_actions.call_tool("set_override", {"kind": "speed", "value": "max", "authority": "pre"})
    assert plane.LIVE["vcruise"] == 5000.0 and "Full throttle on the en-route leg" in r


# ---------------------------------------------------------------- protect: airframe / crew / blackout
def _s(t, g=1.0, **kw):
    return dict({"t": t, "g": g, "parts": 40, "crew": 1, "ctrl": True, "who": "Sidry Kerman", "temp": 0.5}, **kw)


@pytest.fixture
def said(monkeypatch):
    out = []
    monkeypatch.setattr(protect, "_say", out.append)
    speedcap.clear_override("all")
    yield out
    speedcap.clear_override("all")
    protect.reset()


def test_override_flown_then_eased_on_g_heat_breakage(said):
    speedcap.apply_override("bank", 60)
    assert speedcap.bank_limit(100) == 60.0                      # flown as commanded
    protect.tick(_s(0, g=7.5))
    assert protect.factor() == 0.75 and speedcap.bank_limit(100) == pytest.approx(50.0)
    assert said and "7.5 g" in said[-1] and "protecting the airframe" in said[-1]
    protect.tick(_s(1, temp=0.95))
    protect.tick(_s(2, parts=38))
    assert protect.factor() == pytest.approx(0.25) and len(said) == 1   # one message per 10 s
    for i in range(3, 40):
        protect.tick(_s(i))
    assert protect.factor() == 1.0 and speedcap.bank_limit(100) == 60.0   # restored when clear


def test_blackout_via_control_loss_and_recovery(said):
    speedcap.apply_override("pitch", 40)
    protect.tick(_s(0, g=8.0))
    protect.tick(_s(1, g=7.0, ctrl=False))
    assert protect.blackout() and any(m.startswith("Sidry Kerman blacked out!") for m in said), said
    assert speedcap.bank_limit(100) == 0.0 and speedcap.climb_pitch(10.0) <= 2.0
    assert "PILOT BLACKED OUT" in speedcap.status_text()
    protect.tick(_s(2, g=1.0, ctrl=True))
    assert not protect.blackout() and any("is back - resuming" in m for m in said)
    assert speedcap.climb_pitch(10.0) > 2.0


def test_blackout_fallback_sustained_g(said):
    for i in range(4):
        protect.tick(_s(i, g=7.0, ctrl=None))
    assert protect.blackout()
    for i in range(4, 11):
        protect.tick(_s(i, g=1.0, ctrl=None))
    assert not protect.blackout()


def test_no_blackout_on_unmanned_or_brief_g(said):
    protect.tick(_s(0, g=8.0, crew=0, ctrl=False))
    protect.tick(_s(1, g=7.0, ctrl=None))
    assert not protect.blackout()