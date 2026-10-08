"""Offline: direct flight commands (chat.parse_direct -> plane_pitch / set_throttle / set_gear / set_brakes /
deploy_parachutes / eject_kerbal), the hold's commanded-pitch level-off cycle (hold.PitchCycle) and the tools with a
mocked kRPC vessel. No KSP, no network."""
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, hold, ksp_actions, speedcap, takeoff as tko  # noqa: E402


# ---------------- parsing
@pytest.mark.parametrize("text,want", [
    ("pitch up 10", ("up", 10.0)), ("pitchup 90", ("up", 90.0)), ("Pitch Down 5", ("down", 5.0)),
    ("nose up", ("up", 5.0)), ("nose down 12.5 deg", ("down", 12.5)), ("pitch up to 20\u00b0", ("up", 20.0)),
    ("pitch up 95", ("up", 95.0)), ("pitch up the nose please", None), ("set altitude 7200", None),
])
def test_parse_pitch(text, want):
    assert chat.parse_pitch(text) == want


@pytest.mark.parametrize("text,want", [
    ("throttle 60", ("set_throttle", {"value": 0.6})), ("throttle to 60%", ("set_throttle", {"value": 0.6})),
    ("throttle max", ("set_throttle", {"value": 1.0})), ("Throttle idle", ("set_throttle", {"value": 0.0})),
    ("throttle auto", ("set_throttle", {"value": -1.0})), ("throttle 150", ("set_throttle", {"value": 1.0})),
    ("gear down", ("set_gear", {"down": True})), ("wheels up", ("set_gear", {"down": False})),
    ("airbrake on", ("set_brakes", {"on": True})), ("air brakes off", ("set_brakes", {"on": False})),
    ("brakes on", ("set_brakes", {"on": True})), ("deploy chutes", ("deploy_parachutes", {})),
    ("chutes", ("deploy_parachutes", {})), ("deploy the parachutes!", ("deploy_parachutes", {})),
    ("eject", ("eject_kerbal", {})), ("eject kerbal", ("eject_kerbal", {})),
    ("pitch down 3", ("plane_pitch", {"degrees": 3.0, "direction": "down"})),
    ("throttle up a bit", None), ("put the gear down when we're close", None), ("what are chutes for?", None),
])
def test_parse_direct(text, want):
    assert chat.parse_direct(text) == want


# ---------------- PitchCycle (the hold's commanded-pitch logic)
def test_pitch_cycle_levels_off_and_resumes():
    c = hold.PitchCycle(80.0)                                          # level-off below 80, resume at 100 m/s
    assert c.step(10, 120, 3, 1000, 10) == (10.0, True, None)
    assert c.step(10, 79, 3, 1100, 5) == (hold.LEVEL_PITCH, True, "level")
    assert c.step(10, 95, 3, 1100, 0) == (hold.LEVEL_PITCH, True, None)  # still rebuilding, full power
    assert c.step(10, 101, 3, 1100, 0) == (10.0, True, "resume")
    assert c.step(90, 120, 16, 1500, 40)[2] == "level"                # AoA past 15 also levels off
    assert c.step(90, 120, 5, 1500, 0) == (90.0, True, "resume")      # ...and the cycle repeats


def test_pitch_cycle_ends_at_target_ceiling_terrain():
    c = hold.PitchCycle(80.0)
    assert c.step(10, 120, 3, 2990, 0, alt_stop=3000)[0] == 10.0
    assert c.step(10, 120, 3, 3000, 0, alt_stop=3000) == (None, False, "target")
    assert c.step(10, 120, 3, 10600, 20, ceiling=10700) == (None, False, "ceiling")   # 5 s lead of V/S
    assert c.step(-10, 150, 0, 2000, -30, floor_msl=1800) == (None, False, "terrain")
    assert c.step(-10, 150, 0, 2000, -30, alt_stop=1000)[0] == -10.0
    assert c.step(-10, 150, 0, 1100, -30, alt_stop=1000) == (None, False, "target")
    assert c.step(-90, 200, 0, 5000, -200, floor_msl=300)[2] == "terrain"     # steep dive: long pull-out lead
    assert c.step(0, 100, 0, 250, 0, floor_msl=300)[2] == "terrain"           # level attitude under the floor
    assert c.step(0, 100, 0, 900, 0, floor_msl=300)[0] == 0.0


