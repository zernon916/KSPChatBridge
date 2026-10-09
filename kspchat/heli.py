"""Helicopter autopilot (Luke 2026-10-08: Breaking Ground rotor helicopters; his is a COMPOUND heli - one main
M-32S rotor on top (vertical axis), TWO side props pushing forward (one per side), no tail rotor, small wings).

Classification (classify, pure): rotors whose spin axis (the rotor part's 'up' direction) points mostly up in the
surface frame are lift rotors; horizontal-axis rotors are side props (axis fore-aft, off to one side) or tail rotors
(axis sideways). Lift rotor + left and right side props = compound; lift rotors + few/no wings = helicopter; two lift
rotors close together = coaxial, apart = tandem; opposite 'Rotation Direction' = counter-rotating.

Control (one thread, kRPC, DT s):
  * lift = main-rotor collective (blade 'Deploy Angle') from a PI loop on vertical speed; RPM Limit 460, Torque 100 %,
    Motor engaged. The vertical-speed target follows the AGL (radar altitude) target, or the landing schedule.
  * compound: forward speed = side-prop collective (blade pitch from a PI on forward speed, over the inflow angle),
    yaw = differential side-prop pitch (left more = nose right), pitch attitude kept ~level; lateral = roll tilt.
    Others: translation = attitude tilt (pitch / roll, capped TILT_MAX), yaw = tail rotor pitch, coaxial / multirotor
    differential torque (counter-rotating pairs), or reaction wheels via the kRPC AutoPilot heading.
    Multirotor (>=3 vertical lift rotors): every motor is spool_up'd before collective; land disengages motors.
  * the kRPC AutoPilot (surface frame) holds pitch / heading / roll targets.
Emergencies come from emergency.py (DET.active): main rotor RPM loss (autorotation; impossible when the rotor's 'On
Power Loss' is 'Locked' -> glide on the side props and wings / controlled descent), spin / tail loss, vortex ring
state, upside down. Gains are conservative and NOT yet tested in game: every new value is logged.
"""
import logging
import math
import threading
import time

from . import config, guard, krpcx
from . import propulsion as pr

log = logging.getLogger("kspchat")
LIFT_UP, SIDE_UP, AXIS_ALIGN = 0.8, 0.4, 0.7   # |cos| of rotor axis vs world up / vessel axes
COAX_SEP = 1.0              # m: lift rotors closer than this horizontally = coaxial
HOVER_AGL = 20.0            # 'takeoff' / 'hover' height
DESCEND_AGL = 10.0          # bare 'descend' ends in a hover here
TILT_MAX = 12.0             # deg attitude tilt for translation
COMPOUND_PITCH = 6.0        # deg: compound helis tilt less (the side props add the forward thrust)
COLL_MIN, COLL_MAX, COLL_START = 2.0, 30.0, 4.0
COLL_AUTOROT, AUTOROT_MAX = 1.0, 6.0
KP, KI, COLL_RATE, GROUND_SPOOL = 0.6, 0.15, 3.0, 1.0   # deg per m/s, deg per m/s/s, deg/s, deg/s
VS_UP, VS_DN, ALT_K = 3.0, 3.0, 0.3
SPD_MAX = 50.0
HOLD_K, HOLD_VMAX = 0.1, 3.0
VEL_K = 1.5                 # deg tilt per m/s speed error
YAW_KP, YAW_KD, YAW_KI, YAW_IMAX = 0.03, 0.05, 0.01, 0.6
SIDE_YAW_DEG = 10.0         # deg differential side-prop pitch at full yaw command
SIDE_KP, SIDE_KI, SIDE_LO, SIDE_HI = 0.8, 0.1, -12.0, 20.0
TAIL_YAW_DEG, COAX_YAW_TQ = 8.0, 10.0
GLIDE_SPD, GLIDE_VS, GLIDE_PITCH = 30.0, -3.0, 3.0
FLARE_AGL, FLARE_PITCH, CUSHION_AGL = 12.0, 10.0, 6.0
GOTO_ARRIVE = 30.0
SPINUP_S, SPINUP_RPM = 12.0, 60.0   # legacy absolute floor; spool_up waits for SPOOL_FRAC of each rotor's RPM Limit
PARK_RPM, PARK_WAIT_S = 20.0, 30.0  # optional rotor brake after landing (setting 'rotor_brake_park', default off)
MULTI_MIN = 3                       # >= this many vertical lift rotors = multirotor / quadcopter
DT = 0.2
NOSE_FOLLOW = 15.0          # m/s: above this the nose follows the track (unless 'face N' pinned it)
SIDESTEP_M, BACKUP_M = 5.0, 10.0
# heading = where the nose points (yaw loop); track = direction of travel over the ground (tilt loops on the ground
# velocity vector, resolved into forward / lateral relative to the nose); collective = vertical speed.
STATE = {"mode": "off", "alt": None, "heading": None, "track": None, "face": None, "speed": None, "hold": None,
         "goto": None, "t": 0.0}
STATUS = {"phase": "idle"}
_INFO = {}
_thread = None
_stop = threading.Event()


def _clamp(x, lo, hi):
    return max(lo, min(hi, x))


def _wrap(a):
    return (a + 180.0) % 360.0 - 180.0


