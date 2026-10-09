"""Crew intercom chatter during emergencies (Luke 2026-10-08).

The PILOT makes the MAYDAY radio call and flies the recovery (emergency.py). The rest of the crew - read from kRPC
(name + trait) - chime in over the ship INTERCOM: scientists terrified / curious about the readings, engineers about
the broken part (named from the watcher), tourists just scream. Rate-limited: one line per crew member per
MEMBER_GAP_S, at most MAX_PER_EVENT non-pilot lines per event. Once landed and stopped with damage, an engineer
offers to go out and fix it (once per damage list). '/crew chatter on|off' (default on).

Each line is AI-generated with the chat's current backend (Luke's simple role-play prompt, real watcher facts only),
in a background thread with an LLM_TIMEOUT_S deadline; slow / failed / unusable answers (tool calls, action claims,
too long) fall back to the canned pools. The pilot's MAYDAY and recovery never wait on any of this.
"""
import logging
import random
import re
import threading
import time

log = logging.getLogger("kspchat")
LLM_TIMEOUT_S = 4.0
MAX_WORDS = 22   # (a dad joke needs a few more words than a scream)
BACKEND = {"name": "local", "override": None}   # the chat's last backend (chat.Session.send notes it)

MEMBER_GAP_S = 10.0
MAX_PER_EVENT = 2
CRACKLE_P = 0.15
SETTING = "crew_chatter"
ABBR = {"pilot": "Pilot", "scientist": "Sci", "engineer": "Eng", "tourist": "Tourist"}
_last = {}            # name -> time of their last line
_offered = {}         # vessel id -> damage tuple already offered
_rng = random.Random()

# what the event is about (for the engineer / scientist)
MECH = {"parts", "flameout", "overheat", "config", "prop_out", "rotor_brake", "rotor_torque", "heli_rpm", "heli_tail",
        "reverse"}

LINES = {
    "scientist": [
        "WHAT IS GOING ON?!", "These readings are off the charts! Is that GOOD?!", "I'm logging everything! For science! AAAH!",
        "G-meter says {g:.1f}! My stomach says more!", "Fascinating! Terrifying! Mostly terrifying!",
        "Somebody explain the data! Anybody!", "Is it supposed to make that noise?!",
    ],
    "scientist_mech": [
        "Sensors just lost the {part}! WHAT HAPPENED?!", "The {part}?! That's not in my experiment plan!",
        "Readings dropped out with the {part}! Engineers, talk to me!",
    ],
    "engineer": [
        "That's not supposed to do that!", "Hold together, baby, hold together...", "I just tightened those bolts!",
        "Everything's rattling back here!", "Who signed off on this design?! Oh. Me.",
    ],
    "engineer_mech": [
        "The {part} is GONE! I'll have to fix that!", "{part} - that's MY {part}! I'll have to fix that!",
        "We lost the {part}! Somebody owes me a wrench!", "The {part}! I JUST serviced that!",
        "{part} is shot! Don't touch anything else!",
    ],
    "tourist": [
        "AAAAAAAAHHHHH!", "I WANT A REFUND!", "WAAAAAAAH!", "IS THIS PART OF THE TOUR?!", "MOMMY!",
        "EEEEEEEEEE!", "I'M NEVER FLYING AGAIN! AAAH!",
    ],
    "repair": [
        "We're stopped. I can go out and fix the {part} - say the word.",
        "Parked. Give me a minute outside and the {part} will be good as new. Mostly.",
        "Stopped at last. I'll grab the toolbox for the {part} - just say go.",
    ],
}


def enabled():
    try:
        from . import settings
        v = settings.get(SETTING)
        return True if v is None else bool(v)
    except Exception:  # noqa: BLE001
        return True


def command(arg):
    """'/crew chatter on|off' (also '/crew on|off', '/crew' = status)."""
    a = " ".join(str(arg or "").lower().split()).replace("chatter", "").strip()
    from . import settings
    if a in ("on", "off"):
        settings.put(SETTING, a == "on")
        return f"Crew intercom chatter {a}."
    return f"Crew intercom chatter is {'on' if enabled() else 'off'}. Use /crew chatter on|off."


def norm_trait(t):
    t = str(t or "").strip().lower()
    for k in ABBR:
        if k in t:
            return k
    return "tourist" if not t else t


