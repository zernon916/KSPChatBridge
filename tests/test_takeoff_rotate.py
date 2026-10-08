"""Offline: takeoff rotation assist, ramjet start throttle and the runway-end abort (Luke 2026-10-08: the Whiplash
delta Aeris 4A ran the whole runway and never rotated). Mocked kRPC, no live files."""
import logging
import math
import sys
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import hold, plane, spots, takeoff as tko  # noqa: E402


def test_weak_low_speed_engines_start_at_full():
    assert tko.weak_low_speed(["J-X4 \"Whiplash\" Turbo Ramjet Engine", "J-33 \"Wheesley\" Turbofan Engine"])
    assert not tko.weak_low_speed(["J-33 \"Wheesley\" Turbofan Engine", "J-404 \"Panther\" Afterburning Turbofan"])
    assert not tko.weak_low_speed([]) and not tko.weak_low_speed(None)
    assert tko.start_throttle(1.25) == 0.5            # Aeris 4A's TWR alone -> 50 % (what it did at 17:41)
    assert tko.start_throttle(1.25, weak=True) == 1.0
    assert tko.start_throttle(3.0, weak=True) == 1.0  # the cap rules still take power off near the speed cap


def test_governor_detects_ramjets_from_part_titles(caplog):
    eng = lambda t: SimpleNamespace(part=SimpleNamespace(title=t))  # noqa: E731
    body = SimpleNamespace(surface_gravity=9.81)
    v = SimpleNamespace(mass=19400.0, available_thrust=237000.0, max_thrust=237000.0,
                        orbit=SimpleNamespace(body=body),
                        parts=SimpleNamespace(engines=[eng("J-X4 Whiplash Turbo Ramjet Engine"), eng("Aerospike")]))
    with caplog.at_level(logging.INFO, logger="kspchat"):
        gov = plane._takeoff_governor(v, None, 0.0, {}, "plane")
    assert gov.cmd == 1.0 and "ramjets: weak low-speed thrust" in caplog.text and "start throttle 100%" in caplog.text


def test_very_low_roll_accel_jumps_to_full_after_spool():
    gov = tko.TakeoffThrottle(0.5, 0.0)
    out = [gov.update(t * 0.25, 10.0, 10.0, False, 60.0, 70.0, accel=0.3) for t in range(15)]  # 0 .. 3.5 s
    assert set(out) == {0.5}                           # spool-up wait first
    assert gov.update(4.0, 12.0, 12.0, False, 60.0, 70.0, accel=0.3) == 1.0
    assert "full power" in gov.why
    assert gov.update(4.25, 70.0, 70.0, False, 60.0, 70.0, accel=0.3) == 1.0  # above Vr: no change


def test_rotate_assist_ramps_until_nose_rises():
    r = tko.RotateAssist()
    assert r.update(True, 40.0, 56.0, 9.0, 0.0, 1.0) == 0.0          # below Vr: nothing
    for _ in range(4):
        r.update(True, 60.0, 56.0, 8.0, 0.0, 1.0)                     # at Vr, nose not rising: ramps
    assert math.isclose(r.extra, 4 * tko.ROT_RAMP)
    held = r.extra
    r.update(True, 62.0, 56.0, 6.0, 2.0, 1.0)                         # rising 2 deg/s: hold
    assert r.extra == held
    r.update(True, 63.0, 56.0, 4.0, 4.0, 1.0)                         # rising fast: back off
    assert math.isclose(r.extra, held - tko.ROT_BACKOFF)
    for _ in range(30):
        r.update(True, 80.0, 56.0, 9.0, 0.0, 1.0)
    assert r.extra == tko.ROT_MAX and r.maxed                         # within limits
    r.update(False, 80.0, 56.0, 3.0, 1.5, 1.0)                        # airborne: bleeds slowly
    assert math.isclose(r.extra, tko.ROT_MAX - tko.ROT_BLEED)
    r.update(False, 80.0, 56.0, -2.0, 0.0, 1.0)                       # past the attitude: faster
    assert math.isclose(r.extra, tko.ROT_MAX - tko.ROT_BLEED - tko.ROT_BACKOFF)
    assert r.update(True, 30.0, 56.0, 9.0, 0.0, 1.0) == 0.0           # back below Vr on the ground: reset


