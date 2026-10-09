"""Blocker 4: second spool after fake 'already spooled' still runs spool_up."""
from types import SimpleNamespace as NS

from kspchat import heli, ksp_actions, propulsion as pr


def test_spool_rejects_disappearing_rotor(monkeypatch):
    monkeypatch.setattr("kspchat.power_mgmt.rotor_spool_block", lambda v: None)
    monkeypatch.setattr(pr, "preflight", lambda *a, **k: ([], []))
    monkeypatch.setattr(pr, "set_brake", lambda *a, **k: None)
    monkeypatch.setattr(pr, "set_rotor", lambda *a, **k: None)
    snapshots = iter([[{"i": 0}, {"i": 1}], [{"i": 0}]])
    monkeypatch.setattr(pr, "rotor_checks", lambda v: next(snapshots))
    ok, report = pr.spool_up(NS(), sleep=lambda _: None)
    assert not ok
    assert "disappeared" in report


def test_second_ground_hover_spools_after_fake_spooled(monkeypatch):
    reset = []
    monkeypatch.setattr(pr, "reset_spool_state", lambda: reset.append(1))
    v = NS(situation="VesselSituation.landed",
           flight=lambda rf=None: NS(surface_altitude=0.5, heading=90.0),
           orbit=NS(body=NS(reference_frame="brf", equatorial_radius=600000.0)))
    info = {"heli": True, "kind": "multirotor (4)", "yaw": "differential torque",
            "lift": [0, 1, 2, 3], "left": [], "right": [], "tail": [], "locked": False}
    spool_calls = []
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: v)
    monkeypatch.setattr(ksp_actions, "_heli_v", lambda vv: info)
    monkeypatch.setattr(heli, "active", lambda: False)
    monkeypatch.setattr(heli, "command", lambda *a, **k: "started")
    monkeypatch.setattr("kspchat.guard.busy", lambda: None)
    monkeypatch.setattr(pr, "spool_up", lambda vv, rotors=None, **kw: spool_calls.append(rotors) or (True, "spool-up OK"))

    heli.STATE["spooled"] = True  # stale from prior flight
    ksp_actions.heli_control("hover", altitude_m=20)
    assert reset
    assert len(spool_calls) == 1
    assert set(spool_calls[0]) == {0, 1, 2, 3}
