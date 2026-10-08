"""Airframe / crew protection for flown overrides, and G-force blackout handling (KSP 1.12, if enabled in difficulty).

An overridden bank / pitch (speedcap 'override bank 60') is flown as commanded; this watcher protects the airframe and
crew instead of the rule: when G gets too high, a part runs hot (temperature near its max) or parts break off, the
overridden excess is eased off just enough (factor 1 -> 0 in steps, back up slowly when clear) and it says so in chat.

Blackout: kRPC has no 'unconscious' flag, but a blacked-out kerbal drops the vessel's control (Control.source /
Control.state -> none) - so crew aboard + control lost + recent high G = blackout. Older kRPC without those: sustained
G above BLACKOUT_G for BLACKOUT_S. While out: speedcap's bank limit is 0 (wings level) and climb pitch eased; the
commands stay stored and resume automatically when the pilot comes to ('Sidry blacked out!' / 'Sidry is back').

tick(sample) is the pure logic (tested offline); a daemon thread samples kRPC once a second while an override or an
autopilot is active (ensure()).
"""
import logging
import threading
import time
from collections import deque

log = logging.getLogger("kspchat.protect")

G_EASE = 6.0          # g: ease the overridden bank / pitch above this
TEMP_EASE = 0.9       # part temperature / max temperature
BLACKOUT_G = 6.5      # fallback (no control-state API): sustained g ...
BLACKOUT_S = 3.0      # ... for this long = pilot out
RECOVER_G, RECOVER_S = 3.0, 5.0
EASE_STEP, RESTORE_STEP = 0.25, 0.05
AUTO = True           # tests switch the kRPC thread off

_lock = threading.Lock()
_st = {"factor": 1.0, "blackout": False, "parts": None, "reason": "", "said_t": -1e9, "g_hist": deque(maxlen=20),
       "high_since": None, "low_since": None, "who": None}
_thread = None


def factor():
    """1 = the override is flown in full, 0 = eased all the way back to the default limit."""
    return _st["factor"]


def blackout():
    return _st["blackout"]


def reset():
    with _lock:
        _st.update(factor=1.0, blackout=False, parts=None, reason="", said_t=-1e9, high_since=None, low_since=None, who=None)
        _st["g_hist"].clear()


def set_blackout(on, who=None):
    """Blackout state from the emergency watcher (it detects G-LOC even when no override / autopilot runs)."""
    with _lock:
        if on and not _st["blackout"]:
            _st.update(blackout=True, who=who or _st["who"])
        elif not on:
            _st["blackout"] = False


def _say(text):
    log.warning("protect: %s", text)
    try:
        from . import science
        science.post_event(text)
    except Exception:  # noqa: BLE001
        pass


def tick(s):
    """One sample: {"t", "g", "temp" (max ratio or None), "parts" (count), "crew" (count), "ctrl" (True = has control,
    False = lost, None = unknown), "who" (pilot name)}. Updates the factor / blackout state; returns the messages said."""
    said = []
    t, g = float(s.get("t", time.time())), float(s.get("g") or 0.0)
    with _lock:
        _st["g_hist"].append((t, g))
        recent_g = max((x for tt, x in _st["g_hist"] if t - tt <= 5.0), default=g)
        # --- blackout
        who = s.get("who") or _st["who"] or "The pilot"
        crew, ctrl = int(s.get("crew") or 0), s.get("ctrl")
        if g > BLACKOUT_G:
            _st["high_since"] = t if _st["high_since"] is None else _st["high_since"]
        else:
            _st["high_since"] = None
        if g < RECOVER_G:
            _st["low_since"] = t if _st["low_since"] is None else _st["low_since"]
        else:
            _st["low_since"] = None
        if not _st["blackout"]:
            out = (crew > 0 and ctrl is False and recent_g > 4.0) or \
                  (ctrl is None and crew > 0 and _st["high_since"] is not None and t - _st["high_since"] >= BLACKOUT_S)
            if out:
                _st.update(blackout=True, who=who)
                said.append(f"{who} blacked out! Wings level and easing the G until they come to.")
        else:
            back = (ctrl is True) or (ctrl is None and _st["low_since"] is not None and t - _st["low_since"] >= RECOVER_S)
            if back:
                _st["blackout"] = False
                said.append(f"{_st['who'] or who} is back - resuming the previous command.")
        # --- airframe / crew stress -> ease the overridden excess
        reasons = []
        if g > G_EASE:
            reasons.append(f"{g:.1f} g")
        temp = s.get("temp")
        if temp is not None and temp >= TEMP_EASE:
            reasons.append(f"a part at {100 * temp:.0f}% of its max temperature")
        parts = s.get("parts")
        if parts is not None and _st["parts"] is not None and parts < _st["parts"]:
            reasons.append(f"{_st['parts'] - parts} part(s) lost")
        if parts is not None:
            _st["parts"] = parts
        if reasons:
            _st["factor"] = max(0.0, _st["factor"] - EASE_STEP)
            _st["reason"] = ", ".join(reasons)
            if t - _st["said_t"] > 10.0:
                _st["said_t"] = t
                said.append(f"Easing off the override ({_st['reason']}) - protecting the airframe and crew.")
        else:
            _st["factor"] = min(1.0, _st["factor"] + RESTORE_STEP)
    try:
        from . import emergency
        quiet_bo = emergency.running()  # it posts the in-character blackout lines itself
    except Exception:  # noqa: BLE001
        quiet_bo = False
    for m in said:
        if quiet_bo and ("blacked out" in m or "is back" in m):
            continue
        _say(m)
    return said


def _sample(v, scan_temp):
    s = {"t": time.time(), "g": float(v.flight().g_force), "parts": None, "temp": None, "ctrl": None, "crew": 0}
    parts = list(v.parts.all)
    s["parts"] = len(parts)
    if scan_temp:
        hot = 0.0
        for p in parts:
            try:
                hot = max(hot, p.temperature / max(p.max_temperature, 1.0), p.skin_temperature / max(p.max_skin_temperature, 1.0))
            except Exception:  # noqa: BLE001
                pass
        s["temp"] = hot
    try:
        s["crew"] = len(v.crew)
    except Exception:  # noqa: BLE001
        pass
    try:
        src, state = str(v.control.source).lower(), str(v.control.state).lower()
        s["ctrl"] = not (src.endswith("none") or state.endswith("none"))
    except Exception:  # noqa: BLE001
        s["ctrl"] = None
    return s


def _watching():
    from . import hold, plane, speedcap
    return bool(speedcap.bank_override() or speedcap._state.get("pitch") or hold.active() or plane.active() or _st["blackout"])


def _run():
    global _thread
    from . import ksp_actions
    idle_since, n = None, 0
    try:
        while True:
            time.sleep(1.0)
            try:
                if not _watching():
                    idle_since = idle_since or time.time()
                    if time.time() - idle_since > 30.0:
                        return
                    continue
                idle_since = None
                n += 1
                from . import speedcap
                over = bool(speedcap._state.get("bank") or speedcap._state.get("pitch"))
                s = _sample(ksp_actions._vessel(), scan_temp=over and n % 3 == 0)
                s["who"] = ksp_actions.pilot_name()
                tick(s)
            except Exception as e:  # noqa: BLE001
                log.debug("protect sample failed: %s", e)
    finally:
        _thread = None


def ensure():
    """Start the watcher thread if it isn't running (no-op in offline tests)."""
    global _thread
    if not AUTO or (_thread is not None and _thread.is_alive()):
        return
    _thread = threading.Thread(target=_run, daemon=True, name="protect")
    _thread.start()