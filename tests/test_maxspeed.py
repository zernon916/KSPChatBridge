"""Offline: max-speed estimate (kspchat/maxspeed.py) and its clamp in plane_hold / fly_to / the direct 'set speed'
chat command, with a mocked kRPC vessel. No KSP, no network."""
import math
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, ksp_actions, maxspeed, speedcap, takeoff as tko  # noqa: E402


def rho(h):
    return 1.225 * math.exp(-h / 5600.0)


def make_vessel(speed=200.0, alt=0.0, drag=12250.0, q=24500.0, vs=0.0, jet_n=40000.0, rocket=None):
    flight = SimpleNamespace(mean_altitude=alt, speed=speed, dynamic_pressure=q, vertical_speed=vs,
                             drag=(0.0, -drag, 0.0), atmosphere_density=rho(alt), latitude=0.0, longitude=0.0)
    jet = SimpleNamespace(propellant_names=["LiquidFuel", "IntakeAir"], available_thrust=jet_n, max_thrust=jet_n,
                          max_thrust_at=lambda p: 39240.0, kerbin_sea_level_specific_impulse=4000.0, specific_impulse=4000.0)
    engines = [jet] + ([rocket] if rocket else [])
    body = SimpleNamespace(reference_frame="bref", surface_gravity=9.81, equatorial_radius=600000.0,
                           density_at=rho, pressure_at=lambda h: 101325.0 * math.exp(-h / 5600.0))
    return SimpleNamespace(situation="VesselSituation.flying", name="Test Jet", mass=10000.0,
                           control=SimpleNamespace(throttle=0.5), crew=[],
                           parts=SimpleNamespace(wheels=[1], legs=[], engines=engines, parachutes=[],
                                                 modules_with_name=lambda n: []),
                           resources=SimpleNamespace(max=lambda n: 2.0, amount=lambda n: 5000.0),
                           orbit=SimpleNamespace(body=body), flight=lambda ref=None: flight)


def test_vmax_math():
    # CdA 0.5 m^2, 40 kN: sqrt(2*40000 / (1.225*0.5)) = 361.4 -> x0.9 = 325
    assert abs(maxspeed.vmax(10000, 20000, 1.225, 1.225, 40000, 0) - 325.3) < 0.5
    # jets: thrust and drag both scale with density -> altitude doesn't change the estimate
    assert abs(maxspeed.vmax(10000, 20000, 1.225, 0.4, 40000, 0) - maxspeed.vmax(10000, 20000, 1.225, 1.225, 40000, 0)) < 1e-6
    # rockets: same thrust in thinner air -> faster
    assert maxspeed.vmax(10000, 20000, 1.225, 0.4, 0, 40000) > maxspeed.vmax(10000, 20000, 1.225, 1.225, 0, 40000)
    assert maxspeed.vmax(0, 20000, 1.225, 1.225, 40000, 0) is None and maxspeed.vmax(10000, 20000, 1.225, 1.2, 0, 0) == 0.0


def test_estimate_from_krpc_and_no_estimate_cases():
    est, info = maxspeed.estimate(make_vessel())
    assert abs(est - 325.3) < 0.5 and info["cda_m2"] == 0.5
    rocket = SimpleNamespace(propellant_names=["LiquidFuel", "Oxidizer"], available_thrust=20000.0,
                             available_thrust_at=lambda p: 20000.0 + 5000.0 * (1 - p))
    est2, info2 = maxspeed.estimate(make_vessel(rocket=rocket), 8000)
    assert est2 > est and info2["rocket_n"] > 20000
    assert maxspeed.estimate(make_vessel(speed=0.0, q=0.0))[0] is None             # on the ground: no clamp
    assert maxspeed.estimate(SimpleNamespace())[0] is None                        # kRPC can't tell: no clamp
    # already holding a speed above the estimate, level: never below it
    assert maxspeed.estimate(make_vessel(speed=340.0, q=70805.0, drag=35402.5))[0] >= 340.0
    assert maxspeed.clamp(make_vessel(), 300) == (300.0, "")
    assert maxspeed.clamp(make_vessel(), 400) == (325.0, "Best estimate max is ~325 m/s here; you asked 400. Flying 325.")


