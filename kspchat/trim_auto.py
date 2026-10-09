"""Auto-trim in level cruise: deploy angles + pitch_trim so the autopilot needs less elevator input."""
import logging
import math
import time

from . import craft_notes, emergency

log = logging.getLogger("kspchat")

# re-trim when cruise conditions drift
SPD_DELTA = 25.0       # m/s
ALT_DELTA = 400.0      # m
FUEL_FRAC_DELTA = 0.08
TRIM_INTERVAL = 8.0    # s between nudges in hold loop
DEPLOY_STEP = 0.35     # deg per nudge per surface
TRIM_STEP = 0.015      # pitch_trim fraction per nudge
DEPLOY_CLAMP = 12.0    # max |deploy angle| per surface from auto-trim

_ctx = {"t": 0.0, "spd": None, "alt": None, "fuel_frac": None, "name": None,
        "watch_vs": None, "watch_pitch": None, "worse": 0}
_surf_snap = {}  # vessel name -> [{title, deploy_angle, deployed, pitch/yaw/roll, inverted, authority}]

_AUTO_TRIM_DEFAULT = {"master": True, "pitch": True, "roll": True, "yaw": True, "rotor": True}


def auto_trim_settings():
    """Runtime auto-trim toggles from bridge_settings.json ('auto_trim_enabled')."""
    from . import settings
    raw = settings.get("auto_trim_enabled")
    out = dict(_AUTO_TRIM_DEFAULT)
    if raw is None:
        return out
    if isinstance(raw, bool):
        return {k: bool(raw) for k in out}
    if isinstance(raw, dict):
        for k in out:
            if k in raw:
                out[k] = bool(raw[k])
    return out


def set_auto_trim(key, enabled):
    st = auto_trim_settings()
    if key not in st:
        raise ValueError(f"unknown auto-trim key {key!r}")
    st[key] = bool(enabled)
    from . import settings
    settings.put("auto_trim_enabled", st)


def auto_trim_active(axis):
    """axis: pitch | roll | yaw | rotor (master off disables all)."""
    st = auto_trim_settings()
    if not st.get("master", True):
        return False
    return bool(st.get(axis, True))


def _mod(cs):
    try:
        return next(m for m in cs.part.modules if m.name == "ModuleControlSurface")
    except Exception:
        return None


def _deploy_key(mod):
    try:
        for k in mod.fields:
            if "deploy" in k.lower() and "angle" in k.lower():
                return k
    except Exception:
        pass
    return None


def get_deploy_angle(mod):
    key = _deploy_key(mod)
    if not key:
        return None
    try:
        return float(str(mod.get_field(key)).replace(",", "."))
    except Exception:
        return None


def set_deploy_angle(mod, cs, deg):
    key = _deploy_key(mod)
    if not key:
        return False
    try:
        mod.set_field_float(key, float(deg))
    except Exception:
        try:
            mod.set_field(key, str(float(deg)))
        except Exception:
            return False
    try:
        if abs(float(deg)) > 0.05:
            cs.deployed = True
    except Exception:
        pass
    emergency.own_change()
    return True


def _trim_surface_sign(vessel, cs):
    """+1 if increasing deploy angle adds nose-up pitch (conventional rear elevator)."""
    sign = 1.0
    try:
        y = float(cs.part.position(vessel.reference_frame)[1])
        if y > 0.05:  # ahead of CoM (canard / foreplane)
            sign = -1.0
    except Exception:
        pass
    try:
        if cs.inverted:
            sign *= -1.0
    except Exception:
        pass
    mod = _mod(cs)
    if mod is not None:
        try:
            from . import propulsion as pr
            if pr.invert_on(pr.fields(mod), deploy=True):
                sign *= -1.0
        except Exception:
            pass
    return sign


def _control_surfaces(v):
    """Non-blade control surfaces in stable order (matches emergency snapshot)."""
    from . import propulsion as pr
    try:
        surfs = list(v.parts.control_surfaces)
    except Exception:
        return []
    out = []
    for cs in surfs:
        try:
            if pr.is_blade(cs.part.title):
                continue
            out.append(cs)
        except Exception:
            out.append(cs)
    return out


def capture_surface_snapshot(v):
    """Remember deploy angles and authority before auto-trim (hold engage / takeoff)."""
    name = craft_notes.vessel_name(v)
    if not craft_notes.name_ok(name):
        return
    rows = []
    for cs in _control_surfaces(v):
        mod = _mod(cs)
        ang = get_deploy_angle(mod) if mod else None
        try:
            rows.append({
                "title": cs.part.title,
                "deploy_angle": float(ang) if ang is not None else 0.0,
                "deployed": bool(cs.deployed),
                "pitch_enabled": bool(cs.pitch_enabled),
                "yaw_enabled": bool(cs.yaw_enabled),
                "roll_enabled": bool(cs.roll_enabled),
                "inverted": bool(cs.inverted),
                "authority_limiter": float(cs.authority_limiter),
            })
        except Exception:
            pass
    if rows:
        _surf_snap[name] = rows
        log.debug("trim: captured %d surface(s) for %s", len(rows), name)


