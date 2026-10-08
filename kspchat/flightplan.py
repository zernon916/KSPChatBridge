"""Flight Plan (AICS > Flight Plan): a plain-text list of steps, one per line, that AI fill (or a template) writes
into the panel's editor, Luke edits, and Fly runs step by step with the existing tools.

Step format (one per line; '#' starts a comment; numbering / bullets / markdown are ignored):
  PLANES (bridge autopilot: hold.py + plane.py; MechJeb's aircraft autoland is unreliable, see plane.py)
    takeoff                                    from the runway the plane is lined up on (holds take off by themselves)
    climb 3000 m agl vs 50                     altitude above the field ('msl' = sea level); vs = climb rate, optional
    descend 1000 m agl                         same as climb
    cruise hdg 090 alt 3000 m agl speed 180 for 3 min    every part optional ('for' takes s / min / km; default 2 min)
    circle 3 laps left bank 15                 circling turns where we are (left/right, bank 5-20)
    circle 5 laps around field radius 12 km    laps around a runway ('field' = where we took off, or a spot name)
    fly to Island Airfield alt 4000 m speed 200    flies there and lands (fly_to)
    land                                       back at the departure field; or 'land Runway 27' / 'land Island Airfield'
    land Runway 09 tg 2                        2 touch-and-goes, then a full stop (KSC runways)
  ROCKETS (MechJeb via kRPC.MechJeb wherever it has an autopilot)
    ascent 80 km inc 0 | circularize | transfer Mun | warp to soi | warp to apoapsis | course correction 30 km
    deorbit 30 km | land ksc | land here | land <spot> | chutes
  ANY
    wait 30 s | science | science no transmit | stage
  LIMIT OVERRIDES (rest of the plan / session; speedcap.py)
    override bank 50 *        bank / pitch (climb attitude) / speed (m/s EAS cap) / altitude (m ceiling). '*' or no
    override speed 400 ***    marker = ask Luke yes/no in chat when the step runs (no / no answer = defaults kept);
    override off              '***' = pre-authorized, applies directly. 'override off' / 'override bank off' restores.
    authorise all             every override (and speed / ceiling request) applies without asking ('authorize' too)

parse(text, strict=True) -> (steps, errors); fmt(step) -> canonical line (re-parses to the same step).
draft(...) asks the current AI backend for a plan and normalizes its reply to the step format (no tools run).
start(text) runs the plan in a background thread; stop() ends the runner (the active autopilot keeps flying);
abort() (ksp_actions) also stops it.
"""
import logging
import math
import re
import threading
import time

from . import guard, hold, plane, science, settings, speedcap, spots

log = logging.getLogger("kspchat")

BODIES = ("Mun", "Minmus", "Kerbin", "Duna", "Ike", "Eve", "Gilly", "Moho", "Dres", "Jool", "Laythe", "Vall", "Tylo",
          "Bop", "Pol", "Eeloo")
FIELD_WORDS = ("field", "airfield", "the field", "the airfield", "runway", "the runway", "home", "base", "airport",
               "departure", "departure field")
KSC_WORDS = ("ksc", "ksc runway", "kerbal space center", "space center", "ksp")  # (Luke typed "ksp" for KSC)
DEFAULT_CRUISE_S = 120.0

# ---------------------------------------------------------------- parsing
_OPS = [  # (op, regex) - the earliest match in a line decides the step
    ("override", r"^override\b"),
    ("authall", r"^authori[sz]e\s+all\b"),
    ("tg", r"touch[\s-]*(?:and|&|n)[\s-]*go"),
    ("takeoff", r"\btake[\s-]?off\b|\btakeoff\b|\bdepart\b"),
    ("flyto", r"\bfly\s+(?:over\s+)?to\b|\bnavigate\s+to\b|\bhead\s+(?:over\s+)?to\b|\bgo\s+to\b"),
    ("circularize", r"\bcirculari[sz]\w*"),
    ("circle", r"\bcircl\w*|\bloiter\w*|\blaps?\b|holding pattern|\borbits?\s+(?:the|over|around)\b|\bracetrack"),
    ("correction", r"\bcourse\s+correct\w*"),
    ("deorbit", r"\bde-?orbit\w*"),
    ("ascent", r"\blaunch\w*|\bascent\b|\bto\s+orbit\b|\blift[\s-]?off\b"),
    ("transfer", r"\btransfer\b|\bhohmann\b"),
    ("warp", r"\bwarp\w*"),
    ("stationkeep", r"\bstation[\s-]*keep\w*|\bgeo-?sync\w*|\bgeostationary\b|\bsynchronous\s+orbit\b"),
    ("matchplane", r"\bmatch\s+(?:the\s+)?(?:target\s+)?planes?\b"),
    ("lonap", r"\bapsis\s+longitude\b|\blongitude\s+of\s+(?:the\s+)?apsis\b"),
    ("setap", r"\b(?:set|raise|lower|change)\s+(?:the\s+)?(?:ap|apoapsis|apo)\b|^ap\b|^apoapsis\b"),
    ("setpe", r"\b(?:set|raise|lower|change)\s+(?:the\s+)?(?:pe|periapsis|peri)\b|^pe\b|^periapsis\b|\baero-?captur\w*|\baerobrak\w*"),
    ("inclination", r"\b(?:set|change)\s+(?:the\s+)?inclination\b|^inclination\b|^inc\b"),
    ("taxi", r"\btaxi\w*"),
    ("climb", r"\bclimb\w*|\bdescend\w*|\bdescent\b|\bset\s+alt\w*|\baltitude\s+(?:hold|to)\b|\blevel\s+off\b"),
    ("cruise", r"\bcruis\w*|\bfly\s+(?:heading|hdg|straight|level|out)\b|\bturn\s+(?:to|onto|left|right)\b"
               r"|\bhold\s+(?:heading|hdg)\b|^heading\b|^hdg\b"),
    ("land", r"\bland(?:ing)?\b|\btouch\s*down\b|\bautoland\b"),
    ("wait", r"^wait\b|^pause\b"),
    ("chutes", r"\bchutes?\b|\bparachutes?\b"),
    ("science", r"\bscience\b|\bexperiments?\b"),
    ("stage", r"^stage\b"),
]
_OPS_RE = [(op, re.compile(rx, re.I)) for op, rx in _OPS]
_OP_WORDS = (r"take|climb|descend|cruis|circl|loiter|land|fly|head|turn|wait|circulari|transfer|launch|deorbit|warp"
             r"|touch|ascent|science|chute|parachute|stage|set|raise|lower|match|station|taxi|inclination|override|authori")
_NUM = r"(\d+(?:\.\d+)?)"


class PlanError(ValueError):
    pass


def _clean(line):
    s = (line or "").replace("\u2013", "-").replace("\u2014", "-").replace("\u2192", "->").replace("\u00a0", " ")
    s = s.replace("\u2212", "-").replace("\u2011", "-")
    s = re.sub(r"^\s*>+", "", s)                 # markdown quote
    s = re.sub(r"[*_`|]+", " ", s)
    for _ in range(2):  # bullets / numbering / "Step 3:" prefixes
        s = re.sub(r"^\s*(?:[-+\u2022]|\d+\s*[.):]|step\s*\d+\s*[:.)-]?)\s*", "", s, flags=re.I)
    s = re.sub(r"(\d),(\d{3})\b", r"\1\2", s)  # 3,000 -> 3000
    s = re.sub(r"\s#.*$", "", s)                 # trailing comment
    return " ".join(s.split()).strip(" .")


