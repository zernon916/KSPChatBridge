"""Propulsion classification (Luke 2026-10-08: a propeller plane was answered 'no engines' and 'takeoff' was sent to
the rocket ascent).

kRPC lists only engine modules (ModuleEngines*) in Parts.engines. Breaking Ground propellers are a robotic rotor
(ModuleRoboticServoRotor) turning blade parts (ModuleControlSurface, titles '... Blade ...' / 'Propeller ...'); kRPC
reports no thrust for them. Engine modules that burn only ElectricCharge (electric props from mods) are engines in
kRPC but are not rockets. Where thrust can't be read, callers fall back to full takeoff power governed by the measured
acceleration (takeoff.py) and a measured-acceleration thrust estimate (maxspeed.py).
"""
import time

ROTOR_MODULES = ("ModuleRoboticServoRotor",)
BLADE_WORDS = ("blade", "propeller")
ELECTRIC = frozenset(("ElectricCharge",))
TTL = 10.0
_CACHE = {}


def is_blade(title):
    t = str(title or "").lower()
    return any(w in t for w in BLADE_WORDS)


def engine_kind(propellants):
    """'jet' (needs IntakeAir), 'electric' (only ElectricCharge) or 'rocket'."""
    props = set(propellants or ())
    if "IntakeAir" in props:
        return "jet"
    if props and props <= ELECTRIC:
        return "electric"
    return "rocket"


def summarize(kinds, rotors, blades):
    """Pure: engine kinds + rotor / blade counts -> summary dict."""
    jets = sum(1 for k in kinds if k == "jet")
    electric = sum(1 for k in kinds if k == "electric")
    rockets = sum(1 for k in kinds if k == "rocket")
    props = electric > 0 or (rotors > 0 and blades > 0)
    return {"jets": jets, "rockets": rockets, "electric": electric, "rotors": rotors, "blades": blades,
            "props": props, "any": bool(jets or rockets or electric or rotors)}


def classify(v):
    """Summary for a kRPC vessel (cached TTL s per vessel name + part count). Never raises."""
    try:
        key = (str(v.name), len(v.parts.all))
    except Exception:  # noqa: BLE001
        key = None
    hit = _CACHE.get(key) if key else None
    if hit and time.time() - hit[0] < TTL:
        return hit[1]
    kinds = []
    try:
        for e in v.parts.engines:
            try:
                kinds.append(engine_kind(e.propellant_names))
            except Exception:  # noqa: BLE001
                kinds.append("rocket")
    except Exception:  # noqa: BLE001
        pass
    rotors = blades = 0
    try:
        rotors = sum(len(v.parts.modules_with_name(m)) for m in ROTOR_MODULES)
    except Exception:  # noqa: BLE001
        pass
    if rotors:
        try:
            blades = sum(1 for cs in v.parts.control_surfaces if is_blade(cs.part.title))
        except Exception:  # noqa: BLE001
            pass
    out = summarize(kinds, rotors, blades)
    if key:
        _CACHE[key] = (time.time(), out)
    return out


def has_props(v):
    return classify(v)["props"]


def clear_vessel_cache():
    """Forget per-craft prop/heli classification and prop-control layout (vessel switch)."""
    _CACHE.clear()
    STATE["layout"].clear()
    STATE["logged"].clear()
    STATE["sign"].clear()
    STATE["radius"].clear()
    STATE["sense"] = None
    STATE.update(manual_pitch=None, manual_torque=False, reverse=False, diff=None)
    reset_spool_state()


def reset_spool_state():
    """Forget 'already spooled' so the next ground takeoff runs a full spool-up."""
    try:
        from . import heli
        heli.STATE.pop("spooled", None)
    except Exception:  # noqa: BLE001
        pass


def rotor_status(v):
    """[(title, motor, rpm)] for the robotic rotors (field texts as KSP shows them; '' if unknown)."""
    out = []
    try:
        mods = [m for name in ROTOR_MODULES for m in v.parts.modules_with_name(name)]
    except Exception:  # noqa: BLE001
        return out
    for m in mods:
        try:
            f = fields(m)
            title = m.part.title
        except Exception:  # noqa: BLE001
            continue
        motor = next((str(val) for k, val in f.items() if k.lower().startswith("motor") and "motorized" not in k.lower()), "")
        rpm = next((str(val) for k, val in f.items() if "current rpm" in k.lower()), "") or \
            next((str(val) for k, val in f.items() if "rpm" in k.lower()), "")
        out.append((title, motor, rpm))
    return out


def motor_off(status):
    """True if any rotor motor reads disengaged / off."""
    return any(str(m).strip().lower() in ("disengaged", "off", "false", "locked") for _, m, _ in status or ())


def describe(summary):
    bits = []
    for k, word in (("jets", "jet"), ("rockets", "rocket engine"), ("electric", "electric engine"),
                    ("rotors", "propeller rotor")):
        n = summary.get(k) or 0
        if n:
            bits.append(f"{n} {word}{'s' if n != 1 else ''}")
    return ", ".join(bits) or "no engines or propellers"

# ======================================================================== prop control (Breaking Ground)
# Field names are read from the modules at runtime (kRPC Module.fields is keyed by the KSP GUI name) and logged once
# per craft. Assumed (KSP 1.12 BG): rotor ModuleRoboticServoRotor 'RPM Limit' (rpmLimit), 'Torque Limit(%)'
# (servoMotorLimit), 'Motor' (servoMotorIsEngaged: Engaged / Disengaged), 'Current RPM'; blade ModuleControlSurface
# 'Deploy Angle' (deployAngle) with the kRPC ControlSurface.deployed switch. Matching is by keyword, so small naming
# differences still work; anything missing is reported, never guessed.
#
# Blade math (thin-airfoil view, as the BG blades are lifting surfaces): the blade section at radius r moves at
# u = RPM * 2pi/60 * r in the rotation plane while the air comes at the flight speed V, so the inflow angle is
# phi = atan(V / u). The blade's angle of attack = deploy angle (from the rotation plane) - phi. Positive AoA pulls
# forward; a negative deploy angle gives a negative AoA = reverse thrust (beta). The schedule keeps AoA ~ AOA_OPT.
import logging
import math
import re

