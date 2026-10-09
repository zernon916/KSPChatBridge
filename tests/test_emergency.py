"""Offline: emergency detection, in-character alerts, safety actions, config sabotage, control probe, Systems feed."""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import emergency as em  # noqa: E402
from kspchat import protect, speedcap  # noqa: E402


def S(t, **kw):
    s = {"t": t, "vid": 1, "sit": "flying", "throttle": 1.0, "parts": 20, "stage": 0, "g": 1.0, "crew": 1, "ctrl": True,
         "aoa": 3.0, "vs": 0.0, "speed": 200.0, "who": "Sidry",
         "engines": [{"title": "J-33", "active": True, "thrust": 50000.0, "max_thrust": 60000.0, "air": True},
                     {"title": "J-33", "active": True, "thrust": 50000.0, "max_thrust": 60000.0, "air": True}]}
    s.update(kw)
    return s


def eng(thrust1=50000.0, thrust2=50000.0, rev1=False, mode1=""):
    return [{"title": "J-33", "active": True, "thrust": thrust1, "max_thrust": 60000.0, "air": True, "reversed": rev1, "mode": mode1},
            {"title": "J-33", "active": True, "thrust": thrust2, "max_thrust": 60000.0, "air": True}]


@pytest.fixture
def det():
    d = em.Detector()
    d.tick(S(0))  # baseline (new vessel: no alerts)
    return d


def run(d, t0, t1, **kw):
    evs = []
    for t in range(t0, t1):
        evs += d.tick(S(t, **kw))
    return evs


def test_flameout_once_then_resolved(det):
    assert run(det, 1, 4) == []
    evs = run(det, 4, 12, engines=eng(thrust2=0.0))
    assert [e["kind"] for e in evs] == ["flameout"] and evs[0]["what"] == "Engine 2" and evs[0]["start"]
    assert "relight:2" in evs[0]["actions"] and "level" in evs[0]["actions"]
    evs = run(det, 12, 14, engines=eng())
    assert len(evs) == 1 and not evs[0]["start"] and not evs[0]["quiet"]


def test_spool_up_and_idle_are_not_flameouts(det):
    d = em.Detector()
    d.tick(S(0, engines=eng(0.0, 0.0)))
    assert run(d, 1, 10, engines=eng(0.0, 0.0)) == []          # never ran yet (spooling)
    assert run(det, 1, 10, throttle=0.1, engines=eng(0.0, 0.0)) == []   # idle throttle


def test_air_starvation_opens_intakes(det):
    run(det, 1, 3)
    evs = run(det, 3, 9, intake_air=0.0, engines=eng(0.0, 0.0))
    assert len(evs) == 2 and all("intakes" in e["actions"] for e in evs) and "intake air" in evs[0]["short"]


def test_reverse_geometry_needs_deceleration(det):
    assert run(det, 1, 6, engines=eng(rev1=True)) == []          # nose-pointing thrust but not slowing: no alarm
    evs = []
    for t in range(6, 10):
        evs += det.tick(S(t, speed=200.0 - 5 * (t - 5), engines=eng(rev1=True)))
    assert [e["kind"] for e in evs] == ["reverse"]


def test_reverse_in_flight_only(det):
    e1 = eng(rev1=True, mode1="Reverse")
    e1[0]["rev_mode"] = True
    evs = run(det, 1, 3, engines=e1)
    assert [e["kind"] for e in evs] == ["reverse"] and evs[0]["actions"] == ["no_throttle", "forward"] and evs[0]["engines"] == [1]
    evs = run(det, 3, 5, sit="landed", engines=e1)
    assert len(evs) == 1 and not evs[0]["start"] and evs[0]["quiet"]  # touchdown: reversers are fine now
    assert run(det, 5, 8, sit="flying", rollout=True, engines=e1) == []


def test_parts_lost_but_not_staging(det):
    evs = run(det, 1, 2, parts=18)
    assert evs[0]["kind"] == "parts" and evs[0]["what"] == "2 parts" and evs[0]["one_shot"]
    assert run(det, 2, 3, parts=17, stage=1) == []   # staged: decoupled parts are expected


