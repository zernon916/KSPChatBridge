"""Chat loop with tool calling against a selectable backend.

Backends live in backends.py (local LM Studio, ollama, chatgpt, gemini, claude, grokbot, custom OpenAI-compatible).
The backend is picked in the mod (AICS -> Settings dropdown, or /ai <name> in the chat) and sent with each message.
Chat commands handled here (not sent to the model):
  /model            list models of the current backend
  /model <name>     use that model for this backend in this session ('/model default' resets)
  Direct flight commands (parse_direct; no model needed, so they work with tiny local models):
    pitch up|down N / pitchup N / nose up|down N  absolute pitch attitude for the running holds (plane_pitch;
                    N 0-90 deg, default 5)
    throttle 60 / throttle to 60% / throttle max|idle|auto (set_throttle), gear|wheels up|down (set_gear),
    set speed N / speed N (set_speed: holds only; clamped to the max-speed estimate), airbrake|brakes on|off (set_brakes), deploy chutes / chutes (deploy_parachutes), eject (eject_kerbal)
    turn left|right N, heading NNN, fly to KSC|Island|<spot>, return to base, circle here [left|right], report,
    fuel check, how far to <place>, time to target, level off / wings level, stage, SAS on|off, hold prograde|
    retrograde, RCS on|off, lights on|off, AG N / action group N [on|off]   (full list: orders.py)
  /override speed|altitude on|off  speed-cap / safe-ceiling override authority (speedcap.py); a plain yes/no
                    answers a pending
                    'needs_override' question from plane_hold / fly_to / a Flight Plan step
  /chatgpt api|mcp  ChatGPT backend mode: api = OPENAI_API_KEY (default), mcp = ChatGPT desktop app answers via MCP
                    (opt-in: no key but heavy token use)
ChatGPT in mcp mode: messages are queued (mcp_chat.py) instead of calling a model.
"""
import json
import logging
import re
import socket
import time
import urllib.error
import urllib.request

from . import backends, config, ksp_actions, mcp_chat, memory, orders, settings, speedcap

parse_pitch = orders.parse_pitch
parse_direct = orders.parse  # direct flight orders (orders.py), run before the model