_log = logging.getLogger("kspchat")
RPM_MAX = 460.0              # BG rotors cap at 460 RPM ('max' orders)
TORQUE_MAX = 100.0           # Torque Limit is a percentage
AOA_OPT = 8.0                # deg blade angle of attack held by the schedule
PITCH_MIN, PITCH_MAX = 8.0, 60.0
FLIGHT_MIN_TORQUE = 10.0     # never cut props to 0 in flight (Luke): torque floor (%)
FLIGHT_MIN_RPM_FRAC = 0.25   # ... RPM limit floor (fraction of RPM_MAX)
FLIGHT_MIN_PITCH = 2.0       # ... and no flat / negative blades in flight
REV_PITCH = -15.0            # rollout reverse (beta) deploy angle
REV_TORQUE = 60.0
REV_ON_MIN_SPD, REV_OFF_SPD = 15.0, 10.0
BLADE_HALF_SPAN, R_DEFAULT = 0.6, 1.5   # m: blade lift centre beyond its root attach point; radius if unknown
PITCH_EVERY_S, TORQUE_EVERY_S = 2.0, 1.0
GROUP_X = 0.5                # m: |lateral offset| from the CoM below this = center rotor
GROUPS = ("left", "right", "center")
DIFF_DEAD, DIFF_FRAC = 3.0, 0.5   # rollout differential reverse: heading error (deg) deadband / weak-side torque
STATE = {"manual_pitch": None, "manual_torque": False, "reverse": False, "logged": set(), "sign": {}, "radius": {},
         "layout": {}, "diff": None, "sense": None}
_NUM = re.compile(r"-?\d+(?:\.\d+)?")


# kRPC 0.6 (Luke's live server): Module.fields is deprecated and THROWS on Breaking Ground rotors - 'Motor' is the GUI
# name of both servoMotorIsEngaged and motorState ("An item with the same key has already been added. Key: Motor"), so
# every rotor read/write silently failed (live 2026-10-08: "RPM limit 460 (0/2)", Brake 100 / Torque 0 never fixed).
# fields(m) falls back to Module.fields_by_id (one call) with the ids mapped to the GUI names and GUI-style values;
# _set_* write by id whenever the GUI key came from that map.
FIELD_GUI = {"rpmLimit": "RPM Limit", "currentRPM": "Current RPM", "rotateCounterClockwise": "Rotation Direction",
             "inverted": "Invert Direction", "brakePercentage": "Brake", "servoIsLocked": "Locked",
             "servoMotorIsEngaged": "Motor", "servoMotorLimit": "Torque Limit(%)",
             "motorState": "Motor state (motorized)", "lockPartOnPowerLoss": "On Power Loss",
             "deployAngle": "Deploy Angle", "deploy": "Deploy", "deployInvert": "Invert Deploy Direction",
             "authorityLimiter": "Authority Limiter"}
_TRUE = ("true", "1", "yes", "on")
FIELD_VAL = {  # raw by-id value -> the text KSP shows (what the keyword matching expects)
    "rotateCounterClockwise": lambda x: "Counterclockwise" if str(x).lower() in _TRUE else "Clockwise",
    "servoMotorIsEngaged": lambda x: "Engaged" if str(x).lower() in _TRUE else "Disengaged",
    "lockPartOnPowerLoss": lambda x: "Locked" if str(x).lower() in _TRUE else "Free Spin",
}
_FIELD_IDS = {}        # module name -> {GUI key: field id}
_FIELDS_BROKEN = set()  # module names whose Module.fields throws
_GUI_CACHE = {}        # module name -> {field id: GUI name} (field_list, read once)


def _mname(m):
    try:
        return str(m.name)
    except Exception:  # noqa: BLE001
        return "?"


def _gui_names(m, mname, ids):
    known = _GUI_CACHE.setdefault(mname, {})
    if any(i not in FIELD_GUI and i not in known for i in ids):
        try:
            for f in m.field_list:
                fid = f.name
                if fid in ids and fid not in FIELD_GUI:
                    known[fid] = f.gui_name or fid
        except Exception:  # noqa: BLE001
            pass
        for i in ids:
            known.setdefault(i, FIELD_GUI.get(i, i))
    return known


def fields(m):
    """GUI name -> value text for a part module (like kRPC Module.fields), robust to kRPC 0.6's duplicate-name throw.
    Raises only if nothing can be read."""
    mname = _mname(m)
    if mname not in _FIELDS_BROKEN or not hasattr(m, "fields_by_id"):
        try:
            import warnings
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", DeprecationWarning)
                return dict(m.fields)
        except Exception:  # noqa: BLE001
            if not hasattr(m, "fields_by_id"):
                raise
            if mname != "?":
                _FIELDS_BROKEN.add(mname)
                _log.info("props: %s Module.fields unreadable - using fields_by_id", mname)
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", DeprecationWarning)
        raw = dict(m.fields_by_id)
    gui = _gui_names(m, mname, list(raw))
    out, idmap = {}, _FIELD_IDS.setdefault(mname, {})
    for fid, val in raw.items():
        name = FIELD_GUI.get(fid) or gui.get(fid) or fid
        if name in out:
            name = f"{name} [{fid}]"
        out[name] = FIELD_VAL[fid](val) if fid in FIELD_VAL else str(val)
        idmap[name] = fid
    return out


def _field_id(m, key):
    """Field id when `key` came from the by-id fallback, else None (then set by GUI name)."""
    mname = _mname(m)
    return _FIELD_IDS.get(mname, {}).get(key) if (mname in _FIELDS_BROKEN or mname == "?") and \
        hasattr(m, "set_field_float_by_id") else None


def find_field(fields, *needles, exclude=()):
    """Actual field name containing all needles (case-insensitive), None if absent."""
    for k in fields or {}:
        lk = str(k).lower()
        if all(n in lk for n in needles) and not any(x in lk for x in exclude):
            return k
    return None


def num(val):
    m = _NUM.search(str(val or ""))
    return float(m.group(0)) if m else None


