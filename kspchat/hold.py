"""Aircraft autopilot HOLD mode (AICS "Aircraft Autopilot" panel + chat set commands).

Holds altitude (AGL or MSL), vertical speed, heading, roll and speed. Targets are read LIVE from
bridge_settings.json ("plane_hold") about twice a second, so the AICS Engage button and chat commands like
"set altitude 7200" change them without restarting the controller.

Luke's rules (same as plane.py):
  * climbs: pitch holds a fixed attitude band (10 deg < 5.5 km, 7.5 < 8 km, 5 above) and the THROTTLE chases the
    climb V/S target, ignoring airspeed (no over-speed throttle cuts while chasing V/S); stall protection only.
  * never trade altitude for speed; speed comes from the throttle only. Nose-down only for a commanded descent
    (max -5 deg); level holding may trim to -2 deg.
  * throttle in 5 % steps with a spool wait (3-4 s; 1 s when urgent); bank cap 20 deg low / 15 deg above 250 m/s.
  * terrain: AGL means above the ground reference captured when the target is set (the field you took off from),
    never sea level; plus a look-ahead terrain floor (300 m above the highest ground ~10-60 s ahead).
  * commanded pitch ("pitch up 10" / "pitchup 90" / "nose down 5"; Luke 2026-10-08, plane_pitch tool): the player's
    explicit ABSOLUTE attitude 0-90 deg up or down replaces the V/S loop (and the bands above). Nose up: throttle up;
    closing on the stall margin (v_min_safe = 1.3 Vs + 10, or AoA > 15) -> level off at ~1 deg with full power (never
    cut), rebuild to the margin + RESUME_MARGIN, return to the commanded pitch; repeats until the command changes or
    the target altitude / safe ceiling is reached (then it holds altitude). Nose down: ends at the target altitude or
    the terrain floor; over the EAS hard cap it levels off until the speed is back. See PitchCycle.
"""
import logging
import math
import threading
import time

from . import config, emergency, guard, settings, speedcap, spots
from . import takeoff as tko
from .plane import (_clamp, _wrap, _runway_line, _line_track, ROLL_SIGN, PITCH_SIGN, PITCH_RATE, BANK_SLEW,
                    HDG_K, MAX_TURN, TURN_KD, DEFAULT_STALL, V_HARD, _craft_keys, _takeoff_governor,
                    _runway_ahead, _abort_text, ROT_RISE_Q)

log = logging.getLogger("kspchat")
STATUS = {"phase": "idle"}
_thread = None
_stop = threading.Event()
KEY = "plane_hold"
TERRAIN_CLEAR = 300.0
CLIMB_VS_DEFAULT = 25.0
LEVEL_PITCH = 1.0      # deg: attitude of a pitch-hold level-off (Luke: ~0-2)
RESUME_MARGIN = 20.0   # m/s above the stall margin (nose up) / below the EAS cap (nose down) before resuming
LEAD_S = 5.0           # s of current V/S used as a lead for the target-altitude / ceiling end of a pitch hold
PITCH_KEYS = ("pitch", "pitch_t", "pitch_ceiling")
THR_FLOOR = 0.05       # in-flight throttle floor ("idle"): never 0 in the air
# §8 pitch smoothness: heavy craft porpoise if gains stay Aeris-sized
REF_MASS_T = 10.0      # tonnes — gain reference (light jet)
MASS_GAIN_EXP = 0.4
PITCH_KP, PITCH_KD, PITCH_KI = 0.022, 0.012, 0.04
VS_PITCH_P, VS_PITCH_I = 0.8, 0.25
ALT_VS_K = 0.08
ELEV_SLEW = 0.35       # softer elevator (was 0.6) — reduces porpoise overshoot
Q_SOFT_CAP = 4.0       # deg/s: extra rate damping above this


def _inertia_scale(mass_t):
    """<1 for heavy craft → smaller alt/V/S/pitch gains, slightly more rate damping."""
    return _clamp((REF_MASS_T / max(float(mass_t), 1.0)) ** MASS_GAIN_EXP, 0.30, 1.30)


def _mass_t(v):
    try:
        return max(0.5, float(v.mass) / 1000.0)  # kRPC mass is kg
    except Exception:  # noqa: BLE001
        return REF_MASS_T


def active():
    return _thread is not None and _thread.is_alive()


def stop():
    _stop.set()


def targets():
    return dict(settings.get(KEY) or {})


def set_targets(**kw):
    """Merge new targets (None = leave, 'off' = disable that hold). Returns the merged dict."""
    t = targets()
    for k, val in kw.items():
        if val is None:
            continue
        if val == "off":
            t.pop(k, None)
        else:
            t[k] = val
    t["t"] = time.time()
    settings.put(KEY, t)
    return t


