"""Offline: Flight Plan / chat limit overrides ('override bank|pitch|speed|altitude N', '*' ask / '***' pre-authorized,
'override off', 'authorise all'). Pure in-memory speedcap state; no KSP, no network, no live files."""
import copy
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import backends, chat, flightplan as fp, ksp_actions, orders, speedcap  # noqa: E402


@pytest.fixture(autouse=True)
def clean(monkeypatch):
    saved, pend = copy.deepcopy(speedcap._state), dict(speedcap._pending)
    speedcap.clear_override("all")
    speedcap._pending.clear()
    monkeypatch.setattr(ksp_actions, "pilot_name", lambda: None)
    monkeypatch.setattr(fp, "_tool", lambda name, **a: ksp_actions.call_tool(name, a))  # test_flightplan fakes it
    yield
    speedcap._state.clear()
    speedcap._state.update(saved)
    speedcap._pending.clear()
    speedcap._pending.update(pend)


def test_orders_parse_overrides():
    assert orders.parse("override bank 50") == ("set_override", {"kind": "bank", "value": 50.0, "authority": "ask"})
    assert orders.parse("override bank 50 *")[1]["authority"] == "ask"
    assert orders.parse("override speed 400 m/s ***") == ("set_override", {"kind": "speed", "value": 400.0, "authority": "pre"})
    assert orders.parse("override altitude 20 km *")[1]["value"] == 20000.0
    assert orders.parse("override alt 12000")[1]["kind"] == "altitude"
    assert orders.parse("override pitch 30 deg")[1] == {"kind": "pitch", "value": 30.0, "authority": "ask"}
    assert orders.parse("override off") == ("set_override", {"kind": "all", "value": "off"})
    assert orders.parse("override bank off") == ("set_override", {"kind": "bank", "value": "off"})
    assert orders.parse("authorise all") == ("authorise_all", {}) and orders.parse("Authorize all.") == ("authorise_all", {})


def test_defaults_and_bank_limit():
    assert speedcap.bank_limit(100) == 20.0 and speedcap.bank_limit(300) == 15.0
    assert speedcap.climb_pitch(7.5) == 7.5 and speedcap.limits() == (200.0, 220.0)


def test_ask_then_yes_applies(monkeypatch):
    s = chat.Session("ingame")
    r, _ = s.send("override bank 50 *", "local")
    assert "Override the bank limit 50 deg" in r and "(yes/no)" in r and "[Relay" not in r, r
    assert speedcap.bank_limit(300) == 15.0                       # nothing applied before the yes
    r, tools = s.send("yes", "local")
    assert r.startswith("Override: bank limit 50 deg") and tools[0]["tool"] == "set_override", r
    assert speedcap.bank_limit(300) == 50.0 and speedcap.bank_limit(100) == 50.0
    assert "Bank override 50 deg" in speedcap.status_text()
    assert s.send("override bank off", "local")[0].startswith("Bank override off")
    assert speedcap.bank_limit(300) == 15.0


def test_ask_then_no_keeps_defaults():
    s = chat.Session("ingame")
    s.send("override pitch 30", "local")
    r, _ = s.send("no", "local")
    assert r.startswith("Override not granted - default limits kept"), r
    assert speedcap.climb_pitch(10.0) == 10.0 and speedcap.pending("ingame") is None


def test_pre_authorized_applies_directly():
    r = ksp_actions.call_tool("set_override", {"kind": "speed", "value": 400, "authority": "pre"})
    assert r.startswith("Override: speed cap 400 m/s EAS") and speedcap.limits()[1] == 400.0
    r = ksp_actions.call_tool("set_override", {"kind": "altitude", "value": 20000, "authority": "pre"})
    assert speedcap._state["ceiling"] == 20000.0 and speedcap.ceiling(11000.0) >= 20000.0
    ksp_actions.call_tool("set_override", {"kind": "pitch", "value": 25, "authority": "pre"})
    assert speedcap.climb_pitch(10.0) == 25.0
    assert ksp_actions.call_tool("set_override", {"kind": "all", "value": "off"}).startswith("All overrides off")
    assert speedcap.limits() == (200.0, 220.0) and speedcap.climb_pitch(10.0) == 10.0 and speedcap._state["ceiling"] is None


def test_range_and_kind_errors():
    assert "must be 5-80" in ksp_actions.call_tool("set_override", {"kind": "bank", "value": 120, "authority": "pre"})
    assert "kinds" in ksp_actions.call_tool("set_override", {"kind": "yaw", "value": 5})
    assert speedcap.bank_limit(100) == 20.0 and speedcap.pending("ingame") is None


def test_authorise_all_skips_questions():
    s = chat.Session("ingame")
    assert s.send("authorize all", "local")[0].startswith("Authority granted for every override")
    r, _ = s.send("override bank 45", "local")
    assert r.startswith("Override: bank limit 45 deg") and speedcap.pending("ingame") is None
    assert speedcap._state["authority"] == {"speed": True, "altitude": True} and "Authorise all is ON" in speedcap.status_text()
    s.send("override off", "local")
    assert not speedcap.all_authorised() and not any(speedcap._state["authority"].values()) and speedcap.bank_limit(100) == 20.0


