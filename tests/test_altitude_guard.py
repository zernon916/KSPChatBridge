"""Offline: direct altitude orders ('descend to 5km', 'MAX DOWN TO KSP ALTITUDE 5km', ...) -> set_altitude -> the holds;
aircraft guard on the rocket powered-descent landing tools (hidden from the model + refused); explicit enough_fuel
flags. Mocked kRPC only; no KSP, no network, no live files."""
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import backends, chat, ksp_actions, orders, speedcap, takeoff as tko  # noqa: E402


@pytest.fixture
def jet(monkeypatch):
    """A jet flying at 9000 m MSL (sea-level TWR 0.4 -> safe ceiling 11 km); records hold.set_targets."""
    calls = []
    flight = SimpleNamespace(mean_altitude=9000.0, surface_altitude=8900.0, lift=90000.0, speed=200.0,
                             vertical_speed=0.0)
    eng = SimpleNamespace(propellant_names=["LiquidFuel", "IntakeAir"], max_thrust_at=lambda p: 39240.0, max_thrust=39240.0)
    v = SimpleNamespace(situation="VesselSituation.flying", name="Test Jet", mass=10000.0,
                        parts=SimpleNamespace(wheels=[1], engines=[eng], intakes=[1], parachutes=[]),
                        resources=SimpleNamespace(max=lambda n: 2.0),
                        orbit=SimpleNamespace(body=SimpleNamespace(reference_frame="bref", surface_gravity=9.81, name="Kerbin")),
                        flight=lambda ref=None: flight)
    st = {"hold": True, "plane": False}
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions, "pilot_name", lambda: None)
    monkeypatch.setattr(ksp_actions.guard, "busy", lambda: None)
    monkeypatch.setattr(ksp_actions.guard, "refuse_msg", lambda: None)
    monkeypatch.setattr(ksp_actions, "_nearest_runway_elev", lambda v: None)
    monkeypatch.setattr(ksp_actions.hold, "active", lambda: st["hold"])
    monkeypatch.setattr(ksp_actions.plane, "active", lambda: st["plane"])
    monkeypatch.setattr(ksp_actions.hold, "set_targets", lambda **kw: calls.append(kw))
    monkeypatch.setattr(ksp_actions.lander, "start", lambda *a, **k: pytest.fail("rocket lander started for a plane"))
    monkeypatch.setattr(chat, "_post", lambda *a, **k: pytest.fail("the LLM was called"))
    for k in ("speed", "altitude"):
        speedcap.set_authority(False, k)
    speedcap._pending.clear()
    tko._CEIL_CACHE.clear()
    yield SimpleNamespace(calls=calls, v=v, flight=flight, st=st)
    for k in ("speed", "altitude"):
        speedcap.set_authority(False, k)
    speedcap._pending.clear()


@pytest.mark.parametrize("text,alt,d,urgent,ref", [
    ("NOW MAX DOWN TO KSP ALTITUDE 5km", 5000, "down", True, "msl"),
    ("descend to 5k", 5000, "down", False, "msl"),
    ("drop to 2000 m", 2000, "down", False, "msl"),
    ("go down to 3.5 km", 3500, "down", False, "msl"),
    ("take it down to 5k", 5000, "down", False, "msl"),
    ("climb to 8000", 8000, "up", False, "msl"),
    ("Climb to 8 km, now!", 8000, "up", True, "msl"),
    ("altitude 3000 agl", 3000, "", False, "agl"),
    ("set altitude 7200", 7200, "", False, "msl"),
])
def test_parse_altitude(text, alt, d, urgent, ref):
    assert orders.parse(text) == ("set_altitude", {"altitude_m": float(alt), "direction": d, "ref": ref, "urgent": urgent})


@pytest.mark.parametrize("text", ["descend to 5", "descend to 5km and land", "climb at 500", "climb at 50 m/s",
                                  "land at 5 km", "descend to the runway"])
def test_ambiguous_altitude_left_to_model(text):
    assert orders.parse(text) is None or orders.parse(text)[0] != "set_altitude"


def test_max_down_goes_to_holds_not_rocket_lander(jet):
    r, tools = chat.Session("ingame").send("NOW MAX DOWN TO KSP ALTITUDE 5km", "local")
    t = jet.calls[-1]
    assert t["altitude"] == 5000.0 and t["altitude_ref"] == "msl" and t["vertical_speed"] == -30.0, t
    assert "Descending at up to 30 m/s" in r and tools[0]["tool"] == "set_altitude", r


def test_plain_descend_rate(jet):
    chat.Session("ingame").send("descend to 5k", "local")
    assert jet.calls[-1]["vertical_speed"] == -15.0 and jet.calls[-1]["altitude"] == 5000.0


def test_climb_above_ceiling_asks_then_yes(jet):
    s = chat.Session("ingame")
    r, _ = s.send("climb to 15 km", "local")
    assert "estimated safe ceiling of 11000 m" in r and "(yes/no)" in r and jet.calls == [], r
    r, _ = s.send("yes", "local")
    assert "safe ceiling raised to 15000 m" in r and jet.calls[-1]["altitude"] == 15000.0, r
    assert "vertical_speed" not in jet.calls[-1]           # climbs keep the climb logic (no dive / rate change)


