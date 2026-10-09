"""Low electric charge: deploy solar, start generators, shed loads; block rotor spool when too low."""
import logging
import random
import time

log = logging.getLogger("kspchat")

LOW_EC_PCT = 25.0
ROTOR_MIN_EC_PCT = 25.0
SOLAR_MAX_Q = 12000.0   # Pa: skip fragile panels in heavy dynamic pressure
SOLAR_MAX_SPEED = 220.0  # m/s in atmosphere
COOLDOWN_S = 90.0

_last_line = 0.0

LINES = [
    "Battery's in the red - flipping on every generator I can find and killing the disco lights!",
    "We're sucking fumes on electric charge - solar out if it's safe, and the non-essentials are going dark.",
    "Charge is critical - I'll take whatever juice the fuel cells and panels can give us.",
]


def ec_pct(v):
    try:
        mx = v.resources.max("ElectricCharge")
        if mx <= 0:
            return 100.0
        return 100.0 * v.resources.amount("ElectricCharge") / mx
    except Exception:  # noqa: BLE001
        return 100.0


def rotor_spool_block(v):
    pct = ec_pct(v)
    if pct < ROTOR_MIN_EC_PCT:
        return (f"No juice to the rotors! Electric charge {pct:.0f}% - need at least {ROTOR_MIN_EC_PCT:.0f}% "
                "before spool-up.")
    return None


def _safe_solar(sample):
    if not sample:
        return True
    sit = sample.get("sit") or ""
    if sit not in ("flying", "flying_low", "flying_high"):
        return True
    spd = float(sample.get("speed") or 0.0)
    q = sample.get("q")
    if q is not None and float(q) > SOLAR_MAX_Q:
        return False
    return spd <= SOLAR_MAX_SPEED


def _start_power_modules(v):
    n = 0
    try:
        for part in v.parts.all:
            for mod in part.modules:
                for ev in list(getattr(mod, "events", []) or []):
                    el = str(ev).lower()
                    if "start" not in el:
                        continue
                    if any(w in el for w in ("fuel", "cell", "generator", "reactor")):
                        try:
                            mod.trigger_event(ev)
                            n += 1
                        except Exception:  # noqa: BLE001
                            pass
                for act in list(getattr(mod, "actions", []) or []):
                    al = str(act).lower()
                    if "start" in al and any(w in al for w in ("fuel", "cell", "generator")):
                        try:
                            mod.set_action(act, True)
                            n += 1
                        except Exception:  # noqa: BLE001
                            pass
    except Exception:  # noqa: BLE001
        pass
    return n


def low_ec_tick(v, sample=None):
    """Act on low EC; return an in-character line (rate-limited) or None."""
    global _last_line
    pct = ec_pct(v)
    if pct >= LOW_EC_PCT:
        return None
    acts = []
    try:
        from . import emergency
        emergency.own_change()
        if v.control.lights:
            v.control.lights = False
            acts.append("lights")
        rws = list(v.parts.reaction_wheels)
        for w in rws[1:]:
            try:
                if w.active:
                    w.active = False
                    acts.append("reaction wheels")
            except Exception:  # noqa: BLE001
                pass
        if _safe_solar(sample):
            for p in v.parts.solar_panels:
                try:
                    if not p.deployed:
                        p.deployed = True
                        acts.append("solar")
                except Exception:  # noqa: BLE001
                    pass
        if _start_power_modules(v):
            acts.append("generators")
    except Exception as ex:  # noqa: BLE001
        log.debug("power_mgmt: %s", ex)
        return None
    if not acts:
        return None
    now = time.time()
    if now - _last_line < COOLDOWN_S:
        return None
    _last_line = now
    log.info("power_mgmt: EC %.0f%% — %s", pct, ", ".join(sorted(set(acts))))
    return random.choice(LINES)
