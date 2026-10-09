"""Offline: helicopter support (classification incl. Luke's compound heli, collective / yaw / translation laws,
heading vs track, rotor brake / torque checks, emergencies, routing, dashboard). Mocked kRPC; no live files."""
import sys
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import emergency as em, heli, ksp_actions, orders, propulsion as pr  # noqa: E402

SRF, RF = "srf", "rf"


def rot(up, ax, lat, h=(0.0, 0.0), d=1, pl=""):
    return {"up": up, "ax": ax, "lat": lat, "h": h, "dir": d, "power_loss": pl}


def luke():  # one main M-32S rotor on top + two side props pushing forward, small wings
    return [rot(1.0, (0, 0, -1), 0.0, pl="Locked"), rot(0.0, (0, 1, 0), -2.5, d=1), rot(0.0, (0, 1, 0), 2.5, d=-1)]


def test_classify_layouts():
    c = heli.classify(luke(), wings=True)
    assert c["heli"] and c["compound"] and c["locked"] and c["lift"] == [0] and c["left"] == [1] and c["right"] == [2]
    assert c["yaw"] == "side props (differential pitch)" and c["kind"] == "compound (main rotor + side props)"
    st = heli.classify([rot(0.99, (0, 0, -1), 0.0), rot(0.05, (1, 0, 0), 0.0)], wings=False)
    assert st["heli"] and st["tail"] == [1] and st["yaw"] == "tail rotor" and st["kind"] == "single rotor + tail rotor"
    cx = heli.classify([rot(1.0, (0, 0, -1), 0.0, h=(0, 0), d=1), rot(1.0, (0, 0, -1), 0.0, h=(0.1, 0), d=-1)], False)
    assert cx["coaxial"] and cx["counter"] and cx["yaw"] == "coaxial differential torque"
    tandem = heli.classify([rot(1.0, (0, 0, -1), 0.0, h=(4, 0)), rot(1.0, (0, 0, -1), 0.0, h=(-4, 0))], False)
    assert tandem["kind"] == "tandem" and tandem["yaw"] == "reaction wheels / SAS"
    plane = heli.classify([rot(0.0, (0, 1, 0), -3.0), rot(0.0, (0, 1, 0), 3.0)], wings=True)  # twin prop plane
    assert not plane["heli"]
    assert not heli.classify([rot(1.0, (0, 0, -1), 0.0)], wings=True)["heli"]  # one lift fan on a winged plane
    assert heli.classify([rot(1.0, (0, 0, -1), 0.0)], wings=False)["kind"] == "single rotor (no tail rotor)"
    rots = [dict(r, title=t) for r, t in zip(luke(), ("M-32S Rotor", "EM-16 L", "EM-16 R"))]
    assert heli.roles_text(c, rots) == ("main rotor = M-32S Rotor (Clockwise, Locked); left prop = EM-16 L (Clockwise); "
                                        "right prop = EM-16 R (Counterclockwise)")


class Part:
    n = 0

    def __init__(self, title, pos, up_srf, ax_rf):
        Part.n += 1
        self._object_id, self.title, self._pos, self._up, self._ax = Part.n, title, pos, up_srf, ax_rf

    def position(self, rf):
        return self._pos if rf == RF else (self._pos[2] * -1, self._pos[1], self._pos[0])

    def direction(self, rf):
        return self._up if rf == SRF else self._ax


class Mod:
    def __init__(self, part, fields):
        self.part, self._f, self.calls = part, dict(fields), []
        self.actions, self.events = [], []

    @property
    def fields(self):
        return dict(self._f)

    def set_field_float(self, k, v):
        self.calls.append((k, v))
        self._f[k] = f"{v:g}"


