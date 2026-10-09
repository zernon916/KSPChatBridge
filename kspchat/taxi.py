"""Ground taxi (AICS > Taxi / Base Run, taxi_to tool, Flight Plan 'taxi to ...').

Drives a craft on the ground to one or more waypoints at a capped speed, in straight lines (no obstacle avoidance).
Rovers: wheel motors (wheel_throttle); planes: engine throttle + wheel brakes. Steering: wheel steering + a little
rudder. Own kRPC connection in a background thread; claims the craft via guard ("taxi"), so abort() stops it.
"""
import logging
import math
import re
import threading
import time

from . import config, guard, krpcx, plane, spots

log = logging.getLogger("kspchat")
STATUS = {"phase": "idle"}
_thread = None
_stop = threading.Event()

# built-in taxi points: runway starts = the threshold you take off FROM (09 = west end, 27 = east end)
_ISL = spots.BUILTIN["Island Airfield"]
POINTS = {
    "Runway 09 start": tuple(plane.RWY_W),
    "Runway 27 start": tuple(plane.RWY_E),
    "Runway middle": ((plane.RWY_W[0] + plane.RWY_E[0]) / 2, (plane.RWY_W[1] + plane.RWY_E[1]) / 2),
    "KSC Pad": (-0.0972, -74.5577),
    "Island 27 start": tuple(_ISL["a"]),
    "Island 09 start": tuple(_ISL["b"]),
}


def points():
    """name -> (lat, lon): built-ins + Luke's saved spots (V = the point, H = its first threshold)."""
    out = dict(POINTS)
    for k, s in spots.all_spots().items():
        if s.get("builtin"):
            continue
        if s.get("lat") is not None:
            out[k] = (s["lat"], s["lon"])
        elif s.get("a"):
            out[k] = tuple(s["a"])
    return out


def resolve(name):
    """(label, lat, lon) or None. Accepts a point name, a saved spot or 'lat,lon'."""
    m = re.match(r"^\s*(-?\d+(?:\.\d+)?)\s*[, ]\s*(-?\d+(?:\.\d+)?)\s*$", name or "")
    if m:
        return (f"{float(m.group(1)):.4f},{float(m.group(2)):.4f}", float(m.group(1)), float(m.group(2)))
    pts = points()
    n = spots._norm(name)
    for k, (la, lo) in pts.items():
        if spots._norm(k) == n:
            return (k, la, lo)
    hits = [(k, la, lo) for k, (la, lo) in pts.items() if n and n in spots._norm(k)]
    return hits[0] if len(hits) == 1 else None


def active():
    return _thread is not None and _thread.is_alive()


def stop():
    _stop.set()


def start(waypoints, speed=8.0):
    global _thread
    if active():
        return "Already taxiing ('abort' first)."
    _stop.clear()
    _thread = threading.Thread(target=_run, args=(list(waypoints), float(speed)), daemon=True, name="taxi")
    _thread.start()
    return "started"


def _run(wps, cap):
    import krpc
    STATUS.clear()
    STATUS.update(phase="starting", started=time.time())
    end = guard.hold("taxi", _stop, lambda: dict(STATUS))
    conn = None
    try:
        conn = krpc.connect(name="KSPChatBridge-taxi", address=config.KRPC_ADDRESS,
                            rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)
        result = _drive(conn, wps, cap)
    except Exception as e:  # noqa: BLE001
        result = f"Taxi error: {e.__class__.__name__}: {e}"
        log.exception("taxi")
    end()
    STATUS["phase"] = "done"
    STATUS["result"] = result
    log.info("taxi: %s", result)
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


def _drive(conn, wps, cap):
    v = conn.space_center.active_vessel
    body = v.orbit.body
    R = body.equatorial_radius
    ctl = v.control
    fb = v.flight(body.reference_frame)
    fs = v.flight(v.surface_reference_frame)
    s_lat = conn.add_stream(getattr, fb, "latitude")
    s_lon = conn.add_stream(getattr, fb, "longitude")
    s_spd = conn.add_stream(getattr, fb, "horizontal_speed")
    s_hdg = conn.add_stream(getattr, fs, "heading")
    s_sit = conn.add_stream(getattr, v, "situation")
    powered = False
    for w in v.parts.wheels:
        try:
            powered = powered or (w.powered and w.motor_enabled)
        except Exception:  # noqa: BLE001
            pass
    try:
        krpcx.autopilot(v.auto_pilot, False)
    except Exception:  # noqa: BLE001
        pass
    ctl.sas = False
    try:
        from . import parking
        parking.release("taxi")
    except Exception:  # noqa: BLE001
        pass
    ctl.brakes = False
    if not powered and v.available_thrust <= 0:
        ctl.activate_next_stage()  # engines not started yet (plane on the runway)
        time.sleep(1.0)
    STATUS.update(mode="wheel motors" if powered else "engines + brakes")
    thr = 0.0

    def halt():
        ctl.throttle = 0.0
        ctl.wheel_throttle = 0.0
        ctl.wheel_steering = 0.0
        ctl.yaw = 0.0
        ctl.brakes = True

    try:
        for i, (label, la, lo) in enumerate(wps):
            last_wp = i == len(wps) - 1
            arrive = 12.0 if last_wp else 25.0
            d0 = spots.gc_dist(s_lat(), s_lon(), la, lo, R)
            t_end = time.time() + d0 / 1.0 + 180.0
            while True:
                if _stop.is_set():
                    halt()
                    return "Taxi stopped."
                if time.time() > t_end:
                    halt()
                    return f"Taxi gave up: not at {label} in time (stuck? obstacle?)."
                sit = str(s_sit()).split(".")[-1]
                if sit not in ("landed", "pre_launch"):
                    halt()
                    return f"Taxi stopped: we're {sit}."
                lat, lon, spd, hdg = s_lat(), s_lon(), s_spd(), s_hdg()
                d = spots.gc_dist(lat, lon, la, lo, R)
                if d < arrive:
                    break
                brg = spots.bearing(lat, lon, la, lo)
                herr = (brg - hdg + 180.0) % 360.0 - 180.0
                v_des = min(cap, max(2.0, d / 6.0))
                if abs(herr) > 30:
                    v_des = min(v_des, 3.0)
                ctl.wheel_steering = max(-1.0, min(1.0, -0.04 * herr))
                ctl.yaw = max(-0.3, min(0.3, 0.02 * herr))
                e = v_des - spd
                if powered:
                    ctl.wheel_throttle = max(-0.3, min(1.0, 0.25 * e))
                    ctl.brakes = e < -3.0
                else:
                    thr = max(0.0, min(0.4, thr + 0.01 * e))
                    ctl.throttle = thr
                    ctl.brakes = e < -2.0
                STATUS.update(phase="taxi", to=label, dist=round(d), speed=round(spd, 1), hdg_err=round(herr))
                time.sleep(0.1)
        halt()
        return f"Taxi: arrived at {wps[-1][0]}; brakes on."
    except Exception:
        halt()
        raise


def summary():
    st = STATUS
    if st.get("phase") == "taxi":
        return f"Taxi to {st.get('to')}: {st.get('dist')} m, {st.get('speed')} m/s, heading error {st.get('hdg_err'):+d}"
    return ""