def test_runway_abort_rule():
    assert math.isclose(tko.stop_distance(56.0), 56.0 ** 2 / 7.0 + 100.0)
    assert tko.runway_abort(True, 56.0, 500.0, False)                 # 548 m needed, 500 left, nose not up
    assert not tko.runway_abort(True, 56.0, 800.0, False)             # still room
    assert not tko.runway_abort(True, 56.0, 500.0, True)              # nose rising: let it fly
    assert not tko.runway_abort(False, 56.0, 100.0, False)            # airborne
    assert not tko.runway_abort(True, 56.0, None, False)              # not on a known runway
    assert not tko.runway_abort(True, 3.0, 50.0, False)               # (taxiing)
    assert tko.runway_abort(True, 40.0, 300.0, False)                 # below Vr with the end close
    assert tko.runway_remaining(500.0, 2000.0, True) == 1500.0
    assert tko.runway_remaining(500.0, 2000.0, False) == 500.0
    assert tko.runway_remaining(2100.0, 2000.0, True) == 0.0


def test_runway_ahead_geometry(monkeypatch):
    monkeypatch.setattr(plane, "STRIPS", {"t": {"a": (0.0, 0.0), "b": (0.0, 0.2)}})   # east-west strip
    monkeypatch.setattr(spots, "all_spots", lambda: {})
    body = SimpleNamespace(equatorial_radius=600000.0, latitude_at_position=lambda p, r: p[0],
                           longitude_at_position=lambda p, r: p[1])
    v = SimpleNamespace(position=lambda r: (0.0, 0.05))
    k = 2 * math.pi * 600000.0 / 360.0
    assert math.isclose(plane._runway_ahead(body, v, None, 90.0), 0.15 * k)    # rolling east
    assert math.isclose(plane._runway_ahead(body, v, None, 268.0), 0.05 * k)   # rolling west
    assert plane._runway_ahead(body, v, None, 0.0) is None                     # not along the strip
    off = SimpleNamespace(position=lambda r: (0.01, 0.05))                     # ~1 km off the centerline
    assert plane._runway_ahead(body, off, None, 90.0) is None


def test_abort_text():
    r = tko.RotateAssist()
    r.extra = 0.8
    msg = plane._abort_text(120.0, 56.0, 640.0, r)
    assert msg.startswith("Takeoff aborted at 120 m/s with 640 m of runway left") and "nose wouldn't come up" in msg
    assert "80%" in msg and msg.endswith("Throttle idle, brakes on.")
    assert "below rotation speed (40 of 56 m/s)" in plane._abort_text(40.0, 56.0, 300.0, r)


def test_hold_imports_takeoff_helpers():
    assert hold._runway_ahead is plane._runway_ahead and hold._abort_text is plane._abort_text
    assert hold.ROT_RISE_Q == plane.ROT_RISE_Q


# ---- follow-up: Luke flies the Aeris 4A off by hand at ~120 m/s (Vr was ~56) ----

def test_learned_vr_and_need():
    assert tko.rotate_speed(45.0) == 56.25 and tko.rotate_speed(20.0) == 35.0
    assert math.isclose(tko.rotate_speed(45.0, 124.0), 0.92 * 124.0)
    assert tko.lift_need(56.25) == tko.V_LIFT_GUESS and tko.lift_need(56.25, 124.0) == 124.0
    assert tko.learned_liftoff(["Aeris 4A"]) is None
    tko.record_liftoff(["Aeris 4A", "sig:40p/12.0t"], 123.6)
    assert tko.learned_liftoff(["nope", "sig:40p/12.0t"]) == 123.6


def test_rotation_achieved():
    assert not tko.rotation_achieved(0.3, 1.0, 0.2, True)
    assert tko.rotation_achieved(1.5, 0.0, 0.0, True)       # nose rising
    assert tko.rotation_achieved(0.0, 3.5, 0.0, True)       # nose up a few degrees
    assert tko.rotation_achieved(0.0, 0.0, 2.0, True)       # climbing
    assert tko.rotation_achieved(0.0, 0.0, 0.0, False)      # wheels off


def test_force_full_when_rotation_assist_is_maxed():
    gov = tko.TakeoffThrottle(0.5, 0.0)
    assert gov.force_full(10.0, "rotation") and gov.cmd == 1.0 and gov.why == "rotation"
    assert not gov.force_full(11.0, "again")
    assert gov.update(11.25, 80.0, 80.0, False, 56.0, 70.0, accel=5.0) == 1.0   # stays full on the roll


def _roll(accel, need, rotate_at, runway=2400.0, dt=0.2):
    """Simulated takeoff roll: returns (speed, remaining) where it aborted, or ('rotated', speed)."""
    v = x = 0.0
    while x < runway:
        if rotate_at is not None and v >= rotate_at:
            return "rotated", v
        if tko.runway_abort(True, v, runway - x, False, accel, need):
            return v, runway - x
        v += accel * dt
        x += v * dt
    return "ran off", v


