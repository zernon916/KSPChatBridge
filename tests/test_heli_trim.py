"""§10 rotorcraft auto-trim persistence into craft_notes."""
from types import SimpleNamespace as NS

from kspchat import craft_notes, trim_auto


def test_maybe_trim_heli_saves_notes(monkeypatch):
    calls = []
    v = NS(name="Test Quad")
    info = {"lift": [0, 1], "counter": True, "multirotor": True}
    monkeypatch.setattr("kspchat.propulsion.set_blades", lambda *a, **k: calls.append(("blades", k)) or "ok")
    monkeypatch.setattr("kspchat.propulsion.set_rotor", lambda *a, **k: calls.append(("rotor", k)) or "ok")
    monkeypatch.setattr("kspchat.propulsion.layout", lambda vv: {"rotors": [{"dir": 1}, {"dir": -1}]})
    trim_auto.reset_vessel_context()
    msg = trim_auto.maybe_trim_heli(v, info, coll=4.0, yaw_rate=0.0, vs=-0.5, roll=0.0, spd=1.0, now=100.0)
    assert "collective" in msg.lower()
    saved = craft_notes.load("Test Quad")
    assert saved and saved.get("rotor", {}).get("collective") == 4.0
