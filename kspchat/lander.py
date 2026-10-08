"""Our own powered-landing controller (MechJeb's landing autopilot crashed when driven via kRPC).

Runs in a background thread with its OWN kRPC connection so chat/tool calls return immediately.
Phases:
  deorbit  (only if orbiting) point retrograde, burn until periapsis < DEORBIT_PE
  coast    hold surface-retrograde; rails warp above the atmosphere, 4x physics warp high up
  descent  velocity-profile suicide burn: v_des = -(touchdown + sqrt(2*a_use*(h - FLARE_H)))
           with a_use = MARGIN * (a_max - g); throttle = (g + KP*(v_des - vs)) / (a_max * cos(tilt))
  final    below FINAL_H (and slow sideways) hold vertical instead of retrograde
  landed   throttle 0, autopilot off, SAS on
Height above ground = surface_altitude - distance from CoM to the lowest point of the craft.
Optional `target=(lat, lon)` uses MechJeb's landing autopilot to deorbit/guide towards the target and
takes over below HANDOVER_H for the final burn (hybrid).
"""
import logging
import math
import threading
import time
from collections import deque

from . import config, guard

log = logging.getLogger("kspchat")
DEORBIT_PE = 30_000  # shallower entry = lower peak heating than 25 km
FLARE_H = 4.0
FINAL_H = 40.0
MARGIN = 0.6
KP = 2.0
LOOKAHEAD = 0.2  # s; compensates kRPC loop latency
HANDOVER_H = 5_000
REENTRY_V = 1100  # m/s surface speed: above this inside the mid atmosphere we burn retrograde (heat/aero)
DV_RESERVE = 700  # m/s kept for the landing burn; the reentry burn never eats into it
STATUS = {"phase": "idle"}
TRACE = deque(maxlen=3000)  # (t, phase, h, vs, hs, throttle, a_max, up) every ~0.25 s for post-mortems
_thread = None
_stop = threading.Event()


def _notify(text):
    try:
        from . import science
        science.post_event(text)
    except Exception:
        pass


def active():
    return _thread is not None and _thread.is_alive()


def stop():
    _stop.set()


def start(touchdown_speed=1.5, use_chutes=True, target=None):
    global _thread
    if active():
        return "A landing is already in progress."
    _stop.clear()
    _thread = threading.Thread(target=_run, args=(touchdown_speed, use_chutes, target), daemon=True, name="lander")
    _thread.start()
    return "started"


def _run(touchdown, use_chutes, target):
    import krpc
    STATUS.clear()
    STATUS.update(phase="starting", started=time.time(), target=list(target) if target else None)
    end = guard.hold("lander", _stop, lambda: {k: STATUS.get(k) for k in ("phase", "target")})
    conn = None
    try:
        conn = krpc.connect(name="KSPChatBridge-lander", address=config.KRPC_ADDRESS,
                            rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)
        result = _fly(conn, float(touchdown), bool(use_chutes), target)
    except Exception as e:  # never leave the craft with throttle stuck on
        result = f"Landing controller error: {e.__class__.__name__}: {e}"
        try:
            v = conn.space_center.active_vessel
            v.control.throttle = 0
            v.auto_pilot.engaged = False
        except Exception:
            pass
    end()
    STATUS["phase"] = "done"
    STATUS["result"] = result
    log.info("lander: %s", result)
    _notify(result)
    if conn:
        try:
            conn.close()
        except Exception:
            pass