def fmt(name, trait, text, crackle=None):
    """'[INTERCOM] Bob (Sci): WHAT IS GOING ON?!' (engineers on [COMMS]); short, sometimes '-- over'."""
    first = str(name).split()[0] if name else "Crew"
    tr = norm_trait(trait)
    tag = "[COMMS]" if tr == "engineer" else "[INTERCOM]"
    c = (_rng.random() < CRACKLE_P) if crackle is None else crackle
    from . import language
    return f"{tag} {first} ({ABBR.get(tr, tr.title())}): {language.clean(text)}" + (" -- over" if c else "")


def _part(ev):
    lost = [x for x in (ev.get("lost") or []) if x]
    what = lost[0] if lost else (ev.get("what") or "")
    return what or "that part"


def slots(ev, crew, pilot, now=None, g=1.0, rng=None):
    """Who speaks for one emergency event: [(name, trait, canned text)] (pure apart from the rate-limit memory).
    crew = [(name, trait)], pilot = the name making the MAYDAY (excluded)."""
    if not ev.get("start") or ev.get("quiet"):
        return []
    now = time.time() if now is None else now
    rng = rng or _rng
    mech = ev.get("kind") in MECH
    pfirst = str(pilot or "").split()[0].lower()
    others = [(n, norm_trait(t)) for n, t in crew or [] if str(n).split()[0].lower() != pfirst]
    order = {"engineer": 0, "scientist": 1, "tourist": 2} if mech else {"tourist": 0, "scientist": 1, "engineer": 2}
    others.sort(key=lambda x: (order.get(x[1], 3), rng.random()))
    out, seen = [], set()
    for name, tr in others:
        if len(out) >= MAX_PER_EVENT:
            break
        if tr not in ("engineer", "scientist", "tourist") or now - _last.get(name, -1e9) < MEMBER_GAP_S:
            continue
        if tr in seen and len(others) > len(seen) + 1:  # variety: a second of the same trait only if nobody else
            continue
        pool = LINES[f"{tr}_mech"] if mech and f"{tr}_mech" in LINES and ev.get("kind") != "reverse" else LINES[tr]
        text = rng.choice(pool).format(part=_part(ev), g=float(g or 1.0))
        out.append((name, tr, text[:1].upper() + text[1:]))
        _last[name] = now
        seen.add(tr)
    return out


def reactions(ev, crew, pilot, now=None, g=1.0, rng=None):
    """Canned intercom lines for one event (the fallback when the AI is slow or fails)."""
    return [fmt(n, tr, text) for n, tr, text in slots(ev, crew, pilot, now, g, rng)]


def repair_offer(vid, crew, lost, sit, speed, rng=None):
    """Landed / stopped with damage and an engineer aboard -> one offer per damage list, else None."""
    if not lost or sit not in ("landed", "splashed", "pre_launch") or speed > 1.0:
        return None
    eng = next((n for n, t in crew or [] if norm_trait(t) == "engineer"), None)
    key = tuple(lost)
    if eng is None or _offered.get(vid) == key:
        return None
    _offered[vid] = key
    part = lost[0] if len(lost) == 1 else f"{lost[0]} and the rest"
    return fmt(eng, "engineer", (rng or _rng).choice(LINES["repair"]).format(part=part), crackle=False)


def reset():
    _last.clear()
    _offered.clear()
    _snapped.clear()


# ======================================================================== AI-generated lines
FACTS = {
    "flameout": "an engine just flamed out ({what})", "parts": "the craft just lost parts: {what}",
    "overheat": "a part is overheating ({what})", "fuel": "fuel is almost gone", "stall": "the plane is stalling",
    "blackout": "the pilot just blacked out from the G-force", "reverse": "the engines went into reverse thrust in flight",
    "config": "someone changed the controls mid-flight ({what})", "upside_down": "the craft is upside down in flight",
    "upside_down_ground": "the craft landed upside down", "prop_out": "the {what} stopped turning",
    "heli_rpm": "{what} is losing RPM", "heli_tail": "the helicopter is spinning out of control",
    "heli_vrs": "the helicopter is sinking in its own downwash", "rotor_brake": "someone put the rotor brake on in flight ({what})",
    "rotor_torque": "the rotor torque dropped to zero ({what})",
}
TRAIT_WORD = {"scientist": "scientist", "engineer": "engineer", "tourist": "tourist"}
_ACTION = re.compile(r"\bI(?:'ve|'ll| have| will| am|'m)?\s+(?:just\s+)?(?:fix(?:ed)?|repair(?:ed)?|deploy(?:ed)?|engag(?:e|ed)|"
                     r"releas(?:e|ed)|activat(?:e|ed)|eject(?:ed)?|stag(?:e|ed)|land(?:ed)?|cut|turn(?:ed)?|switch(?:ed)?|"
                     r"pull(?:ed)?|press(?:ed)?|flip(?:ped)?|reset)\b|\b(?:fixed|repaired|all clear|good as new)\b", re.I)
