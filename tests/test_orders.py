"""Offline: captain's orders batch 1 (orders.parse + the ksp_actions order tools + captain_order) with a mocked kRPC
vessel. No KSP, no network."""
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, ksp_actions, orders, speedcap, takeoff as tko  # noqa: E402


@pytest.mark.parametrize("text,want", [
    ("turn left 30", ("turn", {"direction": "left", "degrees": 30.0})),
    ("Turn right", ("turn", {"direction": "right", "degrees": 90.0})),
    ("turn right by 45 degrees", ("turn", {"direction": "right", "degrees": 45.0})),
    ("heading 270", ("set_heading", {"heading": 270.0})), ("fly heading 090", ("set_heading", {"heading": 90.0})),
    ("fly to KSC", ("fly_to_place", {"name": "KSC"})), ("fly to the island", ("fly_to_place", {"name": "island"})),
    ("go to Mun Arch", ("fly_to_place", {"name": "Mun Arch"})),
    ("return to base", ("fly_to_place", {"name": "KSC"})), ("RTB", ("fly_to_place", {"name": "KSC"})),
    ("circle here", ("circle_here", {"direction": "left"})), ("circle right", ("circle_here", {"direction": "right"})),
    ("report", ("flight_report", {})), ("sitrep", ("flight_report", {})),
    ("fuel check", ("fuel_check", {})), ("how much fuel left?", ("fuel_check", {})),
    ("how far to KSC?", ("how_far", {"name": "KSC"})), ("how far is it to Island Airfield", ("how_far", {"name": "Island Airfield"})),
    ("time to target", ("time_to_target", {})), ("ETA?", ("time_to_target", {})),
    ("level off", ("level_off", {})), ("wings level", ("level_off", {})),
    ("stage", ("stage", {})), ("SAS off", ("set_sas", {"enabled": False})),
    ("hold prograde", ("set_sas_mode", {"mode": "prograde"})), ("sas retrograde", ("set_sas_mode", {"mode": "retrograde"})),
    ("RCS on", ("set_rcs", {"on": True})), ("lights off", ("set_lights", {"on": False})),
    ("AG 3", ("action_group", {"group": 3, "state": "toggle"})),
    ("action group 10 on", ("action_group", {"group": 10, "state": "on"})),
    # complex / not orders: left to the model
    ("AG 11", None), ("fly to Runway 27 at 6000 m and 300 m/s", None), ("turn left heading 270", None),
    ("fly to KSC and land", None), ("give me a report on the Mun", None), ("level off at 3000", None),
])
def test_parse_orders(text, want):
    assert orders.parse(text) == want and chat.parse_direct(text) == want


def make_vessel(lat=-0.05, lon=-75.5, hdg=100.0, spd=150.0, alt=1500.0):
    flight = SimpleNamespace(mean_altitude=alt, surface_altitude=alt - 70, speed=spd, horizontal_speed=spd,
                             vertical_speed=0.0, heading=hdg, latitude=lat, longitude=lon, dynamic_pressure=0.0, drag=(0, 0, 0))
    jet = SimpleNamespace(propellant_names=["LiquidFuel", "IntakeAir"], max_thrust_at=lambda p: 39240.0, max_thrust=39240.0,
                          specific_impulse=4000.0, thrust=20000.0, active=True, available_thrust=40000.0)
    ctl = SimpleNamespace(rcs=False, lights=False, ags=[])
    ctl.set_action_group = lambda k, on: ctl.ags.append(("set", k, on))
    ctl.toggle_action_group = lambda k: ctl.ags.append(("toggle", k))
    amounts = {"LiquidFuel": 300.0, "Oxidizer": 0.0}
    maxes = {"LiquidFuel": 600.0, "Oxidizer": 0.0, "MonoPropellant": 0.0, "SolidFuel": 0.0, "IntakeAir": 2.0}
    return SimpleNamespace(situation="VesselSituation.flying", name="Test Jet", mass=10000.0, control=ctl,
                           surface_reference_frame="srf", reference_frame="vrf", crew=[],
                           parts=SimpleNamespace(wheels=[1], legs=[], engines=[jet], parachutes=[]),
                           resources=SimpleNamespace(max=lambda n: maxes.get(n, 0.0), amount=lambda n: amounts.get(n, 0.0)),
                           orbit=SimpleNamespace(body=SimpleNamespace(name="Kerbin", reference_frame="bref", surface_gravity=9.81,
                                                                      equatorial_radius=600000.0)),
                           flight=lambda ref=None: flight)


