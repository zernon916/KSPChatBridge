"""Talk to a kerbal (Luke 2026-10-08): a chat message that addresses a crew member ABOARD the active vessel by first
name ('Hey Bob, are you having a good time?', 'Bob: ...', '@Bob ...', '..., Bob?') gets a short in-character reply
from that kerbal over the intercom ('[INTERCOM] Bob (Sci): ...'), built from their personality / trait and the REAL
flight facts only. Kerbals only talk: no tools, and lines claiming an action are dropped. A flight order addressed to
a kerbal still goes to the pilot (the normal chat + tools). A named kerbal who isn't aboard gets no reply - the pilot
says so. Each kerbal remembers the last few exchanges (in memory, per bridge run). The language filter applies.

route(text, crew, pilot, known) is pure (tested offline); reply() does the one AI call (with a canned fallback)."""
import collections
import logging
import re
import threading

log = logging.getLogger("kspchat")
MEM_N = 4                 # exchanges remembered per kerbal
MAX_WORDS, MAX_CHARS = 45, 260
LLM_TIMEOUT_S = 8.0
CLASSIC = ("Jebediah Kerman", "Bill Kerman", "Bob Kerman", "Valentina Kerman")
_MEM = collections.defaultdict(lambda: collections.deque(maxlen=MEM_N))
_lock = threading.Lock()

_AT = re.compile(r"^\s*@([A-Za-z][\w'-]*)[\s,:;!.-]*(.*)$", re.S)
_HEY = re.compile(r"^\s*(?:hey|hi|hello|yo|oi|ok|okay|so|and|well)[\s,]+([A-Za-z][\w'-]*)\b[\s,:;!?.-]*(.*)$", re.S | re.I)
_LEAD = re.compile(r"^\s*([A-Za-z][\w'-]*)\s*[:,]\s*(.+)$", re.S)
_TAIL = re.compile(r"^(.+?),\s*([A-Za-z][\w'-]*)\s*([?!.]*)\s*$", re.S)
_ORDER = re.compile(
    r"^\s*(?:please\s+|can you\s+|could you\s+|would you\s+|go\s+(?:and\s+)?)?(?:land|take\s*off|launch|abort|stage|"
    r"throttle|set|hold|climb|descend|hover|turn|fly|head|deploy|retract|raise|lower|gear|brakes?|engage|disengage|"
    r"autopilot|sas|rcs|eject|dock|undock|burn|circularize|circularise|warp|stop|kill|cut|start|ignite|open|close|"
    r"arm|release|extend|activate|toggle|face|sidestep|back\s+up|taxi|go\s+to|return|pitch|bank|roll|yaw|"
    r"switch|lock|unlock|transfer|run|do|execute|calculate|plot|plan)\b", re.I)
_ACTION = re.compile(r"\bI(?:'ve|'ll| have| will| am|'m| just)?\s+(?:just\s+|already\s+)?(?:fix(?:ed)?|repair(?:ed)?|"
                     r"deploy(?:ed)?|engag(?:e|ed)|releas(?:e|ed)|activat(?:e|ed)|eject(?:ed)?|stag(?:e|ed)|land(?:ed)?|"
                     r"cut|turn(?:ed)?|switch(?:ed)?|pull(?:ed)?|press(?:ed)?|flip(?:ped)?|reset|set|start(?:ed)?|"
                     r"stop(?:ped)?|open(?:ed)?|clos(?:e|ed)|lower(?:ed)?|rais(?:e|ed)|fir(?:e|ed)|launch(?:ed)?|"
                     r"took|take|grab(?:bed)?|flew|fly(?:ing)?\s+(?:us|the))\b|\b(?:fixed|repaired|all clear|good as new|"
                     r"done[,!.]|on it[,!.])", re.I)
_TOOLISH = re.compile(r"[{}<>`]|\[(?!INTERCOM|COMMS)|\b(?:tool|function call|json|as an ai|language model)\b", re.I)


def first(name):
    return str(name or "").split()[0] if name else ""


def _match(token, crew):
    """The crew entry whose first name is `token` (or starts with it, >= 3 letters: 'Jeb' -> Jebediah)."""
    t = token.lower().strip("'")
    for c in crew:
        f = first(c[0]).lower()
        if f and (f == t or (len(t) >= 3 and f.startswith(t))):
            return c
    return None


def _address(text):
    """[(token, rest)] candidate addressings, most explicit first."""
    out = []
    for rx in (_AT, _HEY, _LEAD):
        m = rx.match(text)
        if m:
            out.append((m.group(1), m.group(2).strip(), rx is _AT))
    m = _TAIL.match(text)
    if m:
        out.append((m.group(2), (m.group(1) + m.group(3)).strip(), False))
    return out