def start():
    global _thread
    if active():
        return "already"
    _stop.clear()
    set_targets(throttle="off", **{k: "off" for k in PITCH_KEYS})  # pitch / manual throttle never carry over
    _thread = threading.Thread(target=_run, daemon=True, name="plane-hold")
    _thread.start()
    return "started"


def _run():
    import krpc
    STATUS.clear()
    STATUS.update(phase="starting", started=time.time())
    end = guard.hold("plane", _stop, lambda: {k: STATUS.get(k) for k in ("phase", "hdg_err", "hdg_des", "alt_agl")})
    conn = None
    try:
        conn = krpc.connect(name="KSPChatBridge-hold", address=config.KRPC_ADDRESS,
                            rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)
        result = _hold(conn)
    except Exception as e:  # noqa: BLE001
        result = f"Autopilot hold error: {e.__class__.__name__}: {e}"
        log.exception("hold")
    end()
    STATUS["phase"] = "off"
    STATUS["result"] = result
    log.info("hold: %s", result)
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


def terrain_ahead(body, lat, lon, hdg, spd):
    """Highest terrain (m MSL, water = 0) under us and along the current track 10-60 s ahead."""
    hi = 0.0
    R = body.equatorial_radius
    for t in (0.0, 10.0, 20.0, 35.0, 60.0):
        la, lo = (lat, lon) if t == 0 else spots.offset(lat, lon, hdg, max(spd, 50.0) * t, R)
        try:
            hi = max(hi, body.surface_height(la, lo))
        except Exception:  # noqa: BLE001
            pass
    return hi


class PitchCycle:
    """Commanded pitch attitude with the automatic level-off cycle (pure logic, unit-tested; see the module doc).
    step() -> (pitch_des or None = the pitch hold ended, full_power, event); events: 'level' (near the stall margin),
    'level_fast' (nose down over the EAS cap), 'resume', or the end reasons 'target' / 'ceiling' / 'terrain'."""

    def __init__(self, v_min_safe):
        self.v_lo = float(v_min_safe)
        self.v_hi = self.v_lo + RESUME_MARGIN
        self.leveled = False

    def reset(self):
        self.leveled = False

    def step(self, cmd, spd, aoa, alt, vs, alt_stop=None, ceiling=None, floor_msl=None, eas=0.0, cap=1e9):
        # lead = the height the current V/S covers while the attitude slews back to level (PITCH_RATE) plus margin
        recover = abs(cmd) / PITCH_RATE
        lead = abs(vs) * (LEAD_S + 0.5 * recover)
        if cmd > 0:
            if alt_stop is not None and alt >= alt_stop - lead:
                return None, False, "target"
            if ceiling and alt >= float(ceiling) - lead:
                return None, False, "ceiling"
        else:
            if cmd < 0 and alt_stop is not None and alt <= alt_stop + lead:
                return None, False, "target"
            if floor_msl is not None and alt <= floor_msl - min(vs, 0.0) * (2 * LEAD_S + 0.7 * recover):
                return None, False, "terrain"
        ev = None
        if cmd > LEVEL_PITCH:
            if not self.leveled and (spd < self.v_lo or aoa > 15.0):
                self.leveled, ev = True, "level"
            elif self.leveled and spd >= self.v_hi and aoa < 10.0:
                self.leveled, ev = False, "resume"
        elif cmd < 0:
            if not self.leveled and eas > cap:
                self.leveled, ev = True, "level_fast"
            elif self.leveled and eas < cap - RESUME_MARGIN:
                self.leveled, ev = False, "resume"
        else:
            self.leveled = False
        if self.leveled:
            return LEVEL_PITCH, cmd > 0, ev
        return float(cmd), cmd > 0, ev


TO_PITCH_KI, TO_PITCH_ILIM = 0.03, 0.3   # takeoff climb: integral elevator (nose-heavy deltas never reached 9 deg)
TO_AIR_HANDOFF_S = 25.0                  # airborne this long -> hand off to the holds anyway (they have I + altitude logic)


def takeoff_pitch_i(i, err, dt, airborne):
    """Takeoff climb integrator (elevator units): builds only once airborne, bounded; a P-only loop left a nose-heavy
    delta (Aeris 4A, 2026-10-08) skimming the water at 230 m/s, never reaching the 9 deg climb attitude."""
    if not airborne:
        return 0.0
    return _clamp(i + TO_PITCH_KI * err * dt, -TO_PITCH_ILIM, TO_PITCH_ILIM)