def luke_vessel():
    # Luke's exact rotor fields (screenshot): RPM Limit, Current RPM, Rotation Direction, Brake, Motor, Torque Limit(%),
    # Motor: Motorized, On Power Loss, Auto-Shift State
    main = Mod(Part("M-32S Rotor", (0.0, 0.0, -2.0), (1.0, 0.0, 0.0), (0.0, 0.0, -1.0)),
               {"RPM Limit": "460", "Current RPM": "455", "Rotation Direction": "Clockwise", "Brake": "0",
                "Motor": "Engaged", "Torque Limit(%)": "100", "Motor: Motorized": "True", "On Power Loss": "Locked",
                "Auto-Shift State": "False"})
    side = [Mod(Part("EM-16 Rotor", (x, 0.5, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0)),
                {"RPM Limit": "460", "Current RPM": "440", "Rotation Direction": d, "Motor": "Engaged",
                 "Torque Limit(%)": "100", "On Power Loss": "Locked"}) for x, d in ((-2.5, "Clockwise"), (2.5, "Counterclockwise"))]
    blades = [Mod(Part("Helicopter Blade Type A", (0.0, 0.0, -2.0 + 0.01 * i), (1, 0, 0), (0, 0, -1)), {"Deploy Angle": "6"})
              for i in range(3)]
    blades += [Mod(Part("Propeller Blade Type A", (x + 0.3, 0.6, 0.0), (0, 0, 1), (0, 1, 0)), {"Deploy Angle": "10"})
               for x in (-2.5, 2.5)]
    rotors = [main] + side
    v = NS(name="Luke Compound", reference_frame=RF, surface_reference_frame=SRF,
           parts=NS(all=list(range(30)), engines=[], wheels=[1], control_surfaces=[NS(part=b.part, deployed=True) for b in blades],
                    modules_with_name=lambda n: rotors if n == "ModuleRoboticServoRotor" else (blades if n == "ModuleControlSurface" else [])),
           flight=lambda rf=None: NS(surface_altitude=35.0, heading=90.0))
    return v, main, side, blades


def reset_caches():
    if hasattr(pr, "_CACHE"):
        pr._CACHE.clear()
    heli._INFO.clear()
    for k in ("sign", "radius", "layout"):
        pr.STATE[k] = {}
    pr.STATE["logged"] = set()
    pr.STATE["sense"] = None


def test_scan_and_sample_lukes_fields(monkeypatch):
    reset_caches()
    monkeypatch.setattr(ksp_actions, "_has_wings", lambda v: True)
    v, main, side, blades = luke_vessel()
    info = heli.scan(v)
    assert info["heli"] and info["compound"] and info["locked"] and info["lift"] == [0]
    assert info["roles"].startswith("main rotor = M-32S Rotor (Clockwise, Locked); left prop = EM-16 Rotor")
    assert heli.is_heli(v)
    s = heli.sample(v)
    assert s["rpm"] == 455.0 and s["rpm_limit"] == 460.0 and s["motor_on"] is True and s["locked"]
    assert s["brake"] == 0.0 and s["torque"] == 100.0
    assert s["agl"] == 35.0 and s["hdg"] == 90.0 and s["coll"] == 6.0 and s["compound"]
    pr.set_blades(v, pitch=9.0, rotors={0})  # collective touches the main rotor blades only
    assert all(("Deploy Angle", 9.0) in b.calls for b in blades[:3]) and not any(b.calls for b in blades[3:])
    pr.set_blades(v, pitch=-4.0, allow_reverse=True, rotors={1})
    assert blades[3].calls == [("Deploy Angle", -4.0)] and not blades[4].calls


def test_collective_pi_settles_hover():
    pi = heli.VsPI(0.0)
    vs, coll = 0.0, 0.0
    for _ in range(40):  # on the ground: spools up
        coll = pi.step(1.5, 0.0, 0.2, on_ground=True, lo=0.0)
    assert coll > 5.0
    for _ in range(600):  # crude plant: hover collective 8 deg
        coll = pi.step(1.0, vs, 0.2)
        vs += (0.5 * (coll - 8.0) - 0.3 * vs) * 0.2
    assert abs(vs - 1.0) < 0.2 and heli.COLL_MIN <= coll <= heli.COLL_MAX
    for _ in range(600):
        coll = pi.step(0.0, vs, 0.2)
        vs += (0.5 * (coll - 8.0) - 0.3 * vs) * 0.2
    assert abs(vs) < 0.1 and abs(coll - 8.0) < 0.5