_TOOLISH = re.compile(r"[{}<>`\[\]]|\b(?:tool|function|call|json|assistant|as an ai)\b", re.I)


def facts_for(ev, g=None):
    f = FACTS.get(ev.get("kind"), (ev.get("short") or ev.get("kind") or "an emergency").lower())
    f = f.format(what=ev.get("what") or "unknown").replace(" ()", "")
    if g and float(g) > 3.0:
        f += f", pulling {float(g):.1f} G"
    return f


def who(name, trait):
    """'Bob, a nervous scientist who loves rocks and snacks and hates heights' (personality.py)."""
    first = str(name).split()[0] if name else "Crew"
    try:
        from . import personality
        return f"{first}, {personality.describe(name, TRAIT_WORD.get(trait, trait))}"
    except Exception:  # noqa: BLE001
        return f"{first}, a {TRAIT_WORD.get(trait, trait)}"


def prompt(name, trait, facts):
    """Luke's style: simple role-play."""
    return (f"You are {who(name, trait)}, aboard a Kerbal spacecraft. This is happening: {facts}. "
            "What do you shout over the intercom? One short line.")


def clean(text):
    """One usable intercom line from a model reply, or None (tool calls, action claims, too long -> canned)."""
    t = re.sub(r"<think>.*?</think>", "", str(text or ""), flags=re.S).strip()
    t = next((x.strip() for x in t.splitlines() if x.strip()), "")
    t = re.sub(r"^[\w .'-]{1,40}:\s*", "", t) if ":" in t[:42] else t   # "Bob Kerman: ..." -> "..."
    t = t.strip().strip('"\'*_ ').strip()
    if not t or _TOOLISH.search(t) or _ACTION.search(t) or len(t.split()) > MAX_WORDS or len(t) > 120:
        return None
    return t


def note_backend(backend, override=None):
    BACKEND.update(name=backend or "local", override=override)


def _llm(name, trait, facts, text=None):
    """Ask the chat's current backend for one line (no tools). Never loads a model; raises on any problem."""
    from . import backends, chat, config, language
    b = backends.normalize(BACKEND["name"])
    if b == "local":
        loaded = [m for m, l in backends.lmstudio_models() if l]
        if not loaded:
            raise RuntimeError("no LM Studio model loaded")
        url, key, model = config.LMSTUDIO_URL, None, BACKEND["override"] or loaded[0]
    else:
        url, key, model = backends.resolve(b, BACKEND["override"])
    data = chat._post(url.rstrip("/") + "/chat/completions", {
        "model": model, "temperature": 0.9, "max_tokens": 40,
        "messages": [{"role": "system", "content": "Answer with the line only, at most 12 words. You can only talk. "
                      + language.prompt_lines()},
                     {"role": "user", "content": text or prompt(name, trait, facts)}]}, key, timeout=LLM_TIMEOUT_S)
    return data["choices"][0]["message"].get("content")


def _one(name, trait, canned, facts, post, gen, timeout, text=None, check=None):
    """Generate with a deadline (in its own thread), else canned; then post. text = a full prompt (else the
    emergency prompt from facts); check(line) -> False rejects a line (e.g. invented numbers)."""
    box = {}

    def work():
        try:
            box["text"] = gen(name, trait, facts) if text is None else gen(name, trait, facts, text)
        except Exception as e:  # noqa: BLE001
            box["err"] = e
    th = threading.Thread(target=work, daemon=True, name="crew-llm")
    th.start()
    th.join(timeout)
    line = clean(box.get("text")) if "text" in box else None
    if line and check is not None and not check(line):
        line, box["err"] = None, "invented numbers"
    src = "ai" if line else ("canned: " + ("timeout" if th.is_alive() else str(box.get("err") or "unusable reply")))
    log.info("crew: %s (%s) line %s", name, trait, src)
    try:
        post(fmt(name, trait, line or canned))
    except Exception:  # noqa: BLE001
        pass


