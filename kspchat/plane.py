"""BASELINE (Luke, 2026-10-08 06:36: "perfect, by the book" - Aeris 3A, runway 27, sink 15 m -4.5 / 5 m -4.2 /
2 m -3.9 / touchdown -3.1 m/s at 37 m/s, 7 m off centerline, 0 bounces). Keep these constants as the default.

Aircraft autoland for the KSC runway (own controller; MechJeb's spaceplane autoland is unreliable).

Runs in a background thread with its own kRPC connection (like lander.py).
Phases: to_entry (fly to a point S_ENTRY out on the extended centerline) -> final (track centerline with a
look-ahead carrot, 3 deg glideslope, approach speed, gear down) -> flare (sink rate ~0.6-1.5 m/s, idle)
-> rollout (brakes, rudder/wheel steering on the centerline) -> stopped.
Bank-to-turn heading control and a pitch/vertical-speed cascade drive ctl.roll / ctl.pitch directly (SAS off).
"""
import logging
import math
import threading
import time
from collections import deque

from . import config, emergency, guard, propulsion, protect, reversers, rollout, speedcap
from . import takeoff as tko

log = logging.getLogger("kspchat")

# KSC runway thresholds (lat, lon) and surface altitude (m ASL); 09 = land eastbound, 27 = land westbound
RWY_W = (-0.0485997, -74.724375)
RWY_E = (-0.0502119, -74.490300)
RWY_ALT = 69.1  # measured: terrain under the 09 threshold
# Runway strips: thresholds a/b (lat, lon), names = (landing a->b, landing b->a). alt None = measure terrain.
STRIPS = {
    "KSC": {"body": "Kerbin", "a": RWY_W, "b": RWY_E, "alt": RWY_ALT, "names": ("09", "27")},
}
GLIDE_DEG = 3.0        # minimum; the actual path is set so the final sinks ~4.5 m/s at approach speed (Luke)
FINAL_SINK = 4.5       # m/s steady sink on final above 15 m (Luke: -4.0 to -5.0)
AIM_M = 350.0          # touchdown aim point past the threshold
S_ENTRY = 11_000.0     # m out on the extended centerline where the final approach starts
CARROT_M = 1_800.0     # look-ahead along the centerline for lateral tracking
FLARE_H = 15.0         # m radar height of the wheels: 15->5 m transition, 5->2 m catch (power + nose up), touchdown
MAX_BANK = 20.0       # low speed: 15-20 deg bank allowed (Luke)
BANK_FAST = 15.0      # above 250 m/s (Luke live: 10 made a ~100 km dash turnaround; push to ~15)
CLIMB_VS = 50.0       # m/s climb target on a cruise climb, chased with the THROTTLE (Luke: 50-60), speed ignored
# Luke's speed limits, as EQUIVALENT airspeed (sqrt(2q/rho0)): 200 m/s target / 220 m/s HARD cap down low (Luke, raised from 170/190);
# the true-airspeed limit rises with altitude as the air thins (EAS 170 at ~12 km is ~400 m/s TAS -> supersonic ok)
V_LOW = 200.0         #   target / safety speed (EAS) - Luke raised 170 -> 200
V_HARD = 220.0        #   HARD cap (EAS), never exceeded: cut throttle, pitch up gently to bleed (was 190)
BANK_UPSET = 60.0     # bank beyond this = upset: cut pitch/yaw inputs and level the wings first
HDG_K = 0.2            # 1/s: commanded turn rate per degree of heading error (5 s heading time constant)
MAX_TURN = 3.0         # deg/s max commanded turn rate (standard-rate turn)
HDG_K_FINAL = 0.12     # 1/s on an established final (gentler)
TURN_KD = 0.5          # turn-rate damping (pushes against overshooting turn rate)
BANK_SLEW = 5.0        # deg/s max change of the commanded bank
# output slew limits (fraction of full deflection per second) - small, gentle inputs, no yanking
SLEW = {"pitch": 0.6, "roll": 0.8, "yaw": 0.8, "throttle": 0.15}
THR_SLEW_FLARE = 0.35  # the flare cushion burst may move a bit quicker (still smooth)
PITCH_RATE = 2.5       # deg/s max change of the commanded pitch attitude (all phases)
G_MAX = 1.4            # don't pull the nose up further above this load factor
AOA_STALL = 15.0       # deg: AoA treated as the usable limit ("stall") in the slow-flight test
VAPP_FACTOR = 1.3      # final approach speed = 1.3 x stall (flaps down)
VTD_FACTOR = 1.15      # short final / touchdown target = 1.15 x stall
# airliner-style flap settings with a max-extend speed ("placard") as a multiple of the flaps-down stall speed;
# a setting is never extended above its placard speed and is retracted if we go faster (KSP rips flaps/wings off)
FLAP_PLACARD = ((1.0, 2.4), (5.0, 2.0), (15.0, 1.7), (25.0, 1.45), (30.0, 1.35))
DEFAULT_STALL = 45.0   # m/s guess used only until the slow-flight test has measured the real one


def flap_limit(spd, vs_stall):
    """Largest flap setting allowed at this speed."""
    best = 0.0
    for deg, k in FLAP_PLACARD:
        if spd <= k * vs_stall:
            best = deg
    return best


class Flaps:
    """Flaps = roll-only control surfaces (ailerons/flaperons); deploy angle set via the module field."""

    def __init__(self, vessel):
        # Only DEDICATED flaps: parts titled "flap", or control surfaces with no pitch/yaw/roll authority that the
        # player marked with Deploy in the editor. Elevons/ailerons are never used as flaps.
        self.mods = []
        for cs in vessel.parts.control_surfaces:
            try:
                m = next(m for m in cs.part.modules if m.name == "ModuleControlSurface")
                titled = "flap" in cs.part.title.lower()
                marked = (str(m.get_field("Deploy")).lower() == "true"
                          and not (cs.pitch_enabled or cs.yaw_enabled or cs.roll_enabled))
                if titled or marked:
                    self.mods.append((cs, m))
            except Exception:
                pass
        self.deg = 0.0
        STATUS["flap_parts"] = len(self.mods)

    def set(self, deg):
        if abs(deg - self.deg) < 0.1 or not self.mods:
            return
        for cs, m in self.mods:
            try:
                if deg > 0:
                    m.set_field_float("Deploy Angle", float(deg))
                cs.deployed = deg > 0
            except Exception:
                pass
        self.deg = float(deg)
        STATUS["flaps_deg"] = self.deg

    def schedule(self, spd, vs_stall, want, allow_extend=True):
        """Step toward `want`, but never beyond what the speed allows (retract at once if too fast)."""
        lim = flap_limit(spd, vs_stall)
        target = min(want, lim)
        steps = [0.0] + [d for d, _ in FLAP_PLACARD]
        if target > self.deg and allow_extend:  # extend one step at a time
            nxt = min(d for d in steps if d > self.deg)
            self.set(min(nxt, target) if nxt <= target else self.deg)
        elif target < self.deg:
            if lim < self.deg:  # too fast for this setting: retract at once (placard guard)
                self.set(max(d for d in steps if d <= lim))
            elif allow_extend:  # otherwise retract one step at a time
                self.set(max(target, max(d for d in steps if d < self.deg)))
ROLL_SIGN = 1.0        # probed (Aeris 3A): +0.25 roll for 0.5 s -> +40 deg bank (right)
PITCH_SIGN = 1.0       # probed: +0.25 pitch for 0.5 s -> +9 deg nose up

STATUS = {"phase": "idle"}
# live changes from chat while the autopilot flies (ksp_actions.set_speed / set_altitude / set_override): vcruise =
# en-route speed (m/s), cruise_alt = en-route cruise altitude (m MSL), v_final = speed on final / flare (player's call)
LIVE = {}
TRACE = deque(maxlen=4000)
_thread = None
_stop = threading.Event()


def active():
    return _thread is not None and _thread.is_alive()


def stop():
    _stop.set()


def start(runway=None, approach_speed=None, cruise_speed=None, strip=None, cruise_alt=None, touch_and_go=0):
    """strip: a STRIPS-style dict (default the KSC runway)."""
    global _thread
    if active():
        return "A plane landing is already in progress."
    _stop.clear()
    LIVE.clear()
    protect.ensure()  # G / heat / breakage watcher + blackout handling
    _thread = threading.Thread(target=_run, args=(runway, approach_speed, cruise_speed, strip or STRIPS["KSC"], cruise_alt,
                                                  touch_and_go),
                               daemon=True, name="plane")
    _thread.start()
    return "started"


def _notify(text):
    try:
        from . import science
        science.post_event(text)
    except Exception:
        pass


def _run(runway, vapp, vcruise, strip, cruise_alt=None, touch_and_go=0):
    import krpc
    STATUS.clear()
    STATUS.update(phase="starting", started=time.time())
    end = guard.hold("plane", _stop, lambda: {k: STATUS.get(k) for k in ("phase", "runway", "threshold", "entry", "approach_speed", "hdg_err", "hdg_des")})
    conn = None
    try:
        conn = krpc.connect(name="KSPChatBridge-plane", address=config.KRPC_ADDRESS,
                            rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)
        result = _fly(conn, runway, vapp, vcruise, strip, cruise_alt, touch_and_go)
    except Exception as e:
        result = f"Plane autoland error: {e.__class__.__name__}: {e}"
        try:
            c = conn.space_center.active_vessel.control
            c.throttle = 0
            c.sas = True
        except Exception:
            pass
    if reversers.engaged():  # never leave the engines reversed (error / timeout)
        try:
            c = conn.space_center.active_vessel
            c.control.throttle = 0
            reversers.release(c)
        except Exception:  # noqa: BLE001
            reversers._ENGAGED.clear()
    end()
    STATUS["phase"] = "done"
    STATUS["result"] = result
    log.info("plane: %s", result)
    _notify(result)
    if conn:
        try:
            conn.close()
        except Exception:
            pass


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _unit(a):
    n = math.sqrt(_dot(a, a)) or 1.0
    return (a[0] / n, a[1] / n, a[2] / n)


