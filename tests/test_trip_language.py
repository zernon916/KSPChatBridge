"""Offline: 'are we there yet?' trip chatter + the language option. No live files, no AI services."""
import random
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, crew, language  # noqa: E402

CREW = [("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist"), ("Bill Kerman", "Engineer"), ("Gus Kerman", "Tourist")]


def setup_function(_):
    crew.reset()


def store(monkeypatch):
    st = {}
    from kspchat import settings
    monkeypatch.setattr(settings, "put", lambda k, v: st.__setitem__(k, v))
    monkeypatch.setattr(settings, "get", lambda k: st.get(k))
    return st


def test_next_gap_short_and_long_trips():
    rng = random.Random(0)
    shorts = [crew.next_gap(200.0, 0, random.Random(i)) for i in range(200)]
    said = [g for g in shorts if g is not None]
    assert 0.2 < len(said) / 200 < 0.5 and all(g < 200.0 for g in said)          # rare, and before arrival
    assert crew.next_gap(200.0, 1, rng) is None                                   # short trips: at most once
    gaps = [crew.next_gap(3600.0, n, random.Random(n)) for n in range(1, 8)]
    assert all(crew.GAP_MIN_S * 0.6 <= g <= crew.GAP_MAX_S for g in gaps)
    assert crew.next_gap(None, 6, random.Random(5)) < crew.next_gap(None, 1, random.Random(5))  # growing impatience


def run_trip(trip, t0, t1, posted, gen, emergency=lambda t: False, active=True, facts=("runway 09", 1500.0, 120.0),
             crew_=CREW, sit="flying", rng=None):
    rng = rng or random.Random(7)
    started = []
    t = t0
    while t <= t1:
        if trip.tick(t, 1, sit, crew_, "Sidry Kerman", active, emergency(t), lambda: facts, post=posted.append,
                     gen=gen, timeout=0.5, rng=rng, wait=True):
            started.append(t)
        t += 1.0
    return started


def test_trip_long_cruise_lines_and_pilot_eta(monkeypatch):
    store(monkeypatch)
    posted, prompts = [], []

    def gen(name, trait, facts, text=None):
        prompts.append(text)
        return "Are we there yet?!" if trait != "pilot" else "About 25 min to go, 120 km. Sit tight."
    trip = crew.Trip()
    started = run_trip(trip, 0.0, 3600.0, posted, gen)
    assert 6 <= len(started) <= 25                                                # ~one every 3-6 min, faster later
    assert all(b - a >= crew.GAP_MIN_S * 0.6 - 1 for a, b in zip(started[1:], started[2:]))
    crew_lines = [x for x in posted if "(Pilot)" not in x]
    assert len(crew_lines) == len(started) and not any(x.startswith("[INTERCOM] Sidry") and "yet" in x for x in posted)
    pilot = [x for x in posted if "(Pilot)" in x]
    assert pilot and all("25 min" in x and "120 km" in x for x in pilot)        # real ETA/distance only
    p0 = next(p for p in prompts if p and "aboard" in p)
    assert p0.startswith("You are ") and "heading to runway 09, about 25 min to go, 120 km away" in p0
    assert p0.endswith("What do you say over the intercom? One short line.")


def test_trip_pilot_invented_numbers_fall_back(monkeypatch):
    store(monkeypatch)
    posted = []
    gen = lambda name, trait, facts, text=None: ("Are we close?" if trait != "pilot" else "Only 3 minutes and 7 km!")  # noqa: E731
    trip = crew.Trip()
    run_trip(trip, 0.0, 3600.0, posted, gen)
    pilot = [x for x in posted if "(Pilot)" in x]
    assert pilot and not any("3 minutes" in x for x in pilot)                    # invented numbers -> canned (real ETA)
    assert all(("25 min" in x or "120 km" in x) for x in pilot)
    assert not crew.numbers_ok("Only 3 minutes", {"25", "120"}) and crew.numbers_ok("25 min, 120 km", {"25", "120"})


def test_trip_never_in_emergency_or_off_or_on_ground(monkeypatch):
    st = store(monkeypatch)
    posted = []
    gen = lambda *a: "Are we there yet?"  # noqa: E731
    assert run_trip(crew.Trip(), 0.0, 1800.0, posted, gen, emergency=lambda t: True) == []
    assert run_trip(crew.Trip(), 0.0, 1800.0, posted, gen, sit="landed") == []
    assert run_trip(crew.Trip(), 0.0, 1800.0, posted, gen, crew_=[("Jeb Kerman", "Pilot")]) == []
    st["crew_chatter"] = False
    assert run_trip(crew.Trip(), 0.0, 1800.0, posted, gen) == []
    st["crew_chatter"] = True
    # no autopilot trip: nothing in the first 2 minutes of flight
    s = run_trip(crew.Trip(), 0.0, 1800.0, posted, gen, active=False, facts=(None, None, None))
    assert s and s[0] >= crew.TRIP_MIN_FLIGHT_S
    # an emergency mid-trip: quiet for a minute after it clears
    s = run_trip(crew.Trip(), 0.0, 2400.0, posted, gen, emergency=lambda t: 500 <= t < 600)
    assert not any(500 <= x < 600 + crew.CALM_AFTER_EMERGENCY_S for x in s)
    assert posted == [] or all(not x.startswith("Sidry:") for x in posted)