@pytest.fixture
def w(monkeypatch):
    calls, flown = [], []
    v = make_vessel()
    tko._CEIL_CACHE.clear()
    state = {"hold": True}
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions, "conn", lambda: SimpleNamespace(space_center=SimpleNamespace(active_vessel=v, target_vessel=None)))
    monkeypatch.setattr(ksp_actions.guard, "busy", lambda: None)
    monkeypatch.setattr(ksp_actions, "_nearest_runway_elev", lambda v: None)
    monkeypatch.setattr(ksp_actions.hold, "active", lambda: state["hold"])
    monkeypatch.setattr(ksp_actions.hold, "start", lambda: "started")
    monkeypatch.setattr(ksp_actions.hold, "stop", lambda: state.update(hold=False))
    monkeypatch.setattr(ksp_actions.time, "sleep", lambda s: None)
    monkeypatch.setattr(ksp_actions.plane, "active", lambda: False)
    monkeypatch.setattr(ksp_actions.hold, "set_targets", lambda **kw: calls.append(kw))
    monkeypatch.setattr(ksp_actions, "fly_to", lambda name, *a, **k: flown.append(name) or f"Flying to {name}.")
    for k in ("speed", "altitude"):
        speedcap.set_authority(False, k)
    return SimpleNamespace(v=v, calls=calls, flown=flown, state=state, mp=monkeypatch)


def test_turn_and_heading(w):
    s = chat.Session("ingame")
    assert s.send("turn left 30", "local")[0] == "Turning left 30 deg: heading 070." and w.calls[-1]["heading"] == 70.0
    assert w.calls[-1]["roll"] == "off"
    assert "capped at 175" in s.send("turn right 200", "local")[0] and w.calls[-1]["heading"] == 275.0
    assert s.send("heading 270", "local")[0].startswith("Holds updated: heading 270")
    w.state["hold"] = False
    assert s.send("turn left", "local")[0].startswith("Nothing is engaged")
    assert s.send("heading 90", "local")[0].startswith("Nothing is engaged")


def test_fly_to_place(w):
    assert ksp_actions.call_tool("fly_to_place", {"name": "KSC"}) == "Flying to Runway 09."   # west of KSC -> 09
    w.v.flight().longitude = -73.0
    assert ksp_actions.call_tool("fly_to_place", {"name": "base"}) == "Flying to Runway 27."  # east -> 27
    assert ksp_actions.call_tool("fly_to_place", {"name": "the island"}) == "Flying to Island Airfield."
    w.mp.setattr(ksp_actions.spots, "resolve", lambda sc, n: ("Mun Arch", {"mode": "V", "lat": -0.05, "lon": -75.0}))
    r = ksp_actions.call_tool("fly_to_place", {"name": "Mun Arch"})
    assert r.startswith("Heading 270 toward Mun Arch") and "not a landing" in r and w.calls[-1]["heading"] == pytest.approx(270, abs=1)


def test_circle_level_off(w):
    s = chat.Session("ingame")
    assert s.send("circle here", "local")[0].startswith("Circling left at 15 deg") and w.calls[-1]["roll"] == -15.0
    w.v.flight().speed = 120.0
    ksp_actions.call_tool("circle_here", {"direction": "right", "bank": 30})
    assert w.calls[-1]["roll"] == 20.0                                      # 20 max at low speed
    w.v.flight().speed = 300.0
    ksp_actions.call_tool("circle_here", {"direction": "right", "bank": 30})
    assert w.calls[-1]["roll"] == 15.0                                      # 15 above 250 m/s
    r, _ = s.send("level off", "local")
    kw = w.calls[-1]
    assert r.startswith("Leveling off at 1500 m, heading 100") and kw["altitude"] == 1500.0 and kw["altitude_ref"] == "msl"
    assert kw["heading"] == 100.0 and kw["roll"] == "off" and kw["pitch"] == "off"
    w.v.situation = "VesselSituation.landed"
    assert s.send("wings level", "local")[0].startswith("Level off is for a plane")


