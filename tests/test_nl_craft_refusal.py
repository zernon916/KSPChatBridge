"""NL craft/phase mismatches answered by the bridge before the model."""
import sys
from pathlib import Path
from types import SimpleNamespace as NS

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, ksp_actions, orders  # noqa: E402


def _plane_flying(monkeypatch):
    v = NS(situation="Vessel.situation.flying", parts=NS(wheels=[1], all=[]))
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions, "_heli_v", lambda v: None)
    monkeypatch.setattr(ksp_actions, "_planeish", lambda v: True)


def test_hover_on_flying_plane(monkeypatch):
    _plane_flying(monkeypatch)
    msg = orders.nl_craft_refusal("takeoff and hover at 2000")
    assert msg and "already in flight" in msg


def test_orbit_language_on_plane(monkeypatch):
    _plane_flying(monkeypatch)
    assert orders.nl_craft_refusal("run mechjeb ascent to orbit") and "aircraft" in orders.nl_craft_refusal(
        "run mechjeb ascent to orbit")


def test_session_skips_model_on_mismatch(monkeypatch):
    _plane_flying(monkeypatch)
    monkeypatch.setattr(chat.backends, "resolve", lambda b, m=None: (_ for _ in ()).throw(AssertionError("no model")))
    reply, tools = chat.Session("t").send("takeoff and hover", "local")
    assert tools == [] and "already in flight" in reply


def test_direct_hover_still_runs_tool(monkeypatch):
    """Exact 'hover' still goes through parse_direct -> heli_control redirect, not nl_craft_refusal."""
    _plane_flying(monkeypatch)
    called = []
    monkeypatch.setattr(ksp_actions, "call_tool", lambda n, a: called.append(n) or "ok")
    monkeypatch.setattr(ksp_actions, "pilot_name", lambda: None)
    class _Sess:
        def __enter__(self):
            return None

        def __exit__(self, *a):
            return False

    monkeypatch.setattr(chat.speedcap, "use_session", lambda sid: _Sess())
    chat.Session("t").send("hover", "local")
    assert called == ["heli_control"]