def inflow_deg(spd, rpm, radius):
    u = max(rpm, 0.0) * 2.0 * math.pi / 60.0 * max(radius, 0.0)
    if u <= 0.1:
        return 90.0 if spd > 0.5 else 0.0
    return math.degrees(math.atan2(max(spd, 0.0), u))


def blade_pitch_for(spd, rpm, radius, aoa=AOA_OPT):
    """Deploy angle keeping the blade AoA ~ aoa at this airspeed / RPM / radius (clamped)."""
    if rpm <= 1.0:
        return PITCH_MIN
    return max(PITCH_MIN, min(PITCH_MAX, inflow_deg(spd, rpm, radius) + aoa))


def flight_floor(kind, value):
    """Luke's rule: props never cut to 0 in flight."""
    if kind == "torque":
        return max(FLIGHT_MIN_TORQUE, value)
    if kind == "rpm":
        return max(FLIGHT_MIN_RPM_FRAC * RPM_MAX, value)
    return max(FLIGHT_MIN_PITCH, value)


def _rotor_mods(v, group=None, rotors=None):
    if rotors is not None:  # rotor indices into layout(v)["rotors"] (helicopter lift / tail rotors)
        return [r["mod"] for i, r in enumerate(layout(v)["rotors"]) if i in rotors]
    if group is not None:
        return [r["mod"] for r in layout(v)["rotors"] if r["group"] == group]
    try:
        return [m for name in ROTOR_MODULES for m in v.parts.modules_with_name(name)]
    except Exception:  # noqa: BLE001
        return []


def side_of(x):
    """Group from the lateral offset (vessel frame x: + = right of the CoM)."""
    return "center" if abs(x) < GROUP_X else ("right" if x > 0 else "left")


def spin_dir(fields):
    """+1 clockwise, -1 counter-clockwise, None unknown (rotor 'Rotation Direction'-type field)."""
    k = find_field(fields, "direction", exclude=("deploy",)) or find_field(fields, "counter")
    if not k:
        return None
    val = str(fields.get(k, "")).strip().lower()
    if "counter" in str(k).lower():
        return -1 if val in ("true", "1", "on", "yes") else 1
    if "counter" in val or "ccw" in val or "anti" in val:
        return -1
    if "clock" in val or val == "cw":
        return 1
    return None


def invert_on(fields, deploy=False):
    """True when Invert Direction (rotor) or Invert Deploy Direction (blade) is on."""
    if deploy:
        k = find_field(fields, "deploy", "invert") or find_field(fields, "invert", "deploy")
    else:
        k = find_field(fields, "invert", exclude=("deploy",))
    if not k:
        return None
    val = str(fields.get(k, "")).strip().lower()
    return val in _TRUE or val in ("inverted", "yes")


def layout(v):
    """Rotors grouped by side (left / right / center from their x in the vessel frame, origin = CoM) with their spin
    direction, and each blade assigned to its nearest rotor. Cached per craft (name + part count)."""
    try:
        key = (str(v.name), len(v.parts.all))
    except Exception:  # noqa: BLE001
        key = None
    if key in STATE["layout"]:
        return STATE["layout"][key]
    out = {"rotors": [], "blades": [], "counter_pairs": []}
    try:
        rf = v.reference_frame
    except Exception:  # noqa: BLE001
        rf = None
    for m in _rotor_mods(v):
        r = {"mod": m, "group": "center", "dir": None, "pos": None, "pid": None, "blades": 0}
        try:
            r["pid"] = getattr(m.part, "_object_id", None)
            r["pos"] = tuple(m.part.position(rf))
            r["group"] = side_of(r["pos"][0])
        except Exception:  # noqa: BLE001
            pass
        try:
            r["dir"] = spin_dir(fields(m))
        except Exception:  # noqa: BLE001
            pass
        out["rotors"].append(r)
    by_pid = {r["pid"]: i for i, r in enumerate(out["rotors"]) if r["pid"] is not None}
    for m in _blade_mods(v):
        b = {"mod": m, "rotor": None, "group": "center", "pid": None}
        try:
            b["pid"] = getattr(m.part, "_object_id", id(m.part))
            # the rotor the blade is ATTACHED to (part tree: blade -> hub, maybe via an adapter); live 2026-10-08 a
            # coaxial stack put the upper rotor's blades nearer the lower hub, so distance alone mislabels them
            try:
                par, hops = m.part, 0
                while by_pid and hops < 3 and b["rotor"] is None:
                    par = par.parent
                    if par is None:
                        break
                    b["rotor"] = by_pid.get(getattr(par, "_object_id", None))
                    hops += 1
            except Exception:  # noqa: BLE001
                b["rotor"] = None
            if b["rotor"] is None:
                p = tuple(m.part.position(rf))
                near = [(math.dist(p, r["pos"]), i) for i, r in enumerate(out["rotors"]) if r["pos"] is not None]
                if near:
                    b["rotor"] = min(near)[1]
            if b["rotor"] is not None:
                b["group"] = out["rotors"][b["rotor"]]["group"]
                out["rotors"][b["rotor"]]["blades"] += 1
        except Exception:  # noqa: BLE001
            pass
        out["blades"].append(b)
    by = {g: {r["dir"] for r in out["rotors"] if r["group"] == g and r["dir"]} for g in ("left", "right")}
    if by["left"] and by["right"] and by["left"] != by["right"]:
        out["counter_pairs"].append(("left", "right"))
    if key:
        STATE["layout"][key] = out
    return out


def groups(v):
    """Groups that have rotors, in a fixed order."""
    have = {r["group"] for r in layout(v)["rotors"]}
    return [g for g in GROUPS if g in have]


def _blade_mods(v):
    try:
        return [m for m in v.parts.modules_with_name("ModuleControlSurface") if is_blade(m.part.title)]
    except Exception:  # noqa: BLE001
        return []


def _blade_surfaces(v):
    try:
        return [cs for cs in v.parts.control_surfaces if is_blade(cs.part.title)]
    except Exception:  # noqa: BLE001
        return []