def looks_addressed(text):
    """Cheap pre-check (no kRPC): could this message address someone by name?"""
    t = str(text or "")
    return bool(t.strip()) and not t.lstrip().startswith("/") and bool(_address(t))


def is_order(rest, parse_direct=None):
    if not rest:
        return False
    try:
        if parse_direct is not None and parse_direct(rest):
            return True
    except Exception:  # noqa: BLE001
        pass
    return bool(_ORDER.match(rest))


def route(text, crew, pilot, known=(), parse_direct=None):
    """crew: [(full name, trait)] aboard; pilot: the pilot's first name; known: other kerbal full names (roster).
    -> None (normal chat) | ('kerbal', (name, trait), rest) | ('absent', first name, rest) | ('order', None, rest)."""
    text = str(text or "")
    if not text.strip() or text.lstrip().startswith("/"):
        return None
    crew = [(str(n), str(t or "")) for n, t in crew or []]
    others = [(str(n), "") for n in known or [] if str(n) not in {c[0] for c in crew}]
    for token, rest, explicit in _address(text):
        c = _match(token, crew)
        if c is not None:
            if pilot and first(c[0]).lower() == str(pilot).lower():
                return None  # the pilot IS the chat's voice (with tools)
            if is_order(rest, parse_direct):
                return ("order", c, rest)
            return ("kerbal", c, rest or "Hey!")
        o = _match(token, others)
        if o is not None or explicit:
            return ("absent", first(o[0]) if o else token, rest)
    return None


def remember(name, said, reply):
    with _lock:
        _MEM[str(name)].append((str(said), str(reply)))


def memory(name):
    with _lock:
        return list(_MEM[str(name)])


def reset():
    with _lock:
        _MEM.clear()


def prompt(name, trait, said, facts, mem):
    from . import crew as crew_mod
    lines = [f"You are {crew_mod.who(name, crew_mod.norm_trait(trait))}, a crew member (not the pilot) aboard a Kerbal "
             "spacecraft. The captain, Luke, talks to you over the intercom.",
             f"Real flight facts right now: {facts or 'nothing special'}. Only mention facts from this list; never "
             "invent numbers, places or events.",
             "You can't fly, press buttons, repair or change anything - you can only talk. Reply in character, "
             "one or two short sentences, no stage directions."]
    if mem:
        lines.append("Your recent chat with Luke: " + " | ".join(f"Luke: {a} / You: {b}" for a, b in mem))
    lines.append(f"Luke says: {said}")
    return "\n".join(lines)


def clean(text):
    """The kerbal's spoken reply, or None: think blocks / tool-ish text rejected, a 'Name:' prefix stripped, sentences
    claiming an action dropped, trimmed to MAX_WORDS / MAX_CHARS at a sentence end."""
    t = re.sub(r"<think>.*?</think>", "", str(text or ""), flags=re.S).strip()
    t = " ".join(x.strip() for x in t.splitlines() if x.strip())
    t = re.sub(r"^\[(?:INTERCOM|COMMS)\]\s*", "", t)
    t = re.sub(r"^[\w .'-]{1,40}(?:\([\w .]{1,12}\))?:\s*", "", t) if ":" in t[:52] else t
    t = t.strip().strip('"*_ ').strip()
    if not t or _TOOLISH.search(t):
        return None
    sents = [x.strip() for x in re.split(r"(?<=[.!?])\s+", t) if x.strip()]
    sents = [x for x in sents if not _ACTION.search(x)]
    out = ""
    for s in sents:
        nxt = (out + " " + s).strip()
        if len(nxt.split()) > MAX_WORDS or len(nxt) > MAX_CHARS:
            break
        out = nxt
    return out or None


CANNED = {
    "scientist": ["Fascinating ride so far, Captain! I'm taking notes on everything.",
                  "All good back here - just watching the instruments and trying not to touch any."],
    "engineer": ["Everything's holding together back here, Captain. For now.",
                 "Sounds good from back here. I'm keeping an ear on the creaks."],
    "tourist": ["Is this normal? It's AMAZING. I think. Is it normal?",
                "Best trip ever! Mostly. Are there snacks?"],
    "pilot": ["Copy that, Captain. Enjoying the ride from the other seat."],
}


def canned(name, trait, said):
    from . import crew as crew_mod
    opts = CANNED.get(crew_mod.norm_trait(trait), CANNED["tourist"])
    return opts[(len(memory(name)) + len(str(said))) % len(opts)]


