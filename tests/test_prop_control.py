"""Offline: Breaking Ground prop control (rotor RPM / torque / motor, blade pitch schedule from the inflow angle,
reverse/beta on the ground, side groups, one-prop-out MAYDAY). Mocked kRPC modules; no live files."""
import math
import sys
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import emergency as em, ksp_actions, orders, propulsion as pr  # noqa: E402


class Part:
    _n = 0

    def __init__(self, title, pos):
        Part._n += 1
        self._object_id, self.title, self._pos = Part._n, title, pos

    def position(self, rf):
        return self._pos


class Mod:
    def __init__(self, part, fields, actions=(), events=()):
        self.part, self._f, self.actions, self.events = part, dict(fields), list(actions), list(events)
        from tests.rotor_fake import RotorApi
        part.robotic_rotor = RotorApi(self._f)
        self._object_id = part._object_id + 1000
        self.calls = []

    @property
    def fields(self):
        return dict(self._f)

    def set_field_float(self, k, v):
        self.calls.append(("float", k, v))
        self._f[k] = f"{v:g}"

    def set_field_string(self, k, v):
        self.calls.append(("str", k, v))
        self._f[k] = v

    def set_field_bool(self, k, v):
        self.calls.append(("bool", k, v))
        self._f[k] = "Engaged" if v else "Disengaged"

    def trigger_event(self, e):
        self.calls.append(("event", e))

    def set_action(self, a, v=True):
        self.calls.append(("action", a, v))


def rotor(x, direction="Clockwise", rpm="440", motor="Engaged"):
    return Mod(Part("EM-32 Standard Rotor", (x, 1.0, 0.0)),
               {"RPM Limit": "460", "Torque Limit(%)": "100", "Motor": motor, "Current RPM": rpm,
                "Rotation Direction": direction})


def blade(x, angle="12"):
    return Mod(Part("Propeller Blade Type A", (x + 0.9, 1.2, 0.0)), {"Deploy Angle": angle, "Authority Limiter": "100"})


def craft(rotors, blades, name="Twin Prop", sit="flying"):
    surfs = [NS(part=b.part, deployed=False) for b in blades]
    return NS(name=name, situation=f"VesselSituation.{sit}", reference_frame=None, control=NS(throttle=0.5),
              parts=NS(all=list(range(20 + len(blades))), engines=[], control_surfaces=surfs,
                       modules_with_name=lambda n: rotors if n == "ModuleRoboticServoRotor" else
                       (blades if n == "ModuleControlSurface" else [])))


def setup_function(_):
    pr._CACHE.clear()
    for k in ("logged",):
        pr.STATE[k] = set()
    for k in ("sign", "radius", "layout"):
        pr.STATE[k] = {}
    pr.STATE.update(manual_pitch=None, manual_torque=False, reverse=False, diff=None)
    pr.GOV.__init__()


def twin(**kw):
    rl, rr = rotor(-3.0, "Clockwise"), rotor(3.0, "Counterclockwise")
    bl, br = [blade(-3.0), blade(-3.0)], [blade(3.0), blade(3.0)]
    return craft([rl, rr], bl + br, **kw), rl, rr, bl, br


def test_helpers_and_blade_math():
    f = {"RPM Limit": "460", "Torque Limit(%)": "80", "Motor": "Engaged", "Current RPM": "123.4 "}
    assert pr.find_field(f, "rpm", "limit") == "RPM Limit" and pr.find_field(f, "torque") == "Torque Limit(%)"
    assert pr.num(f["Current RPM"]) == 123.4 and pr.num("n/a") is None
    assert pr.spin_dir({"Rotation Direction": "Counterclockwise"}) == -1 and pr.spin_dir({"x": "1"}) is None
    assert pr.blade_pitch_for(0.0, 460, 1.5) == pr.PITCH_MIN                       # fine pitch at standstill
    u = 460 * 2 * math.pi / 60 * 1.5
    assert math.isclose(pr.inflow_deg(60.0, 460, 1.5), math.degrees(math.atan(60.0 / u)))
    assert math.isclose(pr.blade_pitch_for(60.0, 460, 1.5), pr.inflow_deg(60.0, 460, 1.5) + pr.AOA_OPT)
    assert pr.blade_pitch_for(200.0, 100, 1.0) == pr.PITCH_MAX                     # clamped
    assert pr.REV_PITCH - pr.inflow_deg(30.0, 460, 1.5) < 0                         # beta: negative AoA = reverse


def test_layout_groups_and_counter_rotation():
    v, rl, rr, bl, br = twin()
    lay = pr.layout(v)
    assert [r["group"] for r in lay["rotors"]] == ["left", "right"] and pr.groups(v) == ["left", "right"]
    assert [b["group"] for b in lay["blades"]] == ["left", "left", "right", "right"]
    assert lay["counter_pairs"] == [("left", "right")]
    assert pr.side_of(0.2) == "center" and pr.side_of(-0.8) == "left"