def log_fields(v):
    """Log the real field / action / event names once per craft (so the assumed names can be checked)."""
    try:
        key = str(v.name)
    except Exception:  # noqa: BLE001
        return
    if key in STATE["logged"]:
        return
    STATE["logged"].add(key)
    for kind, mods in (("rotor", _rotor_mods(v)[:1]), ("blade", _blade_mods(v)[:1])):
        for m in mods:
            try:
                _log.info("props: %s %s fields %s | actions %s | events %s", kind, m.part.title, fields(m),
                          list(m.actions), list(m.events))
            except Exception as e:  # noqa: BLE001
                _log.info("props: %s fields unreadable (%s)", kind, e)


def _read_float(m, key):
    if key is None:
        return None
    try:
        return num(fields(m).get(key))
    except Exception:  # noqa: BLE001
        return None


def _set_float(m, key, value, *, verify=False, tol=1.0, tries=4):
    if key is None:
        return False
    val = float(value)

    def _write_once():
        fid = _field_id(m, key)
        if fid:
            try:
                m.set_field_float_by_id(fid, val)
                return True
            except Exception:  # noqa: BLE001
                try:
                    m.set_field_string_by_id(fid, f"{val:g}")
                    return True
                except Exception:  # noqa: BLE001
                    return False
        try:
            m.set_field_float(key, val)
            return True
        except Exception:  # noqa: BLE001
            try:
                m.set_field_string(key, f"{val:g}")
                return True
            except Exception:  # noqa: BLE001
                return False

    if not verify:
        return _write_once()
    for _ in range(tries):
        if not _write_once():
            continue
        got = _read_float(m, key)
        if got is not None and abs(got - val) <= tol:
            return True
    return False


def _set_motor(m, on):
    f = fields(m)
    key = find_field(f, "motor", exclude=("size", "output", "motorized"))
    cur = str(f.get(key, "")).strip().lower() if key else ""
    if key and (cur in ("engaged", "true", "on", "1")) == on:
        return True
    if key:
        try:
            fid = _field_id(m, key)
            if fid:
                m.set_field_bool_by_id(fid, bool(on))
            else:
                m.set_field_bool(key, bool(on))
            return True
        except Exception:  # noqa: BLE001
            pass
    want = "disengage" if not on else "engage"
    try:
        evs = [e for e in m.events if "motor" in e.lower() and want in e.lower()
               and (on is False or "disengage" not in e.lower())]
        if evs:
            m.trigger_event(evs[0])
            return True
    except Exception:  # noqa: BLE001
        pass
    try:
        acts = list(m.actions)
        exact = [a for a in acts if "motor" in a.lower() and want in a.lower() and "toggle" not in a.lower()
                 and (on is False or "disengage" not in a.lower())]
        if exact:
            m.set_action(exact[0], True)
            return True
        tog = [a for a in acts if "motor" in a.lower() and "toggle" in a.lower()]
        if tog and key:  # only when the field told us it's in the other state
            m.set_action(tog[0], True)
            return True
    except Exception:  # noqa: BLE001
        pass
    return False


def set_rotor(v, rpm=None, torque=None, motor=None, flying=False, group=None, rotors=None):
    """Set RPM limit / torque limit (%) / motor on all rotors (or one group / the rotor indices). Returns a report."""
    log_fields(v)
    mods = _rotor_mods(v, group, rotors)
    if not mods:
        return f"no {group + ' ' if group else ''}propeller rotors found"
    said, missing = [], set()
    if motor is False and flying:
        said.append("motor stays ON in flight (never cut props in flight)")
        motor = None
    if torque is not None and flying:
        torque = flight_floor("torque", torque)
    if rpm is not None and flying:
        rpm = flight_floor("rpm", rpm)
    ok_m = ok_r = ok_t = 0
    for m in mods:
        try:
            f = fields(m)
        except Exception:  # noqa: BLE001
            continue
        verify = not flying
        if rpm is not None:
            k = find_field(f, "rpm", "limit")
            ok_r += bool(k and _set_float(m, k, min(RPM_MAX, max(0.0, rpm)), verify=verify, tol=2.0))
            if not k:
                missing.add("RPM Limit")
        if torque is not None:
            k = find_field(f, "torque", "limit") or find_field(f, "torque", exclude=("current", "max"))
            ok_t += bool(k and _set_float(m, k, min(TORQUE_MAX, max(0.0, torque)), verify=verify, tol=2.0))
            if not k:
                missing.add("Torque Limit")
        if motor is not None:
            ok_m += _set_motor(m, motor)
    n = len(mods)
    if group:
        said.insert(0, group)
    if rpm is not None:
        said.append(f"RPM limit {min(RPM_MAX, rpm):.0f} ({ok_r}/{n})")
    if torque is not None:
        said.append(f"torque {min(TORQUE_MAX, torque):.0f}% ({ok_t}/{n})")
    if motor is not None:
        said.append(f"motor {'on' if motor else 'off'} ({ok_m}/{n})")
    if missing:
        said.append("no field " + "/".join(sorted(missing)) + " on the rotor")
    return ", ".join(said)


def _blade_sign(v, m, cur):
    """Remember each blade's own deploy-angle sign (CW/CCW / inverted setups) the first time it's clearly set."""
    try:
        key = (str(v.name), getattr(m, "_object_id", id(m)))
    except Exception:  # noqa: BLE001
        key = id(m)
    if key not in STATE["sign"] and cur is not None and abs(cur) > 1.0 and not STATE["reverse"]:
        STATE["sign"][key] = -1.0 if cur < 0 else 1.0
    return STATE["sign"].get(key, 1.0)


