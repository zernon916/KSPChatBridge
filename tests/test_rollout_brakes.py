"""Offline: gentle autoland rollout brakes (Luke 2026-10-08: his prop plane flipped over under hard braking)."""
import sys
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import plane, rollout as ro  # noqa: E402

DT = 0.05


def run(rb, t0, t1, **kw):
    out, t = [], t0
    while t < t1 - 1e-9:
        state = {k: (f(t) if callable(f) else f) for k, f in kw.items()}
        out.append((t, rb.update(t, **state)))
        t += DT
    return out


def calm(**over):
    d = dict(wheels_down=True, pitch=1.0, q=0.0, roll=0.0, p=0.0, vs=0.0)
    d.update(over)
    return d


def duty(samples):
    return sum(1 for _, on in samples if on) / max(1, len(samples))


def test_no_brakes_until_all_wheels_down_and_settled():
    rb = ro.RolloutBrakes()
    assert not any(on for _, on in run(rb, 0.0, 3.0, **calm(wheels_down=False)))   # nose wheel still up
    assert not any(on for _, on in run(rb, 3.0, 3.0 + ro.SETTLE_S - 0.1, **calm()))  # settling
    assert rb.ref is None
    run(rb, 4.4, 4.7, **calm())
    assert rb.ref == 1.0 and rb.why.startswith("ramping")


def test_bouncy_touchdown_resets_the_settle_timer():
    rb = ro.RolloutBrakes()
    run(rb, 0.0, 1.0, **calm())
    run(rb, 1.0, 1.2, **calm(vs=2.5))          # bounce
    run(rb, 1.2, 2.5, **calm())
    assert rb.ref is None                       # 1.3 s calm since the bounce: not yet
    run(rb, 2.5, 3.0, **calm())
    assert rb.ref is not None


def test_duty_ramps_up_gently():
    rb = ro.RolloutBrakes()
    run(rb, 0.0, 1.6, **calm())
    first = run(rb, 1.6, 2.6, **calm())         # first second of the ramp: light pulses
    late = run(rb, 6.0, 8.0, **calm())          # after RAMP_S: full
    assert 0.1 < duty(first) < 0.45 and duty(late) == 1.0
    assert any(on for _, on in first) and not all(on for _, on in first)


def test_nose_dip_releases_until_settled_and_lowers_the_max():
    rb = ro.RolloutBrakes()
    run(rb, 0.0, 7.0, **calm())
    assert rb.update(7.0, **calm())
    assert not rb.update(7.05, **calm(pitch=1.0 - 3.0))          # nose 3 deg below the rollout attitude
    assert rb.released.startswith("nose dipping") and rb.duty_max == ro.DUTY_MAX0 - ro.DUTY_STEP
    assert not any(on for _, on in run(rb, 7.1, 8.0, **calm(pitch=0.5, q=1.5)))   # rebounding: not calm yet
    run(rb, 8.0, 8.6, **calm(pitch=0.6))                         # settled again (within 1 deg, rates calm)
    assert rb.released is None
    later = run(rb, 13.0, 15.0, **calm())
    assert duty(later) < 0.95                                    # ramps back only to the lowered maximum


def test_fast_nose_down_rate_and_bank_release():
    rb = ro.RolloutBrakes()
    run(rb, 0.0, 7.0, **calm())
    assert not rb.update(7.0, **calm(q=-4.0)) and "nose dropping" in rb.why
    rb2 = ro.RolloutBrakes()
    run(rb2, 0.0, 7.0, **calm())
    assert not rb2.update(7.0, **calm(roll=5.0)) and "bank" in rb2.why            # tip-over risk
    rb3 = ro.RolloutBrakes()
    run(rb3, 0.0, 7.0, **calm())
    assert not rb3.update(7.0, **calm(p=-8.0)) and "rolling" in rb3.why
    rb4 = ro.RolloutBrakes()
    run(rb4, 0.0, 7.0, **calm())
    assert not rb4.update(7.0, **calm(wheels_down=False)) and "wheel lifted" in rb4.why
    for _ in range(10):  # repeated dips never take the max below the floor
        rb4.released = None
        rb4.update(8.0, **calm(pitch=-5.0))
    assert rb4.duty_max == ro.DUTY_FLOOR


def test_wheels_grounded_helper():
    w = lambda g, broken=False: NS(grounded=g, broken=broken)  # noqa: E731
    assert plane._wheels_grounded(NS(parts=NS(wheels=[w(True), w(True), w(True)]))) is True
    assert plane._wheels_grounded(NS(parts=NS(wheels=[w(True), w(False), w(True)]))) is False
    assert plane._wheels_grounded(NS(parts=NS(wheels=[w(True), w(False, True)]))) is True     # broken wheel ignored
    assert plane._wheels_grounded(NS(parts=NS(wheels=[]))) is None
    assert plane._wheels_grounded(NS()) is None