def test_targets_landing_schedule_and_tilt():
    assert heli.vs_target("hover", 10.0, 20.0) == 3.0 and heli.vs_target("hover", 20.0, 20.0) == 0.0
    assert heli.vs_target("land", 50, None) == -3.0 and heli.vs_target("land", 2.0, None) > -1.5
    assert heli.tilt(50.0, 0.0) == heli.TILT_MAX and heli.tilt(-50.0, 0.0) == -heli.TILT_MAX
    vn, ve = heli.hold_ne(1000.0, 0.0)
    assert abs(vn - heli.HOLD_VMAX) < 1e-9 and abs(ve) < 1e-9
    f, r = heli.body_vel(0.0, 1.0, 0.0)   # moving east, facing north -> to the right
    assert abs(f) < 1e-9 and abs(r - 1.0) < 1e-9
    assert heli.side_pitches(5.0, 1.0) == (15.0, -5.0)  # turn right: left prop pushes more


def test_plan_modes_and_emergencies():
    comp = heli.classify(luke(), True)
    single = heli.classify([rot(1.0, (0, 0, -1), 0.0)], False)
    st = {"alt": 20.0, "heading": 90.0, "speed": None, "hold_err": None}
    p = heli.plan("hover", 20.0, 0.0, 0.0, 0.0, 90.0, st, set(), comp)
    assert p["vs_t"] == 0.0 and p["pitch_t"] == 0.0 and p["hdg_t"] == 90.0
    p = heli.plan("fly", 20.0, 0.0, 0.0, 0.0, 90.0, dict(st, speed=30.0), set(), comp)
    assert abs(p["fwd_t"] - 30.0) < 1e-9 and p["pitch_t"] == -heli.COMPOUND_PITCH   # compound: less tilt
    p = heli.plan("fly", 20.0, 0.0, 0.0, 0.0, 90.0, dict(st, speed=30.0), set(), single)
    assert p["pitch_t"] == -heli.TILT_MAX                                          # single: nose down to accelerate
    p = heli.plan("fly", 20.0, 0.0, 25.0, 0.0, 90.0, dict(st, speed=0.0), set(), single)
    assert p["pitch_t"] == heli.TILT_MAX                                           # nose up = slow / stop
    p = heli.plan("hover", 200.0, -5.0, 10.0, 0.0, 90.0, st, {"heli_rpm"}, comp)  # Locked main rotor: glide
    assert p["mode"] == "glide" and abs(p["fwd_t"] - heli.GLIDE_SPD) < 1e-9 and "Locked" in p["note"] and not p["autorot"]
    p = heli.plan("hover", 8.0, -4.0, 10.0, 0.0, 90.0, st, {"heli_rpm"}, single)   # free-wheeling: autorotation
    assert p["autorot"] and p["mode"] == "land" and p["pitch_t"] == heli.FLARE_PITCH and p["coll_lo"] == 0.0
    assert heli.autorot_collective(100.0, -8.0, 300.0, 460.0) <= heli.AUTOROT_MAX
    assert heli.autorot_collective(4.0, -6.0, 300.0, 460.0) > 10.0                 # cushion near the ground
    p = heli.plan("hover", 60.0, -8.0, 0.0, 0.0, 90.0, st, {"heli_vrs"}, single)
    assert p["vs_t"] >= -2.0 and p["fwd_t"] >= 12.0
    p = heli.plan("hover", 60.0, 0.0, 0.0, 0.0, 90.0, st, {"heli_tail"}, single)
    assert p["mode"] == "land" and p["fwd_t"] >= 20.0
    p = heli.plan("hover", 60.0, 0.0, 0.0, 0.0, 90.0, st, {"prop_out:left"}, comp)
    assert p["mode"] == "land"
    p = heli.plan("hover", 60.0, 0.0, 0.0, 0.0, 90.0, st, {"inverted_air"}, comp)
    assert p["coll_lo"] == 0.0 and p["pitch_t"] == 0.0 and p["roll_t"] == 0.0


