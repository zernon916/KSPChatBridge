"""Tiny local HTTP endpoint used by the in-game mod.

POST /chat   {"message": "...", "model": "local|ollama|chatgpt|gemini|custom|claude|grokbot", "session": "default"}
             -> {"reply": "...", "tools": [...]}   (add ?format=text for a plain-text reply)
POST /reset  {"session": "default"}
GET  /health -> {"ok": true, "scene": ..., "busy": <chats in progress>, "backends_ready": "chatgpt,gemini"}
             (backends_ready = keyed APIs with a key/URL configured; names only, never secrets)
GET  /events?since=N -> science-watcher notices; ?format=text gives "id<TAB>text" lines
GET  /systems?format=text -> 'master<TAB>warning|caution|<TAB>seq<TAB>alerts', 'sys<TAB>ok|caution|fail<TAB>name<TAB>detail' lines
GET  /status?format=text  -> live autopilot status "key<TAB>value" lines (STATUS window, AICS indicators)
GET  /landing?format=text -> one-line live landing distance/ETA estimate ("" when no landing is happening)
GET  /spots?format=text   -> landing spots, "name<TAB>mode<TAB>lat<TAB>lon<TAB>builtin<TAB>runways" lines
POST /tool  {"name": ..., "args": {...}} -> run one landing-menu tool directly (no model), plain-text result
POST /setting {"key": "chatgpt_mode", "value": "mcp|api"} -> change a whitelisted runtime setting (AICS Settings)
GET  /setting?key=altitude_band_m -> {"key": ..., "value": ...} (whitelisted bridge_settings keys)
POST /setting {"key": "altitude_band_m", "value": 150} -> same (altitude band 10-500 m)
GET  /api_keys            -> "backend<TAB>1|0<TAB>••••last4" lines + custom_url/custom_model (never full keys)
POST /api_key {"backend": "gemini", "key": "...", "clear": false, "url": .., "model": ..} -> save key to .env + apply now
POST /shutdown            -> stop the bridge (sent by the mod when KSP quits)
AICS Flight Plan (flightplan.py), all plain text:
GET  /flightplan?format=text&since=R  -> "R<TAB>live status line", then (if a newer plan was pushed by the chat
                                         than rev R) the plan text on the following lines
GET  /flightplan/template?kind=circle|cruise|circuit|orbit -> template plan text
GET  /taxi                -> taxi waypoints, "name<TAB>lat<TAB>lon" lines
POST /flightplan/draft {"model": .., "session": .., "request": "..", "from_chat": false} -> AI-drafted plan text
     (normalized to the step format; never runs tools)
POST /flightplan/check {"plan": ".."} -> parse result;  /flightplan/fly {"plan": ".."} -> start the runner
POST /flightplan/stop     -> stop the runner after the current step
ChatGPT in MCP mode: /chat queues the message (mcp_chat.py); ChatGPT desktop's replies arrive via /events.
"""
import json
import logging
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import backends, config, flightplan, keys, ksp_actions, mcp_chat, science, settings, spots
from .chat import Session

log = logging.getLogger("kspchat")
_sessions = {}
_chat_lock = threading.Lock()  # one chat at a time (kRPC + GPU are shared)
_busy = [0]                    # chats in progress; /health reports it so restarts can wait


def chat_in_progress():
    """True while a /chat or flight-plan draft holds the shared model lock (crew skips LLM when busy)."""
    return _busy[0] > 0


def clear_vessel_sessions():
    """Active vessel switched: drop chat history (model must not keep the old craft's context)."""
    for sess in _sessions.values():
        sess.reset()