# ======================================================================== classification (pure + kRPC scan)
def classify(rotors, wings):
    """rotors: [{up: cos(spin axis, world up), ax: spin axis in the vessel frame (x right, y forward, z down),
    h: (north, east) horizontal position, lat: vessel-frame x, dir: +1/-1/None, power_loss: text, blades: n attached}].
    A rotor counts only with blades attached (when blade counts are known); lift rotors stacked on one axis (closer
    than COAX_SEP horizontally - e.g. Luke's R7000 Turboshaft + EM-32 hub) are ONE main rotor ('mains' clusters),
    all of whose motors are still driven together."""
    known = any((r.get("blades") or 0) > 0 for r in rotors)
    bare = [i for i, r in enumerate(rotors) if known and r.get("blades") == 0]
    lift = [i for i, r in enumerate(rotors) if abs(r.get("up") or 0.0) >= LIFT_UP and i not in bare]
    horiz = [i for i, r in enumerate(rotors) if abs(r.get("up") or 0.0) < SIDE_UP and i not in bare]
    ax = lambda i, k: abs((rotors[i].get("ax") or (0.0, 0.0, 0.0))[k])  # noqa: E731
    tail = [i for i in horiz if ax(i, 0) > AXIS_ALIGN]
    props = [i for i in horiz if ax(i, 1) > AXIS_ALIGN]
    left = [i for i in props if (rotors[i].get("lat") or 0.0) <= -pr.GROUP_X]
    right = [i for i in props if (rotors[i].get("lat") or 0.0) >= pr.GROUP_X]
    # EM-16S / BG hubs often report a sideways part.direction, so every rotor looks like a "side prop".
    # Only when there is NO solid vertical lift rotor: 3+ bladeful → multirotor (do not steal compound side props).
    bladed = [i for i in range(len(rotors)) if i not in bare]
    if len(bladed) >= MULTI_MIN and not lift:
        soft = [i for i in bladed if abs(rotors[i].get("up") or 0.0) >= 0.3]
        lift = soft if len(soft) >= MULTI_MIN else list(bladed)
        horiz = tail = left = right = []
    compound = bool(lift and left and right)
    heli = bool(lift) and (compound or not wings or bool(tail) or len(lift) >= 2)
    mains = []
    for i in lift:  # cluster stacked lift rotors into one main rotor each
        hit = next((c for c in mains if any(math.dist(rotors[i].get("h") or (0, 0), rotors[j].get("h") or (0, 0))
                                            < COAX_SEP for j in c)), None)
        if hit is None:
            mains.append([i])
        else:
            hit.append(i)
    coax = any(len(c) > 1 for c in mains)
    dirs = {rotors[i].get("dir") for i in lift if rotors[i].get("dir")}
    counter = len(dirs) > 1
    locked = any("lock" in str(rotors[i].get("power_loss") or "").lower() for i in lift)
    if not heli:
        kind = "not a helicopter"
    elif compound:
        kind = "compound (" + ("coaxial " if coax else "") + "main rotor + side props)"
    elif len(lift) >= 2:
        kind = ("coaxial" if coax else ("tandem" if len(lift) == 2 else f"multirotor ({len(lift)})")) + \
            (", counter-rotating" if counter else "")
    else:
        kind = "single rotor" + (" + tail rotor" if tail else " (no tail rotor)")
    multi = (not compound) and len(lift) >= MULTI_MIN
    if compound:
        yaw = "side props (differential pitch)"
    elif tail:
        yaw = "tail rotor"
    elif multi and counter:
        yaw = "differential torque (counter-rotating pairs)"
    elif len(lift) >= 2 and coax and counter:
        yaw = "coaxial differential torque"
    else:
        yaw = "reaction wheels / SAS"
    return {"heli": heli, "lift": lift, "tail": tail, "left": left, "right": right, "compound": compound,
            "coaxial": coax, "counter": counter, "locked": locked, "multirotor": multi, "kind": kind, "yaw": yaw,
            "wings": bool(wings), "mains": mains, "bare": bare}


def roles_text(info, rots):
    """'main rotor = M-32S Rotor (Clockwise, Locked); left prop = ...; right prop = ...; tail rotor = ...'."""
    def name(i):
        d = rots[i]
        bits = [b for b in ({1: "Clockwise", -1: "Counterclockwise"}.get(d.get("dir")), d.get("power_loss")) if b]
        return (d.get("title") or f"rotor {i + 1}") + (f" ({', '.join(bits)})" if bits else "")
    mains = info.get("mains") or [[i] for i in info["lift"]]
    parts = []
    if info.get("multirotor"):
        for k, c in enumerate(mains):
            side = "L" if (rots[c[0]].get("lat") or 0.0) < 0 else ("R" if (rots[c[0]].get("lat") or 0.0) > 0 else "C")
            parts.append(f"{side}{k + 1} = {name(c[0])}" if len(c) == 1 else
                         f"{side}{k + 1} = stack (" + " + ".join(name(i) for i in c) + ")")
    else:
        for k, c in enumerate(mains):
            label = "main rotor" if len(mains) == 1 else f"lift rotor {k + 1}"
            parts.append(f"{label} = {name(c[0])}" if len(c) == 1 else
                         f"{label} = coaxial stack of {len(c)} (" + " + ".join(name(i) for i in c) + ", driven together)")
        parts += [f"left prop = {name(i)}" for i in info["left"]] + [f"right prop = {name(i)}" for i in info["right"]]
        parts += [f"tail rotor = {name(i)}" for i in info["tail"]]
    parts += [f"no blades (ignored) = {name(i)}" for i in info.get("bare") or ()]
    return "; ".join(parts) or "no rotors"