def test_heading_and_track_are_decoupled():
    single = heli.classify([rot(1.0, (0, 0, -1), 0.0)], False)
    base = {"alt": 20.0, "heading": 0.0, "speed": None, "hold_err": None, "track": None, "face": None}
    # sidestep: hold point 10 m east, nose north -> lateral velocity only, nose unchanged, roll right
    p = heli.plan("hold", 20.0, 0.0, 0.0, 0.0, 0.0, dict(base, hold_err=(0.0, 10.0), face=0.0), set(), single)
    assert abs(p["fwd_t"]) < 1e-9 and abs(p["right_t"] - 1.0) < 1e-9 and p["hdg_t"] == 0.0 and p["roll_t"] > 0
    # back up: hold point 10 m south -> backwards, nose up
    p = heli.plan("hold", 20.0, 0.0, 0.0, 0.0, 0.0, dict(base, hold_err=(-10.0, 0.0), face=0.0), set(), single)
    assert p["fwd_t"] < 0 and p["pitch_t"] > 0 and p["hdg_t"] == 0.0
    # fast: the nose follows the track; the velocity is resolved at the CURRENT heading
    p = heli.plan("fly", 20.0, 0.0, 0.0, 0.0, 0.0, dict(base, track=90.0, speed=25.0), set(), single)
    assert p["hdg_t"] == 90.0 and p["trk_t"] == 90.0 and abs(p["fwd_t"]) < 1e-9 and abs(p["right_t"] - 25.0) < 1e-9
    # slow: the nose holds
    p = heli.plan("fly", 20.0, 0.0, 0.0, 0.0, 0.0, dict(base, track=90.0, speed=10.0), set(), single)
    assert p["hdg_t"] == 0.0 and abs(p["right_t"] - 10.0) < 1e-9
    # face pinned while flying the other way: backwards flight
    p = heli.plan("fly", 20.0, 0.0, 0.0, 0.0, 0.0, dict(base, track=180.0, speed=20.0, face=0.0), set(), single)
    assert p["hdg_t"] == 0.0 and abs(p["fwd_t"] + 20.0) < 1e-9 and p["pitch_t"] == heli.TILT_MAX
    assert heli.track_of(0.0, 5.0) == (90.0, 5.0)


def test_yaw_sign_learning():
    ys = heli.YawSign()
    t, rate, flips = 0.0, 0.0, []
    for _ in range(22):  # command right, but the nose accelerates left -> 3 wrong 1 s windows -> flip
        t += 0.2
        rate -= 1.0
        flips.append(ys.feed(t, 0.6, rate))
    assert flips.count(True) == 1 and ys.sign == -1.0
    ys2 = heli.YawSign()
    t, rate = 0.0, 0.0
    for _ in range(30):
        t += 0.2
        rate += 1.0
        assert not ys2.feed(t, 0.6, rate)
    yc = heli.YawCtl()
    assert yc.step(30.0, 0.0, 0.2) > 0 and yc.step(-30.0, 0.0, 0.2) < 0


def S(t, h, **kw):
    s = {"t": t, "vid": 1, "sit": "flying", "throttle": 0.5, "parts": 30, "stage": 0, "g": 1.0, "crew": 1, "ctrl": True,
         "speed": 5.0, "vs": 0.0, "aoa": 0.0, "engines": [], "fuel": None, "heli": h, "props": None, "roll": 0.0, "pitch": 0.0}
    s.update(kw)
    return s


def H(**kw):
    h = {"rpm": 455.0, "rpm_limit": 460.0, "motor_on": True, "locked": True, "compound": True, "kind": "compound",
         "tail_rpm": None, "tail_n": 0, "agl": 100.0, "hdg": 90.0, "coll": 8.0}
    h.update(kw)
    return h


def test_detector_heli_rpm_locked_glide_and_chat(monkeypatch):
    monkeypatch.setattr(em, "_engaged", lambda: False)
    d = em.Detector()
    d.tick(S(0, H()))
    d.tick(S(1, H()))
    assert d.tick(S(2, H(rpm=100.0))) == []
    evs = d.tick(S(4, H(rpm=100.0)))
    assert [(e["kind"], e["actions"], e["locked"]) for e in evs] == [("heli_rpm", ["heli_glide"], True)]
    said = em.handle(evs, None, S(4, H(), who="Sidry"), post=lambda x: None)
    assert "On Power Loss: Locked" in said[0] and "no autorotation" in said[0] and "glide" in said[0]
    assert "MAIN ROTOR RPM LOW" in em.alerts_text()
    back = d.tick(S(6, H(rpm=450.0)))
    assert [(e["kind"], e["start"]) for e in back] == [("heli_rpm", False)]
    em.handle(back, None, S(6, H()), post=lambda x: None)
    d2 = em.Detector()  # not locked, not compound -> autorotation
    for t in range(2):
        d2.tick(S(t, H(locked=False, compound=False)))
    evs = d2.tick(S(2, H(rpm=50.0, locked=False, compound=False))) + d2.tick(S(4, H(rpm=50.0, locked=False, compound=False)))
    assert evs[0]["actions"] == ["autorotate"]


