"""Per-craft trim and cruise notes (PluginData/craft_notes.json), keyed by vessel name."""
import json
import threading
import time

from . import config

_lock = threading.Lock()
DEFAULT_NAME = "Untitled Space Craft"
_FIELDS = (
    "pitch_trim", "roll_trim", "yaw_trim", "elevator_deploy", "rotation_speed", "cruise_speed", "quirks", "rotor",
    "updated",
)


def _read():
    try:
        raw = json.loads(config.CRAFT_NOTES_FILE.read_text(encoding="utf-8"))
        return raw if isinstance(raw, dict) else {}
    except Exception:
        return {}


def _write(data):
    config.CRAFT_NOTES_FILE.write_text(json.dumps(data, indent=1, sort_keys=True), encoding="utf-8")


def vessel_name(v):
    try:
        return str(v.name or "").strip()
    except Exception:
        return ""


def name_ok(name):
    n = " ".join(str(name or "").split())
    return bool(n) and n != DEFAULT_NAME


def load(name):
    """Notes dict for `name`, or {} if missing / default-named."""
    if not name_ok(name):
        return {}
    with _lock:
        return dict(_read().get(name) or {})


def merge(name, fields):
    """Merge fields into the stored record; returns the merged dict (does not save if name invalid)."""
    if not name_ok(name):
        return {}
    clean = {k: v for k, v in fields.items() if k in _FIELDS and v is not None}
    if not clean:
        return load(name)
    with _lock:
        data = _read()
        rec = dict(data.get(name) or {})
        rec.update(clean)
        rec["updated"] = time.time()
        data[name] = rec
        _write(data)
        return dict(rec)


def save(name, **fields):
    """Replace known fields on the record (merge semantics)."""
    return merge(name, fields)


def list_names():
    """Craft names that have saved notes."""
    with _lock:
        return sorted(_read().keys())


def load_for_vessel(v):
    """Notes for this vessel, or {}."""
    return load(vessel_name(v))


def apply_on_engage(v):
    """Apply saved pitch/roll/yaw trim and mean elevator deploy when the holds engage or the vessel changes."""
    notes = load_for_vessel(v)
    if not notes:
        return False
    ctl = v.control
    applied = False
    for attr, key in (("pitch_trim", "pitch_trim"), ("roll_trim", "roll_trim"), ("yaw_trim", "yaw_trim")):
        if key not in notes:
            continue
        try:
            setattr(ctl, attr, float(notes[key]))
            applied = True
        except (AttributeError, TypeError, ValueError):
            pass
    dep = notes.get("elevator_deploy")
    if dep is not None:
        try:
            from . import trim_auto
            trim_auto.set_mean_elevator_deploy(v, float(dep))
            applied = True
        except Exception:
            pass
    return applied