def test_reports(w):
    s = chat.Session("ingame")
    r = s.send("report", "local")[0]
    assert r.startswith("Alt 1500 m (1430 m above ground), 150 m/s, V/S +0, heading 100, fuel LF 50%, KSC ") and "Autopilot: holds" in r
    r = s.send("fuel check", "local")[0]
    assert "LF 50%" in r and "min" in r and "KSC is" in r, r                # 1500 kg / 0.51 kg/s -> ~49 min
    assert s.send("how far to KSC?", "local")[0].startswith("KSC: ") and "bearing 09" in s.send("how far to KSC", "local")[0]
    w.mp.setattr(ksp_actions.spots, "landing_eta", lambda c, quiet=False: {"active": True, "summary": "Touchdown in 2 min."})
    assert s.send("time to target", "local")[0] == "Touchdown in 2 min."
    w.mp.setattr(ksp_actions.spots, "landing_eta", lambda c, quiet=False: {})
    assert s.send("eta", "local")[0] == "No landing in progress and no KSP target set."


def test_switches_and_captain_order(w):
    s = chat.Session("ingame")
    assert s.send("RCS on", "local")[0] == "RCS on." and w.v.control.rcs is True
    assert s.send("lights on", "local")[0] == "Lights on." and w.v.control.lights is True
    s.send("AG 3", "local")
    s.send("action group 10 off", "local")
    assert w.v.control.ags == [("toggle", 3), ("set", 0, False)]
    assert ksp_actions.call_tool("captain_order", {"order": "turn right 45"}) == "Turning right 45 deg: heading 145."
    assert ksp_actions.call_tool("captain_order", {"order": "do a barrel roll"}).startswith("Not an order I know")
    names = {t["function"]["name"] for t in ksp_actions.tool_schemas()}
    assert "captain_order" in names and "turn" not in names and "flight_report" not in names   # model sees one tool

# ---------------- batch 2
@pytest.mark.parametrize("text,want", [
    ("engines on", ("set_engines", {"on": True})), ("engines off", ("set_engines", {"on": False})),
    ("cut engines", ("cut_engines", {})), ("kill all engines", ("cut_engines", {})),
    ("switch engine mode", ("engine_mode", {})), ("afterburner on", ("afterburner", {"on": True})),
    ("flaps 1", ("flaps", {"setting": "1"})), ("flaps up", ("flaps", {"setting": "up"})), ("flaps full", ("flaps", {"setting": "full"})),
    ("trim nose up 3", ("trim", {"direction": "up", "percent": 3.0})), ("trim down", ("trim", {"direction": "down", "percent": 5.0})),
    ("reset trim", ("trim", {"direction": "reset", "percent": 0.0})),
    ("land", ("land", {"where": ""})), ("land at KSC", ("land", {"where": "ksc"})), ("land the plane", ("land", {"where": ""})),
    ("land at island airfield", ("land", {"where": "island airfield"})),
    ("go around", ("go_around", {})), ("go-around!", ("go_around", {})),
    ("touch and go", ("touch_and_go", {})), ("touch-n-go", ("touch_and_go", {})),
    ("abort", ("abort_ag", {})), ("abort autopilot", ("abort", {})), ("stop all autopilots", ("abort", {})),
    ("crew report", ("crew_report", {})), ("who's aboard?", ("crew_report", {})),
    ("land at 3000 m", None), ("abort the mission now please", None), ("flaps 3", None),
])
def test_parse_orders_batch2(text, want):
    assert orders.parse(text) == want