def test_detector_spin_vrs_inverted_and_side_prop(monkeypatch):
    monkeypatch.setattr(em, "_engaged", lambda: False)
    d = em.Detector()
    d.tick(S(0, H()))
    evs = []
    for t in range(1, 6):
        evs += d.tick(S(t, H(hdg=(90.0 + 60.0 * t) % 360.0)))
    assert [e["kind"] for e in evs] == ["heli_tail"] and evs[0]["actions"] == ["heli_spin"]
    evs = d.tick(S(6, H(hdg=(90.0 + 60.0 * 5 + 2) % 360.0)))
    assert [(e["kind"], e["start"]) for e in evs] == [("heli_tail", False)]
    evs = d.tick(S(7, H(hdg=32.0), vs=-8.0, speed=8.5)) + d.tick(S(8, H(hdg=32.0), vs=-8.0, speed=8.5))
    assert [e["kind"] for e in evs] == ["heli_vrs"] and evs[0]["actions"] == ["vrs_exit"]
    evs = d.tick(S(9, H(hdg=32.0), vs=-2.0, speed=15.0))
    assert [(e["kind"], e["start"]) for e in evs] == [("heli_vrs", False)]
    evs = [e for t in range(10, 14) for e in d.tick(S(t, H(hdg=32.0), roll=170.0))]
    assert [(e["kind"], e["actions"]) for e in evs] == [("upside_down", ["heli_upright"])]
    good = {"rpm": 440.0, "rpm_limit": 460.0, "motor_on": True}
    dead = {"rpm": 10.0, "rpm_limit": 460.0, "motor_on": True}
    d3 = em.Detector()
    d3.tick(S(0, H(), props={"left": good, "right": good, "center": good}))
    d3.tick(S(1, H(), props={"left": good, "right": good, "center": good}))
    evs = [e for t in (2, 4) for e in d3.tick(S(t, H(), props={"left": dead, "right": good, "center": dead}))]
    assert [(e["what"], e["actions"]) for e in evs] == [("left prop", ["heli_side_out"])]   # center = main rotor: own watch


def test_dashboard_mode_row():
    rows = em.build_systems(S(0, H(), vs=-1.25), None, None)
    row = next(r for r in rows if r[1] == "Mode")
    assert row == ("ok", "Mode", "HELI (compound), rotor 455 RPM/460, collective 8.0 deg, V/S -1.2 m/s, "
                                 "HDG 090 / TRK - (hovering), Locked on power loss")
    rows = em.build_systems(S(0, H(trk=120.0, gs=12.0), vs=0.0), None, None)
    assert "HDG 090 / TRK 120 (12 m/s), drift +30" in next(r for r in rows if r[1] == "Mode")[2]


