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


def _rpc_id(obj):
    return str(getattr(obj, "_object_id", "") or "")


def _game_id(obj, *attrs):
    for name in attrs:
        value = getattr(obj, name, None)
        if value is not None:
            text = str(value)
            if text and text != "0":
                return text
    return ""


def _vessel_game_id(vessel):
    return _game_id(vessel, "id", "vessel_id")


def _part_game_id(part):
    return _game_id(part, "flight_id", "flightID", "id")


def _vessel_matches(vessel, snap):
    rpc = _rpc_id(vessel)
    snap_rpc = str(snap.get("rpc_vessel_id") or "")
    snap_vid = str(snap.get("vessel_id") or "")
    if snap_rpc and rpc and rpc != "0" and rpc == snap_rpc:
        return True
    game = _vessel_game_id(vessel)
    if snap_vid and game and game == snap_vid:
        return True
    return False


def _part_matches(part, row):
    rpc = _rpc_id(part)
    snap_rpc = str(row.get("rpc_part_id") or "")
    snap_pid = str(row.get("part_id") or "")
    if snap_rpc and rpc and rpc != "0" and rpc == snap_rpc:
        return True
    game = _part_game_id(part)
    if snap_pid and game and game == snap_pid:
        return True
    if snap_pid and not snap_rpc and rpc and rpc != "0" and rpc == snap_pid:
        return True
    return False


def _stale():
    return time.monotonic() - _received > MAX_AGE


def producer_active(vessel):
    """Fresh snapshot for this vessel (telemetry producer seen recently)."""
    with _lock:
        if _snapshot is None or _stale():
            return False
        return _vessel_matches(vessel, _snapshot)


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
    state_data = data.get("state")
    if state_data is not None:
        if not isinstance(state_data, dict) or not isinstance(state_data.get("resources", {}), dict):
            raise ValueError("invalid vessel state")
        values = list(state_data.get("resources", {}).values())
        values += [state_data.get(k) for k in ("temperature", "altitude", "agl", "speed", "vs", "g")]
        if any(x is not None and (type(x) not in (int, float) or not math.isfinite(x)) for x in values):
            raise ValueError("invalid vessel measurement")
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
    """(producer_seen, fresh matching row). Stale snapshots never fall back to kRPC fields."""
    with _lock:
        if _snapshot is None:
            return False, None
        if _stale():
            return True, None
        if not _vessel_matches(vessel, _snapshot):
            return False, None
        for row in _snapshot["rotors"]:
            if _part_matches(part, row):
                return True, dict(row, sample=(_snapshot["session"], _snapshot["sequence"]))
        if _snapshot["rotors"]:
            return False, None
        return True, None


def state(vessel):
    """Fresh local measurements, or None when unavailable (never another vessel)."""
    with _lock:
        if _snapshot is None or _stale() or not _vessel_matches(vessel, _snapshot):
            return None
        return copy.deepcopy(_snapshot.get("state"))