def _fly(conn, touchdown, use_chutes, target):
    sc = conn.space_center
    v = sc.active_vessel
    body = v.orbit.body
    ctl, ap = v.control, v.auto_pilot
    mj = getattr(conn, "mech_jeb", None)
    try:  # nothing else may fight us for the controls
        if mj and mj.api_ready:
            mj.ascent_autopilot.enabled = False
            mj.node_executor.abort()
            if mj.landing_autopilot.enabled and not target:
                mj.landing_autopilot.stop_landing()
    except Exception:
        pass
    ctl.sas = False
    ctl.throttle = 0.0
    g0 = body.surface_gravity
    R = body.equatorial_radius
    bref = body.reference_frame
    flight = v.flight(bref)
    s_alt = conn.add_stream(getattr, flight, "surface_altitude")
    s_vs = conn.add_stream(getattr, flight, "vertical_speed")
    s_hs = conn.add_stream(getattr, flight, "horizontal_speed")
    s_thr = conn.add_stream(getattr, v, "available_thrust")
    s_mass = conn.add_stream(getattr, v, "mass")
    s_sit = conn.add_stream(getattr, v, "situation")

    def bottom():
        bb = v.bounding_box(v.reference_frame)
        return max(0.0, -bb[0][1])

    mode = {"m": None}

    def retro_surface():  # only send autopilot RPCs when the mode changes (keeps the loop fast)
        if mode["m"] != "retro":
            ap.reference_frame = v.surface_velocity_reference_frame
            ap.target_direction = (0, -1, 0)
            mode["m"] = "retro"

    def vertical():
        if mode["m"] != "vertical":
            ap.reference_frame = v.surface_reference_frame
            ap.target_direction = (1, 0, 0)
            mode["m"] = "vertical"

    s_dir = conn.add_stream(v.direction, v.surface_reference_frame)

    def up_component():
        return s_dir()[0]

    has_chutes = bool(v.parts.parachutes)

    ap.engaged = True
    off = bottom()
    TRACE.clear()
    n_parts0 = len(v.parts.all)
    t_start = time.time()
    last_trace = 0.0
    dv_cache = {"t": 0.0, "dv": 0.0}

    def dv_left():
        if time.time() - dv_cache["t"] > 1.0:
            try:
                m, dry, isp = v.mass, v.dry_mass, v.specific_impulse
                dv_cache["dv"] = 9.81 * isp * math.log(m / dry) if dry > 0 and m > dry else 0.0
            except Exception:
                dv_cache["dv"] = 0.0
            dv_cache["t"] = time.time()
        return dv_cache["dv"]

    legs_out = False
    chutes_out = False

    # ---------------------------------------------------------------- deorbit
    if str(s_sit()).endswith("orbiting") and not target:
        STATUS["phase"] = "deorbit"
        ap.reference_frame = v.orbital_reference_frame
        ap.target_direction = (0, -1, 0)
        mode["m"] = "orbital-retro"
        t0 = time.time()
        while ap.error > 5 and time.time() - t0 < 60:
            time.sleep(0.2)
        while v.orbit.periapsis_altitude > DEORBIT_PE and not _stop.is_set():
            if s_thr() <= 0:
                return "Deorbit failed: no thrust available (out of fuel or engines not staged)."
            ctl.throttle = 1.0 if v.orbit.periapsis_altitude > DEORBIT_PE + 10_000 else 0.3
            time.sleep(0.1)
        ctl.throttle = 0.0

    if target:
        STATUS["phase"] = "mechjeb-guidance"
        la = mj.landing_autopilot
        ap.engaged = False
        mj.target_controller.set_position_target(body, float(target[0]), float(target[1]))
        la.touchdown_speed = touchdown
        la.deploy_gears = True
        la.deploy_chutes = use_chutes
        la.land_at_position_target()
        while s_alt() > HANDOVER_H and not _stop.is_set():
            STATUS["mechjeb_status"] = str(la.status)
            if str(s_sit()).split(".")[-1] in ("landed", "splashed"):
                return "MechJeb landed before handover."
            time.sleep(0.5)
        la.stop_landing()
        ctl.throttle = 0.0
        ap.engaged = True
        mode["m"] = None

    # ---------------------------------------------------------------- coast + descent
    STATUS["phase"] = "coast"
    retro_surface()
    max_impact = 0.0
    last_vs = 0.0
    t_end = time.time() + 1800
    landed_since = None
    while time.time() < t_end:
        if _stop.is_set():
            ctl.throttle = 0.0
            ap.engaged = False
            return "Landing aborted by request."
        alt = s_alt()
        vs = s_vs()
        hs = s_hs()
        sit = str(s_sit()).split(".")[-1]
        h = alt - off
        mass = s_mass()
        a_max = s_thr() / mass if mass else 0.0
        g = g0 * (R / (R + max(alt, 0))) ** 2

        if sit in ("landed", "splashed") or (h < 0.8 and abs(vs) < 1.0 and hs < 2.0):
            landed_since = landed_since or time.time()
            ctl.throttle = 0.0
            if time.time() - landed_since > 1.5:
                break
            time.sleep(0.05)
            continue
        landed_since = None
        last_vs = vs

        # time-warp while nothing is happening
        if STATUS["phase"] == "coast":
            atm = body.atmosphere_depth if body.has_atmosphere else 0
            if alt > atm + 2000 and vs < 0 and (R + alt) > 0:
                if sc.rails_warp_factor == 0:
                    sc.physics_warp_factor = 0
                    sc.rails_warp_factor = min(3, sc.maximum_rails_warp_factor)
            else:
                if sc.rails_warp_factor:
                    sc.rails_warp_factor = 0
                    time.sleep(0.5)
                    off = bottom()
                want_phys = 3 if h > 12_000 else 0
                if sc.physics_warp_factor != want_phys:
                    sc.physics_warp_factor = want_phys

        if not legs_out and h < 4000:
            ctl.legs = True
            ctl.gear = True
            legs_out = True
            time.sleep(0.3)
            off = bottom()
        if use_chutes and not chutes_out and body.has_atmosphere and h < 8000 and vs < -30 and has_chutes:
            for p in v.parts.parachutes:
                try:
                    p.deploy()
                except Exception:
                    pass
            chutes_out = True

        if a_max <= g:
            STATUS["warning"] = f"TWR below 1 here (a_max {a_max:.1f} < g {g:.1f}); best-effort full burn near the ground"
        a_use = max(0.5, MARGIN * (a_max - g))
        hp = max(0.0, h + vs * LOOKAHEAD - FLARE_H)  # predicted height above the flare point
        v_des = -(touchdown + math.sqrt(2 * a_use * hp))
        if h < FINAL_H and hs < 4:
            vertical()
            STATUS["phase"] = "final"
        else:
            retro_surface()
        # feed-forward: deceleration needed to reach touchdown speed exactly at the flare point,
        # plus a feedback term that pulls us onto the v_des profile
        a_req = (vs * vs - touchdown * touchdown) / (2 * max(hp, 0.5)) if vs < -touchdown else 0.0
        a_cmd = g + a_req + KP * (v_des - vs)
        cos_t = max(0.3, up_component())
        thr = a_cmd / (a_max * cos_t) if a_max > 0 else 0.0
        if vs > 0 and h > 50:
            thr = 0.0  # still climbing: coast
        spd = math.hypot(vs, hs)
        atm = body.atmosphere_depth if body.has_atmosphere else 0
        if atm and 15_000 < alt < 0.65 * atm and spd > REENTRY_V and dv_left() > DV_RESERVE:
            if STATUS["phase"] != "reentry-burn":
                STATUS["phase"] = "reentry-burn"
                sc.physics_warp_factor = 0
            thr = 1.0
        elif STATUS["phase"] == "reentry-burn":
            STATUS["phase"] = "coast"
        thr = max(0.0, min(1.0, thr))
        if thr > 0.02 and STATUS["phase"] == "coast":
            STATUS["phase"] = "descent"
            sc.physics_warp_factor = 0
        ctl.throttle = thr
        if h < 30:
            max_impact = max(max_impact, -vs)
        STATUS.update(h=round(h, 1), vs=round(vs, 1), hs=round(hs, 1), throttle=round(thr, 2), v_des=round(v_des, 1))
        if time.time() - last_trace > 0.25:
            last_trace = time.time()
            TRACE.append((round(last_trace - t_start, 2), STATUS["phase"], round(h, 1), round(vs, 2), round(hs, 2),
                          round(thr, 3), round(a_max, 2), round(cos_t, 3), round(off, 2)))
        if a_max == 0 and STATUS["phase"] in ("descent", "final") and h > 5:
            STATUS["warning"] = "No thrust left (out of fuel?)"
        time.sleep(0.05)
    ctl.throttle = 0.0
    ap.engaged = False
    ctl.sas = True
    sc.physics_warp_factor = 0
    v = sc.active_vessel
    f = v.flight(bref)
    up = v.direction(v.surface_reference_frame)[0]
    tilt = math.degrees(math.acos(max(-1.0, min(1.0, up))))
    STATUS.update(touchdown_vs=round(-last_vs, 1), tilt_deg=round(tilt, 1),
                  lat=round(f.latitude, 4), lon=round(f.longitude, 4))
    lost = n_parts0 - len(v.parts.all)
    STATUS["parts_lost"] = lost
    ok = -last_vs < 5 and tilt < 20 and lost == 0
    return (f"{'Landed safely' if ok else 'Landed HARD'}: touchdown ~{-last_vs:.1f} m/s, tilt {tilt:.0f} deg, "
            f"{lost} parts lost, at {f.latitude:.3f}, {f.longitude:.3f} ({str(v.situation).split('.')[-1]}).")