def test_heli_orders():
    p = orders.parse
    assert p("hover") == ("heli_control", {"mode": "hover"})
    assert p("hover at 30") == ("heli_control", {"mode": "hover", "altitude_m": 30.0})
    assert p("hold position") == ("heli_control", {"mode": "hold"})
    assert p("descend") == ("heli_control", {"mode": "descend"})
    assert p("climb to 50") == ("heli_control", {"mode": "climb", "altitude_m": 50.0})
    assert p("fly heading 90 speed 20") == ("heli_control", {"mode": "fly", "heading": 90.0, "speed": 20.0})
    assert p("climb to 5000")[0] == "set_altitude" and p("descend to 50 m")[0] == "set_altitude"
    assert p("heading 270") == ("set_heading", {"heading": 270.0})
    assert p("land")[0] == "land" and p("take off")[0] == "takeoff"
    assert p("face 270") == ("heli_control", {"mode": "face", "heading": 270.0})
    assert p("fly 090") == ("heli_control", {"mode": "fly", "heading": 90.0})
    assert p("fly 90 at 25") == ("heli_control", {"mode": "fly", "heading": 90.0, "speed": 25.0})
    assert p("sidestep left") == ("heli_control", {"mode": "sidestep", "direction": "left"})
    assert p("side step right 12 m") == ("heli_control", {"mode": "sidestep", "direction": "right", "distance": 12.0})
    assert p("back up") == ("heli_control", {"mode": "back"})
    assert p("hover facing 180") == ("heli_control", {"mode": "hover", "heading": 180.0})
    assert p("fly to KSC")[0] == "fly_to_place"


def test_routing(monkeypatch):
    info = dict(heli.classify(luke(), True), roles="main rotor = M-32S Rotor")
    calls = []
    v = NS(situation="VesselSituation.landed", orbit=NS(body=NS(reference_frame=RF, equatorial_radius=600000.0)),
           flight=lambda rf=None: NS(surface_altitude=0.5, mean_altitude=70.5, latitude=0.0, longitude=0.0, heading=90.0),
           surface_reference_frame=SRF, parts=NS(wheels=[1]))
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions, "_heli_v", lambda vv: info)
    monkeypatch.setattr(ksp_actions.guard, "busy", lambda: None)
    monkeypatch.setattr(pr, "preflight", lambda vv, **kw: ("Pre-flight: center rotor (M-32S Rotor) Brake was 100 -> released (0)", ["x"]))
    monkeypatch.setattr(heli, "command", lambda mode, **kw: calls.append((mode, kw)) or "started")
    r = ksp_actions.takeoff()
    assert calls[-1][0] == "hover" and calls[-1][1]["alt"] == heli.HOVER_AGL
    assert "vertical takeoff to a hover at 20 m AGL" in r and "On Power Loss: Locked" in r and "side props" in r
    assert "Brake was 100 -> released (0)" in r and "Layout: main rotor = M-32S Rotor" in r
    assert "helicopter" in ksp_actions.plane_hold(altitude_m=500)
    assert "helicopter" in ksp_actions.land_plane()
    assert "speed loop" in ksp_actions.plane_pitch(10, "up")
    assert ksp_actions.heli_control("land") == "We're already on the ground."
    v.situation = "VesselSituation.flying"
    v.flight = lambda rf=None: NS(surface_altitude=40.0, mean_altitude=110.0, latitude=1.0, longitude=2.0, heading=90.0)
    ksp_actions.land()
    assert calls[-1][0] == "land"
    ksp_actions.set_altitude(60.0, direction="up")                       # < 500 m: taken as AGL
    assert calls[-1] == ("climb", {"alt": 60.0})
    ksp_actions.set_altitude(2000.0)                                     # MSL -> AGL
    assert calls[-1] == ("climb", {"alt": 1930.0})
    ksp_actions.heli_control("hold")
    assert calls[-1][0] == "hold" and calls[-1][1]["hold"] == (1.0, 2.0)
    r = ksp_actions.heli_control("sidestep", direction="right", distance=10)
    assert calls[-1][0] == "hold" and calls[-1][1]["face"] == 90.0 and "sidestepping right 10 m" in r
    lat, lon = calls[-1][1]["hold"]
    assert lat < 1.0 and abs(lon - 2.0) < 1e-6                               # nose east -> right = south
    r = ksp_actions.heli_control("fly", heading=180, speed=25)
    assert calls[-1][0] == "fly" and calls[-1][1]["track"] == 180.0 and calls[-1][1]["face"] == "clear"
    assert "below us" in ksp_actions.heli_control("climb", altitude_m=10)
    monkeypatch.setattr(ksp_actions, "_heli_v", lambda vv: None)
    assert "not a helicopter" in ksp_actions.heli_control("hover")


