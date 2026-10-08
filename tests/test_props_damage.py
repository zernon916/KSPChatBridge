"""Offline (mocked kRPC, no live files): propeller planes count as propulsion; 'takeoff' / 'damage report' direct
orders; upside-down detection in flight and on the ground; lost-part names; the state-claim guard (Luke 2026-10-08:
a prop plane was told 'no engines', and after it flipped on landing the AI said 'Wheels are now down and ready!')."""
import logging
import sys
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, emergency as em, ksp_actions, maxspeed, orders, plane, propulsion, takeoff as tko  # noqa: E402


def prop_vessel(motor="Engaged", sit="landed", engines=()):
    rotor = NS(fields={"Motor": motor, "Current RPM": "460", "RPM Limit": "460"}, part=NS(title="EM-32 Rotor"))
    blades = [NS(part=NS(title="Propeller Blade Type A")) for _ in range(4)]
    eng = [NS(propellant_names=p, part=NS(title="x")) for p in engines]
    return NS(name="Prop Plane", situation=f"VesselSituation.{sit}", mass=4000.0, available_thrust=0.0,
              orbit=NS(body=NS(surface_gravity=9.81)),
              parts=NS(all=list(range(30)), engines=eng, control_surfaces=blades + [NS(part=NS(title="Elevon 1"))],
                       modules_with_name=lambda n: [rotor] if n == "ModuleRoboticServoRotor" else []))


def setup_function(_):
    propulsion._CACHE.clear()


def test_propulsion_classification():
    assert propulsion.engine_kind(["IntakeAir", "LiquidFuel"]) == "jet"
    assert propulsion.engine_kind(["ElectricCharge"]) == "electric"
    assert propulsion.engine_kind(["LiquidFuel", "Oxidizer"]) == "rocket"
    pk = propulsion.classify(prop_vessel())
    assert pk["props"] and pk["any"] and pk["rotors"] == 1 and pk["blades"] == 4 and pk["rockets"] == 0
    assert propulsion.describe(pk) == "1 propeller rotor"
    propulsion._CACHE.clear()
    assert propulsion.classify(prop_vessel(engines=[["ElectricCharge"]]))["electric"] == 1
    assert not propulsion.classify(NS())["props"]                       # kRPC can't tell: no props, no crash
    st = propulsion.rotor_status(prop_vessel(motor="Disengaged"))
    assert st == [("EM-32 Rotor", "Disengaged", "460")] and propulsion.motor_off(st)
    assert not propulsion.motor_off(propulsion.rotor_status(prop_vessel()))


def test_prop_plane_is_not_no_engines_and_takeoff_is_not_an_ascent():
    msg = ksp_actions._orbit_capability_check(prop_vessel())
    assert "propeller-driven" in msg and "no engines" not in msg and "take off" in msg
    propulsion._CACHE.clear()
    e = prop_vessel(engines=[["ElectricCharge"]])
    e.parts.modules_with_name = lambda n: []
    assert "propeller-driven" in ksp_actions._orbit_capability_check(e)  # electric prop is not a rocket


def test_takeoff_tool_for_prop_plane(monkeypatch):
    calls = []
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: prop_vessel(motor="Disengaged"))
    monkeypatch.setattr(ksp_actions, "_planeish", lambda v: True)
    monkeypatch.setattr(ksp_actions, "plane_hold", lambda **k: calls.append(k) or "Holds engaged: taking off.")
    r = ksp_actions.takeoff()
    assert calls == [{"altitude_m": 300.0, "altitude_ref": "agl"}]
    assert "motor on, RPM and torque max" in r and "roll acceleration governs the throttle" in r
    monkeypatch.setattr(ksp_actions, "_planeish", lambda v: False)
    assert "For a rocket say 'launch'" in ksp_actions.takeoff()
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: prop_vessel(sit="flying"))
    assert ksp_actions.takeoff() == "We're already airborne."


def test_direct_orders():
    for s in ("take off", "Takeoff!", "TAKE-OFF", "takeoff now"):
        assert orders.parse(s) == ("takeoff", {}), s
    assert orders.parse("takeoff and set altitude to 2000") is None   # complex: the model handles it
    for s in ("damage report", "Damage check?", "what are we missing?", "what did we lose", "any damage?"):
        assert orders.parse(s) == ("damage_report", {}), s
    assert "takeoff" in ksp_actions.BY_NAME and "damage_report" in ksp_actions.BY_NAME