@pytest.fixture
def world(monkeypatch):
    calls = []
    v = make_vessel()
    tko._CEIL_CACHE.clear()
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions.guard, "busy", lambda: None)
    monkeypatch.setattr(ksp_actions, "_nearest_runway_elev", lambda v: None)
    monkeypatch.setattr(ksp_actions.hold, "active", lambda: True)
    monkeypatch.setattr(ksp_actions.hold, "set_targets", lambda **kw: calls.append(kw))
    for k in ("speed", "altitude"):
        speedcap.set_authority(False, k)
    yield SimpleNamespace(v=v, calls=calls)
    for k in ("speed", "altitude"):
        speedcap.set_authority(False, k)


def test_plane_hold_clamps_then_asks_for_the_cap(world):
    r = ksp_actions.call_tool("plane_hold", {"speed": 400})
    assert r.startswith("needs_override: Best estimate max is ~325 m/s here; you asked 400. Flying 325. That's above"), r
    assert world.calls == [] and speedcap.pending("ingame")["args"]["speed"] == 325.0
    reply, _ = chat.Session("ingame").send("yes", "local")                      # the granted request flies 325
    assert "Holds updated: speed 325 m/s" in reply and world.calls[-1]["speed"] == 325.0, reply
    speedcap.set_authority(False, "speed")
    assert ksp_actions.call_tool("plane_hold", {"speed": 180}) == "Holds updated: speed 180 m/s."


def test_plane_hold_clamp_with_override_on(world):
    speedcap.set_authority(True, "speed")
    r = ksp_actions.call_tool("plane_hold", {"speed": 400, "heading": 90})
    assert r == "Best estimate max is ~325 m/s here; you asked 400. Flying 325. Holds updated: heading 90, speed 325 m/s.", r
    assert world.calls[-1]["speed"] == 325.0


def test_direct_speed_command(world, monkeypatch):
    s = chat.Session("ingame")
    assert chat.parse_direct("set speed 150") == ("set_speed", {"speed": 150.0})
    assert chat.parse_direct("speed to 400 m/s") == ("set_speed", {"speed": 400.0})
    reply, tools = s.send("set speed 150", "local")
    assert reply == "Holds updated: speed 150 m/s." and tools[0]["tool"] == "set_speed"
    assert s.send("speed 400", "local")[0].startswith("Best estimate max is ~325 m/s here; you asked 400. Flying 325. That's above")  # direct path shows just the question
    monkeypatch.setattr(ksp_actions.hold, "active", lambda: False)
    monkeypatch.setattr(ksp_actions.plane, "active", lambda: False)
    monkeypatch.setattr(ksp_actions, "plane_hold", lambda **kw: "Holds engaged: speed %.0f m/s." % kw["speed"])
    assert s.send("speed 150", "local")[0].startswith("Nothing was engaged, so I engaged the holds")  # does it, not argues
    assert "set_speed" not in {t["function"]["name"] for t in ksp_actions.tool_schemas()}  # chat/menu only


def test_fly_to_clamps_cruise_speed(world, monkeypatch):
    started = []
    v = world.v
    monkeypatch.setattr(ksp_actions.guard, "refuse_msg", lambda: None)
    monkeypatch.setattr(ksp_actions, "conn", lambda: SimpleNamespace(space_center=SimpleNamespace(active_vessel=v)))
    monkeypatch.setattr(ksp_actions.spots, "resolve", lambda sc, n: ("Runway 27", {"mode": "H", "runway": "27"}))
    monkeypatch.setattr(ksp_actions.spots, "make_strip", lambda sp: {"a": (0.0, 0.0), "b": (0.0, 0.1)})
    monkeypatch.setattr(ksp_actions.spots, "gc_dist", lambda *a: 50000.0)
    monkeypatch.setattr(ksp_actions, "_plane_check", lambda *a, **k: None)
    monkeypatch.setattr(ksp_actions, "_set_final", lambda *a: None)
    monkeypatch.setattr(ksp_actions.time, "sleep", lambda s: None)
    monkeypatch.setattr(ksp_actions.plane, "start", lambda *a: started.append(a) or "started")
    r = ksp_actions.call_tool("fly_to", {"name": "Runway 27", "cruise_altitude_m": 6000, "cruise_speed": 400})
    assert r.startswith("Best estimate max is ~325 m/s at 6000 m; you asked 400. Flying 325. "), r
    assert started[-1][2] == 325.0