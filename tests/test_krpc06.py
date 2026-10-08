"""Offline: kRPC 0.6.0 compatibility (live bug 2026-10-08, Luke's Kerbodyne Rotodyne): AutoPilot has no engage() -
only the `engaged` property; Module.fields THROWS on BG rotors (two fields named 'Motor'); the R7000 Turboshaft +
EM-32 hub stacked on one axis are ONE (coaxial) main rotor; blades belong to the hub they are attached to.
Fakes mirror the real kRPC 0.6 API surface and the field ids / values read from the live craft."""
import re
import sys
from pathlib import Path
from types import SimpleNamespace as NS

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import heli, krpcx, ksp_actions, propulsion as pr  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
SRF, RF = "srf", "rf"


class AutoPilot06:
    """kRPC 0.6 SpaceCenter.AutoPilot: `engaged` is a read/write property; engage()/disengage() don't exist."""
    __slots__ = ("_on", "reference_frame", "target_pitch", "target_heading", "target_roll")

    def __init__(self):
        self._on = False

    @property
    def engaged(self):
        return self._on

    @engaged.setter
    def engaged(self, on):
        self._on = bool(on)


class AutoPilotOld:
    def __init__(self):
        self.on = False

    def engage(self):
        self.on = True

    def disengage(self):
        self.on = False


def test_autopilot_on_off_on_kRPC_06_and_older():
    ap = AutoPilot06()
    with pytest.raises(AttributeError):
        ap.engage()  # the live error
    krpcx.autopilot(ap, True)
    assert ap.engaged is True
    krpcx.autopilot(ap, False)
    assert ap.engaged is False
    old = AutoPilotOld()
    krpcx.autopilot(old, True)
    assert old.on
    ksp_actions._ap(ap, True)
    assert ap.engaged


def test_no_direct_engage_disengage_calls_left():
    bad = []
    for f in (ROOT / "kspchat").glob("*.py"):
        if f.name == "krpcx.py":
            continue
        for n, line in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
            if re.search(r"(auto_pilot|\bap)\.(engage|disengage)\(", line):
                bad.append(f"{f.name}:{n}")
    assert not bad, bad


# ---------------------------------------------------------------- Module fake (kRPC 0.6 + live Rotodyne values)
ROTOR_GUI = [("rpmLimit", "RPM Limit", "float"), ("currentRPM", "Current RPM", "float"),
             ("rotateCounterClockwise", "Rotation Direction", "bool"), ("inverted", "Invert Direction", "bool"),
             ("brakePercentage", "Brake", "float"), ("servoIsLocked", "Locked", "bool"),
             ("servoMotorIsEngaged", "Motor", "bool"), ("servoMotorLimit", "Torque Limit(%)", "float"),
             ("motorState", "Motor", "string"), ("lockPartOnPowerLoss", "On Power Loss", "bool")]


class Field:
    def __init__(self, mod, fid, gui):
        self.module, self.name, self.gui_name, self.visible = mod, fid, gui, True

    @property
    def value(self):
        return self.module._v[self.name]


class Module06:
    def __init__(self, name, part, values, gui):
        self.name, self.part, self._v, self._gui, self.calls = name, part, dict(values), gui, []
        self.actions, self.events = [], []

    @property
    def fields(self):
        seen = {}
        for fid, g, _ in self._gui:
            if g in seen:
                raise RuntimeError("An item with the same key has already been added. Key: " + g)
            seen[g] = self._v[fid]
        return seen

    @property
    def fields_by_id(self):
        return {fid: self._v[fid] for fid, _, _ in self._gui}

    @property
    def field_list(self):
        return [Field(self, fid, g) for fid, g, _ in self._gui]

    def set_field_float_by_id(self, fid, x):
        self.calls.append((fid, x))
        self._v[fid] = f"{x:g}"

    def set_field_bool_by_id(self, fid, x):
        self.calls.append((fid, x))
        self._v[fid] = str(bool(x))

    def set_field_string_by_id(self, fid, x):
        self.calls.append((fid, x))
        self._v[fid] = x

    def _by_name(self, k):
        hits = [fid for fid, g, _ in self._gui if g == k]
        if len(hits) != 1:
            raise RuntimeError(f"field {k!r} ambiguous/missing")
        return hits[0]

    def set_field_float(self, k, x):
        self.set_field_float_by_id(self._by_name(k), x)

    def set_field_bool(self, k, x):
        self.set_field_bool_by_id(self._by_name(k), x)


class Part:
    n = 0

    def __init__(self, title, pos, up_srf, ax_rf, parent=None):
        Part.n += 1
        self._object_id, self.title, self._pos, self._up, self._ax, self.parent = \
            Part.n, title, pos, up_srf, ax_rf, parent

    def position(self, rf):
        return self._pos if rf == RF else (-self._pos[2], self._pos[1], self._pos[0])

    def direction(self, rf):
        return self._up if rf == SRF else self._ax


def rotor(title, pos, up, ax, ccw, rpm_limit):
    vals = {"rpmLimit": str(rpm_limit), "currentRPM": "0", "rotateCounterClockwise": str(ccw), "inverted": "False",
            "brakePercentage": "100", "servoIsLocked": "False", "servoMotorIsEngaged": "True", "servoMotorLimit": "0",
            "motorState": "Motorized", "lockPartOnPowerLoss": "True"}
    return Module06("ModuleRoboticServoRotor", Part(title, pos, up, ax), vals, ROTOR_GUI)


def blade(title, pos, hub):
    return Module06("ModuleControlSurface", Part(title, pos, (1, 0, 0), (0, 0, -1), parent=hub.part),
                    {"deployAngle": "6", "deploy": "True"}, [("deployAngle", "Deploy Angle", "float"),
                                                             ("deploy", "Deploy", "bool")])


