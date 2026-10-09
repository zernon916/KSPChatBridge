"""Captain's orders: short direct flight commands parsed BEFORE the chat model (so they work with tiny local models).

parse(text) -> (tool name, args) or None. The tools live in ksp_actions (most are menu-only so the model's tool list
stays small); the model reaches all of them through one tool, captain_order(order), which runs the same parser.
Anything that doesn't match exactly goes to the model as usual.
"""
import difflib
import re

PITCH_DEFAULT = 5.0
TURN_DEFAULT = 90.0
_END = r"\s*[.!]*\s*$"
_DEG = r"\s*(?:deg(?:rees?)?|\u00b0)?"
_ON = {"on": True, "off": False, "up": False, "down": True}

_PITCH_RE = re.compile(r"^\s*/?(?:pitch|nose)\s*(up|down)\s*(?:to\s*)?(\d+(?:\.\d+)?)?" + _DEG + _END, re.I)
_THROTTLE_RE = re.compile(r"^\s*throttle\s*(?:to\s*)?(?:(\d+(?:\.\d+)?)\s*(?:%|percent)?|(max|full|maximum)|"
                          r"(idle|min|minimum|zero|off|cut)|(auto|autopilot))" + _END, re.I)
_GEAR_RE = re.compile(r"^\s*(?:gear|wheels)\s*(up|down)" + _END, re.I)
_BRAKES_RE = re.compile(r"^\s*(?:air\s*brakes?|brakes?)\s*(on|off)" + _END, re.I)
_CHUTES_RE = re.compile(r"^\s*(?:deploy\s*(?:the\s*)?)?(?:para)?chutes?" + _END, re.I)
_SPEED_RE = re.compile(r"^\s*(?:set\s*)?speed\s*(?:to\s*)?(\d+(?:\.\d+)?)\s*(?:m/?s)?" + _END, re.I)
_EJECT_RE = re.compile(r"^(?:(?P<pre>confirm(?:ed)?|now)\s+)?(?:eject\s+)?eject(?:\s+(?:the\s+)?(?:kerbal|pilot|crew|him|her))?"
                       r"(?:\s+(?P<post>confirm(?:ed)?|now))?$", re.I)
_DEORBIT_RE = re.compile(r"^\s*(?:(?:do\s+)?(?:a\s+|the\s+)?de-?orbit(?:\s+burn)?|burn\s+to\s+de-?orbit)(?:\s+now)?" + _END, re.I)
# batch 1: navigation / reports / switches
_TURN_RE = re.compile(r"^\s*turn\s*(left|right)(?:\s*(?:by\s*)?(\d+(?:\.\d+)?))?" + _DEG + _END, re.I)
_HEADING_RE = re.compile(r"^\s*(?:set\s*|fly\s*|hold\s*|new\s*)?heading\s*(?:to\s*)?(\d{1,3}(?:\.\d+)?)" + _DEG + _END, re.I)
_FLYTO_RE = re.compile(r"^\s*(?:fly|go|head)\s+to\s+(?:the\s+)?([a-z][\w' .-]{1,40}?)" + _END, re.I)
_CIRCLE_RE = re.compile(r"^\s*(?:circle|orbit|loiter)(?:\s*here)?(?:\s*(left|right))?" + _END, re.I)
_RTB_RE = re.compile(r"^\s*(?:return\s*to\s*base|rtb|go\s*home|return\s*home|head\s*home)" + _END, re.I)
_REPORT_RE = re.compile(r"^\s*(?:report|status\s*report|sitrep|flight\s*report)" + _END, re.I)
_FUEL_RE = re.compile(r"^\s*(?:fuel\s*check|fuel\s*status|fuel\s*report|check\s*fuel|how\s*much\s*fuel"
                      r"(?:\s*(?:do\s*we\s*have|is\s*left|left))?)\s*\??" + _END, re.I)