@pytest.fixture
def w2(w):
    """+ engines with modes, crew, landing autopilot states; hold.stop really releases the holds."""
    v = w.v
    w.state["plane"] = False
    w.mp.setattr(ksp_actions.plane, "active", lambda: w.state["plane"])
    w.mp.setattr(ksp_actions.plane, "stop", lambda: w.state.update(plane=False))
    w.mp.setattr(ksp_actions.hold, "stop", lambda: w.state.update(hold=False))
    w.mp.setattr(ksp_actions, "_has_wings", lambda v: True)
    landed = []
    w.mp.setattr(ksp_actions, "land_plane", lambda **k: landed.append(("land_plane", k)) or "Autoland engaged.")
    w.mp.setattr(ksp_actions, "land_here", lambda **k: landed.append(("land_here", k)) or "Landing here.")
    w.mp.setattr(ksp_actions, "land_at_ksc", lambda **k: landed.append(("land_at_ksc", k)) or "Landing at KSC.")
    w.landed = landed
    v.control.throttle = 0.6
    v.control.abort = False
    eng = v.parts.engines[0]
    eng.has_modes = False
    rapier = SimpleNamespace(active=True, has_modes=True, mode="AirBreathing", modes={"AirBreathing": 1, "ClosedCycle": 2},
                             propellant_names=["LiquidFuel", "Oxidizer"], specific_impulse=300.0, thrust=0.0)
    rapier.toggle_mode = lambda: setattr(rapier, "mode", "ClosedCycle" if rapier.mode == "AirBreathing" else "AirBreathing")
    panther = SimpleNamespace(active=False, has_modes=True, mode="Dry", modes={"Dry": 1, "Wet": 2},
                              propellant_names=["LiquidFuel", "IntakeAir"], specific_impulse=4000.0, thrust=0.0)
    panther.toggle_mode = lambda: None
    v.parts.engines += [rapier, panther]
    v.crew = [SimpleNamespace(name="Sidry Kerman", trait="Pilot", courage=0.5, stupidity=0.8)]
    w.rapier, w.panther = rapier, panther
    return w


def test_cut_engines_needs_confirm(w2):
    w2.mp.setattr(ksp_actions, "pilot_name", lambda: None)  # plain replies (the voice has its own test)
    s = chat.Session("ingame")
    r, _ = s.send("cut engines", "local")
    assert r == "Cut all engines (throttle 0, every engine shut down, autopilots off)? We're flying - that makes us a glider. (yes/no)", r
    assert w2.v.control.throttle == 0.6 and speedcap.pending("ingame")["kind"] == "confirm"
    assert s.send("no", "local")[0] == "Cancelled - nothing done." and w2.v.control.throttle == 0.6
    s.send("engines off", "local")                                    # = cut engines, asks again
    r, tools = s.send("yes", "local")
    assert r == "Engines cut: throttle 0, 2 engine(s) shut down, autopilots off." and tools[0]["tool"] == "cut_engines"
    assert w2.v.control.throttle == 0.0 and not w2.rapier.active and w2.state["hold"] is False
    assert {"engaged": False} in w2.calls
    assert s.send("engines on", "local")[0] == "Engines on (3 activated)."
    # the model path: the tool answers needs_confirm; Luke's yes runs it
    assert ksp_actions.call_tool("cut_engines", {}).startswith("needs_confirm: Cut all engines")
    assert s.send("confirm cut", "local")[0].startswith("Engines cut")


def test_abort_needs_confirm(w2):
    w2.mp.setattr(ksp_actions, "pilot_name", lambda: None)  # plain replies (the voice has its own test)
    aborted = []
    w2.mp.setattr(ksp_actions, "abort", lambda: aborted.append(1) or "Aborted: autopilots off.")
    w2.mp.setitem(ksp_actions.BY_NAME, "abort", ksp_actions.abort)
    s = chat.Session("ingame")
    assert s.send("abort", "local")[0].startswith("Fire the ABORT action group") and not aborted
    assert s.send("confirm abort", "local")[0] == "ABORT action group fired. Aborted: autopilots off."
    assert aborted and w2.v.control.abort is True
    assert s.send("abort autopilot", "local")[0] == "Aborted: autopilots off."    # the safe abort, no question


