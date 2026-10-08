"""Offline: GET /status snapshot (STATUS window + AICS indicators) from in-memory controller state. No KSP, no network."""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import flightplan, hold, ksp_actions, plane, protect, speedcap, spots, status  # noqa: E402


@pytest.fixture
def st(monkeypatch):
    flags = {"hold": False, "plane": False}
    monkeypatch.setattr(hold, "active", lambda: flags["hold"])
    monkeypatch.setattr(plane, "active", lambda: flags["plane"])
    monkeypatch.setattr(flightplan, "active", lambda: False)
    monkeypatch.setattr(flightplan, "status_line", lambda: "")
    monkeypatch.setattr(status, "_mechjeb_active", lambda: "")
    monkeypatch.setattr(spots, "landing_eta", lambda quiet=True: {"active": True, "summary": "Plane autoland (to_entry): runway 09 threshold 84.2 km away"})
    monkeypatch.setitem(ksp_actions._PILOT, "name", "Sidry Kerman")
    speedcap.clear_override("all")
    status._ETA.update(t=0.0, v="")
    yield flags
    speedcap.clear_override("all")
    protect.reset()


def test_idle(st):
    d = dict(status.snapshot())
    assert d["autopilot"] == "none" and d["ind_holds"] == "0" and d["ind_autoland"] == "0" and d["ind_mechjeb"] == "0"
    assert d["pilot"] == "Sidry Kerman - conscious" and "overrides" not in d


def test_holds_with_overrides_and_blackout(st, monkeypatch):
    st["hold"] = True
    monkeypatch.setattr(hold, "STATUS", {"phase": "hold", "alt_msl": 8950, "vs": -12.0, "vs_des": -15.0, "speed": 212.0,
                                         "eas": 150.0, "hdg": 91, "hdg_des": 95, "bank": 4, "bank_des": 5.0, "pitch": -3.0,
                                         "pitch_des": -4.0, "throttle": 1.0,
                                         "targets": {"altitude": 5000.0, "altitude_ref": "msl", "speed": 325.0, "throttle": 1.0}})
    ksp_actions.call_tool("set_override", {"kind": "bank", "value": 60, "authority": "pre"})
    speedcap.authorise_all()
    ksp_actions.call_tool("set_override", {"kind": "pitch", "value": 30})
    protect.tick({"t": 0, "g": 8.0, "crew": 1, "ctrl": True, "parts": 10})
    protect.tick({"t": 1, "g": 7.0, "crew": 1, "ctrl": False, "parts": 10})
    d = dict(status.snapshot())
    assert d["autopilot"] == "holds" and d["phase"] == "hold" and d["ind_holds"] == "1"
    assert d["alt"].startswith("8950 m MSL -> 5000 m MSL") and "212 m/s -> 325" in d["speed"]
    assert d["throttle"] == "manual 100%" and d["heading"] == "091 -> 095"
    assert "bank 60 ***" in d["overrides"] and "pitch 30 all" in d["overrides"] and "authorise all" in d["overrides"]
    assert d["pilot"] == "Sidry Kerman - BLACKED OUT" and "eased to" in d["overrides"] and "protect" in d


def test_autoland_en_route(st, monkeypatch):
    st["plane"] = True
    monkeypatch.setattr(plane, "STATUS", {"phase": "to_entry", "runway_alt": 70.0, "h": 3000.0, "h_des_msl": 3200,
                                          "vs": 5.0, "speed": 250.0, "v_des": 325.0, "hdg": 270, "hdg_des": 268,
                                          "bank": 3, "throttle": 0.8})
    monkeypatch.setattr(plane, "LIVE", {"vcruise": 325.0})
    d = dict(status.snapshot())
    assert d["autopilot"] == "autoland / fly-to" and d["phase"] == "to_entry" and d["ind_autoland"] == "1"
    assert d["alt"].startswith("3070 m MSL -> 3200 m") and "250 m/s -> 325" in d["speed"]
    assert d["throttle"] == "auto 80%" and d["orders"] == "live: vcruise 325" and "84.2 km" in d["eta"]
    assert all("\t" not in v and "\n" not in v for v in d.values())