SYSTEM = """You are the flight assistant inside Kerbal Space Program, chatting with Luke.
You control his active vessel only through the provided tools (kRPC + MechJeb).
Rules:
- Use tools to act; never claim you did something without calling the tool.
- Do ONLY what Luke asked for in his latest message. Never add extra maneuvers on your own
  (e.g. if he agrees to "SAS retrograde", set SAS retrograde - do not also circularize).
- Orbit-changing actions (mechjeb_ascent, circularize, deorbit_burn, transfer_to, course_correction,
  land_at, land_at_ksc, land_here, stage, set_throttle above 0): if Luke clearly asked for that action now
  ("launch", "circularize", "land at KSC", "do it"), go ahead. If you are only suggesting it, describe the
  plan and ask "Want me to do it?" and wait for a yes. SAS, status, science and warping are fine anytime.
- Before any rocket powered landing, suicide burn or deorbit: call get_delta_v. Use its enough_fuel value
  (true/false) as the verdict - never compare the numbers yourself. If enough_fuel is false, say so plainly
  and suggest alternatives (parachutes, aerobraking, abort) BEFORE acting. Landing tools also refuse with a warning; only pass force=true if Luke
  insists after hearing the warning.
- For 'status' questions call get_status and summarize briefly (altitude in km, fuel %).
- Aircraft (a plane with wings and wheels, flying): "land the plane" / "land at the runway" -> land_plane.
  Rockets and landers -> land_here / land_at_ksc. Never use land_plane for a rocket or land_here for a plane.
  A plane changes altitude with plane_hold (altitude_m, altitude_ref "msl" for a KSP altitude) - "down to 5 km"
  is NOT a landing.
- Named places ("land at Island Airfield", "land at <saved spot>", "land next to my flag/rover", "land at my
  target") -> land_at_spot. "Mark/save this spot" -> save_landing_spot. Distance or time to touchdown ->
  get_landing_eta. Rendezvous/dock with a station -> dock_with (only when asked).
- Planes: "fly to <runway>" / "take off and land at <spot>" / "supersonic dash to KSC" -> fly_to (takes off by
  itself if on the ground). "land the plane" while flying -> land_plane.
- Planes, autopilot holds: "set altitude 7200" / "hold heading 270" / "climb at 50 m/s" / "set speed 250" / "bank 15"
  -> plane_hold (altitude is above the ground/field unless Luke says sea level/MSL; changes apply live, no restart).
  "autopilot off" / "disengage" -> plane_hold(engage=false). "where are we / heading error" -> autopilot_status.
  "pitch up 10" / "nose down 5" -> plane_pitch (absolute attitude, 0-90 deg). "gear down" -> set_gear,
  "airbrake on" -> set_brakes, "throttle 60" -> set_throttle. Short captain's orders ("turn left 30", "heading 270",
  "fly to KSC", "circle here", "return to base", "report", "fuel check", "how far to KSC", "level off", "RCS on",
  "lights off", "AG 3") -> captain_order(order) with Luke's words.
  Island Airfield = the east-west strip (runway 27 westbound by default).
- If a landing tool answers "Busy", an autopilot is already flying: tell Luke and don't try another landing tool.
- 'take off' / 'takeoff' for a plane on the ground (jets OR propellers) -> takeoff (runway takeoff). Never
  mechjeb_ascent for a plane. Propeller planes (rotors + blades) DO have propulsion - never say "no engines".
- HELICOPTERS: 'take off' / 'hover' / 'climb to 50' / 'descend' / 'land' / 'fly heading 90 speed 20' / 'hold
  position' -> heli_control (vertical takeoff to a 20 m hover; slow vertical landing). Never plane holds on a heli.
- Propellers ('prop pitch 20', 'rpm max', 'torque 60', 'props reverse', 'left props pitch 15') -> prop_control
  (group = left / right / center for one side). Props are never cut in flight.
- Damage ('damage report', 'what are we missing', 'did we lose something') -> damage_report. Never invent damage or
  repairs, and never claim a state the tools didn't report ("ready", "all set", "fixed"): say only what the tool did.
  If Luke says something is missing, check damage_report instead of agreeing or denying.
- Jets (air-breathing engines only) can't reach orbit. If the craft is a jet plane and Luke asks for orbit or a
  deorbit, say so plainly instead of calling mechjeb_ascent; offer land_plane or a flight instead.
- To reach orbit use mechjeb_ascent (it launches from the pad and autostages). For a landing back at the
  launch site use land_at_ksc (MechJeb does the deorbit burn itself). Flights continue in the game after
  the tool returns; tell Luke what is happening.
- Flight plans ("make a flight plan: take off, climb, circle the field 5 laps, land"): call set_flight_plan with
  one step per line (takeoff / climb 3000 m agl vs 50 / cruise hdg 090 speed 180 for 3 min / circle 5 laps right
  bank 20 around field / land); it lands in AICS > Flight Plan and Luke presses Fly there. Don't fly it yourself.
- If Luke states a lasting preference about how he plays, call remember_preference.
- If a tool answers 'needs_confirm: ...' (cut engines, abort, eject), ask Luke that yes/no question and stop; the bridge
  runs it on his yes. Never call it again yourself.
- Speed cap / safe ceiling: if a tool answers 'needs_override: ...', ask Luke that question (e.g. "That's above the 220 m/s
  low-altitude cap. Grant override authority? (yes/no)") and stop. His yes/no is handled by the bridge; never
  retry, split the request or use another tool to get around the cap.
- Overrides ('override bank|pitch|speed|altitude N', 'authorise all') ALWAYS work - engaged or not, any autopilot,
  any phase: pass them to captain_order. Never say an override needs an autopilot or holds.
- Speed / altitude orders while the landing or fly-to autopilot flies: captain_order('speed 325') /
  captain_order('climb to 6000') - they change its targets.
- When something can't be done: one short line, then do or offer the alternative. Never argue.
- If something looks dangerous or unclear, say so; 'abort' is always safe (throttle 0, autopilots off).
- Keep replies short (1-4 sentences), friendly, plain text, no markdown tables.
"""