def test_engine_modes_flaps_trim(w2, monkeypatch):
    w2.mp.setattr(ksp_actions, "pilot_name", lambda: None)  # plain replies (the voice has its own test)
    assert ksp_actions.call_tool("engine_mode", {}) == "Engine mode switched on 2 engine(s): now ClosedCycle, Dry."
    assert ksp_actions.call_tool("afterburner", {"on": True}) == "Afterburner on (1 engine(s))." and w2.panther.mode == "Wet"
    assert ksp_actions.call_tool("afterburner", {"on": True}) == "Afterburner on (1 engine(s), already there)."
    w2.v.parts.engines[:] = w2.v.parts.engines[:1]
    assert ksp_actions.call_tool("afterburner", {"on": False}).startswith("No afterburning engines")
    assert ksp_actions.call_tool("engine_mode", {}).startswith("No multi-mode engines")
    s = chat.Session("ingame")
    assert s.send("flaps 2", "local")[0] == "Flaps 2 (AG 5 on, AG 6 on)."
    assert s.send("flaps up", "local")[0] == "Flaps up (AG 5 off, AG 6 off)."
    assert w2.v.control.ags == [("set", 5, True), ("set", 6, True), ("set", 5, False), ("set", 6, False)]
    monkeypatch.setattr(ksp_actions.settings, "get", lambda k, d=None: {"1": 7, "2": 10} if k == "flaps_action_groups" else d)
    assert ksp_actions.call_tool("flaps", {"setting": "1"}) == "Flaps 1 (AG 7 on, AG 10 off)." and w2.v.control.ags[-1] == ("set", 0, False)
    assert s.send("trim up", "local")[0].startswith("This kRPC version has no trim")
    w2.v.control.pitch_trim = 0.0
    assert s.send("trim nose up 10", "local")[0].startswith("Pitch trim +10% (the autopilot flies pitch")
    assert s.send("trim down 30", "local")[0].startswith("Pitch trim -20%") and w2.v.control.pitch_trim == pytest.approx(-0.2)
    assert s.send("reset trim", "local")[0].startswith("Pitch trim +0%")


def test_land_go_around_touch_and_go(w2):
    w2.mp.setattr(ksp_actions, "pilot_name", lambda: None)  # plain replies (the voice has its own test)
    s = chat.Session("ingame")
    assert s.send("land", "local")[0] == "Autoland engaged." and w2.landed[-1] == ("land_plane", {})
    assert w2.state["hold"] is False                                  # the holds handed the craft over
    assert s.send("land at island", "local")[0] == "Flying to Island Airfield."
    assert s.send("touch and go", "local")[0] == "Autoland engaged." and w2.landed[-1] == ("land_plane", {"touch_and_go": 1})
    w2.state["plane"] = True
    assert s.send("land", "local")[0] == "A plane landing is already in progress."
    assert s.send("touch and go", "local")[0].startswith("A landing is already running")
    ksp_actions.plane.STATUS["runway_heading"] = 270.0
    r, _ = s.send("go around", "local")
    assert r.startswith("Going around: approach dropped, climbing to 600 m above the field on heading 270"), r
    assert w2.state["plane"] is False and w2.calls[-1]["altitude"] == 600.0 and w2.calls[-1]["heading"] == 270.0
    assert s.send("go around", "local")[0] == "No landing in progress to go around from."
    w2.v.parts.wheels = []                                            # a rocket: land = land_here, at KSC = land_at_ksc
    s.send("land", "local")
    s.send("land at KSC", "local")
    assert [x[0] for x in w2.landed[-2:]] == ["land_here", "land_at_ksc"]


def test_crew_report(w2):
    assert ksp_actions.call_tool("crew_report", {}) == "Crew (1): Sidry Kerman (Pilot, courage 50%, stupidity 80%)."
    w2.v.crew = []
    assert chat.Session("ingame").send("crew report", "local")[0] == "No crew aboard (probe-controlled)."