def test_rotor_control_floors_and_groups():
    v, rl, rr, bl, br = twin()
    r = pr.set_rotor(v, rpm=9999, torque=0.0, motor=False, flying=True)
    assert "motor stays ON in flight" in r and ("float", "Torque Limit(%)", pr.FLIGHT_MIN_TORQUE) in rl.calls
    assert ("float", "RPM Limit", pr.RPM_MAX) in rr.calls
    assert not any(c[0] in ("bool", "event", "action") for c in rl.calls)           # never disengaged in flight
    pr.set_rotor(v, torque=50.0, group="left")
    assert ("float", "Torque Limit(%)", 50.0) in rl.calls and ("float", "Torque Limit(%)", 50.0) not in rr.calls
    assert pr.set_rotor(v, rpm=100, flying=True).startswith("RPM limit 115")          # 25 % floor
    off = rotor(0.0, motor="Disengaged")
    pr.set_rotor(craft([off], [], name="Mono"), motor=True)
    assert ("bool", "Motor", True) in off.calls


def test_blade_pitch_signs_reverse_and_flight_rules():
    v, rl, rr, bl, br = twin()
    br[0]._f["Deploy Angle"] = "-12"                     # this blade was built mirrored: keep its sign
    pr.set_blades(v, pitch=20.0, deploy=True)
    assert ("float", "Deploy Angle", 20.0) in bl[0].calls and ("float", "Deploy Angle", -20.0) in br[0].calls
    assert all(s.deployed for s in v.parts.control_surfaces)
    r = pr.set_blades(v, pitch=-15.0, flying=True)
    assert "no reverse pitch in flight" in r and ("float", "Deploy Angle", pr.FLIGHT_MIN_PITCH) in bl[1].calls
    assert pr.reverse(v, True, "flying") == (False, "props reverse refused: only on the ground, never in flight")
    ok, msg = pr.reverse(v, True, "landed")
    assert ok and pr.STATE["reverse"] and ("float", "Deploy Angle", pr.REV_PITCH) in bl[0].calls
    assert ("float", "Deploy Angle", -pr.REV_PITCH) in br[0].calls          # mirrored blade mirrored again
    assert "differential reverse (right)" in pr.diff_reverse(v, 8.0)
    assert ("float", "Torque Limit(%)", pr.REV_TORQUE * pr.DIFF_FRAC) in rl.calls
    assert pr.diff_reverse(v, 8.0) == ""                                    # unchanged: no spam
    ok, msg = pr.reverse(v, False, "landed")
    assert not pr.STATE["reverse"] and "props forward" in msg


def test_takeoff_setup_and_governor():
    v, rl, rr, bl, br = twin(sit="landed")
    msg = pr.takeoff_setup(v)
    assert "props set for takeoff" in msg and ("float", "RPM Limit", pr.RPM_MAX) in rl.calls
    assert ("float", "Deploy Angle", pr.PITCH_MIN) in bl[0].calls
    v.situation = "VesselSituation.flying"
    rl.calls.clear()
    did = pr.GOV.tick(v, {"speed": 60.0, "sit": "flying", "throttle": 0.0}, True, 100.0)
    assert "blade pitch" in did and "torque 10%" in did                    # schedule + throttle mirror (floored)
    assert any(c[1] == "Deploy Angle" and c[2] > 30 for c in bl[0].calls)      # inflow ~40 deg + AoA 8
    assert math.isclose(pr.blade_radius(v), math.dist((-3.0, 1.0, 0.0), (-2.1, 1.2, 0.0)) + pr.BLADE_HALF_SPAN)
    assert pr.GOV.tick(v, {"speed": 60.0, "sit": "flying", "throttle": 0.0}, True, 100.5) is None   # rate-limited
    assert pr.GOV.tick(v, {"speed": 60.0, "sit": "flying", "throttle": 1.0}, False, 200.0) is None  # not engaged
    pr.STATE["manual_pitch"] = 15.0
    did = pr.GOV.tick(v, {"speed": 90.0, "sit": "flying", "throttle": 1.0}, True, 300.0)
    assert did and "blade pitch" not in did and "torque 100%" in did


def test_prop_orders_with_groups():
    assert orders.parse("prop pitch 15") == ("prop_control", {"pitch": "15"})
    assert orders.parse("props pitch auto") == ("prop_control", {"pitch": "auto"})
    assert orders.parse("rpm max") == ("prop_control", {"rpm": "max"})
    assert orders.parse("torque 60%") == ("prop_control", {"torque": "60"})
    assert orders.parse("props off") == ("prop_control", {"motor": "off"})
    assert orders.parse("props reverse") == ("prop_control", {"reverse": "on"})
    assert orders.parse("props forward") == ("prop_control", {"reverse": "off"})
    assert orders.parse("left props pitch 15") == ("prop_control", {"pitch": "15", "group": "left"})
    assert orders.parse("right rpm max") == ("prop_control", {"rpm": "max", "group": "right"})
    assert orders.parse("left props reverse") == ("prop_control", {"reverse": "on", "group": "left"})
    assert orders.parse("centre torque 50") == ("prop_control", {"torque": "50", "group": "center"})
    assert orders.parse("turn left 30")[0] == "turn"