_HOWFAR_RE = re.compile(r"^\s*how\s*far\s*(?:is\s*it\s*)?(?:to|from)\s+(?:the\s+)?([a-z][\w' .-]{1,40}?)\s*\??" + _END, re.I)
_TTT_RE = re.compile(r"^\s*(?:time\s*to\s*(?:target|touchdown|landing|destination)|eta)\s*\??" + _END, re.I)
_LEVEL_RE = re.compile(r"^\s*(?:level\s*off|level\s*out|wings\s*level|level\s*(?:the\s*)?wings)" + _END, re.I)
_STAGE_RE = re.compile(r"^\s*(?:stage|next\s*stage)" + _END, re.I)
_SAS_RE = re.compile(r"^\s*sas\s*(on|off)" + _END, re.I)
_SASMODE_RE = re.compile(r"^\s*(?:hold|sas)\s*(prograde|retrograde)" + _END, re.I)
_RCS_RE = re.compile(r"^\s*rcs\s*(on|off)" + _END, re.I)
_LIGHTS_RE = re.compile(r"^\s*lights?\s*(on|off)" + _END, re.I)
_AG_RE = re.compile(r"^\s*(?:ag|action\s*group)\s*(\d{1,2})(?:\s*(on|off))?" + _END, re.I)
# batch 2: engines / flaps / trim / landing / abort / crew
_ENGINES_RE = re.compile(r"^\s*engines?\s*(on|off)" + _END, re.I)
_CUT_RE = re.compile(r"^\s*(?:cut|kill|shut\s*down)\s*(?:the\s*|all\s*)?engines?" + _END, re.I)
_MODE_RE = re.compile(r"^\s*(?:switch|toggle|change)\s*(?:the\s*)?engines?\s*modes?" + _END, re.I)
_AB_RE = re.compile(r"^\s*after\s*burners?\s*(on|off)" + _END, re.I)
_FLAPS_RE = re.compile(r"^\s*flaps?\s*(1|2|one|two|up|down|full|0|off|retract(?:ed)?)" + _END, re.I)
_TRIM_RE = re.compile(r"^\s*trim\s*(?:nose\s*)?(up|down)(?:\s*(\d+(?:\.\d+)?))?\s*(?:%|percent)?" + _END, re.I)
_TRIM0_RE = re.compile(r"^\s*(?:trim\s*(?:reset|zero|0|off|neutral)|reset\s*trim)" + _END, re.I)
_LAND_RE = re.compile(r"^\s*land(?:\s+(?:the\s+)?(?:plane|aircraft|craft|ship))?"
                      r"(?:\s+(?:at|on)\s+(?:the\s+)?(ksc|base|home|runway|island(?:\s+airfield)?))?" + _END, re.I)
_LAND_RWY_RE = re.compile(r"^\s*land\s+(?:at|on)\s+(?:the\s+)?(?:runway\s*|rwy\s*)?(0?9|27)"
                          r"(?:\s+(?:at\s+)?(?:the\s+)?ksc)?" + _END, re.I)
_STOP_CUR_RE = re.compile(r"^\s*(?:please\s+)?(?:stop|cancel|abort|end|scrap|drop)\s+(?:the\s+|that\s+|this\s+|my\s+|our\s+)?"
                          r"(?:current(?:\s+(?:flight\s*plan|plan|autopilot|task|order|maneuver|manoeuvre))?|flight\s*plan|plan"
                          r"|what\s+(?:you'?re|we'?re)\s+doing)\b(.*)$", re.I)


def split_stop(text):
    """'stop current plan' -> (True, ''); 'stop current, and land at 27' -> (True, 'land at 27'); else (False, text).
    Run before the model so even small models can't skip the cancel."""
    m = _STOP_CUR_RE.match(text or "")
    if not m:
        return False, text
    rest = re.sub(r"^(?:\s|[,;:.!]|and\b|then\b)+", "", m.group(1), flags=re.I).strip()
    if rest and not re.match(r"^[,;:.!]|\s*(?:and|then)\b", m.group(1), re.I):
        return False, text   # 'stop current plan X' without a separator: not ours
    return True, rest.rstrip(" .!")


_GOAROUND_RE = re.compile(r"^\s*go[\s-]*around" + _END, re.I)
_TNG_RE = re.compile(r"^\s*(?:do\s*a\s*)?touch[\s-]*(?:and|n|&)[\s-]*go" + _END, re.I)
_PROP_PITCH_RE = re.compile(r"^\s*(?:props?|propellers?|blades?)\s*pitch\s*(?:to\s*)?(auto|-?\d+(?:\.\d+)?)\s*"
                            r"(?:deg(?:rees?)?|\u00b0)?" + _END, re.I)
_RPM_RE = re.compile(r"^\s*(?:set\s*)?rpm\s*(?:to\s*|limit\s*)?(max|maximum|full|\d+(?:\.\d+)?)" + _END, re.I)
_TORQUE_RE = re.compile(r"^\s*(?:set\s*)?torque\s*(?:to\s*|limit\s*)?(max|maximum|full|auto|\d+(?:\.\d+)?)\s*(?:%|percent)?"
                        + _END, re.I)