def restore_surface_snapshot(v):
    """Restore every control surface from the pre-trim snapshot; hand authority back."""
    name = craft_notes.vessel_name(v)
    rows = _surf_snap.get(name)
    if not rows:
        try:
            ref = emergency.DET.ref_cfg if emergency.DET else None
        except Exception:
            ref = None
        refs = (ref or {}).get("surfaces") or []
        surfs = _control_surfaces(v)
        if len(refs) == len(surfs):
            n = 0
            for cs, r0 in zip(surfs, refs):
                r = list(r0) + [0.0] * 9
                mod = _mod(cs)
                if mod and len(r) > 8 and r[8] is not None:
                    set_deploy_angle(mod, cs, float(r[8]))
                try:
                    cs.deployed = bool(r[6])
                    cs.inverted = bool(r[4])
                    cs.authority_limiter = float(r[5])
                    cs.pitch_enabled, cs.yaw_enabled, cs.roll_enabled = bool(r[1]), bool(r[2]), bool(r[3])
                    n += 1
                except Exception:
                    pass
            if n:
                emergency.own_change()
                return n
        return 0
    surfs = _control_surfaces(v)
    if len(surfs) != len(rows):
        return 0
    n = 0
    for cs, r in zip(surfs, rows):
        mod = _mod(cs)
        if mod is not None:
            set_deploy_angle(mod, cs, float(r["deploy_angle"]))
        try:
            cs.deployed = bool(r["deployed"])
            cs.inverted = bool(r["inverted"])
            cs.authority_limiter = float(r["authority_limiter"])
            cs.pitch_enabled = bool(r["pitch_enabled"])
            cs.yaw_enabled = bool(r["yaw_enabled"])
            cs.roll_enabled = bool(r["roll_enabled"])
            n += 1
        except Exception:
            pass
    if n:
        emergency.own_change()
    return n


def _level_getting_worse(vs, pitch):
    """Stop auto-trim when vertical speed or pitch keeps moving away from level."""
    vs, pitch = float(vs), float(pitch)
    w = int(_ctx.get("worse") or 0)
    prev_vs = _ctx.get("watch_vs")
    prev_pitch = _ctx.get("watch_pitch")
    if prev_vs is not None and abs(vs) > abs(prev_vs) + 0.2 and abs(vs) > 0.8:
        w += 1
    if prev_pitch is not None and pitch < prev_pitch - 0.35 and pitch < 2.0:
        w += 1
    if prev_vs is not None and abs(vs) <= abs(prev_vs) - 0.15:
        w = max(0, w - 1)
    _ctx["watch_vs"] = vs
    _ctx["watch_pitch"] = pitch
    _ctx["worse"] = w
    return w >= 2


def pitch_authority_surfaces(vessel):
    """Control surfaces that trim pitch (not roll-only flaps — inverse of plane.Flaps)."""
    out = []
    try:
        surfs = list(vessel.parts.control_surfaces)
    except Exception:
        return out
    for cs in surfs:
        mod = _mod(cs)
        if mod is None:
            continue
        try:
            titled = "flap" in cs.part.title.lower()
            marked = (str(mod.get_field("Deploy")).lower() == "true"
                      and not (cs.pitch_enabled or cs.yaw_enabled or cs.roll_enabled))
            if titled or marked:
                continue
            if cs.pitch_enabled or (cs.yaw_enabled and not cs.roll_enabled):
                out.append((cs, mod))
            elif cs.pitch_enabled is False and cs.roll_enabled and not cs.yaw_enabled:
                continue
            elif cs.pitch_enabled or cs.yaw_enabled or cs.roll_enabled:
                if cs.pitch_enabled:
                    out.append((cs, mod))
        except Exception:
            pass
    return out


def mean_elevator_deploy(vessel):
    surfs = pitch_authority_surfaces(vessel)
    if not surfs:
        return None
    vals = [a for _, m in surfs if (a := get_deploy_angle(m)) is not None]
    return sum(vals) / len(vals) if vals else None


def set_mean_elevator_deploy(vessel, target):
    surfs = pitch_authority_surfaces(vessel)
    if not surfs:
        return 0
    n = 0
    for cs, mod in surfs:
        if set_deploy_angle(mod, cs, target):
            n += 1
    return n


def _fuel_frac(v):
    try:
        tot = rem = 0.0
        for r in v.resources.all:
            if "Fuel" not in r.name and "Oxidizer" not in r.name:
                continue
            tot += float(r.max)
            rem += float(r.amount)
        return rem / tot if tot > 0 else None
    except Exception:
        return None


