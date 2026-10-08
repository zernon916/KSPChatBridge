"""Offline: crew small talk on calm cruises. No live files, no AI services."""
import random
import sys
import threading
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import crew, emergency as em  # noqa: E402

CREW = [("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist"), ("Bill Kerman", "Engineer"), ("Gus Kerman", "Tourist")]


@pytest.fixture(autouse=True)
def _fresh(monkeypatch):
    crew.reset()
    st = {}
    from kspchat import settings
    monkeypatch.setattr(settings, "put", lambda k, v: st.__setitem__(k, v))
    monkeypatch.setattr(settings, "get", lambda k: st.get(k))
    monkeypatch.setattr(crew, "CRACKLE_P", 0.0)
    return st


def test_talk_gap_and_pairs():
    long_ = [crew.talk_gap(3600.0, n, random.Random(n)) for n in range(1, 30)]
    assert all(crew.TALK_GAP_MIN_S <= g <= crew.TALK_GAP_MAX_S for g in long_)
    shorts = [crew.talk_gap(200.0, 0, random.Random(i)) for i in range(300)]
    assert 0.1 < sum(g is not None for g in shorts) / 300 < 0.3                  # rare on short trips
    assert crew.talk_gap(200.0, 1, random.Random(0)) is None                      # and at most once
    p = crew.pick_pair(CREW, "Sidry Kerman", random.Random(1))
    assert len(p) == 2 and "Sidry Kerman" not in [n for n, _ in p] and p[0][0] != p[1][0]
    duo = crew.pick_pair([("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist")], "Sidry Kerman", random.Random(2))
    assert sorted(n for n, _ in duo) == ["Bob Kerman", "Sidry Kerman"]          # pilot + the only other kerbal
    assert crew.pick_pair([("Jeb Kerman", "Pilot")], "Jeb Kerman") is None


def test_conversation_alternates_with_transcript_and_filter():
    posted, prompts = [], []

    def gen(name, trait, facts, text=None):
        prompts.append((name, text))
        return f"{name.split()[0]} says shit about stew #{len(prompts)}"
    pair = [("Bob Kerman", "scientist"), ("Bill Kerman", "engineer")]
    n = crew.converse(pair, "the new stew in the cafeteria", 6, posted.append, gen, 0.5, threading.Event(),
                      spacing=(0.01, 0.02))
    assert n == 6 and len(posted) == 6
    assert [x.split(" (")[0].split("] ")[1] for x in posted] == ["Bob", "Bill"] * 3     # alternating
    assert all("s***" in x for x in posted)                                      # language filter
    first, later = prompts[0][1], prompts[3][1]
    assert first.startswith("You are Bob, ") and "You start a chat with Bill about the new stew in the cafeteria" in first
    assert "So far: " in later and 'Bill: "Bill says s*** about stew #2"' in later and later.endswith("One short line.")


def test_conversation_canned_fallback_and_cut():
    posted = []
    cut = threading.Event()

    def boom(*a):
        raise RuntimeError("no model")
    crew.converse([("Bob Kerman", "scientist"), ("Gus Kerman", "tourist")], "snacks", 4, posted.append, boom, 0.3, cut,
                  spacing=(0.01, 0.02))
    assert [x.split(": ", 1)[1] for x in posted] == crew.TOPICS["snacks"][:4]
    posted.clear()
    th = threading.Thread(target=crew.converse, args=([("Bob Kerman", "scientist"), ("Gus Kerman", "tourist")], "Jeb", 6,
                                                      posted.append, boom, 0.3, cut), kwargs={"spacing": (0.3, 0.3)})
    th.start()
    time.sleep(0.4)
    cut.set()                                                                    # an emergency
    th.join(2.0)
    assert not th.is_alive() and 1 <= len(posted) <= 2


def test_smalltalk_scheduler_cut_by_emergency_and_chatter_off(_fresh):
    st = _fresh
    talk = crew.SmallTalk()
    posted = []
    gen = lambda *a: "Nice view, huh?"  # noqa: E731
    facts = lambda: ("runway 09", 3600.0, 300.0)  # noqa: E731
    starts = []
    t = 0.0
    while t <= 3 * 3600.0:
        if talk.tick(t, 1, "flying", CREW, "Sidry Kerman", True, False, facts, post=posted.append, gen=gen, timeout=0.3,
                     rng=random.Random(int(t)), spacing=(0.0, 0.0), wait=True):
            starts.append(t)
        t += 5.0
    assert 10 <= len(starts) <= 25                                               # ~one every 8-15 min
    assert all(b - a >= crew.TALK_GAP_MIN_S - 5 for a, b in zip(starts, starts[1:]))
    assert crew.TALK_LINES_MIN * len(starts) <= len(posted) <= crew.TALK_LINES_MAX * len(starts)
    # emergency: a running conversation stops at once and nothing starts for 60 s after
    talk2 = crew.SmallTalk()
    talk2.due = 0.0
    talk2.fly_since = -1000.0
    talk2.vid = 1
    posted.clear()
    assert talk2.tick(10.0, 1, "flying", CREW, "Sidry Kerman", True, False, facts, post=posted.append, gen=gen,
                      timeout=0.3, spacing=(0.5, 0.5))
    time.sleep(0.2)
    talk2.tick(11.0, 1, "flying", CREW, "Sidry Kerman", True, True, facts, post=posted.append, gen=gen)
    talk2.th.join(2.0)
    assert len(posted) == 1 and not talk2.active()
    talk2.due = 0.0
    assert not talk2.tick(50.0, 1, "flying", CREW, "Sidry Kerman", True, False, facts, post=posted.append, gen=gen)
    # chatter off: nothing
    st["crew_chatter"] = False
    talk3 = crew.SmallTalk()
    posted.clear()
    for t in range(0, 7200, 5):
        assert not talk3.tick(float(t), 1, "flying", CREW, "Sidry Kerman", True, False, facts, post=posted.append, gen=gen)
    assert posted == []


def test_emergency_handle_cuts_small_talk(monkeypatch):
    monkeypatch.setattr(crew, "speak", lambda *a, **k: [])
    cut = threading.Event()
    crew.TALK.cut = cut
    em.handle([{"kind": "stall", "key": "stall", "start": True, "quiet": False, "what": "", "pct": None, "short": "STALL",
                "actions": [], "changes": [], "engines": [], "one_shot": False}], None, {"who": "Sidry"}, post=lambda x: None)
    assert cut.is_set()