_PROP_ONOFF_RE = re.compile(r"^\s*(?:props?|propellers?|motors?|rotors?)\s*(on|off)" + _END, re.I)
_PROP_REV_RE = re.compile(r"^\s*(?:props?|propellers?)\s*(reverse|forward)" + _END, re.I)
_HOVER_RE = re.compile(r"^\s*hover(?:\s+(?:at\s+)?(\d+(?:\.\d+)?)\s*(?:m|meters?|metres?)?(?:\s*agl)?)?"
                       r"(?:\s+facing\s+(\d{1,3}(?:\.\d+)?))?" + _END, re.I)
_FACE_RE = re.compile(r"^\s*(?:face|nose\s+to|point\s+(?:the\s+nose\s+)?(?:to\s+)?)\s*(\d{1,3}(?:\.\d+)?)" + _DEG + _END, re.I)
_FLY_TRK_RE = re.compile(r"^\s*fly\s+(\d{1,3}(?:\.\d+)?)(?:\s*(?:,|and)?\s*(?:at\s+)?(?:speed\s+)?(\d+(?:\.\d+)?)\s*(?:m/?s)?)?"
                         + _END, re.I)
_SIDESTEP_RE = re.compile(r"^\s*side\s*-?\s*step\s+(left|right)(?:\s+(\d+(?:\.\d+)?)\s*(?:m|meters?|metres?)?)?" + _END, re.I)
_BACKUP_RE = re.compile(r"^\s*(?:back\s*up|(?:fly|go|move)\s+back(?:wards?)?)(?:\s+(\d+(?:\.\d+)?)\s*(?:m|meters?|metres?)?)?"
                        + _END, re.I)
_HOLDPOS_RE = re.compile(r"^\s*(?:hold\s+(?:position|here|this\s+spot)|station\s*keep)" + _END, re.I)
_DESCEND_BARE_RE = re.compile(r"^\s*(?:descend|go\s+down|come\s+down)" + _END, re.I)
_SMALL_ALT_RE = re.compile(r"^\s*(climb|descend|go\s+up|go\s+down)\s+(?:to\s+)?(\d+(?:\.\d+)?)(?:\s*agl)?" + _END, re.I)
_HDG_SPD_RE = re.compile(r"^\s*(?:fly\s+)?heading\s+(\d{1,3}(?:\.\d+)?)\s*(?:,|and)?\s*(?:at\s+)?(?:speed\s+)?"
                         r"(\d+(?:\.\d+)?)\s*(?:m/?s)?" + _END, re.I)
_TAKEOFF_RE = re.compile(r"^\s*(?:take[\s-]?off|depart)(?:\s+now)?" + _END, re.I)
_DAMAGE_RE = re.compile(r"^\s*(?:damage\s*(?:report|check|status)|any\s*damage|are\s*we\s*damaged"
                        r"|what(?:'s|\s*is)\s*missing|what\s*(?:are|did)\s*we\s*(?:missing|lose|lost)"
                        r"|what\s*did\s*we\s*lose|parts?\s*(?:check|report|lost))\s*\??" + _END, re.I)
_ABORT_RE = re.compile(r"^\s*abort" + _END, re.I)
_ABORT_AP_RE = re.compile(r"^\s*(?:abort|stop)\s+(?:the\s+|all\s+)?autopilots?" + _END, re.I)
_CREW_RE = re.compile(r"^\s*(?:crew\s*(?:report|status|check|list)|who'?s\s*(?:aboard|on\s*board)\s*\??)" + _END, re.I)
# limit overrides ('*' / unmarked = ask yes/no, '***' = pre-authorized), 'authorise all'
_OVR_MAX_RE = re.compile(r"^\s*override\s+speed\s+(?:to\s+)?(?:max(?:imum)?|full)\s*(\*{1,3})?" + _END, re.I)
_OVR_RE = re.compile(r"^\s*override\s+(bank|pitch|speed|altitude|alt)\s+(?:to\s+)?(\d+(?:\.\d+)?)\s*"
                     r"(km|m/?s|m|deg(?:rees?)?|\u00b0)?\s*(\*{1,3})?" + _END, re.I)
