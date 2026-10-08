"""Landing test harness (Grok Test sandbox, KSP in Space Center or flight).

  python tests/landing_test.py hop   "Swivel Hopper" 3000        # vertical-ish hop to ~3 km apoapsis, then land_here
  python tests/landing_test.py orbit "Kerbal X Lander" 80        # MechJeb ascent to 80 km, drop boosters, land_here
  python tests/landing_test.py ksc   "Kerbal X Lander" 80        # same, but land_at_ksc (hybrid MechJeb + controller)
Prints one JSON line with the outcome.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import ksp_actions as k, lander  # noqa: E402


def wait(cond, timeout, step=1.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if cond():
            return True
        time.sleep(step)
    return False


def main():
    mode, craft, arg = sys.argv[1], sys.argv[2], float(sys.argv[3])
    t0 = time.time()
    print(k.call_tool("launch_craft", {"craft_name": craft}), flush=True)
    c = k.conn()
    v = c.space_center.active_vessel
    ctl = v.control
    if mode == "hop":
        ap = v.auto_pilot
        ap.reference_frame = v.surface_reference_frame
        ap.target_pitch_and_heading(85, 90)
        ap.engaged = True
        ctl.throttle = 1.0
        ctl.activate_next_stage()
        time.sleep(1)
        while v.orbit.apoapsis_altitude < arg + 70:
            time.sleep(0.2)
        ctl.throttle = 0.0
        ap.engaged = False
        wait(lambda: v.flight(v.orbit.body.reference_frame).vertical_speed < 0, 300, 0.5)
    else:
        print(k.call_tool("mechjeb_ascent", {"target_altitude_km": arg}), flush=True)
        mj = c.mech_jeb
        ok = wait(lambda: not mj.ascent_autopilot.enabled and v.orbit.periapsis_altitude > 70000, 900, 5)
        print("orbit reached" if ok else "orbit NOT reached", int(v.orbit.apoapsis_altitude), int(v.orbit.periapsis_altitude), flush=True)
        if not ok:
            return
        for _ in range(4):  # drop the launch stages until the lander engine (decouple stage 1) is lit
            if any(e.active and e.part.decouple_stage == 1 for e in v.parts.engines):
                break
            ctl.activate_next_stage()
            time.sleep(1.5)
    print(k.call_tool("get_delta_v", {})[:300], flush=True)
    tool = "land_at_ksc" if mode == "ksc" else "land_here"
    if "askmodel" in sys.argv:  # end-to-end: let the local model pick the tool
        from kspchat.chat import Session
        reply, tools = Session().send("land here", "local")
        print("MODEL tools:", [t["tool"] for t in tools], "| reply:", reply[:200], flush=True)
    else:
        print(tool, "->", k.call_tool(tool, {}), flush=True)
    last = 0
    while lander.active():
        if time.time() - last > 20:
            print("  ", json.dumps({x: lander.STATUS.get(x) for x in ("phase", "h", "vs", "hs", "throttle", "warning", "mechjeb_status")}), flush=True)
            last = time.time()
        time.sleep(1)
    st = dict(lander.STATUS)
    try:
        st["parts"] = len(c.space_center.active_vessel.parts.all)
    except Exception:
        st["parts"] = None
    st["mode"], st["craft"], st["minutes"] = mode, craft, round((time.time() - t0) / 60, 1)
    tf = Path(__file__).resolve().parent.parent / "logs" / f"landing_trace_{time.strftime('%H%M%S')}.csv"
    tf.parent.mkdir(exist_ok=True)
    tf.write_text("t,phase,h,vs,hs,throttle,a_max,up,bottom_off\n" + "\n".join(",".join(map(str, r)) for r in lander.TRACE))
    st["trace_file"] = str(tf)
    print("RESULT", json.dumps(st, default=str), flush=True)


if __name__ == "__main__":
    main()