def rotodyne():
    """Live vessel-frame positions (x right, y fwd, z down): side props at x = +-2.03, R7000 at z -0.35 with its
    blades at z -1.19 (nearer the EM-32 hub at z -1.15 than its own!), EM-32 blades at z -1.47."""
    up, side = (1.0, 0.0, 0.0), (0.0, 0.0, 1.0)
    l_prop = rotor("EM-32S Standard Rotor", (-2.03, -0.96, 0.15), side, (0.0, -1.0, 0.0), True, 460)
    r_prop = rotor("EM-32S Standard Rotor", (2.03, -0.96, 0.15), side, (0.0, -1.0, 0.0), False, 460)
    r7000 = rotor("R7000 Turboshaft Engine", (0.0, -0.08, -0.35), up, (0.0, 0.09, -1.0), False, 430)
    em32 = rotor("EM-32 Standard Rotor", (0.0, -0.01, -1.15), up, (0.0, 0.09, -1.0), True, 430)
    rotors = [r_prop, l_prop, r7000, em32]
    blades = [blade("Propeller Blade Type A", (2.19, -1.27, 0.29), r_prop) for _ in range(6)]
    blades += [blade("Propeller Blade Type A", (-1.88, -1.27, 0.3), l_prop) for _ in range(6)]
    blades += [blade("Helicopter Blade Type B", (0.28, 0.12, -1.19), r7000) for _ in range(6)]
    blades += [blade("Helicopter Blade Type B", (0.27, -0.11, -1.47), em32) for _ in range(6)]
    v = NS(name="Kerbodyne Rotodyne", reference_frame=RF, surface_reference_frame=SRF,
           parts=NS(all=list(range(67)), engines=[], wheels=[1],
                    control_surfaces=[NS(part=b.part, deployed=True) for b in blades],
                    modules_with_name=lambda n: rotors if n == "ModuleRoboticServoRotor" else
                    (blades if n == "ModuleControlSurface" else [])),
           flight=lambda rf=None: NS(surface_altitude=1.0, heading=90.0))
    return v, rotors, blades


def reset():
    pr._CACHE.clear()
    heli._INFO.clear()
    for k in ("sign", "radius", "layout"):
        pr.STATE[k] = {}
    pr.STATE["logged"] = set()
    pr._FIELDS_BROKEN.clear()
    pr._FIELD_IDS.clear()


def test_fields_fall_back_to_ids_with_gui_names_and_values():
    reset()
    v, rotors, _ = rotodyne()
    f = pr.fields(rotors[2])
    assert f["RPM Limit"] == "430" and f["Brake"] == "100" and f["Torque Limit(%)"] == "0"
    assert f["Motor"] == "Engaged" and f["Rotation Direction"] == "Clockwise" and f["On Power Loss"] == "Locked"
    assert pr.find_field(f, "motor", exclude=("size", "output", "motorized")) == "Motor"
    assert pr.fields(rotors[3])["Rotation Direction"] == "Counterclockwise"


def test_preflight_releases_brake_and_sets_torque_by_id():
    reset()
    v, rotors, _ = rotodyne()
    line, probs = pr.preflight(v)
    assert len(probs) == 8, probs  # 4 rotors x (Brake 100, Torque 0)
    for m in rotors:
        assert ("brakePercentage", 0.0) in m.calls and ("servoMotorLimit", pr.TORQUE_MAX) in m.calls
    assert all(c["brake"] == 0 and c["torque"] == pr.TORQUE_MAX and c["motor_on"] for c in pr.rotor_checks(v))
    rep = pr.set_rotor(v, rpm=pr.RPM_MAX, torque=pr.TORQUE_MAX, motor=True, rotors={2, 3})
    assert "(2/2)" in rep and "(0/2)" not in rep, rep


def test_coaxial_stack_is_one_main_rotor_and_blades_follow_their_hub(monkeypatch):
    reset()
    monkeypatch.setattr(ksp_actions, "_has_wings", lambda v: True)
    v, rotors, blades = rotodyne()
    lay = pr.layout(v)
    assert [r["blades"] for r in lay["rotors"]] == [6, 6, 6, 6]
    assert all(b["rotor"] == 2 for b in lay["blades"][12:18])  # R7000's own blades, not the nearer EM-32 hub
    info = heli.scan(v)
    assert info["heli"] and info["compound"] and info["coaxial"] and info["counter"]
    assert info["mains"] == [[2, 3]] and sorted(info["lift"]) == [2, 3]
    assert info["left"] == [1] and info["right"] == [0]
    assert info["kind"] == "compound (coaxial main rotor + side props)"
    assert info["roles"].startswith("main rotor = coaxial stack of 2 (R7000 Turboshaft Engine (Clockwise, Locked) + "
                                    "EM-32 Standard Rotor (Counterclockwise, Locked), driven together)")
    assert "lift rotor" not in info["roles"]


def test_bladeless_hub_does_not_count():
    rots = [{"up": 1.0, "ax": (0, 0, -1), "lat": 0.0, "h": (0, 0), "dir": 1, "blades": 6},
            {"up": 1.0, "ax": (0, 0, -1), "lat": 0.0, "h": (3.0, 0), "dir": 1, "blades": 0, "title": "bare hub"}]
    c = heli.classify(rots, wings=False)
    assert c["lift"] == [0] and c["bare"] == [1] and c["kind"] == "single rotor (no tail rotor)"
    assert "no blades (ignored) = bare hub" in heli.roles_text(c, [dict(r, title=r.get("title", "M")) for r in rots])
