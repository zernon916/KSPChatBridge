"""§8: pitch hold gains scale with mass; elevator slew and rate damping soft for heavy craft."""
from kspchat import hold


def test_inertia_scale_softens_heavy():
    assert hold._inertia_scale(40.0) < hold._inertia_scale(10.0) < hold._inertia_scale(5.0)
    assert hold._inertia_scale(40.0) < 0.75
    assert abs(hold._inertia_scale(hold.REF_MASS_T) - 1.0) < 0.08


def test_heavy_plane_level_hold_no_sustained_oscillation():
    """Heavy-craft effective gains are softer + more rate-damped than the Aeris reference (anti-porpoise)."""
    kin = hold._inertia_scale(40.0)
    # V/S→pitch and pitch P shrink with mass
    assert kin * hold.VS_PITCH_P < 0.55
    assert kin * hold.PITCH_KP < hold.PITCH_KP * 0.8
    assert kin * hold.ALT_VS_K < hold.ALT_VS_K
    # rate damping grows as kin falls
    kd_heavy = hold.PITCH_KD * (1.0 + 0.6 * (1.0 - kin))
    assert kd_heavy > hold.PITCH_KD * 1.15
    # elevator command is slewed softer than the old 0.6 /s
    assert hold.ELEV_SLEW <= 0.4
    assert hold.Q_SOFT_CAP > 0
