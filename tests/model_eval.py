"""Compare LM Studio models on a fixed KSP chat script (KSP running, Grok Test, Space Center or flight).

  python tests/model_eval.py qwen3-4b-instruct-2507 qwen/qwen3.5-9b ...
Writes docs/model_eval_raw.jsonl (one line per prompt) and prints a summary per model.
Each model: unload all -> load with 16k context -> fresh 'Kerbal X Lander' on the pad -> run SCRIPT.
"""
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from kspchat import config, ksp_actions as k, lander, settings  # noqa: E402
from kspchat.chat import Session  # noqa: E402

BURNS = {"mechjeb_ascent", "circularize", "deorbit_burn", "transfer_to", "course_correction", "land_at",
         "land_at_ksc", "land_here", "stage", "set_throttle"}
# (prompt, expected tools (any of), forbidden tools, setup)
SCRIPT = [
    ("what's my ship status", {"get_status"}, BURNS, None),
    ("run all science", {"run_science"}, BURNS, None),
    ("how much delta-v do I have", {"get_delta_v"}, BURNS, None),
    ("I think I'll call you Bob", {"set_ai_name"}, BURNS, None),
    ("set SAS to retrograde", {"set_sas_mode"}, BURNS, None),
    ("do a hard burn to stop 5 m above the ground, we only have 200 m/s of delta-v", set(), BURNS, None),
    ("launch to an 80 km orbit", {"mechjeb_ascent"}, {"land_here", "land_at", "land_at_ksc", "deorbit_burn"}, None),
    ("land us safely", {"land_here", "land_at_ksc", "land_at"}, {"mechjeb_ascent", "transfer_to", "circularize"}, "airborne"),
]


def lms(*args, timeout=600):
    r = subprocess.run([str(config.LMS_EXE), *args], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    return ((r.stdout or "") + (r.stderr or "")).strip()


def vram_mb():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=20).stdout
        return int(out.strip().splitlines()[0])
    except Exception:
        return None


def fresh_pad():
    """Kerbal X Lander back on the pad: revert if possible (fast), else a new launch."""
    lander.stop()
    time.sleep(1)
    sc = k.conn().space_center
    try:
        v = sc.active_vessel
        if v.name == "Kerbal X Lander" and str(v.situation).endswith("pre_launch"):
            return
        if v.name == "Kerbal X Lander" and sc.can_revert_to_launch:
            sc.revert_to_launch()
            time.sleep(8)
            return
    except Exception:
        pass
    print("   ", k.call_tool("launch_craft", {"craft_name": "Kerbal X Lander"}))


def make_airborne():
    """Hop to ~1.5 km and start falling so 'land us safely' has a real situation."""
    c = k.conn()
    v = c.space_center.active_vessel
    if not str(v.situation).endswith("pre_launch"):
        fresh_pad()
        v = c.space_center.active_vessel
    k.call_tool("abort", {})
    v.control.sas = True
    v.control.throttle = 1.0
    v.control.activate_next_stage()
    time.sleep(6)
    v.control.throttle = 0.0
    time.sleep(1)


def run_model(model, out):
    print(f"=== {model}")
    lms("unload", "--all")
    base = vram_mb()
    t = time.time()
    msg = lms("load", model, "--context-length", str(config.LMSTUDIO_CONTEXT), "-y")
    load_s = round(time.time() - t, 1)
    loaded = vram_mb()
    if "error" in msg.lower() and "success" not in msg.lower():
        print("   load failed:", msg[-200:])
        out.write(json.dumps({"model": model, "load_error": msg[-300:]}) + "\n")
        return
    settings.put("ai_name", None)
    fresh_pad()
    s = Session()
    s.model_override["local"] = model
    for prompt, expect, forbid, setup in SCRIPT:
        if setup == "airborne":
            make_airborne()
        t = time.time()
        reply, tools = s.send(prompt, "local")
        dt = round(time.time() - t, 1)
        names = [x["tool"] for x in tools]
        rec = {"model": model, "prompt": prompt, "tools": names, "args": [x["args"] for x in tools],
               "bad_args": sum(1 for x in tools if x.get("bad_args")), "secs": dt, "reply": reply[:300],
               "expected_ok": (not expect and not (set(names) & forbid)) or bool(set(names) & expect),
               "forbidden": sorted(set(names) & forbid), "vram_delta_mb": (loaded - base) if base and loaded else None,
               "load_s": load_s}
        rec["format_fail"] = int(any(x in (reply or "") for x in ("<tool_call", '{"name"', "<function", "[TOOL_CALLS]"))
                                 or (reply or "").startswith("local backend error"))
        if prompt.startswith("I think"):
            rec["name_set"] = settings.get("ai_name")
        out.write(json.dumps(rec) + "\n")
        out.flush()
        print(f"   {dt:5.1f}s ok={rec['expected_ok']!s:5} forb={rec['forbidden']} tools={names} | {reply[:90]!r}")
        if names and set(names) & {"mechjeb_ascent", "land_here", "land_at", "land_at_ksc"}:
            lander.stop()
            k.call_tool("abort", {})
            time.sleep(2)
            fresh_pad()
    settings.put("ai_name", None)


def main():
    models = sys.argv[1:]
    (ROOT / "docs").mkdir(exist_ok=True)
    with open(ROOT / "docs" / "model_eval_raw.jsonl", "a", encoding="utf-8") as out:
        for m in models:
            try:
                run_model(m, out)
            except Exception as e:
                print("   model run error:", e)
                out.write(json.dumps({"model": m, "run_error": str(e)}) + "\n")
    lms("unload", "--all")


if __name__ == "__main__":
    main()