def set_blades(v, pitch=None, deploy=None, flying=False, allow_reverse=False, group=None, rotors=None):
    """Blade pitch (deploy angle, deg from the rotation plane; negative = reverse - ground only) and deploy, on all
    blades or one group's. Each blade keeps its own learned sign (a blade Luke built with a negative / inverted deploy
    angle is mirrored); KSP itself applies 'Invert Deploy Direction', so no extra mirroring is added for it."""
    log_fields(v)
    surfs = _blade_surfaces(v)
    mods = _blade_mods(v)
    if group is not None or rotors is not None:
        bl = [b for b in layout(v)["blades"] if (b["rotor"] in rotors if rotors is not None else b["group"] == group)]
        mods = [b["mod"] for b in bl]
        pids = {b["pid"] for b in bl}
        surfs = [cs for cs in surfs if getattr(cs.part, "_object_id", id(cs.part)) in pids]
    if not surfs and not mods:
        return f"no {group + ' ' if group else ''}propeller blades found"
    said = [group] if group else []
    if deploy is not None:
        n = 0
        for cs in surfs:
            try:
                cs.deployed = bool(deploy)
                n += 1
            except Exception:  # noqa: BLE001
                pass
        said.append(f"blades {'deployed' if deploy else 'retracted'} ({n}/{len(surfs)})")
    if pitch is not None:
        if pitch < 0 and (flying or not allow_reverse):
            pitch = FLIGHT_MIN_PITCH if flying else 0.0
            said.append("no reverse pitch in flight" if flying else "reverse only via 'props reverse'")
        elif flying:
            pitch = flight_floor("pitch", pitch)
        n, missing = 0, False
        for m in mods:
            try:
                f = fields(m)
                k = find_field(f, "deploy", "angle")
                if not k:
                    missing = True
                    continue
                sign = _blade_sign(v, m, num(f.get(k)))
                n += _set_float(m, k, sign * pitch)
            except Exception:  # noqa: BLE001
                pass
        said.append(f"blade pitch {pitch:+.0f} deg ({n}/{len(mods)})" + (" - no 'Deploy Angle' field" if missing else ""))
    return ", ".join(said)


def rotor_rpm(v):
    """Actual rotor RPM (field 'Current RPM'), else the RPM limit, else RPM_MAX."""
    for m in _rotor_mods(v)[:1]:
        try:
            f = fields(m)
            for k in (find_field(f, "current", "rpm"), find_field(f, "rpm", "limit")):
                x = num(f.get(k)) if k else None
                if x is not None:
                    return x
        except Exception:  # noqa: BLE001
            pass
    return RPM_MAX


def blade_radius(v):
    """Effective blade radius: rotor hub -> blade root distance (vessel frame) + half a blade span; cached."""
    try:
        key = (str(v.name), len(v.parts.all))
    except Exception:  # noqa: BLE001
        key = None
    if key in STATE["radius"]:
        return STATE["radius"][key]
    r = R_DEFAULT
    try:  # each blade's distance to its OWN (nearest) rotor hub
        lay = layout(v)
        rf = v.reference_frame
        ds = []
        for b in lay["blades"][:16]:
            if b["rotor"] is None:
                continue
            hub = lay["rotors"][b["rotor"]]["pos"]
            ds.append(math.dist(hub, b["mod"].part.position(rf)))
        ds = [d for d in ds if 0.05 < d < 20.0]
        if ds:
            r = sum(ds) / len(ds) + BLADE_HALF_SPAN
    except Exception:  # noqa: BLE001
        pass
    if key:
        STATE["radius"][key] = r
    return r


def takeoff_setup(v):
    """Motor engaged, RPM and torque max, blades deployed at a fine pitch (the schedule then follows the speed)."""
    STATE.update(manual_pitch=None, manual_torque=False, reverse=False)
    pre, _ = preflight(v)
    a = set_rotor(v, rpm=RPM_MAX, torque=TORQUE_MAX, motor=True)
    b = set_blades(v, pitch=blade_pitch_for(0.0, RPM_MAX, blade_radius(v)), deploy=True)
    msg = f"props set for takeoff: {a}; {b}" + (f" | {pre}" if pre else "")
    _log.info("%s (radius %.2f m)", msg, blade_radius(v))
    return msg


def reverse(v, on, sit, group=None):
    """Beta / reverse thrust on the ground only (negative blade pitch); off -> fine pitch. group = one side only."""
    if on and sit not in ("landed", "pre_launch", "splashed"):
        return False, "props reverse refused: only on the ground, never in flight"
    if on:
        STATE["reverse"] = True
        STATE["diff"] = None
        r = (set_blades(v, pitch=REV_PITCH, deploy=True, allow_reverse=True, group=group) + "; "
             + set_rotor(v, torque=REV_TORQUE, group=group))
        return True, "props REVERSE: " + r
    if group is None:
        STATE["reverse"] = False
    return True, "props forward: " + set_blades(v, pitch=blade_pitch_for(0.0, RPM_MAX, blade_radius(v)), deploy=True,
                                                 flying=sit == "flying", group=group)


def diff_reverse(v, herr):
    """Rollout steering with differential reverse (left + right groups only): heading error herr > 0 = need to turn
    right -> full reverse torque on the right (it pulls the right side back), reduced on the left; and vice versa.
    Returns a report when it changed something, else ''."""
    gs = groups(v)
    if not STATE["reverse"] or "left" not in gs or "right" not in gs:
        return ""
    want = "right" if herr > DIFF_DEAD else ("left" if herr < -DIFF_DEAD else "even")
    if want == STATE["diff"]:
        return ""
    STATE["diff"] = want
    lo = REV_TORQUE * DIFF_FRAC
    tl, tr = {"right": (lo, REV_TORQUE), "left": (REV_TORQUE, lo), "even": (REV_TORQUE, REV_TORQUE)}[want]
    return (f"differential reverse ({want}): " + set_rotor(v, torque=tl, group="left") + "; "
            + set_rotor(v, torque=tr, group="right"))


def _motor_on(text):
    t = str(text or "").strip().lower()
    return None if not t else t in ("engaged", "true", "on", "1")