def test_pitch_cycle_nose_down_overspeed_and_level():
    c = hold.PitchCycle(80.0)
    assert c.step(-10, 250, 0, 5000, -40, eas=230, cap=220) == (hold.LEVEL_PITCH, False, "level_fast")
    assert c.step(-10, 230, 0, 5000, -5, eas=195, cap=220) == (-10.0, False, "resume")
    assert c.step(0, 100, 0, 5000, 0) == (0.0, False, None)


# ---------------- tools with a mocked vessel
@pytest.fixture
def fake(monkeypatch):
    """A flying jet at 500 m (safe ceiling 11 km); records hold.set_targets."""
    calls = []
    flight = SimpleNamespace(mean_altitude=500.0, vertical_speed=-3.0)
    jet = SimpleNamespace(propellant_names=["LiquidFuel", "IntakeAir"], max_thrust_at=lambda p: 39240.0, max_thrust=39240.0)
    mods = {"ModuleAeroSurface": [1, 2], "RealChuteModule": []}
    v = SimpleNamespace(situation="VesselSituation.flying", name="Test Jet", mass=10000.0,
                        control=SimpleNamespace(throttle=0.5, gear=False, brakes=False),
                        crew=[SimpleNamespace(name="Sidry Kerman")],
                        parts=SimpleNamespace(wheels=[1], legs=[], engines=[jet], parachutes=[],
                                              modules_with_name=lambda n: mods.get(n, [])),
                        resources=SimpleNamespace(max=lambda n: 2.0),
                        orbit=SimpleNamespace(body=SimpleNamespace(reference_frame="bref", surface_gravity=9.81)),
                        flight=lambda ref=None: flight)
    tko._CEIL_CACHE.clear()
    state = {"hold": True, "plane": False}
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions.guard, "busy", lambda: None)
    monkeypatch.setattr(ksp_actions, "_nearest_runway_elev", lambda v: None)
    monkeypatch.setattr(ksp_actions.hold, "active", lambda: state["hold"])
    monkeypatch.setattr(ksp_actions.plane, "active", lambda: state["plane"])
    monkeypatch.setattr(ksp_actions.hold, "set_targets", lambda **kw: calls.append(kw))
    for k in ("speed", "altitude"):
        speedcap.set_authority(False, k)
    return SimpleNamespace(v=v, calls=calls, state=state, mods=mods)


def test_plane_pitch_tool(fake):
    r = ksp_actions.call_tool("plane_pitch", {"degrees": 10})
    assert r.startswith("Pitch hold: 10 deg nose up") and "11000 m" in r, r
    kw = fake.calls[-1]
    assert kw["pitch"] == 10.0 and kw["pitch_ceiling"] == 11000.0 and kw["throttle"] == "off" and kw["pitch_t"] > 0
    assert ksp_actions.call_tool("plane_pitch", {"degrees": 5, "direction": "down"}).startswith("Pitch hold: 5 deg nose down")
    assert fake.calls[-1]["pitch"] == -5.0 and fake.calls[-1]["pitch_ceiling"] == "off"
    assert ksp_actions.call_tool("plane_pitch", {"degrees": -7})[:12] == "Pitch hold: " and fake.calls[-1]["pitch"] == -7.0
    n = len(fake.calls)
    assert "0-90" in ksp_actions.call_tool("plane_pitch", {"degrees": 95}) and len(fake.calls) == n


def test_plane_pitch_nothing_engaged(fake):
    fake.state["hold"] = False
    assert ksp_actions.call_tool("plane_pitch", {"degrees": 10}).startswith("Nothing is engaged")
    fake.state["plane"] = True
    assert "landing / fly-to autopilot" in ksp_actions.call_tool("plane_pitch", {"degrees": 10})
    assert fake.calls == []


def test_chat_direct_pitch_skips_the_model(fake):
    s = chat.Session("ingame")
    reply, tools = s.send("pitchup 90", "local")                      # no backend / network involved
    assert reply.startswith("Pitch hold: 90 deg nose up") and tools[0]["tool"] == "plane_pitch"
    assert fake.calls[-1]["pitch"] == 90.0 and s.last_name == "Sidry" and s.history[-1]["content"] == reply
    fake.state["hold"] = False
    assert s.send("nose down", "local")[0].startswith("Nothing is engaged")