def motor_label(info, i, title, group="center"):
    """Emergency / dashboard name for one rotor (L1 (title), ... on multirotors)."""
    if not info.get("multirotor") or i not in info.get("lift", ()):
        return f"{group} rotor ({title})"
    mains = info.get("mains") or [[j] for j in info["lift"]]
    k = next(n for n, c in enumerate(mains) if i in c)
    side = {"left": "L", "right": "R", "center": "C"}.get(group or "center", "C")
    return f"{side}{k + 1} ({title})"


def _spin_axis(part, srf, rf, sc):
    """(up_cos_signed, ax_in_vessel_frame): the part-local axis most aligned with world-up.
    BG rotor hubs don't always face along their spin axis, so we try local ±X/±Y/±Z via transform_direction."""
    best_abs, best_up, best_ax = -1.0, 0.0, (0.0, 0.0, -1.0)
    try:
        pref = part.reference_frame
    except Exception:  # noqa: BLE001
        pref = None
    locals_ = ((0.0, 0.0, -1.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0), (0.0, -1.0, 0.0),
               (1.0, 0.0, 0.0), (-1.0, 0.0, 0.0))
    if sc is not None and pref is not None:
        for loc in locals_:
            try:
                w = sc.transform_direction(loc, pref, srf)
                a = sc.transform_direction(loc, pref, rf)
                up = float(w[0])
                if abs(up) > best_abs:
                    best_abs, best_up, best_ax = abs(up), up, tuple(float(x) for x in a)
            except Exception:  # noqa: BLE001
                pass
    if best_abs < 0.0:
        try:
            d_srf, d_rf = part.direction(srf), part.direction(rf)
            return float(d_srf[0]), tuple(float(x) for x in d_rf)
        except Exception:  # noqa: BLE001
            return 0.0, (0.0, 0.0, -1.0)
    return best_up, best_ax


def _space_center(v=None):
    """Best-effort SpaceCenter for transform_direction (None offline / no conn)."""
    try:
        from . import ksp_actions
        c = ksp_actions.conn()
        if c is not None:
            return c.space_center
    except Exception:  # noqa: BLE001
        pass
    return None


def scan(v):
    """Measure the rotors (kRPC) and classify; cached per craft (name + part count). Never raises."""
    try:
        key = (str(v.name), len(v.parts.all))
    except Exception:  # noqa: BLE001
        return classify([], False)
    if key in _INFO:
        return _INFO[key]
    rots = []
    try:
        lay = pr.layout(v)
        srf, rf = v.surface_reference_frame, v.reference_frame
        sc = _space_center(v)
        for r in lay["rotors"]:
            m = r["mod"]
            d = {"up": 0.0, "ax": (0.0, 0.0, 0.0), "h": (0.0, 0.0), "lat": (r["pos"] or (0.0,))[0], "dir": r["dir"],
                 "power_loss": "", "title": "", "blades": r.get("blades")}
            try:
                d["title"] = m.part.title
                d["up"], d["ax"] = _spin_axis(m.part, srf, rf, sc)
                p = m.part.position(srf)
                d["h"] = (float(p[1]), float(p[2]))
                f = pr.fields(m)
                k = pr.find_field(f, "power", "loss")
                d["power_loss"] = str(f.get(k, "")) if k else ""
            except Exception:  # noqa: BLE001
                pass
            rots.append(d)
    except Exception:  # noqa: BLE001
        pass
    wings = False
    try:
        from . import ksp_actions
        wings = bool(ksp_actions._has_wings(v))
    except Exception:  # noqa: BLE001
        pass
    out = classify(rots, wings)
    out["roles"] = roles_text(out, rots) if rots else ""
    _INFO[key] = out
    if rots:
        log.info("heli: %s -> %s; yaw by %s; roles: %s; rotors %s", key[0], out["kind"] if out["heli"] else "not a heli",
                 out["yaw"], roles_text(out, rots),
                 [(d["title"], round(d["up"], 2), tuple(round(x, 2) for x in d["ax"]), round(d["lat"], 2),
                   d["dir"], d["power_loss"], d["blades"]) for d in rots])
    return out


def is_heli(v):
    try:
        return pr.classify(v)["rotors"] > 0 and scan(v)["heli"]
    except Exception:  # noqa: BLE001
        return False


