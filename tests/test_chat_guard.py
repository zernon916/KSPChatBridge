"""Offline: a chat reply that claims a change (holds / settings) without any tool call is re-prompted once and, if
it still claims it, flagged "(no action taken)" (seen live: 'Holds updated: altitude_m 5000 m' with tools [])."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import chat  # noqa: E402


def fake_backend(monkeypatch, replies):
    sent, seq = [], list(replies)
    monkeypatch.setattr(chat.backends, "resolve", lambda b, m=None: ("http://x", None, "m"))
    monkeypatch.setattr(chat.memory, "log_turn", lambda *a, **k: None)

    def post(url, payload, key=None, timeout=300):
        sent.append(payload)
        return {"choices": [{"message": {"content": seq.pop(0)}}]}
    monkeypatch.setattr(chat, "_post", post)
    return sent


def test_claim_without_tool_is_reprompted_then_flagged(monkeypatch):
    sent = fake_backend(monkeypatch, ["Holds updated: altitude_m 5000 m", "Holds updated: altitude 5000 m."])
    reply, tools = chat.Session("t1").send("set altitude 5000 and hold it there for a while", "local")  # not a direct order
    assert tools == [] and reply.endswith("(no action taken)") and len(sent) == 2, reply
    assert "called no tool" in sent[1]["messages"][-1]["content"] and "tools" in sent[1]


def test_reprompt_can_fix_it(monkeypatch):
    fake_backend(monkeypatch, ["I've set the heading to 270.", "Sorry - which heading do you want?"])
    reply, _ = chat.Session("t2").send("hold heading", "local")
    assert reply == "Sorry - which heading do you want?"


def test_plain_answer_untouched(monkeypatch):
    sent = fake_backend(monkeypatch, ["We're at 3 km, fuel 80%."])
    reply, _ = chat.Session("t3").send("status?", "local")
    assert reply == "We're at 3 km, fuel 80%." and len(sent) == 1
    assert not chat.claims_action("Want me to set altitude 5000?") and chat.claims_action("Throttle set to 50%.")