def test_plan_parse_fmt_markers():
    text = ("override bank 50\noverride speed 400 ***\noverride altitude 20 km *\noverride pitch off\n"
            "override off\nauthorize all\ncircle 2 laps left bank 45")
    out = fp.check(text)
    assert out.startswith("OK, 7 steps"), out
    assert ("override bank 50 * | override speed 400 *** | override altitude 20000 * | override pitch off | "
            "override off | authorise all | circle 2 laps left bank 45") in out
    steps, errs = fp.parse(text)
    assert errs == [] and steps[1]["auth"] == "pre"
    assert {k: steps[0][k] for k in ("op", "kind", "value", "auth")} == {"op": "override", "kind": "bank", "value": 50.0, "auth": "ask"}
    again, _ = fp.parse("\n".join(fp.fmt(st) for st in steps))
    assert [fp.fmt(st) for st in again] == [fp.fmt(st) for st in steps]


def test_plan_parse_errors():
    for bad in ("override bank", "override bank 200", "override all 30", "override 30"):
        steps, errs = fp.parse(bad)
        assert steps == [] and errs and "override" in errs[0][2], (bad, errs)


def _run_world(monkeypatch, answer):
    said = []
    s = chat.Session("ingame")

    def sleep(dt):
        if speedcap.pending("ingame"):
            s.resolve_override(answer)
    monkeypatch.setattr(fp, "_say", said.append)
    monkeypatch.setattr(fp, "_sleep", sleep)
    monkeypatch.setattr(fp, "_now", lambda: 0.0)
    monkeypatch.setattr(fp, "_stopped", lambda: False)
    return said


def test_plan_step_asks_and_yes_applies(monkeypatch):
    said = _run_world(monkeypatch, "yes")
    ok, msg = fp._do_override({"op": "override", "kind": "bank", "value": 40.0, "auth": "ask"}, [])
    assert ok is True and "granted" in msg and speedcap.bank_limit(300) == 40.0
    assert any("Override the bank limit 40 deg" in m and "answer yes/no in chat" in m for m in said), said


def test_plan_step_no_keeps_defaults_and_continues(monkeypatch):
    _run_world(monkeypatch, "no")
    ok, msg = fp._do_override({"op": "override", "kind": "speed", "value": 400.0, "auth": "ask"}, [])
    assert ok is True and "default limits kept" in msg and speedcap.limits() == (200.0, 220.0)


def test_plan_step_timeout_keeps_defaults(monkeypatch):
    clock = [0.0]
    monkeypatch.setattr(fp, "_say", lambda m: None)
    monkeypatch.setattr(fp, "_sleep", lambda dt: clock.__setitem__(0, clock[0] + 30.0))
    monkeypatch.setattr(fp, "_now", lambda: clock[0])
    monkeypatch.setattr(fp, "_stopped", lambda: False)
    ok, msg = fp._do_override({"op": "override", "kind": "bank", "value": 40.0, "auth": "ask"}, [])
    assert ok is True and "default limits kept" in msg and speedcap.bank_limit(100) == 20.0
    assert speedcap.pending("ingame") is None


def test_plan_step_pre_and_authall(monkeypatch):
    said = _run_world(monkeypatch, "no")
    ok, msg = fp._do_override({"op": "override", "kind": "bank", "value": 35.0, "auth": "pre"}, [])
    assert ok and msg.startswith("Override: bank limit 35") and said == []
    fp._do_override({"op": "override", "kind": "all", "value": "off"}, [])
    ok, msg = fp._do_authall({"op": "authall"}, [])
    assert ok and speedcap.all_authorised()
    ok, msg = fp._do_override({"op": "override", "kind": "pitch", "value": 20.0, "auth": "ask"}, [])
    assert ok and speedcap.climb_pitch(10.0) == 20.0 and said == []


def _draft(monkeypatch, ai_text, request):
    monkeypatch.setattr(fp, "_context", lambda: ("Test Jet, flying over Kerbin", True))
    monkeypatch.setattr(fp, "_runway_names", lambda: ["KSC"])
    monkeypatch.setattr(backends, "resolve", lambda b, m=None: ("http://x", "k", "m"))
    monkeypatch.setattr(chat, "_post", lambda url, body, key: {"choices": [{"message": {"content": ai_text}}]})
    return fp.draft("local", request=request)


def test_ai_fill_cannot_pre_authorize(monkeypatch):
    out = _draft(monkeypatch, "override bank 40 ***\nauthorise all\ncircle 2 laps", "fly some laps")
    assert "override bank 40 *" in out and "***" not in out and "authorise all" not in out, out
    out = _draft(monkeypatch, "override bank 40 ***\nauthorise all\ncircle 2 laps", "laps, override bank 40 *** and authorise all")
    assert "override bank 40 ***" in out and "authorise all" in out, out
    assert "ALWAYS end it" in fp.DRAFT_SYSTEM and "Never write '***'" in fp.DRAFT_SYSTEM