"""External safety watchdog for plane test flights (separate process, own kRPC connection).

  python tests/flight_watchdog.py <controller_pid> [logfile]

Logs pitch / throttle / speed / vertical speed every second. Takes over (kills the controller process, engages
the kRPC autopilot: wings level, gentle positive pitch, power) if
  * the controller's heartbeat in bridge_settings.json goes stale (> 5 s: controller frozen or dead), or
  * the nose is down (< -10 deg) while sinking hard (< -25 m/s) outside the final approach (speed control must
    never drop the nose; nose-down only on final or a planned, controlled descent).
"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import krpc

ROOT = Path(__file__).resolve().parent.parent
pid = int(sys.argv[1])
logf = open(sys.argv[2] if len(sys.argv) > 2 else ROOT / "logs" / f"watchdog_{time.strftime('%H%M%S')}.log", "a",
            encoding="utf-8")


def log(msg):
    line = f"{time.strftime('%H:%M:%S')} {msg}"
    print(line, flush=True)
    logf.write(line + "\n")
    logf.flush()


def claim():
    try:
        return json.loads((ROOT / "bridge_settings.json").read_text(encoding="utf-8")).get("autopilot_busy") or {}
    except Exception:
        return {}


def alive(p):
    r = subprocess.run(["tasklist", "/FI", f"PID eq {p}", "/NH"], capture_output=True, text=True)
    return str(p) in r.stdout


c = krpc.connect("watchdog")
v = c.space_center.active_vessel
fb = v.flight(v.orbit.body.reference_frame)
fs = v.flight(v.surface_reference_frame)
t0 = time.time()
last_log = 0
while alive(pid):
    now = time.time()
    try:
        pitch, spd, vs, hr, thr = fs.pitch, fb.speed, fb.vertical_speed, fb.surface_altitude, v.control.throttle
        sit = v.situation.name
    except Exception as e:  # noqa: BLE001
        log(f"read failed: {e}")
        time.sleep(1)
        continue
    cl = claim()
    phase = (cl.get("info") or {}).get("phase")
    hb_age = now - float(cl.get("heartbeat", now)) if cl else 0.0
    if now - last_log >= 1.0:
        last_log = now
        log(f"pitch {pitch:6.1f} thr {thr:4.2f} spd {spd:5.0f} vs {vs:6.1f} hr {hr:6.0f} {sit} phase {phase} hb {hb_age:3.0f}s")
    why = None
    if sit == "flying" and now - t0 > 10:
        if cl and hb_age > 5.0:
            why = f"controller heartbeat stale {hb_age:.0f}s"
        elif pitch < -10 and vs < -25 and phase not in ("final", "flare"):
            why = f"nose down {pitch:.0f} deg sinking {vs:.0f} m/s in phase {phase}"
    if why:
        log(f"TAKEOVER: {why}")
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(pid)], capture_output=True)
        ap = v.auto_pilot
        ap.reference_frame = v.surface_reference_frame
        ap.target_pitch_and_heading(5.0, fs.heading)
        ap.target_roll = 0.0
        ap.engaged = True
        v.control.throttle = max(thr, 0.6)
        for _ in range(60):
            time.sleep(1)
            log(f"  hold: pitch {fs.pitch:6.1f} vs {fb.vertical_speed:6.1f} spd {fb.speed:5.0f} hr {fb.surface_altitude:6.0f}")
            if fs.pitch > 2 and fb.vertical_speed > 0 and fb.speed < 250:
                break
        log("holding level on the kRPC autopilot; controller stopped")
        break
    time.sleep(0.25)
log("watchdog exit")
