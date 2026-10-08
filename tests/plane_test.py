"""Plane autoland test (Grok Test, KSP in Space Center or flight).

  python tests/plane_test.py "Aeris 3A" 90 15 1200 [probe]
Takes off from runway 09 with the kRPC autopilot, flies heading <hdg> until <km> from the runway, at <alt> m,
then calls land_plane and waits. 'probe' also measures control signs once at altitude.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import ksp_actions as k, plane  # noqa: E402


def main():
    craft, hdg, km, alt = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
    probe = any(a.startswith("probe") for a in sys.argv[5:])
    t0 = time.time()
    c = k.conn()
    sc = c.space_center
    v = sc.active_vessel if str(c.krpc.current_game_scene).endswith("flight") else None
    if "takeover" in sys.argv and v is not None and str(v.situation).split(".")[-1] == "flying":
        print("taking over a plane already in flight", flush=True)
        return land(v, t0)
    reuse = "noreset" in sys.argv and v is not None and str(v.situation).split(".")[-1] == "landed"
    if not reuse:
        print(k.call_tool("launch_craft", {"craft_name": craft, "editor": "SPH"}), flush=True)
        v = sc.active_vessel
    else:
        print("re-using the landed plane (take off from where it stopped)", flush=True)
    body = v.orbit.body
    bref = body.reference_frame
    fb = v.flight(bref)
    fs = v.flight(v.surface_reference_frame)
    print("spawn lat/lon/alt/ralt/terrain", round(fb.latitude, 5), round(fb.longitude, 5), round(fb.mean_altitude, 2),
          round(fb.surface_altitude, 2), round(body.surface_height(fb.latitude, fb.longitude), 2),
          "bbox", [tuple(round(x, 2) for x in b) for b in v.bounding_box(v.reference_frame)], flush=True)
    ctl = v.control
    ctl.sas = False
    ap = v.auto_pilot
    ap.reference_frame = v.surface_reference_frame
    hdg0 = v.flight(v.surface_reference_frame).heading if reuse else 90
    ap.target_pitch_and_heading(2, hdg0)
    ap.target_roll = 0
    ap.engaged = True
    ctl.brakes = False
    from kspchat import settings
    vs_st = (settings.get("stall_speeds") or {}).get(v.name, plane.DEFAULT_STALL)
    fl = plane.Flaps(v)
    fl.set(30.0)  # full flaps for the takeoff roll (Luke); retracted step by step once airborne
    ctl.throttle = 1.0
    if not reuse:
        ctl.activate_next_stage()
    while fb.speed < 55:
        fl.schedule(fb.speed, vs_st, 30.0)
        time.sleep(0.2)
    ap.target_pitch_and_heading(10, hdg0)
    t_fl = 0.0
    while fb.mean_altitude < 69 + 150:
        step = time.time() - t_fl > 3.0
        fl.schedule(fb.speed, vs_st, 0.0 if fb.surface_altitude > 20 else 30.0, step)
        t_fl = time.time() if step else t_fl
        time.sleep(0.3)
    ctl.gear = False
    probed = not probe
    lat0, lon0 = plane.RWY_W
    import math
    while True:
        h = fb.mean_altitude
        step = time.time() - t_fl > 3.0
        fl.schedule(fb.speed, vs_st, 0.0, step)
        t_fl = time.time() if step else t_fl
        ap.target_pitch_and_heading(max(-5.0, min(12.0, (alt - h) / 40)), hdg)
        f_lat, f_lon = fb.latitude, fb.longitude
        mid = ((plane.RWY_W[0] + plane.RWY_E[0]) / 2, (plane.RWY_W[1] + plane.RWY_E[1]) / 2)
        dist = math.hypot(f_lat - mid[0], f_lon - mid[1]) * math.pi / 180 * body.equatorial_radius
        ctl.throttle = 1.0 if fb.speed < 130 else 0.0  # Aeris overspeeds to 340 m/s otherwise
        if not probed and h > 700 and abs(fs.roll) < 5:
            ap.engaged = False
            for name in ("roll", "pitch"):
                a0 = getattr(fs, name)
                setattr(ctl, name, 0.25)
                time.sleep(0.5)
                a1 = getattr(fs, name)
                setattr(ctl, name, 0.0)
                print(f"PROBE +ctl.{name} 0.25 for 0.5 s: flight.{name} {a0:.1f} -> {a1:.1f}", flush=True)
                ap.engaged = True
                time.sleep(4)
                ap.engaged = False
            ap.engaged = True
            probed = True
            if "probeonly" in sys.argv:
                return
        if dist > km * 1000:
            break
        time.sleep(0.3)
    if "askmodel" not in sys.argv:
        ap.engaged = False  # with askmodel the autopilot keeps flying until land_plane takes over
    print("outbound done", round(dist), "m, alt", round(fb.mean_altitude), "speed", round(fb.speed), flush=True)
    land(v, t0)


def land(v, t0):
    if "askmodel" in sys.argv:  # end-to-end: let the local model pick the tool
        ap = v.auto_pilot  # keep the plane flying (level, current heading) while the model thinks
        ap.reference_frame = v.surface_reference_frame
        ap.target_pitch_and_heading(3, v.flight(v.surface_reference_frame).heading)
        ap.target_roll = 0
        ap.engaged = True
        from kspchat.chat import Session
        reply, tools = Session().send("land the plane", "local")
        ap.engaged = False  # hand over to the autoland thread (it uses its own connection)
        print("MODEL tools:", [t["tool"] for t in tools], "| reply:", reply[:200], flush=True)
    else:
        print("land_plane ->", k.call_tool("land_plane", {}), flush=True)
    last = 0
    while plane.active():
        if time.time() - last > 20:
            print("  ", json.dumps({x: plane.STATUS.get(x) for x in ("phase", "s", "xt", "h", "vs", "speed", "hdg", "bank", "throttle")}), flush=True)
            last = time.time()
        time.sleep(1)
    st = dict(plane.STATUS)
    st["minutes"] = round((time.time() - t0) / 60, 1)
    tf = Path(__file__).resolve().parent.parent / "logs" / f"plane_trace_{time.strftime('%H%M%S')}.csv"
    tf.write_text("t,phase,s,xt,h,hr,vs,vs_des,speed,pitch,pitch_des,hdg,hdg_des,roll,bank_des,thr\n"
                  + "\n".join(",".join(map(str, r)) for r in plane.TRACE))
    st["trace_file"] = str(tf)
    print("RESULT", json.dumps(st, default=str), flush=True)


if __name__ == "__main__":
    main()
