from types import SimpleNamespace as NS
import pytest
from kspchat import telemetry as t, propulsion as p


@pytest.fixture(autouse=True)
def clean():
    t.reset()
    yield
    t.reset()


def snapshot(sequence=1, session="game"):
    return dict(version=1, session=session, sequence=sequence, vessel_id="vessel-guid", rpc_vessel_id="10",
                rotors=[dict(part_id="42", rpc_part_id="20", rpm=380, rpm_limit=400,
                             torque=100, brake=0, motor_on=True, label="Main Rotor")])


def test_authoritative_sample_overrides_stale_field():
    t.accept(snapshot())
    m = NS(part=NS(_object_id=20), fields={"Current RPM": "0"})
    assert p._live_rotor(NS(_object_id=10), m)["rpm"] == 380


def test_legacy_typed_api_overrides_stale_field():
    api = NS(current_rpm=380, target_rpm=400, torque_limit=100, brake_percentage=0, motor_engaged=True)
    m = NS(part=NS(robotic_rotor=api), fields={"Current RPM": "0"})
    assert p._live_rotor(NS(), m)["rpm"] == 380


def test_stale_wrong_vessel_and_missing_rotor_never_fall_back(monkeypatch):
    t.accept(snapshot())
    assert t.rotor(NS(_object_id=11), NS(_object_id=20)) == (False, None)
    assert t.rotor(NS(_object_id=10), NS(_object_id=21)) == (False, None)
    monkeypatch.setattr(t.time, "monotonic", lambda: t._received + 3)
    assert p._live_rotor(NS(_object_id=10), NS(part=NS(_object_id=20)))["rpm"] is None


def test_empty_rpc_ids_match_part_and_vessel_game_ids():
    s = snapshot()
    s["rpc_vessel_id"] = ""
    s["rotors"][0]["rpc_part_id"] = ""
    s["state"] = dict(resources={"ElectricCharge": 0.5})
    t.accept(s)
    v = NS(id="vessel-guid")
    part = NS(flight_id="42")
    seen, row = t.rotor(v, part)
    assert seen and row["rpm"] == 380
    assert t.state(v)["resources"]["ElectricCharge"] == 0.5
    assert t.state(NS(_object_id=11)) is None


def test_seen_but_no_row_falls_back_to_typed_api():
    t.accept(snapshot())
    api = NS(current_rpm=220, target_rpm=400, torque_limit=100, brake_percentage=0, motor_engaged=True)
    m = NS(part=NS(_object_id=99, robotic_rotor=api), fields={"Current RPM": "0"})
    assert p._live_rotor(NS(_object_id=10), m)["rpm"] == 220


def test_zero_is_real_and_repeated_sample_has_same_identity():
    s = snapshot(); s["rotors"][0]["rpm"] = 0
    t.accept(s)
    v, part = NS(_object_id=10), NS(_object_id=20)
    assert t.rotor(v, part)[1]["rpm"] == 0
    assert t.rotor(v, part)[1]["sample"] == ("game", 1)
    with pytest.raises(ValueError): t.accept(s)
    t.accept(snapshot(2))
    assert t.rotor(v, part)[1]["sample"] == ("game", 2)


def test_revert_new_session_retires_old_session():
    t.accept(snapshot())
    t.accept(snapshot(session="revert"))
    with pytest.raises(ValueError): t.accept(snapshot(50))


@pytest.mark.parametrize("value", [float("nan"), float("inf"), -1, "380", True])
def test_invalid_rpm_rejected(value):
    s = snapshot(); s["rotors"][0]["rpm"] = value
    with pytest.raises(ValueError): t.accept(s)


def test_duplicate_identity_rejected():
    s = snapshot(); s["rotors"] *= 2
    with pytest.raises(ValueError): t.accept(s)


def test_http_accepts_telemetry_and_rejects_bad_version():
    from kspchat.http_server import Handler
    class Request(Handler):
        def __init__(self):
            self.path = "/telemetry/rotors"
            self.headers = {"Content-Type": "application/json"}
            self.payload = snapshot()
        def _body(self): return self.payload
        def _send(self, code, body, **kwargs): self.result = code, body
    request = Request(); request.do_POST()
    assert request.result[0] == 200
    request.payload["version"] = 2; request.do_POST()
    assert request.result[0] == 400


def test_unknown_rpm_does_not_repitch_airborne_prop(monkeypatch):
    monkeypatch.setattr(p, "has_props", lambda v: True)
    monkeypatch.setattr(p, "rotor_rpm", lambda v: None)
    def fail(*a, **k): raise AssertionError("unknown RPM must not change blade pitch")
    monkeypatch.setattr(p, "set_blades", fail)
    monkeypatch.setattr(p, "set_rotor", lambda *a, **k: "torque held")
    p.PropGovernor().tick(NS(), dict(speed=60, sit="flying", throttle=.5), True, 1)


def test_resource_and_temperature_consumers_use_local_batch():
    from kspchat import emergency
    s = snapshot(); s["state"] = dict(resources={"ElectricCharge": .8}, temperature=.3, hottest="Rotor")
    t.accept(s)
    v = NS(_object_id=10)  # no kRPC resources or parts: any poll would fail
    assert emergency._ratio(v, "ElectricCharge") == .8
    assert emergency._ratio(v, "LiquidFuel") is None
    assert emergency._hottest(v) == (.3, "Rotor")
    assert t.state(NS(_object_id=11)) is None


def test_invalid_batched_state_rejected():
    s = snapshot(); s["state"] = dict(resources={"ElectricCharge": float("nan")})
    with pytest.raises(ValueError): t.accept(s)


def test_spool_cannot_succeed_by_reusing_one_snapshot(monkeypatch):
    monkeypatch.setattr("kspchat.power_mgmt.rotor_spool_block", lambda v: None)
    monkeypatch.setattr(p, "preflight", lambda *a, **k: ([], []))
    monkeypatch.setattr(p, "set_brake", lambda *a, **k: None)
    monkeypatch.setattr(p, "set_rotor", lambda *a, **k: None)
    row = dict(i=0, label="Main", rpm=400, rpm_limit=400, brake=0, torque=100,
               motor_on=True, sample=("game", 1))
    monkeypatch.setattr(p, "rotor_checks", lambda v: [row])
    clock = [0]
    monkeypatch.setattr(p.time, "time", lambda: clock[0])
    def sleep(dt): clock[0] += dt
    assert not p.spool_up(NS(), wait_s=2, sleep=sleep)[0]