def speak(ev, crew, pilot, g=1.0, post=None, gen=None, timeout=LLM_TIMEOUT_S, wait=False):
    """Intercom reactions to an emergency event, posted from background threads (the caller never waits unless
    wait=True, for tests). -> the threads started."""
    if not enabled():
        return []
    picks = slots(ev, crew, pilot, g=g)
    if not picks:
        return []
    if post is None:
        from . import science
        post = science.post_event
    facts = facts_for(ev, g)
    ths, joked = [], False
    for name, tr, canned in picks:  # (the pilot is never in picks: the pilot flies the recovery)
        kw = {}
        if _dad(name) and _rng.random() < EMERG_JOKE_P:  # a nervous dad joke as this kerbal's line
            canned = _rng.choice(EMERG_DAD_JOKES)
            kw["text"] = emerg_joke_prompt(name, tr, facts)
            joked = True
        th = threading.Thread(target=_one, args=(name, tr, canned, facts, post, gen or _llm, timeout), kwargs=kw,
                              daemon=True, name="crew-chatter")
        th.start()
        ths.append(th)
    key = ev.get("key") or ev.get("kind")
    p_snap = SNAP_JOKE_P if joked else (SNAP_P if len(picks) >= 2 else 0.0)
    if pilot and key not in _snapped and p_snap and _rng.random() < p_snap:
        _snapped.add(key)
        crew_ths = list(ths)

        # only when crew chatter is actually happening in this emergency (speak() returned early if chatter is off
        # or nobody else spoke); never unprompted
        def snap():  # after the crew lines are out (async: the recovery never waits on this)
            for th in crew_ths:
                th.join(timeout + 1.0)
            from . import language
            try:
                post(f"[RADIO] {str(pilot).split()[0]}: {language.clean(_rng.choice(SNAPS))}")
            except Exception:  # noqa: BLE001
                pass
        sth = threading.Thread(target=snap, daemon=True, name="crew-snap")
        sth.start()
        ths.append(sth)
    if wait:
        for th in ths:
            th.join(2 * timeout + 2.0)
    return ths


def speak_repair(vid, crew, lost, sit, speed, post=None, gen=None, timeout=LLM_TIMEOUT_S, wait=False):
    """Engineer's repair offer when stopped with damage (AI line with the canned offer as fallback)."""
    if not enabled():
        return None
    line = repair_offer(vid, crew, lost, sit, speed)
    if not line:
        return None
    eng = next(n for n, t in crew if norm_trait(t) == "engineer")
    canned = line.split(": ", 1)[1]
    facts = f"the craft has stopped on the ground and is damaged ({', '.join(lost)}); you could go out and repair it"
    if post is None:
        from . import science
        post = science.post_event
    th = threading.Thread(target=_one, args=(eng, "engineer", canned, facts, post, gen or _llm, timeout), daemon=True,
                          name="crew-repair")
    th.start()
    if wait:
        th.join(timeout + 1.0)
    return th

# ======================================================================== trip chatter ("ARE WE THERE YET?")
TRIP_MIN_FLIGHT_S = 120.0          # no autopilot trip: only after this long in the air
SHORT_TRIP_S = 300.0               # ETA under this: at most one line, rarely
SHORT_TRIP_P = 0.35
GAP_MIN_S, GAP_MAX_S = 180.0, 360.0
FIRST_MIN_S = 90.0
CALM_AFTER_EMERGENCY_S = 60.0
PILOT_REPLY_P = 0.5
DAD_JOKE_P = 0.35          # a trip line from a 'loves dad jokes' kerbal is a joke this often ...
DAD_JOKE_GAP_S = 900.0     # ... and at most one joke per 15 min of flight
EMERG_JOKE_P = 0.5         # in an emergency a dad-joke lover cracks a nervous one this often (counts toward the 2 lines)
SNAP_P, SNAP_JOKE_P = 0.25, 0.6   # the pilot snaps back: after 2+ crew lines / after a crew dad joke (once per event)
SNAPS = ["Everyone QUIET, I need to concentrate!", "Cut the chatter! I'm flying here!", "Intercom OFF, people! Busy!",
         "Not now! Hands full!", "Can it, back there! Working!", "One more joke and you're walking home!"]
_snapped = set()
EMERG_DAD_JOKES = ["I'd panic, but I left my panic in my other suit. Heh. AAAH!",
                   "Well, at least this ride is... breathtaking. Literally. HELP!",
                   "Good news: we're making great time downward!", "I guess you could say things are... falling into place?!",
                   "Somebody tell me this is a drill. Get it? Drill? ENGINEERS?!",
                   "This is fine. Totally fine. Fine-ally the end? NO!"]
