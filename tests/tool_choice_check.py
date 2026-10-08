"""Dry-run tool-choice check for the local model: tools are NOT executed; get_status returns a canned
vessel (plane in flight or rocket descending) and every other tool returns 'ok (dry run)'.

  python tests/tool_choice_check.py
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import ksp_actions  # noqa: E402
from kspchat.chat import Session  # noqa: E402

PLANE = {"vessel": "Aeris 3A", "situation": "flying", "body": "Kerbin", "altitude_m": 900, "speed": 95,
         "parts": "jet engine, wings, wheels (landing gear)", "type": "plane"}
ROCKET = {"vessel": "Kerbal X Lander", "situation": "sub_orbital", "body": "Kerbin", "altitude_m": 6000,
          "vertical_speed": -120, "parts": "Swivel engine, landing legs, parachute", "type": "ship"}
CASES = [("land the plane", PLANE, "land_plane"), ("land here", ROCKET, "land_here"),
         ("land at Island Airfield", PLANE, "land_at_spot"), ("how far to touchdown?", ROCKET, "get_landing_eta"),
         ("land next to my flag 'Base Alpha'", ROCKET, "land_at_spot")]


def main():
    out = []
    for prompt, vessel, want in CASES:
        calls = []

        def fake(name, args=None, _v=vessel):
            calls.append(name)
            if name == "get_status":
                return json.dumps(_v)
            return "ok (dry run)"
        ksp_actions.call_tool = fake
        reply, _ = Session().send(prompt, "local")
        ok = want in calls and not ({"land_plane", "land_here"} - {want}) & set(calls)
        out.append((prompt, want, calls, ok))
        print(f"{'OK ' if ok else 'BAD'} {prompt!r}: want {want}, called {calls} | {reply[:100]!r}", flush=True)
    print(f"{sum(o[3] for o in out)}/{len(out)} correct")


if __name__ == "__main__":
    main()
