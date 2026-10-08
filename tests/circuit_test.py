"""Circuit test from the ground: land_plane(touch_and_go=N) -> takeoff, pattern, N graded touch-and-goes, full stop.

  python tests/circuit_test.py [N] [tool-args-json]
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import ksp_actions as k, plane  # noqa: E402
import faulthandler
import logging
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")

n = int(sys.argv[1]) if len(sys.argv) > 1 else 1
args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
tool = args.pop("_tool", "land_plane")
if tool == "land_plane":
    args.setdefault("touch_and_go", n)
t0 = time.time()
print(tool, args, "->", k.call_tool(tool, args), flush=True)
last = 0
tf = Path(__file__).resolve().parent.parent / "logs" / f"plane_trace_{time.strftime('%H%M%S')}.csv"
HDR = "t,phase,s,xt,h,hr,vs,vs_des,speed,pitch,pitch_des,hdg,hdg_des,roll,bank_des,thr\n"
last_w = 0
_fh = open(Path(__file__).resolve().parent.parent / "logs" / "circuit_stacks.log", "a")
while plane.active():
    faulthandler.dump_traceback_later(8, repeat=False, file=_fh)  # dumps all thread stacks if we stall >= 8 s
    if time.time() - last_w > 5:  # write the trace as we go (a killed run used to lose it)
        tf.write_text(HDR + "\n".join(",".join(map(str, r)) for r in list(plane.TRACE)))
        last_w = time.time()
    if time.time() - last > 20:
        print("  ", json.dumps({x: plane.STATUS.get(x) for x in ("phase", "s", "xt", "h", "vs", "speed", "hdg", "bank",
                                                                  "throttle")}), flush=True)
        last = time.time()
    time.sleep(1)
st = dict(plane.STATUS)
st["minutes"] = round((time.time() - t0) / 60, 1)
tf.write_text(HDR + "\n".join(",".join(map(str, r)) for r in plane.TRACE))
st["trace_file"] = str(tf)
print("RESULT", json.dumps(st, default=str), flush=True)
