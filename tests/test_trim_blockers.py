"""Blocker 1: auto-trim sign per surface layout; reset restores snapshot."""
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

from kspchat import trim_auto


def _surface(*, title="Elevator", y=-2.0, pitch=True, inverted=False, deploy_invert=False, deploy_angle=1.0):
    mod = MagicMock()
    mod.name = "ModuleControlSurface"
    mod.fields = ["Deploy Angle", "Invert Deploy Direction"]
    mod.get_field.side_effect = lambda k: (
        "True" if k == "Invert Deploy Direction" and deploy_invert else str(deploy_angle)
    )

    def fields_side():
        from kspchat import propulsion as pr
        return {"Deploy Angle": str(deploy_angle),
                "Invert Deploy Direction": "True" if deploy_invert else "False"}

    part = SimpleNamespace(title=title, modules=[mod], position=lambda rf: (0.0, y, 0.0))
    cs = SimpleNamespace(
        part=part,
        pitch_enabled=pitch,
        roll_enabled=False,
        yaw_enabled=False,
        deployed=False,
        inverted=inverted,
        authority_limiter=1.0,
    )
    return cs, mod


def test_nudge_rear_elevator_increases_deploy_when_pitch_input_positive(monkeypatch):
    """Positive ctl.pitch (pulling nose up) -> more rear-elevator deploy, not a dive."""
    elev, mod = _surface(y=-3.0, deploy_angle=2.0)
    v = SimpleNamespace(
        name="Cruise Craft",
        reference_frame="vrf",
        parts=SimpleNamespace(control_surfaces=[elev]),
        control=SimpleNamespace(pitch=0.25, pitch_trim=0.0),
    )
    angles = []

    def capture_set(mod_, cs, deg):
        angles.append(float(deg))
        return True

    monkeypatch.setattr(trim_auto, "set_deploy_angle", capture_set)
    monkeypatch.setattr(trim_auto.emergency, "own_change", lambda: None)
    trim_auto.nudge_trim(v, v.control, force=True)
    assert len(angles) == 1
    assert angles[0] > 2.0


def test_nudge_canard_opposite_sign(monkeypatch):
    """Foreplane (canard) gets opposite deploy delta from the tail elevator."""
    canard, _ = _surface(title="Canard", y=2.0, deploy_angle=0.0)
    tail, _ = _surface(title="Elevator", y=-2.0, deploy_angle=0.0)
    v = SimpleNamespace(
        name="Canard Craft",
        reference_frame="vrf",
        parts=SimpleNamespace(control_surfaces=[canard, tail]),
        control=SimpleNamespace(pitch=0.3, pitch_trim=0.0),
    )
    by_title = {}

    def capture_set(mod_, cs, deg):
        by_title[cs.part.title] = float(deg)
        return True

    monkeypatch.setattr(trim_auto, "set_deploy_angle", capture_set)
    monkeypatch.setattr(trim_auto.emergency, "own_change", lambda: None)
    trim_auto.nudge_trim(v, v.control, force=True)
    assert by_title["Elevator"] > 0.0
    assert by_title["Canard"] < 0.0


def test_nudge_inverted_surface_flips_sign(monkeypatch):
    elev, _ = _surface(y=-2.0, inverted=True, deploy_angle=1.0)
    v = SimpleNamespace(
        name="Inv Craft",
        reference_frame="vrf",
        parts=SimpleNamespace(control_surfaces=[elev]),
        control=SimpleNamespace(pitch=0.2, pitch_trim=0.0),
    )
    angles = []

    def capture_set(mod_, cs, deg):
        angles.append(float(deg))
        return True

    monkeypatch.setattr(trim_auto, "set_deploy_angle", capture_set)
    monkeypatch.setattr(trim_auto.emergency, "own_change", lambda: None)
    trim_auto.nudge_trim(v, v.control, force=True)
    assert angles[0] < 1.0


def test_restore_surface_snapshot(monkeypatch):
    elev, mod = _surface(deploy_angle=5.0)
    elev.deployed = True
    v = SimpleNamespace(
        name="Snap Craft",
        reference_frame="vrf",
        parts=SimpleNamespace(control_surfaces=[elev]),
        control=SimpleNamespace(pitch_trim=0.1),
    )
    trim_auto.capture_surface_snapshot(v)
    elev.deployed = False
    monkeypatch.setattr(trim_auto, "set_deploy_angle", lambda m, c, d: setattr(mod, "_ang", d) or True)
    n = trim_auto.restore_surface_snapshot(v)
    assert n == 1
    assert elev.deployed is True


def test_level_worsening_blocks_trim(monkeypatch):
    trim_auto.reset_vessel_context()
    tgt = {"speed": 200.0}
    v = SimpleNamespace(name="X", parts=SimpleNamespace(control_surfaces=[]), control=SimpleNamespace(pitch=0.0))
    trim_auto._ctx["watch_vs"] = 0.2
    trim_auto._ctx["watch_pitch"] = 5.0
    trim_auto._ctx["worse"] = 1
    assert trim_auto._level_getting_worse(-2.5, 2.0) is True
    msg = trim_auto.maybe_trim_hold(v, v.control, tgt, vs=-2.5, roll=0.0, spd=200.0, alt=1000.0,
                                    climbing=False, pitch_cmd=None, pitch=2.0, now=1000.0)
    assert msg == ""
