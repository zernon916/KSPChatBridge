"""Tiny shared runtime settings store (bridge_settings.json): science_mode, ai_name, ..."""
import json
import threading

from . import config

_lock = threading.Lock()


def get(key, default=None):
    try:
        return json.loads(config.SETTINGS_FILE.read_text(encoding="utf-8")).get(key, default)
    except Exception:
        return default


def put(key, value):
    with _lock:
        try:
            data = json.loads(config.SETTINGS_FILE.read_text(encoding="utf-8"))
        except Exception:
            data = {}
        data[key] = value
        config.SETTINGS_FILE.write_text(json.dumps(data, indent=1), encoding="utf-8")


# Autopilot / bridge toggles exposed via GET/POST /setting (numeric values stored as JSON numbers).
SETTING_KEYS = frozenset({
    "altitude_band_m",
    "autoland_reversers",
    "rotor_brake_park",
    "mayday_lights",
})


def get_setting(key, default=None):
    if key not in SETTING_KEYS:
        return None
    return get(key, default)


def put_setting(key, value):
    if key not in SETTING_KEYS:
        raise ValueError(f"setting '{key}' not allowed")
    if key == "altitude_band_m":
        from . import alt_hold
        value = alt_hold.set_band_m(value)
    elif key in ("autoland_reversers", "rotor_brake_park", "mayday_lights"):
        value = bool(str(value).lower() in ("1", "true", "on", "yes"))
    put(key, value)
    return value
