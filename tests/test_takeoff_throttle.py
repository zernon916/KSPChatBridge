"""Offline: TWR -> starting takeoff throttle and the takeoff throttle governor (kspchat/takeoff.py). Mocked kRPC."""
import logging
import sys
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import plane, takeoff as tko  # noqa: E402


def fake_vessel(mass_t, avail_kn, max_kn=None, g=9.81):
    body = SimpleNamespace(surface_gravity=g)
    return SimpleNamespace(mass=mass_t * 1000.0, available_thrust=avail_kn * 1000.0,
                           max_thrust=(max_kn or avail_kn) * 1000.0, orbit=SimpleNamespace(body=body))


def test_start_throttle_mapping():
    table = {0.0: 1.0, 0.3: 1.0, 0.4: 1.0, 0.6: 0.9, 0.8: 0.75, 1.0: 0.65, 1.2: 0.55, 1.5: 0.35, 3.0: 0.35}
    for twr, want in table.items():
        assert tko.start_throttle(twr) == want, (twr, tko.start_throttle(twr))
    prev = 1.0
    for i in range(0, 400):
        thr = tko.start_throttle(i / 100.0)
        assert 0.35 <= thr <= 1.0 and thr <= prev + 1e-9   # never 0, high TWR -> lower start
        assert abs(thr / 0.05 - round(thr / 0.05)) < 1e-6  # 5 % steps
        prev = thr


def test_vessel_specs_from_krpc():
    sp = tko.vessel_specs(fake_vessel(10.0, 150.0, 190.0))
    assert abs(sp["twr"] - 150.0 / (10.0 * 9.81)) < 1e-9 and sp["mass_t"] == 10.0 and sp["max_kn"] == 190.0
    assert tko.start_throttle(sp["twr"]) == 0.35            # TWR 1.53: hot jet -> gentle start
    assert tko.start_throttle(tko.vessel_specs(fake_vessel(20.0, 80.0))["twr"]) == 1.0  # TWR 0.41: full
    assert tko.vessel_specs(fake_vessel(0.0, 100.0))["twr"] == 0.0


def test_takeoff_governor_logs_twr(caplog):
    status = {}
    with caplog.at_level(logging.INFO, logger="kspchat"):
        gov = plane._takeoff_governor(fake_vessel(10.0, 98.1), None, 0.0, status, "plane")
    assert gov.cmd == 0.65 and status == {"takeoff_twr": 1.0, "takeoff_throttle": 0.65}
    assert "takeoff TWR 1.00" in caplog.text and "start throttle 65%" in caplog.text
    broken = SimpleNamespace(mass=1.0)  # kRPC error -> full power (the old behavior), still logged
    assert plane._takeoff_governor(broken, None, 0.0, {}, "hold").cmd == 1.0


def run(gov, t0, t1, **kw):
    t, out = t0, []
    while t <= t1 + 1e-9:
        out.append(gov.update(t, **kw))
        t += 0.25
    return out


def test_roll_adds_power_in_5pct_steps_when_too_slow():
    gov = tko.TakeoffThrottle(0.5, 0.0)
    thr = run(gov, 0.0, 3.5, spd=10.0, eas=10.0, airborne=False, vr=60.0, v_min_safe=70.0, accel=1.0)
    assert set(thr) == {0.5}                       # spool-up wait: no change in the first 4 s
    thr = run(gov, 3.75, 10.0, spd=20.0, eas=20.0, airborne=False, vr=60.0, v_min_safe=70.0, accel=1.0)
    steps = sorted(set(thr))
    assert steps[0] == 0.5 and all(abs(b - a - 0.05) < 1e-9 for a, b in zip(steps, steps[1:]))
    assert max(thr) <= 0.65 + 1e-9                 # >= 3 s between roll steps


def test_roll_holds_when_accelerating_well():
    gov = tko.TakeoffThrottle(0.35, 0.0)
    thr = run(gov, 0.0, 20.0, spd=40.0, eas=40.0, airborne=False, vr=60.0, v_min_safe=70.0, accel=4.0)
    assert set(thr) == {0.35}