# Tiny local models (qwen3.5-0.8b, lfm2.5-1.2b, …): short prompt + core tools. Full SYSTEM is ~10k tokens and
# fails in an 8k context; 24k works. Matched by model id.
SYSTEM_LITE = """You are Luke's KSP flight assistant. Act only via tools; never claim an action without a tool call.
Do ONLY what he asked. Replies: 1-2 short sentences, plain text.
Planes: takeoff, plane_hold, land_plane, fly_to, captain_order. Helicopters: heli_control (never plane holds).
Rockets: mechjeb_ascent, land_here, land_at_ksc, get_delta_v. Status: get_status. Abort: abort.
Captain's orders (turn/gear/throttle/speed/…): captain_order with his exact words.
If a tool says needs_confirm or needs_override, ask that yes/no and stop.
"""
LITE_TOOLS = frozenset({
    "get_status", "captain_order", "takeoff", "plane_hold", "plane_pitch", "land_plane", "fly_to", "fly_to_place",
    "heli_control", "land_here", "land_at_ksc", "land_at_spot", "land", "get_delta_v", "mechjeb_ascent",
    "set_throttle", "set_gear", "set_brakes", "stage", "abort", "set_flight_plan", "remember_preference",
    "damage_report", "autopilot_status", "prop_control", "set_sas", "set_rcs", "set_lights",
})
_SMALL_MODEL = re.compile(r"(?:^|[^\d])(?:0\.\d+|1\.[0-5])\s*b\b|0\.8b|1\.2b|\btiny\b", re.I)
_CONTEXT_ERR = re.compile(r"context\s*(?:length|window|size)|maximum context|too many tokens|"
                          r"n_ctx|exceed(?:s|ed)?\s+(?:the\s+)?context|context.?overflow", re.I)
_HISTORY_ERR = re.compile(r"^(?:Chat error:|Can't reach the |.*(?:backend error|rate limit|rejected the API key|"
                          r"context window is too small))", re.I)
CONTEXT_HELP = ("This model's context window is too small for the bridge (the full prompt is ~10k tokens; 8k fails). "
                "In LM Studio load the model with at least 24k context (or set LMSTUDIO_CONTEXT=24576 and reload). "
                "Tiny models (*0.8b* / *1.2b*) automatically get a shorter lite prompt.")


def is_small_model(model):
    """True for sub-2B local instruct ids (qwen3.5-0.8b, lfm2.5-1.2b-instruct, …)."""
    return bool(_SMALL_MODEL.search(str(model or "").replace("_", "-")))


def prune_error_history(history):
    """Drop assistant turns that are bridge/backend errors so tiny models don't echo them as replies."""
    out = []
    for m in history or []:
        if m.get("role") == "assistant" and _HISTORY_ERR.search((m.get("content") or "").strip()):
            if out and out[-1].get("role") == "user":
                out.pop()  # drop the user turn that produced the error too
            continue
        out.append(m)
    return out



log = logging.getLogger("kspchat")
COMMS_DOWN = "Comms with mission control just cut out - say again when you've got me back."


def _request_timed_out(err):
    if isinstance(err, (TimeoutError, socket.timeout)):
        return True
    if isinstance(err, urllib.error.URLError):
        r = err.reason
        return isinstance(r, (TimeoutError, socket.timeout)) or "timed out" in str(r).lower()
    return False


def _comms_down_line():
    try:
        pilot = ksp_actions.pilot_name()
    except Exception:  # noqa: BLE001
        pilot = None
    if pilot:
        return f"{pilot}: {COMMS_DOWN}"
    name = settings.get("ai_name") or "Bridge"
    return f"{name}: {COMMS_DOWN}"


def _notify_comms_down(cause):
    line = _comms_down_line()
    log.warning("model request failed (%s); posting in-game: %s", cause, line)
    try:
        from . import science
        science.post_event(line)
    except Exception:  # noqa: BLE001
        log.exception("comms-down post")


def _post(url, payload, key=None, timeout=None):
    if timeout is None:
        timeout = config.LLM_CHAT_TIMEOUT_S
    """POST JSON. Free cloud tiers answer 429 when a per-minute limit is hit (Groq free = 8K tokens/min and one
    tool round here is ~10K): wait Retry-After (<= CLOUD_RETRY_MAX_WAIT s) and retry, at most twice."""
    body = json.dumps(payload).encode()
    for attempt in range(3):
        hdrs = {"Content-Type": "application/json",
                "User-Agent": "KSPChatBridge/1.0",
                "Accept": "application/json"}
        if key:
            hdrs["Authorization"] = f"Bearer {key}"
        req = urllib.request.Request(url, data=body, method="POST", headers=hdrs)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return json.loads(r.read().decode())
        except urllib.error.HTTPError as e:
            try:
                wait = float(e.headers.get("Retry-After") or 10)
            except ValueError:
                wait = 10.0
            if e.code != 429 or attempt == 2 or wait > config.CLOUD_RETRY_MAX_WAIT:
                raise
            time.sleep(max(1.0, wait))


