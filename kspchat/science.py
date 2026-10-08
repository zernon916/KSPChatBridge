"""Science watcher: notices new science situations and runs/reminds about experiments.

A background thread (started by `run_bridge.py serve`) polls the active vessel every few seconds
with its OWN kRPC connection. When the science situation changes - KSP situation (landed, splashed,
flying low/high, space low/high), biome, or body (SOI change) - it acts according to the mode:

  auto    run + transmit every available experiment that holds no data (skips completed subjects)
  remind  post a reminder to the in-game chat AND auto-run rerunnable experiments (default)
  off     do nothing

Mode lives in bridge_settings.json so the chat tool, the MCP server and the watcher share it.
Messages are pushed to the in-game window via http_server's event queue (GET /events).
"""
import logging
import threading
import time

from . import config, settings

log = logging.getLogger("kspchat")
MODES = ("auto", "remind", "off")
MIN_EC_PCT = 10.0        # don't run experiments below this electric charge
MIN_EC_TRANSMIT_PCT = 25.0
POLL_S = 3.0
MIN_POST_GAP_S = 20.0  # rate-limit reminders that ran nothing
_events = []             # (id, text), consumed by GET /events
_ev_lock = threading.Lock()
_ev_id = 0


def post_event(text):
    global _ev_id
    with _ev_lock:
        _ev_id += 1
        _events.append((_ev_id, text))
        del _events[:-50]
    log.info("event %s", text)


MOD_CMD = "!cmd "       # event lines for the AICS mod itself (run on KSP's main thread), e.g. "!cmd eject <unix t> <name>"
_cmd_seen = [0.0]        # last GET /events?cmd=1 (a mod that understands commands)


def post_command(cmd):
    """Queue a command for the AICS mod (delivered with the /events poll, every ~3 s)."""
    post_event(MOD_CMD + cmd)


def mark_cmd_client():
    _cmd_seen[0] = time.time()


def cmd_client_seen(within=20.0):
    """True if a mod that understands /events commands polled in the last `within` seconds."""
    return time.time() - _cmd_seen[0] < within


def events_since(since):
    with _ev_lock:
        if since > _ev_id:  # bridge restarted; client has stale ids
            since = 0
        return [(i, t) for i, t in _events if i > since]


def get_mode():
    return settings.get("science_mode", "remind")


def set_mode(mode):
    mode = str(mode).lower().strip()
    mode = {"on": "auto", "true": "auto", "auto on": "auto", "auto off": "remind", "false": "remind"}.get(mode, mode)
    if mode not in MODES:
        return f"Unknown mode '{mode}'. Use auto, remind or off."
    settings.put("science_mode", mode)
    return f"Science watcher mode: {mode}."


def science_situation(v):
    """KSP's science situation for the vessel."""
    sit = str(v.situation).split(".")[-1]
    if sit in ("landed", "pre_launch"):
        return "landed"
    if sit == "splashed":
        return "splashed"
    body = v.orbit.body
    alt = v.flight(body.reference_frame).mean_altitude
    if body.has_atmosphere and alt < body.atmosphere_depth:
        return "flying_low" if alt < body.flying_high_altitude_threshold else "flying_high"
    return "space_low" if alt < body.space_high_altitude_threshold else "space_high"


def _ec_pct(v):
    mx = v.resources.max("ElectricCharge")
    return 100.0 * v.resources.amount("ElectricCharge") / mx if mx > 0 else 0.0


def _subject_done(e):
    try:
        s = e.science_subject
        return bool(s and s.is_complete)
    except Exception:
        return False


def run_experiments(v, only_rerunnable=False, transmit=True):
    """Run eligible experiments; returns (ran, skipped_reason_or_None, transmitted)."""
    if _ec_pct(v) < MIN_EC_PCT:
        return [], f"electric charge below {MIN_EC_PCT:.0f}%", []
    ran = []
    for e in v.parts.experiments:
        try:
            if e.inoperable or e.has_data or not e.available or _subject_done(e):
                continue
            if only_rerunnable and not e.rerunnable:
                continue
            e.run()
            ran.append(e.part.title)
        except Exception:
            pass
    sent = []
    if transmit and ran and _ec_pct(v) >= MIN_EC_TRANSMIT_PCT:
        time.sleep(2.0)
        for e in v.parts.experiments:
            try:
                if e.has_data and (not only_rerunnable or e.rerunnable):
                    e.transmit()
                    sent.append(e.part.title)
            except Exception:
                pass
    return ran, None, sent


class Watcher(threading.Thread):
    def __init__(self):
        super().__init__(daemon=True, name="science-watcher")
        self.conn = None
        self.last = None
        self.last_post = 0.0

    def _connect(self):
        import krpc
        self.conn = krpc.connect(name="KSPChatBridge-science", address=config.KRPC_ADDRESS,
                                 rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)

    def run(self):
        while True:
            try:
                self.tick()
                time.sleep(POLL_S)
            except Exception as ex:  # KSP closed, scene change, etc.
                self.conn, self.last = None, None
                log.debug("science watcher: %s", ex)
                time.sleep(10)

    def tick(self):
        mode = get_mode()
        if mode == "off":
            self.last = None
            return
        if self.conn is None:
            self._connect()
        if str(self.conn.krpc.current_game_scene).split(".")[-1] != "flight":
            self.last = None
            return
        v = self.conn.space_center.active_vessel
        try:
            biome = v.biome
        except Exception:
            biome = "?"
        key = (v.id if hasattr(v, "id") else v.name, v.orbit.body.name, science_situation(v), biome)
        if key == self.last:
            return
        first, self.last = self.last is None or self.last[0] != key[0], key
        if first:  # just loaded / switched vessel: remember the situation, don't spam
            return
        where = f"{key[2].replace('_', ' ')} over {key[1]}" + (f" ({biome})" if biome and biome != "?" else "")
        if mode == "auto":
            ran, why, sent = run_experiments(v)
            msg = (f"New science situation: {where}. " + (f"Skipped: {why}." if why else
                   f"Ran {len(ran)}: {', '.join(ran) or 'none available'}. Transmitted {len(sent)}."))
        else:
            ran, why, sent = run_experiments(v, only_rerunnable=True)
            left = [e.part.title for e in v.parts.experiments
                    if not e.rerunnable and not e.has_data and not e.inoperable and e.available and not _subject_done(e)]
            msg = f"New science situation: {where}."
            if ran:
                msg += f" Auto-ran rerunnable: {', '.join(ran)} (transmitted {len(sent)})."
            if why:
                msg += f" Not running experiments: {why}."
            if left:
                msg += f" One-shot experiments available: {', '.join(left)} - say 'run science' to use them."
            if not (ran or why or left):
                return  # nothing to do here: stay quiet (orbits cross many biomes)
        if mode == "auto" and not ran and not why:
            return
        if time.time() - self.last_post < MIN_POST_GAP_S and not ran:
            return
        self.last_post = time.time()
        post_event(msg)


_watcher = None


def start():
    global _watcher
    if _watcher is None:
        _watcher = Watcher()
        _watcher.start()
