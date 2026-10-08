"""Landing spots + landing distance/ETA estimates.

Spots: built-ins (KSC pad, runway 09/27, Island Airfield) plus Luke's own, stored in bridge_settings.json
under "landing_spots". A spot is V (vertical: a point for rockets) or H (horizontal: a runway for planes,
given by two thresholds a/b; landing direction names[0] = a->b, names[1] = b->a).

Targets for "land at ..." resolve in this order: saved spot name, "target" (the KSP target: vessel, flag
or docking port's vessel), a vessel/flag name (a "radio beacon"), a contract waypoint name.
"""
import math
import threading
import time

from . import config, lander, plane, settings

R_DEFAULT = 600_000.0
BUILTIN = {
    "KSC Pad": {"mode": "V", "body": "Kerbin", "lat": -0.0972, "lon": -74.5577},
    "Runway 09": {"mode": "H", "body": "Kerbin", "strip": "KSC", "runway": "09"},
    "Runway 27": {"mode": "H", "body": "Kerbin", "strip": "KSC", "runway": "27"},
    # Island Airfield: the real strip runs EAST-WEST (Luke, 2026-10-08: lined up at -1.51608, -71.85674, heading
    # 270.7, MSL 134.6 at the east end). a = east threshold, b = 1.1 km west. Landing 27 (westbound, a->b) is the
    # default. (The old N-S line at lon -71.885 from the launch-site spawn was wrong: that spawn faces a cliff.)
    "Island Airfield": {"mode": "H", "body": "Kerbin", "a": [-1.516092, -71.856744], "b": [-1.514809, -71.961815],
                        "alt": 134.6, "names": ["27", "09"], "runway": "27"},
}


def _norm(s):
    return " ".join(str(s).lower().replace("_", " ").split())


def all_spots():
    out = {k: dict(v, builtin=True, name=k) for k, v in BUILTIN.items()}
    for k, v in (settings.get("landing_spots") or {}).items():
        out[k] = dict(v, builtin=False)
    return out


def find_spot(name):
    spots = all_spots()
    n = _norm(name)
    for k, v in spots.items():
        if _norm(k) == n:
            return k, v
    hits = [(k, v) for k, v in spots.items() if n and (n in _norm(k) or _norm(k) in n)]
    return hits[0] if len(hits) == 1 else (None, None)


def offset(lat, lon, hdg_deg, dist_m, radius=R_DEFAULT):
    h = math.radians(hdg_deg)
    dlat = math.degrees(dist_m * math.cos(h) / radius)
    dlon = math.degrees(dist_m * math.sin(h) / (radius * max(0.05, math.cos(math.radians(lat)))))
    return lat + dlat, lon + dlon