def test_altitude_or_speed_command_ends_pitch_and_manual_throttle(fake):
    assert ksp_actions.call_tool("plane_hold", {"altitude_m": 3000}).startswith("Holds updated")
    kw = fake.calls[-1]
    assert kw["pitch"] == "off" and kw["pitch_ceiling"] == "off" and kw["throttle"] == "off"
    ksp_actions.call_tool("plane_hold", {"speed": 150})
    assert fake.calls[-1]["throttle"] == "off" and "pitch" not in fake.calls[-1]
    ksp_actions.call_tool("plane_hold", {"heading": 90})
    assert "pitch" not in fake.calls[-1] and "throttle" not in fake.calls[-1]
    ksp_actions.call_tool("plane_hold", {"off": "pitch"})
    assert fake.calls[-1]["pitch"] == "off"


def test_throttle(fake):
    s = chat.Session("ingame")
    r, _ = s.send("throttle 60", "local")                             # holds flying: manual override
    assert "manual override" in r and fake.calls[-1] == {"throttle": 0.6} and fake.v.control.throttle == 0.5
    assert s.send("throttle auto", "local")[0].startswith("Throttle handed back") and fake.calls[-1] == {"throttle": "off"}
    fake.state["hold"] = False
    r, _ = s.send("throttle idle", "local")                           # in flight never 0
    assert fake.v.control.throttle == 0.05 and "never 0 in flight" in r
    s.send("throttle max", "local")
    assert fake.v.control.throttle == 1.0
    fake.v.situation = "VesselSituation.orbiting"
    s.send("throttle idle", "local")
    assert fake.v.control.throttle == 0.0                             # out of the air 0 is fine
    fake.state["plane"] = True
    assert "abort it first" in s.send("throttle 50", "local")[0] and fake.v.control.throttle == 0.0


def test_gear_brakes_chutes_eject(fake):
    s = chat.Session("ingame")
    assert s.send("gear down", "local")[0] == "Gear down, Captain." and fake.v.control.gear is True
    assert s.send("wheels up", "local")[0] == "Gear up, Captain." and fake.v.control.gear is False
    r, _ = s.send("airbrake on", "local")
    assert fake.v.control.brakes is True and "2 airbrake(s)" in r
    fake.mods["ModuleAeroSurface"] = []
    r, _ = s.send("brakes off", "local")
    assert fake.v.control.brakes is False and "no airbrake parts" in r
    assert s.send("deploy chutes", "local")[0] == "No parachutes on this craft."
    fake.v.parts.parachutes = [SimpleNamespace(deploy=lambda: None)]
    assert "1 parachute(s)" in s.send("chutes", "local")[0]
    assert "Can't eject Sidry Kerman yet" in s.send("eject", "local")[0]
    fake.v.crew = []
    assert s.send("eject kerbal", "local")[0] == "Nobody aboard to eject (no crew)."
    fake.v.situation = "VesselSituation.landed"
    assert s.send("gear up", "local")[0].startswith("Not raising") and fake.v.control.gear is False


def test_new_tools_are_model_tools():
    names = {t["function"]["name"] for t in ksp_actions.tool_schemas()}
    assert {"plane_pitch", "set_throttle", "set_gear", "set_brakes", "deploy_parachutes", "eject_kerbal"} <= names


def test_pilot_voices_replies(fake, monkeypatch):
    """The kerbal at the controls speaks: Pilot trait first, else the first crew member; cached; Bridge without crew."""
    fake.v.crew = [SimpleNamespace(name="Bob Kerman", trait="Scientist"), SimpleNamespace(name="Sidry Kerman", trait="Pilot")]
    s = chat.Session("ingame")
    assert s.send("gear down", "local")[0] == "Gear down, Captain." and s.last_name == "Sidry"
    fake.v.crew = [SimpleNamespace(name="Bob Kerman", trait="Scientist")]
    s.send("gear up", "local")
    assert s.last_name == "Sidry"                                    # cached for ~10 s
    ksp_actions._PILOT["t"] = 0.0
    s.send("gear up", "local")
    assert s.last_name == "Bob"                                      # no pilot: the first crew member
    fake.v.crew = []
    ksp_actions._PILOT["t"] = 0.0
    r, _ = s.send("gear down", "local")
    assert r == "Gear down." and s.last_name == "Bridge"
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: (_ for _ in ()).throw(RuntimeError("Not in flight")))
    ksp_actions._PILOT["t"] = 0.0
    assert ksp_actions.pilot_name() is None
    assert s.send("/override", "local") and s.last_name == "Bridge"   # bridge commands stay "Bridge"