_srv = [None]
MENU_TOOLS = {"land_at_spot", "dock_with", "save_landing_spot", "get_landing_eta", "list_landing_spots", "abort",
              "delete_landing_spot", "land_plane", "run_science", "set_science_watcher", "get_status",
              "plane_hold", "plane_pitch", "set_throttle", "set_gear", "set_brakes", "deploy_parachutes", "autopilot_status", "sun_lock", "antenna_lock", "reset_experiments", "fly_to",
              "mechjeb_ascent", "circularize", "change_apoapsis", "change_periapsis", "change_inclination",
              "match_target_plane", "apsis_longitude", "launch_to_target_plane", "station_keep", "capture_plan",
              "landing_check", "taxi_to", "sync_orbit_altitude", "get_delta_v", "transfer_to",
              "get_trim_state", "set_trim", "auto_trim_now", "save_craft_notes", "trim_panel_open"}  # + AICS menu / Trim UI


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, body, text=False, model="", name=None):
        raw = (body if text else json.dumps(body)).encode("utf-8")
        self.send_response(code)
        if model:
            self.send_header("X-AI-Model", model.encode("ascii", "replace").decode())
        name = name or settings.get("ai_name") or "AI"
        self.send_header("X-AI-Name", str(name).encode("ascii", "replace").decode())
        self.send_header("Content-Type", "text/plain; charset=utf-8" if text else "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def log_message(self, fmt, *args):
        if not any(p in (fmt % args) for p in ("/events", "/landing", "/status", "/systems", "/telemetry")):  # polled every few seconds
            log.info("http %s", fmt % args)

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(min(n, 100_000)).decode("utf-8", errors="replace") if n else ""
        try:
            return json.loads(raw) if raw else {}
        except json.JSONDecodeError:
            return {"message": raw}

    def do_GET(self):
        if self.path.startswith("/health"):
            cg = mcp_chat.status()
            return self._send(200, {"ok": True, "busy": _busy[0], "ai_name": settings.get("ai_name") or "AI",
                                    "chatgpt_mode": cg["mode"], "chatgpt_open": cg["open"],
                                 "language_filter": _on("language_filter"), "crew_chatter": _on("crew_chatter"),
                                    "chatgpt_seen_s": cg["chatgpt_seen_s"],
                                    "backends_ready": ",".join(backends.configured())})
        if self.path.startswith("/events"):
            import urllib.parse
            for t in mcp_chat.drain():  # ChatGPT desktop (MCP) replies -> in-game chat
                science.post_event(CHATGPT_TAG + _chatgpt_name() + ": " + t)
            q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            evs = science.events_since(int((q.get("since") or ["0"])[0]))
            if "cmd=1" in self.path:  # this mod runs "!cmd ..." lines (eject); older mods never see them
                science.mark_cmd_client()
            else:
                evs = [(i, t) for i, t in evs if not t.startswith(science.MOD_CMD)]
            if "chat=1" not in self.path:  # older mod: show "Bridge: ChatGPT: ..." (no tag)
                evs = [(i, t[len(CHATGPT_TAG):] if t.startswith(CHATGPT_TAG) else t) for i, t in evs]
            if "format=text" in self.path:
                return self._send(200, "".join(f"{i}\t{t}\n" for i, t in evs), text=True)
            return self._send(200, {"events": [{"id": i, "text": t} for i, t in evs]})
        if self.path.startswith("/systems"):  # Systems dashboard: master caution/warning + system lights (~1 s)
            from . import emergency
            if "format=text" in self.path:
                return self._send(200, emergency.systems_text(), text=True)
            lvl, seq, text = emergency.master()
            return self._send(200, {"master": lvl, "seq": seq, "text": text,
                                    "systems": [list(r) for r in emergency.SYSTEMS]})
        if self.path.startswith("/status"):  # STATUS window + AICS indicators (polled ~1 s)
            from . import status
            rows = status.snapshot()
            if "format=text" in self.path:
                return self._send(200, "".join(f"{k}\t{v}\n" for k, v in rows), text=True)
            return self._send(200, dict(rows))
        if self.path.startswith("/landing"):
            r = spots.landing_eta(quiet=True)
            if "format=text" in self.path:
                return self._send(200, r.get("summary", "") if r.get("active") else "", text=True)
            return self._send(200, r)
        if self.path.startswith("/spots"):
            rows = []
            for k, s in spots.all_spots().items():
                lat, lon = spots.spot_point(s)
                st = spots.make_strip(s) if s["mode"] == "H" else None
                rows.append(f"{k}\t{s['mode']}\t{lat:.4f}\t{lon:.4f}\t{int(s.get('builtin', False))}\t"
                            + (s.get("runway") or ("/".join(st["names"]) if st else "")))
            return self._send(200, "\n".join(rows) + "\n", text=True)
        if self.path.startswith("/flightplan"):
            import urllib.parse
            q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            if self.path.startswith("/flightplan/template"):
                return self._send(200, flightplan.template((q.get("kind") or ["circle"])[0]), text=True)
            try:
                since = int((q.get("since") or ["0"])[0])
            except ValueError:
                since = 0
            rev, text = flightplan.pushed_since(since)
            return self._send(200, f"{rev}\t{flightplan.status_line()}\n{text}", text=True)
        if self.path.startswith("/taxi"):  # AICS Taxi panel: "name<TAB>lat<TAB>lon" lines
            from . import taxi
            return self._send(200, "".join(f"{k}\t{a:.6f}\t{b:.6f}\n" for k, (a, b) in taxi.points().items()), text=True)
        if self.path.startswith("/setting"):
            import urllib.parse
            key = (urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query).get("key") or [""])[0]
            if key not in settings.SETTING_KEYS:
                return self._send(400, {"error": f"setting '{key}' not allowed"})
            return self._send(200, {"key": key, "value": settings.get_setting(key)})
        if self.path.startswith("/api_keys"):
            return self._send(200, keys.status_text(), text=True)
        self._send(404, {"error": "not found"})

    def do_POST(self):
        b = self._body()
        if self.path in ("/telemetry/rotors", "/telemetry"):
            from . import telemetry
            try:
                if "application/json" not in (self.headers.get("Content-Type") or ""):
                    return self._send(415, {"error": "JSON required"})
                telemetry.accept(b)
                return self._send(200, {"ok": True})
            except ValueError as ex:
                return self._send(400, {"error": str(ex)})
        if not isinstance(b, dict):
            return self._send(400, {"error": "JSON object required"})
        if self.path == "/native/prepare-off":
            from . import guard, hold, heli, plane, lander, taxi, docking
            with ksp_actions._lock:
                if _busy[0] or guard.busy() or any(m.active() for m in (hold, heli, plane, lander, taxi, docking)):
                    return self._send(409, {"error": "Stop the current controller/chat before disabling the bridge"})
                ksp_actions.NATIVE_HANDOFF = True
            return self._send(200, {"ok": True})
        if self.path == "/native/cancel-off":
            with ksp_actions._lock:
                ksp_actions.NATIVE_HANDOFF = False
            return self._send(200, {"ok": True})
        sid = str(b.get("session") or "default")
        if self.path.startswith("/shutdown"):
            self._send(200, {"ok": True})
            log.info("shutdown requested (KSP quitting)")
            threading.Thread(target=_shutdown, daemon=True).start()
            return
        if self.path.startswith("/tool"):
            name = str(b.get("name") or "")
            if name not in MENU_TOOLS:
                return self._send(400, f"tool '{name}' not allowed from the menu", text=True)
            args = b.get("args") or {}
            log.info("menu tool %s %s", name, args)
            if name == "delete_landing_spot":
                return self._send(200, spots.delete(str(args.get("name", ""))), text=True)
            return self._send(200, ksp_actions.call_tool(name, args), text=True)
        if self.path.startswith("/flightplan/"):
            return self._flightplan(b, sid)
        if self.path.startswith("/api_key"):  # in-game key entry (AICS > Settings); body is never logged
            if "application/json" not in (self.headers.get("Content-Type") or ""):  # no browser "simple" cross-site POST
                return self._send(415, "send JSON (Content-Type: application/json)", text=True)
            r = keys.set_key(str(b.get("backend") or ""), b.get("key"), b.get("url"), b.get("model"), bool(b.get("clear")))
            return self._send(200, r, text=True)
        if self.path.startswith("/setting"):
            key, val = str(b.get("key") or ""), b.get("value")
            if key in ("language_filter", "crew_chatter"):  # AICS Settings toggles (on/off)
                from . import crew, language
                log.info("setting %s=%s", key, val)
                return self._send(200, (language if key == "language_filter" else crew).command(val), text=True)
            if key in settings.SETTING_KEYS:
                try:
                    out = settings.put_setting(key, val)
                except ValueError as e:
                    return self._send(400, str(e), text=True)
                log.info("setting %s=%s", key, out)
                return self._send(200, {"key": key, "value": out})
            if key != "chatgpt_mode":
                return self._send(400, f"setting '{key}' not allowed", text=True)
            log.info("setting %s=%s", key, val)
            return self._send(200, mcp_chat.set_mode(val), text=True)
        if self.path.startswith("/reset"):
            _sessions.pop(sid, None)
            mcp_chat.clear()
            return self._send(200, {"ok": True})
        if not self.path.startswith("/chat"):
            return self._send(404, {"error": "not found"})
        msg = str(b.get("message") or "").strip()
        if not msg:
            return self._send(400, {"error": "empty message"})
        model = str(b.get("model") or "local")
        log.info("chat [%s] %s", model, msg[:200])
        _busy[0] += 1
        sess = None
        try:
            with _chat_lock:
                sess = _sessions.get(sid) or _sessions.setdefault(sid, Session(sid))
                reply, tools = sess.send(msg, model)
        finally:
            _busy[0] = max(0, _busy[0] - 1)
        log.info("reply %s | tools %s", reply[:200], [t["tool"] for t in tools])
        try:  # a flight plan the chat AI wrote as text (not via set_flight_plan) still lands in AICS > Flight Plan
            if not any(t["tool"] == "set_flight_plan" for t in tools) and flightplan.maybe_from_chat(msg, reply):
                log.info("flight plan from chat pushed to the AICS editor")
        except Exception:  # noqa: BLE001
            log.exception("flightplan from chat")
        if "format=text" in self.path:
            return self._send(200, reply, text=True, model=sess.last_model, name=sess.last_name)
        self._send(200, {"reply": reply, "tools": tools, "ai_name": sess.last_name or settings.get("ai_name") or "AI",
                         "model": sess.last_model}, model=sess.last_model, name=sess.last_name)

    def _flightplan(self, b, sid):
        """AICS > Flight Plan buttons. Plain-text replies; 4xx = shown to Luke as an error line."""
        cmd = self.path.split("?")[0].rstrip("/").rsplit("/", 1)[-1]
        if cmd == "fly":
            return self._send(200, flightplan.start(str(b.get("plan") or "")), text=True)
        if cmd == "check":
            return self._send(200, flightplan.check(str(b.get("plan") or "")), text=True)
        if cmd == "stop":
            return self._send(200, flightplan.stop(), text=True)
        if cmd != "draft":
            return self._send(404, f"unknown flight plan command '{cmd}'", text=True)
        model = str(b.get("model") or "local")
        sess = _sessions.get(sid)
        chat_reply = ""
        request = str(b.get("request") or "")
        if b.get("from_chat"):
            hist = sess.history if sess else []
            chat_reply = next((m["content"] for m in reversed(hist) if m["role"] == "assistant" and m.get("content")), "")
            last_user = next((m["content"] for m in reversed(hist) if m["role"] == "user"), "")
            if not chat_reply and not last_user:
                return self._send(400, "Nothing in the chat yet: ask the AI for a flight plan there first, or use AI fill.",
                                  text=True)
            request = request or last_user
        log.info("flight plan draft [%s] request=%r from_chat=%s", model, request[:120], bool(b.get("from_chat")))
        _busy[0] += 1
        try:
            with _chat_lock:  # one model call at a time (LM Studio = one GPU)
                text = flightplan.draft(model, (sess.model_override.get(backends.normalize(model)) if sess else None),
                                        request, chat_reply)
        finally:
            _busy[0] = max(0, _busy[0] - 1)
        return self._send(200, text, text=True)