def gc_dist(lat1, lon1, lat2, lon2, radius=R_DEFAULT):
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dp, dl = p2 - p1, math.radians(lon2 - lon1)
    a = math.sin(dp / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * radius * math.asin(min(1.0, math.sqrt(a)))


def bearing(lat1, lon1, lat2, lon2):
    p1, p2, dl = math.radians(lat1), math.radians(lat2), math.radians(lon2 - lon1)
    y = math.sin(dl) * math.cos(p2)
    x = math.cos(p1) * math.sin(p2) - math.sin(p1) * math.cos(p2) * math.cos(dl)
    return math.degrees(math.atan2(y, x)) % 360


def save(name, spot):
    name = str(name).strip()[:40]
    if not name:
        return "Give the spot a name."
    if name in BUILTIN:
        return f"'{name}' is a built-in spot; pick another name."
    data = settings.get("landing_spots") or {}
    if len(data) >= 50 and name not in data:
        return "50 saved spots max; delete one first."
    data[name] = spot
    settings.put("landing_spots", data)
    return "saved"


def delete(name):
    data = settings.get("landing_spots") or {}
    k = next((k for k in data if _norm(k) == _norm(name)), None)
    if not k:
        return f"No saved spot '{name}' (built-ins can't be deleted)."
    data.pop(k)
    settings.put("landing_spots", data)
    return f"Deleted landing spot '{k}'."


def make_strip(spot, radius=R_DEFAULT):
    """STRIPS-style dict for an H spot (None if it has no runway info)."""
    if spot.get("strip"):
        return plane.STRIPS[spot["strip"]]
    if spot.get("a") and spot.get("b"):
        return {"body": spot.get("body", "Kerbin"), "a": tuple(spot["a"]), "b": tuple(spot["b"]),
                "alt": spot.get("alt"), "names": tuple(spot.get("names") or ("A", "B")), "name": spot.get("name", "")}
    if spot.get("heading") is not None and spot.get("lat") is not None:
        h = float(spot["heading"]) % 360
        b = offset(spot["lat"], spot["lon"], h, float(spot.get("length", 1000)), radius)
        n1 = f"{round(h / 10) % 36 or 36:02d}"
        n2 = f"{round(((h + 180) % 360) / 10) % 36 or 36:02d}"
        return {"body": spot.get("body", "Kerbin"), "a": (spot["lat"], spot["lon"]), "b": b, "alt": None,
                "names": (n1, n2), "name": spot.get("name", "")}
    return None


def spot_point(spot):
    """(lat, lon) a V landing aims at; for runways the midpoint."""
    if spot.get("lat") is not None:
        return spot["lat"], spot["lon"]
    s = make_strip(spot)
    return (s["a"][0] + s["b"][0]) / 2, (s["a"][1] + s["b"][1]) / 2


def resolve(sc, name):
    """-> (label, spot dict) or (None, error). Handles saved spots, 'target', vessel/flag and waypoint names."""
    v = sc.active_vessel
    body = v.orbit.body
    n = _norm(name)
    if n in ("", "target", "my target", "the target", "ksp target", "current target", "selected target"):
        tv = None
        try:
            tv = sc.target_vessel
            if tv is None and sc.target_docking_port is not None:
                tv = sc.target_docking_port.part.vessel
        except Exception:
            tv = None
        if tv is None:
            wps = _waypoints(sc, body)
            hint = (" Waypoints here: " + ", ".join(w["name"] for w in wps[:6]) + ".") if wps else ""
            return None, "No KSP target set. Set a vessel or flag as target on the map, or name a spot." + hint
        return _vessel_spot(tv, body)
    k, s = find_spot(name)
    if k:
        return k, s
    for tv in sc.vessels:
        try:
            if tv != v and _norm(tv.name) == n:
                return _vessel_spot(tv, body)
        except Exception:
            continue
    for w in _waypoints(sc, body):
        if _norm(w["name"]) == n or n in _norm(w["name"]):
            return w["name"], {"mode": "V", "body": body.name, "lat": w["lat"], "lon": w["lon"], "kind": "waypoint"}
    return None, f"Unknown landing spot/target '{name}'. Saved spots: " + ", ".join(all_spots())


def _vessel_spot(tv, body):
    sit = str(tv.situation).split(".")[-1]
    vb = tv.orbit.body
    kind = str(tv.type).split(".")[-1]
    if sit in ("orbiting", "escaping") or (sit == "sub_orbital" and tv.orbit.periapsis_altitude > 0):
        return tv.name, {"mode": "DOCK", "body": vb.name, "vessel": tv.name}
    if vb.name != body.name:
        return None, f"'{tv.name}' is on {vb.name}; we're at {body.name}."
    f = tv.flight(vb.reference_frame)
    return tv.name, {"mode": "V", "body": vb.name, "lat": f.latitude, "lon": f.longitude, "kind": kind,
                     "offset_m": 40 if kind not in ("flag",) else 25}


def _waypoints(sc, body):
    out = []
    try:
        for w in sc.waypoint_manager.waypoints:
            if w.body.name == body.name:
                out.append({"name": w.name, "lat": w.latitude, "lon": w.longitude})
    except Exception:
        pass
    return out


# ---------------- landing distance / ETA ----------------
_eta_conn = None
_eta_lock = threading.Lock()


def _econn():
    global _eta_conn
    if _eta_conn is None:
        import krpc
        _eta_conn = krpc.connect(name="KSPChatBridge-eta", address=config.KRPC_ADDRESS,
                                 rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)
    return _eta_conn


def _fmt_t(t):
    if t is None:
        return "?"
    t = int(round(t))
    return f"{t // 3600}h{(t % 3600) // 60:02d}m" if t >= 3600 else f"{t // 60}:{t % 60:02d}"


def _impact(v, body, now):
    """Vacuum ballistic impact point from the current orbit: (lat, lon, ut) or None."""
    o = v.orbit
    bref = body.reference_frame
    f = v.flight(bref)
    terr = max(0.0, body.surface_height(f.latitude, f.longitude))
    for _ in range(3):
        r = body.equatorial_radius + terr
        if o.periapsis >= r:
            return None
        try:
            ta = o.true_anomaly_at_radius(r)
            ut = o.ut_at_true_anomaly(-abs(ta))
            if ut < now:
                ut = o.ut_at_true_anomaly(abs(ta))
        except Exception:
            return None
        pos = o.position_at(ut, bref)
        lat = body.latitude_at_position(pos, bref)
        lon = body.longitude_at_position(pos, bref) - math.degrees(body.rotational_speed * (ut - now))
        lon = (lon + 180) % 360 - 180
        terr = max(0.0, body.surface_height(lat, lon))
    return lat, lon, ut


def landing_eta(conn=None, quiet=False):
    """Estimate where/when the active vessel lands. Returns a dict (all values estimates)."""
    with _eta_lock:
        c = conn or _econn()
        try:
            return _landing_eta(c, quiet)
        except Exception as e:
            global _eta_conn
            if conn is None:
                try:
                    _eta_conn.close()
                except Exception:
                    pass
                _eta_conn = None
            return {"active": False, "error": f"{e.__class__.__name__}: {e}"}


def _landing_eta(c, quiet):
    sc = c.space_center
    v = sc.active_vessel
    body = v.orbit.body
    R = body.equatorial_radius
    sit = str(v.situation).split(".")[-1]
    if sit in ("landed", "splashed", "pre_launch", "docked"):
        return {"active": False, "summary": f"On the ground ({sit})."}
    bref = body.reference_frame
    f = v.flight(bref)
    lat, lon = f.latitude, f.longitude
    gs, vs, h = f.horizontal_speed, f.vertical_speed, max(0.0, f.surface_altitude)
    now = sc.ut
    out = {"estimated": True, "altitude_m": round(h), "vertical_speed": round(vs, 1), "ground_speed": round(gs, 1)}

    from . import guard, hold
    if hold.active() and hold.STATUS.get("phase") == "hold":
        out.update(active=True, mode="hold", hdg_err=hold.STATUS.get("hdg_err"), summary=hold.summary())
        return out
    claim = guard.busy() or {}
    if plane.active() and plane.STATUS.get("threshold"):
        st = plane.STATUS
    elif claim.get("kind") == "plane" and (claim.get("info") or {}).get("threshold"):
        st = claim["info"]  # plane autoland running in another process (MCP server / test harness)
    else:
        st = None
    if st:
        tl = st["threshold"]
        d_thr = gc_dist(lat, lon, tl[0], tl[1], R)
        path = d_thr
        va = float(st.get("approach_speed") or gs or 1)
        eta = None
        if st.get("phase") in ("to_entry", "stall_test", "recover") and st.get("entry"):
            e = st["entry"]
            d_e, d_f = gc_dist(lat, lon, e[0], e[1], R), gc_dist(e[0], e[1], tl[0], tl[1], R)
            path = d_e + d_f
            if gs > 5:  # to the entry at the current speed, the final at ~approach speed (+15%)
                eta = d_e / gs + d_f / (va * 1.15)
        elif gs > 5:
            eta = d_thr / max(gs, va * 0.9)
        out.update(active=True, mode="H", target=f"runway {st.get('runway', '?')}", phase=st.get("phase"),
                   distance_km=round(d_thr / 1000, 2), path_km=round(path / 1000, 1), eta_s=eta and round(eta),
                   method="great-circle distance to the threshold / ground speed; via the approach entry point (final flown at ~approach speed) if not yet on final")
        he = st.get("hdg_err")
        out.update(hdg_err=he, hdg_des=st.get("hdg_des"))
        out["summary"] = (f"Plane autoland ({st.get('phase')}): runway {st.get('runway', '?')} threshold "
                          f"{d_thr / 1000:.1f} km away, about {_fmt_t(eta)} to touchdown (est.)."
                          + (f" Heading err {he:+.0f} deg ({'turn right' if he > 0 else 'turn left'} to "
                             f"{st.get('hdg_des')})." if he is not None and abs(he) >= 1 else
                             (" On heading." if he is not None else "")))
        return out

    chutes = 0
    try:
        chutes = sum(1 for p in v.parts.parachutes if p.deployed)
    except Exception:
        pass
    lst = lander.STATUS if lander.active() else ((claim.get("info") or {}) if claim.get("kind") == "lander" else {})
    mj_landing = False
    try:
        mj = c.mech_jeb
        mj_landing = bool(mj.api_ready and mj.landing_autopilot.enabled)
    except Exception:
        pass
    descending = vs < -1.0
    if quiet and not (lst or mj_landing or (chutes and descending)):
        return {"active": False}  # live readout: only while a landing is actually happening
    if not (lst or mj_landing or chutes) and v.parts.wheels and gs > 30 and gs > 3 * abs(vs) \
            and f.mean_altitude < body.atmosphere_depth and body.name == "Kerbin":
        # flying a plane without autoland: distance to the nearer KSC threshold
        best = min(((gc_dist(lat, lon, *plane.STRIPS["KSC"][k], R), n)
                    for k, n in (("a", "09"), ("b", "27"))))
        eta = best[0] / gs
        out.update(active=True, mode="H", target=f"KSC runway {best[1]} threshold", distance_km=round(best[0] / 1000, 2),
                   eta_s=round(eta), method="great-circle distance / ground speed (straight line, no approach pattern)")
        out["summary"] = (f"KSC runway {best[1]} threshold is {best[0] / 1000:.1f} km away, about {_fmt_t(eta)} at the "
                          "current ground speed (est., straight line).")
        return out

    imp = _impact(v, body, now)
    target = lst.get("target") if lst else None
    method_t = ""
    eta = None
    phase = lst.get("phase", "")
    if phase in ("descent", "final"):
        eta = 2 * h / -vs if descending else None
        method_t = "suicide-burn model (constant deceleration to touchdown: 2 x height / sink rate)"
    elif chutes and descending:
        eta = h / -vs
        method_t = "height / current sink rate (under parachutes)"
    elif imp:
        eta = imp[2] - now
        method_t = "ballistic (vacuum) time to impact from the current orbit; drag, chutes and burns make it longer"
    elif descending:
        eta = h / -vs
        method_t = "height / current sink rate"
    out.update(active=True, mode="V", phase=phase or ("parachutes" if chutes else ("MechJeb landing" if mj_landing else "")),
               eta_s=eta and round(eta), eta_method=method_t)
    parts = []
    if imp:
        d_imp = gc_dist(lat, lon, imp[0], imp[1], R)
        out.update(impact_lat=round(imp[0], 4), impact_lon=round(imp[1], 4), impact_distance_km=round(d_imp / 1000, 2),
                   impact_method="vacuum ballistic prediction from the orbit (drag shortens it in atmosphere)")
        parts.append(f"predicted impact {d_imp / 1000:.1f} km away")
    if target:
        ref = imp[:2] if imp else (lat, lon)
        miss = gc_dist(ref[0], ref[1], target[0], target[1], R)
        out.update(target=list(target), miss_km=round(miss / 1000, 2),
                   distance_to_target_km=round(gc_dist(lat, lon, target[0], target[1], R) / 1000, 2))
        parts.append(f"target {out['distance_to_target_km']:.1f} km away" + (f", predicted miss {miss / 1000:.1f} km" if imp else ""))
    label = "Landing" + (f" ({out['phase']})" if out["phase"] else "")
    out["summary"] = f"{label}: " + (", ".join(parts) + ", " if parts else "") + f"about {_fmt_t(eta)} to touchdown (est.)."
    return out
