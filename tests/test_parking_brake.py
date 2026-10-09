"""Parking brake on load (NEXT 7)."""
import sys
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from kspchat import parking


def setup_function(_):
    parking.STATE.clear()
    parking.STATE.update(vid=None, sit=None, released=False, announced=False)


def _v(wheels=2, brakes=False):
    return NS(parts=NS(wheels=[object()] * wheels), control=NS(brakes=brakes), name="TestCraft")


def test_applies_brakes_when_landed(monkeypatch):
    monkeypatch.setattr(parking, "_exempt", lambda: False)
    v = _v(brakes=False)
    line = parking.tick(v, "landed")
    assert v.control.brakes is True
    assert line and "Parking brake" in line


def test_skips_when_no_wheels(monkeypatch):
    monkeypatch.setattr(parking, "_exempt", lambda: False)
    v = _v(wheels=0)
    assert parking.tick(v, "landed") is None


def test_release_takeoff(monkeypatch):
    monkeypatch.setattr(parking, "_exempt", lambda: False)
    v = _v(brakes=False)
    parking.tick(v, "landed")
    parking.release("takeoff roll")
    v.control.brakes = False
    assert parking.tick(v, "landed") is None
    assert v.control.brakes is False


def test_player_brakes_off_releases(monkeypatch):
    monkeypatch.setattr(parking, "_exempt", lambda: False)
    v = _v(brakes=False)
    parking.tick(v, "pre_launch")
    parking.note_player_brakes(False)
    v.control.brakes = False
    assert parking.tick(v, "pre_launch") is None
    assert v.control.brakes is False


def test_rearms_after_landing(monkeypatch):
    monkeypatch.setattr(parking, "_exempt", lambda: False)
    v = _v(brakes=False)
    parking.release("taxi")
    parking.tick(v, "flying")
    parking.tick(v, "landed")
    assert v.control.brakes is True


def test_exempt_during_takeoff(monkeypatch):
    monkeypatch.setattr(parking, "_exempt", lambda: True)
    v = _v(brakes=False)
    assert parking.tick(v, "landed") is None
