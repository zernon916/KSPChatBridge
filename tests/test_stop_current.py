"""'stop current (plan)': deterministic soft cancel (autopilots off, throttle untouched), compound '..., and land at 27'."""
from types import SimpleNamespace as NS

from kspchat import chat, ksp_actions, orders


def test_split_stop():
    assert orders.split_stop("stop current plan") == (True, "")
    assert orders.split_stop("Stop current.") == (True, "")
    assert orders.split_stop("cancel the flight plan") == (True, "")
    assert orders.split_stop("stop current, and land at 27") == (True, "land at 27")
    assert orders.split_stop("stop current plan then land on runway 09") == (True, "land on runway 09")
    assert orders.split_stop("stop what you're doing; gear down") == (True, "gear down")
    assert orders.split_stop("stop the plan now")[0] is False        # no separator: not a compound cancel
    assert orders.split_stop("land at 27")[0] is False
    assert orders.split_stop("abort")[0] is False                     # plain abort stays the hard abort


def test_land_runway_direct():
    assert orders.parse("land at 27") == ("land_plane", {"runway": "27"})
    assert orders.parse("land on runway 09") == ("land_plane", {"runway": "09"})
    assert orders.parse("land at 9 at KSC") == ("land_plane", {"runway": "09"})


def test_stop_current_keeps_throttle(monkeypatch):
    ctl = NS(throttle=0.7, sas=False)
    called = []
    monkeypatch.setattr(ksp_actions, "_stop_controllers", lambda: called.append("stop"))
    monkeypatch.setattr(ksp_actions, "_mj_autopilots_off", lambda: called.append("mj"))
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: NS(control=ctl))
    for m in (ksp_actions.plane, ksp_actions.hold, ksp_actions.taxi, ksp_actions.lander):
        if hasattr(m, "active"):
            monkeypatch.setattr(m, "active", lambda: False)
    monkeypatch.setattr(ksp_actions.guard, "busy", lambda: None)
    r = ksp_actions.stop_current()
    assert called == ["stop", "mj"] and ctl.throttle == 0.7 and ctl.sas is True and "throttle unchanged" in r
    assert "stop_current" in ksp_actions.BY_NAME


def test_chat_compound_cancel_then_land(monkeypatch):
    calls = []
    monkeypatch.setattr(ksp_actions, "call_tool", lambda n, a=None: calls.append((n, a)) or f"{n} ok.")
    monkeypatch.setattr(ksp_actions, "pilot_name", lambda: None)
    s = chat.Session()
    reply, tools = s.send("stop current, and land at 27", "local")
    assert calls == [("stop_current", {}), ("land_plane", {"runway": "27"})]
    assert [t["tool"] for t in tools] == ["stop_current", "land_plane"]
    calls.clear()
    reply, tools = s.send("stop current plan", "local")
    assert calls == [("stop_current", {})] and reply == "stop_current ok."