def _pieces(s):
    """Split a compound line ("take off, climb to 3000 m then circle 2 laps and land") into single steps."""
    out = []
    for p in re.split(r"\s*(?:;|->|\bthen\b|,\s*then\b|\.\s+)\s*", s, flags=re.I):
        out += re.split(r"\s*(?:,|\band\b|&)\s*(?=(?:" + _OP_WORDS + r"))", p, flags=re.I)
    return [p.strip(" ,.") for p in out if p and p.strip(" ,.")]


def _op_of(s):
    best = None
    for op, rx in _OPS_RE:
        m = rx.search(s)
        if m and (best is None or m.start() < best[1]):
            best = (op, m.start())
    return best[0] if best else None


def _take(rx, s, flags=re.I):
    """Find rx in s; return (match or None, s with the match removed)."""
    m = re.search(rx, s, flags)
    if not m:
        return None, s
    return m, (s[:m.start()] + " " + s[m.end():])


def _alt_m(val, unit):
    v = float(val)
    u = (unit or "m").lower()
    if u in ("km", "k"):
        return v * 1000.0
    if u in ("ft", "feet"):
        return v * 0.3048
    return v


def _known_spots():
    try:
        return spots.all_spots()
    except Exception:  # noqa: BLE001
        return {}


def _spot_in(s):
    """Longest known spot name mentioned in s, else a KSC / field word, else None."""
    low = spots._norm(s)
    for k in sorted(_known_spots(), key=len, reverse=True):
        if re.search(r"(?<![a-z0-9])" + re.escape(spots._norm(k)) + r"(?![a-z0-9])", low):
            return k
    if re.search(r"\b(?:ksc|ksp|kerbal space center|space center)\b", low):
        return "KSC"
    m = re.search(r"\brunway\s*(\d{1,2})\b", low)
    if m:
        return f"Runway {int(m.group(1)):02d}"
    if re.search(r"\b(?:air)?field\b|\bairport\b|\bthe runway\b|\bhome\b|\bbase\b", low):
        return "field"
    return None


def _duration(s):
    m, s = _take(r"\bfor\s+" + _NUM + r"\s*(seconds?|secs?|s|minutes?|mins?|min|m|hours?|h|km|kilometers?)\b", s)
    if not m:
        return None, None, s
    v, u = float(m.group(1)), m.group(2).lower()
    if u.startswith("k"):
        return None, v * 1000.0, s
    if u.startswith("h"):
        return v * 3600.0, None, s
    if u.startswith("m"):
        return v * 60.0, None, s
    return v, None, s


def _hdg(s):
    m, s2 = _take(r"\b(?:hdg|heading|course|turn\s+(?:left\s+|right\s+)?(?:to|onto))\s*(?:of|to|:)?\s*(\d{1,3}(?:\.\d+)?)"
                  r"(?:\s*(?:deg\w*|\u00b0))?", s)
    if m:
        return float(m.group(1)) % 360, s2
    return None, s


def _speed(s, vs_ok=False):
    """(speed, vs, rest). 'speed N' / 'at N m/s'. In a climb line a bare 'N m/s' <= 90 is the climb rate."""
    vs = None
    m, s = _take(r"\b(?:vs|v/s|vertical\s+speed|climb\s+rate|rate\s+of\s+climb|climb\s+at\s+a\s+rate\s+of|rate\s+of)"
                 r"\s*(?:of|at|:)?\s*[+-]?" + _NUM + r"\s*(?:m/s)?", s)
    if m:
        vs = float(m.group(1))
    m, s = _take(r"\b(?:speed|spd|airspeed)\s*(?:of|to|at|:)?\s*" + _NUM + r"(?:\s*m/s)?", s)
    spd = float(m.group(1)) if m else None
    while True:
        m, s2 = _take(r"[+-]?" + _NUM + r"\s*m/s", s)
        if not m:
            break
        val = float(m.group(1))
        if vs_ok and vs is None and val <= 90:
            vs = val
        elif spd is None:
            spd = val
        s = s2
    return spd, vs, s


def _altitude(s):
    """(alt_m, ref, rest)."""
    ref = "msl" if re.search(r"\b(?:msl|asl|sea\s*level)\b", s, re.I) else "agl"
    m, s2 = _take(r"\b(?:alt|altitude|fl)\s*(?:of|to|at|:)?\s*" + _NUM + r"\s*(km|k|m|ft|feet)?\b(?!\s*/)", s)
    if not m:
        m, s2 = _take(_NUM + r"\s*(km|k|m|ft|feet)\b(?!\s*/)", s)
    if not m:
        return None, ref, s
    return _alt_m(m.group(1), m.group(2)), ref, s2


def _laps(s):
    words = {"once": 1, "twice": 2, "thrice": 3, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6,
             "seven": 7, "eight": 8, "nine": 9, "ten": 10}
    m, s2 = _take(r"(\d+|" + "|".join(words) + r")\s*(?:x\s*)?(?:laps?|times|circles?|orbits?|loops?|turns?|circuits?)\b", s)
    if not m:
        m, s2 = _take(r"\blaps?\s*(\d+)", s)
    if not m:
        m, s2 = _take(r"\b(once|twice|thrice)\b", s)
    if not m:
        return None, s
    g = m.group(1).lower()
    return int(words.get(g, g)), s2


def parse_line(raw, strict=True):
    """One cleaned line -> list of step dicts. Raises PlanError for a line that names a step but can't be used."""
    s = _clean(raw)
    if not s or s.startswith(("#", "//")):
        return []
    auth = "pre" if re.search(r"\*{3}", raw or "") else "ask"  # authority marker ('*' is stripped by _clean)
    out = []
    for p in _pieces(s):
        st = _parse_piece(p, strict)
        if st:
            if st["op"] == "override" and st.get("value") != "off":
                st["auth"] = auth
            out.append(st)
    if not out and strict:
        if s.endswith(":") or re.match(r"^(?:flight\s+)?plan\b", s, re.I) or len(s) < 3:
            return []
        raise PlanError("not a step I know (see the step list: takeoff / climb / cruise / circle / land ...)")
    return out


