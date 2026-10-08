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