def test_fuel_overheat_stall_blackout(det):
    k = lambda evs: [(e["kind"], e["start"]) for e in evs]  # noqa: E731
    assert k(run(det, 1, 3, fuel=0.03)) == [("fuel", True)]
    assert k(run(det, 3, 4, fuel=0.2)) == [("fuel", False)]
    assert k(run(det, 4, 5, temp=(0.97, "Mk1 Cockpit"))) == [("overheat", True)]
    assert k(run(det, 5, 6, temp=(0.5, "Mk1 Cockpit"))) == [("overheat", False)]
    assert k(run(det, 6, 9, aoa=25.0, vs=-20.0)) == [("stall", True)]
    assert k(run(det, 9, 12, aoa=5.0)) == [("stall", False)]
    assert k(run(det, 12, 13, g=8.0, ctrl=False)) == [("blackout", True)]
    assert k(run(det, 13, 14, g=1.0, ctrl=True)) == [("blackout", False)]


def SURF(p=True, y=False, r=True, inv=False, auth=100.0, dep=False):
    return ("Elevon", p, y, r, inv, auth, dep)


def test_diff_config():
    old = {"surfaces": [SURF(), SURF()], "wheels": [("Wheel", True, False)], "engines": [("J-33", True, "")]}
    new = {"surfaces": [SURF(p=False), SURF(dep=True)], "wheels": [("Wheel", False, False)], "engines": [("J-33", False, "")]}
    tags = [c[2] for c in em.diff_config(old, new) if c[0]]
    assert tags == ["pitch_lost", "wheels", "engine_off"]                    # flap deploy is not sabotage
    assert [c[2] for c in em.diff_config(old, new, own=True) if c[0]] == ["pitch_lost", "wheels"]
    inv = em.diff_config({"surfaces": [SURF()]}, {"surfaces": [SURF(auth=-100.0)]})
    assert inv[0][0] and inv[0][2] == "inverted"


def test_config_sabotage_and_restore(det, monkeypatch):
    ok = {"surfaces": [SURF(), SURF()]}
    bad = {"surfaces": [SURF(p=False), SURF(p=False)]}
    d = em.Detector()
    d.tick(S(0, cfg=ok))
    evs = d.tick(S(1, cfg=bad))
    assert evs[0]["kind"] == "config" and evs[0]["tag"] == "pitch_lost" and "safe" in evs[0]["actions"]
    posted = []
    monkeypatch.setattr(em, "_engaged", lambda: True)
    lines = em.handle(evs, None, S(1), post=posted.append)
    assert lines[0].startswith("Sidry: The elevators just turned into FLAPS?!") and "2 surfaces lost pitch" in lines[0]
    assert speedcap.bank_limit(100) == em.SAFE_BANK and em.alerts_text().startswith("CONFIG:")
    evs = d.tick(S(2, cfg=ok))
    assert [e["kind"] for e in evs] == ["config_ok"]
    lines = em.handle(evs, None, S(2), post=posted.append)
    assert "back to normal" in lines[0] and em.bank_cap() is None and len(posted) == 2


class FakeCtl:
    throttle, sas = 0.8, False


class FakeEngine:
    def __init__(self):
        self.has_modes, self.mode, self.modes, self.active = True, "Reverse", {"Forward": 1, "Reverse": 2}, True


class FakeV:
    def __init__(self):
        self.control = FakeCtl()
        self.parts = type("P", (), {})()
        self.parts.engines = [FakeEngine()]


def test_reverse_actions_idle_and_forward(monkeypatch):
    monkeypatch.setattr(em, "_engaged", lambda: True)
    v, posted = FakeV(), []
    ev = {"kind": "reverse", "key": "reverse", "start": True, "quiet": False, "one_shot": False, "what": "Engine 1",
          "pct": None, "short": "REVERSE THRUST IN FLIGHT", "actions": ["no_throttle", "forward"], "changes": [], "engines": [1]}
    timers = []
    monkeypatch.setattr(em.threading, "Timer", lambda d, fn: timers.append((d, fn)) or type("T", (), {"start": lambda s: None})())
    line = em.handle([ev], v, S(0), post=posted.append)[0]
    assert line.startswith("Sidry: ") and "idle" in line and "flipping the reversers back" in line
    assert v.control.throttle == 0.0 and 2.0 <= timers[0][0] <= 4.0   # the kerbal fumbles 2-4 s first
    timers[0][1]()
    assert v.parts.engines[0].mode == "Forward"
    assert em.shape("throttle", 0.9) == 0.0 and em.reverse_active()   # autopilots can't add power
    em.handle([dict(ev, start=False, actions=[])], v, S(1), post=posted.append)
    assert em.shape("throttle", 0.9) == 0.9 and len(posted) == 2


