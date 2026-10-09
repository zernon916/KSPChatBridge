"""GET/POST /setting for autopilot keys."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from kspchat import alt_hold, settings


def test_put_get_altitude_band():
    settings.put_setting("altitude_band_m", 120)
    assert settings.get_setting("altitude_band_m") == 120.0
    assert alt_hold.band_m() == 120.0