def test_language_option(monkeypatch):
    st = store(monkeypatch)
    assert language.enabled()                                                     # default ON
    assert language.clean("Oh shit, the wheel!") == "Oh s***, the wheel!"
    assert language.clean("hello shell assassin") == "hello shell assassin"      # whole words only
    assert "no cursing" in language.prompt_lines() and "slurs" in language.prompt_lines()
    assert language.command("filter off") == "Language filter off. (Slurs and hate stay blocked.)"
    assert st["language_filter"] is False
    assert language.clean("Oh shit, the wheel!") == "Oh shit, the wheel!"
    assert "no cursing" not in language.prompt_lines() and "slurs" in language.prompt_lines()
    assert "[removed]" in language.clean("you retard")                             # slurs: always
    assert language.command("filter on") == "Language filter on."
    assert "is on" in language.command("")
    assert crew.fmt("Bob Kerman", "Scientist", "Holy shit!", crackle=False) == "[INTERCOM] Bob (Sci): Holy s***!"
    assert "no cursing" in chat.Session().system_prompt()

def test_personality_generated_once_seeded_and_persisted():
    from kspchat import personality
    personality.reset_cache()
    if personality.path().exists():
        personality.path().unlink()
    calls = []

    def stats():
        calls.append(1)
        return {"courage": 0.1, "stupidity": 0.9, "badass": False, "veteran": True}
    p = personality.ensure("Bob Kerman", stats)
    assert p["temper"][0] in ("nervous", "panicky", "jumpy") and p["temper"][1] in ("goofy", "scatterbrained")
    assert len(p["likes"]) == 2 and len(p["dislikes"]) == 1 and p["stats"]["veteran"] is True
    assert personality.ensure("Bob Kerman", stats) == p and len(calls) == 1      # once per kerbal
    personality.reset_cache()
    assert personality.ensure("Bob Kerman", stats) == p and len(calls) == 1      # persisted in the json
    assert "Bob Kerman" in personality.path().read_text(encoding="utf-8")
    assert personality.path().parent == __import__("kspchat.config", fromlist=["x"]).SETTINGS_FILE.parent  # test dir
    d = personality.describe("Bob Kerman", "scientist")
    assert d.startswith("a " + " ".join(p["temper"]) + " scientist who " + " and ".join(p["likes"]))
    assert {"loves snacks", "collects moon rocks", "hums while working", "adores Jeb", "tells bad puns"} <= set(personality.LIKES)
    assert "optimistic" in personality.CALM
    assert d.endswith(" and " + p["dislikes"][0])
    assert {"hates flying", "is afraid of heights", "gets airsick", "hates rockets", "is terrified of space"} <= set(personality.DISLIKES)
    jeb = personality.generate("Jebediah Kerman", {"courage": 0.5, "stupidity": 0.0, "badass": True, "veteran": True})
    assert jeb["temper"] == ["unflappable", "seasoned"]
    assert personality.generate("Val Kerman") == personality.generate("Val Kerman")  # seeded by the name
    assert crew.prompt("Bob Kerman", "scientist", "x").startswith("You are Bob, " + d + ", aboard")


def test_dad_jokes_on_trips(monkeypatch):
    st = store(monkeypatch)
    from kspchat import personality
    monkeypatch.setattr(personality, "likes", lambda name, phrase: name == "Bob Kerman" and phrase == "loves dad jokes")
    assert "loves dad jokes" in personality.LIKES
    posted, prompts = [], []

    def gen(name, trait, facts, text=None):
        prompts.append(text)
        return "Why did the strut break up? It felt held back." if "dad joke" in (text or "") else "Are we there yet?"
    started = run_trip(crew.Trip(), 0.0, 7200.0, posted, gen, crew_=[("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist")])
    jokes = [x for x in posted if "strut" in x]
    assert 1 <= len(jokes) <= 7200 / crew.DAD_JOKE_GAP_S + 1                    # rare: at most one per 15 min
    assert all(x.startswith("[INTERCOM] Bob (Sci): ") for x in jokes)
    jp = next(p for p in prompts if p and "dad joke" in p)
    assert jp.startswith("You are Bob, ") and "clean dad joke" in jp
    # nobody with 'loves dad jokes' -> no jokes; emergencies / chatter off -> nothing at all
    monkeypatch.setattr(personality, "likes", lambda name, phrase: False)
    posted.clear()
    crew.reset()
    run_trip(crew.Trip(), 0.0, 7200.0, posted, gen, crew_=[("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist")])
    assert posted and not any("strut" in x for x in posted)
    monkeypatch.setattr(personality, "likes", lambda name, phrase: True)
    posted.clear()
    assert run_trip(crew.Trip(), 0.0, 3600.0, posted, gen, emergency=lambda t: True) == [] and posted == []
    st["crew_chatter"] = False
    assert run_trip(crew.Trip(), 0.0, 3600.0, posted, gen) == [] and posted == []
    st["crew_chatter"] = True
    # the language filter still applies to a joke line
    crew.reset()
    out = []
    crew._one("Bob Kerman", "scientist", "canned", "x", out.append, lambda *a: "Holy shit, a joke!", 0.5, text="p")
    assert out and "s***" in out[0]


