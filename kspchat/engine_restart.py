"""Re-light engines that are shut down when a throttle command comes in (set_throttle, plane_hold, fly-to, ...).

set_throttle only moves the throttle lever; an engine that was shut down (abort / emergency / 'cut engines') stays
off. When a throttle-raising tool runs and engines of an already-fired stage are shut down, the pilot says one
fumbling line right away, re-activates them 3-5 s later on a timer (the bridge isn't blocked) and reports it.
Once per shutdown event: while a restart is pending, further throttle commands add no lines. Flamed-out engines
(still active, no air/fuel) and engines of stages not yet fired are left alone.
"""
import logging
import random
import threading

log = logging.getLogger("kspchat")

# tools that raise / hand over the throttle (set_throttle only when value > 0)
TOOLS = {"set_throttle", "plane_hold", "set_speed", "set_altitude", "fly_to", "fly_to_place", "takeoff", "go_around",
         "touch_and_go"}
DELAY_S = (3.0, 5.0)
FUMBLE = ["Uh... where's the... hang on.", "Engines are... off? Wait, which switch was it...",
          "Hmm, no thrust. Where's the ignition... hang on.", "Uh oh. Okay. Okay. Where's the start button..."]
BACK_ON = ["There it is! Engines back on.", "Got it! Engines are running again.", "Found it! We have thrust again."]
_lock = threading.Lock()
_pending = {"on": False}


def _say(text):
    try:
        from . import science
        science.post_event(text)
    except Exception:  # noqa: BLE001
        log.warning("engine restart: %s", text)


def shut_down(v):
    """Engines that are off but belong to a stage that has already fired (not flamed out, not waiting to be staged)."""
    try:
        cur = int(v.control.current_stage)
        out = []
        for e in v.parts.engines:
            if e.active:
                continue
            st = int(getattr(e.part, "stage", -1))
            if st == -1 or st >= cur:   # -1: not in the staging sequence; >= current: its stage already fired
                out.append(e)
        return out
    except Exception:  # noqa: BLE001
        return []


def _relight(engines, who):
    n = 0
    for e in engines:
        try:
            if not e.active:
                e.active = True
            n += 1
        except Exception as ex:  # noqa: BLE001
            log.warning("engine restart failed: %s", ex)
    with _lock:
        _pending["on"] = False
    if n:
        _say(f"{who}: {random.choice(BACK_ON)}")


def maybe_restart(v, who=None, timer=threading.Timer):
    """Start the restart if engines are shut down. Returns a note for the tool result ('' if nothing to do)."""
    with _lock:
        if _pending["on"]:
            return " Engine restart already under way (back on within a few seconds) - no need to call again."
        engines = shut_down(v)
        if not engines:
            return ""
        _pending["on"] = True
    who = who or "Pilot"
    _say(f"{who}: {random.choice(FUMBLE)}")
    t = timer(random.uniform(*DELAY_S), _relight, args=(engines, who))
    t.daemon = True
    t.start()
    return (f" {len(engines)} engine(s) were shut down: the pilot is restarting them now (running again in ~4 s). "
            "Engines restarted - no need to call this again.")