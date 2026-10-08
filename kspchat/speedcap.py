"""Speed-cap and safe-ceiling override authority (Luke 2026-10-08).

The plane autopilots never exceed Luke's speed cap: 200 m/s target / 220 m/s HARD cap as EQUIVALENT airspeed (EAS),
so near the ground that is ~220 m/s true and the true-airspeed limit rises as the air thins. A request for more
(chat, plane_hold / fly_to tool, Flight Plan step) is NOT just flown: the tool answers "needs_override: ... Grant
override authority? (yes/no)" and stores the request as pending for that chat session. A plain "yes" in that chat
grants it (the cap is raised to the requested EAS and the request is applied); "no" keeps the cap. "/override speed
on" grants authority for the rest of the bridge session (requests above the cap apply without asking, each raising
the cap to what was asked); "/override speed off" drops it and restores the 200/220 limits. Nothing is persisted:
a bridge restart is back to the safe defaults.
Altitude works the same way: a target above the craft's estimated safe ceiling (takeoff.vessel_ceiling: from its
air-breathing thrust-to-weight and intakes) answers "That's above the estimated safe ceiling of X m. Grant override
authority? (yes/no)"; yes raises the ceiling to that altitude; "/override altitude on|off".
"""
import contextlib
import contextvars
import logging
import math
import re
import threading
import time

from . import protect

log = logging.getLogger("kspchat")

V_TARGET = 200.0       # default target / safety speed (EAS) - same as plane.V_LOW
V_CAP = 220.0          # default HARD cap (EAS) - same as plane.V_HARD
CAP_MARGIN = 20.0      # a granted request flies at its EAS with the hard cap this much above it
H_RHO = 5600.0         # Kerbin density scale height (m): rho ~ rho0 * exp(-h / H)
PENDING_TTL = 600.0    # s a pending request stays answerable
DEFAULT_SESSION = "ingame"  # the in-game chat; Flight Plan / MCP / menu tool calls land here too

_lock = threading.Lock()
_state = {"authority": {"speed": False, "altitude": False},  # blanket /override <kind> on
          "eas": None, "ceiling": None,  # eas = raised speed cap target (EAS); ceiling = raised safe ceiling (m MSL)
          "bank": None, "pitch": None,   # Flight Plan / chat 'override bank|pitch N' (None = Luke's defaults)
          "all": False,                  # 'authorise all': every override applies without asking
          "speed_max": False}            # 'override speed max': no speed cap at all (full throttle)
_marks = {}                            # override kind -> authority marker shown in STATUS ('*' asked, '***', 'all')
BANK_LOW, BANK_FAST = 20.0, 15.0       # Luke's bank limits: 20 deg, 15 above 250 m/s
OVERRIDE_RANGE = {"bank": (5.0, 80.0), "pitch": (5.0, 90.0), "speed": (50.0, 3000.0), "altitude": (500.0, 70000.0)}
_pending = {}                                # session -> request
_session = contextvars.ContextVar("speedcap_session", default=DEFAULT_SESSION)
_origin = contextvars.ContextVar("speedcap_origin", default="chat")

_YES = {"y", "yes", "yeah", "yep", "yup", "sure", "ok", "okay", "affirmative", "grant", "granted", "grant it",
        "confirm", "confirmed", "approve", "approved", "do it", "yes do it", "go ahead", "yes please", "override"}
_NO = {"n", "no", "nope", "nah", "negative", "deny", "denied", "cancel", "don't", "dont", "no thanks", "keep the cap",
       "keep cap"}


def eas_at(tas, alt_m):
    """Equivalent airspeed of a true airspeed at an altitude (exponential Kerbin atmosphere)."""
    return float(tas) * math.exp(-max(float(alt_m or 0.0), 0.0) / (2.0 * H_RHO))


def tas_at(eas, alt_m):
    return float(eas) * math.exp(max(float(alt_m or 0.0), 0.0) / (2.0 * H_RHO))


def limits():
    """(target, hard cap) as EAS in m/s - the defaults unless an override raised them."""
    with _lock:
        e = _state["eas"]
        if _state.get("speed_max"):
            return 1.0e6, 1.0e6
    if e:
        return max(V_TARGET, e), max(V_CAP, e + CAP_MARGIN)
    return V_TARGET, V_CAP


def raised(kind="speed"):
    return bool(_state["eas"] if kind == "speed" else _state["ceiling"])