def test_preflight_brake_torque_motor_lukes_fields(monkeypatch):
    reset_caches()
    monkeypatch.setattr(ksp_actions, "_has_wings", lambda v: True)
    v, main, side, blades = luke_vessel()
    main._f["Brake"] = "100"                      # the screenshot: Brake 100 would hold the main rotor
    side[0]._f["Torque Limit(%)"] = "0"
    side[1]._f["Motor"] = "Disengaged"
    side[1].set_field_bool = lambda k, val: (side[1].calls.append((k, val)), side[1]._f.__setitem__(k, "Engaged"))
    cs = {c["i"]: c for c in pr.rotor_checks(v)}
    assert cs[0]["brake"] == 100.0 and cs[0]["motor_on"] is True and cs[0]["has_brake"]   # 'Motor: Motorized' ignored
    assert cs[1]["brake"] is None and not cs[1]["has_brake"]
    line, probs = pr.preflight(v)
    assert ("Brake", 0.0) in main.calls and ("Torque Limit(%)", 100.0) in side[0].calls and ("Motor", True) in side[1].calls
    assert "center rotor (M-32S Rotor) Brake was 100 -> released (0)" in line
    assert "left rotor (EM-16 Rotor) Torque Limit was 0 -> 100%" in line
    assert "right rotor (EM-16 Rotor) Motor was disengaged -> Engaged" in line
    assert "has no 'Brake' field" in line                                   # the side props here have none
    side[0]._f["Brake"] = side[1]._f["Brake"] = "0"
    line, probs = pr.preflight(v)
    assert probs == [] and line == "Pre-flight: 3 rotors Brake 0, torque set, Motor Engaged - OK"
    assert pr.set_brake(v, 100.0, rotors={0}) == 1 and main._f["Brake"] == "100"


def test_rotor_brake_in_flight_is_tamper(monkeypatch):
    monkeypatch.setattr(em, "_engaged", lambda: False)

    def rc(b, tq=100.0):
        return [{"i": 0, "group": "center", "title": "M-32S Rotor", "label": "center rotor (M-32S Rotor)", "brake": b,
                 "torque": tq, "motor_on": True, "rpm": 450.0, "rpm_limit": 460.0, "has_brake": True}]
    d = em.Detector()
    d.tick(S(0, H(), rotors=rc(0.0)))
    evs = d.tick(S(1, H(brake=100.0), rotors=rc(100.0)))
    assert [(e["kind"], e["actions"]) for e in evs] == [("rotor_brake", ["rotor_release:0"])]
    released = []
    monkeypatch.setattr(pr, "set_brake", lambda v, val, rotors=None: released.append((val, rotors)) or 1)
    said = em.handle(evs, object(), S(1, H(), who="Sidry"), post=lambda x: None)
    assert released == [(0.0, {0})] and "Brake released (0)" in said[0] and "ROTOR BRAKE 100" in em.alerts_text()
    evs = d.tick(S(2, H(), rotors=rc(0.0)))
    assert [(e["kind"], e["start"]) for e in evs] == [("rotor_brake", False)]
    em.handle(evs, None, S(2, H()), post=lambda x: None)
    evs = d.tick(S(3, H(), rotors=rc(0.0, tq=0.0)))
    assert [(e["kind"], e["actions"]) for e in evs] == [("rotor_torque", ["rotor_torque:0"])]
    d2 = em.Detector()  # on the ground a brake is just parking
    d2.tick(S(0, H(), sit="landed", rotors=rc(100.0)))
    assert d2.tick(S(1, H(), sit="landed", rotors=rc(100.0))) == []
    rows = em.build_systems(S(0, H(), sit="landed", rotors=rc(100.0)), {}, None)
    assert ("caution", "Rotor brakes", "center M-32S Rotor 100") in rows
    rows = em.build_systems(S(0, H(), rotors=rc(100.0)), {}, None)
    assert ("fail", "Rotor brakes", "center M-32S Rotor 100") in rows
    d3 = em.Detector()  # main rotor RPM loss names the cause
    d3.tick(S(0, H()))
    d3.tick(S(1, H()))
    evs = d3.tick(S(2, H(rpm=200.0, brake=100.0))) + d3.tick(S(4, H(rpm=200.0, brake=100.0)))
    assert evs[0]["what"] == "main rotor (Brake 100)" and "BRAKE 100" in evs[0]["short"]