_OVR_OFF_RE = re.compile(r"^\s*override(?:\s+(bank|pitch|speed|altitude|alt|all))?\s+off" + _END, re.I)
_AUTH_ALL_RE = re.compile(r"^\s*authori[sz]e\s+all" + _END, re.I)
# altitude orders: 'descend to 5km', 'drop to 5k', 'climb to 8000', 'altitude 3000 agl', 'NOW MAX DOWN TO KSP ALTITUDE 5km'
_ALT_URGENT = re.compile(r"\b(?:max(?:imum)?|now|asap|fast|quick(?:ly)?|immediately|hurry|expedite|right\s+now)\b", re.I)
_ALT_FILLER = re.compile(r"\b(?:now|max(?:imum)?|asap|fast|quick(?:ly)?|immediately|hurry|expedite|right|please|pls|"
                         r"captain|ok(?:ay)?|ksp|kerbin|the|an?|us|it|her|of)\b|[,!]", re.I)
_ALT_RE = re.compile(r"^\s*(?P<verb>descend|drop|climb|go\s+down|go\s+up|come\s+down|get\s+down|head\s+down|take\s+down|"
                     r"take\s+up|down|up|set\s+(?:altitude|alt)|altitude|alt)\s*(?:to\s+)?(?:altitude\s+|alt\s+)?"
                     r"(?P<n>\d+(?:\.\d+)?)\s*(?P<u>km|k|m|meters?|metres?)?\s*(?P<ref>agl|msl|asl)?\s*[.]*\s*$", re.I)
_PLACE_STOP = re.compile(r"\d|\b(?:at|and|then|with|speed|altitude|km|land(?:ing)?)\b", re.I)  # complex: leave to the model


# typo tolerance: command keywords (>= 6 letters) a word may be fuzzily corrected to ('overide' -> 'override')
_KEYWORDS = ("override", "authorise", "authorize", "throttle", "altitude", "descend", "heading", "report", "circle",
             "engines", "afterburner", "airbrake", "brakes", "parachutes", "chutes", "eject", "flaps", "climb", "speed",
             "level", "abort", "turn", "trim", "gear", "wheels", "action", "group", "stage", "lights", "pitch",
             "deorbit")
_FIX_RE = re.compile(r"\b(?:over\s+ride|over-ride)\b", re.I)


def fix_typos(text):
    """'Overide speed to 325' -> 'override speed to 325': words of 6+ letters close to a command keyword (difflib
    ratio >= 0.8) are replaced by it; everything else is left exactly as typed."""
    t = _FIX_RE.sub("override", text or "")

    def one(m):
        w = m.group(0)
        lw = w.lower()
        if len(lw) < 6 or lw in _KEYWORDS or any(k in lw for k in _KEYWORDS):  # "pitchup" stays "pitchup"
            return w
        hit = difflib.get_close_matches(lw, [k for k in _KEYWORDS if len(k) >= 5 and abs(len(k) - len(lw)) <= 2], n=1, cutoff=0.8)
        return hit[0] if hit else w
    return re.sub(r"[A-Za-z]+", one, t)


def parse_pitch(text):
    """'pitch up 10' / 'pitchup 90' / 'nose down 5' / 'pitch up' (= 5) -> (direction, degrees), else None."""
    m = _PITCH_RE.match(text or "")
    if not m:
        return None
    return m.group(1).lower(), float(m.group(2)) if m.group(2) else PITCH_DEFAULT


def parse_altitude(text):
    """'descend to 5km' / 'drop to 5k' / 'climb to 8000' / 'altitude 3000 agl' / 'MAX DOWN TO KSP ALTITUDE 5km' ->
    set_altitude args, else None. A bare number under 100 has no unit we can trust -> None (left to the model)."""
    t = (text or "").strip()
    urgent = bool(_ALT_URGENT.search(t))
    m = _ALT_RE.match(" ".join(_ALT_FILLER.sub(" ", t).split()))
    if not m:
        return None
    n, u = float(m.group("n")), (m.group("u") or "").lower()
    if u in ("km", "k"):
        n *= 1000.0
    elif not u and n < 100:
        return None
    verb = m.group("verb").lower()
    d = ("down" if any(w in verb for w in ("descend", "drop", "down")) else
         "up" if any(w in verb for w in ("climb", "up")) else "")
    ref = (m.group("ref") or "msl").lower()
    return {"altitude_m": n, "direction": d, "ref": "agl" if ref == "agl" else "msl", "urgent": urgent}


_GROUP_RE = re.compile(r"^\s*(left|right|center|centre)\s+(.*)$", re.I)