def _wrap(a):
    return (a + 180.0) % 360.0 - 180.0


def _clamp(x, lo, hi):
    return max(lo, min(hi, x))


class Geo:
    """Local flat frame around the runway built from kRPC body-frame positions (no cross products:
    kRPC frames are left-handed, so east/north come from finite differences in lat/lon)."""

    def __init__(self, body, bref, a=RWY_W, b=RWY_E):
        self.body, self.bref = body, bref
        latm, lonm = (a[0] + b[0]) / 2, (a[1] + b[1]) / 2
        p0 = body.surface_position(latm, lonm, bref)
        self.o = p0
        self.e = _unit(_sub(body.surface_position(latm, lonm + 0.01, bref), p0))
        self.n = _unit(_sub(body.surface_position(latm + 0.01, lonm, bref), p0))
        self.w = self.xy(body.surface_position(*a, bref))      # threshold a ("w" = 09 end at KSC)
        self.east = self.xy(body.surface_position(*b, bref))   # threshold b

    def xy(self, p):
        d = _sub(p, self.o)
        return (_dot(d, self.e), _dot(d, self.n))


def _bearing(dx, dy):
    return math.degrees(math.atan2(dx, dy)) % 360.0


def _grade(td, lost, on_rwy):
    """A-F grade for one touchdown."""
    sink, xt, b = abs(td.get("vs", 0)), abs(td.get("xt", 0)), td.get("bounces", 0)
    if lost:
        return "F"
    if not on_rwy:
        return "D"
    if sink < 1.0 and xt < 5 and not b:
        return "A"
    if sink < 2.0 and xt < 10 and b <= 1:
        return "B"
    if sink < 3.5:
        return "C"
    return "D"


_LOC_NAMES = {"#autoLOC_501174": "Aeris 3A"}  # stock craft spawned via kRPC keep their localization tag as name


def _craft_keys(v):
    """Keys for the per-craft stall-speed cache: name, de-localized name, and a part-count/mass signature."""
    keys = [v.name]
    if v.name in _LOC_NAMES:
        keys.append(_LOC_NAMES[v.name])
    try:
        keys.append(f"sig:{len(v.parts.all)}p/{v.dry_mass / 1000:.1f}t")
    except Exception:  # noqa: BLE001
        pass
    return keys


def _runway_line(body, v, bref, hdg):
    """If we're sitting on a known strip (KSC or a saved H spot), return (lat_a, lon_a, bearing) of its centerline
    in the direction closest to our heading, else None."""
    try:
        from . import spots
        lines = [(s["a"], s["b"]) for s in STRIPS.values()]
        lines += [(tuple(s["a"]), tuple(s["b"])) for s in spots.all_spots().values() if s.get("a") and s.get("b")]
        pos = v.position(bref)
        lat, lon = body.latitude_at_position(pos, bref), body.longitude_at_position(pos, bref)
        k = 2 * math.pi * body.equatorial_radius / 360.0
        for a, b in lines:
            ux, uy = (b[1] - a[1]) * k * math.cos(math.radians(a[0])), (b[0] - a[0]) * k
            ln = math.hypot(ux, uy)
            px, py = (lon - a[1]) * k * math.cos(math.radians(a[0])), (lat - a[0]) * k
            along, cross = (px * ux + py * uy) / ln, (px * uy - py * ux) / ln
            if -300 < along < ln + 300 and abs(cross) < 80:
                brg = math.degrees(math.atan2(ux, uy)) % 360
                for bb in (brg, (brg + 180) % 360):
                    if abs(_wrap(bb - hdg)) < 30:
                        return (a[0], a[1], bb)
    except Exception as e:  # noqa: BLE001
        log.info("runway line lookup failed: %s", e)
    return None


def _runway_ahead(body, v, bref, hdg):
    """Metres of known runway (KSC or a saved H spot) ahead in the direction of hdg; None if not on a known strip."""
    try:
        from . import spots
        lines = [(s["a"], s["b"]) for s in STRIPS.values()]
        lines += [(tuple(s["a"]), tuple(s["b"])) for s in spots.all_spots().values() if s.get("a") and s.get("b")]
        pos = v.position(bref)
        lat, lon = body.latitude_at_position(pos, bref), body.longitude_at_position(pos, bref)
        k = 2 * math.pi * body.equatorial_radius / 360.0
        for a, b in lines:
            ux, uy = (b[1] - a[1]) * k * math.cos(math.radians(a[0])), (b[0] - a[0]) * k
            ln = math.hypot(ux, uy)
            px, py = (lon - a[1]) * k * math.cos(math.radians(a[0])), (lat - a[0]) * k
            along, cross = (px * ux + py * uy) / ln, (px * uy - py * ux) / ln
            if -300 < along < ln + 300 and abs(cross) < 80:
                brg = math.degrees(math.atan2(ux, uy)) % 360
                if abs(_wrap(brg - hdg)) < 30:
                    return tko.runway_remaining(along, ln, True)
                if abs(_wrap(brg + 180 - hdg)) < 30:
                    return tko.runway_remaining(along, ln, False)
    except Exception as e:  # noqa: BLE001
        log.info("runway-ahead lookup failed: %s", e)
    return None


def _line_track(body, v, bref, line):
    lat_a, lon_a, brg = line
    pos = v.position(bref)
    lat, lon = body.latitude_at_position(pos, bref), body.longitude_at_position(pos, bref)
    k = 2 * math.pi * body.equatorial_radius / 360.0
    px, py = (lon - lon_a) * k * math.cos(math.radians(lat_a)), (lat - lat_a) * k
    xt_right = px * math.cos(math.radians(brg)) - py * math.sin(math.radians(brg))
    return brg, xt_right


ROT_RISE_Q = 1.0  # deg/s: nose rising = rotating (no runway-end abort then)


def _abort_text(spd, vr, ahead, rot, need=None):
    why = (f"the nose wouldn't come up (extra up-elevator {100 * rot.extra:.0f}%) - the main gear may sit too far "
           "behind the center of mass, or the elevators/canards lack authority" if spd >= vr else
           f"still below rotation speed ({spd:.0f} of {vr:.0f} m/s)")
    if need:
        why += f", and it can't reach {need:.0f} m/s and rotate before the end"
    return f"Takeoff aborted at {spd:.0f} m/s with {ahead:.0f} m of runway left: {why}. Throttle idle, brakes on."


WHEELS_FALLBACK_S = 8.0  # wheel 'grounded' unreadable / stuck: count the wheels as down after this on the roll


def _wheels_grounded(v):
    """True if every intact wheel reports grounded, False if one doesn't, None if kRPC can't tell."""
    try:
        ws = list(v.parts.wheels)
        if not ws:
            return None
        down = []
        for w in ws:
            try:
                if getattr(w, "broken", False):
                    continue
            except Exception:  # noqa: BLE001
                pass
            down.append(bool(w.grounded))
        return all(down) if down else None
    except Exception:  # noqa: BLE001
        return None


def _takeoff_governor(v, body, now, status, tag):
    """Takeoff throttle governor seeded from the vessel's TWR (kRPC mass / available thrust); logged to bridge.log."""
    try:
        sp = tko.vessel_specs(v, body)
    except Exception as e:  # noqa: BLE001
        log.info("%s: takeoff specs unavailable (%s) - full power", tag, e)
        sp = {"twr": 0.0, "mass_t": 0.0, "avail_kn": 0.0, "max_kn": 0.0, "max_twr": 0.0}
    try:
        titles = [e.part.title for e in v.parts.engines]
    except Exception:  # noqa: BLE001
        titles = []
    weak = tko.weak_low_speed(titles)
    props = propulsion.has_props(v)
    thr0 = tko.start_throttle(sp["twr"], weak or props)
    note = ", props: thrust unreadable - full power, governed by the measured acceleration" if props else \
        (", ramjets: weak low-speed thrust" if weak else "")
    log.info("%s: takeoff TWR %.2f (mass %.1f t, available thrust %.0f kN, max %.0f kN, max TWR %.2f%s) -> start "
             "throttle %.0f%%", tag, sp["twr"], sp["mass_t"], sp["avail_kn"], sp["max_kn"], sp["max_twr"], note,
             100 * thr0)
    status.update(takeoff_twr=round(sp["twr"], 2), takeoff_throttle=thr0)
    return tko.TakeoffThrottle(thr0, now, V_LOW, V_HARD)