def test_props_ceiling_maxspeed_and_takeoff_power(caplog):
    assert tko.ceiling_from_specs(0.0, 0, 0, True, props=True) == tko.CEIL_PROP
    assert tko.ceiling_from_specs(0.0, 0, 0) == tko.CEIL_MIN
    # level (vs 0), speeding up 0.5 m/s over 0.5 s (1 m/s^2), 4 t, drag 2 kN, 60 % throttle -> (2000 + 4000) / 0.6
    assert abs(maxspeed.prop_thrust(2000.0, 4000.0, 60.0, 60.5, 0.5, 0.0, 9.81, 0.6) - 6000.0 / 0.6) < 1e-6
    assert maxspeed.prop_thrust(2000.0, 4000.0, 0.0, 1.0, 0.5, 0.0, 9.81, 1.0) == 0.0
    with caplog.at_level(logging.INFO, logger="kspchat"):
        gov = plane._takeoff_governor(prop_vessel(), None, 0.0, {}, "hold")
    assert gov.cmd == 1.0 and "props: thrust unreadable" in caplog.text


# ---- upside down ----
def S(t, **kw):
    s = {"t": t, "vid": 1, "sit": "flying", "throttle": 0.8, "parts": 20, "stage": 0, "g": 1.0, "crew": 1,
         "ctrl": True, "speed": 120.0, "vs": 0.0, "aoa": 2.0, "engines": [], "fuel": None, "plane": True,
         "roll": 0.0, "pitch": 2.0}
    s.update(kw)
    return s


def test_inverted_in_flight_mayday_and_upright_recovery(monkeypatch):
    monkeypatch.setattr(em, "_engaged", lambda: True)
    d = em.Detector()
    d.tick(S(0))
    assert d.tick(S(1, roll=170.0)) == [] and d.tick(S(2, roll=165.0)) == []
    evs = d.tick(S(3, roll=-175.0))
    assert [(e["kind"], e["start"], e["actions"]) for e in evs] == [("upside_down", True, ["upright"])]
    said = em.handle(evs, None, S(3, who="Sidry"), post=lambda x: None)
    assert said[0].startswith("Sidry: ") and "rolling upright the shortest way" in said[0]
    assert em.shape("pitch", 0.6) == 0.0 and em.shape("roll", 0.5) == 0.5 and em.bank_cap() == em.LEVEL_BANK
    assert em.shape("throttle", 0.8) == 0.8                         # never cuts the throttle for this
    assert "INVERTED" in em.alerts_text()
    assert d.tick(S(4, roll=40.0)) == []
    end = d.tick(S(5, roll=10.0))
    assert [(e["kind"], e["start"]) for e in end] == [("upside_down", False)]
    em.handle(end, None, S(5), post=lambda x: None)
    assert em.shape("pitch", 0.6) == 0.6 and "INVERTED" not in em.alerts_text()


def test_inverted_ignored_for_rockets_and_vertical_noses():
    d = em.Detector()
    d.tick(S(0, plane=False))
    assert all(d.tick(S(t, plane=False, roll=180.0)) == [] for t in range(1, 6))     # MechJeb ascent rolls freely
    d = em.Detector()
    d.tick(S(0))
    assert all(d.tick(S(t, roll=170.0, pitch=80.0)) == [] for t in range(1, 6))      # roll meaningless near vertical
    d = em.Detector()
    d.tick(S(0, sit="pre_launch", up=0.0, roll=170.0, pitch=90.0))
    assert all(d.tick(S(t, sit="pre_launch", up=0.0, roll=170.0, pitch=90.0)) == [] for t in range(1, 6))


def test_upside_down_on_the_ground_after_landing(monkeypatch):
    monkeypatch.setattr(em, "running", lambda: True)
    d = em.DET  # build_systems reads the live detector's active set
    d.tick(S(0, sit="landed", up=0.98, roll=0.0))
    d.tick(S(1, sit="landed", up=-0.95, roll=178.0))
    d.tick(S(2, sit="landed", up=-0.95, roll=178.0))
    evs = d.tick(S(3, sit="landed", up=-0.95, roll=178.0))
    assert [(e["kind"], e["start"]) for e in evs] == [("upside_down_ground", True)]
    line = em.handle(evs, None, S(3, who="Sidry", sit="landed"), post=lambda x: None)[0]
    assert line.split(": ", 1)[1] in em.LINES["upside_down_ground"]
    rows = em.build_systems(S(3, sit="landed", roll=178.0), None, None)
    assert ("fail", "Attitude", "INVERTED (on the ground)") in rows
    assert "UPSIDE DOWN (on the ground)" in em.damage_facts() and "Parts lost this flight: none detected" in em.damage_facts()
    assert "on our roof" in em.damage_text()
    assert em.gear_note() == ""                                      # nothing lost: don't assume a missing wheel


