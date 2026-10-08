"""In-game chat via the ChatGPT desktop app (MCP) - no OpenAI API key.

MCP is inverted: ChatGPT is the client and calls our tools; the bridge can't call ChatGPT. So when the in-game
backend is ChatGPT in "mcp" mode (OPT-IN: heavy token use, ChatGPT polls the queue; `chatgpt_mode` in
bridge_settings.json; default "api" = OPENAI_API_KEY path):
  1. POST /chat (HTTP bridge) queues Luke's message here and answers at once with a "waiting for ChatGPT" status.
  2. An open ChatGPT thread with the ksp_bridge MCP server pulls it (game_chat_pending / game_chat_wait), acts with
     the normal KSP tools, and answers with game_chat_reply.
  3. Replies land in the outbox; the HTTP bridge drains it into the GET /events stream the mod already polls.
The HTTP bridge and the MCP server are separate processes, so state lives in one small JSON file
(config.CHAT_QUEUE_FILE) guarded by a lock file.
"""
import json
import os
import time

from . import config, settings

MODES = ("mcp", "api")
SEEN_FRESH = 600      # ChatGPT counts as "listening" if it pulled within this many seconds
KEEP_DONE = 30        # answered messages kept (for context/ids)


def mode():
    m = str(settings.get("chatgpt_mode") or "api").lower()  # default api: MCP polling burns ChatGPT tokens (opt-in)
    return m if m in MODES else "api"


def set_mode(m):
    m = (m or "").strip().lower()
    if m not in MODES:
        return f"ChatGPT mode must be one of {MODES}."
    settings.put("chatgpt_mode", m)
    return ("ChatGPT mode: MCP (ChatGPT desktop app answers the in-game chat, no API key; heavy token use - its polling burns ChatGPT tokens)." if m == "mcp" else
            "ChatGPT mode: API (OpenAI key: paste it in AICS > Settings).")


class _Lock:
    """Cross-process lock: exclusive-create a .lock file (stale after 5 s)."""
    def __init__(self):
        self.path = str(config.CHAT_QUEUE_FILE) + ".lock"

    def __enter__(self):
        end = time.time() + 5
        while True:
            try:
                os.close(os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY))
                return self
            except FileExistsError:
                try:
                    if time.time() - os.path.getmtime(self.path) > 5:
                        os.remove(self.path)
                        continue
                except OSError:
                    pass
                if time.time() > end:
                    return self  # give up waiting; a lost race beats a hung chat
                time.sleep(0.05)

    def __exit__(self, *exc):
        try:
            os.remove(self.path)
        except OSError:
            pass


def _load():
    try:
        d = json.loads(config.CHAT_QUEUE_FILE.read_text(encoding="utf-8"))
    except Exception:
        d = {}
    d.setdefault("next", 1)
    d.setdefault("inbox", [])
    d.setdefault("outbox", [])
    d.setdefault("seen", 0)
    return d


def _save(d):
    tmp = config.CHAT_QUEUE_FILE.with_suffix(".tmp")
    tmp.write_text(json.dumps(d, indent=1), encoding="utf-8")
    os.replace(tmp, config.CHAT_QUEUE_FILE)


def _open(d):
    return [m for m in d["inbox"] if m["state"] != "answered"]


def queue(text):
    """Queue an in-game message for ChatGPT. Returns the status line shown in game."""
    with _Lock():
        d = _load()
        mid = d["next"]
        d["next"] += 1
        d["inbox"].append({"id": mid, "ts": time.time(), "text": text, "state": "pending"})
        n = len(_open(d))
        listening = time.time() - d["seen"] < SEEN_FRESH
        _save(d)
    if listening:
        return f"Queued for ChatGPT desktop (#{mid}, {n} waiting) - it answers here when it checks the KSP chat."
    return (f"Waiting for ChatGPT desktop (MCP)... queued #{mid}. No API key needed: in the ChatGPT app open a "
            "thread with the ksp_bridge tools and say \"check KSP chat\" (or \"keep answering KSP chat\"). "
            "Replies show up here.")


def pending(mark=True):
    """Open (unanswered) messages; marks them seen-by-ChatGPT and refreshes the 'listening' timestamp."""
    with _Lock():
        d = _load()
        d["seen"] = time.time()
        msgs = _open(d)
        if mark:
            for m in msgs:
                m["state"] = "seen"
        _save(d)
    return msgs


def has_new(after_id=0):
    d = _load()
    return any(m["id"] > after_id for m in _open(d))


def reply(text, reply_to=0):
    """Post ChatGPT's answer to the game window; closes message reply_to (0 = all open ones)."""
    text = (text or "").strip()
    if not text:
        return "Empty reply - nothing sent."
    with _Lock():
        d = _load()
        d["seen"] = time.time()
        closed = []
        for m in d["inbox"]:
            if m["state"] != "answered" and (not reply_to or m["id"] == reply_to):
                m["state"] = "answered"
                closed.append(m)
        done = [m for m in d["inbox"] if m["state"] == "answered"]
        d["inbox"] = [m for m in d["inbox"] if m["state"] != "answered"] + done[-KEEP_DONE:]
        d["inbox"].sort(key=lambda m: m["id"])
        d["outbox"] = (d["outbox"] + [{"ts": time.time(), "text": text}])[-50:]
        _save(d)
    try:
        from . import memory
        memory.log_turn({"ts": time.strftime("%Y-%m-%dT%H:%M:%S"), "backend": "chatgpt-mcp", "model": "ChatGPT desktop",
                         "user": " | ".join(m["text"] for m in closed), "tools": [], "reply": text, "secs": 0})
    except Exception:
        pass
    left = len(_open(d))
    return f"Shown in the game chat. {left} message(s) still open." if left else "Shown in the game chat."


def drain():
    """Outbox replies for the HTTP bridge to push into /events (cheap no-op when there is nothing)."""
    if not config.CHAT_QUEUE_FILE.exists() or not _load()["outbox"]:
        return []
    with _Lock():
        d = _load()
        out = d["outbox"]
        if out:
            d["outbox"] = []
            _save(d)
    return [o["text"] for o in out]


def clear():
    """Drop open messages (in-game Clear button)."""
    with _Lock():
        d = _load()
        d["inbox"] = [m for m in d["inbox"] if m["state"] == "answered"]
        _save(d)


def status():
    d = _load()
    age = time.time() - d["seen"] if d["seen"] else None
    return {"mode": mode(), "open": len(_open(d)),
            "chatgpt_seen_s": round(age) if age is not None else None}