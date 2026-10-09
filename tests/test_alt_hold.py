"""Altitude hold band logic (BLOCKER 3)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from kspchat import alt_hold, settings


def setup_function(_):
    settings.put(alt_hold.BAND_KEY, 150.0)


def test_default_band_150():
    settings.put(alt_hold.BAND_KEY, None)
    assert alt_hold.band_m() == 150.0
    assert alt_hold.in_band(1000.0, 1000.0)
    assert alt_hold.in_band(925.0, 1000.0)
    assert alt_hold.in_band(1075.0, 1000.0)
    assert not alt_hold.in_band(849.0, 1000.0)
    assert not alt_hold.in_band(1151.0, 1000.0)


def test_set_band_clamped():
    assert alt_hold.set_band_m(200) == 200.0
    assert alt_hold.set_band_m(5) == 10.0
    assert alt_hold.set_band_m(900) == 500.0


def test_in_band_holds_zero_vs():
    st = {}
    vs, inside = alt_hold.vertical_speed_target(1000.0, 1000.0, 25, 15, 0.08, 1.0, st, band=150)
    assert inside and vs == 0.0
    assert st["capture_msl"] == 1000.0


def test_approach_eases_before_band():
    st = {}
    vs_out, inside = alt_hold.vertical_speed_target(800.0, 1000.0, 25, 15, 0.08, 1.0, st, band=150)
    assert not inside
    vs_full, _ = alt_hold.vertical_speed_target(700.0, 1000.0, 25, 15, 0.08, 1.0, {}, band=150)
    assert abs(vs_out) < abs(vs_full)


def test_leaving_band_resumes_chase():
    st = {"capture_msl": 990.0}
    vs, inside = alt_hold.vertical_speed_target(800.0, 1000.0, 25, 15, 0.08, 1.0, st, band=150)
    assert not inside and st["capture_msl"] is None
    assert vs > 0


def test_easing_fraction():
    assert alt_hold.approach_easing_fraction(1000, 1000, 150) == 0.0
    assert alt_hold.approach_easing_fraction(1150, 1000, 150) == 0.0
    assert alt_hold.approach_easing_fraction(1300, 1000, 150) == 1.0