def test_level_and_relight(monkeypatch):
    monkeypatch.setattr(em, "_engaged", lambda: True)
    v = FakeV()
    v.parts.engines[0].mode = "Forward"
    ev = {"kind": "flameout", "key": "flameout:1", "start": True, "quiet": False, "one_shot": False, "what": "Engine 1",
          "pct": None, "short": "ENGINE 1 FLAMEOUT", "actions": ["level", "relight:1", "keep_throttle"], "changes": [], "engines": [1]}
    monkeypatch.setattr(em.time, "sleep", lambda s: None)
    line = em.handle([ev], v, S(0, engines=eng(thrust1=0.0)), post=lambda x: None)[0]
    assert "wings level 30 s" in line and "relight tried on Engine 1" in line and "remaining 1 engine" in line
    assert speedcap.bank_limit(100) == em.LEVEL_BANK and v.parts.engines[0].active


def test_probe_eval_and_adapt():
    before = [(0.0, 0.0), (0.15, 0.3)]                      # rolling +2 deg/s
    assert em.eval_probe(0.1, before, [(0.55, 3.0)]) == "normal"
    assert em.eval_probe(0.1, before, [(0.55, -2.0)]) == "inverted"
    assert em.eval_probe(0.1, before, [(0.55, 1.1)]) == "dead"
    assert em.eval_probe(0.1, [(0, 179.0), (0.1, 179.5)], [(0.5, -176.0)], wrap=True) == "normal"
    line = em.probe_result("roll", "inverted", post=lambda x: None)
    assert "BACKWARDS" in line and em.SIGN["roll"] == -1.0 and em.shape("roll", 0.3) == -0.3 and em.shape("pitch", 0.3) == 0.3


def test_systems_feed_and_master():
    em._alert("fuel", "fuel", "FUEL LOW (4%)")
    assert em.master()[0] == "caution"
    em._alert("reverse", "reverse", "REVERSE THRUST IN FLIGHT")
    lvl, seq, text = em.master()
    assert lvl == "warning" and seq >= 2 and "REVERSE" in text
    cfg = {"surfaces": [SURF()], "wheels": [("Wheel", False, False)], "intakes": [("Intake", True)], "gear": False,
           "brakes": False, "chutes": []}
    em.DET.active["reverse"] = "reverse"
    em.SYSTEMS[:] = em.build_systems(S(0, fuel=0.04, intake_air=0.9, engines=eng(rev1=True)), cfg, cfg)
    txt = em.systems_text().splitlines()
    assert txt[0].startswith("master\twarning\t") and txt[-1].startswith("lights\t")
    rows = {ln.split("\t")[2]: ln.split("\t")[1] for ln in txt if ln.startswith("sys\t")}
    assert rows["Engine 1 J-33"] == "fail" and rows["Engine 2 J-33"] == "ok" and rows["Reaction wheels"] == "caution"
    assert rows["Fuel"] == "fail" and rows["Surface 1 Elevon"] == "ok" and rows["Pilot"] == "ok"
    em._clear_alert("fuel")
    em._clear_alert("reverse")
    assert em.master() == ("", 0, "")


def test_protect_leaves_blackout_lines_to_emergency(monkeypatch):
    monkeypatch.setattr(em, "running", lambda: True)
    said = []
    monkeypatch.setattr(protect, "_say", said.append)
    protect.tick({"t": 0, "g": 8.0, "crew": 1, "ctrl": True})
    out = protect.tick({"t": 1, "g": 7.0, "crew": 1, "ctrl": False, "who": "Sidry"})
    assert any("blacked out" in m for m in out) and not any("blacked out" in m for m in said)
    em.handle([{"kind": "blackout", "key": "blackout", "start": True, "quiet": False, "one_shot": False, "what": "",
                "pct": None, "short": "PILOT BLACKED OUT", "actions": ["blackout"], "changes": [], "engines": []}],
              None, S(1), post=lambda x: None)
    assert protect.blackout()