DAD_JOKES = ["Why don't rockets ever get lonely? They always have a launch buddy.",
             "I'd tell you a space joke, but it's too far out.", "What holds the Mun up? Moonbeams.",
             "I used to hate gravity. Now I'm really down to earth.", "Why did the strut break up? It felt held back.",
             "Did you hear about the claustrophobic astronaut? He just needed a little space.",
             "How do you organise a space party? You planet.", "This flight is so smooth it should be called a glide-ness."]
TRIP_LINES = {
    "scientist": ["Are we there yet?", "How much longer? My samples are getting bored.", "Is this the scenic route?",
                  "I've catalogued every cloud. Twice. Are we close?", "By my calculations we should be there by now."],
    "engineer": ["Are we there yet? Engine sounds fine, I'm just bored.", "How long? I've tightened every bolt twice.",
                 "Are we close? I'm running out of things to fix.", "Any chance we arrive this week?"],
    "tourist": ["ARE WE THERE YET?", "Are we there yet? Are we? Are we?", "I need the bathroom. Are we close?",
                "Is that it? Is THAT it?", "This is the longest tour EVER."],
    "impatient": ["ARE. WE. THERE. YET?!", "Seriously, how much LONGER?!", "I've asked {n} times! ARE WE THERE?!",
                  "I'm walking the rest of the way!"],
}
PILOT_REPLIES = {
    "eta": ["About {eta} to go - sit tight.", "{dist} out, roughly {eta}. Patience.", "Roughly {eta}. Ask me again and I land here.",
            "{eta}, give or take. Enjoy the view."],
    "dist": ["{dist} to go. Sit tight.", "Still {dist} out. Patience."],
    "none": ["No ETA yet - enjoy the view.", "We get there when we get there.", "Sit back. I'll tell you when."],
}


def _fmt_eta(s):
    s = int(round(float(s)))
    return f"{s // 60} min" if s >= 120 else (f"{s // 60} min {s % 60} s" if s >= 60 else f"{s} s")


def trip_facts_text(dest, eta_s, dist_km):
    bits = ["a long, quiet cruise"]
    if dest:
        bits.append(f"heading to {dest}")
    if eta_s:
        bits.append(f"about {_fmt_eta(eta_s)} to go")
    if dist_km:
        bits.append(f"{dist_km:.0f} km away" if dist_km >= 10 else f"{dist_km:.1f} km away")
    return ", ".join(bits)


def trip_prompt(name, trait, facts, n):
    """Luke's style: simple role-play."""
    return (f"You are {who(name, trait)}, aboard a Kerbal spacecraft. This is happening: {facts}"
            + (f"; you've already asked {n} times" if n else "") + ". What do you say over the intercom? One short line.")


def _dad(name):
    try:
        from . import personality
        return personality.likes(name, "loves dad jokes")
    except Exception:  # noqa: BLE001
        return False


def joke_prompt(name, trait, facts):
    """Luke's style: simple role-play; clean."""
    return (f"You are {who(name, trait)}, aboard a Kerbal spacecraft. This is happening: {facts}. Tell one short, "
            "clean dad joke over the intercom. One short line.")


def emerg_joke_prompt(name, trait, facts):
    return (f"You are {who(name, trait)}, aboard a Kerbal spacecraft. This is happening: {facts}. Crack one short, "
            "clean, nervous dad joke about it over the intercom. One short line.")


def pilot_prompt(name, facts):
    return (f"You are {who(name, 'pilot')}, flying a Kerbal spacecraft. A passenger just asked 'are we there yet?'. This is "
            f"happening: {facts}. What do you answer over the intercom? One short line; only use the numbers given.")


def numbers_ok(line, allowed):
    """No invented numbers: every number in the line must appear in the real facts."""
    return all(x in allowed for x in re.findall(r"\d+(?:\.\d+)?", line))


def next_gap(eta_s, n, rng=None):
    """Seconds until the next trip line (None = no more). Short trips: maybe one; long trips: every 3-6 min,
    shrinking as the impatience grows."""
    rng = rng or _rng
    if eta_s is not None and eta_s < SHORT_TRIP_S:
        if n >= 1 or rng.random() > SHORT_TRIP_P:
            return None
        return rng.uniform(FIRST_MIN_S * 0.5, max(FIRST_MIN_S * 0.5 + 1, min(eta_s * 0.6, FIRST_MIN_S * 2)))
    lo, hi = (FIRST_MIN_S, GAP_MAX_S * 0.6) if n == 0 else (GAP_MIN_S, GAP_MAX_S)
    return rng.uniform(lo, hi) * max(0.6, 1.0 - 0.1 * n)