def takeoff_handoff(now, air_since, hr, vs, gear_down, pitch_t, t0):
    """Why the takeoff hands off to the holds now (None = keep climbing out):
    'climbing' (> 100 m, climbing, gear up - the old rule), 'airborne' (> 20 m for TO_AIR_HANDOFF_S - never stuck in
    the takeoff loop again), 'pitch order' (Luke gave a pitch command while airborne - the holds fly it)."""
    if hr > 100 and vs > 2.0 and not gear_down:
        return "climbing"
    if hr > 20 and air_since is not None and now - air_since >= TO_AIR_HANDOFF_S:
        return "airborne"
    if hr > 20 and pitch_t and pitch_t > t0:
        return "pitch order"
    return None


def _note(msg):
    """bridge.log + a short note in the chat (event feed)."""
    log.info("hold: %s", msg)
    try:
        from . import science
        science.post_event(msg)
    except Exception:  # noqa: BLE001
        pass


def _hold(conn):
    sc = conn.space_center
    v = sc.active_vessel
    try:
        from . import craft_notes, trim_auto
        craft_notes.apply_on_engage(v)
        trim_auto.reset_vessel_context()
    except Exception:  # noqa: BLE001
        pass
    body = v.orbit.body
    bref = body.reference_frame
    ctl = v.control
    try:
        v.auto_pilot.engaged = False
    except Exception:  # noqa: BLE001
        pass
    ctl.sas = False
    fb = v.flight(bref)
    fs = v.flight(v.surface_reference_frame)
    s_alt = conn.add_stream(getattr, fb, "mean_altitude")
    s_ralt = conn.add_stream(getattr, fb, "surface_altitude")
    s_vs = conn.add_stream(getattr, fb, "vertical_speed")
    s_spd = conn.add_stream(getattr, fb, "speed")
    s_pitch = conn.add_stream(getattr, fs, "pitch")
    s_hdg = conn.add_stream(getattr, fs, "heading")
    s_roll = conn.add_stream(getattr, fs, "roll")
    s_q = conn.add_stream(getattr, fb, "dynamic_pressure")
    s_aoa = conn.add_stream(getattr, fb, "angle_of_attack")
    s_lat = conn.add_stream(getattr, fb, "latitude")
    s_lon = conn.add_stream(getattr, fb, "longitude")
    s_sit = conn.add_stream(getattr, v, "situation")

    cache = settings.get("stall_speeds") or {}
    vs0 = next((float(cache[k]) for k in _craft_keys(v) if cache.get(k)), None) or DEFAULT_STALL
    v_min_safe = 1.3 * vs0 + 10.0

    def gsched():
        return _clamp(2205.0 / max(s_q(), 200.0), 0.05, 2.5)

    cmd = {"pitch": float(ctl.pitch), "roll": float(ctl.roll), "yaw": float(ctl.yaw), "throttle": float(ctl.throttle)}
    SLEW = {"pitch": ELEV_SLEW, "roll": 0.8, "yaw": 0.8}
    mass_t = _mass_t(v)
    kin = _inertia_scale(mass_t)
    log.info("hold: mass %.1f t, inertia scale %.2f (pitch gains soft for heavy craft)", mass_t, kin)

    def out(axis, val, dt_, slew=None):
        m = (slew or SLEW[axis]) * dt_
        val = _clamp(_clamp(val, -1.0, 1.0), cmd[axis] - m, cmd[axis] + m)
        cmd[axis] = val
        setattr(ctl, axis, emergency.shape(axis, val))  # probe-adapted sign / test input

    thr_step = {"t": 0.0}

    def throttle_step(want, wait, floor=0.05):
        """Luke's standard rule: 5 % steps, then wait (spool) before the next one."""
        c = cmd["throttle"]
        want = _clamp(want, floor, 1.0)
        now_ = time.time()
        if abs(want - c) >= 0.025 and now_ - thr_step["t"] >= wait:
            c = _clamp(c + math.copysign(min(0.05, abs(want - c)), want - c), 0.0, 1.0)
            thr_step["t"] = now_
        elif c < floor:
            c = floor
        cmd["throttle"] = c
        ctl.throttle = emergency.shape("throttle", c)  # idle while reversed in flight

    # ---- takeoff from the ground (old smooth takeoff, on the runway's own heading) ----
    STATUS.pop("climbout_hdg", None)  # set again below only for a takeoff from the ground
    sit = str(s_sit()).split(".")[-1]
    if sit in ("landed", "pre_launch"):
        line = _runway_line(body, v, bref, s_hdg())
        if line is None:
            return ("Not lined up on a known runway (heading %.0f) - I won't take off from here (cliffs!). Line up on "
                    "a runway first, or take off by hand and then engage the holds." % s_hdg())
        STATUS.update(phase="takeoff", runway_heading=round(line[2], 1), climbout_hdg=line[2])
        ctl.brakes = False
        ctl.gear = True
        from . import propulsion
        if propulsion.has_props(v):
            propulsion.takeoff_setup(v)  # motor on, RPM / torque max, blades deployed at a fine pitch
        elif v.available_thrust <= 0:  # props show no thrust: never stage them
            ctl.activate_next_stage()
        pitch_des_prev, last, t0, climb_t = s_pitch(), time.time(), time.time(), None
        last_pitch = s_pitch()
        q_f = 0.0
        try:
            lift = tko.learned_liftoff(_craft_keys(v))  # learned liftoff speed (hand or autopilot takeoffs)
        except Exception:  # noqa: BLE001
            lift = None
        vr = tko.rotate_speed(vs0, lift)
        need = tko.lift_need(vr, lift)
        log.info("hold: takeoff Vr %.0f m/s (%s)", vr, f"learned liftoff {lift:.0f}" if lift else
                 f"1.25 x stall {vs0:.0f}, not learned yet")
        gov = None  # TWR-based takeoff throttle (plane._takeoff_governor)
        i_to, air_since, t_tg, tg_pitch_t = 0.0, None, 0.0, None
        rot, pitch0, t_rwy = tko.RotateAssist(), s_pitch(), 0.0
        hdg_prev, yaw_rate = s_hdg(), 0.0
        while True:
            if _stop.is_set():
                ctl.throttle = 0.0 if s_ralt() < 3 else ctl.throttle
                return "Hold autopilot stopped during the takeoff."
            now = time.time()
            dt = max(0.01, now - last)
            last = now
            spd, hr, vs, pitch, hdg, roll = s_spd(), s_ralt(), s_vs(), s_pitch(), s_hdg(), s_roll()
            q_f += ((pitch - last_pitch) / dt - q_f) * min(1.0, dt / 0.25)
            last_pitch = pitch
            on_ground = hr < 3.0
            # throttle from the vessel's TWR (not blindly full), 5 % steps with spool waits (takeoff.py)
            if gov is None and (v.available_thrust > 0 or now - t0 > 3.0):
                gov = _takeoff_governor(v, body, now, STATUS, "hold")
            eas = math.sqrt(2.0 * max(s_q(), 0.0) / 1.225)
            thr_t = gov.update(now, spd, eas, not on_ground, vr, v_min_safe) if gov is not None else tko.THR_MIN
            if gov is not None and abs(thr_t - cmd["throttle"]) > 1e-6:
                log.info("hold: takeoff throttle %.0f%% (%s; spd %.0f eas %.0f a %+.1f)", 100 * thr_t, gov.why, spd,
                         eas, gov.acc)
            cmd["throttle"] = thr_t
            ctl.throttle = emergency.shape("throttle", thr_t)
            if on_ground:
                brg, xt_r = _line_track(body, v, bref, line)
                hdg_t = (brg - _clamp(0.05 * xt_r, -3.0, 3.0)) % 360
            else:
                hdg_t = line[2]
            herr = _wrap(hdg_t - hdg)
            yaw_rate += (_wrap(hdg - hdg_prev) / dt - yaw_rate) * min(1.0, dt / 0.2)
            hdg_prev = hdg
            if on_ground:  # runway heading only: small, damped, capped (no ground loops)
                wheel, rud = tko.ground_steer(herr, yaw_rate, spd)
                why_stop = tko.ground_runaway(_wrap(line[2] - hdg), xt_r, spd)
                if why_stop:
                    ctl.throttle = 0.0  # on the ground only (never 0 in flight)
                    ctl.brakes = True
                    try:
                        ctl.wheel_steering = 0.0
                    except Exception:  # noqa: BLE001
                        pass
                    out("yaw", 0.0, dt)
                    STATUS.pop("climbout_hdg", None)
                    msg = f"Takeoff stopped: {why_stop}. Throttle idle, brakes on."
                    log.warning("hold: %s", msg)
                    return msg
            else:
                wheel, rud = 0.0, 0.0
            out("yaw", tko.takeoff_rudder(rud, on_ground, spd, vr), dt)  # centered from rotation on
            try:
                ctl.wheel_steering = wheel
            except Exception:  # noqa: BLE001
                pass
            out("roll", ROLL_SIGN * (0.02 * (0.0 - roll) - 0.01 * 0), dt)
            p_tgt = 0.0 if spd < vr else 9.0
            pitch_des_prev = _clamp(p_tgt, pitch_des_prev - PITCH_RATE * dt, pitch_des_prev + PITCH_RATE * dt)
            g_q = _clamp((60.0 / max(spd, 20.0)) ** 2, 1.0, 2.5)
            i_to = takeoff_pitch_i(i_to, pitch_des_prev - pitch, dt, not on_ground and spd >= vr)
            rx = rot.update(on_ground, spd, vr, pitch_des_prev - pitch, q_f, dt)  # rotate until the nose rises
            out("pitch", PITCH_SIGN * (g_q * (0.022 * (pitch_des_prev - pitch) - 0.02 * q_f) + i_to + rx), dt)
            rising = tko.rotation_achieved(q_f, pitch - pitch0, vs, on_ground)
            if rot.maxed and not rising and gov is not None and gov.force_full(
                    now, "rotation: max elevator, nose still down - full power, keep accelerating"):
                log.info("hold: takeoff throttle 100%% (%s; spd %.0f)", gov.why, spd)
            if on_ground and now - t_rwy > 0.2:  # runway can't support continuing: stop while we still can
                t_rwy = now
                ahead = _runway_ahead(body, v, bref, hdg)
                if tko.runway_abort(True, spd, ahead, rising, gov.acc if gov else None, need):
                    ctl.throttle = 0.0  # on the ground only (never 0 in flight)
                    ctl.brakes = True
                    msg = _abort_text(spd, vr, ahead, rot, need)
                    log.warning("hold: %s", msg)
                    return msg
            if vs > 3.0 and hr > 30.0:
                climb_t = climb_t or now
                if now - climb_t > 2.0:
                    ctl.gear = False
            else:
                climb_t = None
            air_since = (air_since or now) if hr > 20.0 else None
            if now - t_tg > 0.5:
                t_tg = now
                tg_pitch_t = targets().get("pitch_t")
            why = takeoff_handoff(now, air_since, hr, vs, bool(ctl.gear), tg_pitch_t, t0)
            if why:
                if why != "climbing" and vs > -2.0:
                    ctl.gear = False
                log.info("hold: takeoff done - %s (hr=%.0f spd=%.0f vs=%.1f pitch=%.1f i=%.2f)", why, hr, spd, vs,
                         pitch, i_to)
                break
            if now - t0 > 90 and hr < 5:
                ctl.throttle = 0.0
                ctl.brakes = True
                return "Takeoff aborted: not airborne after 90 s."
            STATUS.update(speed=round(spd, 1), hr=round(hr, 1), hdg=round(hdg))
            time.sleep(0.03)

    # ---- holds ----
    pitch_base = s_pitch()
    pitch_des_prev = pitch_base
    i_pitch = 0.0
    last = time.time()
    last_pitch, last_roll, last_hdg = s_pitch(), s_roll(), s_hdg()
    q_f = p_f = r_f = 0.0
    bank_prev = s_roll()
    tgt, t_read = {}, 0.0
    ref_elev, ref_key = None, None
    terr, t_terr = 0.0, 0.0
    acc = {"f": 0.0, "last": s_spd(), "t": time.time()}
    hold_alt0, hold_hdg0 = s_alt(), s_hdg()
    log_t = 0.0
    pcyc, pc_key, was_pitch = PitchCycle(v_min_safe), None, False
    while True:
        if _stop.is_set():
            ctl.sas = True
            return "Autopilot holds disengaged."
        now = time.time()
        dt = max(0.01, now - last)
        last = now
        if now - t_read > 0.5:  # live targets
            t_read = now
            tgt = targets()
            if tgt.get("engaged") is False:
                ctl.sas = True
                return "Autopilot holds disengaged."
        alt, ralt, vs, spd = s_alt(), s_ralt(), s_vs(), s_spd()
        pitch, hdg, roll = s_pitch(), s_hdg(), s_roll()
        sit = str(s_sit()).split(".")[-1]
        if sit in ("landed", "splashed"):
            return f"Hold autopilot: we're {sit} - holds off."
        a_f = min(1.0, dt / 0.25)
        q_f += ((pitch - last_pitch) / dt - q_f) * a_f
        p_f += (_wrap(roll - last_roll) / dt - p_f) * a_f
        r_f += (_wrap(hdg - last_hdg) / dt - r_f) * min(1.0, dt / 0.5)
        last_pitch, last_roll, last_hdg = pitch, roll, hdg
        if now - acc["t"] > 0.05:
            acc["f"] += ((spd - acc["last"]) / (now - acc["t"]) - acc["f"]) * min(1.0, (now - acc["t"]) / 1.5)
            acc["last"], acc["t"] = spd, now
        eas = math.sqrt(2.0 * max(s_q(), 0.0) / 1.225)
        g_q = gsched()
        if now - t_terr > 2.0:
            t_terr = now
            terr = terrain_ahead(body, s_lat(), s_lon(), hdg, spd)
        floor_msl = terr + TERRAIN_CLEAR
        pcmd = tgt.get("pitch")

        # ---------------- lateral
        bank_lim = speedcap.bank_limit(spd)  # 20 / 15 above 250 m/s unless an override raised it
        hdg_des = None
        if tgt.get("roll") is not None and STATUS.get("climbout_hdg") is None:
            bank_raw = _clamp(float(tgt["roll"]), -bank_lim, bank_lim)
        else:
            hdg_des = float(tgt["heading"]) % 360 if tgt.get("heading") is not None else hold_hdg0
            if STATUS.get("climbout_hdg") is not None:  # just took off: runway heading until > 150 m and climbing
                if tko.climbout_done(ralt, vs, ralt < 3.0):
                    STATUS.pop("climbout_hdg", None)
                else:
                    hdg_des = float(STATUS["climbout_hdg"])
            herr = _wrap(hdg_des - hdg)
            r_des = _clamp(HDG_K * herr, -MAX_TURN, MAX_TURN)
            r_cmd = r_des + TURN_KD * (r_des - r_f)
            bank_raw = _clamp(math.degrees(math.atan(max(spd, 20.0) * math.radians(r_cmd) / 9.81)), -bank_lim, bank_lim)
        if pcmd is not None and abs(float(pcmd)) > 60.0:
            bank_raw = 0.0  # steep commanded pitch: wings level (heading is meaningless near vertical)
        bank_des = _clamp(bank_raw, bank_prev - BANK_SLEW * dt, bank_prev + BANK_SLEW * dt)
        bank_prev = bank_des
        out("roll", ROLL_SIGN * min(1.0, g_q) * (0.014 * (bank_des - roll) - 0.01 * p_f), dt)
        out("yaw", 0.0, dt)

        # ---------------- altitude target (AGL = above the ground reference captured when it was set)
        alt_des = None
        if tgt.get("altitude") is not None:
            ref = str(tgt.get("altitude_ref", "agl")).lower()
            key = (tgt.get("altitude"), ref, tgt.get("t"))
            if key != ref_key:
                ref_key = key
                ref_elev = 0.0
                if ref == "agl":
                    try:
                        ref_elev = max(0.0, body.surface_height(s_lat(), s_lon()))
                    except Exception:  # noqa: BLE001
                        ref_elev = max(0.0, alt - ralt)
                    if tgt.get("ref_elev") is not None:
                        ref_elev = float(tgt["ref_elev"])
                STATUS["alt_ref_elev"] = round(ref_elev, 1)
            alt_des = float(tgt["altitude"]) + ref_elev
        elif tgt.get("vertical_speed") is None:
            alt_des = hold_alt0  # nothing set: hold the altitude we engaged at
        vs_t = tgt.get("vertical_speed")
        if alt_des is not None:
            up = abs(float(vs_t)) if vs_t is not None and float(vs_t) > 0 else CLIMB_VS_DEFAULT
            dn = abs(float(vs_t)) if vs_t is not None and float(vs_t) < 0 else 15.0
            # V/S from altitude error (not a hard altitude PID on the elevator) — softened by mass
            vs_des = _clamp(ALT_VS_K * kin * (alt_des - alt), -dn, up)
        else:
            vs_des = float(vs_t)
        terrain_hold = False
        if alt < floor_msl:  # terrain floor (look-ahead): climb, whatever the targets say
            vs_des = max(vs_des, _clamp(0.1 * (floor_msl - alt), 3.0, 30.0))
            terrain_hold = True
        climbing = vs_des > 3.0 and (alt_des is None or alt_des - alt > 150.0 or terrain_hold)

        # ---------------- commanded pitch attitude (plane_pitch: "pitch up 10" / "nose down 5")
        p_cmd, full_pwr = None, False
        if pcmd is not None:
            pcmd = float(pcmd)
            key = (pcmd, tgt.get("pitch_t"))
            if key != pc_key:
                pc_key = key
                pcyc.reset()
                if (pcmd and tgt.get("altitude") is not None and alt_des is not None
                        and (alt_des - alt) * math.copysign(1.0, pcmd) < 150.0):
                    # the altitude being held (or one on the other side): the pitch command replaces it
                    set_targets(altitude="off", ref_elev="off")
                    tgt.pop("altitude", None)
                    alt_des = None
                log.info("hold: pitch command %+.0f deg (alt %.0f, %.0f m/s; level-off below %.0f, resume at %.0f m/s)",
                         pcmd, alt, spd, pcyc.v_lo, pcyc.v_hi)
            alt_stop = alt_des if tgt.get("altitude") is not None else None
            cap = speedcap.limits()[1]
            p_cmd, full_pwr, ev = pcyc.step(pcmd, spd, s_aoa(), alt, vs, alt_stop, tgt.get("pitch_ceiling"), floor_msl,
                                            eas, cap)
            if ev == "level":
                _note(f"Pitch {pcmd:+.0f}: airspeed {spd:.0f} m/s near the stall margin ({pcyc.v_lo:.0f}) - leveling "
                      "off, full power to rebuild speed.")
            elif ev == "level_fast":
                _note(f"Pitch {pcmd:+.0f}: {eas:.0f} m/s EAS over the {cap:.0f} cap - leveling off until it comes back.")
            elif ev == "resume":
                _note(f"Pitch {pcmd:+.0f}: speed back ({spd:.0f} m/s) - returning to the commanded pitch.")
            elif p_cmd is None:
                extra = {} if ev == "target" else dict(altitude=float(round(alt)), altitude_ref="msl", ref_elev="off")
                set_targets(**{k: "off" for k in PITCH_KEYS}, **extra)
                tgt = targets()
                _note({"target": f"Pitch hold ended: target altitude {alt_stop or 0:.0f} m reached - holding it.",
                       "ceiling": f"Pitch hold ended at the safe ceiling (~{float(tgt.get('altitude') or alt):.0f} m) - "
                                  "holding altitude.",
                       "terrain": f"Pitch hold ended: terrain floor {floor_msl:.0f} m ahead - holding "
                                  f"{alt:.0f} m (terrain climb if needed)."}[ev])
                pcmd = None
        if was_pitch and pcmd is None:
            hold_alt0 = alt  # pitch command over and no altitude target: hold where it left us
        was_pitch = pcmd is not None

        # ---------------- pitch
        aoa = s_aoa()
        stalling = aoa > 15.0 or spd < 1.15 * vs0
        e_vs = vs_des - vs
        if p_cmd is not None:  # the player's commanded attitude (or the level-off of its cycle)
            pd_new = p_cmd
            pitch_base = _clamp(pitch_des_prev, -5.0, 12.0)  # smooth hand-back to the V/S loop afterwards
        elif climbing:  # pitch holds the climb attitude band; the throttle chases V/S
            p_climb = speedcap.climb_pitch(10.0 if alt < 5500.0 else (7.5 if alt < 8000.0 else 5.0))
            if stalling:
                p_climb = min(p_climb, max(pitch, 2.0))
            pd_new = p_climb
            pitch_base = pitch_des_prev
        else:
            # speed soften × inertia scale: heavy + fast → smallest V/S→pitch gains
            ks = _clamp(100.0 / max(spd, 50.0), 0.25, 1.0) * kin
            pitch_base = _clamp(pitch_base + VS_PITCH_I * ks * e_vs * dt, -5.0, 12.0)
            pd_new = _clamp(pitch_base + VS_PITCH_P * ks * e_vs, -5.0, 12.0)
            lo = -5.0 if vs_des < -1.0 else -2.0  # nose-down only for a commanded descent; level hold trims to -2
            if stalling:
                lo = max(lo, min(max(pitch, 0.0) + 1.0, 6.0))
            pd_new = max(pd_new, lo)
        # pitch-rate cap on the command + extra damping when the nose is whipping
        rate_lim = PITCH_RATE * (0.7 + 0.3 * kin)
        pd_new = _clamp(pd_new, pitch_des_prev - rate_lim * dt, pitch_des_prev + rate_lim * dt)
        pitch_des_prev = pd_new
        e_p = pd_new - pitch
        i_pitch = _clamp(i_pitch + PITCH_KI * kin * e_p * dt, -0.35, 0.35)
        kd = PITCH_KD * (1.0 + 0.6 * (1.0 - kin)) + 0.015 * max(0.0, abs(q_f) - Q_SOFT_CAP)
        out("pitch", PITCH_SIGN * (max(g_q, 0.25) * (PITCH_KP * kin * e_p - kd * q_f) + i_pitch), dt)

        # ---------------- throttle
        spd_t = tgt.get("speed")
        c = cmd["throttle"]
        thr_man = tgt.get("throttle")  # manual throttle override ("throttle 60"): only stall protection overrides it
        if thr_man is not None and not (stalling or spd < v_min_safe or (p_cmd is not None and pcyc.leveled and full_pwr)):
            cmd["throttle"] = _clamp(float(thr_man), THR_FLOOR, 1.0)
            ctl.throttle = emergency.shape("throttle", cmd["throttle"])
        elif p_cmd is not None and (full_pwr or stalling or spd < v_min_safe):
            urgent = pcyc.leveled or stalling or spd < v_min_safe  # level-off: power up now, never cut it
            if not urgent and eas > speedcap.limits()[1]:
                throttle_step(c - 0.05, 1.0, floor=0.3)
            else:
                throttle_step(1.0, 1.0 if urgent else 3.0)
        elif stalling or spd < v_min_safe:
            throttle_step(1.0, 1.0)  # safety: add power now
        elif climbing and p_cmd is None:
            # V/S via throttle, airspeed ignored (Luke) - 5 % steps every 3 s
            want = c + (0.05 if e_vs > 2.0 else (-0.05 if e_vs < -4.0 else 0.0))
            throttle_step(want, 3.0)
        else:
            if spd_t is not None:
                e_s = float(spd_t) - spd
                a_ = acc["f"]
                want = c
                if e_s > 3.0 and a_ < 0.3:
                    want = c + 0.05
                elif e_s < -3.0 and a_ > -0.3:
                    want = c - 0.05
            else:
                want = c
            if eas > speedcap.limits()[1]:  # 220 EAS unless an override raised it
                throttle_step(min(want, c - 0.05), 1.0)
            else:
                throttle_step(want, 4.0)

        hdg_err = round(_wrap((hdg_des if hdg_des is not None else hdg) - hdg), 1)
        STATUS.update(phase="hold", alt_msl=round(alt), alt_agl=round(alt - (ref_elev or 0.0)), radar=round(ralt),
                      vs=round(vs, 1), vs_des=round(vs_des, 1), speed=round(spd, 1), eas=round(eas, 1),
                      hdg=round(hdg), hdg_des=None if hdg_des is None else round(hdg_des), hdg_err=hdg_err,
                      bank=round(roll), bank_des=round(bank_des, 1), pitch=round(pitch, 1), pitch_des=round(pd_new, 1),
                      throttle=round(cmd["throttle"], 2), terrain_floor=round(floor_msl), climbing=climbing,
                      pitch_cmd=pcmd, pitch_leveled=pcyc.leveled if pcmd is not None else None,
                      lat=round(s_lat(), 6), lon=round(s_lon(), 6),
                      targets={k: tgt.get(k) for k in ("altitude", "altitude_ref", "vertical_speed", "heading", "roll", "speed",
                                                       "pitch", "throttle")})
        if now - log_t > 5.0:
            log_t = now
            log.info("hold: alt=%.0f(agl %.0f, radar %.0f) vs=%.1f/%.1f spd=%.0f hdg=%.0f err=%+.0f bank=%.0f/%.0f pitch=%.1f/%.1f thr=%.2f floor=%.0f",
                     alt, alt - (ref_elev or 0.0), ralt, vs, vs_des, spd, hdg, hdg_err, roll, bank_des, pitch, pd_new,
                     cmd["throttle"], floor_msl)
        try:
            from . import trim_auto
            trim_msg = trim_auto.maybe_trim_hold(v, ctl, tgt, vs, roll, spd, alt, climbing, pcmd, now)
            if trim_msg:
                _note(trim_msg)
        except Exception:  # noqa: BLE001
            pass
        time.sleep(0.03)