def test_dad_jokes_in_emergencies(monkeypatch):
    store(monkeypatch)
    from kspchat import personality
    monkeypatch.setattr(personality, "likes", lambda name, phrase: phrase == "loves dad jokes")  # everyone loves them
    monkeypatch.setattr(crew, "EMERG_JOKE_P", 1.0)
    monkeypatch.setattr(crew, "SNAP_JOKE_P", 0.0)
    crew_ = [("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist"), ("Bill Kerman", "Engineer"), ("Gus Kerman", "Tourist")]
    posted, prompts = [], []

    def gen(name, trait, facts, text=None):
        prompts.append((name, text))
        return "Things are really... falling into place?!"
    ev = {"kind": "stall", "key": "stall", "start": True, "quiet": False, "what": "", "lost": [], "short": "STALL"}
    crew.speak(ev, crew_, "Sidry Kerman", post=posted.append, gen=gen, wait=True)
    assert len(posted) == 2                                                     # still max 2 lines per emergency
    assert not any("Sidry" in x for x in posted) and not any(n == "Sidry Kerman" for n, _ in prompts)  # never the pilot
    assert all(t and "nervous dad joke" in t and "the plane is stalling" in t for _, t in prompts)
    crew.reset()
    posted.clear()
    crew.speak(ev, crew_, "Sidry Kerman", post=posted.append, gen=lambda *a: (_ for _ in ()).throw(RuntimeError()), wait=True)
    assert len(posted) == 2 and all(any(j in x for j in crew.EMERG_DAD_JOKES) for x in posted)  # canned jokes on failure
    monkeypatch.setattr(personality, "likes", lambda name, phrase: False)
    crew.reset()
    prompts.clear()
    crew.speak(ev, crew_, "Sidry Kerman", post=posted.append, gen=gen, wait=True)
    assert prompts and all(t is None for _, t in prompts)                      # no joke lovers -> normal panic lines


def test_pilot_snaps_back_once_per_emergency(monkeypatch):
    store(monkeypatch)
    from kspchat import personality
    monkeypatch.setattr(personality, "likes", lambda name, phrase: phrase == "loves dad jokes")
    monkeypatch.setattr(crew, "EMERG_JOKE_P", 1.0)
    monkeypatch.setattr(crew, "SNAP_JOKE_P", 1.0)
    monkeypatch.setattr(crew, "SNAPS", ["Everyone QUIET, I need to shitting concentrate!"])
    crew_ = [("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist"), ("Gus Kerman", "Tourist")]
    posted = []
    ev = {"kind": "stall", "key": "stall", "start": True, "quiet": False, "what": "", "lost": [], "short": "STALL"}
    t0 = __import__("time").time()

    def slow(*a):
        __import__("time").sleep(0.3)
        return "Falling into place, heh?!"
    ths = crew.speak(ev, crew_, "Sidry Kerman", post=posted.append, gen=slow, timeout=1.0)
    assert __import__("time").time() - t0 < 0.1                                # the caller (recovery) never waits
    for th in ths:
        th.join(5.0)
    assert len(posted) == 3 and posted[-1].startswith("[RADIO] Sidry: ")       # after the crew lines
    assert "s*******" in posted[-1]                                             # language filter applies
    crew._last.clear()
    posted.clear()
    crew.speak(ev, crew_, "Sidry Kerman", post=posted.append, gen=slow, timeout=1.0, wait=True)
    assert len(posted) == 2 and not any("[RADIO]" in x for x in posted)        # max one per emergency
    crew.reset()
    posted.clear()
    monkeypatch.setattr(personality, "likes", lambda name, phrase: False)
    monkeypatch.setattr(crew, "SNAP_P", 0.0)
    crew.speak(ev, crew_, "Sidry Kerman", post=posted.append, gen=slow, timeout=1.0, wait=True)
    assert not any("[RADIO]" in x for x in posted)


def test_pilot_never_snaps_unprompted(monkeypatch):
    st = store(monkeypatch)
    monkeypatch.setattr(crew, "SNAP_P", 1.0)
    monkeypatch.setattr(crew, "SNAP_JOKE_P", 1.0)
    ev = {"kind": "stall", "key": "stall", "start": True, "quiet": False, "what": "", "lost": [], "short": "STALL"}
    posted = []
    crew.speak(ev, [("Sidry Kerman", "Pilot")], "Sidry Kerman", post=posted.append, gen=lambda *a: "x", wait=True)
    assert posted == []                                                         # nobody else aboard: no snap
    st["crew_chatter"] = False
    crew.speak(ev, [("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist"), ("Gus Kerman", "Tourist")], "Sidry Kerman",
               post=posted.append, gen=lambda *a: "x", wait=True)
    assert posted == []                                                         # chatter off: no snap either