class Trip:
    """'Are we there yet?' scheduler (ticked by the emergency watcher once a second)."""

    def __init__(self):
        self.reset()

    def reset(self, vid=None):
        self.vid, self.fly_since, self.due, self.n, self.done, self.last_emerg = vid, None, None, 0, False, -1e9
        self.last_joke = -1e9

    def tick(self, now, vid, sit, crew, pilot, trip_active, emergency, facts_fn, post=None, gen=None,
             timeout=LLM_TIMEOUT_S, rng=None, wait=False):
        """facts_fn() -> (dest, eta_s, dist_km) (only real values, None when unknown). -> True if a line started."""
        rng = rng or _rng
        if vid != self.vid:
            self.reset(vid)
        if sit != "flying":
            self.reset(vid)
            return False
        self.fly_since = now if self.fly_since is None else self.fly_since
        if emergency:
            self.last_emerg, self.due = now, None
            return False
        if not enabled() or self.done or now - self.last_emerg < CALM_AFTER_EMERGENCY_S:
            return False
        if not (trip_active or now - self.fly_since >= TRIP_MIN_FLIGHT_S):
            return False
        if "TALK" in globals() and TALK.active():  # a conversation is going on: no 'are we there yet' over it
            return False
        pf = str(pilot or "").split()[0].lower()
        cands = [(nm, norm_trait(t)) for nm, t in crew or [] if str(nm).split()[0].lower() != pf
                 and norm_trait(t) in ("scientist", "engineer", "tourist") and now - _last.get(nm, -1e9) >= MEMBER_GAP_S]
        if not cands:
            return False
        if self.due is None:
            dest, eta, dist = facts_fn()
            gap = next_gap(eta, self.n, rng)
            if gap is None:
                self.done = True
                return False
            self.due = now + gap
            return False
        if now < self.due:
            return False
        dest, eta, dist = facts_fn()
        name, tr = rng.choice(cands)
        _last[name] = now
        facts = trip_facts_text(dest, eta, dist)
        joke = now - self.last_joke >= DAD_JOKE_GAP_S and rng.random() < DAD_JOKE_P and _dad(name)
        if joke:  # a clean dad joke instead of 'are we there yet' (doesn't count as asking)
            self.last_joke = now
            if post is None:
                from . import science
                post = science.post_event
            th = threading.Thread(target=_one, args=(name, tr, rng.choice(DAD_JOKES), facts, post, gen or _llm, timeout),
                                  kwargs={"text": joke_prompt(name, tr, facts)}, daemon=True, name="crew-joke")
            th.start()
            if wait:
                th.join(timeout + 2.0)
            nxt = next_gap(eta, max(self.n, 1), rng)
            self.due = None if nxt is None else now + nxt
            return True
        self.n += 1
        pool = TRIP_LINES["impatient"] if self.n >= 3 and rng.random() < 0.6 else TRIP_LINES[tr]
        canned = rng.choice(pool).format(n=self.n)
        nxt = next_gap(eta, self.n, rng)
        self.due = None if nxt is None else now + nxt
        self.done = nxt is None
        reply = None
        if pilot and rng.random() < PILOT_REPLY_P:
            if eta:
                canned_p = rng.choice(PILOT_REPLIES["eta"]).format(eta=_fmt_eta(eta), dist=f"{dist:.0f} km" if dist else "a way")
            elif dist:
                canned_p = rng.choice(PILOT_REPLIES["dist"]).format(dist=f"{dist:.0f} km")
            else:
                canned_p = rng.choice(PILOT_REPLIES["none"])
            allowed = set(re.findall(r"\d+(?:\.\d+)?", facts + " " + canned_p))
            reply = (pilot, canned_p, facts, allowed)
        if post is None:
            from . import science
            post = science.post_event

        def run():
            _one(name, tr, canned, facts, post, gen or _llm, timeout, text=trip_prompt(name, tr, facts, self.n - 1))
            if reply:
                p, cp, f, allowed = reply
                _one(p, "pilot", cp, f, post, gen or _llm, timeout, text=pilot_prompt(p, f),
                     check=lambda line: numbers_ok(line, allowed))
        th = threading.Thread(target=run, daemon=True, name="crew-trip")
        th.start()
        if wait:
            th.join(2 * timeout + 2.0)
        return True