def sample(v, sc=None):
    """Watcher sample (None if not a heli): main-rotor RPM / limit / motor / brake / torque, tail, AGL, heading,
    track + ground speed (with the space center)."""
    if not is_heli(v):
        return None
    info = scan(v)
    lay = pr.layout(v)
    out = {"rpm": None, "rpm_limit": None, "motor_on": None, "locked": info["locked"], "compound": info["compound"],
           "multirotor": info["multirotor"], "lift": info["lift"], "mains": info["mains"], "kind": info["kind"],
           "tail_rpm": None, "tail_n": len(info["tail"]), "agl": None, "hdg": None,
           "coll": STATUS.get("coll") if active() else None}
    for i in info["lift"] + info["tail"]:
        try:
            live = pr._live_rotor(v, lay["rotors"][i]["mod"])
            f = {"Current RPM": live.get("rpm"), "RPM Limit": live.get("rpm_limit"),
                 "Motor": None if live.get("motor_on") is None else ("Engaged" if live["motor_on"] else "Disengaged"),
                 "Brake": live.get("brake"), "Torque Limit": live.get("torque")}
        except Exception:  # noqa: BLE001
            continue
        k = pr.find_field(f, "current", "rpm")
        rpm = pr.num(f.get(k)) if k else None
        if i in info["tail"]:
            if rpm is not None:
                out["tail_rpm"] = rpm if out["tail_rpm"] is None else min(out["tail_rpm"], rpm)
            continue
        kl, km = pr.find_field(f, "rpm", "limit"), pr.find_field(f, "motor", exclude=("size", "output", "motorized"))
        lim, mot = (pr.num(f.get(kl)) if kl else None), (pr._motor_on(f.get(km)) if km else None)
        if rpm is not None:
            out["rpm"] = rpm if out["rpm"] is None else min(out["rpm"], rpm)
        if lim is not None:
            out["rpm_limit"] = lim if out["rpm_limit"] is None else min(out["rpm_limit"], lim)
        if mot is not None:
            out["motor_on"] = mot if out["motor_on"] is None else out["motor_on"] and mot
        kb = pr.find_field(f, "brake", exclude=("auto",))
        b = pr.num(f.get(kb)) if kb else None
        if b is not None:
            out["brake"] = b if out.get("brake") is None else max(out["brake"], b)
        kt = pr.find_field(f, "torque", "limit")
        tq = pr.num(f.get(kt)) if kt else None
        if tq is not None:
            out["torque"] = tq if out.get("torque") is None else min(out["torque"], tq)
        kp = pr.find_field(f, "power", "loss")  # Luke can change 'On Power Loss' in flight
        if kp:
            out["locked"] = "lock" in str(f.get(kp, "")).lower()
    try:
        out["agl"] = float(v.flight().surface_altitude)
        out["hdg"] = float(v.flight(v.surface_reference_frame).heading)
    except Exception:  # noqa: BLE001
        pass
    if sc is not None:
        try:
            brf = v.orbit.body.reference_frame
            _, n, e = sc.transform_direction(v.flight(brf).velocity, brf, v.surface_reference_frame)
            out["trk"], out["gs"] = track_of(n, e)
        except Exception:  # noqa: BLE001
            pass
    if out["coll"] is None:
        try:
            ps = []
            for b in lay["blades"]:
                if b["rotor"] in info["lift"] and len(ps) < 4:
                    f = pr.fields(b["mod"])
                    k = pr.find_field(f, "deploy", "angle")
                    x = pr.num(f.get(k)) if k else None
                    if x is not None:
                        ps.append(abs(x))
            out["coll"] = sum(ps) / len(ps) if ps else None
        except Exception:  # noqa: BLE001
            pass
    return out


# ======================================================================== control laws (pure)
class VsPI:
    """Collective (deg) from vertical-speed error; on the ground it spools up until the craft climbs."""

    def __init__(self, i0=0.0):
        self.i = self.out = i0

    def step(self, vs_t, vs, dt, on_ground=False, lo=COLL_MIN, hi=COLL_MAX):
        e = vs_t - vs
        if on_ground and vs_t > 0 and vs < 0.3:
            self.i += GROUND_SPOOL * dt
        else:
            self.i += KI * e * dt
        self.i = _clamp(self.i, lo, hi)
        want = _clamp(self.i + KP * e, lo, hi)
        self.out += _clamp(want - self.out, -COLL_RATE * dt, COLL_RATE * dt)
        return self.out


def vs_target(mode, agl, alt_t):
    if mode == "land":
        return -3.0 if agl > 30 else (-2.0 if agl > 10 else (-1.0 if agl > 3 else -0.7))
    if mode == "glide":
        return -1.5 if agl < 10 else GLIDE_VS
    if alt_t is None:
        return 0.0
    return _clamp(ALT_K * (alt_t - agl), -VS_DN, VS_UP)


def autorot_collective(agl, vs, rpm, rpm_ref):
    """Low collective keeps the rotor turning; near the ground the stored rotor energy cushions the touchdown."""
    if agl < CUSHION_AGL and vs < -1.0:
        return 0.7 * COLL_MAX
    return _clamp(COLL_AUTOROT + 0.02 * (rpm - 0.9 * rpm_ref), 0.0, AUTOROT_MAX)


def body_vel(north, east, hdg):
    """(forward, right) m/s for a heading."""
    h = math.radians(hdg)
    return north * math.cos(h) + east * math.sin(h), -north * math.sin(h) + east * math.cos(h)


def hold_vel(err_n, err_e, hdg):
    """Velocity target (forward, right) toward a hold point (errors in m, north / east), capped HOLD_VMAX."""
    vn, ve = HOLD_K * err_n, HOLD_K * err_e
    n = math.hypot(vn, ve)
    if n > HOLD_VMAX:
        vn, ve = vn * HOLD_VMAX / n, ve * HOLD_VMAX / n
    return body_vel(vn, ve, hdg)


def hold_ne(err_n, err_e):
    """Ground velocity target (north, east) toward a hold point, capped HOLD_VMAX."""
    vn, ve = HOLD_K * err_n, HOLD_K * err_e
    n = math.hypot(vn, ve)
    if n > HOLD_VMAX:
        vn, ve = vn * HOLD_VMAX / n, ve * HOLD_VMAX / n
    return vn, ve


def track_of(north, east):
    """(track deg, ground speed) from the horizontal velocity."""
    return math.degrees(math.atan2(east, north)) % 360.0, math.hypot(north, east)


def tilt(v_t, v, cap=TILT_MAX):
    return _clamp(VEL_K * (v_t - v), -cap, cap)


