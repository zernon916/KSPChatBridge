"""Bounded, receive-time-stamped snapshots pushed by the in-game plugin.

kRPC object IDs are optional compatibility bindings, not stable game IDs.
The plugin obtains them locally without RPC and also sends game/session IDs.
"""
import copy
import math
import threading
import time

MAX_AGE = 2.5
_lock = threading.Lock()
_snapshot = None
_received = 0.0
_retired = set()


def reset():
    global _snapshot, _received
    with _lock:
        _snapshot, _received = None, 0.0
        _retired.clear()


def accept(data):
    global _snapshot, _received
    if not isinstance(data, dict) or data.get("version") != 1:
        raise ValueError("unsupported telemetry version")
    if not isinstance(data.get("session"), str) or not data["session"] or len(data["session"]) > 80:
        raise ValueError("invalid session")
    if type(data.get("sequence")) is not int or data["sequence"] < 0:
        raise ValueError("invalid sequence")
    if not isinstance(data.get("vessel_id"), str):
        raise ValueError("invalid vessel")
    rows = data.get("rotors")
    if not isinstance(rows, list) or len(rows) > 256:
        raise ValueError("invalid rotor list")
    ids = set()
    for row in rows:
        if not isinstance(row, dict) or not isinstance(row.get("part_id"), str) or row["part_id"] in ids:
            raise ValueError("invalid/duplicate rotor identity")
        ids.add(row["part_id"])
        for key in ("rpm", "rpm_limit", "torque", "brake"):
            value = row.get(key)
            if value is not None and (type(value) not in (int, float) or not math.isfinite(value) or value < 0):
                raise ValueError("invalid rotor measurement")
        if row.get("motor_on") is not None and type(row["motor_on"]) is not bool:
            raise ValueError("invalid motor state")
    with _lock:
        if data["session"] in _retired:
            raise ValueError("retired session")
        if _snapshot:
            if data["session"] == _snapshot["session"]:
                if data["sequence"] <= _snapshot["sequence"]:
                    raise ValueError("out-of-order snapshot")
            else:
                if len(_retired) >= 64:
                    raise ValueError("too many session changes; restart bridge")
                _retired.add(_snapshot["session"])
        _snapshot, _received = copy.deepcopy(data), time.monotonic()


def rotor(vessel, part):
    """(producer_seen, fresh matching row). Once seen, stale never falls back."""
    with _lock:
        if _snapshot is None:
            return False, None
        if time.monotonic() - _received > MAX_AGE:
            return True, None
        vid = str(getattr(vessel, "_object_id", ""))
        pid = str(getattr(part, "_object_id", ""))
        if not vid or vid == "0" or vid != _snapshot.get("rpc_vessel_id"):
            return True, None
        for row in _snapshot["rotors"]:
            if pid and pid != "0" and row.get("rpc_part_id") == pid:
                return True, dict(row, sample=(_snapshot["session"], _snapshot["sequence"]))
        return True, None