def test_wrong_direction_and_landing_autopilot(jet):
    s = chat.Session("ingame")
    assert "is above us" in s.send("descend to 12000", "local")[0] and jet.calls == []
    assert "is below us" in s.send("climb to 2 km", "local")[0]
    jet.st.update(hold=False, plane=True)                   # landing autopilot en route: its cruise altitude
    ksp_actions.plane.STATUS["phase"] = "to_entry"
    assert s.send("descend to 5 km", "local")[0].startswith("En-route cruise altitude 5000 m MSL") and jet.calls == []
    assert ksp_actions.plane.LIVE.pop("cruise_alt") == 5000.0
    ksp_actions.plane.STATUS["phase"] = "final"
    assert "go around" in s.send("descend to 5 km", "local")[0] and "cruise_alt" not in ksp_actions.plane.LIVE
    ksp_actions.plane.STATUS["phase"] = "idle"


def test_rocket_landing_tools_refused_for_aircraft(jet):
    for name, args in (("land_here", {}), ("land_here", {"force": True}), ("land_at_ksc", {"force": True}),
                       ("land_at", {"latitude": 0.0, "longitude": -74.0})):
        r = ksp_actions.call_tool(name, args)
        assert r.startswith("This is an aircraft") and "descend to 5 km" in r, (name, r)
    jet.st["hold"] = False                                  # no autopilot: intakes alone make it an aircraft
    assert ksp_actions._is_aircraft(jet.v)
    jet.v.parts.intakes, jet.flight.lift = [], 50000.0      # lift > 30 % of weight
    assert ksp_actions._is_aircraft(jet.v)


def test_rocket_is_not_aircraft(jet, monkeypatch):
    jet.st["hold"] = False
    jet.v.parts.intakes, jet.flight.lift = [], 100.0
    monkeypatch.setattr(ksp_actions, "_has_wings", lambda v: False)
    assert not ksp_actions._is_aircraft(jet.v) and ksp_actions._aircraft_refusal(jet.v) is None
    jet.v.situation = "VesselSituation.sub_orbital"
    assert not ksp_actions._is_aircraft(jet.v)


def test_rocket_tools_hidden_from_model_for_aircraft(jet, monkeypatch):
    names = {t["function"]["name"] for t in ksp_actions.tool_schemas(ksp_actions.ROCKET_LANDING_TOOLS)}
    assert not names & {"land_here", "land_at", "land_at_ksc"} and "land_plane" in names and "plane_hold" in names
    assert {"land_here", "land_at", "land_at_ksc"} <= {t["function"]["name"] for t in ksp_actions.tool_schemas()}
    seen = {}

    def post(url, body, key=None, timeout=None):
        seen["tools"] = {t["function"]["name"] for t in body.get("tools", [])}
        return {"choices": [{"message": {"role": "assistant", "content": "Roger."}}]}
    monkeypatch.setattr(chat, "_post", post)
    monkeypatch.setattr(backends, "resolve", lambda b, m=None: ("http://x", "k", "m"))
    chat.Session("ingame").send("what should we do about the weather", "local")
    assert seen["tools"] and "land_here" not in seen["tools"] and "plane_hold" in seen["tools"]


def _dv_world(monkeypatch, total, accel=20.0, speed=100.0, aircraft=False):
    v = SimpleNamespace(available_thrust=accel * 1000.0, mass=1000.0, situation="VesselSituation.flying",
                        orbit=SimpleNamespace(body=SimpleNamespace(reference_frame="b", surface_gravity=9.81)),
                        flight=lambda ref=None: SimpleNamespace(speed=speed, surface_altitude=3000.0),
                        parts=SimpleNamespace(parachutes=[]))
    monkeypatch.setattr(ksp_actions, "_stage_dvs", lambda v: [{"dv": total, "twr_vac_kerbin": 2.0}])
    monkeypatch.setattr(ksp_actions, "_is_aircraft", lambda v: aircraft)
    return v


def test_enough_fuel_flag(monkeypatch):
    d = ksp_actions._dv_numbers(_dv_world(monkeypatch, 1000.0))
    assert d["landing_dv_needed_estimate"] == 196 and d["enough_fuel"] is True
    v = _dv_world(monkeypatch, 150.0)
    assert ksp_actions._dv_numbers(v)["enough_fuel"] is False
    assert ksp_actions._landing_check(v, False).startswith("enough_fuel: false.")
    assert ksp_actions._dv_numbers(_dv_world(monkeypatch, 1000.0, accel=5.0))["enough_fuel"] is False   # TWR < 1
    d = ksp_actions._dv_numbers(_dv_world(monkeypatch, 19766.0, aircraft=True))
    assert d["enough_fuel"] is False and "aircraft" in d["note"]