class YawCtl:
    """u in [-1, 1] (+ = nose right) from heading error and turn rate, with a small integral for the steady main-rotor
    torque reaction."""

    def __init__(self):
        self.i = 0.0

    def step(self, herr, rate, dt):
        self.i = _clamp(self.i + YAW_KI * herr * dt, -YAW_IMAX, YAW_IMAX)
        return _clamp(YAW_KP * herr - YAW_KD * rate + self.i, -1.0, 1.0)


class YawSign:
    """Checks the yaw actuator sign online: over 1 s windows with a steady command |u| > 0.3, the turn-rate change
    should follow u. Three wrong windows in a row flip the sign (logged)."""

    def __init__(self):
        self.sign, self.bad, self.win = 1.0, 0, []

    def feed(self, t, u, rate):
        self.win.append((t, u, rate))
        t0, u0, r0 = self.win[0]
        if t - t0 < 1.0:
            return False
        us = [x[1] for x in self.win]
        self.win = [(t, u, rate)]
        if min(abs(x) for x in us) < 0.3 or (max(us) > 0) != (min(us) > 0):
            return False
        acc = (rate - r0) / (t - t0)
        if acc * u0 < -2.0:
            self.bad += 1
        elif acc * u0 > 2.0:
            self.bad = 0
        if self.bad >= 3:
            self.sign, self.bad = -self.sign, 0
            return True
        return False


class SidePI:
    """Compound: side-prop blade pitch (deg) for a forward-speed target = inflow angle + PI thrust term."""

    def __init__(self):
        self.i = 0.0

    def step(self, fwd_t, fwd, dt, rpm, radius):
        e = fwd_t - fwd
        self.i = _clamp(self.i + SIDE_KI * e * dt, SIDE_LO, SIDE_HI)
        return pr.inflow_deg(max(fwd, 0.0), rpm, radius) + _clamp(SIDE_KP * e + self.i, SIDE_LO, SIDE_HI)


def side_pitches(base, u, sign=1.0):
    """(left, right) side-prop pitch: + u (nose right) = the left prop pushes more."""
    d = SIDE_YAW_DEG * sign * u
    return base + d, base - d


def plan(mode, agl, vs, fwd, right, hdg, st, em, info, rpm=None, rpm_ref=None):
    """One control decision (pure). st = targets {alt, heading, speed, hold_err (n, e) | None, goto (dist, brg) | None};
    em = set of active emergency keys. -> {mode, vs_t, fwd_t, right_t, hdg_t, pitch_t, roll_t, coll_lo, autorot,
    land_now, note}."""
    out = {"mode": mode, "vs_t": 0.0, "fwd_t": 0.0, "right_t": 0.0, "hdg_t": st.get("heading"), "pitch_t": 0.0,
           "roll_t": 0.0, "coll_lo": COLL_MIN, "autorot": False, "note": "", "trk_t": None, "gs_t": 0.0}
    if out["hdg_t"] is None:
        out["hdg_t"] = hdg
    compound = info.get("compound")
    if "heli_rpm" in em:
        if info.get("locked") or compound:
            mode, out["note"] = "glide", "main rotor power lost (Locked on power loss): gliding on side props + wings"
        else:
            out["autorot"], out["note"] = True, "autorotation"
            mode = "land"
    elif "heli_tail" in em or (compound and ("prop_out:left" in em or "prop_out:right" in em)):
        mode, out["note"] = "land", "yaw control lost: landing"
    out["mode"] = mode
    out["vs_t"] = vs_target(mode, agl, st.get("alt"))
    # ground velocity target (north, east): track + speed, or toward the hold point
    vn = ve = 0.0
    if mode == "glide":
        trk, gs = hdg, (GLIDE_SPD if agl > 10 else 15.0)
    elif mode == "fly":
        trk, gs = (st.get("track") if st.get("track") is not None else hdg), _clamp(float(st.get("speed") or 0.0), 0.0, SPD_MAX)
    elif mode == "goto" and st.get("goto"):
        d, trk = st["goto"]
        gs = _clamp(min(float(st.get("speed") or 20.0), max(2.0, 0.05 * d)), 0.0, SPD_MAX)
    else:
        trk, gs = None, 0.0
        if st.get("hold_err") is not None:
            vn, ve = hold_ne(st["hold_err"][0], st["hold_err"][1])
    if trk is not None:
        vn, ve = gs * math.cos(math.radians(trk)), gs * math.sin(math.radians(trk))
        out["trk_t"], out["gs_t"] = trk % 360.0, gs
    # nose: pinned by 'face N', else follows the track when fast, else holds
    if st.get("face") is not None:
        out["hdg_t"] = st["face"] % 360.0
    elif trk is not None and gs > NOSE_FOLLOW:
        out["hdg_t"] = trk % 360.0
    # resolve into the nose's axes (forward / right) at the CURRENT heading
    out["fwd_t"], out["right_t"] = body_vel(vn, ve, hdg)
    if "heli_tail" in em and not compound and agl > 25:
        out["fwd_t"] = max(out["fwd_t"], 20.0)  # weathervane on the fuselage before the landing
    if "heli_vrs" in em:
        out["vs_t"] = max(out["vs_t"], -2.0)
        out["fwd_t"] = max(out["fwd_t"], 12.0)
    # helicopter speed law (NOT the plane one): nose down = accelerate, nose up = slow / stop; height = collective
    if compound and mode == "glide":
        out["pitch_t"] = GLIDE_PITCH
    else:
        out["pitch_t"] = -tilt(out["fwd_t"], fwd, COMPOUND_PITCH if compound else TILT_MAX)
    out["roll_t"] = tilt(out["right_t"], right)
    if out["autorot"]:
        out["coll_lo"] = 0.0
        if agl < FLARE_AGL and fwd > 5.0:
            out["pitch_t"] = FLARE_PITCH
        if agl < 3.0:
            out["pitch_t"] = 0.0
    if "inverted_air" in em:  # rotor thrust points at the ground: collective down, level the disc
        out.update(pitch_t=0.0, roll_t=0.0, coll_lo=0.0, vs_t=-99.0, note="inverted: collective down, levelling")
    return out