def ceiling(estimate):
    """Effective safe ceiling (m MSL): the craft's estimate unless an override raised it."""
    return max(float(estimate), _state["ceiling"] or 0.0)


def session():
    return _session.get()


@contextlib.contextmanager
def use_session(sid=None, origin=None):
    """Tool calls inside this block store their pending override request under chat session `sid`."""
    t1 = _session.set(str(sid or DEFAULT_SESSION))
    t2 = _origin.set(origin or "chat")
    try:
        yield
    finally:
        _session.reset(t1)
        _origin.reset(t2)


def _raise(eas):
    with _lock:
        _state["eas"] = max(_state["eas"] or 0.0, float(eas))
    log.info("speedcap: cap raised -> target %.0f / hard %.0f m/s EAS", *limits())


def _ask(req):
    sid = session()
    req.update(origin=_origin.get(), t=time.time())
    with _lock:
        _pending[sid] = req
    log.info("speedcap: %s %s %.0f above the limit %.0f -> asking for override authority [%s]", req["tool"],
             req["kind"], req["value"], req["limit"], sid)
    return "needs_override: " + question(req)


def check(tool, args, speed, alt_m, speed_key="speed"):
    """None if `speed` (true airspeed, m/s) at `alt_m` is within the active cap (or override authority is on, which
    raises the cap to it). Otherwise stores the request as pending for the current session and returns the
    needs_override message for the model / player."""
    eas = eas_at(speed, alt_m)
    tgt, cap = limits()
    if eas <= cap + 0.5:
        return None
    if _state["authority"]["speed"]:
        _raise(eas)
        return None
    return _ask({"kind": "speed", "tool": tool, "args": dict(args or {}), "speed": float(speed), "value": float(speed),
                 "alt": float(alt_m or 0.0), "eas": eas, "cap": cap, "limit": cap, "key": speed_key,
                 "fallback": float(math.floor(tas_at(tgt, alt_m)))})


def check_altitude(tool, args, alt_msl, estimate, alt_key="altitude_m", given=None):
    """Like check() for an altitude target (m MSL) against the craft's estimated safe ceiling (None = unknown)."""
    if estimate is None:
        return None
    lim = ceiling(estimate)
    if alt_msl <= lim + 1.0:
        return None
    if _state["authority"]["altitude"]:
        with _lock:
            _state["ceiling"] = max(_state["ceiling"] or 0.0, float(alt_msl))
        return None
    given = float(alt_msl if given is None else given)
    return _ask({"kind": "altitude", "tool": tool, "args": dict(args or {}), "value": float(alt_msl), "alt": float(alt_msl),
                 "limit": lim, "key": alt_key, "fallback": float(math.floor(given - (alt_msl - lim)))})


def confirm(tool, args, question_text):
    """A destructive order (cut engines, abort, eject): store it as pending for this chat session and return the
    yes/no question. A plain 'yes' (or 'confirm ...') runs tool(**args); 'no' cancels."""
    sid = session()
    req = {"kind": "confirm", "tool": tool, "args": dict(args or {}), "question": question_text, "value": 0.0,
           "limit": 0.0, "origin": _origin.get(), "t": time.time()}
    with _lock:
        _pending[sid] = req
    log.info("confirm: %s asks for a yes/no [%s]", tool, sid)
    return "needs_confirm: " + question(req)


def describe_limit(kind):
    return f"{limits()[1]:.0f} m/s cap" if kind == "speed" else "estimated safe ceiling"


def question(req):
    if req["kind"] == "confirm":
        return req["question"] + " [Relay this question to Luke and wait; don't call the tool again - his 'yes' runs it.]"
    if req["kind"] == "altitude":
        return (f"That's above the estimated safe ceiling of {req['limit']:.0f} m (you asked {req['value']:.0f} m MSL)."
                " Grant override authority? (yes/no)"
                " [Relay this question to Luke and wait; don't call the tool again - his 'yes' applies it.]")
    where = ("low-altitude cap" if req["alt"] < 3000.0 else
             f"cap (equivalent airspeed; ~{tas_at(req['cap'], req['alt']):.0f} m/s true at {req['alt']:.0f} m)")
    extra = (f" (you asked {req['speed']:.0f} m/s at {req['alt']:.0f} m)" if req["alt"] < 3000.0 else
             f" {req['speed']:.0f} m/s there is ~{req['eas']:.0f} m/s EAS")
    return (f"That's above the {req['cap']:.0f} m/s {where}{extra}. Grant override authority? (yes/no)"
            " [Relay this question to Luke and wait; don't call the tool again - his 'yes' applies it.]")