def _parse_piece(p, strict):
    op = _op_of(p)
    low = p.lower()
    if op is None:
        return None
    if op == "land" and re.search(r"\b(?:for|before|prepare\s+for)\s+(?:the\s+)?landing\b", low):
        return None  # "gear down for landing" is not a landing step
    if op == "takeoff":
        return {"op": "takeoff"}
    if op == "authall":
        return {"op": "authall"}
    if op == "override":
        m = re.match(r"^override\s+(?:(bank|pitch|speed|altitude|alt|all)\s+)?(off|" + _NUM[1:-1] + r")\s*"
                     r"(km|m/?s|m|deg\w*|\u00b0)?\s*$", low)
        if not m or (m.group(2) != "off" and not m.group(1)) or (m.group(1) == "all" and m.group(2) != "off"):
            raise PlanError("override needs: override bank|pitch|speed|altitude N [* or ***], or override [kind] off")
        kind = {"alt": "altitude", None: "all"}.get(m.group(1), m.group(1))
        if m.group(2) == "off":
            return {"op": "override", "kind": kind, "value": "off"}
        v = float(m.group(2)) * (1000.0 if (m.group(3) or "") == "km" else 1.0)
        lo, hi = speedcap.OVERRIDE_RANGE[kind]
        if not lo <= v <= hi:
            raise PlanError(f"override {kind} must be {lo:.0f}-{hi:.0f}")
        return {"op": "override", "kind": kind, "value": v}
    if op in ("climb",):
        verb = "descend" if re.search(r"\bdescen", low) else "climb"
        spd, vs, rest = _speed(p, vs_ok=True)
        alt, ref, rest = _altitude(rest)
        if alt is None:
            if strict:
                raise PlanError(f"'{verb}' needs an altitude, e.g. '{verb} 3000 m agl'")
            if verb == "descend":
                return None  # "descend toward the runway": the landing step does that
            alt = 3000.0
        st = {"op": "climb", "verb": verb, "alt": alt, "ref": ref}
        if not strict and not _altitude(rest)[0] and alt == 3000.0 and "3000" not in p:
            st["alt_default"] = True
        if vs:
            st["vs"] = abs(vs)
        if spd:
            st["spd"] = spd
        return st
    if op == "cruise":
        secs, dist, rest = _duration(p)
        hdg, rest = _hdg(rest)
        spd, _, rest = _speed(rest)
        alt, ref, rest = _altitude(rest)
        st = {"op": "cruise"}
        if hdg is not None:
            st["hdg"] = hdg
        if alt is not None:
            st.update(alt=alt, ref=ref)
        if spd:
            st["spd"] = spd
        if dist:
            st["dist"] = dist
        else:
            st["secs"] = secs if secs else DEFAULT_CRUISE_S
        return st
    if op == "circle":
        laps, rest = _laps(p)
        st = {"op": "circle", "laps": max(1, min(50, laps or 1))}
        st["dir"] = "left" if re.search(r"\bleft\b|\bcounter[\s-]*clockwise\b|\bccw\b|\banti[\s-]*clockwise\b", low) else "right"
        m, rest = _take(r"\bradius\s*(?:of|:)?\s*" + _NUM + r"\s*(km|m)?", rest)
        if m:
            r = float(m.group(1))
            st["radius"] = r * 1000.0 if (m.group(2) or ("km" if r < 200 else "m")).lower() == "km" else r
        m, rest = _take(r"\bbank\w*\s*(?:of|angle|at|:)?\s*" + _NUM, rest)
        if not m:
            m, rest = _take(_NUM + r"\s*(?:deg\w*|\u00b0)\s*bank", rest)
        st["bank"] = max(5.0, min(80.0, float(m.group(1)))) if m else 15.0  # flown at most at the bank limit
        where = _spot_in(rest)
        if where:
            st["around"] = where
        spd, _, rest = _speed(rest)
        alt, ref, rest = _altitude(rest)
        if alt is not None:
            st.update(alt=alt, ref=ref)
        if spd:
            st["spd"] = spd
        return st
    if op == "flyto":
        where = _spot_in(p)
        if not where or where == "field":
            m = re.search(r"\b(?:to)\s+(.+?)(?:\s+(?:alt|at|speed|cruise)\b|$)", p, re.I)
            raise PlanError(f"fly to: unknown runway '{m.group(1) if m else p}' - known: " + ", ".join(_runway_names()))
        spd, _, rest = _speed(p)
        alt, _, rest = _altitude(re.sub(re.escape(where), " ", rest, flags=re.I))
        st = {"op": "flyto", "target": where}
        if alt is not None:
            st["alt"] = alt
        if spd:
            st["spd"] = spd
        return st
    if op in ("land", "tg"):
        st = {"op": "land"}
        m = re.search(r"\btg\s*(\d+)|(\d+)\s*(?:x\s*)?touch[\s-]*(?:and|&|n)[\s-]*go"
                      r"|touch[\s-]*(?:and|&|n)[\s-]*go(?:es)?\s*(?:x\s*)?(\d+)?", low)
        if m:
            n = next((g for g in m.groups() if g), None)
            st["tg"] = int(n) if n else 1
        if re.search(r"\bhere\b|\bwhere\s+we\s+are\b", low):
            st["target"] = "here"
        else:
            where = _spot_in(re.sub(r"\btg\s*\d+", " ", p, flags=re.I))
            if where and where != "field":
                st["target"] = where
        return st
    if op == "wait":
        m = re.search(_NUM + r"\s*(seconds?|secs?|s|minutes?|mins?|min|m)?\b", p, re.I)
        if not m:
            raise PlanError("wait needs a time, e.g. 'wait 30 s'")
        v = float(m.group(1))
        return {"op": "wait", "secs": v * 60.0 if (m.group(2) or "s").lower().startswith("m") else v}
    if op == "ascent":
        m, rest = _take(r"\binc\w*\s*(?:of|:)?\s*(-?\d+(?:\.\d+)?)", p)
        inc = float(m.group(1)) if m else 0.0
        m = re.search(_NUM + r"\s*(km|k|m)?\b", rest)
        km = 80.0
        if m:
            v, u = float(m.group(1)), (m.group(2) or "").lower()
            km = v / 1000.0 if (u == "m" or (not u and v >= 1000)) else v
        if km < 70 or km > 5000:
            if strict:
                raise PlanError(f"ascent altitude {km:g} km is outside 70-5000 km")
            km = 80.0
        return {"op": "ascent", "km": km, "inc": inc}
    if op == "circularize":
        return {"op": "circularize"}
    if op == "transfer":
        b = next((b for b in BODIES if re.search(r"\b" + b + r"\b", p, re.I)), None)
        if not b:
            raise PlanError("transfer needs a body, e.g. 'transfer Mun'")
        return {"op": "transfer", "body": b}
    if op == "warp":
        if re.search(r"\bapo|\bap\b", low):
            return {"op": "warp", "what": "apoapsis"}
        if re.search(r"\bsoi\b|sphere|encounter|" + "|".join(b.lower() for b in BODIES), low):
            return {"op": "warp", "what": "soi"}
        raise PlanError("warp to soi / warp to apoapsis")
    if op in ("correction", "deorbit"):
        m = re.search(_NUM + r"\s*(km|k|m)?\b", p)
        pe = 30.0 if op == "deorbit" else 50.0
        if m:
            v, u = float(m.group(1)), (m.group(2) or "").lower()
            pe = v / 1000.0 if (u == "m" or (not u and v >= 1000)) else v
        return {"op": op, "pe": pe}
    if op in ("setap", "setpe"):
        m = re.search(_NUM + r"\s*(km|k|m)?\b", p)
        if not m:
            raise PlanError(f"{'set ap' if op == 'setap' else 'set pe'} needs an altitude, e.g. '{'set ap' if op == 'setap' else 'set pe'} 100 km'")
        v, u = float(m.group(1)), (m.group(2) or "").lower()
        return {"op": op, "km": v / 1000.0 if (u == "m" or (not u and v >= 100000)) else v}
    if op == "inclination":
        m = re.search(r"(-?\d+(?:\.\d+)?)", p)
        if not m:
            raise PlanError("inclination needs degrees, e.g. 'inclination 0'")
        return {"op": "inclination", "deg": float(m.group(1))}
    if op == "lonap":
        m = re.search(r"(-?\d+(?:\.\d+)?)", p)
        if not m:
            raise PlanError("apsis longitude needs a longitude, e.g. 'apsis longitude -74.56'")
        return {"op": "lonap", "lon": float(m.group(1))}
    if op == "matchplane":
        return {"op": "matchplane"}
    if op == "stationkeep":
        m = re.search(r"\b(?:lon|longitude)\s*(-?\d+(?:\.\d+)?)", p, re.I)
        return {"op": "stationkeep", **({"lon": float(m.group(1))} if m else {})}
    if op == "taxi":
        rest = re.sub(r"^.*?\btaxi\w*\s*(?:to\s+)?", "", p, flags=re.I)
        m, rest = _take(r"\b(?:at|speed)\s*" + _NUM + r"\s*(?:m/s)?", rest)
        name = rest.strip(" ,.")
        if not name:
            raise PlanError("taxi to <point>, e.g. 'taxi to Runway 09 start'")
        from . import taxi as _taxi
        if _taxi.resolve(name) is None:
            raise PlanError(f"unknown taxi point '{name}' (known: {', '.join(_taxi.points())})")
        st = {"op": "taxi", "target": _taxi.resolve(name)[0]}
        if m:
            st["spd"] = float(m.group(1))
        return st
    if op == "chutes":
        return {"op": "chutes"}
    if op == "science":
        return {"op": "science", "transmit": not re.search(r"\bno\s+transmit|\bdon'?t\s+transmit|\bkeep\b", low)}
    if op == "stage":
        return {"op": "stage"}
    return None