# ======================================================================== commands / thread
def active():
    return _thread is not None and _thread.is_alive()


def clear_vessel_cache():
    """Forget cached rotor scan / classification; stop heli autopilot tied to the old craft."""
    _INFO.clear()
    if active():
        stop()
    STATE.pop("spooled", None)
    STATE.update(mode="off", alt=None, heading=None, track=None, face=None, speed=None, hold=None, goto=None, t=0.0)
    try:
        from . import propulsion
        propulsion.reset_spool_state()
    except Exception:  # noqa: BLE001
        pass


def stop():
    _stop.set()


def command(mode, alt=None, heading=None, speed=None, goto=None, hold=None, track=None, face=None):
    """Set the heli targets (None = keep; face="clear" unpins the nose) and start the controller if needed.
    heading = nose heading to hold when not following the track. -> 'started' / 'updated'."""
    STATE["mode"] = mode
    for k, val in (("alt", alt), ("heading", heading), ("speed", speed), ("goto", goto), ("hold", hold),
                   ("track", track)):
        if val is not None:
            STATE[k] = val
    if face is not None:
        STATE["face"] = None if face == "clear" else float(face) % 360.0
    if mode in ("hover", "climb", "descend", "hold", "land"):
        STATE["goto"] = None
    if mode in ("fly", "goto", "land"):
        STATE["hold"] = None if mode != "land" else STATE["hold"]
    STATE["t"] = time.time()
    if active():
        return "updated"
    global _thread
    _stop.clear()
    _thread = threading.Thread(target=_run, daemon=True, name="heli")
    _thread.start()
    return "started"


def _run():
    import krpc
    STATUS.clear()
    STATUS.update(phase="starting", started=time.time())
    end = guard.hold("heli", _stop, lambda: {k: STATUS.get(k) for k in ("phase", "agl", "vs", "coll")})
    conn = None
    try:
        conn = krpc.connect(name="KSPChatBridge-heli", address=config.KRPC_ADDRESS,
                            rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)
        result = _fly(conn)
    except Exception as e:  # noqa: BLE001
        result = f"Heli autopilot error: {e.__class__.__name__}: {e}"
        log.exception("heli")
    end()
    STATUS["phase"] = "off"
    STATUS["result"] = result
    STATE["mode"] = "off"
    log.info("heli: %s", result)
    try:
        from . import science
        science.post_event(result)
    except Exception:  # noqa: BLE001
        pass
    if conn:
        try:
            conn.close()
        except Exception:  # noqa: BLE001
            pass


def _post(text):
    try:
        from . import science
        science.post_event(text)
    except Exception:  # noqa: BLE001
        pass


def _park(v, lift):
    """Optional (setting 'rotor_brake_park', default off): after landing, torque 0, wait for the RPM to drop, then
    apply the rotor Brake. Ground only. -> '' or a short note."""
    try:
        from . import settings
        if not settings.get("rotor_brake_park"):
            return ""
    except Exception:  # noqa: BLE001
        return ""
    pr.set_rotor(v, torque=0.0, rotors=lift)
    t0 = time.time()
    rpm = 1e9
    while time.time() - t0 < PARK_WAIT_S and not _stop.is_set():
        if str(v.situation).split(".")[-1] not in ("landed", "splashed", "pre_launch"):
            return "Parking stopped: we're not on the ground. "
        rpm = min((c["rpm"] or 0.0) for c in pr.rotor_checks(v) if c["i"] in lift)
        if rpm < PARK_RPM:
            break
        time.sleep(1.0)
    if rpm >= PARK_RPM:
        return f"Rotor still at {rpm:.0f} RPM - brake not applied. "
    pr.set_brake(v, 100.0, rotors=lift)
    log.info("heli: parked - torque 0, main rotor Brake 100 at %.0f RPM", rpm)
    return "Parked: rotor brake on. "


def _write(v, cache, key, value, fn, step):
    if cache.get(key) is None or abs(cache[key] - value) >= step:
        cache[key] = value
        return fn(value)
    return None