def _fly(conn, runway, vapp, vcruise, strip, cruise_alt=None, touch_and_go=0):
    from . import settings as _st
    # final approach length: AICS Approach "Short final (5 km)" / "Long final (11 km, default)" (settings plane_final_m)
    S_ENTRY = float(_st.get("plane_final_m") or 0) or globals()["S_ENTRY"]  # noqa: N806
    STATUS["final_m"] = S_ENTRY
    sc = conn.space_center
    v = sc.active_vessel
    body = v.orbit.body
    if body.name != strip.get("body", "Kerbin"):
        return f"That runway is on {strip.get('body')}, not {body.name}."
    bref = body.reference_frame
    a_ll, b_ll = tuple(strip["a"]), tuple(strip["b"])
    geo = Geo(body, bref, a_ll, b_ll)
    rwy_alt = strip.get("alt")
    if rwy_alt is None:
        rwy_alt = max(body.surface_height(*a_ll), body.surface_height(*b_ll), 0.0)
    names = tuple(strip.get("names") or ("A", "B"))
    STATUS.update(strip=strip.get("name", "KSC"), runway_alt=round(rwy_alt, 1))
    ctl = v.control
    try:
        mj = conn.mech_jeb
        if mj.api_ready:
            mj.ascent_autopilot.enabled = False
    except Exception:
        pass
    try:
        v.auto_pilot.engaged = False
    except Exception:
        pass
    ctl.sas = False

    fb = v.flight(bref)
    fs = v.flight(v.surface_reference_frame)
    s_pos = conn.add_stream(v.position, bref)
    s_alt = conn.add_stream(getattr, fb, "mean_altitude")
    s_ralt = conn.add_stream(getattr, fb, "surface_altitude")
    s_vs = conn.add_stream(getattr, fb, "vertical_speed")
    s_spd = conn.add_stream(getattr, fb, "speed")
    s_pitch = conn.add_stream(getattr, fs, "pitch")
    s_hdg = conn.add_stream(getattr, fs, "heading")
    s_roll = conn.add_stream(getattr, fs, "roll")
    s_sit = conn.add_stream(getattr, v, "situation")
    s_aoa = conn.add_stream(getattr, fb, "angle_of_attack")
    s_q = conn.add_stream(getattr, fb, "dynamic_pressure")
    s_sos = conn.add_stream(getattr, fb, "speed_of_sound")

    def gsched():
        # control gains were tuned at ~60 m/s near sea level; surface authority scales with dynamic
        # pressure, so scale the gains down when fast (a 214 m/s climb-out with sea-level gains rolled
        # the plane inverted). 2205 Pa = q at 60 m/s, sea level.
        return _clamp(2205.0 / max(s_q(), 200.0), 0.05, 2.5)
    # flaps = roll-only control surfaces (ailerons/flaperons); deploying them lowers the stall speed
    airbrakes = any("airbrake" in pt.name.lower() for pt in v.parts.all)
    STATUS.update(airbrakes=airbrakes)

    fl = Flaps(v)
    flap_t = {"t": 0.0}

    bb = v.bounding_box(v.reference_frame)
    # vessel frame: y = nose, z = out of the belly, so the wheels are at max z
    gear_h = max(0.3, bb[1][2])
    STATUS["gear_h"] = round(gear_h, 2)

    x, y = geo.xy(s_pos())
    mid = ((geo.w[0] + geo.east[0]) / 2, (geo.w[1] + geo.east[1]) / 2)
    ax = _unit((geo.east[0] - geo.w[0], geo.east[1] - geo.w[1], 0))
    if runway is None:
        # approach from the side we are on: east of the field -> land westbound (27)
        runway = names[1] if (x - mid[0]) * ax[0] + (y - mid[1]) * ax[1] > 0 else names[0]
    norm = lambda r: str(r).strip().upper().lstrip("0") or "0"
    if norm(runway) == norm(names[0]):
        thr, d = geo.w, (ax[0], ax[1])
        runway = names[0]
    else:
        thr, d = geo.east, (-ax[0], -ax[1])
        runway = names[1]
    rwy_hdg = _bearing(d[0], d[1])
    thr_ll = a_ll if runway == names[0] else b_ll
    back = math.radians(rwy_hdg + 180.0)
    k_m = 180.0 / math.pi / body.equatorial_radius
    rwy_len = math.hypot(geo.east[0] - geo.w[0], geo.east[1] - geo.w[1])
    aim_m = min(AIM_M, 0.3 * rwy_len)  # short strips: touch down earlier
    e_dist = S_ENTRY - aim_m
    entry_ll = (thr_ll[0] + e_dist * math.cos(back) * k_m,
                thr_ll[1] + e_dist * math.sin(back) * k_m / max(0.05, math.cos(math.radians(thr_ll[0]))))
    STATUS.update(threshold=thr_ll, entry=entry_ll)
    aim = (thr[0] + d[0] * aim_m, thr[1] + d[1] * aim_m)
    STATUS.update(runway=runway, runway_heading=round(rwy_hdg, 1))

    # approach speeds adapt per plane: from an explicit approach_speed, a cached stall speed for this craft,
    # or a slow-flight stall test flown at altitude on the way to the final approach
    from . import settings
    cache = settings.get("stall_speeds") or {}
    vs_stall = None
    vapp_given = bool(vapp)
    if vapp:
        vapp = float(vapp)
        vtd = vapp - 5.0
    else:
        for key in _craft_keys(v):
            if cache.get(key):
                vs_stall = float(cache[key])
                break
    def speeds_from_stall(vs0):
        return max(30.0, VAPP_FACTOR * vs0), max(25.0, VTD_FACTOR * vs0)
    if vs_stall:
        vapp, vtd = speeds_from_stall(vs_stall)
    elif not vapp:
        vapp, vtd = 70.0, 62.0  # until the stall test has run
    vcruise_user = float(vcruise or 0)
    vcruise = float(vcruise or max(vapp + 30, 110.0))
    need_stall_test = not vs_stall and not vapp_given and not cruise_alt
    sf = None  # slow-flight state
    STATUS.update(stall_speed=vs_stall, approach_speed=round(vapp, 1), touchdown_target=round(vtd, 1))
    def glide_for(va):
        g = max(GLIDE_DEG, min(7.0, math.degrees(math.atan(FINAL_SINK / max(va, 20.0)))))
        return math.tan(math.radians(g)), S_ENTRY * math.tan(math.radians(g)) + 60
    tan_g, entry_h = glide_for(vapp)
    STATUS["glide_deg"] = round(math.degrees(math.atan(tan_g)), 1)

    def track(px, py):  # s = distance from aim point out along the approach (positive = before touchdown)
        rx, ry = px - aim[0], py - aim[1]
        s = -(rx * d[0] + ry * d[1])
        xt = rx * (-d[1]) + ry * d[0]  # cross-track, positive = left of centerline? (sign only used via carrot)
        return s, xt

    def point_on_line(s):
        return (aim[0] - d[0] * s, aim[1] - d[1] * s)

    phase = "to_entry"
    if str(v.situation).split(".")[-1] in ("landed", "pre_launch"):
        phase = "takeoff"  # circuit / flight from the ground: take off first, then the pattern
    s0, _ = track(x, y)
    hdg0 = s_hdg()
    if phase != "takeoff" and 4000 < s0 < S_ENTRY + 4000 and abs(track(x, y)[1]) < 1200 \
            and abs(_wrap(hdg0 - rwy_hdg)) < 35:
        phase = "final"
    STATUS["phase"] = phase

    # controller state
    pitch_base = s_pitch()
    pitch_des_prev = pitch_base
    t_gear = None
    i_pitch = 0.0
    i_thr = 0.4
    h_des = 0.0
    climbing = False
    last = time.time()
    last_pitch, last_roll = s_pitch(), s_roll()
    last_hdg, r_f, bank_prev = s_hdg(), 0.0, s_roll()
    q_f = p_f = 0.0
    s_g = conn.add_stream(getattr, fb, "g_force")
    cmd = {"pitch": float(ctl.pitch), "roll": float(ctl.roll), "yaw": float(ctl.yaw), "throttle": float(ctl.throttle)}

    thr_step = {"t": 0.0}
    acc = {"f": 0.0, "last": float(s_spd()), "t": time.time()}  # filtered airspeed trend, m/s^2
    v_des, spd = 0.0, float(s_spd())
    s_thrust = conn.add_stream(getattr, v, "thrust")
    s_avail = conn.add_stream(getattr, v, "available_thrust")

    def out(axis, val, dt_, slew=None):
        """Slew-limited control output (gentle inputs in every mode).
        Throttle (Luke's standard rule): 5 % steps, then wait a few seconds to see the effect before the next
        step (3 s; 2 s on final; 1 s when nearing the hard speed cap; 0.5 s in an upset recovery). Only the
        flare cushion (slew=THR_SLEW_FLARE) moves continuously; the takeoff uses its own (old) logic."""
        c = cmd[axis]
        if axis == "throttle" and slew != THR_SLEW_FLARE:
            # jet engines spool with lag: >= 4 s between steps (3 s on final, 2 s near the hard cap / in a
            # recovery), and only once the actual thrust has settled at the commanded level (or 2x the wait)
            wait = {None: 3.0 if phase == "final" else 4.0, 0.5: 2.0, 1.0: 2.0}.get(slew, 4.0)
            # never 0 in flight; Luke: >= 30 % in climb / cruise (only a planned descent or final may go lower)
            floor = 0.0
            if phase in ("to_entry", "stall_test", "final"):
                floor = 0.05
                if phase == "to_entry" and not (h_des < h - 30.0) and eas < v_hard - 12.0:
                    floor = 0.30
            val = _clamp(val, floor, 1.0)
            t_ = time.time()
            try:
                avail = s_avail()
                settled = avail <= 0 or abs(s_thrust() - avail * c) < 0.05 * avail
            except Exception:  # noqa: BLE001
                settled = True
            # Luke: recovering from over-speed, step down only while the speed is still NOT decreasing - once it
            # starts to bleed, hold that setting and let it bleed (cutting further ends in a stall). Likewise
            # stop stepping up once it's accelerating and near the target (spool lag overshoots).
            # Symmetric (Luke): change only once the speed has settled. Increasing -> no more power (let it
            # stabilize); decreasing -> no further cut. Exception: decelerating BELOW target may add power (stall
            # protection).
            a_ = acc["f"]
            v_min_safe = 1.3 * (vs_stall or DEFAULT_STALL) + 10.0  # near-stall safety threshold only
            urgent = (val > c and spd < v_min_safe) or (val < c and eas > v_hard and not climbing)  # safety: act now
            if urgent:
                wait = min(wait, 1.0)
            elif val > c and a_ > 0.2:
                val = c
            elif val < c and a_ < -0.2:
                val = c
            elif val > c and a_ < -0.2 and not spd < v_des:
                val = c
            if urgent:
                settled = True
            if abs(val - c) >= 0.025 and (t_ - thr_step["t"] >= 2 * wait or
                                          (t_ - thr_step["t"] >= wait and settled)):
                val = _clamp(c + math.copysign(min(0.05, abs(val - c)), val - c), 0.0, 1.0)
                thr_step["t"] = t_
            else:
                val = c
            cmd[axis] = val
            ctl.throttle = emergency.shape("throttle", val)  # idle while reversed in flight
            return
        m = (slew or SLEW[axis]) * dt_
        lo = 0.0 if axis == "throttle" else -1.0
        val = _clamp(_clamp(val, lo, 1.0), c - m, c + m)
        cmd[axis] = val
        setattr(ctl, axis, emergency.shape(axis, val))  # throttle idle if reversed; probe-adapted sign
    rec = None  # upset-recovery state
    dash = {"ext": None, "done": False}
    orbit = {"chk": 0.0, "until": 0.0, "alt": None, "dir": 1}
    tg_left = int(touch_and_go or 0)  # >0: that many touch-and-goes, <0: until told to stop
    t_started = time.time()

    def tg_stop_requested():
        from . import settings as _s
        return float(_s.get("plane_tg_stop") or 0) > t_started
    to_state = to_gov = None
    v_low, v_hard = speedcap.limits()
    low_warn, last_logged_phase = 0.0, None
    last_trace = 0.0
    t_start = time.time()
    terr = {"t": 0.0, "hi": rwy_alt}  # look-ahead terrain (m MSL)
    s_lat = conn.add_stream(getattr, fb, "latitude")
    s_lon = conn.add_stream(getattr, fb, "longitude")
    td = None
    n_parts0 = len(v.parts.all)
    min_vs_low = 0.0
    rev = {"tried": False, "on": False, "prop_tried": False, "prop_on": False}  # rollout reversers (jets / props)
    rb, wd = None, {"t": 0.0, "v": None}  # gentle rollout brakes (rollout.py), wheels-grounded check (every 0.2 s)
    t_end = time.time() + 1500

    while time.time() < t_end:
        if _stop.is_set():
            if str(v.situation).split(".")[-1] in ("landed", "pre_launch"):
                ctl.throttle = 0  # (in flight the throttle is left where it is - never chop it - Luke)
            if reversers.engaged():
                ctl.throttle = 0
                reversers.release(v)
            ctl.sas = True
            return "Plane autoland aborted by request."
        now = time.time()
        dt = max(0.01, now - last)
        last = now
        px, py = geo.xy(s_pos())
        alt, ralt, vs, spd = s_alt(), s_ralt(), s_vs(), s_spd()
        t_a = time.time()
        if t_a - acc["t"] > 0.05:  # filtered airspeed trend (m/s^2, ~1.5 s time constant)
            acc["f"] += ((spd - acc["last"]) / (t_a - acc["t"]) - acc["f"]) * min(1.0, (t_a - acc["t"]) / 1.5)
            acc["last"], acc["t"] = spd, t_a
        eas = math.sqrt(2.0 * max(s_q(), 0.0) / 1.225)  # equivalent airspeed for the speed limits
        v_low, v_hard = speedcap.limits()  # Luke's 200 / 220 EAS unless an override raised them
        pitch, hdg, roll = s_pitch(), s_hdg(), s_roll()
        h = alt - rwy_alt - gear_h          # height of the wheels above the runway elevation
        hr = ralt - gear_h                  # radar height of the wheels
        # low-passed pitch/roll rates for the damping terms: raw per-loop differences are noisy and made
        # the surfaces chatter (pitch and roll surfaces twitching against each other)
        a_f = min(1.0, dt / 0.25)
        q_f += ((pitch - last_pitch) / dt - q_f) * a_f
        p_f += (_wrap(roll - last_roll) / dt - p_f) * a_f
        q, p = q_f, p_f
        last_pitch, last_roll = pitch, roll
        sit = str(s_sit()).split(".")[-1]
        s, xt = track(px, py)
        if now - terr["t"] > 2.0 and phase not in ("takeoff", "rollout", "flare"):
            terr["t"] = now
            try:
                from .hold import terrain_ahead
                terr["hi"] = terrain_ahead(body, s_lat(), s_lon(), hdg, spd)
            except Exception as e:  # noqa: BLE001
                log.info("terrain look-ahead failed: %s", e)
        h_terr = terr["hi"] - rwy_alt - gear_h  # highest ground ahead, relative to the runway (like h)
        if sit == "splashed" and phase not in ("flare", "final", "rollout", "stopped"):
            ctl.throttle = 0.0
            return (f"Splashed down during {phase} (lost {n_parts0 - len(v.parts.all)} parts) - "
                    "the flight ended in the water, not on a runway.")

        # ------------------------------------------------------------ rollout
        if phase in ("flare", "final") and sit in ("landed", "splashed"):
            phase = "rollout"
            rb = None  # fresh brake ramp every touchdown (touch-and-go)
            td = {"vs": round(min_vs_low, 2), "speed": round(spd, 1), "s": round(s, 0), "xt": round(xt, 1), "t": now,
                  "pitch": pitch}
            STATUS["touchdown"] = td
        if phase == "rollout" and tg_left != 0 and not tg_stop_requested():
            t_roll = now - td["t"]
            if t_roll > 1.5 and hr < 1.0 and "graded" not in td:
                lost_now = n_parts0 - len(v.parts.all)
                on_rwy_now = abs(xt) < 30
                g = _grade(td, lost_now, on_rwy_now)
                entry = {"n": len(STATUS.setdefault("touchdowns", [])) + 1, "grade": g, "sink": abs(td["vs"]),
                         "speed": td["speed"], "xt": td["xt"], "bounces": td.get("bounces", 0),
                         "sink_at": dict(STATUS.get("sink_at", {}))}
                STATUS["touchdowns"].append(entry)
                td["graded"] = True
                log.info("plane: touch-and-go #%d grade %s %s", entry["n"], g, entry)
                _notify(f"Touch-and-go #{entry['n']}: grade {g}, sink {entry['sink']:.1f} m/s at {entry['speed']:.0f} m/s, "
                        f"{entry['xt']:+.0f} m off centerline. Going around.")
                if lost_now:
                    tg_left = 0  # damaged: make this one a full stop
                else:
                    tg_left -= 1
                    STATUS.pop("sink_at", None)
                    min_vs_low = 0.0
                    to_state = to_gov = None
                    phase = "takeoff"  # smooth power-up (20 %/s), no brakes, rotate, climb, pattern again
                    STATUS["extend"] = False
                    continue
        if phase == "rollout":
            # stay in rollout even if the wheels skip; nose slightly down kills lift so it doesn't porpoise,
            # brakes once the wheels have been down ~1.5 s
            ctl.throttle = 0.0
            t_roll = now - td["t"]
            # mains first with the nose up; never push the nose down - only damp the pitch rate (so a
            # rebound can't pitch us back up) and let gravity lower the nose. Gentle (pulsed) braking until
            # the nose wheel is down, then full brakes.
            nose_down = pitch < 0.8 or t_roll > 6.0
            ctl.pitch = _clamp(PITCH_SIGN * (-0.04 * q + (0.02 * (td["pitch"] - pitch) if pitch > td["pitch"] else 0.0)), -0.2, 0.3)
            # brakes ramp in gently (Luke's prop plane flipped onto its back under hard braking): none until every
            # wheel is down and settled, then a duty cycle ramping up while the pitch / bank are watched; a nose dip,
            # fast nose-down rate or growing bank releases them until it settles again
            if now - wd["t"] > 0.2:
                wd["t"], wd["v"] = now, _wheels_grounded(v)
            wheels_down = hr < 0.5 and (wd["v"] if wd["v"] is not None else nose_down)
            if not wheels_down and hr < 0.5 and nose_down and t_roll > WHEELS_FALLBACK_S:
                wheels_down = True  # a wheel that never reports grounded must not stop us braking at all
            if rb is None:
                rb = rollout.RolloutBrakes()
            was = rb.why
            ctl.brakes = rb.update(now, wheels_down, pitch, q, roll, p, vs)
            if rb.why != was and not rb.why.startswith("braking"):
                log.info("plane: rollout brakes %s (spd %.0f pitch %.1f q %+.1f roll %.1f)", rb.why, spd, pitch, q, roll)
            STATUS["brakes"] = rb.why
            ctl.roll = _clamp(ROLL_SIGN * (0.02 * (0 - roll) - 0.006 * p), -1, 1)
            # xt < 0 = right of the centerline (xt = left-positive cross-track): steer back toward it.
            # (sign was flipped before: rollouts drifted further off, -19 -> -32 m)
            herr = _wrap(rwy_hdg - hdg) + _clamp(xt * 0.15, -5, 5)
            ctl.yaw = _clamp(0.08 * herr, -1, 1)
            try:
                ctl.wheel_steering = _clamp(-0.05 * herr, -1, 1)
            except Exception:
                pass
            # thrust reversers (full stop only): mains + nose down, wings level, wheels on the ground -> reverse at
            # ~60 %; idle + forward again below ~30 m/s; brakes as above until stopped. No reverser -> as before.
            if (not rev["tried"] and tg_left == 0 and sit == "landed" and hr < 0.5 and nose_down and abs(roll) < 5.0
                    and t_roll > 1.0 and spd > reversers.ON_MIN_SPEED and reversers.enabled()):
                rev["tried"] = True
                rev["on"] = reversers.engage(v) > 0
                if rev["on"]:
                    log.info("plane: rollout reversers engaged at %.0f m/s", spd)
            # propellers: reverse (beta) blade pitch on the ground only, back to fine pitch + torque 0 below ~10 m/s
            if (not rev["prop_tried"] and tg_left == 0 and sit == "landed" and hr < 0.5 and nose_down
                    and abs(roll) < 5.0 and t_roll > 1.0 and spd > propulsion.REV_ON_MIN_SPD and reversers.enabled()
                    and propulsion.has_props(v)):
                rev["prop_tried"] = True
                rev["prop_on"], msg = propulsion.reverse(v, True, sit)
                log.info("plane: rollout %s at %.0f m/s", msg, spd)
            if rev["prop_on"] and spd >= propulsion.REV_OFF_SPD and sit == "landed":  # steer with differential reverse
                note = propulsion.diff_reverse(v, herr)
                if note:
                    log.info("plane: rollout %s (herr %+.1f)", note, herr)
            if rev["prop_on"] and (spd < propulsion.REV_OFF_SPD or sit != "landed"):
                rev["prop_on"] = False
                ok, msg = propulsion.reverse(v, False, sit)
                if sit == "landed":
                    msg += "; " + propulsion.set_rotor(v, torque=0.0)
                log.info("plane: rollout %s at %.0f m/s", msg, spd)
            if rev["on"]:
                if spd < reversers.OFF_SPEED:
                    ctl.throttle = 0.0
                    reversers.release(v)
                    rev["on"] = False
                    log.info("plane: rollout reversers off at %.0f m/s", spd)
                else:  # power only with the wheels down and roughly straight (a bounce / swerve = idle)
                    ctl.throttle = reversers.ROLLOUT_THROTTLE if (hr < 0.5 and abs(herr) < 15.0) else 0.0
            if hr > 1.0:
                td["bounces"] = td.get("bounces", 0) + (0 if td.get("_air") else 1)
                td["_air"] = True
            else:
                td["_air"] = False
            if spd < 0.5:
                break
            STATUS.update(phase="rollout: reversers" if rev["on"] else ("rollout: prop reverse" if rev["prop_on"]
                                                                          else phase), speed=round(spd, 1), xt=round(xt, 1),
                          s=round(s, 0))
            if now - last_trace > 0.25:
                last_trace = now
                TRACE.append((round(now - t_start, 1), phase, round(s), round(xt, 1), round(h, 1), round(hr, 1),
                              round(vs, 2), 0, round(spd, 1), round(pitch, 1), -1.0, round(hdg), round(rwy_hdg),
                              round(roll), 0, 0))
            time.sleep(0.03)
            continue

        # ------------------------------------------------------------ takeoff (from the ground)
        if phase == "takeoff":
            # Luke: the takeoff uses the OLD (b94c425) takeoff code that flew the smooth earlier takeoffs - none of
            # this session's gain scheduling / trim / nose rules / climb-rate throttle. Only additions: the 220 m/s
            # hard cap, the gear-up rule (vs > 3 m/s and > 30 m AGL for 2 s) and a handoff to the flight plan only
            # once safely climbing (>= 100 m AGL, climbing, gear up).
            if to_state is None:
                line = _runway_line(body, v, bref, hdg)
                if line is None and hr < 3.0 and spd < 5.0:  # (never mid touch-and-go roll)
                    return (f"Not lined up on a known runway (heading {hdg:.0f}) - I won't take off from here (the "
                            "island spawn faces a cliff). Line up on the runway first.")
                to_state = {"hdg": line[2] if line else hdg, "t": now, "alt0": alt, "line": line}
                STATUS["takeoff_heading"] = round(to_state["hdg"], 1)
                ctl.brakes = False
                ctl.sas = False
                ctl.gear = True
                if propulsion.has_props(v):
                    propulsion.takeoff_setup(v)  # motor on, RPM / torque max, blades deployed at a fine pitch
                elif v.available_thrust <= 0:  # (props show no thrust: never stage)
                    ctl.activate_next_stage()  # engines not started yet (fresh from the launch site)
            vs0t = vs_stall or DEFAULT_STALL
            if "lift" not in to_state:  # learned liftoff speed for this craft (hand or autopilot takeoffs)
                try:
                    to_state["lift"] = tko.learned_liftoff(_craft_keys(v))
                except Exception:  # noqa: BLE001
                    to_state["lift"] = None
                vr0 = tko.rotate_speed(vs0t, to_state["lift"])
                log.info("plane: takeoff Vr %.0f m/s (%s)", vr0, f"learned liftoff {to_state['lift']:.0f}"
                         if to_state["lift"] else f"1.25 x stall {vs0t:.0f}, not learned yet")
            vr = tko.rotate_speed(vs0t, to_state["lift"])
            # throttle from the vessel's specs (TWR), not blindly full power (a fast jet overspeeded into the sea -
            # Luke); 5 % steps with spool waits, hold while the speed changes unless under the stall margin / over cap
            if to_gov is None and (v.available_thrust > 0 or now - to_state["t"] > 3.0):
                to_gov = _takeoff_governor(v, body, now, STATUS, "plane")
            thr_t = to_gov.update(now, spd, eas, hr >= 3.0, vr, 1.3 * vs0t + 10.0) \
                if to_gov is not None else tko.THR_MIN
            if to_gov is not None and abs(thr_t - cmd["throttle"]) > 1e-6:
                log.info("plane: takeoff throttle %.0f%% (%s; spd %.0f eas %.0f a %+.1f)", 100 * thr_t, to_gov.why,
                         spd, eas, to_gov.acc)
            cmd["throttle"] = thr_t
            ctl.throttle = emergency.shape("throttle", thr_t)
            ctl.brakes = False
            if to_state.get("line") and hr < 3.0:  # track the runway centerline on the roll
                brg_l, xt_r = _line_track(body, v, bref, to_state["line"])
                to_state["hdg"] = (brg_l - _clamp(0.05 * xt_r, -3.0, 3.0)) % 360
            herr_t = _wrap(to_state["hdg"] - hdg)
            out("yaw", 0.06 * herr_t, dt)
            ctl.wheel_steering = _clamp(-0.05 * herr_t, -1, 1)
            out("roll", ROLL_SIGN * (0.02 * (0.0 - roll) - 0.01 * p), dt)
            p_tgt = 0.0 if spd < vr else 9.0
            pitch_des_prev = _clamp(p_tgt, pitch_des_prev - PITCH_RATE * dt, pitch_des_prev + PITCH_RATE * dt)
            g_q = _clamp((60.0 / max(spd, 20.0)) ** 2, 1.0, 2.5)
            rot = to_state.setdefault("rot", tko.RotateAssist())  # rotate until the nose actually rises
            to_state.setdefault("pitch0", pitch)
            rx = rot.update(hr < 3.0, spd, vr, pitch_des_prev - pitch, q, dt)
            out("pitch", PITCH_SIGN * (g_q * (0.022 * (pitch_des_prev - pitch) - 0.02 * q) + rx), dt)
            rising = tko.rotation_achieved(q, pitch - to_state["pitch0"], vs, hr < 3.0)
            if rot.maxed and not rising and to_gov is not None and to_gov.force_full(
                    now, "rotation: max elevator, nose still down - full power, keep accelerating"):
                log.info("plane: takeoff throttle 100%% (%s; spd %.0f)", to_gov.why, spd)
            if hr < 3.0 and now - to_state.get("rwy_t", 0.0) > 0.2:  # runway can't support continuing: stop
                to_state["rwy_t"] = now
                ahead = _runway_ahead(body, v, bref, hdg)
                need = tko.lift_need(vr, to_state["lift"])
                if tko.runway_abort(True, spd, ahead, rising, to_gov.acc if to_gov else None, need):
                    ctl.throttle = 0.0  # on the ground only (never 0 in flight)
                    ctl.brakes = True
                    msg = _abort_text(spd, vr, ahead, rot, need)
                    log.warning("plane: %s", msg)
                    return msg
            if vs > 3.0 and hr > 30.0:
                to_state.setdefault("climb_t", now)
                if now - to_state["climb_t"] > 2.0:
                    ctl.gear = False
            else:
                to_state.pop("climb_t", None)
            if hr > 100 and ((vs > 2.0 and not ctl.gear) or now - to_state["t"] > 120):
                phase = "to_entry"
                pitch_base = pitch_des_prev
                i_thr = cmd["throttle"]
                STATUS["takeoff_s"] = round(now - to_state["t"])
                log.info("plane: takeoff done -> flight plan (hr=%.0f vs=%.1f spd=%.0f pitch=%.1f)", hr, vs, spd, pitch)
            elif now - to_state["t"] > 90 and hr < 5:
                ctl.throttle = 0.0
                ctl.brakes = True
                return "Takeoff aborted: not airborne after 90 s (runway too short / too rough, or not enough thrust)."
            if now - last_trace > 0.25:
                last_trace = now
                TRACE.append((round(now - t_start, 1), "takeoff", 0, 0, round(h, 1), round(hr, 1), round(vs, 2), 0,
                              round(spd, 1), round(pitch, 1), round(pitch_des_prev, 1), round(hdg), 0, round(roll),
                              round(cmd["pitch"], 3), round(cmd["throttle"], 2)))
            if phase == "takeoff":
                STATUS.update(phase="takeoff", speed=round(spd, 1), h=round(h, 1), hdg=round(hdg), bank=round(roll))
                time.sleep(0.03)
                continue
            last_hdg, r_f = hdg, 0.0  # fresh turn-rate filter for the flight plan

        # ------------------------------------------------------------ stall / spin guard
        # (anything that upsets the plane: a stall, a big bank, a spin, another autopilot fighting us)
        vs0 = vs_stall or DEFAULT_STALL
        if phase in ("to_entry", "final") and rec is None and (hr > 40 or (hr > 10 and abs(roll) > BANK_UPSET)):
            aoa = s_aoa()
            if aoa > AOA_STALL + 4 or abs(roll) > BANK_UPSET or spd < 0.85 * vs0 or abs(r_f) > 20:
                rec = {"t": now, "why": f"aoa {aoa:.0f} roll {roll:.0f} spd {spd:.0f} yaw-rate {r_f:.0f}"}
                STATUS["recoveries"] = STATUS.get("recoveries", 0) + 1
                STATUS["last_recovery"] = rec["why"]
                log.info("plane: upset recovery (%s)", rec["why"])
        if rec is not None:
            aoa = s_aoa()
            good = aoa < 8 and abs(roll) < 15 and spd > 1.25 * vs0 and vs > -15
            if (good and now - rec["t"] > 3) or now - rec["t"] > 60:
                rec = None
                phase = "to_entry"  # re-enter the pattern from wherever we are
                STATUS["extend"] = False
            else:
                STATUS["phase"] = "recover"
                i_pitch = 0.0
                g_q = gsched()
                slow = aoa > 8 or spd < 1.3 * vs0
                # power: full only when slow; when fast, ease off (smoothly) instead of adding energy
                out("throttle", 1.0 if (slow or vs < -2.0) else (0.3 if spd > 150 else cmd["throttle"]), dt, 1.0)
                ctl.brakes = False
                r_f += (_wrap(hdg - last_hdg) / dt - r_f) * min(1.0, dt / 0.5)
                last_hdg = hdg
                # wings level, with the roll command allowed to move faster than normal (2/s) so a big
                # bank is stopped promptly; gains scaled by dynamic pressure
                out("roll", ROLL_SIGN * _clamp(min(1.5, g_q) * (0.02 * _wrap(0.0 - roll) - 0.01 * p), -0.5, 0.5),
                    dt, 2.0)
                if abs(roll) > BANK_UPSET:
                    # big bank: cut pitch/yaw inputs (pulling while banked/inverted just tightens the dive)
                    p_tgt = None
                    out("pitch", 0.0, dt, 2.0)
                    out("yaw", 0.0, dt, 2.0)
                else:
                    p_tgt = 3.0  # Luke's rule: full power and a gentle pull-up (speed builds, nose comes up)
                    if hr < 30.0:  # too low to dive for speed: wings level, nose slightly up, full power
                        p_tgt = 2.0
                    out("pitch", PITCH_SIGN * g_q * (0.025 * (p_tgt - pitch) - 0.02 * q), dt)
                    out("yaw", -min(1.0, g_q) * 0.04 * r_f, dt)  # stop any spin rotation
                STATUS.update(h=round(h, 1), vs=round(vs, 1), speed=round(spd, 1), hdg=round(hdg), bank=round(roll))
                if now - last_trace > 0.25:
                    last_trace = now
                    TRACE.append((round(now - t_start, 1), "recover", round(s), round(xt, 1), round(h, 1), round(hr, 1),
                                  round(vs, 2), 0, round(spd, 1), round(pitch, 1), p_tgt or 0, round(hdg), 0, round(roll), 0, 1.0))
                time.sleep(0.03)
                continue

        # ------------------------------------------------------------ lateral guidance
        if phase == "to_entry":
            if LIVE.get("vcruise"):  # 'speed 325' from chat while en route
                vcruise_user = vcruise = float(LIVE["vcruise"])
            if LIVE.get("cruise_alt"):  # 'climb to 6000' from chat while en route (m MSL)
                if not cruise_alt:
                    dash.update(ext=0.0, done=True)  # a change en route: no extra outbound leg
                cruise_alt = max(300.0, float(LIVE["cruise_alt"]) - rwy_alt)
            tx, ty = point_on_line(S_ENTRY)
            dist = math.hypot(tx - px, ty - py)
            path_rem = dist
            if cruise_alt and vcruise_user >= 300 and alt > 8000.0 and not LIVE.get("vcruise"):
                # supersonic dash (Luke): Mach 1.5 from the local speed of sound (~445 m/s at 11-12 km)
                try:
                    vcruise = max(vcruise_user, 1.5 * s_sos())
                except Exception:  # noqa: BLE001
                    vcruise = max(vcruise_user, 445.0)
            if cruise_alt:
                v_fin = max(vapp + 30, 75.0)
                plan_d = (max(0.0, cruise_alt - entry_h) / math.tan(math.radians(6.0))
                          + 0.5 * max(0.0, vcruise ** 2 - v_fin ** 2) / 2.5 + 3000.0)
                if dash["ext"] is None:  # route too short for climb + cruise + planned descent: add an outbound leg
                    # (searched over the real geometry: path = here -> turn point on the extended centerline -> entry)
                    need = max(0.0, cruise_alt - h) / math.tan(math.radians(8.0)) + plan_d + 20000.0
                    dash["ext"] = 0.0
                    if dist < need:
                        for ext_try in range(5000, 205000, 5000):
                            ex_, ey_ = point_on_line(S_ENTRY + ext_try)
                            if math.hypot(ex_ - px, ey_ - py) + ext_try >= need:
                                dash["ext"] = float(ext_try)
                                break
                        else:
                            dash["ext"] = 200000.0
                    STATUS["dash_extension_km"] = round(dash["ext"] / 1000, 1)
                if dash["ext"] > 0 and not dash["done"]:
                    ex, ey = point_on_line(S_ENTRY + dash["ext"])
                    d_ex = math.hypot(ex - px, ey - py)
                    if d_ex < 3000:
                        dash["done"] = True
                    else:
                        tx, ty = ex, ey
                        path_rem = d_ex + dash["ext"]
            hdg_err = abs(_wrap(hdg - rwy_hdg))
            # reaching the entry point pointing the wrong way (e.g. flying outbound along the centerline):
            # go 5 km further out first, then come back in (teardrop) instead of turning onto final there
            if dist < 2500 and hdg_err >= 60 and not (dash["ext"] and not dash["done"]):
                STATUS["extend"] = True
            if STATUS.get("extend"):
                ex, ey = point_on_line(S_ENTRY + 5000)
                if math.hypot(ex - px, ey - py) < 2000:
                    STATUS["extend"] = False
                else:
                    tx, ty = ex, ey
            hdg_des = _bearing(tx - px, ty - py)
            if now - orbit["chk"] > 1.0:  # sustained circling turn on request (settings "plane_orbit_until")
                orbit["chk"] = now
                orbit["until"] = float(settings.get("plane_orbit_until") or 0)
                if orbit["until"] > now and orbit["alt"] is None:
                    orbit["alt"], orbit["dir"] = h, (1 if _wrap(hdg_des - hdg) >= 0 else -1)
                    log.info("plane: circling turn until %.0f at h=%.0f", orbit["until"], h)
                elif orbit["until"] <= now:
                    orbit["alt"] = None
            if orbit["alt"] is not None:
                hdg_des = (hdg + 40.0 * orbit["dir"]) % 360  # saturates the turn: steady bank at the cap
            if dash["ext"] and not dash["done"]:
                pass  # still on the outbound dash leg
            elif (dist < 1500 and hdg_err < 60) or (S_ENTRY - 3000 < s < S_ENTRY + 3000 and abs(xt) < 800
                                                 and hdg_err < 40):
                phase = "final"
            h_des = max(entry_h, min(h, 1500.0)) if dist > 4000 else entry_h
            v_des = vcruise if dist > 6000 else (vcruise + vapp) / 2
            if cruise_alt:
                # dash: climb to cruise_alt, cruise at vcruise, then a planned descent (6 deg) that also
                # bleeds the speed early (~2.5 m/s^2) so it arrives at the entry point slow
                if path_rem > plan_d:
                    h_des, v_des = cruise_alt, vcruise
                    if h < cruise_alt - 500:  # climb first (thin air), accelerate past Mach 1 only up high
                        v_des = min(vcruise, v_low)  # Luke: ~200 m/s in the climb (220 hard cap)
                        if alt > 8000.0:  # thin air: accelerate toward supersonic already in the climb (Luke)
                            v_des = vcruise
                else:
                    fr = path_rem / plan_d
                    h_des = entry_h + (cruise_alt - entry_h) * fr
                    v_des = v_fin + (vcruise - v_fin) * fr * fr
                STATUS["descent_plan_km"] = round(plan_d / 1000, 1)
            if orbit["alt"] is not None:
                h_des = orbit["alt"]  # hold altitude while circling
            h_des = max(h_des, h - hr + 300.0, h_terr + 300.0)  # terrain clearance: under us AND ahead (mountains)
            v_des = min(v_des, v_low * spd / max(eas, 1.0))  # speed limit (EAS 170) as true airspeed here
            if need_stall_test and sf is None and hr > 350 and abs(roll) < 10 and phase == "to_entry" and spd < 110:
                sf = {"t0": now, "h": h, "hdg": hdg}
                STATUS["phase"] = phase = "stall_test"
        if phase == "stall_test":
            # wings level, hold altitude, idle: speed bleeds until the AoA hits AOA_STALL (or we start sinking)
            hdg_des, h_des, v_des = sf["hdg"], sf["h"], 0.0
            aoa = s_aoa()
            # step the flaps out as we slow (provisional stall estimate from the AoA) so the stall is
            # measured in landing configuration
            vs_est = spd * math.sqrt(max(aoa, 1.0) / AOA_STALL) if aoa > 1.0 else DEFAULT_STALL
            ext = now - flap_t["t"] > 2.0
            fl.schedule(spd, max(vs_est, 20.0), 30.0, ext)
            if ext:
                flap_t["t"] = now
            if aoa >= AOA_STALL or (aoa > 8 and vs < -4) or spd < 20 or now - sf["t0"] > 90:
                vs_stall = spd
                vapp, vtd = speeds_from_stall(vs_stall)
                tan_g, entry_h = glide_for(vapp)
                STATUS["glide_deg"] = round(math.degrees(math.atan(tan_g)), 1)
                for key in _craft_keys(v):
                    cache[key] = round(vs_stall, 1)
                settings.put("stall_speeds", cache)
                STATUS.update(stall_speed=round(vs_stall, 1), stall_aoa=round(aoa, 1), approach_speed=round(vapp, 1),
                              touchdown_target=round(vtd, 1))
                need_stall_test = False
                fl.schedule(spd, vs_stall, 5.0)
                phase = "to_entry"
                pitch_base = min(pitch_base, 5.0)
                vcruise = vcruise_user or max(vapp + 30, 110.0)
        if phase in ("final", "flare"):
            # gear down late (drag wastes fuel): ~2.5 km / 30 s before the threshold, or below 80 m (Luke)
            d_thr = s - aim_m
            gear_now = phase == "flare" or d_thr < 2500.0 or d_thr / max(spd, 1.0) < 30.0 or h < 80.0
            if gear_now:
                ctl.gear = True
            if t_gear is None and gear_now:
                t_gear = now
            elif t_gear is not None and t_gear > 0 and now - t_gear > 5:
                gear_h = max(0.3, v.bounding_box(v.reference_frame)[1][2])
                STATUS["gear_h"] = round(gear_h, 2)
                t_gear = -1
            cs = max(s - CARROT_M, -2000.0)
            tx, ty = point_on_line(cs)
            hdg_des = _bearing(tx - px, ty - py)
            if s < 2000:  # short final: blend toward runway heading to avoid S-turns
                hdg_des = (rwy_hdg + _clamp(_wrap(hdg_des - rwy_hdg), -8, 8)) % 360
            h_des = max(0.0, s * tan_g)
            # speed schedule (later slowdown, per Luke): vapp + 12 beyond 5 km from the aim point, linear down to
            # vapp at 2.5 km, then ~1.15 x stall below 60 m wheel height (~0.8 km before the threshold)
            v_far = vapp + 12.0
            # hold vapp (1.3 x stall) down to the flare: the flare needs that energy to arrest the sink (at
            # 1.15 x stall the nose couldn't get above ~7 deg and it set down at 3.6 m/s); the speed bleeds to
            # ~1.05-1.1 x stall in the flare itself
            v_des = v_far if s > 5000 else vapp + (v_far - vapp) * max(0.0, s - 2500) / 2500
            if LIVE.get("v_final"):  # Luke's speed order on final (his authority; the speed cap still applies)
                v_des = float(LIVE["v_final"])
            if hr < FLARE_H and s < 1500:
                phase = "flare"
        STATUS["phase"] = phase
        if phase != last_logged_phase:
            log.info("plane: phase %s -> %s (h=%.0f hr=%.0f vs=%.1f spd=%.0f pitch=%.1f roll=%.0f s=%.0f xt=%.0f)",
                     last_logged_phase, phase, h, hr, vs, spd, pitch, roll, s, xt)
            last_logged_phase = phase
        if phase != "stall_test":  # step flaps out gradually (every 2 s) as speed drops; retract at once if fast
            ext = now - flap_t["t"] > 2.0
            if ext:
                flap_t["t"] = now
            want = 0.0
            if phase == "to_entry":
                want = 5.0
            elif phase in ("final", "flare"):
                want = 30.0
            fl.schedule(spd, vs_stall or DEFAULT_STALL, want, ext)

        herr = _wrap(hdg_des - hdg)
        bank_lim = MAX_BANK if phase != "flare" else (2.0 if hr > 5 else 0.0)  # wings level in the flare
        if phase == "final" and h < 60:
            bank_lim = 10.0
        if spd > 250:
            bank_lim = min(bank_lim, BANK_FAST)  # high speed (> 250 m/s): 15 deg max
        if speedcap.bank_override():  # 'override bank N' always wins (Luke's authority, every phase)
            bank_lim = float(speedcap.bank_override())
        if protect.blackout():  # pilot out (G-LOC): wings level until they come to
            bank_lim = 0.0
        # turn-rate command -> coordinated bank angle. Scales with airspeed (a fixed bank-per-degree gain
        # over-controlled at ~40 m/s and the plane wobbled side to side), plus heading-rate damping and a
        # bank slew limit.
        r = _wrap(hdg - last_hdg) / dt
        last_hdg = hdg
        r_f += (r - r_f) * min(1.0, dt / 0.5)          # filtered turn rate, deg/s
        aligned = phase in ("final", "flare") and abs(xt) < 300 and abs(herr) < 15
        if aligned:  # established on final: softer gain, small deadband, bank capped at 8 deg
            if not speedcap.bank_override():
                bank_lim = min(bank_lim, 8.0)
            herr_eff = math.copysign(max(0.0, abs(herr) - 0.2), herr)
            r_des = _clamp(HDG_K_FINAL * herr_eff, -1.5, 1.5)
        else:
            r_des = _clamp(HDG_K * herr, -MAX_TURN, MAX_TURN)
        r_cmd = r_des + TURN_KD * (r_des - r_f)
        bank_raw = math.degrees(math.atan(max(spd, 20.0) * math.radians(r_cmd) / 9.81))
        bank_raw = _clamp(bank_raw, -bank_lim, bank_lim)
        bank_des = _clamp(bank_raw, bank_prev - BANK_SLEW * dt, bank_prev + BANK_SLEW * dt)
        bank_prev = bank_des
        kr = 0.010 if aligned else 0.014
        out("roll", ROLL_SIGN * min(1.0, gsched()) * (kr * (bank_des - roll) - 0.01 * p), dt)
        out("yaw", _clamp(0.02 * herr if phase == "flare" else 0.0, -0.2, 0.2), dt)  # rudder holds heading in the flare

        # ------------------------------------------------------------ vertical guidance
        if phase == "flare":
            # Luke's sink profile: 15 m -4.5 -> 5 m -2.0 (smooth), catch 5 -> 2 m: power burst + gentle nose-up to
            # -1.0, touchdown -0.5..-1.0
            if hr > 5.0:
                vs_des = -(2.0 + (hr - 5.0) / 10.0 * 2.5)
            elif hr > 2.0:
                vs_des = -(1.0 + (hr - 2.0) / 3.0)
            else:
                vs_des = -0.7
        elif phase == "final":
            vs_des = -spd * tan_g + _clamp(0.12 * (h_des - h), -6, 6)
        else:
            vs_des = _clamp(0.08 * (h_des - h), -15, CLIMB_VS if cruise_alt else 12)
        # climb floor (Luke): never command a descent below 300 m AGL while en route (to_entry)
        if phase == "to_entry" and hr < 300.0:
            if vs_des < 2.0 and now - low_warn > 3.0:
                low_warn = now
                log.warning("plane: CLIMB FLOOR to_entry hr=%.0f h_des=%.0f vs_des=%.1f -> +2 pitch=%.1f cmd=%.2f",
                            hr, h_des, vs_des, pitch, cmd["pitch"])
            vs_des = max(vs_des, 2.0)
        # terrain floor: stay >= 150 m above the ground until established on final within ~5 km
        clr = min(hr, h - h_terr)  # radar height, or height above the highest ground ahead
        if not (phase == "flare" or (phase == "final" and s < 5000)) and clr < 150.0:
            vs_des = max(vs_des, _clamp(0.1 * (150.0 - clr), 0.0, 8.0 if hr < 150.0 else 25.0))
            if hr < 100.0 and now - low_warn > 5.0:
                low_warn = now
                log.warning("plane: LOW %s hr=%.0f h=%.0f vs=%.1f pitch=%.1f pitch_cmd=%.2f s=%.0f", phase, hr, h, vs,
                            pitch, cmd["pitch"], s)
        over = eas > v_hard - 12.0 and phase in ("to_entry", "final")
        # (Luke: the speed loop drives the THROTTLE only, never pitch - no pitch-up/nose-down for speed)
        e_vs = vs_des - vs
        # mass soften on en-route pitch (same idea as hold._inertia_scale); leave flare/final crisp
        try:
            from . import hold as _hold
            kin = 1.0 if phase in ("flare", "final") else _hold._inertia_scale(max(0.5, float(v.mass) / 1000.0))
        except Exception:  # noqa: BLE001
            kin = 1.0
        ks = (1.0 if phase in ("flare", "final") else _clamp(100.0 / max(spd, 50.0), 0.25, 1.0)) * kin
        kv = (0.5 if phase == "flare" else 0.8) * ks
        pitch_base = _clamp(pitch_base + 0.25 * ks * e_vs * dt, -8, 14)
        pd_new = _clamp(pitch_base + kv * e_vs, -8, {"flare": 10, "stall_test": 20}.get(phase, 12))
        # rate-limit the commanded pitch attitude everywhere (no yanking; no porpoising near the ground)
        rate_lim = PITCH_RATE * (0.7 + 0.3 * kin)
        pd_new = _clamp(pd_new, pitch_des_prev - rate_lim * dt, pitch_des_prev + rate_lim * dt)
        g_now = s_g()
        if g_now > G_MAX:      # load-factor cap: don't pull further
            pd_new = min(pd_new, max(pitch, pitch_des_prev))
        elif g_now < 0.5:      # don't push into negative g either
            pd_new = max(pd_new, min(pitch, pitch_des_prev))
        # Luke's climb pitch bands (en route climbs): 5-15 deg in thick air (< ~5.5 km), 5-10 deg above; the
        # throttle (climb-rate control) does the rest. The 5 deg floor gives way if the speed decays toward stall.
        climbing = phase == "to_entry" and h_des > h + 300.0 and vs_des > 0
        if phase == "to_entry" and h_des > h + 50.0 and vs_des > 0:
            p_hi = 15.0 if alt < 5500.0 else 10.0
            p_lo = 5.0  # HARD RULE (Luke): never trade altitude for speed - the floor never drops for speed
            if climbing:  # Luke: in climbs pitch HOLDS the climb attitude; speed comes from the throttle
                p_climb = 10.0 if alt < 5500.0 else (7.5 if alt < 8000.0 else 5.0)  # thin air: band floor, lets it accelerate
                pd_new = _clamp(p_climb, pitch_des_prev - PITCH_RATE * dt, pitch_des_prev + PITCH_RATE * dt)
            pd_new = _clamp(pd_new, p_lo, p_hi)
        if hr < 30.0:  # near the ground never command more than a few degrees nose-down
            pd_new = max(pd_new, -3.0)
        # Luke's nose-attitude rule: nose-down only (1) on final/flare or (2) on a planned, controlled descent to a
        # new altitude; (3) stalling (slow / sinking / high AoA) -> add power AND pull up gently; any other
        # nose-down while sinking is blocked (hold level or nose-up and add power)
        vs0n = vs_stall or DEFAULT_STALL
        aoa_n = s_aoa()
        stalling = phase in ("to_entry", "final") and (aoa_n > AOA_STALL or spd < 1.15 * vs0n)
        planned_desc = phase == "to_entry" and h_des < h - 30.0 and vs_des < 0
        nose_rule = None
        if planned_desc:
            pd_new = max(pd_new, -5.0)  # a planned descent at a controlled rate: never steeper than 5 deg nose-down
        if stalling:
            nose_rule = "stall"
            pd_new = max(pd_new, min(max(pitch, 0.0) + 1.0, 6.0))
        elif pd_new < 0 and not (phase in ("final", "flare") or planned_desc):
            level_hold = phase == "to_entry" and abs(h_des - h) < 150.0 and vs > -5.0
            if pd_new < (-2.0 if level_hold else 0.0):  # holding altitude may trim to -2 (not a speed trade)
                nose_rule = "blocked"
            pd_new = max(pd_new, -2.0 if level_hold else 0.0)
        if nose_rule and now - low_warn > 3.0:
            low_warn = now
            log.warning("plane: NOSE RULE %s %s hr=%.0f h_des=%.0f vs=%.1f vs_des=%.1f pitch=%.1f -> %.1f",
                        nose_rule, phase, hr, h_des, vs, vs_des, pitch, pd_new)
        pitch_des = pitch_des_prev = pd_new
        e_p = pitch_des - pitch
        i_pitch = _clamp(i_pitch + 0.004 * e_p * dt * 10 * kin, -0.4, 0.4)
        kd = (0.02 if hr < 40 else 0.012) * (1.0 + 0.5 * (1.0 - kin))
        # dynamic-pressure gain scheduling: at ~30-40 m/s the elevator needs more deflection per degree
        # (the last flare commanded 10 deg nose-up but the nose sank from 9 to 4 deg -> 5 m/s touchdown)
        g_q = max(gsched(), 0.25)
        out("pitch", PITCH_SIGN * (g_q * (0.022 * kin * e_p - kd * q) + i_pitch), dt)

        # ------------------------------------------------------------ speed
        if phase == "stall_test":
            thr = 0.0
        elif phase == "flare":
            # mostly idle, but don't let it bleed below stall + 1.5 m/s before the wheels touch (Luke: touchdown
            # 1-2 m/s above stall is right; the last one touched down 3 m/s BELOW the measured stall and dropped)
            v_floor = (vs_stall or vtd / VTD_FACTOR) + 1.5
            thr = _clamp(0.15 * (v_floor - spd), 0.0, 0.6)
            if 2.0 < hr < 5.0:  # catch zone: quick power burst to cushion the sink (747-style)
                thr = max(thr, _clamp(0.3 + 0.15 * (-vs - 1.0), 0.3, 0.55))
            elif hr <= 2.0:     # back toward idle for touchdown; only a trickle if still sinking fast
                thr = max(thr, _clamp(0.15 * (-vs - 0.8), 0.0, 0.2))
        else:
            # Luke: power follows the CLIMB / DESCENT RATE target (can't hold the climb -> more power; sinking
            # faster than the target -> more power; above the target -> less), with the speed kept inside a
            # band around v_des (tight on final) and Luke's 200/220 EAS limits. Changes stay slow (slew 15 %/s).
            vs0b = vs_stall or DEFAULT_STALL
            band_lo, band_hi = (4.0, 5.0) if phase == "final" else (15.0, 10.0)
            v_lo = max(v_des - band_lo, 1.25 * vs0b)
            v_hi = min(v_des + band_hi, v_low * spd / max(eas, 1.0))
            e_v = vs_des - vs
            if climbing:  # climb: attitude fixed, throttle chases the climb V/S (Luke: airspeed ignored)
                i_thr = _clamp(i_thr + 0.01 * e_v * dt, 0.0, 1.0)
                thr = i_thr + 0.03 * e_v
            else:
                i_thr = _clamp(i_thr + 0.015 * e_v * dt, 0.0, 1.0)
                thr = i_thr + 0.04 * e_v
            if climbing:
                pass
            elif spd < v_lo:
                i_thr = _clamp(i_thr + 0.01 * (v_lo - spd) * dt, 0.0, 1.0)
                thr += 0.06 * (v_lo - spd)
            elif spd > v_hi:
                i_thr = _clamp(i_thr - 0.01 * (spd - v_hi) * dt, 0.0, 1.0)
                thr -= 0.06 * (spd - v_hi)
            if nose_rule:  # stalling, or sinking without a planned descent: add power (smoothly)
                thr = max(thr, 0.9)
                i_thr = max(i_thr, 0.6)
            thr = _clamp(thr, 0.0, 1.0)
        if eas > v_hard - 12.0 and phase != "flare" and not climbing:
            thr = 0.0  # nearing the hard cap: power off (not while chasing the climb V/S - Luke)
        out("throttle", thr, dt, 0.5 if (eas > v_hard - 12.0 and not climbing) else
            (THR_SLEW_FLARE if phase == "flare" else None))
        thr = cmd["throttle"]
        ctl.brakes = airbrakes and phase == "final" and spd > v_des + 8  # airbrakes (brakes group) if fast
        if hr < 30:
            min_vs_low = vs if hr < 3 else min_vs_low
        if phase in ("final", "flare"):
            sl = STATUS.setdefault("sink_at", {})
            for mark in (15, 5, 2):
                if hr <= mark and str(mark) not in sl:
                    sl[str(mark)] = round(vs, 2)
        STATUS.update(hdg_des=round(hdg_des), hdg_err=round(_wrap(hdg_des - hdg), 1), terrain_ahead=round(terr["hi"]))
        STATUS.update(s=round(s), xt=round(xt, 1), h=round(h, 1), hr=round(hr, 1), vs=round(vs, 1),
                      speed=round(spd, 1), hdg=round(hdg), bank=round(roll), throttle=round(thr, 2))
        try:  # targets for the mod's STATUS window
            STATUS.update(v_des=round(float(v_des), 1), h_des_msl=round(float(h_des) + rwy_alt + gear_h))
        except Exception:  # noqa: BLE001
            pass
        if now - last_trace > 0.25:
            last_trace = now
            TRACE.append((round(now - t_start, 1), phase, round(s), round(xt, 1), round(h, 1), round(hr, 1),
                          round(vs, 2), round(vs_des, 2), round(spd, 1), round(pitch, 1), round(pitch_des, 1),
                          round(hdg), round(hdg_des), round(roll), round(bank_des), round(thr, 2)))
        if phase == "to_entry" and h < 40 and vs < -3:
            STATUS["warning"] = "too low while maneuvering - climbing"
        time.sleep(0.03)

    ctl.throttle = 0.0
    if reversers.engaged():
        reversers.release(v)
    if rev.get("prop_on"):
        propulsion.reverse(v, False, str(v.situation).split(".")[-1])
    ctl.brakes = True
    ctl.sas = True
    lost = n_parts0 - len(v.parts.all)
    STATUS["parts_lost"] = lost
    if not td:
        return f"Plane autoland timed out in phase {phase}."
    px, py = geo.xy(s_pos())
    s, xt = track(px, py)
    along_thr = -(s - aim_m)  # metres past the threshold
    on_rwy = abs(xt) < 30 and -50 < along_thr < rwy_len + 50
    STATUS.update(stop_xt=round(xt, 1), stop_past_threshold=round(along_thr))
    ok = on_rwy and lost == 0 and abs(td["vs"]) < 5  # gear takes ~5 m/s; 3-5 is reported as firm
    td.pop("_air", None)
    td.pop("t", None)
    sink = abs(td["vs"])
    verdict = ("Landed" + ("" if sink < 2.0 else (" (firm)" if sink < 3.5 else " (hard)"))) if ok else "Landed with problems"
    tds = STATUS.get("touchdowns") or []
    tg_txt = (" Touch-and-goes: " + ", ".join(f"#{t['n']} {t['grade']} ({t['sink']:.1f} m/s)" for t in tds) + "."
              if tds else "")
    sl = STATUS.get("sink_at", {})
    sinks = ", ".join(f"{m} m {sl[m]:+.1f}" for m in ("15", "5", "2") if m in sl)
    return (f"{verdict} on runway {runway}: touchdown sink {abs(td['vs']):.1f} m/s"
            + (f" (sink at {sinks})" if sinks else "") + " at "
            f"{td['speed']:.0f} m/s, stopped {along_thr:.0f} m past the threshold, {xt:+.0f} m off centerline"
            f"{'' if on_rwy else ' (OFF the runway)'}, {td.get('bounces', 0)} bounce(s), {lost} parts lost. "
            f"Grade {_grade(td, lost, on_rwy)}.{tg_txt}")