def parse(text, strict=True):
    """-> (steps, errors). errors = [(line_no, source, message)]. Lenient mode (strict=False) drops what it can't
    use (AI prose) and fills defaults; strict mode is what Fly uses."""
    steps, errors = [], []
    for i, raw in enumerate((text or "").splitlines(), 1):
        try:
            for st in parse_line(raw, strict):
                st["line"] = i
                steps.append(st)
        except PlanError as e:
            if strict:
                errors.append((i, raw.strip(), str(e)))
    return (steps if strict else _merge_climbs(steps)), errors


def _merge_climbs(steps):
    """Lenient mode: "climb to 8k ... climb at a rate of 40 m/s" is ONE climb (rate merged into the altitude step)."""
    out = []
    for st in steps:
        guess = st.pop("alt_default", False)
        prev = out[-1] if out and out[-1]["op"] == "climb" else None
        if st["op"] == "climb" and guess and prev:
            for k in ("vs", "spd"):
                if st.get(k) and not prev.get(k):
                    prev[k] = st[k]
            continue
        if st["op"] == "climb" and prev and prev.pop("_guess", False):
            for k in ("vs", "spd"):
                if prev.get(k) and not st.get(k):
                    st[k] = prev[k]
            out[-1] = st
            continue
        if st["op"] == "climb" and guess:
            st["_guess"] = True
        out.append(st)
    for st in out:
        st.pop("_guess", None)
    return out


def _g(x):
    return f"{x:g}" if abs(x - round(x)) > 1e-6 else str(int(round(x)))


def fmt(st):
    op = st["op"]
    if op == "takeoff":
        return "takeoff"
    if op == "authall":
        return "authorise all"
    if op == "override":
        if st["value"] == "off":
            return "override off" if st["kind"] == "all" else f"override {st['kind']} off"
        return f"override {st['kind']} {_g(st['value'])} " + ("***" if st.get("auth") == "pre" else "*")
    if op == "climb":
        s = f"{st.get('verb', 'climb')} {_g(st['alt'])} m {st.get('ref', 'agl')}"
        if st.get("vs"):
            s += f" vs {_g(st['vs'])}"
        if st.get("spd"):
            s += f" speed {_g(st['spd'])}"
        return s
    if op == "cruise":
        s = "cruise"
        if st.get("hdg") is not None:
            s += f" hdg {int(round(st['hdg'])) % 360:03d}"
        if st.get("alt") is not None:
            s += f" alt {_g(st['alt'])} m {st.get('ref', 'agl')}"
        if st.get("spd"):
            s += f" speed {_g(st['spd'])}"
        if st.get("dist"):
            s += f" for {_g(st['dist'] / 1000.0)} km"
        else:
            secs = st.get("secs") or DEFAULT_CRUISE_S
            s += f" for {_g(secs / 60.0)} min" if secs % 60 == 0 else f" for {_g(secs)} s"
        return s
    if op == "circle":
        n = st.get("laps", 1)
        s = f"circle {n} lap{'s' if n != 1 else ''} {st.get('dir', 'right')} bank {_g(st.get('bank', 15))}"
        if st.get("around"):
            s += f" around {st['around']}"
        if st.get("radius"):
            s += f" radius {_g(st['radius'] / 1000.0)} km"
        if st.get("alt") is not None:
            s += f" alt {_g(st['alt'])} m {st.get('ref', 'agl')}"
        if st.get("spd"):
            s += f" speed {_g(st['spd'])}"
        return s
    if op == "flyto":
        s = f"fly to {st['target']}"
        if st.get("alt") is not None:
            s += f" alt {_g(st['alt'])} m"
        if st.get("spd"):
            s += f" speed {_g(st['spd'])}"
        return s
    if op == "land":
        s = "land" + (f" {st['target']}" if st.get("target") else "")
        return s + (f" tg {st['tg']}" if st.get("tg") else "")
    if op == "wait":
        return f"wait {_g(st['secs'])} s"
    if op == "ascent":
        return f"ascent {_g(st['km'])} km inc {_g(st.get('inc', 0))}"
    if op == "transfer":
        return f"transfer {st['body']}"
    if op == "warp":
        return f"warp to {st['what']}"
    if op == "correction":
        return f"course correction {_g(st['pe'])} km"
    if op == "deorbit":
        return f"deorbit {_g(st['pe'])} km"
    if op == "science":
        return "science" if st.get("transmit", True) else "science no transmit"
    if op == "setap":
        return f"set ap {_g(st['km'])} km"
    if op == "setpe":
        return f"set pe {_g(st['km'])} km"
    if op == "inclination":
        return f"inclination {_g(st['deg'])}"
    if op == "lonap":
        return f"apsis longitude {_g(st['lon'])}"
    if op == "matchplane":
        return "match plane"
    if op == "stationkeep":
        return "station keep" + (f" lon {_g(st['lon'])}" if st.get("lon") is not None else "")
    if op == "taxi":
        return f"taxi to {st['target']}" + (f" speed {_g(st['spd'])}" if st.get("spd") else "")
    return op  # circularize, chutes, stage


def normalize(text, strict=False):
    """Plan text (AI prose / markdown / Luke's sentence) -> canonical lines (list of str)."""
    steps, _ = parse(text, strict=strict)
    out = []
    for st in steps:
        line = fmt(st)
        if not out or out[-1] != line or st["op"] in ("circle", "cruise", "wait"):
            out.append(line)
    return out


def _runway_names():
    return ["KSC"] + [k for k, v in _known_spots().items() if v.get("mode") == "H"]


# ---------------------------------------------------------------- templates
TEMPLATES = {
    "circle": ("Cruise + circle: takeoff -> climb -> cruise -> circle laps around the field -> land",
               ["takeoff", "climb 2000 m agl vs 50", "cruise speed 180 for 2 min",
                "circle 3 laps right bank 20 around field", "land"]),
    "cruise": ("Out and back: takeoff -> climb -> cruise out -> circling turns -> land (autoland flies home)",
               ["takeoff", "climb 3000 m agl vs 50", "cruise speed 200 for 4 min", "circle 1 lap left bank 15",
                "land"]),
    "circuit": ("Touch-and-go circuit at KSC (autoland flies the pattern)",
                ["takeoff", "climb 600 m agl vs 30", "land Runway 09 tg 2"]),
    "orbit": ("Orbit with MechJeb: ascent (autostage, circularizes at the top), then science",
              ["ascent 80 km inc 0", "science"]),
}


def template(kind):
    title, lines = TEMPLATES.get(kind) or TEMPLATES["circle"]
    return f"# {title}. Edit the numbers, then Fly.\n" + "\n".join(lines) + "\n"


# ---------------------------------------------------------------- AI draft
DRAFT_SYSTEM = """You write flight plans for Kerbal Space Program. A simple autopilot runs them line by line.
Output ONLY the plan: one step per line, no numbering, no markdown, no explanations. Allowed steps (numbers are examples):
PLANES
takeoff
climb 3000 m agl vs 50          (agl = above the field, msl = sea level; vs = climb rate in m/s)
cruise hdg 090 alt 3000 m agl speed 180 for 3 min   (every part optional; 'for' takes s, min or km)
circle 3 laps right bank 15     (circling turns where the plane is; bank 5-20)
circle 5 laps right bank 20 around field   (laps around the departure runway; or around <runway name>)
fly to Island Airfield alt 4000 m speed 200   (flies there and lands)
land                            (lands back at the departure runway; or: land Runway 27 / land Island Airfield)
land Runway 09 tg 2             (2 touch-and-goes, then full stop; KSC only)
ROCKETS (MechJeb)
ascent 80 km inc 0
circularize
transfer Mun
warp to soi
course correction 30 km
deorbit 30 km
land ksc / land here / chutes
ANY
wait 30 s
science
override bank 30 *              (raises a limit for the rest of the plan: bank / pitch / speed / altitude; ALWAYS end it
                                 with ' *' = Luke is asked yes/no when it runs. Never write '***' or 'authorise all'
                                 unless Luke explicitly said so)
override off                    (back to the default limits)
Known runways: {runways}.
A typical plane flight: takeoff, climb, cruise, circle laps, land. Never output anything except step lines."""