TRIP = Trip()


def trip_facts(v=None):
    """(destination, eta_s, distance_km) from the running autopilots (real values only; None when unknown)."""
    dest = eta = dist = None
    try:
        from . import heli, plane
        if plane.active():
            st = plane.STATUS
            dest = f"runway {st['runway']}" if st.get("runway") else None
        elif heli.active() and heli.STATE.get("goto"):
            dest = "the destination"
    except Exception:  # noqa: BLE001
        pass
    try:
        from . import flightplan
        line = flightplan.status_line() if flightplan.active() else ""
        if line and not dest:
            dest = line[:60]
    except Exception:  # noqa: BLE001
        pass
    try:
        from . import spots
        r = spots.landing_eta(quiet=False)
        if r.get("active") and r.get("mode") == "H":
            eta = r.get("eta_s")
            dist = r.get("distance_km")
            dest = dest or r.get("target")
    except Exception:  # noqa: BLE001
        pass
    return dest, eta, dist


def trip_active():
    try:
        from . import flightplan, heli, hold, plane
        return bool(plane.active() or hold.active() or heli.active() or flightplan.active())
    except Exception:  # noqa: BLE001
        return False


# ======================================================================== small talk (calm cruise conversations)
TALK_GAP_MIN_S, TALK_GAP_MAX_S = 480.0, 900.0     # long trips: one conversation every ~8-15 min
TALK_FIRST_MIN_S, TALK_FIRST_MAX_S = 240.0, 600.0
TALK_SHORT_P = 0.2                                # short trips (ETA < 5 min): rarely, at most once
TALK_LINES_MIN, TALK_LINES_MAX = 4, 6
TALK_SPACING = (5.0, 10.0)
TOPICS = {
    "the new stew in the cafeteria": ["Have you tried the new stew in the cafeteria?", "The green one? It moved.",
                                      "It's supposed to move. Extra protein.", "I'm sticking to snacks, thanks.",
                                      "More stew for me then!", "You're braver than any pilot."],
    "snacks": ["Who ate the last snack bar?", "Define 'ate'.", "You hid it in the strut bin again?",
               "Snacks are mission critical. I'm protecting them.", "From me?", "Especially from you."],
    "Jeb": ["Did you hear what Jeb did last week?", "The thing with the boosters?", "No, the OTHER thing.",
            "Gene's still finding pieces of the hangar.", "Legend.", "Don't tell him that, he'll do it again."],
    "the view": ["You ever just look out the window?", "All the time. Clouds look like snacks.",
                 "That one looks like Jeb.", "That one IS on fire.", "...Probably just the sunset.", "Probably."],
    "science": ["I logged three new readings today!", "What do they mean?", "No idea. That's the fun part.",
                "Will it get us more funding?", "If it explodes, definitely.", "Let's not."],
    "sleeping in the bunks": ["Did you sleep at all last night?", "Somebody snores like a mainsail.",
                              "That was the coolant pump.", "Then the coolant pump snores.",
                              "I'll fix it after the trip.", "Bring earplugs next time."],
}


def talk_gap(eta_s, n, rng=None):
    """Seconds until the next conversation (None = no more on this trip)."""
    rng = rng or _rng
    if eta_s is not None and eta_s < SHORT_TRIP_S:
        if n >= 1 or rng.random() > TALK_SHORT_P:
            return None
        return rng.uniform(FIRST_MIN_S * 0.5, max(FIRST_MIN_S * 0.5 + 1, min(eta_s * 0.5, FIRST_MIN_S * 2)))
    return rng.uniform(TALK_FIRST_MIN_S, TALK_FIRST_MAX_S) if n == 0 else rng.uniform(TALK_GAP_MIN_S, TALK_GAP_MAX_S)


def talk_prompt(name, trait, other, topic, transcript):
    """Luke's style: simple role-play, with the conversation so far."""
    base = f"You are {who(name, trait)}, aboard a Kerbal spacecraft on a quiet cruise. "
    if not transcript:
        return base + f"You start a chat with {other} about {topic}. What do you say over the intercom? One short line."
    convo = " ".join(f"{n}: \"{t}\"" for n, t in transcript[-4:])
    return base + f"You're chatting with {other} about {topic}. So far: {convo} What do you reply? One short line."