def steady_cruise(tgt, vs, roll, spd, climbing, pitch_cmd):
    if pitch_cmd is not None or climbing:
        return False
    if abs(float(vs)) >= 1.0 or abs(float(roll)) >= 5.0:
        return False
    spd_t = tgt.get("speed")
    if spd_t is not None and abs(float(spd) - float(spd_t)) > 20.0:
        return False
    if float(spd) < 25.0:
        return False
    return True


def _context_changed(spd, alt, fuel_frac):
    if _ctx["spd"] is None:
        return True
    if abs(float(spd) - float(_ctx["spd"])) >= SPD_DELTA:
        return True
    if _ctx["alt"] is not None and abs(float(alt) - float(_ctx["alt"])) >= ALT_DELTA:
        return True
    if fuel_frac is not None and _ctx["fuel_frac"] is not None:
        if abs(float(fuel_frac) - float(_ctx["fuel_frac"])) >= FUEL_FRAC_DELTA:
            return True
    return False


def _remember(spd, alt, fuel_frac, name):
    _ctx.update(spd=float(spd), alt=float(alt), fuel_frac=fuel_frac, name=name, t=time.time())


def nudge_trim(v, ctl, *, force=False):
    """One auto-trim step from current control input. Returns a short status string or ''."""
    name = craft_notes.vessel_name(v)
    if not craft_notes.name_ok(name):
        return ""
    surfs = pitch_authority_surfaces(v)
    if not surfs:
        return ""
    try:
        pitch_in = float(ctl.pitch)
    except Exception:
        return ""
    if not force and abs(pitch_in) < 0.04:
        return ""
    base = math.copysign(1.0, pitch_in) if pitch_in else 0.0
    step = min(DEPLOY_STEP, abs(pitch_in) * 3.0)
    n = 0
    deltas = []
    for cs, mod in surfs:
        ang = get_deploy_angle(mod)
        if ang is None:
            ang = 0.0
        delta = base * _trim_surface_sign(v, cs) * step
        new_ang = max(-DEPLOY_CLAMP, min(DEPLOY_CLAMP, ang + delta))
        if set_deploy_angle(mod, cs, new_ang):
            n += 1
            deltas.append(delta)
    trim_note = ""
    try:
        cur = float(ctl.pitch_trim)
        new = max(-1.0, min(1.0, cur + base * min(TRIM_STEP, abs(pitch_in) * 0.05)))
        if abs(new - cur) > 1e-4:
            ctl.pitch_trim = new
            trim_note = f", pitch trim {100 * new:+.0f}%"
    except AttributeError:
        pass
    emergency.own_change()
    if n:
        dmean = sum(deltas) / len(deltas) if deltas else 0.0
        log.info("auto-trim: %s nudged deploy ~%+.2f deg (pitch in %.2f)%s", name, dmean, pitch_in, trim_note)
        return f"Auto-trim: {n} surface(s) ~{dmean:+.2f} deg{trim_note}."
    return ""


def maybe_trim_hold(v, ctl, tgt, vs, roll, spd, alt, climbing, pitch_cmd, pitch=None, now=None):
    """Called from hold.py ~every loop when in level cruise; rate-limited."""
    now = now or time.time()
    if not auto_trim_active("pitch"):
        return ""
    if not steady_cruise(tgt, vs, roll, spd, climbing, pitch_cmd):
        return ""
    if pitch is not None and _level_getting_worse(vs, pitch):
        return ""
    fuel = _fuel_frac(v)
    name = craft_notes.vessel_name(v)
    if name != _ctx.get("name"):
        _remember(spd, alt, fuel, name)
    if now - float(_ctx.get("t") or 0) < TRIM_INTERVAL and not _context_changed(spd, alt, fuel):
        return ""
    if _context_changed(spd, alt, fuel):
        _remember(spd, alt, fuel, name)
    msg = nudge_trim(v, ctl)
    if msg:
        _ctx["t"] = now
    return msg


def auto_trim_now():
    """Force one auto-trim step (Trim UI / tool)."""
    if not auto_trim_active("pitch"):
        return "Auto-trim is off (master or pitch disabled in Trim panel)."
    from . import ksp_actions
    v = ksp_actions._vessel()
    msg = nudge_trim(v, v.control, force=True)
    return msg or "Auto-trim: already near neutral (pitch input small)."


def snapshot_state(v):
    """Current trim values for get_trim_state."""
    ctl = v.control
    st = {"vessel": craft_notes.vessel_name(v), "elevator_deploy": mean_elevator_deploy(v)}
    for attr in ("pitch_trim", "roll_trim", "yaw_trim"):
        try:
            st[attr] = float(getattr(ctl, attr))
        except (AttributeError, TypeError, ValueError):
            st[attr] = None
    notes = craft_notes.load_for_vessel(v)
    if notes:
        st["saved"] = {k: notes.get(k) for k in craft_notes._FIELDS if k in notes}
    tgt = None
    try:
        from . import hold, settings
        if hold.active():
            tgt = settings.get("plane_hold") or {}
    except Exception:
        pass
    if tgt and tgt.get("speed") is not None:
        st["cruise_speed"] = float(tgt["speed"])
    return st