def prop_status(v):
    """{'rotors': [{title, group, dir, motor, motor_on, rpm, rpm_limit, torque}], 'pitch': mean deploy angle | None,
    'groups': {group: {rpm, rpm_limit, torque, pitch, motor_on, n}}, 'counter_pairs', 'reverse'}."""
    lay = layout(v)
    out = {"rotors": [], "pitch": None, "groups": {}, "counter_pairs": lay["counter_pairs"], "reverse": STATE["reverse"]}
    for r in lay["rotors"]:
        m = r["mod"]
        try:
            f = fields(m)
            g = lambda *n, **kw: f.get(find_field(f, *n, **kw)) if find_field(f, *n, **kw) else None  # noqa: E731
            mt = str(g("motor", exclude=("size", "output", "motorized")) or "")
            out["rotors"].append({"title": m.part.title, "group": r["group"], "dir": r["dir"], "motor": mt,
                                  "motor_on": _motor_on(mt), "rpm": num(g("current", "rpm")),
                                  "rpm_limit": num(g("rpm", "limit")), "torque": num(g("torque", "limit"))})
        except Exception:  # noqa: BLE001
            pass
    pitch = {}
    for b in lay["blades"]:
        try:
            f = fields(b["mod"])
            k = find_field(f, "deploy", "angle")
            x = num(f.get(k)) if k else None
            if x is not None:
                pitch.setdefault(b["group"], []).append(x)
        except Exception:  # noqa: BLE001
            pass
    allp = [x for xs in pitch.values() for x in xs]
    if allp:
        out["pitch"] = sum(allp) / len(allp)
    for gname in GROUPS:
        rs = [r for r in out["rotors"] if r["group"] == gname]
        if not rs:
            continue
        avg = lambda key: (lambda xs: sum(xs) / len(xs) if xs else None)([r[key] for r in rs if r[key] is not None])  # noqa: E731
        ons = [r["motor_on"] for r in rs if r["motor_on"] is not None]
        ps = pitch.get(gname) or []
        out["groups"][gname] = {"n": len(rs), "rpm": avg("rpm"), "rpm_limit": avg("rpm_limit"), "torque": avg("torque"),
                                "pitch": sum(ps) / len(ps) if ps else None, "motor_on": all(ons) if ons else None,
                                "dirs": sorted({r["dir"] for r in rs if r["dir"]})}
    return out


def rotor_checks(v):
    """Per-rotor safety fields (one kRPC call per rotor): [{i, group, title, label, brake, torque, motor_on, rpm,
    rpm_limit, has_brake}]. Luke: 'Brake' must be 0 to spin, 'Torque Limit(%)' > 0, 'Motor' Engaged."""
    out = []
    for i, r in enumerate(layout(v)["rotors"]):
        m = r["mod"]
        try:
            f = fields(m)
            title = m.part.title
        except Exception:  # noqa: BLE001
            continue
        g = lambda *n, **kw: (lambda k: num(f.get(k)) if k else None)(find_field(f, *n, **kw))  # noqa: E731
        k_mot = find_field(f, "motor", exclude=("size", "output", "motorized"))
        out.append({"i": i, "group": r["group"], "title": title, "label": f"{r['group']} rotor ({title})",
                    "brake": g("brake", exclude=("auto",)), "torque": g("torque", "limit"),
                    "motor_on": _motor_on(f.get(k_mot)) if k_mot else None,
                    "rpm": g("current", "rpm"), "rpm_limit": g("rpm", "limit"),
                    "has_brake": find_field(f, "brake", exclude=("auto",)) is not None,
                    "dir": spin_dir(f), "invert": invert_on(f)})
    return out


def _set_bool(m, key, value):
    if key is None:
        return False
    fid = _field_id(m, key)
    try:
        if fid:
            m.set_field_bool_by_id(fid, bool(value))
        else:
            m.set_field_bool(key, bool(value))
        return True
    except Exception:  # noqa: BLE001
        try:
            (m.set_field_string_by_id if fid else m.set_field_string)(fid or key, "True" if value else "False")
            return True
        except Exception:  # noqa: BLE001
            return False


def set_spin_dir(m, want):
    """Set rotor Rotation Direction to want (+1 CW, -1 CCW). Returns True on success."""
    try:
        f = fields(m)
    except Exception:  # noqa: BLE001
        return False
    k = find_field(f, "direction", exclude=("deploy",)) or find_field(f, "counter")
    if not k or want not in (1, -1):
        return False
    if "counter" in str(k).lower() or k == "rotateCounterClockwise":
        return _set_bool(m, k, want < 0)
    # text field: Clockwise / Counterclockwise
    try:
        text = "Counterclockwise" if want < 0 else "Clockwise"
        fid = _field_id(m, k)
        if fid:
            m.set_field_string_by_id(fid, text)
        else:
            m.set_field_string(k, text)
        return True
    except Exception:  # noqa: BLE001
        return _set_bool(m, k, want < 0)


def set_invert_flag(m, want, deploy=False):
    """Set Invert Direction (rotor) or Invert Deploy Direction (blade)."""
    try:
        f = fields(m)
    except Exception:  # noqa: BLE001
        return False
    if deploy:
        k = find_field(f, "deploy", "invert") or find_field(f, "invert", "deploy")
    else:
        k = find_field(f, "invert", exclude=("deploy",))
    return _set_bool(m, k, bool(want))


def sense_snapshot(v):
    """Baseline rotor spin direction + invert, and blade deploy-invert, for wrong-way detection."""
    rotors = [{"i": c["i"], "dir": c.get("dir"), "invert": c.get("invert"), "label": c["label"]}
              for c in rotor_checks(v)]
    blades = []
    for j, b in enumerate(layout(v)["blades"]):
        try:
            inv = invert_on(fields(b["mod"]), deploy=True)
        except Exception:  # noqa: BLE001
            inv = None
        blades.append({"j": j, "rotor": b.get("rotor"), "invert": inv})
    return {"rotors": rotors, "blades": blades}


def remember_sense(v, force=False):
    """Store the craft's rotor/blade sense as the known-good baseline (once per vessel unless force)."""
    try:
        key = (str(v.name), len(v.parts.all))
    except Exception:  # noqa: BLE001
        key = None
    cur = STATE.get("sense")
    if not force and cur and cur.get("key") == key:
        return cur
    snap = sense_snapshot(v)
    snap["key"] = key
    STATE["sense"] = snap
    return snap