def test_climb_out_holds_while_speed_changes_then_trims():
    gov = tko.TakeoffThrottle(0.6, -10.0)
    thr = run(gov, 0.0, 20.0, spd=150.0, eas=150.0, airborne=True, vr=60.0, v_min_safe=70.0, accel=2.0)
    assert set(thr) == {0.6}                       # still accelerating, far from the cap: hold
    thr = run(gov, 20.0, 30.0, spd=150.0, eas=150.0, airborne=True, vr=60.0, v_min_safe=70.0, accel=0.0)
    assert max(thr) > 0.6                          # settled below the 200 target: add power (5 % / 4 s)
    assert thr.count(0.65) >= 15                   # waited ~4 s before the next step


def test_over_cap_takes_power_off_but_never_zero():
    gov = tko.TakeoffThrottle(0.5, -10.0)
    thr = run(gov, 0.0, 60.0, spd=230.0, eas=230.0, airborne=True, vr=60.0, v_min_safe=70.0, accel=1.0)
    assert min(thr) == tko.FLOOR_OVER > 0          # stepped down to the floor, never 0
    diffs = [round(a - b, 3) for a, b in zip(thr, thr[1:]) if a != b]
    assert set(diffs) == {0.05}                    # 5 % steps
    gov = tko.TakeoffThrottle(0.5, -10.0)          # heading for the cap (210 + 5 s x 3 m/s^2 > 220): ease off early
    thr = run(gov, 0.0, 1.75, spd=210.0, eas=210.0, airborne=True, vr=60.0, v_min_safe=70.0, accel=3.0)
    assert thr[-1] == 0.45


def test_under_stall_margin_adds_power_even_while_changing():
    gov = tko.TakeoffThrottle(0.4, -10.0)
    thr = run(gov, 0.0, 4.0, spd=65.0, eas=65.0, airborne=True, vr=60.0, v_min_safe=70.0, accel=-1.0)
    assert thr[0] == 0.45 and thr[-1] == 0.55      # at once, then every 2 s (urgent)


def test_trend_filter_without_explicit_accel():
    gov = tko.TakeoffThrottle(0.5, 0.0)
    t, spd = 0.0, 100.0
    while t < 10.0:
        gov.update(t, spd, spd, True, 60.0, 70.0)
        t += 0.1
        spd += 0.3                                 # 3 m/s^2
    assert 2.5 < gov.acc < 3.5 and gov.cmd == 0.5  # accelerating -> held


def test_safe_ceiling_from_specs():
    assert tko.ceiling_from_specs(0.4, 1, 0) == 11000.0      # 8000 * ln(4) = 11.09 km, floored to 500 m
    assert tko.ceiling_from_specs(0.2, 1, 0) == 5500.0
    assert tko.ceiling_from_specs(1.5, 2, 0) == 18000.0      # clamped
    assert tko.ceiling_from_specs(0.08, 1, 0) == 3000.0      # too weak / unknown -> conservative minimum
    assert tko.ceiling_from_specs(0.6, 1, 0, intake_air=False) == 3000.0
    assert tko.ceiling_from_specs(0.0, 0, 2) == 20000.0      # rocket plane


def test_vessel_ceiling_from_krpc():
    tko._CEIL_CACHE.clear()
    jet = SimpleNamespace(propellant_names=["LiquidFuel", "IntakeAir"], max_thrust_at=lambda p: 2 * 39240.0, max_thrust=0.0)
    v = fake_vessel(20.0, 0.0)
    v.name, v.parts, v.resources = "Jet", SimpleNamespace(engines=[jet]), SimpleNamespace(max=lambda n: 1.0)
    ceil, info = tko.vessel_ceiling(v)
    assert ceil == 11000.0 and info["jets"] == 1 and info["rockets"] == 0 and info["twr_air"] == 0.4
    assert tko.vessel_ceiling(SimpleNamespace(name="x", mass=1.0)) == (None, {})   # kRPC can't tell -> no check