def _context():
    """One-paragraph craft/situation summary for the AI (empty-ish if KSP isn't reachable)."""
    try:
        from . import ksp_actions
        with ksp_actions._lock:
            v = ksp_actions._vessel()
            body = v.orbit.body
            f = v.flight(body.reference_frame)
            sit = str(v.situation).split(".")[-1]
            planeish = ksp_actions._planeish(v)
            info = {"sit": sit, "lat": f.latitude, "lon": f.longitude, "body": body.name, "R": body.equatorial_radius}
            hdg = v.flight(v.surface_reference_frame).heading
            s = (f"Craft '{v.name}' ({'plane: wings + wheels' if planeish else 'rocket / not a plane'}), {sit} on "
                 f"{body.name}, altitude {f.mean_altitude:.0f} m MSL, speed {f.speed:.0f} m/s, heading {hdg:.0f}.")
        near = _nearest_field(info)
        if near:
            s += f" Nearest runway: {near}."
        return s, planeish
    except Exception as e:  # noqa: BLE001
        return f"(KSP not reachable: {e.__class__.__name__}; assume a plane on the KSC runway)", True


def draft(backend="local", model_override=None, request="", chat_reply=""):
    """Ask the AI for a plan; return canonical plan text for the editor (never runs anything)."""
    from . import backends, chat, mcp_chat
    ctx, planeish = _context()
    req = (request or "").strip()
    want = req or ("Plan a local flight: takeoff, climb, cruise, circle laps around the field, then land back on the "
                   "runway." if planeish else "Plan an orbit flight with MechJeb.")
    user = f"Craft and situation: {ctx}\nLuke wants: {want}\n"
    if chat_reply:
        user += f"Convert this plan from the chat into the step format:\n{chat_reply[:3000]}\n"
    user += "Write the plan now, step lines only."
    head, lines, said = "", [], ""
    backend = backends.normalize(backend)
    if backend == "chatgpt" and mcp_chat.mode() == "mcp":
        head = "# ChatGPT desktop (MCP) can't fill the editor directly; plan built from your request / template."
    else:
        try:
            url, key, model = backends.resolve(backend, model_override)
            sysmsg = DRAFT_SYSTEM.format(runways=", ".join(_runway_names()))
            data = chat._post(url + "/chat/completions", {"model": model, "temperature": 0.2, "messages": [
                {"role": "system", "content": sysmsg}, {"role": "user", "content": user}]}, key)
            said = chat._strip_think(data["choices"][0]["message"].get("content"))
            lines = normalize(said)
            head = f"# AI draft ({backends.LABELS.get(backend, backend)} / {model}). Edit, then Fly."
        except Exception as e:  # noqa: BLE001
            head = f"# AI not available ({e.__class__.__name__}: {str(e)[:80]}); plan built from your request / template."
    note = ""
    if not lines:  # rule-based fallback: Luke's own words, then the chat reply
        for label, src in (("your request", req), ("the chat reply", chat_reply)):
            lines = normalize(src) if src else []
            if lines:
                note = f"\n# (AI gave no usable steps; parsed from {label})" if said else f"\n# (parsed from {label})"
                break
    if not lines:
        kind = "circle" if planeish else "orbit"
        return (head + "\n# No usable steps from the AI" + (f" (it said: {said[:100]!r})" if said else "")
                + "; template filled instead.\n" + template(kind))
    if planeish and _context_sit_landed(ctx) and lines[0].split()[0] not in ("takeoff", "fly", "land"):
        lines.insert(0, "takeoff")
    if "***" not in req and not re.search(r"\bauthori[sz]e\b|pre-?authori", req, re.I):
        # only Luke grants authority in advance: an AI-written '***' becomes '*' (ask), 'authorise all' is dropped
        lines = [ln[:-3] + "*" if ln.startswith("override ") and ln.endswith("***") else ln
                 for ln in lines if ln != "authorise all"]
    return head + note + "\n" + "\n".join(lines) + "\n"


def _context_sit_landed(ctx):
    return any(w in ctx for w in (", landed on", ", pre_launch on"))


# chat -> editor: a plan the chat AI drafts lands in the panel (polled via GET /flightplan)
_pushed = {"rev": 0, "text": ""}


def push(text, note=""):
    lines = normalize(text)
    if not lines:
        return 0
    _pushed["rev"] += 1
    _pushed["text"] = (f"# {note}\n" if note else "") + "\n".join(lines) + "\n"
    return len(lines)


def maybe_from_chat(user_msg, reply):
    """After a chat turn: if Luke asked for a flight plan and the reply contains one, load it into the editor."""
    if not re.search(r"\bflight\s*plan\b|\bplan\b.*\b(?:flight|fly|circle|land|take\s*off)", user_msg or "", re.I):
        return 0
    n = push(reply, "From the chat (normalized). Check it, then Fly.")
    if n < 2:  # a prose reply: try Luke's own words instead
        n = push(user_msg, "From your chat request (parsed). Check it, then Fly.")
    return n


def pushed_since(rev):
    return (_pushed["rev"], _pushed["text"] if _pushed["rev"] > rev else "")


# ---------------------------------------------------------------- runner
_state = {"running": False, "steps": [], "i": -1, "detail": "", "result": "", "field": None, "t0": 0.0}
_thread = None
_stop = threading.Event()
_now = time.time
_sleep = time.sleep


def active():
    return _thread is not None and _thread.is_alive()


def stop():
    if not active():
        return "No flight plan is running."
    _stop.set()
    return "Flight plan stopped after the current step; the autopilot that is flying keeps flying (Abort = everything off)."


def status_line():
    st = _state
    if st["running"] and 0 <= st["i"] < len(st["steps"]):
        return f"Plan step {st['i'] + 1}/{len(st['steps'])}: {fmt(st['steps'][st['i']])} - {st['detail']}"
    return ("Plan: " + st["result"]) if st["result"] else ""


def check(text):
    steps, errs = parse(text)
    if errs:
        return "Fix: " + "; ".join(f"line {n} '{src}': {msg}" for n, src, msg in errs[:5])
    if not steps:
        return "The plan is empty: use AI fill or a template, or type steps (takeoff / climb 3000 m agl / circle 2 laps / land)."
    return f"OK, {len(steps)} steps: " + " | ".join(fmt(s) for s in steps)


def start(text):
    global _thread
    if active():
        return "A flight plan is already running (Stop it first)."
    steps, errs = parse(text)
    if errs:
        return "Can't fly this plan yet. " + check(text)
    if not steps:
        return check(text)
    claim = guard.busy()
    if claim and not (claim.get("kind") == "plane" and hold.active()):
        return (guard.refuse_msg() or "Busy.") + " (Abort it, then Fly.)"
    _stop.clear()
    _state.update(running=True, steps=steps, i=-1, detail="starting", result="", field=None, t0=_now())
    _thread = threading.Thread(target=_run, daemon=True, name="flightplan")
    _thread.start()
    return (f"Flying the plan: {len(steps)} steps ({' | '.join(fmt(s) for s in steps)}). Progress shows in the Flight "
            "Plan panel and here. Stop = stop after this step; Abort = everything off.")