def pick_pair(crew, pilot, rng=None):
    """Two talkers: two non-pilot crew, else the pilot plus the only other kerbal. None if fewer than two aboard."""
    rng = rng or _rng
    pf = str(pilot or "").split()[0].lower()
    others = [(n, norm_trait(t)) for n, t in crew or [] if str(n).split()[0].lower() != pf]
    if len(others) >= 2:
        return rng.sample(others, 2)
    if len(others) == 1 and pilot:
        return [(pilot, "pilot"), others[0]] if rng.random() < 0.5 else [others[0], (pilot, "pilot")]
    return None


def converse(pair, topic, n_lines, post, gen, timeout, cut, spacing=TALK_SPACING, rng=None):
    """Run one conversation (call in a thread): alternating lines, each AI-generated from the speaker's personality
    and the conversation so far (canned script fallback), spaced `spacing` s apart. cut (threading.Event) or the
    chatter switch going off ends it at once. -> lines posted."""
    rng = rng or _rng
    script = TOPICS.get(topic) or TOPICS["snacks"]
    transcript, said = [], 0
    for i in range(n_lines):
        if cut.is_set() or not enabled():
            break
        name, tr = pair[i % 2]
        other = str(pair[(i + 1) % 2][0]).split()[0]
        out = []
        _one(name, tr, script[i % len(script)], topic, out.append, gen, timeout,
             text=talk_prompt(name, tr, other, topic, transcript))
        if cut.is_set() or not enabled() or not out:  # an emergency while generating: drop the line
            break
        try:
            post(out[0])
        except Exception:  # noqa: BLE001
            pass
        _last[name] = time.time()
        said += 1
        transcript.append((str(name).split()[0], out[0].split(": ", 1)[-1].replace(" -- over", "")))
        if i < n_lines - 1 and cut.wait(rng.uniform(*spacing)):
            break
    return said


class SmallTalk:
    """Calm-cruise conversations (ticked by the emergency watcher once a second; same eligibility as Trip)."""

    def __init__(self):
        self.cut = threading.Event()
        self.th = None
        self.reset()

    def reset(self, vid=None):
        self.cut.set()  # ends a running conversation
        self.cut = threading.Event()
        self.vid, self.fly_since, self.due, self.n, self.done, self.last_emerg = vid, None, None, 0, False, -1e9

    def active(self):
        return self.th is not None and self.th.is_alive()

    def tick(self, now, vid, sit, crew, pilot, trip_active, emergency, facts_fn, post=None, gen=None,
             timeout=LLM_TIMEOUT_S, rng=None, spacing=TALK_SPACING, wait=False):
        rng = rng or _rng
        if vid != self.vid or sit != "flying":
            if vid != self.vid or self.fly_since is not None:
                self.reset(vid)
            return False
        self.fly_since = now if self.fly_since is None else self.fly_since
        if emergency:  # cut it off immediately
            self.cut.set()
            self.last_emerg, self.due = now, None
            return False
        if not enabled():
            self.cut.set()
            return False
        if self.done or self.active() or now - self.last_emerg < CALM_AFTER_EMERGENCY_S:
            return False
        if not (trip_active or now - self.fly_since >= TRIP_MIN_FLIGHT_S):
            return False
        pair = pick_pair(crew, pilot, rng)
        if not pair:
            return False
        if self.due is None:
            gap = talk_gap(facts_fn()[1], self.n, rng)
            if gap is None:
                self.done = True
                return False
            self.due = now + gap
            return False
        if now < self.due:
            return False
        eta = facts_fn()[1]
        self.n += 1
        nxt = talk_gap(eta, self.n, rng)
        self.due, self.done = (None if nxt is None else now + nxt), nxt is None
        topic = rng.choice(list(TOPICS))
        n_lines = rng.randint(TALK_LINES_MIN, TALK_LINES_MAX)
        if post is None:
            from . import science
            post = science.post_event
        self.cut = threading.Event()
        log.info("crew: small talk %s & %s about %s (%d lines)", pair[0][0], pair[1][0], topic, n_lines)
        self.th = threading.Thread(target=converse, args=(pair, topic, n_lines, post, gen or _llm, timeout, self.cut),
                                   kwargs={"spacing": spacing}, daemon=True, name="crew-talk")
        self.th.start()
        if wait:
            self.th.join(n_lines * (timeout + spacing[1] + 1.0))
        return True


TALK = SmallTalk()
