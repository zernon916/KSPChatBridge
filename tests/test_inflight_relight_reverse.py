"""In-flight engine shutdown -> fumble + relight; reverse thrust in flight -> 'BACKWARDS?!', fumble 2-4 s, forward."""
from types import SimpleNamespace as NS

from kspchat import emergency, engine_restart, reversers, science


def _cfg(engines):
    return {"engines": engines}


def test_engine_off_in_flight_adds_relight(monkeypatch):
    w = emergency.Detector()
    base = {"vid": 1, "sit": "flying", "t": 0.0, "engines": [], "cfg": None}
    w.tick(dict(base, cfg={"engines": [("J-33", True, "")]}))
    w.tick(dict(base, t=1.0, cfg={"engines": [("J-33", True, "")]}))
    out = w.tick(dict(base, t=2.0, cfg={"engines": [("J-33", False, "")]}))
    cfg_ev = [e for e in out if e["kind"] == "config"]
    assert cfg_ev and "relight_off" in cfg_ev[0]["actions"]
    w2 = emergency.Detector()
    landed = dict(base, sit="landed")
    w2.tick(dict(landed, cfg={"engines": [("J-33", True, "")]}))
    out = w2.tick(dict(landed, t=2.0, cfg={"engines": [("J-33", False, "")]}))
    assert not any("relight_off" in e.get("actions", []) for e in out)


def test_relight_action_uses_engine_restart(monkeypatch):
    calls = []
    monkeypatch.setattr(engine_restart, "maybe_restart", lambda v, who: calls.append(who) or " restarting")
    r = emergency._act("relight_off", {"engines": [1]}, "V", {"who": "Sidry"})
    assert calls == ["Sidry"] and "restarting" in r


def test_forward_after_fumble(monkeypatch):
    said, made = [], []
    monkeypatch.setattr(science, "post_event", said.append)
    monkeypatch.setattr(emergency, "_forward", lambda v, idxs: len(idxs))

    class T:
        def __init__(self, d, fn):
            self.d, self.fn, self.daemon = d, fn, False
            made.append(self)

        def start(self):
            pass
    emergency._forward_later("V", [1], "Sidry", timer=T)
    assert 2.0 <= made[0].d <= 4.0 and made[0].daemon and said == []
    made[0].fn()
    assert said and said[0].startswith("Sidry: ") and said[0][7:] in emergency.FWD_DONE
    assert "Why are we going BACKWARDS?!" in emergency.LINES["reverse"]


def test_module_reversed_states(monkeypatch):
    reversers._NO_MODULE.clear()
    mod = lambda evs, fields=None: NS(events=evs, actions=[], fields=fields or {})
    e = lambda m, oid: NS(part=NS(modules=[m], _object_id=oid))
    assert reversers.module_reversed(e(mod(["Forward Thrust"]), 1)) is True
    assert reversers.module_reversed(e(mod(["Reverse Thrust"]), 2)) is False
    assert reversers.module_reversed(e(mod(["Toggle Thrust Reverser"], {"Reverser": "Deployed"}), 3)) is True
    assert reversers.module_reversed(e(mod(["Toggle Thrust Reverser"], {"Reverser": "Retracted"}), 4)) is False
    plain = NS(part=NS(modules=[NS(events=["Activate Engine"], actions=[], fields={})], _object_id=5))
    assert reversers.module_reversed(plain) is False and reversers._NO_MODULE[5]


def test_reverse_detected_in_flight_not_on_rollout(monkeypatch):
    monkeypatch.setattr(emergency, "_own_reversal", lambda: False)
    w = emergency.Detector()
    eng = [{"active": True, "rev_module": True, "reversed": True, "thrust": 1.0, "max_thrust": 50.0}]
    base = {"vid": 1, "sit": "flying", "t": 0.0, "engines": eng, "speed": 100.0}
    w.tick(dict(base))
    out = w.tick(dict(base, t=1.0))
    assert any(e["kind"] == "reverse" and e["start"] for e in out)
    w2 = emergency.Detector()
    w2.tick(dict(base, sit="landed"))
    out = w2.tick(dict(base, sit="landed", t=1.0))
    assert not any(e["kind"] == "reverse" and e["start"] for e in out)