"""engine_restart: throttle up with shut-down engines -> one fumble line, delayed relight, follow-up, no spam."""
from types import SimpleNamespace as NS

from kspchat import engine_restart as er, ksp_actions, science


class FakeTimer:
    made = []

    def __init__(self, delay, fn, args=()):
        self.delay, self.fn, self.args, self.daemon = delay, fn, args, False
        FakeTimer.made.append(self)

    def start(self):
        pass


def _vessel(active=(False, True, False), stages=(3, 3, 0), cur=2):
    engines = [NS(active=a, part=NS(stage=s)) for a, s in zip(active, stages)]
    return NS(control=NS(current_stage=cur), parts=NS(engines=engines)), engines


def test_restart_once_with_delay(monkeypatch):
    said = []
    monkeypatch.setattr(science, "post_event", said.append)
    er._pending["on"] = False
    FakeTimer.made.clear()
    v, eng = _vessel()
    note = er.maybe_restart(v, "Sidry", timer=FakeTimer)
    assert "restart" in note.lower() and "no need to call" in note
    assert len(said) == 1 and said[0].startswith("Sidry: ") and said[0][7:] in er.FUMBLE
    t = FakeTimer.made[0]
    assert 3.0 <= t.delay <= 5.0 and t.daemon
    assert not eng[0].active                      # nothing re-lit before the delay
    assert "already" in er.maybe_restart(v, "Sidry", timer=FakeTimer) and len(said) == 1 and len(FakeTimer.made) == 1
    t.fn(*t.args)                                 # timer fires
    assert eng[0].active and not eng[2].active    # stage-0 engine (not fired yet) left alone
    assert said[1][7:] in er.BACK_ON
    assert er.maybe_restart(v, "Sidry", timer=FakeTimer) == ""   # all running: nothing to do


def test_no_restart_when_running_or_flamed_out(monkeypatch):
    said = []
    monkeypatch.setattr(science, "post_event", said.append)
    er._pending["on"] = False
    v, _ = _vessel(active=(True, True), stages=(3, 3))  # flamed-out engines stay active -> not touched
    assert er.maybe_restart(v, "Sidry", timer=FakeTimer) == "" and said == []


def test_call_tool_hook(monkeypatch):
    calls = []
    monkeypatch.setattr(ksp_actions, "_vessel", lambda: "V")
    monkeypatch.setattr(ksp_actions, "pilot_name", lambda: "Sidry")
    monkeypatch.setattr(er, "maybe_restart", lambda v, who: calls.append((v, who)) or " [restart]")
    assert ksp_actions._engine_restart_note("set_throttle", {"value": 0.6}, "Throttle set to 60%.") == " [restart]"
    assert ksp_actions._engine_restart_note("set_throttle", {"value": 0}, "Throttle set to 0%.") == ""
    assert ksp_actions._engine_restart_note("set_gear", {}, "Gear down.") == ""
    assert ksp_actions._engine_restart_note("plane_hold", {}, "plane_hold failed: X") == ""
    assert calls == [("V", "Sidry")]