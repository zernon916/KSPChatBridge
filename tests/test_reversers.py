"""Offline: autoland rollout thrust reversers (mode / reverser event / toggle), setting, emergency treats it as normal."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, emergency, reversers, settings  # noqa: E402


class ModeEngine:
    def __init__(self):
        self.active, self.has_modes, self.mode, self.modes = True, True, "Forward", {"Forward": 1, "Reverse": 2}


class Module:
    def __init__(self, events, toggle=False):
        self.events, self.actions, self.toggle, self.fired = list(events), [], toggle, []

    def trigger_event(self, name):
        self.fired.append(name)
        if not self.toggle:
            self.events = ["Forward Thrust"] if name == "Reverse Thrust" else ["Reverse Thrust"]


class EventEngine:
    def __init__(self, module):
        self.active, self.has_modes = True, False
        self.part = type("Part", (), {"modules": [module]})()


class PlainEngine:
    def __init__(self):
        self.active, self.has_modes = True, False
        self.part = type("Part", (), {"modules": []})()


def vessel(*engines):
    v = type("V", (), {})()
    v.parts = type("P", (), {"engines": list(engines)})()
    return v


def test_mode_engine_engage_release_and_own_change():
    e, plain = ModeEngine(), PlainEngine()
    emergency._flags["own_t"] = -1e9
    assert reversers.engage(vessel(plain, e)) == 1 and e.mode == "Reverse" and reversers.engaged()
    assert emergency._flags["own_t"] > 0                 # our own change: no 'who touched the engines' alarm
    assert reversers.release(vessel(plain, e)) == 1 and e.mode == "Forward" and not reversers.engaged()


def test_named_reverser_events():
    m = Module(["Reverse Thrust"])
    e = EventEngine(m)
    assert reversers.engage(vessel(e)) == 1 and m.events == ["Forward Thrust"]
    assert reversers.release(vessel(e)) == 1 and m.events == ["Reverse Thrust"] and m.fired == ["Reverse Thrust", "Forward Thrust"]
    assert reversers.switch(e, False) and m.fired[-1] == "Forward Thrust"   # already forward: nothing fired
    assert len(m.fired) == 2


def test_toggle_only_needs_known_state():
    m = Module(["Toggle Thrust Reverser"], toggle=True)
    e = EventEngine(m)
    assert not reversers.switch(e, False)                 # unknown state: never blind-toggle 'to forward'
    assert reversers.engage(vessel(e)) == 1 and reversers.release(vessel(e)) == 1
    assert m.fired == ["Toggle Thrust Reverser", "Toggle Thrust Reverser"]


def test_no_reverser_behaves_as_before():
    assert reversers.engage(vessel(PlainEngine())) == 0 and not reversers.engaged()
    assert reversers.release(vessel(PlainEngine())) == 0


def test_setting_and_slash_command(monkeypatch):
    assert reversers.enabled()                            # default on
    monkeypatch.setattr(chat.memory, "log_turn", lambda *a, **k: None)
    reply, _ = chat.Session("rv").send("/reversers off", "local")
    assert "off" in reply and settings.get("autoland_reversers") is False and not reversers.enabled()
    assert "on" in reversers.command("on") and reversers.enabled()


def test_emergency_ignores_rollout_reverse():
    rev_eng = [{"title": "J-33", "active": True, "thrust": 50000.0, "max_thrust": 60000.0, "reversed": True,
                "rev_mode": True, "mode": "Reverse"}]
    d = emergency.Detector()
    base = {"vid": 1, "sit": "landed", "speed": 60.0, "throttle": 0.6, "engines": rev_eng, "crew": 1, "ctrl": True}
    d.tick(dict(base, t=0))
    evs = []
    for t in range(1, 6):
        evs += d.tick(dict(base, t=t, speed=60.0 - 5 * t))
    evs += d.tick(dict(base, t=6, sit="flying", rollout=True))   # a skip off the runway mid-rollout
    assert evs == []
    reversers._ENGAGED[:] = [1]
    assert emergency._rollout_rev()
    reversers._ENGAGED.clear()
    assert not emergency._rollout_rev()