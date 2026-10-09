"""Helicopter autopilot audit: no throttle for lift, preflight/spin-up, face vs track, plane redirect, wrong-way fix."""
import ast
from pathlib import Path
from types import SimpleNamespace as NS

from kspchat import emergency as em, heli, ksp_actions, propulsion as pr
from tests.test_heli import H, S, luke_vessel, reset_caches


ROOT = Path(__file__).resolve().parent.parent


def test_heli_py_never_touches_main_throttle():
    """Altitude comes from collective blade pitch — heli.py must not write v.control.throttle."""
    src = (ROOT / "kspchat" / "heli.py").read_text(encoding="utf-8")
    tree = ast.parse(src)
    hits = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Attribute) and node.attr == "throttle":
            hits.append(node.lineno)
        if isinstance(node, ast.Constant) and isinstance(node.value, str) and "control.throttle" in node.value:
            hits.append(node.lineno)
    assert hits == [], f"heli.py references throttle at lines {hits}"


def test_set_throttle_does_not_map_to_heli_torque(monkeypatch):
    """Prop planes map throttle -> torque; helicopters must not."""
    v, main, side, blades = luke_vessel()
    reset_caches()
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions, "_planeish", lambda vv: False)
    monkeypatch.setattr(heli, "is_heli", lambda vv: True)
    monkeypatch.setattr(pr, "has_props", lambda vv: True)
    called = []
    monkeypatch.setattr(pr, "set_rotor", lambda *a, **k: called.append(k) or "torque set")
    v.situation = "VesselSituation.flying"
    v.control = NS(throttle=0.5)
    r = ksp_actions.set_throttle(0.8)
    assert called == [] and "Props:" not in r and "80%" in r


def test_preflight_then_zero_collective_before_spinup(monkeypatch):
    """Checklist first; blades go to 0 collective before the rotor is asked to lift."""
    reset_caches()
    monkeypatch.setattr(ksp_actions, "_has_wings", lambda v: True)
    v, main, side, blades = luke_vessel()
    for b in blades:
        b._f["Deploy Angle"] = "12"
    line, probs = pr.preflight(v)
    assert pr.STATE["sense"] and pr.STATE["sense"]["rotors"][0]["dir"] == 1
    assert line.startswith("Pre-flight:")
    # mimic heli._fly blade setup: zero collective while spooling
    pr.set_blades(v, pitch=0.0, deploy=True, rotors={0})
    assert ("Deploy Angle", 0.0) in blades[0].calls
    assert heli.SPINUP_RPM > 0 and heli.SPINUP_S > 0


def test_forward_speed_is_nose_pitch_not_throttle():
    """Speed law: nose down = accelerate; collective is height only (plan has no throttle field)."""
    single = heli.classify([{"up": 1.0, "ax": (0, 0, -1), "lat": 0.0, "h": (0, 0), "dir": 1, "power_loss": ""}], False)
    st = {"alt": 20.0, "heading": 0.0, "speed": 30.0, "hold_err": None, "track": 0.0, "face": None}
    p = heli.plan("fly", 20.0, 0.0, 0.0, 0.0, 0.0, st, set(), single)
    assert p["pitch_t"] == -heli.TILT_MAX and "throttle" not in p
    # face pinned, travel the other way = backwards flight (heading ≠ track)
    p = heli.plan("fly", 20.0, 0.0, 0.0, 0.0, 0.0, dict(st, track=180.0, face=0.0, speed=20.0), set(), single)
    assert p["hdg_t"] == 0.0 and p["fwd_t"] < 0 and p["pitch_t"] > 0


def test_heli_control_rejects_planes(monkeypatch):
    v = NS(situation="VesselSituation.landed")
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions, "_heli_v", lambda vv: None)
    monkeypatch.setattr(ksp_actions, "_planeish", lambda vv: True)
    monkeypatch.setattr(ksp_actions, "takeoff", lambda altitude_m=300: f"takeoff to {altitude_m:.0f}")
    r = ksp_actions.heli_control("hover", altitude_m=50)
    assert r.startswith("Not a helicopter") and "takeoff to 50" in r


def test_wrong_way_rotor_detected_and_fixed(monkeypatch):
    """Reversed motor direction vs the pre-takeoff sense: stop, correct, spin back up."""
    reset_caches()
    monkeypatch.setattr(ksp_actions, "_has_wings", lambda v: True)
    v, main, side, blades = luke_vessel()
    # give Mod the writers set_spin_dir / set_brake need
    def set_field_string(k, val, m=main):
        m.calls.append((k, val))
        m._f[k] = val
    def set_field_bool(k, val, m=main):
        m.calls.append((k, val))
        m._f[k] = "Counterclockwise" if (k == "Rotation Direction" and val) else ("Clockwise" if k == "Rotation Direction" else str(val))
    main.set_field_string = set_field_string
    main.set_field_bool = set_field_bool
    main.set_field_float = lambda k, val: (main.calls.append((k, val)), main._f.__setitem__(k, f"{val:g}"))

    pr.preflight(v)
    ref = pr.STATE["sense"]
    assert ref["rotors"][0]["dir"] == 1

    main._f["Rotation Direction"] = "Counterclockwise"   # saboteur flipped it
    pr._CACHE.clear()  # fields cache may be empty; layout caches dir at build — force re-read via rotor_checks
    # layout caches dir at first build; clear layout so spin_dir is re-read
    pr.STATE["layout"].clear()
    checks = pr.rotor_checks(v)
    bad = pr.sense_bad(ref, checks)
    assert bad and bad[0]["kind"] == "dir" and bad[0]["want"] == 1 and bad[0]["got"] == -1

    # emergency path
    d = em.Detector()
    d.tick(S(0, H(), rotors=[{"i": 0, "group": "center", "title": "M-32S", "label": "center rotor", "brake": 0.0,
                              "torque": 100.0, "motor_on": True, "rpm": 450.0, "rpm_limit": 460.0, "has_brake": True,
                              "dir": 1, "invert": False}]))
    evs = d.tick(S(1, H(), rotors=[{"i": 0, "group": "center", "title": "M-32S", "label": "center rotor", "brake": 0.0,
                                   "torque": 100.0, "motor_on": True, "rpm": 450.0, "rpm_limit": 460.0, "has_brake": True,
                                   "dir": -1, "invert": False}]))
    assert evs and evs[0]["kind"] == "rotor_sense" and "rotor_sense:0" in evs[0]["actions"][0]

    monkeypatch.setattr(em, "_engaged", lambda: True)
    lines = em.handle(evs, v, S(1, H(), who="Sidry"), post=lambda x: None)
    assert "WRONG way" in lines[0] or "reversed" in lines[0].lower() or "spinning" in lines[0].lower()

    # direct fix restores Clockwise (stop -> correct -> spin up)
    pr.STATE["layout"].clear()
    main._f["Rotation Direction"] = "Counterclockwise"
    main.calls.clear()
    report = pr.fix_rotor_sense(v, bad)
    assert "spinning back up" in report
    assert main._f["Rotation Direction"] == "Clockwise"
    assert any(c[0] in ("Rotation Direction", "Brake", "Torque Limit(%)") for c in main.calls)


def test_blade_invert_in_sense_bad():
    ref = {"rotors": [], "blades": [{"j": 0, "rotor": 0, "invert": False}]}
    assert pr.sense_bad(ref, [], [True])[0]["kind"] == "blade"
    assert pr.sense_bad(ref, [], [False]) == []
