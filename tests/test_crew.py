"""Offline: crew intercom chatter (pilot MAYDAY stays separate; other crew react, rate-limited; AI lines with a
deadline and canned fallback; repair offer). No live files, no AI services (generators are injected)."""
import random
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import crew, emergency as em  # noqa: E402

CREW = [("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist"), ("Bill Kerman", "Engineer"), ("Gusfrid Kerman", "Tourist")]


def setup_function(_):
    crew.reset()
    crew._rng.seed(1)


import pytest  # noqa: E402


@pytest.fixture(autouse=True)
def _no_jokes_or_snaps(monkeypatch):  # (dad jokes / pilot snaps have their own tests in test_trip_language.py)
    monkeypatch.setattr(crew, "_dad", lambda name: False)
    monkeypatch.setattr(crew, "SNAP_P", 0.0)
    monkeypatch.setattr(crew, "SNAP_JOKE_P", 0.0)


def ev(kind="parts", **kw):
    e = {"kind": kind, "key": kind, "start": True, "quiet": False, "what": "left main wheel", "lost": ["left main wheel"],
         "short": "PARTS LOST", "actions": []}
    e.update(kw)
    return e


def test_slots_pilot_excluded_rate_limited_max_two():
    p = crew.slots(ev(), CREW, "Sidry Kerman", now=0.0, rng=random.Random(2))
    names = [n for n, _, _ in p]
    assert len(p) == 2 and "Sidry Kerman" not in names and names[0] == "Bill Kerman"   # damage: engineer first
    eng = next(t for n, tr, t in p if tr == "engineer")
    assert "left main wheel" in eng
    again = crew.slots(ev(), CREW, "Sidry Kerman", now=5.0, rng=random.Random(3))
    assert [n for n, _, _ in again] == [n for n in ("Bob Kerman", "Gusfrid Kerman") if n not in names]  # 10 s each
    assert crew.slots(ev(), CREW, "Sidry Kerman", now=6.0) == []
    assert len(crew.slots(ev(), CREW, "Sidry Kerman", now=20.0)) == 2
    assert crew.slots(ev(start=False), CREW, "Sidry", now=99.0) == [] and crew.slots(ev(quiet=True), CREW, "Sidry", now=99.0) == []
    solo = crew.slots(ev("stall"), [("Jeb Kerman", "Pilot")], "Jeb Kerman", now=0.0)
    assert solo == []                                                           # only the pilot aboard


def test_intercom_format():
    assert crew.fmt("Bob Kerman", "Scientist", "WHAT IS GOING ON?!", crackle=False) == "[INTERCOM] Bob (Sci): WHAT IS GOING ON?!"
    assert crew.fmt("Bill Kerman", "Engineer", "Wheel's gone!", crackle=True) == "[COMMS] Bill (Eng): Wheel's gone! -- over"
    assert crew.fmt("Gus Kerman", "Tourist", "AAAH!", crackle=False).startswith("[INTERCOM] Gus (Tourist):")


def test_prompt_facts_and_cleaning():
    from kspchat import personality
    p = crew.prompt("Bob Kerman", "scientist", crew.facts_for(ev(), g=4.2))
    assert p == ("You are Bob, " + personality.describe("Bob Kerman", "scientist") + ", aboard a Kerbal spacecraft. "
                 "This is happening: the craft just lost parts: left main wheel, pulling 4.2 G. What do you shout over "
                 "the intercom? One short line.")
    assert crew.facts_for(ev("stall", what="")) == "the plane is stalling"
    assert crew.clean('Bob Kerman: "The wheel is GONE?!"') == "The wheel is GONE?!"
    assert crew.clean("<think>hmm</think>\nWHAT WAS THAT?!") == "WHAT WAS THAT?!"
    assert crew.clean("I've fixed the wheel, no worries!") is None             # crew can't act
    assert crew.clean("I'll eject now!") is None
    assert crew.clean('{"tool": "eject_kerbal"}') is None                        # tool calls
    assert crew.clean("call deploy_parachutes") is None
    assert crew.clean(" ".join(["AAAH"] * 30)) is None                            # too long
    assert crew.clean("") is None


