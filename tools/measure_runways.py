"""Measure runway thresholds by spawning a plane at each stock launch site and raycasting along the runway.

  python tools/measure_runways.py [craft] [site ...]     (KSP running, Grok Test; replaces the active vessel)
Prints one JSON line per site: start/end lat/lon (thresholds), heading, runway altitude, length.
"""
import json
import math
import sys
import time

import krpc

craft = sys.argv[1] if len(sys.argv) > 1 else "Aeris 3A"
sites = sys.argv[2:] or ["Island_Airfield", "Desert_Airfield", "Runway"]
c = krpc.connect("measure-runways")
sc = c.space_center


def wait_flight():
    for _ in range(120):
        try:
            if str(c.krpc.current_game_scene).endswith("flight") and sc.active_vessel:
                return sc.active_vessel
        except Exception:
            pass
        time.sleep(1)
    raise RuntimeError("no flight scene")


def offset(lat, lon, hdg, d, R):
    h = math.radians(hdg)
    return (lat + math.degrees(d * math.cos(h) / R),
            lon + math.degrees(d * math.sin(h) / (R * math.cos(math.radians(lat)))))


for site in sites:
    if site != "here":  # "here" = measure under the active vessel (already sitting at a runway start)
        try:
            sc.launch_vessel("SPH", craft, site, crew=[], recover=True)
        except Exception as e:
            print(json.dumps({"site": site, "error": f"{e.__class__.__name__}: {e}"}), flush=True)
            continue
        time.sleep(3)
    v = wait_flight()
    time.sleep(2 if site == "here" else 8)
    body = v.orbit.body
    bref = body.reference_frame
    R = body.equatorial_radius
    f = v.flight(bref)
    lat0, lon0 = f.latitude, f.longitude
    hdg = v.flight(v.surface_reference_frame).heading

    def hit_alt(lat, lon, top):
        p = body.position_at_altitude(lat, lon, top, bref)
        q = body.position_at_altitude(lat, lon, top - 50, bref)
        d = [q[i] - p[i] for i in range(3)]
        n = math.sqrt(sum(x * x for x in d))
        dist = sc.raycast_distance(p, tuple(x / n for x in d), bref)
        return top - dist if dist < 1e6 else None

    a0 = hit_alt(lat0, lon0, f.mean_altitude + 20)
    # the raycast may hit the plane itself at the spawn point: sample 30 m ahead/behind instead
    ref = [hit_alt(*offset(lat0, lon0, hdg, d, R), f.mean_altitude + 20) for d in (-30, 30, 60)]
    ref = [x for x in ref if x is not None]
    alt = sorted(ref)[len(ref) // 2] if ref else a0
    prof = []
    end_f = end_b = None
    for d in range(25, 5000, 25):
        h = hit_alt(*offset(lat0, lon0, hdg, d, R), alt + 30)
        prof.append((d, None if h is None else round(h, 2)))
        if h is None or abs(h - alt) > 1.5:
            end_f = d - 25
            break
    for d in range(25, 2000, 25):
        h = hit_alt(*offset(lat0, lon0, hdg, -d, R), alt + 30)
        if h is None or abs(h - alt) > 1.5:
            end_b = -(d - 25)
            break
    a = offset(lat0, lon0, hdg, end_b or 0, R)
    b = offset(lat0, lon0, hdg, end_f or 0, R)
    print(json.dumps({"site": site, "spawn": [round(lat0, 6), round(lon0, 6)], "heading": round(hdg, 2),
                      "runway_alt": round(alt, 2) if alt else None, "a": [round(a[0], 6), round(a[1], 6)],
                      "b": [round(b[0], 6), round(b[1], 6)], "length_m": (end_f or 0) - (end_b or 0),
                      "fwd_end_m": end_f, "back_end_m": end_b, "profile_tail": prof[-4:]}), flush=True)