def _llm(name, trait, text):
    """One reply from the chat's current backend (crew.BACKEND, noted by chat.Session.send). Never loads a model."""
    from . import backends, chat, config, crew as crew_mod, language
    b = backends.normalize(crew_mod.BACKEND["name"])
    if b == "local":
        loaded = [m for m, l in backends.lmstudio_models() if l]
        if not loaded:
            raise RuntimeError("no LM Studio model loaded")
        url, key, model = config.LMSTUDIO_URL, None, crew_mod.BACKEND["override"] or loaded[0]
    else:
        url, key, model = backends.resolve(b, crew_mod.BACKEND["override"])
    data = chat._post(url.rstrip("/") + "/chat/completions", {
        "model": model, "temperature": 0.8, "max_tokens": 90,
        "messages": [{"role": "system", "content": "Answer with your spoken reply only, at most 35 words. You can "
                      "only talk. " + language.prompt_lines()},
                     {"role": "user", "content": text}]}, key, timeout=LLM_TIMEOUT_S)
    return data["choices"][0]["message"].get("content")


def reply(member, said, facts, gen=None, timeout=LLM_TIMEOUT_S):
    """-> the intercom line '[INTERCOM] Bob (Sci): ...' (AI with a deadline, else canned); remembered."""
    from . import crew as crew_mod
    name, trait = member
    box = {}

    def work():
        try:
            box["text"] = (gen or _llm)(name, trait, prompt(name, trait, said, facts, memory(name)))
        except Exception as e:  # noqa: BLE001
            box["err"] = e
    th = threading.Thread(target=work, daemon=True, name="kerbal-talk")
    th.start()
    th.join(timeout)
    line = clean(box.get("text")) if "text" in box else None
    log.info("talk: %s (%s) reply %s", name, trait, "ai" if line else
             "canned: " + ("timeout" if th.is_alive() else str(box.get("err") or "unusable reply")))
    line = line or canned(name, trait, said)
    out = crew_mod.fmt(name, trait, line, crackle=False)
    remember(name, said, out.split(": ", 1)[-1])
    return out


def absent_line(name, crew, pilot):
    others = [first(n) for n, _ in crew or [] if first(n).lower() != str(pilot or "").lower()]
    aboard = (" - it's just me" + (" and " + ", ".join(others) if others else "") + " up here") if crew else ""
    return f"{name} isn't aboard this craft, Captain{aboard}."


# ------------------------------------------------------------------ live helpers (kRPC; never raise)
def crew_aboard():
    """[(full name, trait)] on the active vessel."""
    try:
        from . import ksp_actions
        return [(str(k.name), str(getattr(k, "trait", "") or "")) for k in ksp_actions._vessel().crew]
    except Exception:  # noqa: BLE001
        return []


def roster():
    """Other kerbal names we know of: the personality file, every vessel's crew, the classic four."""
    names = set(CLASSIC)
    try:
        from . import personality
        names |= set(personality._load())
    except Exception:  # noqa: BLE001
        pass
    try:
        from . import ksp_actions
        for vv in ksp_actions.conn().space_center.vessels:
            for k in vv.crew:
                names.add(str(k.name))
    except Exception:  # noqa: BLE001
        pass
    return sorted(names)


def flight_facts():
    """Short real-fact text: situation, body, altitude, speed, destination / ETA, active emergencies."""
    bits = []
    try:
        from . import ksp_actions
        v = ksp_actions._vessel()
        f = v.flight(v.orbit.body.reference_frame)
        sit = str(v.situation).split(".")[-1].replace("_", " ")
        bits.append(f"the craft '{v.name}' is {sit} at {v.orbit.body.name}")
        bits.append(f"altitude {float(v.flight().surface_altitude):.0f} m above ground")
        bits.append(f"speed {float(f.speed):.0f} m/s")
    except Exception:  # noqa: BLE001
        pass
    try:
        from . import crew as crew_mod
        dest, eta, dist = crew_mod.trip_facts()
        if dest:
            bits.append(f"heading to {dest}")
        if eta:
            bits.append(f"about {crew_mod._fmt_eta(eta)} to go")
        if dist:
            bits.append(f"{dist:.0f} km to go" if dist >= 10 else f"{dist:.1f} km to go")
    except Exception:  # noqa: BLE001
        pass
    try:
        from . import emergency
        act = [k.split(":")[0].replace("_", " ") for k in emergency.DET.active]
        if act:
            bits.append("active emergency: " + ", ".join(sorted(set(act))))
    except Exception:  # noqa: BLE001
        pass
    return "; ".join(bits)
