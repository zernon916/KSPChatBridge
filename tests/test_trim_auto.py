"""Auto-trim deploy-angle nudges (offline mocks)."""
from types import SimpleNamespace
from unittest.mock import MagicMock

from kspchat import trim_auto


def _surface(*, title="Elevator", pitch=True, roll=False, yaw=False, flap=False, deploy_angle=2.0):
    mod = MagicMock()
    mod.name = "ModuleControlSurface"
    mod.fields = ["Deploy Angle"]
    mod.get_field.side_effect = lambda k: "true" if k == "Deploy" else str(deploy_angle)
    cs = SimpleNamespace(
        part=SimpleNamespace(title=title, modules=[mod]),
        pitch_enabled=pitch,
        roll_enabled=roll,
        yaw_enabled=yaw,
        deployed=False,
    )
    return cs, mod


def test_pitch_authority_excludes_roll_only_flap():
    flap_cs, _ = _surface(title="Flap Left", pitch=False, roll=False, yaw=False, flap=True)
    elev_cs, _ = _surface(title="Elevator", pitch=True)
    v = SimpleNamespace(parts=SimpleNamespace(control_surfaces=[flap_cs, elev_cs]))
    surfs = trim_auto.pitch_authority_surfaces(v)
    assert len(surfs) == 1
    assert surfs[0][0] is elev_cs


def test_nudge_trim_adjusts_deploy(monkeypatch):
    elev, mod = _surface(deploy_angle=1.0)
    v = SimpleNamespace(
        name="Cruise Craft",
        parts=SimpleNamespace(control_surfaces=[elev]),
        control=SimpleNamespace(pitch=0.25, pitch_trim=0.0),
    )
    angles = []

    def capture_set(mod_, cs, deg):
        angles.append(float(deg))
        return True

    monkeypatch.setattr(trim_auto, "set_deploy_angle", capture_set)
    monkeypatch.setattr(trim_auto.emergency, "own_change", lambda: None)
    msg = trim_auto.nudge_trim(v, v.control, force=True)
    assert msg.startswith("Auto-trim:")
    assert len(angles) == 1
    assert angles[0] != 1.0


def test_steady_cruise_gate():
    tgt = {"speed": 200.0}
    assert trim_auto.steady_cruise(tgt, vs=0.2, roll=2.0, spd=198.0, climbing=False, pitch_cmd=None)
    assert not trim_auto.steady_cruise(tgt, vs=3.0, roll=2.0, spd=198.0, climbing=False, pitch_cmd=None)
    assert not trim_auto.steady_cruise(tgt, vs=0.0, roll=8.0, spd=198.0, climbing=False, pitch_cmd=None)
    assert not trim_auto.steady_cruise(tgt, vs=0.0, roll=0.0, spd=198.0, climbing=True, pitch_cmd=None)