def sense_bad(ref, checks, blade_inverts=None):
    """Changes from the baseline sense -> [{i|j, label, kind dir|invert|blade, want, got}]."""
    out = []
    if not ref:
        return out
    by = {c["i"]: c for c in checks or []}
    for r in ref.get("rotors") or []:
        c = by.get(r["i"])
        if not c:
            continue
        if r.get("dir") is not None and c.get("dir") is not None and r["dir"] != c["dir"]:
            out.append({"i": r["i"], "label": r["label"], "kind": "dir", "want": r["dir"], "got": c["dir"]})
        if r.get("invert") is not None and c.get("invert") is not None and bool(r["invert"]) != bool(c["invert"]):
            out.append({"i": r["i"], "label": r["label"], "kind": "invert", "want": bool(r["invert"]),
                        "got": bool(c["invert"])})
    if blade_inverts is not None:
        for b, inv in zip(ref.get("blades") or [], blade_inverts):
            if b.get("invert") is not None and inv is not None and bool(b["invert"]) != bool(inv):
                out.append({"j": b["j"], "i": b.get("rotor"), "label": f"blade {b['j'] + 1}", "kind": "blade",
                            "want": bool(b["invert"]), "got": bool(inv)})
    return out


def fix_rotor_sense(v, bad):
    """Stop affected rotors, restore direction / invert / blade deploy-invert from the baseline, spin back up.
    -> short report."""
    if not bad:
        return "nothing to fix"
    lay = layout(v)
    idxs = {b["i"] for b in bad if b.get("i") is not None}
    if idxs:
        set_rotor(v, torque=0.0, rotors=idxs, flying=False)
        set_brake(v, 100.0, rotors=idxs)
    n = 0
    for b in bad:
        try:
            if b["kind"] == "dir" and b.get("i") is not None:
                n += bool(set_spin_dir(lay["rotors"][b["i"]]["mod"], b["want"]))
            elif b["kind"] == "invert" and b.get("i") is not None:
                n += bool(set_invert_flag(lay["rotors"][b["i"]]["mod"], b["want"]))
            elif b["kind"] == "blade" and b.get("j") is not None:
                n += bool(set_invert_flag(lay["blades"][b["j"]]["mod"], b["want"], deploy=True))
        except Exception:  # noqa: BLE001
            pass
    if idxs:
        set_brake(v, 0.0, rotors=idxs)
        set_rotor(v, rpm=RPM_MAX, torque=TORQUE_MAX, motor=True, rotors=idxs, flying=False)
    labs = [b["label"] for b in bad]
    labels = ", ".join(labs[:2]) + (f" +{len(labs) - 2}" if len(labs) > 2 else "")
    try:
        from . import emergency
        emergency.own_change()
    except Exception:  # noqa: BLE001
        pass
    reset_spool_state()
    return f"stopped, corrected ({n}), spinning back up: {labels}" if n else f"couldn't correct {labels}"


def group_sample(v, checks=None):
    """Light per-group sample for the emergency watcher: {group: {rpm, rpm_limit, motor_on, brake, torque}} (None = no
    props). A group's weakest rotor counts (one dead / braked rotor = that side is out)."""
    if not has_props(v):
        return None
    out = {}
    for c in checks if checks is not None else rotor_checks(v):
        d = out.setdefault(c["group"], {"rpm": None, "rpm_limit": None, "motor_on": None, "brake": None, "torque": None})
        for k, fn in (("rpm", min), ("rpm_limit", min), ("brake", max), ("torque", min)):
            if c[k] is not None:
                d[k] = c[k] if d[k] is None else fn(d[k], c[k])
        if c["motor_on"] is not None:
            d["motor_on"] = c["motor_on"] if d["motor_on"] is None else (d["motor_on"] and c["motor_on"])
    return out


def preflight(v, rotors=None, torque=TORQUE_MAX):
    """Pre-flight rotor checklist (Luke): every rotor's 'Brake' 0 (released), 'Torque Limit(%)' > 0 (set to torque if
    0), 'Motor' Engaged. Fixes what's wrong. -> (chat line, [problems found])."""
    log_fields(v)
    probs, n = [], 0
    lay = layout(v)["rotors"]
    for c in rotor_checks(v):
        if rotors is not None and c["i"] not in rotors:
            continue
        n += 1
        m = lay[c["i"]]["mod"]
        f = fields(m)
        if c["brake"] is not None and c["brake"] > 0:
            ok = _set_float(m, find_field(f, "brake", exclude=("auto",)), 0.0)
            probs.append(f"{c['label']} Brake was {c['brake']:.0f} -> " + ("released (0)" if ok else "COULDN'T release"))
        if c["torque"] is not None and c["torque"] < 1.0:
            ok = _set_float(m, find_field(f, "torque", "limit"), torque)
            probs.append(f"{c['label']} Torque Limit was 0 -> " + (f"{torque:.0f}%" if ok else "COULDN'T set"))
        if c["motor_on"] is False:
            ok = _set_motor(m, True)
            probs.append(f"{c['label']} Motor was disengaged -> " + ("Engaged" if ok else "COULDN'T engage"))
        if not c["has_brake"]:
            probs.append(f"{c['label']} has no 'Brake' field - can't check it")
    if not n:
        return "", []
    remember_sense(v)  # known-good spin direction / invert after the checklist
    line = ("Pre-flight: " + "; ".join(probs)) if probs else \
        f"Pre-flight: {n} rotor{'s' if n != 1 else ''} Brake 0, torque set, Motor Engaged - OK"
    (_log.warning if probs else _log.info)("props: %s", line)
    return line, probs


SPOOL_STEPS = (20.0, 40.0, 60.0, 80.0, 100.0)  # gradual Torque Limit(%) so BG rotors actually spin up
SPOOL_STEP_S = 1.0
SPOOL_WAIT_S = 45.0
SPOOL_FRAC = 0.9  # every rotor must reach this fraction of its RPM Limit before collective