def test_aeris_uncached_is_not_aborted_before_its_120_rotation():
    # old rule (abort at the last stop point) would have stopped it at ~100 m/s; now it keeps going and rotates
    assert _roll(6.0, tko.lift_need(56.25), 120.0)[0] == "rotated"
    assert _roll(6.0, tko.lift_need(tko.rotate_speed(45.0, 124.0), 124.0), 124.0)[0] == "rotated"
    v, left = _roll(6.0, None, 120.0)            # (no need known = the old rule) -> aborts below 120
    assert v < 110.0


def test_abort_when_it_truly_cannot_make_it():
    v, left = _roll(2.0, tko.lift_need(56.25, 124.0), None)   # weak: can't reach the learned 124 in time
    assert v < 124.0 and left >= tko.stop_distance(v) - 30.0  # aborts near the last point it can still stop
    assert not tko.runway_abort(True, 100.0, 3000.0, False, 0.0, 124.0)   # still room to stop: wait
    assert tko.runway_abort(True, 100.0, 1500.0, False, 0.1, 124.0)       # not accelerating any more
    assert not tko.runway_abort(True, 100.0, 1500.0, True, 0.1, 124.0)    # rotating: never
    assert not tko.runway_abort(False, 100.0, 100.0, False, 0.1, 124.0)   # airborne: never (no 0 throttle aloft)


def _feed(lr, seq, vid=1):
    return [r for r in (lr.feed(t, sit, spd, vid, agl) for t, sit, spd, agl in seq) if r]


def test_liftoff_learner_hand_takeoff():
    lr = tko.LiftoffLearner()
    roll = [(t, "landed", 6.0 * t, None) for t in range(21)]                 # 0 .. 120 m/s
    out = _feed(lr, roll + [(21, "flying", 126.0, None)])
    assert out == [("liftoff", 123.0)]
    assert _feed(lr, [(25, "flying", 130.0, 60.0)]) == []                     # not confirmed yet
    assert _feed(lr, [(29, "flying", 135.0, 120.0)]) == [("learned", 123.0)]
    assert _feed(lr, [(40, "flying", 140.0, 300.0)]) == []                    # once


def test_liftoff_learner_ignores_bounces_skims_and_vessel_switches():
    lr = tko.LiftoffLearner()
    bounce = [(0, "flying", 70.0, 5.0), (1, "landed", 65.0, None), (2, "flying", 66.0, None)]
    assert _feed(lr, bounce) == []                                           # landing bounce: ground run not from rest
    lr = tko.LiftoffLearner()
    roll = [(t, "landed", 8.0 * t, None) for t in range(22)]
    assert _feed(lr, roll + [(22, "flying", 176.0, None)])[0][0] == "liftoff"
    assert _feed(lr, [(31, "flying", 200.0, 8.0)]) == [("not_learned", 172.0)]   # ran off the end, skimming
    lr = tko.LiftoffLearner()
    _feed(lr, roll[:15] + [(15, "flying", 120.0, None)])
    assert _feed(lr, [(16, "landed", 110.0, None), (30, "flying", 100.0, 200.0)]) == []   # touched back down
    lr = tko.LiftoffLearner()
    _feed(lr, roll[:15])
    assert _feed(lr, [(15, "flying", 120.0, None)], vid=2) == []            # vessel switch resets


def test_watcher_learns_and_logs_hand_takeoff(monkeypatch, caplog):
    from kspchat import emergency, settings
    monkeypatch.setattr(emergency, "_LIFT", None)
    monkeypatch.setattr(emergency, "_engaged", lambda: False)
    fl = SimpleNamespace(pitch=9.5, surface_altitude=150.0)
    v = SimpleNamespace(name="Aeris 4A", surface_reference_frame=None, flight=lambda *a: fl,
                        parts=SimpleNamespace(all=[0] * 40), dry_mass=12000.0)
    s = lambda t, sit, spd: {"t": t, "sit": sit, "speed": spd, "vid": 7}  # noqa: E731
    with caplog.at_level(logging.INFO, logger="kspchat"):
        for t in range(21):
            emergency._learn_liftoff(v, s(t, "landed", 6.0 * t))
        assert emergency._learn_liftoff(v, s(21, "flying", 126.0)) == ("liftoff", 123.0)
        assert emergency._learn_liftoff(v, s(30, "flying", 140.0)) == ("learned", 123.0)
    assert "Aeris 4A lifted off at ~123 m/s (pitch 9.5, by hand)" in caplog.text
    assert "learned liftoff 123 m/s for Aeris 4A (by hand) -> next autopilot Vr 113 m/s" in caplog.text
    cache = settings.get(tko.VR_CACHE)
    assert cache["Aeris 4A"] == 123.0 and cache["sig:40p/12.0t"] == 123.0
    assert tko.learned_liftoff(plane._craft_keys(v)) == 123.0
