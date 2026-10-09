"""P5-1: per-tool replacement / test / live matrix for all BY_NAME tools (replaces the 5A prose).

MATRIX maps every kspchat.ksp_actions.BY_NAME tool to its P5 status (drop reasons are the inline comments):
  native    - ported to NativeCommands.Ported (C# NativeFlightController), bridge fallback allowed until P5-8
  p5-2/3/4  - scheduled for that mini-phase (flight residuals / lifecycle+science / orbital+docking)
  drop      - intentionally dropped (or renamed) with an explicit note in the inline comment

Checks (no network, no KSP):
  1. every BY_NAME tool has a matrix row and a plausible status
  2. tools marked `native` really are in NativeCommands.Ported
  3. NativeCommands.Ported chat-tool entries are all declared in the matrix (nothing silently unreviewed)
  4. no "stub for now" / "is a stub" strings ship in the C# UI (P5-1 stub sweep)
Run: python -m pytest tests/test_tool_matrix.py -q
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from kspchat import ksp_actions  # noqa: E402

NATIVE = "native"
SCHEDULED = ("p5-2", "p5-3", "p5-4")
DROP = "drop"

# status per tool; filled as P5-2..4 land. `native` = done now (NativeCommands.Ported).
MATRIX = {
    # -- already native (Phase 3 / early Phase 5 foundation) --
    "abort": NATIVE, "stop_current": NATIVE, "plane_hold": NATIVE, "takeoff": NATIVE, "land_here": NATIVE,
    "land_plane": NATIVE, "land_at_spot": NATIVE, "heli_control": NATIVE, "taxi_to": NATIVE,
    "get_trim_state": NATIVE, "set_trim": NATIVE, "save_craft_notes": NATIVE, "save_landing_spot": NATIVE,
    "get_status": NATIVE, "autopilot_status": NATIVE, "set_gear": NATIVE, "set_brakes": NATIVE,
    "set_lights": NATIVE, "set_rcs": NATIVE, "set_sas": NATIVE, "auto_trim_now": NATIVE, "trim": NATIVE,
    "set_heading": NATIVE, "set_speed": NATIVE,
    # -- P5-1: capture_plan/list_*/trim_panel_open/set_flight_plan decided (port or drop, explicit note) --
    "capture_plan": DROP,          # orbital capture numbers -> P5-4 mechjeb-optional helpers cover it
    "list_craft": DROP,            # craft listing is a menu/browser feature; no chat replacement planned
    "list_landing_spots": "native",  # read-only spot listing -> NativeSpots (menu shows saved spots)
    "list_taxi_points": "native",    # read-only taxi points -> NativeSpots/NativePlan taxi/list
    "trim_panel_open": DROP,       # opens an in-mod window; menus open it directly now
    "set_flight_plan": DROP,       # plan editor is filled by the in-mod Flight Plan UI / flightplan/check
    # -- P5-2 flight residuals --
    "land": NATIVE, "land_at": NATIVE, "land_at_ksc": NATIVE, "fly_to": NATIVE, "fly_to_place": NATIVE,
    "touch_and_go": NATIVE, "go_around": NATIVE, "circle_here": NATIVE, "prop_control": NATIVE,
    "afterburner": NATIVE, "engine_mode": NATIVE, "flaps": NATIVE, "set_throttle": NATIVE,
    "set_engines": NATIVE, "cut_engines": NATIVE, "set_altitude": NATIVE, "level_off": NATIVE,
    "set_sas_mode": NATIVE, "abort_ag": NATIVE, "action_group": NATIVE, "fuel_check": NATIVE,
    "get_delta_v": NATIVE, "get_landing_eta": NATIVE, "how_far": NATIVE, "landing_check": NATIVE,
    "crew_report": NATIVE, "flight_report": NATIVE, "damage_report": NATIVE, "turn": NATIVE,
    "plane_pitch": NATIVE, "course_correction": "p5-4",
    # -- P5-3 vessel lifecycle + science --
    "stage": NATIVE, "recover_vessel": NATIVE, "launch_craft": NATIVE, "deploy_parachutes": NATIVE,
    "eject_kerbal": NATIVE, "run_science": NATIVE, "reset_experiments": NATIVE, "set_science_watcher": NATIVE,
    # -- P5-4 orbital / docking / MechJeb-optional --
    "mechjeb_ascent": NATIVE, "circularize": NATIVE, "transfer_to": "p5-4", "deorbit_burn": NATIVE,
    "warp_to_apoapsis": NATIVE, "warp_to_soi_change": NATIVE, "dock_with": NATIVE, "station_keep": "p5-4",
    "change_apoapsis": NATIVE, "change_periapsis": NATIVE, "change_inclination": NATIVE,
    "apsis_longitude": "p5-4", "sun_lock": NATIVE, "antenna_lock": NATIVE, "sync_orbit_altitude": NATIVE,
    "match_target_plane": "p5-4", "launch_to_target_plane": "p5-4", "time_to_target": NATIVE,
    # -- personality / settings / misc: stay bridge-or-native thin (P5-1 decisions) --
    "set_ai_name": "native", "remember_preference": "native", "captain_order": DROP,  # orders.parse handles speech
    "set_override": DROP, "authorise_all": DROP,   # security toggles become in-mod settings, no chat tool
}


def _ported():
    src = (ROOT / "KSPChatMod" / "NativeCommands.cs").read_text(encoding="utf-8")
    m = re.search(r"Ported = new HashSet<string>.*?\{(.*?)\};", src, re.S)
    return set(re.findall(r'"([^"]+)"', m.group(1)))


def test_every_by_name_tool_has_a_matrix_row():
    missing = sorted(set(ksp_actions.BY_NAME) - set(MATRIX))
    extra = sorted(set(MATRIX) - set(ksp_actions.BY_NAME))
    assert not missing, "tools without a matrix row: %s" % missing
    assert not extra, "matrix rows that are not BY_NAME tools: %s" % extra
    bad = sorted(t for t, s in MATRIX.items() if s not in (NATIVE, DROP) and s not in SCHEDULED)
    assert not bad, "implausible status: %s" % bad


def test_native_rows_are_really_ported():
    ported = _ported()
    lying = sorted(t for t, s in MATRIX.items() if s == NATIVE and t not in ported)
    assert not lying, "matrix says native but NativeCommands.Ported lacks: %s" % lying


def test_ported_set_is_fully_reviewed():
    # HTTP-route style entries (taxi/list, flightplan/*) are not chat tools; the matrix covers BY_NAME only.
    unreviewed = sorted(t for t in _ported() if "/" not in t and t not in MATRIX)
    assert not unreviewed, "NativeCommands.Ported entries missing from matrix: %s" % unreviewed


def test_no_stub_strings_in_shipping_ui():
    for path in (ROOT / "KSPChatMod").glob("*.cs"):
        text = path.read_text(encoding="utf-8", errors="replace")
        for needle in ("stub for now", "is a stub", "not wired yet"):
            assert needle not in text, "%s still ships %r" % (path.name, needle)