def spool_up(v, rotors=None, rpm_target=None, frac=SPOOL_FRAC, wait_s=SPOOL_WAIT_S, stop_event=None, sleep=None):
    """Pre-takeoff spool for EVERY selected rotor (code path, not the model): Brake 0, Motor Engaged, RPM Limit set,
    Torque Limit stepped up to 100%, then wait until each rotor is at >= frac of its RPM Limit.
    -> (ok, report). Field names are discovered via find_field on each part module (never hard-coded GUI strings)."""
    try:
        from . import power_mgmt
        block = power_mgmt.rotor_spool_block(v)
        if block:
            return False, block
    except Exception:  # noqa: BLE001
        pass
    sleep = sleep or time.sleep
    reset_spool_state()
    idxs = set(rotors) if rotors is not None else None
    pre, probs = preflight(v, rotors=idxs)
    rpm_t = float(rpm_target if rpm_target is not None else RPM_MAX)
    set_brake(v, 0.0, rotors=idxs)
    set_rotor(v, rpm=rpm_t, motor=True, rotors=idxs, flying=False)
    set_rotor(v, torque=0.0, rotors=idxs, flying=False)  # start from idle torque, then ramp
    for tq in SPOOL_STEPS:
        if stop_event is not None and stop_event.is_set():
            return False, "spool-up cancelled"
        set_rotor(v, torque=tq, rotors=idxs, flying=False)
        sleep(SPOOL_STEP_S)
    t0 = time.time()
    last = {}
    prev_rpm = {}
    stable = {}
    while time.time() - t0 < wait_s:
        if stop_event is not None and stop_event.is_set():
            return False, "spool-up cancelled"
        # keep motor engaged / brake off every poll (saboteurs / sticky fields)
        set_brake(v, 0.0, rotors=idxs)
        set_rotor(v, rpm=rpm_t, torque=TORQUE_MAX, motor=True, rotors=idxs, flying=False)
        checks = [c for c in rotor_checks(v) if idxs is None or c["i"] in idxs]
        if not checks:
            return False, "spool-up: no rotors found"
        ready = []
        for c in checks:
            lim = float(c["rpm_limit"] or rpm_t)
            need = frac * lim
            rpm = float(c["rpm"] or 0.0)
            br = float(c.get("brake") or 0.0)
            tq = float(c.get("torque") or 0.0)
            mot = c.get("motor_on")
            last[c["label"]] = (rpm, need, mot, br, tq)
            fields_ok = mot is not False and br <= 0.5 and tq >= 5.0
            i = c["i"]
            prev = prev_rpm.get(i)
            climbing = prev is not None and rpm > prev + 0.5
            at_target = rpm >= need
            if at_target and fields_ok:
                stable[i] = stable.get(i, 0) + 1
            else:
                stable[i] = 0
            prev_rpm[i] = rpm
            ready.append(fields_ok and at_target and (climbing or stable.get(i, 0) >= 2))
        if all(ready):
            detail = ", ".join(f"{lab} {rpm:.0f}/{need:.0f}" for lab, (rpm, need, *_) in last.items())
            _log.info("props: spool-up OK in %.1f s (%s)", time.time() - t0, detail)
            try:
                from . import heli
                heli.STATE["spooled"] = True
            except Exception:  # noqa: BLE001
                pass
            return True, f"spool-up OK ({len(checks)} rotor{'s' if len(checks) != 1 else ''} >= {100 * frac:.0f}% RPM): {detail}"
        sleep(0.5)
    detail = "; ".join(
        f"{lab}: {rpm:.0f} RPM (need {need:.0f}), Motor {'ON' if mot else 'OFF'}, Brake {br}, Torque {tq}"
        for lab, (rpm, need, mot, br, tq) in last.items())
    _log.warning("props: spool-up FAILED after %.0f s - %s", wait_s, detail)
    return False, f"spool-up failed after {wait_s:.0f} s - {detail}"


def set_brake(v, value, rotors=None):
    """Rotor 'Brake' (0 = released) on the given rotor indices (all if None). -> count set.
    Also writes brakePercentage by id when Module.fields is broken (kRPC 0.6 BG rotors)."""
    n = 0
    for m in _rotor_mods(v, rotors=rotors):
        try:
            f = fields(m)
            k = find_field(f, "brake", exclude=("auto",))
            if k and _set_float(m, k, value, verify=True, tol=1.5, tries=5):
                n += 1
                continue
            # direct id fallback (live miss: Brake stuck at 100 when GUI map missed the field)
            if hasattr(m, "set_field_float_by_id"):
                try:
                    m.set_field_float_by_id("brakePercentage", float(value))
                    n += 1
                except Exception:  # noqa: BLE001
                    pass
        except Exception:  # noqa: BLE001
            pass
    return n


class PropGovernor:
    """Runs in the emergency watcher (1 s) while an autopilot is engaged and the craft has props: blade pitch follows
    the airspeed (AoA ~ AOA_OPT) every PITCH_EVERY_S, and in flight the torque limit mirrors the autopilot throttle
    (floored, never 0 in flight). Manual 'prop pitch N' / 'torque N' orders pause the matching part ('auto' resumes);
    a rollout reverse pauses the pitch schedule."""

    def __init__(self):
        self.t_pitch = self.t_torque = -1e9
        self.last_pitch = self.last_torque = None

    def tick(self, v, s, engaged, now):
        if not engaged or not has_props(v) or s.get("heli"):  # helicopters: heli.py flies the collective
            return None
        spd, sit = float(s.get("speed") or 0.0), s.get("sit")
        flying = sit == "flying"
        did = None
        if now - self.t_pitch >= PITCH_EVERY_S and STATE["manual_pitch"] is None and not STATE["reverse"]:
            self.t_pitch = now
            want = blade_pitch_for(spd, rotor_rpm(v), blade_radius(v))
            if self.last_pitch is None or abs(want - self.last_pitch) >= 1.0:
                self.last_pitch = want
                did = set_blades(v, pitch=want, flying=flying)
        if flying and now - self.t_torque >= TORQUE_EVERY_S and not STATE["manual_torque"]:
            self.t_torque = now
            want = flight_floor("torque", 100.0 * float(s.get("throttle") or 0.0))
            if self.last_torque is None or abs(want - self.last_torque) >= 5.0:
                self.last_torque = want
                did = (did + "; " if did else "") + set_rotor(v, torque=want, flying=True)
        return did


GOV = PropGovernor()