def _say(text):
    log.info("flightplan: %s", text)
    science.post_event(text)


def _stopped():
    if _stop.is_set():
        return True
    try:
        return float(settings.get("autopilot_stop") or 0) > _state["t0"]
    except Exception:  # noqa: BLE001
        return False


def _run():
    steps = _state["steps"]
    try:
        for i, st in enumerate(steps):
            if _stopped():
                _state["result"] = f"stopped before step {i + 1} ({fmt(st)})."
                break
            _state.update(i=i, detail="starting")
            _say(f"Flight plan step {i + 1}/{len(steps)}: {fmt(st)}")
            try:
                ok, msg = _exec(st, steps[i + 1:])
            except Exception as e:  # noqa: BLE001
                log.exception("flightplan step")
                ok, msg = False, f"{e.__class__.__name__}: {e}"
            if ok is None:
                _state["result"] = f"stopped at step {i + 1} ({fmt(st)})."
                break
            if not ok:
                _state["result"] = f"step {i + 1} ({fmt(st)}) failed: {msg}"
                break
            if msg:
                _say(f"Plan step {i + 1} done: {msg}")
        else:
            _state["result"] = f"complete ({len(steps)} steps)."
    finally:
        _state["running"] = False
        _say("Flight plan " + _state["result"])


# ---- helpers (module-level so tests can replace them)
def _tool(name, **args):
    from . import ksp_actions
    return ksp_actions.call_tool(name, args)


def _vinfo():
    from . import ksp_actions
    with ksp_actions._lock:
        v = ksp_actions._vessel()
        body = v.orbit.body
        f = v.flight(body.reference_frame)
        return {"sit": str(v.situation).split(".")[-1], "plane": ksp_actions._planeish(v), "lat": f.latitude,
                "lon": f.longitude, "body": body.name, "R": body.equatorial_radius, "spd": f.speed}


def _mj_enabled(what):
    from . import ksp_actions
    with ksp_actions._lock:
        return bool(getattr(ksp_actions.conn().mech_jeb, what).enabled)


def _lander_active():
    from . import lander
    return lander.active()


def _nearest_field(info, max_m=80_000):
    best = None
    for name, sp in _known_spots().items():
        if sp.get("mode") != "H":
            continue
        st = spots.make_strip(sp)
        if not st or st.get("body", "Kerbin") != info.get("body", "Kerbin"):
            continue
        lat, lon = spots.spot_point(sp)
        d = spots.gc_dist(info["lat"], info["lon"], lat, lon, info.get("R", spots.R_DEFAULT))
        if best is None or d < best[0]:
            best = (d, "KSC" if sp.get("strip") == "KSC" else name)
    return best[1] if best and best[0] < max_m else None


