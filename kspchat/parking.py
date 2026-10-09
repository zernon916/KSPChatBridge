"""Parking brake: wheeled craft on the ground stay braked until takeoff roll, taxi, or player brakes off."""
import logging

log = logging.getLogger("kspchat")

STATE = {"vid": None, "sit": None, "released": False, "announced": False}


def has_wheels(v):
    try:
        return len(v.parts.wheels) > 0
    except Exception:  # noqa: BLE001
        return False


def _exempt():
    try:
        from . import hold, plane, taxi
        if hold.active() and hold.STATUS.get("phase") == "takeoff":
            return True
        if taxi.active():
            return True
        if plane.active():
            ph = str(plane.STATUS.get("phase") or "")
            if ph.startswith("rollout") or ph in ("takeoff", "flare", "final"):
                return True
    except Exception:  # noqa: BLE001
        pass
    return False


def on_vessel_switch(vid):
    STATE.update(vid=vid, released=False, announced=False)


def release(reason=""):
    STATE["released"] = True
    if reason:
        log.info("parking: released (%s)", reason)


def note_player_brakes(on):
    if not on:
        release("player brakes off")


def tick(v, sit):
    """Apply parking brake when appropriate; returns a one-line crew notice or None."""
    if v is None:
        return None
    vid = getattr(v, "_object_id", None) or v.name
    if STATE["vid"] != vid:
        on_vessel_switch(vid)
    prev = STATE.get("sit")
    STATE["sit"] = sit
    if prev == "flying" and sit in ("landed", "pre_launch", "splashed"):
        STATE["released"] = False
        STATE["announced"] = False
    if not has_wheels(v) or sit not in ("landed", "pre_launch", "splashed"):
        return None
    if STATE["released"] or _exempt():
        return None
    try:
        if v.control.brakes:
            return None
        from . import emergency
        emergency.own_change()
        v.control.brakes = True
        if not STATE["announced"]:
            STATE["announced"] = True
            log.info("parking: brakes on (%s, %s)", v.name, sit)
            return "Parking brake set - she isn't rolling anywhere on my watch."
    except Exception as ex:  # noqa: BLE001
        log.debug("parking: %s", ex)
    return None
