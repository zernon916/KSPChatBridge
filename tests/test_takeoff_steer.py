"""Takeoff roll: runway heading only (small damped capped steering), stop instead of ground loops, no turns before
climb-out (> 150 m, climbing), target-steering tools deferred, heli_control on a plane -> runway takeoff."""
from types import SimpleNamespace as NS

from kspchat import hold, ksp_actions, takeoff as tko


def test_ground_steer_small_damped_capped():
    w, r = tko.ground_steer(2.0, 0.0, 30.0)
    assert w < 0 < r and abs(w) <= 0.1                      # small correction to the right (kRPC wheel + = left)
    for spd in (5, 20, 40, 80):                             # never saturates the nose wheel
        w, r = tko.ground_steer(25.0, 0.0, spd)
        assert abs(w) <= tko.GS_CAP_SLOW + 1e-9 and abs(r) <= tko.GS_CAP_SLOW + 1e-9
    assert abs(tko.ground_steer(25.0, 0.0, 60)[0]) <= tko.GS_CAP_FAST + 1e-9
    w_fast_turn, _ = tko.ground_steer(5.0, 8.0, 30.0)        # already swinging right fast: damping counter-steers
    assert w_fast_turn > 0


def test_ground_runaway():
    assert tko.ground_runaway(5.0, 3.0, 40.0) == ""
    assert "off the runway heading" in tko.ground_runaway(35.0, 3.0, 40.0)
    assert "off the centerline" in tko.ground_runaway(2.0, 60.0, 20.0)
    assert tko.ground_runaway(35.0, 0.0, 1.0) == ""          # parked at an angle, not rolling


def test_climbout_gate():
    assert not tko.climbout_done(100.0, 10.0, False)
    assert not tko.climbout_done(200.0, -1.0, False)
    assert not tko.climbout_done(200.0, 5.0, True)
    assert tko.climbout_done(160.0, 3.0, False)


def test_steer_tools_deferred_during_takeoff(monkeypatch):
    monkeypatch.setattr(hold, "active", lambda: True)
    monkeypatch.setitem(hold.STATUS, "phase", "takeoff")
    started = []
    monkeypatch.setattr(ksp_actions.threading, "Thread", lambda target, daemon, name: NS(start=lambda: started.append(name)))
    called = []
    monkeypatch.setitem(ksp_actions.BY_NAME, "land_at_ksc", lambda **k: called.append(k) or "landing")
    r = ksp_actions.call_tool("land_at_ksc", {})
    assert r.startswith("Deferred:") and "150 m" in r and called == [] and started == ["defer-land_at_ksc"]
    monkeypatch.setitem(hold.STATUS, "phase", "holds")
    hold.STATUS.pop("climbout_hdg", None)
    assert ksp_actions.call_tool("land_at_ksc", {}) == "landing"


def test_heli_control_on_plane_redirects_to_takeoff(monkeypatch):
    v = NS(situation="VesselSituation.landed")
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions, "_heli_v", lambda v: None)
    monkeypatch.setattr(ksp_actions, "_planeish", lambda v: True)
    monkeypatch.setattr(ksp_actions, "takeoff", lambda altitude_m=300: f"takeoff to {altitude_m:.0f}")
    r = ksp_actions.heli_control("hover", altitude_m=4000)
    assert r.startswith("Not a helicopter") and r.endswith("takeoff to 4000")