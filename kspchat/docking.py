"""Rendezvous + dock with a vessel in orbit (e.g. a refuel station), using MechJeb's rendezvous and docking
autopilots through kRPC.MechJeb. Runs in a background thread with its own kRPC connection and posts the
result to chat. EXPERIMENTAL: built on the exposed MechJeb API, not yet flight-tested.

Port choice: our ready (free) docking ports vs the station's ready ports of the SAME size (by part name /
title: Jr = 0.625 m, standard 1.25 m, Sr = 2.5 m); picks the free station port nearest to us.
"""
import logging
import math
import threading
import time

from . import config, guard

log = logging.getLogger("kspchat")
STATUS = {"phase": "idle"}
_thread = None
_stop = threading.Event()


def active():
    return _thread is not None and _thread.is_alive()


def stop():
    _stop.set()


def port_size(port):
    p = port.part
    s = (p.name + " " + p.title).lower()
    if "jr" in s or "junior" in s or "dockingport3" in s or "0.625" in s:
        return "0.625m (Jr)"
    if "sr" in s or "senior" in s or "large" in s or "2.5" in s:
        return "2.5m (Sr)"
    if "1.875" in s:
        return "1.875m"
    return "1.25m"


def _ready(port):
    return str(port.state).split(".")[-1] == "ready"


def plan(sc, station):
    """Feasibility + port pick. Returns (dict, None) or (None, error)."""
    v = sc.active_vessel
    if str(v.situation).split(".")[-1] != "orbiting":
        return None, "Docking needs us in a stable orbit first."
    if station.orbit.body.name != v.orbit.body.name:
        return None, f"'{station.name}' orbits {station.orbit.body.name}; we're around {v.orbit.body.name}."
    ours = [p for p in v.parts.docking_ports if _ready(p)]
    if not ours:
        return None, "This ship has no free docking port."
    theirs = [p for p in station.parts.docking_ports if _ready(p)]
    if not theirs:
        return None, f"'{station.name}' has no free docking port."
    sizes = {}
    for p in ours:
        sizes.setdefault(port_size(p), p)
    cands = [p for p in theirs if port_size(p) in sizes]
    if not cands:
        return None, (f"No compatible free port: ours {sorted(sizes)}, theirs {sorted({port_size(p) for p in theirs})}.")
    ref = v.reference_frame
    def dist(p):
        x, y, z = p.position(ref)
        return math.sqrt(x * x + y * y + z * z)
    target_port = min(cands, key=dist)
    our_port = sizes[port_size(target_port)]
    # delta-v estimate: Hohmann between the two orbits + plane change + ~40 m/s for phasing/terminal
    mu = v.orbit.body.gravitational_parameter
    r1, r2 = v.orbit.semi_major_axis, station.orbit.semi_major_axis
    a_t = (r1 + r2) / 2
    hoh = abs(math.sqrt(mu / r1) * (math.sqrt(r2 / a_t) - 1)) + abs(math.sqrt(mu / r2) * (1 - math.sqrt(r1 / a_t)))
    di = abs(v.orbit.relative_inclination(station.orbit))
    plane = 2 * math.sqrt(mu / max(r1, r2)) * math.sin(di / 2)
    need = hoh + plane + 40
    rcs = len(v.parts.rcs) > 0 and v.resources.amount("MonoPropellant") > 1
    return {"our_port": our_port, "target_port": target_port, "size": port_size(target_port),
            "dv_needed": round(need), "rcs": rcs, "rel_incl_deg": round(math.degrees(di), 2)}, None


def start(station_name):
    global _thread
    if active():
        return "A rendezvous/docking is already in progress."
    _stop.clear()
    _thread = threading.Thread(target=_run, args=(station_name,), daemon=True, name="docking")
    _thread.start()
    return "started"


def _notify(text):
    try:
        from . import science
        science.post_event(text)
    except Exception:
        pass


def _run(station_name):
    import krpc
    STATUS.clear()
    STATUS.update(phase="starting", station=station_name, started=time.time())
    end = guard.hold("docking", _stop)
    conn = None
    try:
        conn = krpc.connect(name="KSPChatBridge-dock", address=config.KRPC_ADDRESS,
                            rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)
        result = _fly(conn, station_name)
    except Exception as e:
        result = f"Docking error: {e.__class__.__name__}: {e}"
        try:
            mj = conn.mech_jeb
            mj.rendezvous_autopilot.enabled = False
            mj.docking_autopilot.enabled = False
        except Exception:
            pass
    end()
    STATUS.update(phase="done", result=result)
    log.info("docking: %s", result)
    _notify(result)
    if conn:
        try:
            conn.close()
        except Exception:
            pass


def _fly(conn, station_name):
    sc = conn.space_center
    v = sc.active_vessel
    station = next((x for x in sc.vessels if x.name == station_name and x != v), None)
    if station is None:
        return f"Lost track of '{station_name}'."
    p, err = plan(sc, station)
    if err:
        return err
    mj = conn.mech_jeb
    sc.target_vessel = station
    # 1) rendezvous to ~100 m
    dist = lambda: math.dist((0, 0, 0), station.position(v.reference_frame))
    if dist() > 300:
        STATUS["phase"] = "rendezvous"
        ra = mj.rendezvous_autopilot
        ra.desired_distance = 100.0
        ra.max_phasing_orbits = 5
        ra.enabled = True
        t_end = time.time() + 6 * 3600
        while time.time() < t_end:
            if _stop.is_set():
                ra.enabled = False
                return "Rendezvous aborted by request."
            STATUS.update(distance_m=round(dist()), mj_status=str(ra.status))
            if not ra.enabled:
                break
            time.sleep(2)
        ra.enabled = False
        if dist() > 500:
            return f"Rendezvous ended {dist():.0f} m from {station.name} (MechJeb: {STATUS.get('mj_status')})."
    # 2) dock: control from our port, target their port, MechJeb docking autopilot (RCS)
    STATUS["phase"] = "docking"
    v.parts.controlling = p["our_port"].part
    sc.target_docking_port = p["target_port"]
    v.control.rcs = True
    da = mj.docking_autopilot
    da.speed_limit = 1.0
    da.enabled = True
    t_end = time.time() + 1800
    while time.time() < t_end:
        if _stop.is_set():
            da.enabled = False
            return "Docking aborted by request."
        st = str(p["our_port"].state).split(".")[-1]
        STATUS.update(distance_m=round(dist()), mj_status=str(da.status), port_state=st)
        if st == "docked":
            return f"Docked with {station.name} ({p['size']} port)."
        if not da.enabled and st not in ("docking", "docked"):
            return f"MechJeb docking autopilot stopped: {da.status}"
        time.sleep(1)
    da.enabled = False
    return "Docking timed out after 30 min."