def _strip_think(text):
    return re.sub(r"<think>.*?</think>", "", text or "", flags=re.S).strip()


# a reply that claims a change ("Holds updated: altitude 5000 m") although no tool ran must not be relayed as fact
_CLAIM = re.compile(r"\bholds? (?:updated|engaged|set|changed)\b|\bautopilot (?:engaged|updated|set)\b"
                    r"|\b(?:altitude|heading|speed|throttle|roll|bank|sas|v/?s|setting)s?\b[^.?!]{0,30}\b(?:set|updated"
                    r"|changed|engaged)\b|\b(?:i(?:'ve| have)?|now) (?:set|changed|updated|engaged|switched|enabled"
                    r"|disabled|turned)\b"
                    r"|\b(?:ejecting|deploying|staging|engaging|disengaging|activating|jettisoning|decoupling|firing"
                    r"|launching|separating|retracting|extending|initiating|executing|commencing)\b"
                    r"|\b(?:burn|maneuver|node)s? (?:initiated|started|executed|complete[d]?|planned)\b"
                    r"|\b(?:initiated|commenced|executed)\b"
                    r"|\b(?:ejected|deployed|staged|jettisoned|decoupled|activated|launched|separated)\b", re.I)
_NO_TOOL_NUDGE = ("You said you changed something, but you called no tool, so nothing happened in the game. If Luke "
                  "asked for an action, call the right tool now; otherwise answer without claiming any change.")


def claims_action(text):
    return bool(_CLAIM.search(text or ""))


# a reply that claims a STATE beyond what the tools did ('Wheels are now down and ready!' after a bare gear-down while
# the plane lay on its roof - Luke 2026-10-08): repairs never happen (no tool repairs anything); readiness / all-clear
# claims need a tool result saying so. With damage on record (watcher) more all-good words count.
_REPAIR = re.compile(r"\b(?:fixed|repaired|replaced|re-?attached|good as new|back to normal)\b", re.I)
_READY = re.compile(r"\b(?:(?:is|are|'re|'s|now|and|all)\s+ready\b|ready\s+(?:for|to)\s+(?:take\s*-?off|landing|land"
                    r"|flight|fly|launch|depart|go)\b|all\s+set\b|good\s+to\s+go\b|equipped\s+for\b"
                    r"|fully\s+(?:operational|functional|ready|intact)\b)", re.I)
_ALL_GOOD = re.compile(r"\b(?:everything(?:'s|\s+is)\s+(?:fine|ok|okay|good|normal|working)|no\s+(?:damage|problems?"
                       r"|issues?)|all\s+(?:good|systems\s+(?:go|normal|nominal))|in\s+(?:good|perfect|great)\s+"
                       r"(?:shape|condition)|intact|nominal|undamaged)\b", re.I)


def overclaim(text, tool_log=None, damaged=False):
    """The state-claim phrase in a reply that no tool result backs ('' = fine)."""
    backed = " ".join(str(t.get("result", "")) for t in tool_log or []).lower()
    for rx in (_REPAIR, _READY) + ((_ALL_GOOD,) if damaged else ()):
        m = rx.search(text or "")
        if m and m.group(0).lower() not in backed:
            return m.group(0)
    return ""


_DAMAGE_WORDS = re.compile(r"\b(?:missing|lost|lose|broke|broken|damage[d]?|fell\s+off|wheel|gear|upside|flipp?ed"
                           r"|crash(?:ed)?|roof|inverted)\b", re.I)


def _damage_state(text=""):
    """(context for the system prompt or '', damage on record). Only added when relevant (no token cost otherwise)."""
    try:
        from . import emergency
        dmg = emergency.has_damage()
        if dmg or _DAMAGE_WORDS.search(text or ""):
            return emergency.damage_context(), dmg
    except Exception:  # noqa: BLE001
        pass
    return "", False