def save_to_craft_notes(v=None):
    """Persist current trim + cruise hints to craft_notes.json."""
    from . import ksp_actions, hold, settings
    v = v or ksp_actions._vessel()
    name = craft_notes.vessel_name(v)
    if not craft_notes.name_ok(name):
        return f"Won't save notes for default/empty craft name ({name!r}). Rename the vessel in the editor first."
    st = snapshot_state(v)
    fields = {}
    for k in ("pitch_trim", "roll_trim", "yaw_trim", "elevator_deploy"):
        if st.get(k) is not None:
            fields[k] = st[k]
    tgt = settings.get("plane_hold") or {} if hold.active() else {}
    if tgt.get("speed") is not None:
        fields["cruise_speed"] = float(tgt["speed"])
    rot = None
    try:
        from . import propulsion
        if propulsion.classify(v)["rotors"]:
            samp = propulsion.group_sample(v, propulsion.rotor_checks(v))
            if samp:
                rot = {"collective": samp.get("pitch"), "blade_pitch": samp.get("pitch")}
                fields["rotor"] = rot
    except Exception:
        pass
    craft_notes.save(name, **fields)
    bits = [f"pitch {100 * fields['pitch_trim']:+.0f}%" if "pitch_trim" in fields else "",
            f"elevator {fields['elevator_deploy']:.1f}°" if isinstance(fields.get("elevator_deploy"), (int, float)) else ""]
    bits = [b for b in bits if b]
    return f"Saved craft notes for {name}" + (f" ({', '.join(bits)})." if bits else ".")


def reset_vessel_context():
    _ctx.update(spd=None, alt=None, fuel_frac=None, name=None, t=0.0, heli_t=0.0, yaw_bias=0.0,
                 watch_vs=None, watch_pitch=None, worse=0)


def maybe_trim_heli(v, info, coll, yaw_rate, vs, roll, spd, now=None):
    """§10: in a settled hover / slow cruise, bias collective and counter-rotating torque; save to craft_notes."""
    now = now or time.time()
    if not auto_trim_active("rotor"):
        return ""
    if abs(float(vs)) > 0.8 or abs(float(roll)) > 8.0:
        return ""
    if float(spd) > 25.0:  # translating fast — leave the yaw/tilt loops alone
        return ""
    if now - float(_ctx.get("heli_t") or 0) < TRIM_INTERVAL:
        return ""
    name = craft_notes.vessel_name(v)
    if not craft_notes.name_ok(name):
        return ""
    from . import propulsion as pr
    lift = set(info.get("lift") or ())
    if not lift:
        return ""
    notes = []
    # collective bias: if sinking with low collective or climbing with high, nudge toward hover
    try:
        coll_f = float(coll)
        bias = 0.0
        if vs < -0.25 and coll_f < 12.0:
            bias = 0.4
        elif vs > 0.35 and coll_f > 2.0:
            bias = -0.3
        if abs(bias) > 0.05:
            pr.set_blades(v, pitch=coll_f + bias, rotors=lift)
            notes.append(f"collective {coll_f + bias:.1f}°")
            emergency.own_change()
    except Exception:  # noqa: BLE001
        pass
    # yaw: if spinning while hover, bias torque on counter-rotating multirotor / coaxial
    yaw_bias = float(_ctx.get("yaw_bias") or 0.0)
    if info.get("counter") and len(lift) >= 2 and abs(float(yaw_rate)) > 3.0:
        yaw_bias = max(-8.0, min(8.0, yaw_bias - 0.5 * math.copysign(1.0, yaw_rate)))
        _ctx["yaw_bias"] = yaw_bias
        try:
            lay = pr.layout(v)["rotors"]
            for i in lift:
                if i >= len(lay):
                    continue
                d = lay[i].get("dir") or 1
                pr.set_rotor(v, torque=90.0 + yaw_bias * d, rotors={i}, flying=True)
            notes.append(f"yaw torque bias {yaw_bias:+.1f}")
            emergency.own_change()
        except Exception:  # noqa: BLE001
            pass
    _ctx["heli_t"] = now
    if not notes:
        return ""
    craft_notes.save(name, rotor={"collective": float(coll), "yaw_torque_bias": yaw_bias,
                                  "blade_pitch": float(coll)})
    log.info("heli auto-trim: %s %s", name, ", ".join(notes))
    return "Rotor auto-trim: " + ", ".join(notes) + "."