def test_prop_control_tool(monkeypatch):
    v, rl, rr, bl, br = twin()
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    assert "No center propellers" in ksp_actions.prop_control(torque="50", group="center")
    r = ksp_actions.prop_control(reverse="on")
    assert "refused" in r                                                  # in flight
    r = ksp_actions.prop_control(motor="off", group="left")
    assert "motor stays ON in flight" in r
    r = ksp_actions.prop_control(pitch="22", group="right")
    assert "right" in r and pr.STATE["manual_pitch"] == 22.0
    assert "Give a number" in ksp_actions.prop_control(rpm="lots")
    assert "Props:" in ksp_actions.set_throttle(0.0) and ("float", "Torque Limit(%)", pr.FLIGHT_MIN_TORQUE) in rl.calls


def S(t, props, **kw):
    s = {"t": t, "vid": 1, "sit": "flying", "throttle": 0.8, "parts": 20, "stage": 0, "g": 1.0, "crew": 1,
         "ctrl": True, "speed": 70.0, "vs": 0.0, "aoa": 2.0, "engines": [], "fuel": None, "props": props}
    s.update(kw)
    return s


def test_one_prop_out_mayday_and_yaw_counter(monkeypatch):
    monkeypatch.setattr(em, "_engaged", lambda: True)
    good = {"rpm": 440.0, "rpm_limit": 460.0, "motor_on": True}
    d = em.Detector()
    d.tick(S(0, {"left": good, "right": good}))
    d.tick(S(1, {"left": good, "right": good}))
    dead = {"rpm": 40.0, "rpm_limit": 460.0, "motor_on": True}
    assert d.tick(S(2, {"left": dead, "right": good})) == []
    evs = d.tick(S(4, {"left": dead, "right": good}))
    assert [(e["kind"], e["what"], e["actions"]) for e in evs] == [("prop_out", "left prop", ["level", "prop_yaw:left"])]
    said = em.handle(evs, None, S(4, {}, who="Sidry"), post=lambda x: None)
    assert "left prop" in said[0] and "rudder trim right" in said[0] and "LEFT PROP OUT" in em.alerts_text()
    assert em.shape("yaw", 0.0) == em.PROP_YAW_TRIM
    back = d.tick(S(6, {"left": good, "right": good}))
    assert [(e["kind"], e["start"]) for e in back] == [("prop_out", False)]
    em.handle(back, None, S(6, {}), post=lambda x: None)
    assert em.shape("yaw", 0.0) == 0.0
    # Luke turning one side's RPM limit down is not a failure; nor is a disengaged motor at idle throttle on the ground
    d2 = em.Detector()
    d2.tick(S(0, {"left": good, "right": good}))
    d2.tick(S(1, {"left": good, "right": good}))
    slow = {"rpm": 100.0, "rpm_limit": 100.0, "motor_on": True}
    assert all(d2.tick(S(t, {"left": slow, "right": good})) == [] for t in range(2, 8))
    off = {"rpm": 0.0, "rpm_limit": 460.0, "motor_on": False}
    evs = [e for t in range(8, 12) for e in d2.tick(S(t, {"left": slow, "right": off}))]
    assert [e["what"] for e in evs] == ["right prop (Motor disengaged)"]  # motor disengaged in flight counts + why


def test_props_dashboard_per_group():
    ps = {"groups": {"left": {"n": 1, "rpm": 440.0, "rpm_limit": 460.0, "torque": 100.0, "pitch": 24.0, "motor_on": True},
                     "right": {"n": 1, "rpm": 0.0, "rpm_limit": 460.0, "torque": 100.0, "pitch": -24.0, "motor_on": False}},
          "counter_pairs": [("left", "right")], "reverse": False}
    txt = em.props_detail([("r", "Engaged", "440"), ("r", "Disengaged", "0")], ps)
    assert txt == "L: 440 RPM/460 tq 100% pitch +24; R: 0 RPM/460 tq 100% pitch -24 motor OFF; counter-rotating"
    v, *_ = twin()
    st = pr.prop_status(v)
    assert set(st["groups"]) == {"left", "right"} and st["groups"]["left"]["pitch"] == 12.0
    g = {"rpm": 440.0, "rpm_limit": 460.0, "motor_on": True, "brake": 0.0, "torque": 100.0}
    assert pr.group_sample(v) == {"left": g, "right": g}
