"""Altitude hold band: target ± band counts as on-target (default 150 m). Used by hold.py and flightplan."""
import math

DEFAULT_BAND_M = 150.0
BAND_KEY = "altitude_band_m"
BAND_MIN, BAND_MAX = 10.0, 500.0


def band_m():
    from . import settings
    v = settings.get(BAND_KEY)
    if v is None:
        return DEFAULT_BAND_M
    try:
        return max(BAND_MIN, min(BAND_MAX, float(v)))
    except (TypeError, ValueError):
        return DEFAULT_BAND_M


def set_band_m(value):
    from . import settings
    b = max(BAND_MIN, min(BAND_MAX, float(value)))
    settings.put(BAND_KEY, b)
    return b


def in_band(alt, target, band=None):
    """alt and target in the same frame (AGL or MSL)."""
    b = band if band is not None else band_m()
    return abs(float(target) - float(alt)) <= b


def _clamp(x, lo, hi):
    return max(lo, min(hi, x))


def vertical_speed_target(alt_msl, alt_des_msl, vs_up, vs_dn, alt_vs_k, kin, state, band=None):
    """Ease V/S toward 0 entering the band; hold capture altitude inside; chase target only outside.

    state: mutable dict; uses key 'capture_msl' for the altitude locked when the band was entered.
    Returns (vs_des, inside_band).
    """
    b = band if band is not None else band_m()
    err = float(alt_des_msl) - float(alt_msl)
    if abs(err) <= b:
        if state.get("capture_msl") is None:
            state["capture_msl"] = float(alt_msl)
        cap = float(state["capture_msl"])
        if abs(err) <= b * 0.9:
            return 0.0, True
        return _clamp(alt_vs_k * kin * (cap - alt_msl), -3.0, 3.0), True
    state["capture_msl"] = None
    soften = 1.0
    outer = b * 2.0
    if abs(err) < outer:
        soften = max(0.0, (abs(err) - b) / max(outer - b, 1.0))
    vs_full = _clamp(alt_vs_k * kin * err, -float(vs_dn), float(vs_up))
    return vs_full * soften, False


def approach_easing_fraction(alt_msl, alt_des_msl, band=None):
    """0 at the band edge, 1 at 2× band or farther (for tests)."""
    b = band if band is not None else band_m()
    err = abs(float(alt_des_msl) - float(alt_msl))
    if err <= b:
        return 0.0
    outer = b * 2.0
    if err >= outer:
        return 1.0
    return (err - b) / max(outer - b, 1.0)
