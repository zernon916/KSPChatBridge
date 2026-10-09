"""Blocker 4: second spool after fake 'already spooled' still runs spool_up."""
from types import SimpleNamespace as NS

from kspchat import heli, ksp_actions, propulsion as pr, telemetry as tel


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


def test_spool_unknown_rpm_not_treated_as_zero(monkeypatch):
    monkeypatch.setattr("kspchat.power_mgmt.rotor_spool_block", lambda v: None)
    monkeypatch.setattr(pr, "preflight", lambda *a, **k: ("", []))
    monkeypatch.setattr(pr, "set_brake", lambda *a, **k: None)
    monkeypatch.setattr(pr, "set_rotor", lambda *a, **k: None)
    row = dict(i=0, label="Main", rpm=None, rpm_limit=400, brake=0, torque=100,
               motor_on=True, sample=("game", 1))
    monkeypatch.setattr(pr, "rotor_checks", lambda v: [row])
    tel.accept(dict(version=1, session="game", sequence=1, vessel_id="v", rpc_vessel_id="1", rotors=[]))
    clock = [0.0]
    monkeypatch.setattr(pr.time, "time", lambda: clock[0])
    def sleep(dt):
        clock[0] += dt
    ok, report = pr.spool_up(NS(_object_id=1), wait_s=30, sleep=sleep)
    assert not ok
    assert "0 RPM" not in report
    assert "telemetry RPM missing" in report