class Session:
    """One conversation (history kept in memory, trimmed)."""

    def __init__(self, sid=speedcap.DEFAULT_SESSION):
        self.sid = str(sid)       # chat session id (pending speed-override requests are kept per session)
        self.history = []
        self.model_override = {}  # backend -> model chosen with /model
        self.last_model = ""      # model that answered the last message (shown in the mod's title)
        self.last_name = None     # speaker shown in game for the last reply (None = the AI's name)

    def reset(self):
        self.history = []

    def system_prompt(self, lite=False):
        notes = memory.notes_block()
        name = settings.get("ai_name")
        who = (f"\nYour name is {name}. If Luke renames you, call set_ai_name." if name else
               "\nIf Luke gives you a name, call set_ai_name.")
        from . import language
        base = SYSTEM_LITE if lite else SYSTEM
        extra = ("\n" + language.prompt_lines()) if not lite else ""
        return base + who + extra + ("\n\n" + notes if notes else "")

    def model_command(self, arg, backend):
        label = backends.LABELS.get(backend, backend)
        if not arg:
            try:
                models = backends.list_models(backend)
            except Exception as e:
                return f"Can't list {label} models: {e}"
            cur = self.model_override.get(backend) or "(default)"
            return f"{label} models: " + ", ".join(models[:30]) + f". Current: {cur}. Switch with /model <name>."
        if arg.lower() in ("default", "auto", "reset"):
            self.model_override.pop(backend, None)
            return f"{label} model reset to default."
        try:
            names = [m.split("  (")[0] for m in backends.list_models(backend)]
            match = next((n for n in names if n.lower() == arg.lower()), None) or \
                next((n for n in names if arg.lower() in n.lower()), None)
        except Exception:
            names, match = [], None
        if names and not match:
            return f"No {label} model matching '{arg}'. Try /model to list them."
        self.model_override[backend] = match or arg
        note = " (LM Studio will swap models on the next message; can take ~30 s)" if backend == "local" else ""
        return f"{label} model set to {match or arg}{note}."

    def resolve_override(self, ans):
        """Luke answered the pending speed / altitude override question: (reply, tool_log)."""
        if ans == "no":
            req = speedcap.deny(self.sid)
            if req and req["kind"] == "confirm":
                if req.get("tool") == "set_override":
                    return ("Override not granted - default limits kept"
                            + ("; the flight plan continues." if req.get("origin") == "flightplan" else "."), [])
                return "Cancelled - nothing done.", []
            what = (f"the {speedcap.limits()[1]:.0f} m/s cap" if not req or req["kind"] == "speed" else
                    f"the {req['limit']:.0f} m estimated safe ceiling")
            if req and req.get("origin") == "flightplan":
                return f"Override denied - keeping {what}; the flight plan flies that step within it.", []
            return f"Override denied - keeping {what}. Nothing changed.", []
        req = speedcap.grant(self.sid)
        if not req:
            return "Nothing is waiting for override authority.", []
        if req["kind"] == "confirm":  # a confirmed destructive order (cut engines / abort / eject)
            with speedcap.use_session(self.sid):
                res = ksp_actions.call_tool(req["tool"], req["args"])
            return res, [{"tool": req["tool"], "args": req["args"], "result": res[:500]}]
        if req["kind"] == "speed":
            msg = (f"Override authority granted: speed cap raised to {speedcap.limits()[1]:.0f} m/s EAS for this request "
                   f"({req['speed']:.0f} m/s at {req['alt']:.0f} m). /override speed off restores the 220 cap.")
        else:
            msg = (f"Override authority granted: safe ceiling raised to {req['value']:.0f} m (estimate was "
                   f"{req['limit']:.0f} m). /override altitude off restores it.")
        if req.get("origin") == "flightplan":
            return msg + " The flight plan continues.", []
        with speedcap.use_session(self.sid):
            res = ksp_actions.call_tool(req["tool"], req["args"])
        return msg + " " + res, [{"tool": req["tool"], "args": req["args"], "result": res[:500]}]

    def talk_to_kerbal(self, text, backend):
        """Talk to a kerbal aboard (talk.py): None = not addressed to a crew member; ('reply'|'absent', line, speaker);
        ('order', the order text) -> the pilot handles it. Never raises."""
        import logging
        log = logging.getLogger("kspchat")
        try:
            from . import crew, language, talk
            if not talk.looks_addressed(text):
                return None
            aboard, pilot = talk.crew_aboard(), ksp_actions.pilot_name()
            r = talk.route(text, aboard, pilot, talk.roster(), parse_direct)
            if r is None:
                return None
            kind, who, rest = r
            if kind == "order":
                log.info("talk: order to %s -> pilot: %s", who[0], rest)
                return ("order", rest)
            if kind == "absent":
                return ("absent", language.clean(talk.absent_line(who, aboard, pilot)), pilot or "Bridge")
            crew.note_backend(backend, self.model_override.get(backend))
            return ("reply", talk.reply(who, rest, talk.flight_facts()), talk.first(who[0]))
        except Exception:  # noqa: BLE001
            log.exception("talk")
            return None

    def send(self, text, backend="local"):
        """Send a user message, run tool calls, return (reply_text, tool_log)."""
        backend = backends.normalize(backend)
        self.last_name = None
        low = text.strip().lower()
        if low.startswith("/chatgpt"):
            self.last_name = "Bridge"
            arg = low[8:].strip()
            return (mcp_chat.set_mode(arg) if arg else
                    f"ChatGPT mode is '{mcp_chat.mode()}'. Use /chatgpt api (default, OpenAI key) or /chatgpt mcp (desktop app, heavy token use)."), []
        if low.startswith("/language"):
            self.last_name = "Bridge"
            from . import language
            return language.command(low[9:]), []
        if low.startswith("/crew"):
            self.last_name = "Bridge"
            from . import crew
            return crew.command(low[5:]), []
        if low.startswith("/reversers"):
            self.last_name = "Bridge"
            from . import reversers
            return reversers.command(low[10:]), []
        if low.startswith("/override"):
            self.last_name = "Bridge"
            return speedcap.command(text.strip()[9:]), []
        if speedcap.pending(self.sid):
            ans = speedcap.answer(text)
            if ans:  # a plain yes / no answers the pending speed-override question (any chat backend)
                self.last_name = "Bridge"
                reply, tlog = self.resolve_override(ans)
                self.history += [{"role": "user", "content": text}, {"role": "assistant", "content": reply}]
                self.history = self.history[-config.HISTORY_MESSAGES:]
                return reply, tlog
            if speedcap.pending(self.sid).get("origin") == "chat":
                speedcap.drop(self.sid)  # Luke moved on: a later unrelated "yes" must not grant it
        stopped, rest = orders.split_stop(text)
        if stopped:  # 'stop current [plan]' (+ ', and land at 27'): cancel deterministically, then the rest
            with speedcap.use_session(self.sid):
                sres = ksp_actions.call_tool("stop_current", {})
            log_stop = [{"tool": "stop_current", "args": {}, "result": sres[:500]}]
            if rest:
                reply, tools = self.send(rest, backend)
                return sres + " " + reply, log_stop + tools
            self.last_name = ksp_actions.pilot_name() or "Bridge"
            self.history += [{"role": "user", "content": text}, {"role": "assistant", "content": sres}]
            self.history = self.history[-config.HISTORY_MESSAGES:]
            return sres, log_stop
        talked = self.talk_to_kerbal(text, backend)
        if talked is not None:
            if talked[0] == "order":
                text = talked[1]  # an order to a crew member goes to the pilot (normal handling below)
            else:
                self.last_name = talked[2]
                self.history += [{"role": "user", "content": text}, {"role": "assistant", "content": talked[1]}]
                self.history = self.history[-config.HISTORY_MESSAGES:]
                return talked[1], []
        mismatch = orders.nl_craft_refusal(text)
        if mismatch:
            self.last_name = "Bridge"
            self.history += [{"role": "user", "content": text}, {"role": "assistant", "content": mismatch}]
            self.history = self.history[-config.HISTORY_MESSAGES:]
            return mismatch, []
        direct = parse_direct(text)
        if direct:  # direct flight command (pitch / throttle / gear / brakes / chutes / eject): no model needed
            self.last_name = "Bridge"
            name, args = direct
            with speedcap.use_session(self.sid):
                res = ksp_actions.call_tool(name, args)
            if res.startswith(("needs_confirm: ", "needs_override: ")):  # show Luke just the question
                res = res.split(": ", 1)[1].split(" [Relay this question")[0]
            pilot = ksp_actions.pilot_name()
            if pilot:  # voiced by the kerbal at the controls: "Sidry: Gear down, Captain."
                self.last_name = pilot
                if len(res) <= 40 and res.endswith(".") and "(" not in res and not res.startswith(("No", "Not")):
                    res = res[:-1] + ", Captain."
            self.history += [{"role": "user", "content": text}, {"role": "assistant", "content": res}]
            self.history = self.history[-config.HISTORY_MESSAGES:]
            return res, [{"tool": name, "args": args, "result": res[:500]}]
        if backend == "chatgpt" and mcp_chat.mode() == "mcp":
            self.last_name, self.last_model = "Bridge", "desktop (MCP)"
            if low.startswith("/model"):
                return "In MCP mode the ChatGPT desktop app uses whatever model its thread is set to.", []
            return mcp_chat.queue(text.strip()), []
        if low.startswith("/model"):
            return self.model_command(text.strip()[6:].strip(), backend), []
        try:
            url, key, model = backends.resolve(backend, self.model_override.get(backend))
        except backends.BackendError as e:
            self.last_name, self.last_model = "Bridge", ""
            return str(e), []

        self.last_model = model
        try:
            from . import crew
            crew.note_backend(backend, self.model_override.get(backend))  # crew intercom lines use the same AI
        except Exception:
            pass
        self.last_name = ksp_actions.pilot_name()  # the pilot speaks the AI's replies (None = the AI's name)
        self.history = prune_error_history(self.history)  # tiny models echo old error text as replies
        self.history.append({"role": "user", "content": text})
        dctx, damaged = _damage_state(text)
        lite = is_small_model(model)
        hist_n = 8 if lite else config.HISTORY_MESSAGES
        msgs = [{"role": "system", "content": self.system_prompt(lite=lite) + ("\n\n" + dctx if dctx else "")}] + \
            self.history[-hist_n:]
        # aircraft: the rocket powered-descent / suicide-burn landing tools are hidden (and refused if called anyway)
        hide = set(ksp_actions.ROCKET_LANDING_TOOLS if ksp_actions.aircraft_now() else ())
        if lite:
            hide |= {f.__name__ for f in ksp_actions.TOOLS if f.__name__ not in LITE_TOOLS}
        tools, tool_log, reply = ksp_actions.tool_schemas(hide), [], ""
        reprompted = False
        t0 = time.time()
        try:
            for _ in range(config.MAX_TOOL_ROUNDS):
                payload = {"model": model, "messages": msgs, "tools": tools, "temperature": 0.3}
                data = _post(url + "/chat/completions", payload, key, timeout=config.LLM_CHAT_TIMEOUT_S)
                m = data["choices"][0]["message"]
                calls = m.get("tool_calls") or []
                if not calls:
                    reply = _strip_think(m.get("content"))
                    if not tool_log and not reprompted and claims_action(reply):  # claimed a change, no tool: re-prompt once
                        reprompted = True
                        msgs += [{"role": "assistant", "content": reply}, {"role": "user", "content": _NO_TOOL_NUDGE}]
                        continue
                    oc = overclaim(reply, tool_log, damaged)
                    if oc and not reprompted:  # a state the tools never reported: re-prompt once with the facts
                        reprompted = True
                        did = "; ".join(f"{t['tool']}: {t['result'][:160]}" for t in tool_log) or "no tool ran"
                        msgs += [{"role": "assistant", "content": reply}, {"role": "user", "content": (
                            f"You wrote '{oc}', but the tools only did this: {did}. " + (dctx + " " if dctx else "")
                            + "Rewrite the reply saying only what the tools did and the real state - no readiness, "
                            "repair or all-clear claims the tools didn't report, and no invented damage.")}]
                        continue
                    if not reply and tool_log:  # some local models go silent after tools: ask for a summary
                        msgs.append({"role": "user", "content": "Briefly tell me the result in plain text."})
                        data = _post(url + "/chat/completions", {"model": model, "messages": msgs, "temperature": 0.3},
                                      key, timeout=config.LLM_CHAT_TIMEOUT_S)
                        reply = _strip_think(data["choices"][0]["message"].get("content"))
                    break
                msgs.append({"role": "assistant", "content": m.get("content") or "", "tool_calls": calls})
                for c in calls:
                    name = c["function"]["name"]
                    bad = False
                    try:
                        args = json.loads(c["function"].get("arguments") or "{}")
                        if not isinstance(args, dict):
                            args, bad = {}, True
                    except json.JSONDecodeError:
                        args, bad = {}, True
                    with speedcap.use_session(self.sid):  # a needs_override request is kept for this session
                        result = ksp_actions.call_tool(name, args)
                    tool_log.append({"tool": name, "args": args, "result": result[:500], **({"bad_args": True} if bad else {})})
                    msgs.append({"role": "tool", "tool_call_id": c.get("id", name), "content": result[:4000]})
            else:  # step limit: ask for a plain explanation instead of a bare stop message
                msgs.append({"role": "user", "content": (
                    "You hit the tool-step limit. Do not call tools. In 2-3 short sentences tell Luke what you "
                    "tried, what happened, and what is missing or what he could ask instead.")})
                try:
                    data = _post(url + "/chat/completions", {"model": model, "messages": msgs, "temperature": 0.3},
                                  key, timeout=config.LLM_CHAT_TIMEOUT_S)
                    reply = _strip_think(data["choices"][0]["message"].get("content"))
                except Exception:
                    reply = ""
                reply = reply or ("I couldn't finish that within my step limit. Tried: "
                                  + ", ".join(t["tool"] for t in tool_log) + ".")
        except urllib.error.HTTPError as e:
            label = backends.LABELS.get(backend, backend)
            try:
                detail = (e.read().decode(errors="replace") or "").strip()
            except Exception:
                detail = ""
            if e.code == 429:
                reply = (f"{label} rate limit hit (free tiers allow only a few requests/tokens per minute or day). "
                         "Wait a minute, or switch AI in AICS > Settings.")
            elif _CONTEXT_ERR.search(detail) or (e.code == 400 and "context" in detail.lower()):
                reply = CONTEXT_HELP
            elif e.code in (401, 403):
                if "1010" in detail or "cloudflare" in detail.lower() or "Attention Required" in detail:
                    reply = (f"{label} blocked the request ({e.code}, CDN/firewall). "
                             "Usually a missing User-Agent — update the bridge. Detail: " + detail[:80])
                else:
                    reply = (f"{label} rejected the API key ({e.code}). "
                             "Re-enter it in AICS > Settings (API key field, Save)."
                             + (f" Detail: {detail[:160]}" if detail else ""))
            else:
                reply = f"{label} backend error {e.code}: {detail[:300]}"
        except urllib.error.URLError as e:
            if _request_timed_out(e):
                _notify_comms_down(e.reason or e)
                reply = _comms_down_line()
            else:
                reply = (f"Can't reach the {backends.LABELS.get(backend, backend)} backend ({e.reason}). "
                         + {"local": "Is LM Studio's server running (lms server start)?",
                            "ollama": "Is Ollama running (ollama serve)?",
                            "custom": "Check CUSTOM_AI_URL."}.get(backend, "Check the internet connection."))
        except Exception as e:
            if _request_timed_out(e):
                _notify_comms_down(e)
                reply = _comms_down_line()
            else:
                reply = f"Chat error: {e.__class__.__name__}: {e}"
        if not tool_log and claims_action(reply):
            reply += " (no action taken)"  # still claiming a change without any tool call
        oc = overclaim(reply, tool_log, damaged)
        if oc:  # still claiming an unreported state: flag it with what the watcher actually knows
            try:
                from . import emergency
                facts = emergency.damage_facts() if emergency.running() else ""
            except Exception:  # noqa: BLE001
                facts = ""
            reply += f" (Bridge: '{oc}' is not confirmed by any tool." + (f" {facts}" if facts else "") + ")"
        if not reply and tool_log:
            reply = "Done: " + "; ".join(t["result"][:120] for t in tool_log)
        from . import language
        reply = language.clean(reply)  # slurs always; cuss words while '/language filter' is on
        self.history.append({"role": "assistant", "content": reply})
        self.history = self.history[-config.HISTORY_MESSAGES:]
        memory.log_turn({"ts": time.strftime("%Y-%m-%dT%H:%M:%S"), "backend": backend, "model": model,
                         "user": text, "tools": tool_log, "reply": reply, "secs": round(time.time() - t0, 1)})
        return reply, tool_log