def _on(key):
    """Bridge-side on/off settings for AICS (default on)."""
    v = settings.get(key)
    return True if v is None else bool(v)


CHATGPT_TAG = "@"  # /events?chat=1: the mod shows tagged lines as chat replies (no "Bridge:" prefix)


def _chatgpt_name():
    n = settings.get("ai_name")
    return f"{n} (ChatGPT)" if n else "ChatGPT"


def _shutdown():
    import os
    import time
    for _ in range(50):  # let an in-flight chat reply finish (max ~5 s)
        if _busy[0] == 0:
            break
        time.sleep(0.1)
    if _srv[0]:
        _srv[0].shutdown()
    time.sleep(0.3)
    os._exit(0)


class _Server(ThreadingHTTPServer):
    # Windows SO_REUSEADDR lets a SECOND bridge bind the same port (both listen, requests split, two science
    # watchers). Exclusive bind there, so a duplicate fails fast and exits.
    allow_reuse_address = os.name != "nt"


def serve():
    try:
        srv = _Server((config.HTTP_HOST, config.HTTP_PORT), Handler)
    except OSError as e:  # another bridge already owns the port (e.g. the mod's autostart raced a manual restart)
        log.warning("port %d busy (%s): another bridge is already running, exiting", config.HTTP_PORT, e)
        print(f"KSPChatBridge: port {config.HTTP_PORT} already in use - another bridge is running. Exiting.")
        return
    science.start()  # only after the port is ours (a duplicate must not start a 2nd watcher)
    try:
        from . import emergency
        emergency.start()  # flameout / reverse thrust / sabotage / blackout ... watcher (own kRPC connection)
    except Exception as e:  # noqa: BLE001
        log.warning("emergency watcher not started: %s", e)
    _srv[0] = srv
    log.info("KSPChatBridge listening on http://%s:%d", config.HTTP_HOST, config.HTTP_PORT)
    print(f"KSPChatBridge listening on http://{config.HTTP_HOST}:{config.HTTP_PORT}  (Ctrl+C to stop)")
    srv.serve_forever()