# ---- parts: names, EVA from a seat isn't a lost part ----
def test_lost_part_names_and_eva_seat(monkeypatch):
    monkeypatch.setattr(em, "running", lambda: True)
    d = em.Detector()
    d.tick(S(0))
    evs = d.tick(S(10, parts=19, lost=["left main wheel (LY-10 Small Landing Gear)"]))
    assert evs[0]["kind"] == "parts" and evs[0]["what"] == "left main wheel (LY-10 Small Landing Gear)"
    assert evs[0]["short"].startswith("PARTS LOST: left main wheel")
    em.handle(evs, NS(name="Prop Plane"), S(10), post=lambda x: None)
    assert "left main wheel (LY-10 Small Landing Gear)" in em.damage_text() and "landing will be rough" in em.damage_text()
    assert "left main wheel" in em.gear_note()
    d = em.Detector()
    d.tick(S(0))
    assert d.tick(S(10, parts=19, crew=0, lost=["Sidry Kerman"])) == []   # kerbal left an external seat


def test_part_tracker_labels():
    class P:
        def __init__(self, oid, title, pos=(0.0, 0.0, 0.0)):
            self._object_id, self.title, self._pos = oid, title, pos

        def position(self, rf):
            return self._pos
    lw, rw, nw, wing = P(1, "LY-10", (-1.2, -0.5, 1.0)), P(2, "LY-10", (1.2, -0.5, 1.0)), P(3, "LY-05", (0, 3.0, 1)), P(4, "Wing")
    v = NS(reference_frame=None, parts=NS(wheels=[NS(part=lw), NS(part=rw), NS(part=nw)], legs=[]))
    tr = em.PartTracker()
    assert tr.update(7, [lw, rw, nw, wing], v) == []
    assert tr.update(7, [rw, nw, wing], v) == ["left main wheel (LY-10)"]
    assert tr.update(7, [rw, wing], v) == ["nose wheel (LY-05)"]
    assert tr.update(8, [wing], v) == []                              # vessel switch: new baseline


# ---- state-claim guard ----
def test_overclaim_rules():
    gear = [{"tool": "set_gear", "result": "Gear down."}]
    assert chat.overclaim("Wheels are now down and ready! The aircraft is equipped for takeoff or landing.", gear)
    assert chat.overclaim("Gear down. Ready for your next command.", gear) == ""
    assert chat.overclaim("The wheel is fixed.", []) == "fixed"
    assert chat.overclaim("Everything is fine.", gear) == ""                 # no damage on record: plain wording ok
    assert chat.overclaim("Everything is fine.", gear, damaged=True) == "Everything is fine"
    assert chat.overclaim("Ready for takeoff.", [{"tool": "x", "result": "Ready for takeoff."}]) == ""


def test_gear_reply_overclaim_is_reprompted_with_watcher_facts(monkeypatch):
    monkeypatch.setattr(em, "running", lambda: True)
    em.DAMAGE["inverted"] = "on the ground"
    sent, seq = [], [
        {"content": "", "tool_calls": [{"id": "1", "function": {"name": "set_gear", "arguments": "{\"down\": true}"}}]},
        {"content": "Wheels are now down and ready! The aircraft is equipped for takeoff or landing."},
        {"content": "Gear is down, but we're upside down on the ground - no takeoff from here."}]
    monkeypatch.setattr(chat.backends, "resolve", lambda b, m=None: ("http://x", None, "m"))
    monkeypatch.setattr(chat.memory, "log_turn", lambda *a, **k: None)
    monkeypatch.setattr(chat.ksp_actions, "call_tool", lambda n, a=None: "Gear down.")

    def post(url, payload, key=None, timeout=300):
        sent.append(payload)
        return {"choices": [{"message": seq.pop(0)}]}
    monkeypatch.setattr(chat, "_post", post)
    reply, tools = chat.Session("dmg").send("um, we are missing a wheel", "local")
    assert reply == "Gear is down, but we're upside down on the ground - no takeoff from here."
    assert "UPSIDE DOWN" in sent[0]["messages"][0]["content"]          # watcher facts in the system prompt
    nudge = sent[2]["messages"][-1]["content"]
    assert "and ready" in nudge and "set_gear: Gear down." in nudge and "none detected" in nudge


def test_unfixed_overclaim_is_flagged(monkeypatch):
    monkeypatch.setattr(chat.backends, "resolve", lambda b, m=None: ("http://x", None, "m"))
    monkeypatch.setattr(chat.memory, "log_turn", lambda *a, **k: None)
    seq = ["All systems go, we're good to go!", "All good, ready for takeoff!"]
    monkeypatch.setattr(chat, "_post", lambda *a, **k: {"choices": [{"message": {"content": seq.pop(0)}}]})
    reply, _ = chat.Session("oc").send("how are we doing", "local")
    assert "(Bridge: '" in reply and "is not confirmed by any tool" in reply