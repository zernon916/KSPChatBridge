"""Offline: real eject - bridge side (eject_kerbal asks yes/no, then queues "!cmd eject <t> <name>" for the AICS mod's
/events poll; an old mod build gets the old explanation). In-memory only; no KSP, no network, no live files."""
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, ksp_actions, science, speedcap  # noqa: E402


@pytest.fixture
def v(monkeypatch):
    vessel = SimpleNamespace(crew=[SimpleNamespace(name="Sidry Kerman")])
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: vessel)
    monkeypatch.setattr(ksp_actions, "pilot_name", lambda: None)
    monkeypatch.setattr(science, "_cmd_seen", [0.0])
    vessel.posted = []
    monkeypatch.setattr(science, "post_event", vessel.posted.append)  # record (test_flightplan also swaps it module-wide)
    speedcap._pending.clear()
    yield vessel
    speedcap._pending.clear()


def test_old_mod_build_explains(v):
    r = chat.Session("ingame").send("eject", "local")[0]
    assert "Can't eject Sidry Kerman yet" in r and "install_mod.ps1" in r and v.posted == []


def test_eject_asks_then_no_cancels(v):
    science.mark_cmd_client()
    s = chat.Session("ingame")
    r = s.send("eject", "local")[0]
    assert r.startswith("Eject Sidry Kerman?") and "(yes/no)" in r and v.posted == [], r
    assert s.send("no", "local")[0] == "Cancelled - nothing done." and v.posted == []


def test_eject_yes_queues_mod_command(v):
    science.mark_cmd_client()
    s = chat.Session("ingame")
    s.send("eject kerbal", "local")
    r = s.send("yes", "local")[0]
    assert r.startswith("Ejecting Sidry Kerman"), r
    t, = v.posted
    assert t.startswith(science.MOD_CMD + "eject ") and t.endswith(" Sidry Kerman")
    assert abs(float(t.split()[2]) - __import__("time").time()) < 5


def test_no_crew(v):
    science.mark_cmd_client()
    v.crew = []
    assert chat.Session("ingame").send("eject", "local")[0] == "Nobody aboard to eject (no crew)."