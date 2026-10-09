"""Power management at low EC (NEXT 8)."""
import sys
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from kspchat import power_mgmt


class Res:
    def __init__(self, amt, mx):
        self._a, self._m = amt, mx

    def max(self, _):
        return self._m

    def amount(self, _):
        return self._a


def test_rotor_spool_block():
    v = NS(resources=Res(10, 100))
    msg = power_mgmt.rotor_spool_block(v)
    assert msg and "No juice to the rotors" in msg


def test_rotor_spool_ok():
    v = NS(resources=Res(30, 100))
    assert power_mgmt.rotor_spool_block(v) is None


def test_low_ec_sheds_and_lines(monkeypatch):
    power_mgmt._last_line = 0.0
    lights = [True]

    class Ctl:
        @property
        def lights(self):
            return lights[0]

        @lights.setter
        def lights(self, val):
            lights[0] = val

    rw = NS(active=True)
    panel = NS(deployed=False)
    v = NS(resources=Res(20, 100), control=Ctl(),
           parts=NS(reaction_wheels=[rw, NS(active=True)], solar_panels=[panel], all=[]))

    monkeypatch.setattr(power_mgmt, "_start_power_modules", lambda _v: 0)
    monkeypatch.setattr(power_mgmt, "random", NS(choice=lambda xs: xs[0]))
    line = power_mgmt.low_ec_tick(v, {"sit": "landed", "speed": 0.0})
    assert line
    assert lights[0] is False
    assert panel.deployed is True


def test_solar_skipped_in_high_q(monkeypatch):
    power_mgmt._last_line = 0.0
    panel = NS(deployed=False)
    v = NS(resources=Res(5, 100), control=NS(lights=False),
           parts=NS(reaction_wheels=[], solar_panels=[panel], all=[]))
    monkeypatch.setattr(power_mgmt, "_start_power_modules", lambda _v: 1)
    monkeypatch.setattr(power_mgmt, "random", NS(choice=lambda xs: xs[0]))
    power_mgmt.low_ec_tick(v, {"sit": "flying", "speed": 250.0, "q": 20000.0})
    assert panel.deployed is False