def _field_point(name):
    """(lat, lon) center of a runway: 'KSC' / spot name / 'field' (departure)."""
    n = spots._norm(name)
    if n in FIELD_WORDS:
        name = _state.get("field") or "KSC"
        n = spots._norm(name)
    if n in KSC_WORDS:
        a, b = plane.STRIPS["KSC"]["a"], plane.STRIPS["KSC"]["b"]
        return (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, "KSC"
    k, sp = spots.find_spot(name)
    if not sp:
        return None
    lat, lon = spots.spot_point(sp)
    return lat, lon, ("KSC" if sp.get("strip") == "KSC" else k)


def _wait(cond, timeout, detail=None, every=0.5):
    """True when cond() holds, False on timeout, None when stopped/aborted."""
    end = _now() + timeout
    while _now() < end:
        if _stopped():
            return None
        try:
            if cond():
                return True
        except Exception:  # noqa: BLE001
            pass
        if detail:
            try:
                _state["detail"] = detail()
            except Exception:  # noqa: BLE001
                pass
        _sleep(every)
    return False


def _wrap(a):
    return (a + 180.0) % 360.0 - 180.0


def _hs(k, d=None):
    return hold.STATUS.get(k, d)


OVERRIDE_WAIT_S = 120.0  # s to wait for Luke's yes/no when a step asks for more than the speed cap / ceiling


def _gated_tool(name, **args):
    """A tool call that may hit the speed cap / safe ceiling: on 'needs_override' ask in chat and wait for Luke's
    yes/no. Yes -> the limit is raised (speedcap.grant) and the call repeated; no / no answer -> repeated at the
    speed / altitude the limit allows. Returns the tool reply, or None if the plan was stopped while waiting."""
    with speedcap.use_session(origin="flightplan"):
        r = _tool(name, **args)
    if not str(r).startswith("needs_override"):
        return r
    req = speedcap.pending()
    _say("Flight plan: " + str(r)[len("needs_override: "):].split(" [")[0] + " (answer yes/no in chat)")
    res = _wait(lambda: speedcap.pending() is None, OVERRIDE_WAIT_S, lambda: "waiting for override authority (yes/no)")
    if res is None:
        speedcap.drop()
        return None
    if not (req and req.get("granted")):
        speedcap.drop()
        if req:
            key = req["key"]
            args = dict(args, **{key: req["fallback"]})
            _say(f"Flight plan: keeping the {'speed cap' if req['kind'] == 'speed' else 'safe ceiling'} - "
                 f"flying {key.replace('_', ' ')} {args[key]:.0f} instead.")
    with speedcap.use_session(origin="flightplan"):
        return _tool(name, **args)


def _ensure_hold(**kw):
    """Holds on (and update targets). Returns (ok, msg); ok None = stopped while waiting for override authority."""
    args = {k: v for k, v in kw.items() if v is not None}
    r = _gated_tool("plane_hold", **args)
    if r is None:
        return None, ""
    if not hold.active():
        _wait(hold.active, 3.0)
    return (True, r) if hold.active() else (False, r)


def _release_hold():
    if hold.active():
        _tool("plane_hold", engage=False)
        _wait(lambda: not hold.active(), 15.0)
    _wait(lambda: guard.busy() is None, 6.0)


# ---- steps
def _exec(st, rest):
    op = st["op"]
    fn = globals().get("_do_" + op)
    if fn is None:
        return False, f"step '{op}' isn't runnable yet"
    return fn(st, rest)


def _do_takeoff(st, rest):
    info = _vinfo()
    if info["sit"] == "flying":
        _state["field"] = _state.get("field") or _nearest_field(info)
        return True, "already airborne - skipped"
    if info["sit"] not in ("landed", "pre_launch"):
        return False, f"can't take off from here ({info['sit']})"
    if not info["plane"]:
        return False, "takeoff is for planes (wings + wheels); rockets use 'ascent 80 km'"
    _state["field"] = _nearest_field(info)
    nxt = next((s for s in rest if s["op"] in ("climb", "cruise", "circle")), None)
    alt = (nxt or {}).get("alt") or 300.0
    ref = (nxt or {}).get("ref", "agl")
    vs = (nxt or {}).get("vs")
    if not hold.active():
        settings.put(hold.KEY, {"t": _now()})  # fresh targets: no stale roll/heading/speed from an earlier flight
    ok, r = _ensure_hold(altitude_m=alt, altitude_ref=ref, vertical_speed=vs)
    if not ok:
        return ok, r
    res = _wait(lambda: _hs("phase") == "hold" or not hold.active(), 240.0,
                lambda: f"takeoff roll, {_hs('speed', 0)} m/s, radar {_hs('hr', 0)} m")
    if res is None:
        return None, ""
    if not hold.active():
        return False, _hs("result") or r
    if not res:
        return False, "not airborne after 4 min"
    return True, f"airborne from {_state['field'] or 'the runway'}"


def _alt_key(ref):
    return "alt_msl" if ref == "msl" else "alt_agl"


def _do_climb(st, rest):
    ok, r = _ensure_hold(altitude_m=st["alt"], altitude_ref=st.get("ref", "agl"),
                         vertical_speed=st.get("vs"), speed=st.get("spd"))
    if not ok:
        return ok, r
    key = _alt_key(st.get("ref", "agl"))
    cur = _hs(key)
    dh = abs(st["alt"] - (cur if cur is not None else 0))
    rate = max(5.0, 0.5 * (st.get("vs") or 15.0))
    res = _wait(lambda: _hs(key) is not None and abs(_hs(key) - st["alt"]) < 75, 90.0 + dh / rate,
                lambda: f"{_hs(key)} m -> {_g(st['alt'])} m {st.get('ref', 'agl')}, V/S {_hs('vs')}, {_hs('speed')} m/s")
    if res is None:
        return None, ""
    if not hold.active():
        return False, _hs("result") or "the holds disengaged"
    return True, (f"at {_hs(key)} m" if res else f"still at {_hs(key)} m (timed out; the hold keeps going)")


def _turn_to(hdg):
    _tool("plane_hold", heading=hdg)
    return _wait(lambda: _hs("hdg_err") is not None and abs(_hs("hdg_err")) < 5 and _hs("hdg_des") is not None
                 and abs(_wrap(_hs("hdg_des") - hdg)) < 1, 150.0,
                 lambda: f"turning to {hdg:03.0f}, heading {_hs('hdg')} (err {_hs('hdg_err')})")


def _do_cruise(st, rest):
    ok, r = _ensure_hold(altitude_m=st.get("alt"), altitude_ref=st.get("ref") if st.get("alt") is not None else None,
                         speed=st.get("spd"))
    if not ok:
        return ok, r
    if st.get("hdg") is not None:
        if _turn_to(st["hdg"]) is None:
            return None, ""
    if st.get("dist"):
        flown, last = [0.0], [_now()]

        def far():
            t = _now()
            flown[0] += float(_hs("speed") or 0) * (t - last[0])
            last[0] = t
            return flown[0] >= st["dist"]
        res = _wait(far, 6 * 3600.0, lambda: f"cruise {flown[0] / 1000:.1f}/{st['dist'] / 1000:g} km, {_hs('speed')} m/s")
    else:
        t_end = _now() + st.get("secs", DEFAULT_CRUISE_S)
        res = _wait(lambda: _now() >= t_end, st.get("secs", DEFAULT_CRUISE_S) + 5,
                    lambda: f"cruise {max(0, t_end - _now()):.0f} s left, alt {_hs('alt_agl')} m AGL, {_hs('speed')} m/s")
    if res is None:
        return None, ""
    if not hold.active():
        return False, _hs("result") or "the holds disengaged"
    return True, ""


def _turn_radius(spd, bank):
    return spd * spd / (9.81 * math.tan(math.radians(bank)))


def _do_circle(st, rest):
    ok, r = _ensure_hold(altitude_m=st.get("alt"), altitude_ref=st.get("ref") if st.get("alt") is not None else None,
                         speed=st.get("spd"))
    if not ok:
        return ok, r
    sign = -1.0 if st.get("dir") == "left" else 1.0
    laps = int(st.get("laps", 1))
    _sleep(1.0)  # let the hold publish a fresh heading (STATUS may be from an earlier hold)
    entry = float(_hs("hdg") or 0.0)
    if not st.get("around"):  # circling turns where we are: a roll hold, laps counted from the heading change
        bank = min(float(st.get("bank", 15.0)), speedcap.bank_limit(float(_hs("speed") or 0)))
        _tool("plane_hold", roll=sign * bank)
        acc, prev = [0.0], [entry]

        def done():
            h = float(_hs("hdg") or prev[0])
            acc[0] += sign * _wrap(h - prev[0])
            prev[0] = h
            return acc[0] >= 360.0 * laps - 12.0
        res = _wait(done, laps * 1200.0 + 60.0,
                    lambda: f"lap {min(laps, int(max(acc[0], 0) // 360) + 1)}/{laps}, turned {max(acc[0], 0):.0f} deg, "
                            f"bank {_hs('bank')}, alt {_hs('alt_agl')} m AGL")
        if res is None:
            _tool("plane_hold", heading=entry)
            return None, ""
        _turn_to(entry)  # roll out on the entry heading (also clears the roll hold)
        return True, f"{laps} lap(s) done" if res else "laps timed out - rolled out"
    # laps around a runway: steer the tangent of a circle (radius from speed + bank cap), count bearing laps
    c = _field_point(st["around"])
    if not c:
        return False, f"unknown place to circle: '{st['around']}' (known: {', '.join(_runway_names())})"
    clat, clon, cname = c
    R_body = spots.R_DEFAULT
    spd = max(float(st.get("spd") or 0), float(_hs("speed") or 0), 80.0)
    cap = min(speedcap.bank_limit(spd), float(st.get("bank", 20.0)))
    radius = max(float(st.get("radius") or 0), 1.6 * _turn_radius(spd, cap), 2500.0)
    acc, prev, on = [0.0], [None], [False]

    def pos():
        lat, lon = _hs("lat"), _hs("lon")
        if lat is None:
            i = _vinfo()
            lat, lon = i["lat"], i["lon"]
        return lat, lon

    def step():
        lat, lon = pos()
        d = spots.gc_dist(lat, lon, clat, clon, R_body)
        brg_pc = spots.bearing(lat, lon, clat, clon)
        corr = max(-45.0, min(90.0, (d - radius) / radius * 180.0))
        hold.set_targets(heading=(brg_pc - sign * 90.0 + sign * corr) % 360.0, roll="off")
        b = spots.bearing(clat, clon, lat, lon)
        if not on[0] and abs(d - radius) < 0.35 * radius:
            on[0], prev[0] = True, b
        if on[0]:
            acc[0] += sign * _wrap(b - prev[0])
            prev[0] = b
        _state["detail"] = (f"lap {min(laps, int(max(acc[0], 0) // 360) + 1)}/{laps} around {cname} "
                            f"(r {radius / 1000:.1f} km, {d / 1000:.1f} km out), {_hs('speed')} m/s, alt {_hs('alt_agl')} m AGL"
                            if on[0] else f"heading to the {radius / 1000:.1f} km circle around {cname}: {d / 1000:.1f} km out")
        return acc[0] >= 360.0 * laps
    lap_s = 2 * math.pi * radius / spd
    res = _wait(step, laps * lap_s * 2.5 + 1800.0, None, every=1.0)
    if res is None:
        return None, ""
    return True, (f"{laps} lap(s) around {cname} done (radius {radius / 1000:.1f} km)" if res
                  else "laps timed out - continuing")


def _land_route(target):
    """('ksc', runway) or ('spot', name) or (None, error)."""
    name = target or _state.get("field") or ""
    if not name:
        try:
            name = _nearest_field(_vinfo(), 600_000) or "KSC"
        except Exception:  # noqa: BLE001
            name = "KSC"
    n = spots._norm(name)
    if n in FIELD_WORDS:
        name, n = _state.get("field") or "KSC", spots._norm(_state.get("field") or "KSC")
    if n in KSC_WORDS:
        return "ksc", ""
    k, sp = spots.find_spot(name)
    if sp and sp.get("strip") == "KSC":
        return "ksc", sp.get("runway", "")
    if sp and sp.get("mode") == "H":
        return "spot", k
    return None, f"'{name}' isn't a runway I know ({', '.join(_runway_names())})"


def _do_land(st, rest):
    info = _vinfo()
    if not info["plane"]:
        return _rocket_land(st)
    if info["sit"] in ("landed", "pre_launch", "splashed") and not hold.active():
        return True, f"already on the ground ({info['sit']})"
    kind, which = _land_route(st.get("target"))
    if kind is None:
        return False, which
    if st.get("tg") and kind != "ksc":
        return False, "touch-and-goes only work on the KSC runway (land Runway 09 tg 2)"
    _release_hold()  # hand the craft from the holds to the autoland
    if kind == "ksc":
        r = _tool("land_plane", runway=which, touch_and_go=int(st.get("tg") or 0))
    else:
        r = _tool("land_at_spot", name=which, mode="H")
    if not _wait(plane.active, 5.0):
        return False, r
    res = _wait(lambda: not plane.active(), 4 * 3600.0,
                lambda: f"autoland {plane.STATUS.get('phase')}, runway {plane.STATUS.get('runway', '?')}, "
                        f"{plane.STATUS.get('speed', '?')} m/s, h {plane.STATUS.get('h', '?')} m")
    if res is None:
        return None, ""
    return True, str(plane.STATUS.get("result") or "landed")


def _rocket_land(st):
    t = spots._norm(st.get("target") or "")
    if t in ("", "here"):
        r = _tool("land_here")
    elif t in KSC_WORDS or t == "ksc pad":
        r = _tool("land_at_ksc")
    else:
        r = _tool("land_at_spot", name=st["target"], mode="V")
    if not _wait(_lander_active, 5.0):
        return False, r
    res = _wait(lambda: not _lander_active(), 3 * 3600.0, lambda: "landing controller flying")
    if res is None:
        return None, ""
    from . import lander
    return True, str(lander.STATUS.get("result") or "landed")


def _do_flyto(st, rest):
    _release_hold()
    args = {"name": st["target"]}
    if st.get("alt"):
        args["cruise_altitude_m"] = st["alt"]
    if st.get("spd"):
        args["cruise_speed"] = st["spd"]
    r = _gated_tool("fly_to", **args)
    if r is None:
        return None, ""
    if not _wait(plane.active, 5.0):
        return False, r
    res = _wait(lambda: not plane.active(), 6 * 3600.0,
                lambda: f"fly_to {plane.STATUS.get('phase')}, {plane.STATUS.get('speed', '?')} m/s")
    if res is None:
        return None, ""
    return True, str(plane.STATUS.get("result") or "arrived")


def _do_wait(st, rest):
    t_end = _now() + st["secs"]
    res = _wait(lambda: _now() >= t_end, st["secs"] + 5, lambda: f"waiting {max(0, t_end - _now()):.0f} s")
    return (None, "") if res is None else (True, "")


def _mj_step(r, ok_words, what, timeout):
    if not any(w in r for w in ok_words):
        return False, r
    _sleep(2.0)
    res = _wait(lambda: not _mj_enabled(what), timeout, lambda: f"MechJeb {what.replace('_', ' ')} running", every=2.0)
    if res is None:
        return None, ""
    return True, r if res else f"{r} (still running after {timeout / 60:.0f} min - continuing)"


def _do_ascent(st, rest):
    r = _tool("mechjeb_ascent", target_altitude_km=st["km"], inclination_deg=st.get("inc", 0.0))
    ok, msg = _mj_step(r, ("MechJeb ascent engaged",), "ascent_autopilot", 30 * 60.0)
    if ok and _vinfo()["sit"] != "orbiting":
        return False, f"ascent ended but we're {_vinfo()['sit']}, not orbiting"
    return ok, msg


def _do_circularize(st, rest):
    return _mj_step(_tool("circularize"), ("node executor is flying",), "node_executor", 60 * 60.0)


def _do_transfer(st, rest):
    return _mj_step(_tool("transfer_to", body=st["body"]), ("planned",), "node_executor", 6 * 3600.0)


def _do_correction(st, rest):
    return _mj_step(_tool("course_correction", periapsis_km=st["pe"]), ("node executor",), "node_executor", 6 * 3600.0)


def _do_deorbit(st, rest):
    return _mj_step(_tool("deorbit_burn", periapsis_km=st["pe"]), ("node executor",), "node_executor", 6 * 3600.0)


def _do_warp(st, rest):
    r = _tool("warp_to_soi_change" if st["what"] == "soi" else "warp_to_apoapsis")
    return (not any(w in r for w in ("No SOI", "Refusing", "failed"))), r


def _do_override(st, rest):
    """'override bank 50 *' asks Luke yes/no in chat (no / no answer = defaults kept, the plan goes on); '***' and
    'override off' apply directly."""
    args = {"kind": st["kind"], "value": st["value"], "authority": st.get("auth", "ask")}
    with speedcap.use_session(origin="flightplan"):
        r = str(_tool("set_override", **args))
    if not r.startswith("needs_confirm"):
        return True, r
    req = speedcap.pending()
    _say("Flight plan: " + r[len("needs_confirm: "):].split(" [")[0] + " (answer yes/no in chat)")
    res = _wait(lambda: speedcap.pending() is None, OVERRIDE_WAIT_S, lambda: "waiting for override authority (yes/no)")
    if res is None:
        speedcap.drop()
        return None, ""
    if req and req.get("granted"):
        return True, f"override {st['kind']} {_g(st['value'])} granted"
    speedcap.drop()
    return True, f"override {st['kind']} not granted - default limits kept"


def _do_authall(st, rest):
    return True, _tool("authorise_all")


def _do_chutes(st, rest):
    r = _tool("deploy_parachutes")
    return ("parachute(s)" in r and not r.startswith("No")), r


def _do_science(st, rest):
    return True, _tool("run_science", transmit=bool(st.get("transmit", True)))


def _do_stage(st, rest):
    r = _tool("stage")
    return r.startswith("Staged"), r


_NODE_OK = ("node executor is flying",)


def _do_setap(st, rest):
    return _mj_step(_tool("change_apoapsis", altitude_km=st["km"]), _NODE_OK, "node_executor", 6 * 3600.0)


def _do_setpe(st, rest):
    return _mj_step(_tool("change_periapsis", altitude_km=st["km"]), _NODE_OK, "node_executor", 6 * 3600.0)


def _do_inclination(st, rest):
    return _mj_step(_tool("change_inclination", inclination_deg=st["deg"]), _NODE_OK, "node_executor", 6 * 3600.0)


def _do_lonap(st, rest):
    return _mj_step(_tool("apsis_longitude", longitude_deg=st["lon"]), _NODE_OK, "node_executor", 6 * 3600.0)


def _do_matchplane(st, rest):
    return _mj_step(_tool("match_target_plane"), _NODE_OK, "node_executor", 6 * 3600.0)


def _do_stationkeep(st, rest):
    """Synchronous orbit over a longitude: raise Ap to sync altitude, put Ap over the longitude, circularize."""
    import json as _json
    sync = _json.loads(_tool("sync_orbit_altitude"))
    lon = st.get("lon")
    if lon is None:
        lon = -74.5577  # KSC
    for sub in ({"op": "setap", "km": sync["sync_altitude_km"]}, {"op": "lonap", "lon": lon}, {"op": "circularize"}):
        _state["detail"] = fmt(sub)
        ok, msg = _exec(sub, [])
        if not ok:
            return ok, msg
    return True, f"synchronous orbit {sync['sync_altitude_km']} km over longitude {lon:g}"


def _do_taxi(st, rest):
    from . import taxi as _taxi
    r = _tool("taxi_to", name=st["target"], speed=st.get("spd") or 8)
    if not _wait(_taxi.active, 5.0):
        return False, r
    res = _wait(lambda: not _taxi.active(), 3 * 3600.0, lambda: _taxi.summary() or "taxiing")
    if res is None:
        return None, ""
    out = str(_taxi.STATUS.get("result") or "")
    return ("arrived" in out), out
