"""Offline: talk to a kerbal aboard (talk.py + chat.Session routing). No kRPC, no AI: fakes only."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat, crew, language, orders, talk  # noqa: E402

CREW = [("Sidry Kerman", "Pilot"), ("Bob Kerman", "Scientist"), ("Bill Kerman", "Engineer")]
KNOWN = ["Jebediah Kerman", "Valentina Kerman", "Bob Kerman"]


def setup_function(_):
    talk.reset()


def r(text):
    return talk.route(text, CREW, "Sidry", KNOWN, orders.parse)


def test_addressing_forms():
    assert r("Hey Bob, are you having a good time?") == ("kerbal", CREW[1], "are you having a good time?")
    assert r("Bob: how's the view?")[:2] == ("kerbal", CREW[1])
    assert r("@Bob what's up")[:2] == ("kerbal", CREW[1])
    assert r("How are you doing, Bill?") == ("kerbal", CREW[2], "How are you doing?")
    assert r("bob, you ok?")[:2] == ("kerbal", CREW[1])


def test_not_addressed_and_pilot():
    for t in ("set altitude 2000", "what's our speed?", "land at the KSC", "hey there, status?", "/crew chatter off",
              "Status, please"):
        assert r(t) is None, t
    assert r("Hey Sidry, how are you?") is None  # the pilot is the chat's own voice


def test_absent_kerbals():
    assert r("Hey Jeb, you there?") == ("absent", "Jebediah", "you there?")
    assert r("Valentina: how's orbit?")[:2] == ("absent", "Valentina")
    assert r("@Gerdy hello")[:2] == ("absent", "Gerdy")  # explicit @ even if unknown
    line = talk.absent_line("Jebediah", CREW, "Sidry")
    assert line == "Jebediah isn't aboard this craft, Captain - it's just me and Bob, Bill up here."


def test_orders_go_to_the_pilot():
    assert r("Bob, land the plane") == ("order", CREW[1], "land the plane")
    assert r("Hey Bill, gear down")[0] == "order"
    assert r("@Bob please deploy the chutes")[0] == "order"


def test_clean_strips_claims_and_tools():
    assert talk.clean("Bob: Oh it's lovely up here! I fixed the engine. The clouds look like snacks.") == \
        "Oh it's lovely up here! The clouds look like snacks."
    assert talk.clean('{"tool": "abort"}') is None
    assert talk.clean("I've deployed the chutes.") is None
    assert talk.clean("<think>hmm</think>[INTERCOM] Bob (Sci): Having a blast, Captain!") == "Having a blast, Captain!"
    long = " ".join(["word."] * 80)
    assert len(talk.clean(long).split()) <= talk.MAX_WORDS


def test_reply_prompt_memory_and_format(monkeypatch):
    monkeypatch.setattr(crew, "who", lambda n, t: f"{n.split()[0]}, a curious {t} who loves snacks")
    seen = []

    def gen(name, trait, text):
        seen.append(text)
        return "Having a great time, Captain! The view from 3000 m is wild."
    out = talk.reply(CREW[1], "are you having a good time?", "altitude 3000 m above ground; speed 120 m/s", gen=gen)
    assert out == "[INTERCOM] Bob (Sci): Having a great time, Captain! The view from 3000 m is wild."
    assert "Bob, a curious scientist who loves snacks" in seen[0] and "altitude 3000 m" in seen[0]
    assert "can't fly" in seen[0] and "Luke says: are you having a good time?" in seen[0]
    talk.reply(CREW[1], "what's your favourite snack?", "", gen=gen)
    assert "Your recent chat with Luke: Luke: are you having a good time?" in seen[1]
    for i in range(6):
        talk.reply(CREW[1], f"q{i}", "", gen=gen)
    assert len(talk.memory("Bob Kerman")) == talk.MEM_N
    assert talk.memory("Bill Kerman") == []  # per-kerbal


def test_reply_falls_back_and_filters_language(monkeypatch):
    def boom(*a):
        raise RuntimeError("no model")
    out = talk.reply(CREW[2], "hi", "", gen=boom, timeout=1.0)
    assert out.startswith("[COMMS] Bill (Eng): ") and len(out) > 25
    monkeypatch.setattr(language, "enabled", lambda: True)
    out = talk.reply(CREW[1], "hi", "", gen=lambda *a: "Damn, this is a great view!")
    assert "Damn" not in out and out.startswith("[INTERCOM] Bob (Sci): ")


def test_session_routing(monkeypatch):
    monkeypatch.setattr(talk, "crew_aboard", lambda: CREW)
    monkeypatch.setattr(talk, "roster", lambda: KNOWN)
    monkeypatch.setattr(talk, "flight_facts", lambda: "speed 100 m/s")
    monkeypatch.setattr(chat.ksp_actions, "pilot_name", lambda: "Sidry")
    monkeypatch.setattr(talk, "_llm", lambda n, t, text: "Doing great, Captain!")
    s = chat.Session()
    reply, tools = s.send("Hey Bob, are you having a good time?")
    assert reply == "[INTERCOM] Bob (Sci): Doing great, Captain!" and tools == [] and s.last_name == "Bob"
    reply, tools = s.send("Hey Jeb, how's it going?")
    assert reply.startswith("Jebediah isn't aboard") and s.last_name == "Sidry" and tools == []
    called = []
    monkeypatch.setattr(chat.ksp_actions, "call_tool", lambda name, args: called.append((name, args)) or "Gear down.")
    reply, tools = s.send("Bob, gear down")
    assert called and called[0][0] == tools[0]["tool"] and s.last_name == "Sidry"  # the pilot did it