def summary():
    st = STATUS
    if st.get("phase") != "hold":
        return f"Hold autopilot: {st.get('phase')}" + (f" ({st.get('result')})" if st.get("result") else "")
    t = st.get("targets") or {}
    parts = []
    if t.get("altitude") is not None:
        parts.append(f"alt {t['altitude']:.0f} m {str(t.get('altitude_ref', 'agl')).upper()}")
    if t.get("vertical_speed") is not None:
        parts.append(f"V/S {t['vertical_speed']:+.0f}")
    if t.get("heading") is not None:
        parts.append(f"hdg {t['heading']:.0f}")
    if t.get("roll") is not None:
        parts.append(f"roll {t['roll']:.0f}")
    if t.get("speed") is not None:
        parts.append(f"spd {t['speed']:.0f}")
    if t.get("throttle") is not None:
        parts.append(f"manual throttle {100 * float(t['throttle']):.0f}%")
    if t.get("pitch") is not None:
        parts.append(f"pitch {t['pitch']:+.0f}" + (" (leveled off: rebuilding speed)" if st.get("pitch_leveled") else ""))
    he = st.get("hdg_err")
    return (f"Hold [{', '.join(parts) or 'current alt/hdg'}]: alt {st.get('alt_agl')} m AGL (radar {st.get('radar')}), "
            f"V/S {st.get('vs')}/{st.get('vs_des')}, {st.get('speed')} m/s, hdg {st.get('hdg')}"
            + (f" err {he:+.0f}" if he is not None else "") + f", thr {st.get('throttle')}")