def pending(sid=None):
    sid = str(sid or session())
    with _lock:
        req = _pending.get(sid)
        if req and time.time() - req["t"] > PENDING_TTL:
            _pending.pop(sid, None)
            req = None
    return req


def answer(text):
    """'yes' / 'no' for a plain reply to the override question, else None."""
    t = re.sub(r"[\s.!,]+$", "", (text or "").strip().lower())
    t = re.sub(r"\s+", " ", t)
    if t in _YES or (t.startswith("confirm ") and len(t) < 30):  # "confirm eject" / "confirm abort"
        return "yes"
    if t in _NO:
        return "no"
    return None


def grant(sid=None):
    """Grant the pending request: raise the cap to its EAS. Returns the request (or None)."""
    sid = str(sid or session())
    req = pending(sid)
    if not req:
        return None
    with _lock:
        _pending.pop(sid, None)
    if req["kind"] == "confirm":
        pass  # nothing to raise: the order just runs
    elif req["kind"] == "speed":
        _raise(req["eas"])
    else:
        with _lock:
            _state["ceiling"] = max(_state["ceiling"] or 0.0, req["value"])
        log.info("speedcap: safe ceiling raised -> %.0f m", _state["ceiling"])
    req["granted"] = True
    return req


def deny(sid=None):
    sid = str(sid or session())
    req = pending(sid)
    with _lock:
        _pending.pop(sid, None)
    if req:
        req["granted"] = False
        log.info("speedcap: %s override denied - limit stays", req["kind"])
    return req


def drop(sid=None):
    with _lock:
        _pending.pop(str(sid or session()), None)


def set_authority(on, kind="speed"):
    with _lock:
        _state["authority"][kind] = bool(on)
        if not on:
            _state["eas" if kind == "speed" else "ceiling"] = None
            for k in [k for k, r in _pending.items() if r.get("kind") == kind]:
                _pending.pop(k, None)
    log.info("speedcap: %s override authority %s", kind, "ON" if on else "OFF")


def _eased(over, default):
    """The override, minus whatever the airframe/crew watcher (protect) has eased off (factor 1 = full override)."""
    over = float(over)
    return over if over <= default else default + (over - default) * protect.factor()


def bank_override():
    """The overridden bank limit as flown now (None = no override); 0 while the pilot is blacked out."""
    b = _state["bank"]
    if not b:
        return None
    if protect.blackout():
        return 0.0
    eff, cap = _eased(b, BANK_LOW), _emergency_cap()
    return eff if cap is None else min(eff, cap)


def _emergency_cap():
    """Wings level / gentle bank while an emergency (flameout, part loss, stall, sabotage) asks for it."""
    try:
        from . import emergency
        return emergency.bank_cap()
    except Exception:  # noqa: BLE001
        return None


def bank_limit(spd):
    """Max bank (deg) for the autopilots: an override (eased if the airframe/crew suffer), else Luke's 20 / 15 above
    250 m/s; 0 (wings level) while the pilot is blacked out."""
    if protect.blackout():
        return 0.0
    d = BANK_FAST if float(spd or 0.0) > 250.0 else BANK_LOW
    b = _state["bank"]
    eff, cap = (_eased(b, d) if b else d), _emergency_cap()
    return eff if cap is None else min(eff, cap)


def climb_pitch(default):
    """Climb attitude (deg) for the hold's climbs: an override (eased like the bank), else the default band value;
    at most 2 deg while the pilot is blacked out."""
    p = _state["pitch"]
    eff = _eased(p, default) if p else default
    return min(eff, 2.0) if protect.blackout() else eff


def all_authorised():
    return bool(_state["all"])


def authorise_all():
    """'authorise all': every override (and speed / ceiling request) applies without asking, until 'override off'."""
    with _lock:
        _state["all"] = True
    set_authority(True, "speed")
    set_authority(True, "altitude")
    log.info("speedcap: authorise all")
    return ("Authority granted for every override this session (bank, pitch, speed, altitude) - no more yes/no. "
            "'override off' restores the defaults.")