def _fly(conn):
    from . import emergency, spots
    sc = conn.space_center
    v = sc.active_vessel
    info = scan(v)
    if not info["heli"]:
        return "Heli autopilot: this craft isn't a helicopter."
    body = v.orbit.body
    brf, srf = body.reference_frame, v.surface_reference_frame
    f = v.flight(brf)
    lift, side_l, side_r, tail = set(info["lift"]), set(info["left"]), set(info["right"]), set(info["tail"])
    log.info("heli: engage %s (%s, yaw by %s; Locked on power loss: %s)", v.name, info["kind"], info["yaw"],
             info["locked"])
    pre, probs = pr.preflight(v)  # Luke: Brake 0, Torque Limit > 0, Motor Engaged on EVERY rotor
    if probs:
        _post(f"Heli {pre}")
    pr.remember_sense(v)  # baseline spin direction / invert before we lift
    # blades out at zero collective until EVERY lift rotor is spinning (altitude = collective, not throttle)
    log.info("heli: lift blades %s", pr.set_blades(v, pitch=0.0, deploy=True, rotors=lift))
    others = side_l | side_r | tail
    if others:
        log.info("heli: side/tail blades %s", pr.set_blades(v, pitch=0.0, deploy=True, rotors=others))
    tail_base = None
    if tail:
        try:
            b = next(b for b in pr.layout(v)["blades"] if b["rotor"] in tail)
            fl = pr.fields(b["mod"])
            tail_base = pr.num(fl.get(pr.find_field(fl, "deploy", "angle"))) or 0.0
        except Exception:  # noqa: BLE001
            tail_base = 0.0
    p0 = float(v.flight(srf).pitch)
    vab = abs(p0) > 60.0  # control point looks up (VAB-built): AutoPilot pitch/heading don't map - vertical only
    ap = v.auto_pilot
    if vab:
        log.info("heli: control point points up (pitch %.0f) - SAS + vertical control only, no translation", p0)
        v.control.sas = True
    else:
        ap.reference_frame = srf
        krpcx.autopilot(ap, True)  # kRPC 0.6: no engage() - the `engaged` property
    sit = str(v.situation).split(".")[-1]
    if sit in ("landed", "pre_launch", "splashed"):
        # heli_control may already have spooled; skip the long ramp if every lift rotor is already up
        already = STATE.pop("spooled", False)
        if already:
            checks = [c for c in pr.rotor_checks(v) if c["i"] in lift]
            need = lambda c: pr.SPOOL_FRAC * float(c["rpm_limit"] or pr.RPM_MAX)  # noqa: E731
            already = bool(checks) and all(
                float(c["rpm"] or 0) >= need(c) and c.get("motor_on") is not False
                and not (c.get("brake") or 0) > 0 for c in checks)
        if already:
            log.info("heli: spool-up already done in heli_control - keeping RPM, collective still zero")
            pr.set_rotor(v, rpm=pr.RPM_MAX, torque=pr.TORQUE_MAX, motor=True, rotors=lift)
            if others:
                pr.set_rotor(v, rpm=pr.RPM_MAX, torque=pr.TORQUE_MAX, motor=True, rotors=others)
        else:
            ok, report = pr.spool_up(v, rotors=lift, stop_event=_stop)
            log.info("heli: spool-up lift: %s", report)
            if not ok:
                return f"Heli takeoff aborted: {report}"
            if others:
                ok2, report2 = pr.spool_up(v, rotors=others, stop_event=_stop)
                log.info("heli: spool-up side/tail: %s", report2)
                if not ok2:
                    return f"Heli takeoff aborted (side/tail): {report2}"
    else:
        log.info("heli: lift %s", pr.set_rotor(v, rpm=pr.RPM_MAX, torque=pr.TORQUE_MAX, motor=True, rotors=lift))
        if others:
            log.info("heli: side/tail %s", pr.set_rotor(v, rpm=pr.RPM_MAX, torque=pr.TORQUE_MAX, motor=True, rotors=others))
    pi = VsPI(0.0 if sit in ("landed", "pre_launch", "splashed") else COLL_START)
    yaw, ysign, side = YawCtl(), YawSign(), SidePI()
    radius = pr.blade_radius(v)
    cache, last_hdg, last_t, landed_t = {}, None, time.time(), None
    rpm_ref = pr.RPM_MAX
    if STATE.get("heading") is None:
        STATE["heading"] = float(v.flight(srf).heading)
    try:
        while not _stop.is_set():
            time.sleep(DT)
            now = time.time()
            dt = max(0.05, now - last_t)
            last_t = now
            sit = str(v.situation).split(".")[-1]
            fs = v.flight(srf)
            agl, vs, hdg = float(f.surface_altitude), float(f.vertical_speed), float(fs.heading)
            up, north, east = sc.transform_direction(f.velocity, brf, srf)
            fwd, right = body_vel(north, east, hdg)
            rate = 0.0 if last_hdg is None else _wrap(hdg - last_hdg) / dt
            last_hdg = hdg
            mode = STATE["mode"]
            st = {"alt": STATE.get("alt"), "heading": STATE.get("heading"), "speed": STATE.get("speed"),
                  "track": STATE.get("track"), "face": STATE.get("face")}
            lat, lon = float(f.latitude), float(f.longitude)
            if mode in ("hover", "climb", "descend", "hold") and STATE.get("hold") is None:
                STATE["hold"] = (lat, lon)
            if STATE.get("hold") and mode not in ("fly", "goto"):
                d = spots.gc_dist(lat, lon, *STATE["hold"], body.equatorial_radius)
                b = math.radians(spots.bearing(lat, lon, *STATE["hold"]))
                st["hold_err"] = (d * math.cos(b), d * math.sin(b))
            if mode == "goto" and STATE.get("goto"):
                glat, glon = STATE["goto"]
                d = spots.gc_dist(lat, lon, glat, glon, body.equatorial_radius)
                st["goto"] = (d, spots.bearing(lat, lon, glat, glon))
                if d < GOTO_ARRIVE:
                    STATE.update(mode="hold", hold=(glat, glon), goto=None)
                    log.info("heli: arrived - holding position")
            em = set(emergency.DET.active)
            rpm = pr.rotor_rpm(v) if "heli_rpm" in em else None
            p = plan(mode, agl, vs, fwd, right, hdg, st, em, info, rpm, rpm_ref)
            on_ground = sit in ("landed", "pre_launch", "splashed")
            # --- touchdown
            if p["mode"] == "land" and on_ground:
                landed_t = landed_t or now
                pi.out = max(0.0, pi.out - COLL_RATE * dt)
                _write(v, cache, "coll", pi.out, lambda x: pr.set_blades(v, pitch=x, rotors=lift), 0.25)
                if now - landed_t > 3.0 and pi.out <= 0.0:
                    park = _park(v, lift)
                    td = STATUS.get("vs_td", vs)
                    if info.get("multirotor"):
                        pr.set_rotor(v, torque=0.0, motor=False, rotors=lift)
                        return park + (f"Multirotor landed (touchdown {td:+.1f} m/s); collective down, motors disengaged.")
                    return park + (f"Helicopter landed (touchdown {td:+.1f} m/s); collective down, rotors still turning.")
                continue
            landed_t = None
            if p["mode"] == "land" and agl < 1.5:
                STATUS["vs_td"] = vs
            # --- collective
            if p["autorot"]:
                coll = autorot_collective(agl, vs, rpm or 0.0, rpm_ref)
                pi.out = pi.i = coll
            else:
                coll = pi.step(p["vs_t"], vs, dt, on_ground, lo=0.0 if on_ground else p["coll_lo"])
            _write(v, cache, "coll", coll, lambda x: pr.set_blades(v, pitch=x, rotors=lift), 0.25)
            # --- attitude
            if not vab:
                ap.target_pitch = p["pitch_t"] if not on_ground else 0.0
                ap.target_heading = p["hdg_t"] % 360.0
                ap.target_roll = p["roll_t"] if not on_ground else 0.0
            # --- yaw actuators
            u = yaw.step(_wrap(p["hdg_t"] - hdg), rate, dt) if not on_ground else 0.0
            if ysign.feed(now, u, rate):
                log.warning("heli: yaw actuator sign flipped -> %+.0f (turn rate went against the command)", ysign.sign)
            if info["compound"]:
                rpm_s = pr.RPM_MAX
                base = side.step(p["fwd_t"], fwd, dt, rpm_s, radius) if not on_ground else 0.0
                pl, prr = side_pitches(base, u, ysign.sign)
                _write(v, cache, "side_l", pl, lambda x: pr.set_blades(v, pitch=x, allow_reverse=True, rotors=side_l), 0.5)
                _write(v, cache, "side_r", prr, lambda x: pr.set_blades(v, pitch=x, allow_reverse=True, rotors=side_r), 0.5)
            elif tail:
                tp = tail_base + TAIL_YAW_DEG * ysign.sign * u
                _write(v, cache, "tail", tp, lambda x: pr.set_blades(v, pitch=x, allow_reverse=True, rotors=tail), 0.5)
            elif info["counter"] and len(lift) >= 2 and (info.get("multirotor") or info["coaxial"]):
                # counter-rotating pairs: more torque on one spin sense yaws the craft
                lay = pr.layout(v)["rotors"]
                for i in lift:
                    if i >= len(lay):
                        log.warning("heli: lift index %s out of range (layout has %s rotors)", i, len(lay))
                        continue
                    dq = COAX_YAW_TQ * ysign.sign * u * (lay[i].get("dir") or 1)
                    _write(v, cache, f"tq{i}", 90.0 + dq, lambda x, i=i: pr.set_rotor(v, torque=x, rotors={i}), 2.0)
            if p["hdg_t"] is not None and STATE.get("face") is None:
                STATE["heading"] = p["hdg_t"]  # a nose that followed the track stays there when we slow down
            trk, gs = track_of(north, east)
            STATUS.update(trk=trk, gs=gs, trk_t=p["trk_t"], gs_t=p["gs_t"], right=right, right_t=p["right_t"],
                          drift=_wrap(trk - hdg) if gs > 1.0 else 0.0)
            STATUS.update(phase=("heli " + p["mode"]) + (f" ({p['note']})" if p["note"] else ""), agl=agl, vs=vs,
                          vs_t=p["vs_t"], coll=coll, fwd=fwd, fwd_t=p["fwd_t"], hdg=hdg, hdg_t=p["hdg_t"],
                          pitch_t=p["pitch_t"], roll_t=p["roll_t"], yaw_u=u, kind=info["kind"])
            if not on_ground and p["mode"] in ("hover", "hold", "fly"):
                try:
                    from . import trim_auto
                    trim_auto.maybe_trim_heli(v, info, coll, rate, vs, float(fs.roll) if hasattr(fs, "roll") else 0.0,
                                              math.hypot(fwd, right), now)
                except Exception:  # noqa: BLE001
                    pass
            if int(now / 2.0) != int((now - dt) / 2.0):
                log.info("heli: %s agl %.1f vs %+.1f->%+.1f coll %.1f fwd %.1f->%.1f hdg %.0f->%.0f u %+.2f",
                         p["mode"], agl, vs, p["vs_t"], coll, fwd, p["fwd_t"], hdg, p["hdg_t"], u)
        return "Heli autopilot stopped."
    finally:
        try:
            if not vab:
                krpcx.autopilot(ap, False)
        except Exception:  # noqa: BLE001
            log.exception("heli: autopilot off")
        try:
            v.control.sas = True
        except Exception:  # noqa: BLE001
            pass
