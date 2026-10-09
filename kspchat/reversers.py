"""Thrust reversers: find engines that can reverse and switch them (autoland rollout; emergency switch-back).

Two mechanisms (stock and mod engines differ): an engine mode whose name contains 'revers' (MultiModeEngine), or a
part-module event / action naming the reverser ('Reverse Thrust', 'Forward Thrust', 'Toggle Thrust Reverser', ...).
kRPC lists only the events currently shown in the part menu, so the visible one tells the direction.

Rollout use (plane.py): engage() after main-gear touchdown -> reverse thrust at ROLLOUT_THROTTLE, release() below
OFF_SPEED (throttle idle first, engines back to forward). Setting 'autoland_reversers' (default on; '/reversers off').
"""
import logging

log = logging.getLogger("kspchat.reversers")

ROLLOUT_THROTTLE = 0.6   # reverse throttle on the rollout (Luke: 50-70 %)
OFF_SPEED = 30.0         # m/s: idle + forward again below this (taxi speed), brakes keep going until stopped
ON_MIN_SPEED = 40.0      # don't bother engaging below this
_ENGAGED = []            # 1-based indices of the engines the rollout reversed


def enabled():
    from . import settings
    return bool(settings.get("autoland_reversers", True))


def command(arg):
    """'/reversers [on|off]'."""
    from . import settings
    a = (arg or "").strip().lower()
    if a in ("on", "off"):
        settings.put("autoland_reversers", a == "on")
        return f"Autoland thrust reversers {a} (rollout: reverse at {100 * ROLLOUT_THROTTLE:.0f}% until {OFF_SPEED:.0f} m/s)."
    return (f"Autoland thrust reversers are {'on' if enabled() else 'off'}. Use /reversers on or /reversers off.")


def _modes(e):
    try:
        return list(e.modes.keys()) if e.has_modes else []
    except Exception:  # noqa: BLE001
        return []


def _rev_mode(e):
    return next((m for m in _modes(e) if "revers" in m.lower()), None)


def _fwd_mode(e):
    return next((m for m in _modes(e) if "revers" not in m.lower()), None)


def _module(e):
    """(module, events, actions) naming a reverser on this engine's part, else None."""
    try:
        for m in e.part.modules:
            evs = [x for x in m.events if "revers" in x.lower() or "forward thrust" in x.lower()]
            acts = [x for x in m.actions if "revers" in x.lower()]
            if evs or acts:
                return m, evs, acts
    except Exception:  # noqa: BLE001
        pass
    return None


_NO_MODULE = {}


def module_reversed(e):
    """True when the engine's reverser module shows it is reversed: it offers only a 'Forward Thrust' event (no
    'Reverse Thrust'), or a reverser field reads deployed / reverse. Plain toggles (state unknown) -> False.
    Parts without a reverser module are remembered (the emergency watcher samples every engine often)."""
    key = getattr(e.part, "_object_id", None) or id(e.part)
    if _NO_MODULE.get(key):
        return False
    r = _module(e)
    if r is None:
        _NO_MODULE[key] = True
        return False
    m, evs, _ = r
    low = [x.lower() for x in evs]
    if any("forward" in x for x in low) and not any("revers" in x and "forward" not in x and "toggle" not in x for x in low):
        return True
    try:
        for k, val in dict(m.fields).items():
            if "revers" in k.lower() and str(val).strip().lower() in ("deployed", "reverse", "reversed", "on", "true", "locked"):
                return True
    except Exception:  # noqa: BLE001
        pass
    return False


def can_reverse(e):
    return bool(_rev_mode(e) and _fwd_mode(e)) or _module(e) is not None


def switch(e, reverse, toggle_ok=False):
    """Switch one engine to reverse (True) or forward (False); True if something was switched. A plain toggle
    (event / action that doesn't name the direction) is only used when toggle_ok (the caller knows the state)."""
    rm, fm = _rev_mode(e), _fwd_mode(e)
    if rm and fm:
        want = rm if reverse else fm
        try:
            if str(e.mode) != want:
                e.mode = want
            return True
        except Exception:  # noqa: BLE001
            try:
                e.toggle_mode()
                return True
            except Exception:  # noqa: BLE001
                return False
    r = _module(e)
    if r is None:
        return False
    m, evs, acts = r
    low = [x.lower() for x in evs]
    named_rev = [x for x, lx in zip(evs, low) if "revers" in lx and "forward" not in lx and "toggle" not in lx]
    named_fwd = [x for x, lx in zip(evs, low) if "forward" in lx]
    toggles = [x for x, lx in zip(evs, low) if "toggle" in lx]
    try:
        if reverse:
            if named_rev:
                m.trigger_event(named_rev[0])
                return True
            if named_fwd:  # only 'Forward Thrust' offered: it is already reversed
                return True
        else:
            if named_fwd:
                m.trigger_event(named_fwd[0])
                return True
            if named_rev:  # only 'Reverse Thrust' offered: it is already forward
                return True
        if reverse or toggle_ok:
            if toggles:
                m.trigger_event(toggles[0])
                return True
            if acts:
                m.set_action(acts[0], True)
                return True
    except Exception as ex:  # noqa: BLE001
        log.debug("reverser switch failed: %s", ex)
    return False


def _own():
    try:
        from . import emergency
        emergency.own_change()
    except Exception:  # noqa: BLE001
        pass


def engaged():
    return bool(_ENGAGED)


def engage(v):
    """Reverse every running engine that can; returns how many."""
    _own()
    try:
        engines = list(v.parts.engines)
    except Exception:  # noqa: BLE001
        return 0
    for i, e in enumerate(engines, 1):
        try:
            if e.active and can_reverse(e) and switch(e, True):
                _ENGAGED.append(i)
        except Exception:  # noqa: BLE001
            pass
    log.info("reversers: engaged %s", _ENGAGED)
    return len(_ENGAGED)


def release(v):
    """Engines the rollout reversed back to forward (call with the throttle at idle); returns how many."""
    if not _ENGAGED:
        return 0
    _own()
    n = 0
    try:
        engines = list(v.parts.engines)
        for i in list(_ENGAGED):
            try:
                if switch(engines[i - 1], False, toggle_ok=True):
                    n += 1
            except Exception:  # noqa: BLE001
                pass
    except Exception:  # noqa: BLE001
        pass
    finally:
        _ENGAGED.clear()
    log.info("reversers: released %d", n)
    return n