def parse_prop(t):
    """Prop orders ('prop pitch 15', 'rpm max', 'torque 50', 'props off', 'props reverse'), optionally for one side
    group: 'left props pitch 15', 'right rpm max', 'left props reverse', 'center torque 50'. -> args or None."""
    g = None
    m = _GROUP_RE.match(t)
    if m:
        g, t = m.group(1).lower().replace("centre", "center"), m.group(2)
    args = None
    m = _PROP_PITCH_RE.match(t)
    if m:
        args = {"pitch": m.group(1).lower()}
    elif _RPM_RE.match(t):
        args = {"rpm": _RPM_RE.match(t).group(1).lower()}
    elif _TORQUE_RE.match(t):
        args = {"torque": _TORQUE_RE.match(t).group(1).lower()}
    elif _PROP_ONOFF_RE.match(t):
        args = {"motor": _PROP_ONOFF_RE.match(t).group(1).lower()}
    elif _PROP_REV_RE.match(t):
        args = {"reverse": "on" if _PROP_REV_RE.match(t).group(1).lower() == "reverse" else "off"}
    if args and g:
        args["group"] = g
    return args


def parse(text):
    """A direct order -> (tool, args), else None. Numbers after 'throttle' are percent. Keyword typos are
    tolerated (fix_typos)."""
    t = fix_typos((text or "").strip())
    m = _SMALL_ALT_RE.match(t)
    if m and float(m.group(2)) < 100:
        # small heights are helicopter heights (planes: a bare number < 100 was never trusted anyway)
        return "heli_control", {"mode": "climb" if any(w in m.group(1).lower() for w in ("up", "climb")) else "descend",
                                "altitude_m": float(m.group(2))}
    pc = parse_pitch(t)
    if pc:
        return "plane_pitch", {"degrees": pc[1], "direction": pc[0]}
    alt = parse_altitude(t)
    if alt:
        return "set_altitude", alt
    m = _THROTTLE_RE.match(t)
    if m:
        pct, hi, lo, auto = m.groups()
        val = -1.0 if auto else (1.0 if hi else (0.0 if lo else min(float(pct), 100.0) / 100.0))
        return "set_throttle", {"value": val}
    m = _GEAR_RE.match(t)
    if m:
        return "set_gear", {"down": m.group(1).lower() == "down"}
    m = _BRAKES_RE.match(t)
    if m:
        return "set_brakes", {"on": m.group(1).lower() == "on"}
    m = _SPEED_RE.match(t)
    if m:
        return "set_speed", {"speed": float(m.group(1))}
    if _CHUTES_RE.match(t):
        return "deploy_parachutes", {}
    m = _EJECT_RE.match(" ".join(re.sub(r"[,;:.!]+", " ", t).split()))  # 'EJECT CONFIRM!' / 'eject, now' / 'EJECT!!'
    if m:  # one-step 'eject confirm' / 'confirm eject' / 'eject now' runs at once; plain 'eject' asks yes/no first
        return "eject_kerbal", ({"confirmed": True} if (m.group("pre") or m.group("post")) else {})
    m = _TURN_RE.match(t)
    if m:
        return "turn", {"direction": m.group(1).lower(), "degrees": float(m.group(2)) if m.group(2) else TURN_DEFAULT}
    m = _HEADING_RE.match(t)
    if m:
        return "set_heading", {"heading": float(m.group(1))}
    if _DEORBIT_RE.match(t):  # 'Deorbit!' (the model once only claimed it had burned)
        return "deorbit_burn", {}
    if _RTB_RE.match(t):
        return "fly_to_place", {"name": "KSC"}
    m = _FLYTO_RE.match(t)
    if m and not _PLACE_STOP.search(m.group(1)):
        return "fly_to_place", {"name": m.group(1).strip()}
    m = _CIRCLE_RE.match(t)
    if m:
        return "circle_here", {"direction": (m.group(1) or "left").lower()}
    if _REPORT_RE.match(t):
        return "flight_report", {}
    if _FUEL_RE.match(t):
        return "fuel_check", {}
    m = _HOWFAR_RE.match(t)
    if m and not _PLACE_STOP.search(m.group(1)):
        return "how_far", {"name": m.group(1).strip()}
    if _TTT_RE.match(t):
        return "time_to_target", {}
    if _LEVEL_RE.match(t):
        return "level_off", {}
    if _STAGE_RE.match(t):
        return "stage", {}
    m = _SAS_RE.match(t)
    if m:
        return "set_sas", {"enabled": m.group(1).lower() == "on"}
    m = _SASMODE_RE.match(t)
    if m:
        return "set_sas_mode", {"mode": m.group(1).lower()}
    m = _RCS_RE.match(t)
    if m:
        return "set_rcs", {"on": m.group(1).lower() == "on"}
    m = _LIGHTS_RE.match(t)
    if m:
        return "set_lights", {"on": m.group(1).lower() == "on"}
    m = _AG_RE.match(t)
    if m and 1 <= int(m.group(1)) <= 10:
        return "action_group", {"group": int(m.group(1)), "state": (m.group(2) or "toggle").lower()}
    m = _OVR_MAX_RE.match(t)
    if m:
        return "set_override", {"kind": "speed", "value": "max", "authority": "pre" if m.group(1) == "***" else "ask"}
    m = _OVR_RE.match(t)
    if m:
        kind = "altitude" if m.group(1).lower() == "alt" else m.group(1).lower()
        val = float(m.group(2)) * (1000.0 if (m.group(3) or "").lower() == "km" else 1.0)
        return "set_override", {"kind": kind, "value": val, "authority": "pre" if m.group(4) == "***" else "ask"}
    m = _OVR_OFF_RE.match(t)
    if m:
        kind = (m.group(1) or "all").lower()
        return "set_override", {"kind": "altitude" if kind == "alt" else kind, "value": "off"}
    if _AUTH_ALL_RE.match(t):
        return "authorise_all", {}
    m = _ENGINES_RE.match(t)
    if m:
        return "set_engines", {"on": m.group(1).lower() == "on"}
    if _CUT_RE.match(t):
        return "cut_engines", {}
    if _MODE_RE.match(t):
        return "engine_mode", {}
    m = _AB_RE.match(t)
    if m:
        return "afterburner", {"on": m.group(1).lower() == "on"}
    m = _FLAPS_RE.match(t)
    if m:
        return "flaps", {"setting": m.group(1).lower()}
    m = _TRIM_RE.match(t)
    if m:
        return "trim", {"direction": m.group(1).lower(), "percent": float(m.group(2)) if m.group(2) else 5.0}
    if _TRIM0_RE.match(t):
        return "trim", {"direction": "reset", "percent": 0.0}
    if _TAKEOFF_RE.match(t):
        return "takeoff", {}
    m = _HOVER_RE.match(t)
    if m:
        return "heli_control", {"mode": "hover", **({"altitude_m": float(m.group(1))} if m.group(1) else {}),
                                **({"heading": float(m.group(2))} if m.group(2) else {})}
    m = _FACE_RE.match(t)
    if m:
        return "heli_control", {"mode": "face", "heading": float(m.group(1))}
    m = _FLY_TRK_RE.match(t)
    if m:
        return "heli_control", {"mode": "fly", "heading": float(m.group(1)), **({"speed": float(m.group(2))} if m.group(2) else {})}
    m = _SIDESTEP_RE.match(t)
    if m:
        return "heli_control", {"mode": "sidestep", "direction": m.group(1).lower(),
                                **({"distance": float(m.group(2))} if m.group(2) else {})}
    m = _BACKUP_RE.match(t)
    if m:
        return "heli_control", {"mode": "back", **({"distance": float(m.group(1))} if m.group(1) else {})}
    if _HOLDPOS_RE.match(t):
        return "heli_control", {"mode": "hold"}
    if _DESCEND_BARE_RE.match(t):
        return "heli_control", {"mode": "descend"}
    m = _HDG_SPD_RE.match(t)
    if m:
        return "heli_control", {"mode": "fly", "heading": float(m.group(1)), "speed": float(m.group(2))}
    pc = parse_prop(t)
    if pc:
        return "prop_control", pc
    if _DAMAGE_RE.match(t):
        return "damage_report", {}
    m = _LAND_RWY_RE.match(t)
    if m:
        return "land_plane", {"runway": "27" if m.group(1) == "27" else "09"}
    m = _LAND_RE.match(t)
    if m:
        return "land", {"where": (m.group(1) or "").lower()}
    if _GOAROUND_RE.match(t):
        return "go_around", {}
    if _TNG_RE.match(t):
        return "touch_and_go", {}
    if _ABORT_AP_RE.match(t):
        return "abort", {}
    if _ABORT_RE.match(t):
        return "abort_ag", {}
    if _CREW_RE.match(t):
        return "crew_report", {}
    return None