def _override_desc(kind, value):
    if kind == "speed" and str(value).lower() == "max":
        return "speed cap (none: full throttle)"
    return {"bank": f"bank limit {value:.0f} deg", "pitch": f"climb pitch {value:.0f} deg",
            "speed": f"speed cap {value:.0f} m/s EAS", "altitude": f"safe ceiling {value:.0f} m"}[kind]


def apply_override(kind, value):
    if kind == "speed" and str(value).lower() == "max":
        with _lock:
            _state["speed_max"] = True
        log.info("speedcap: override speed max (no cap)")
        return "Override: no speed cap for the rest of the session ('override speed off' restores it)."
    value = float(value)
    if kind in ("bank", "pitch"):
        protect.ensure()  # the airframe / crew watcher eases it off if G, heat or breakage get bad
    with _lock:
        if kind in ("bank", "pitch"):
            _state[kind] = value
        elif kind == "speed":
            _state["eas"] = max(value - CAP_MARGIN, 1.0)  # the hard cap becomes the requested value
        else:
            _state["ceiling"] = value
    log.info("speedcap: override %s -> %.0f", kind, value)
    return f"Override: {_override_desc(kind, value)} for the rest of the session ('override {kind} off' restores it)."


def clear_override(kind="all"):
    kinds = ["bank", "pitch", "speed", "altitude"] if kind == "all" else [kind]
    for k in kinds:
        if k in ("bank", "pitch"):
            with _lock:
                _state[k] = None
        else:
            set_authority(False, k)
            if k == "speed":
                with _lock:
                    _state["speed_max"] = False
    for k in kinds:
        _marks.pop(k, None)
    if kind == "all":
        with _lock:
            _state["all"] = False
        return "All overrides off: bank 20 / 15 deg, climb pitch band, 220 m/s EAS cap and the estimated ceiling are back."
    return f"{kind.capitalize()} override off: back to the default."


def status_text():
    tgt, cap = limits()
    a = _state["authority"]
    extra = "".join(f" {k.capitalize()} override {_state[k]:.0f} deg." for k in ("bank", "pitch") if _state[k])
    extra += " Authorise all is ON." if _state["all"] else ""
    extra += " Speed override MAX (no cap)." if _state.get("speed_max") else ""
    extra += f" Override eased to {100 * protect.factor():.0f}% ({protect._st['reason']})." if protect.factor() < 1.0 else ""
    extra += " PILOT BLACKED OUT." if protect.blackout() else ""
    return (extra.strip() + " " if extra else "") + (f"Speed override {'ON' if a['speed'] else 'off'}; cap {cap:.0f} m/s EAS (target {tgt:.0f})"
            + (" raised by an override" if raised() else " = Luke's default 200/220")
            + f". Altitude override {'ON' if a['altitude'] else 'off'}"
            + (f"; ceiling raised to {_state['ceiling']:.0f} m." if raised("altitude") else
               "; ceiling = the craft's estimate."))


def command(arg):
    """/override speed|altitude on|off (no kind = both), /override = status."""
    a = (arg or "").strip().lower().split()
    kinds = ["speed", "altitude"]
    if a and a[0] in ("speed", "spd"):
        kinds, a = ["speed"], a[1:]
    elif a and a[0] in ("altitude", "alt", "ceiling"):
        kinds, a = ["altitude"], a[1:]
    if not a or a[0] in ("status", "?"):
        return status_text() + " Use /override speed|altitude on|off."
    if a[0] in ("on", "yes", "grant"):
        for k in kinds:
            set_authority(True, k)
        what = " and ".join("the speed cap" if k == "speed" else "the safe ceiling" for k in kinds)
        return (f"Override authority ON for {what} for this bridge session: requests above it are applied without "
                "asking (the limit rises to what you ask for)." + (" Say 'yes' to apply the pending request." if pending() else ""))
    if a[0] in ("off", "no"):
        for k in kinds:
            set_authority(False, k)
        out = []
        if "speed" in kinds:
            out.append(f"Speed override OFF: back to the {V_CAP:.0f} m/s hard cap ({V_TARGET:.0f} target), equivalent airspeed.")
        if "altitude" in kinds:
            out.append("Altitude override OFF: back to the craft's estimated safe ceiling.")
        return " ".join(out)
    return "Usage: /override speed|altitude on|off (or /override for the status)."