def test_speak_ai_line_and_fallbacks(monkeypatch):
    monkeypatch.setattr(crew, "enabled", lambda: True)
    posted = []
    crew.speak(ev(), CREW, "Sidry Kerman", post=posted.append, gen=lambda n, t, f: "Wheel's gone! WHEEL'S GONE!", wait=True)
    assert len(posted) == 2 and all("Wheel's gone! WHEEL'S GONE!" in x for x in posted)
    assert any(x.startswith("[COMMS] Bill (Eng):") for x in posted)
    crew.reset()
    posted.clear()

    def slow(n, t, f):
        time.sleep(1.0)
        return "too late"
    t0 = time.time()
    ths = crew.speak(ev("stall", lost=[], what=""), CREW, "Sidry Kerman", post=posted.append, gen=slow, timeout=0.2)
    assert time.time() - t0 < 0.1                                               # the caller (pilot) never waits
    for th in ths:
        th.join(2.0)
    assert len(posted) == 2 and not any("too late" in x for x in posted)       # deadline -> canned
    crew.reset()
    posted.clear()
    crew.speak(ev(), CREW, "Sidry Kerman", post=posted.append, gen=lambda *a: "I've repaired the wheel.", wait=True)
    assert len(posted) == 2 and not any("repaired" in x for x in posted)       # action claim -> canned

    def boom(*a):
        raise RuntimeError("no model")
    crew.reset()
    posted.clear()
    crew.speak(ev(), CREW, "Sidry Kerman", post=posted.append, gen=boom, wait=True)
    assert len(posted) == 2
    monkeypatch.setattr(crew, "enabled", lambda: False)
    crew.reset()
    assert crew.speak(ev(), CREW, "Sidry Kerman", post=posted.append, gen=boom, wait=True) == []


def test_repair_offer_once_when_stopped(monkeypatch):
    monkeypatch.setattr(crew, "enabled", lambda: True)
    posted = []
    assert crew.speak_repair(1, CREW, ["left main wheel"], "flying", 50.0, post=posted.append) is None
    assert crew.speak_repair(1, CREW, ["left main wheel"], "landed", 5.0, post=posted.append) is None   # still rolling
    crew.speak_repair(1, CREW, ["left main wheel"], "landed", 0.2, post=posted.append, gen=lambda *a: "Want me to patch that wheel, boss?", wait=True)
    assert posted == ["[COMMS] Bill (Eng): Want me to patch that wheel, boss?"] or posted == ["[COMMS] Bill (Eng): Want me to patch that wheel, boss? -- over"]
    assert crew.speak_repair(1, CREW, ["left main wheel"], "landed", 0.0, post=posted.append) is None   # once
    no_eng = [c for c in CREW if c[1] != "Engineer"]
    assert crew.repair_offer(2, no_eng, ["x"], "landed", 0.0) is None
    line = crew.repair_offer(3, CREW, ["left main wheel"], "landed", 0.0, rng=random.Random(0))
    assert line.startswith("[COMMS] Bill (Eng): ") and "left main wheel" in line


def test_command(monkeypatch):
    store = {}
    from kspchat import settings
    monkeypatch.setattr(settings, "put", lambda k, v: store.__setitem__(k, v))
    monkeypatch.setattr(settings, "get", lambda k: store.get(k))
    assert crew.enabled()                                                      # default on
    assert crew.command(" chatter off") == "Crew intercom chatter off." and not crew.enabled()
    assert crew.command("chatter on") == "Crew intercom chatter on." and crew.enabled()
    assert "is on" in crew.command("")


def test_part_the_no_double_article():
    assert crew._part(ev()) == "left main wheel"
    assert crew._part_the(ev()) == "the left main wheel"
    bare = ev(lost=[], what="")
    assert crew._part(bare) == "that part"
    assert crew._part_the(bare) == "that part"
    cfg = ev("config", lost=[], what="", changes=["Elevon: deploy direction INVERTED"])
    assert crew._part(cfg) == "Elevon"
    assert crew._part_the(cfg) == "the Elevon"


def test_config_sabotage_no_part_lost_chatter(monkeypatch):
    monkeypatch.setattr(crew, "enabled", lambda: True)
    posted = []
    crew.speak(ev("config", lost=[], what="", changes=["Elevon: INVERTED"], tag="inverted"), CREW, "Sidry Kerman",
               post=posted.append, gen=lambda *a: "nope", wait=True)
    assert posted == []


def test_parts_count_only_no_chatter(monkeypatch):
    monkeypatch.setattr(crew, "enabled", lambda: True)
    posted = []
    crew.speak(ev("parts", lost=[], what="2 parts"), CREW, "Sidry Kerman", post=posted.append, wait=True)
    assert posted == []


def test_handle_posts_pilot_first_then_crew(monkeypatch):
    posted = []
    started = []
    monkeypatch.setattr(crew, "speak", lambda e, c, p, g=1.0, post=None, **kw: started.append((e["kind"], p, len(c))))
    s = {"who": "Sidry", "who_full": "Sidry Kerman", "crew_list": CREW, "g": 1.0, "vid": 1}
    em.handle([ev("stall", key="stall")], None, s, post=posted.append)
    assert posted[0].startswith("Sidry: ") and started == [("stall", "Sidry Kerman", 4)]
    em.handle([dict(ev("stall", key="stall"), start=False)], None, s, post=posted.append)
    assert len(started) == 1                                                   # no crew lines on 'resolved'