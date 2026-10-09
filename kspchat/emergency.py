"""Emergency detection and parts / control-config awareness, with the pilot reacting in character (Luke 2026-10-08).

A daemon thread (started with the HTTP server, its OWN kRPC connection, like the science watcher) samples the active
vessel once a second in flight and watches for:
  flameout     an engine that was producing thrust drops to ~0 with the throttle up (cause: intake air starvation /
               out of fuel when visible) -> wings level 30 s, relight attempt (engine off/on), intakes opened,
               throttle kept on the remaining engines
  reverse      thrust reversers / a 'reverse' engine mode in FLIGHT (thrust pointing out of the nose; normal on the
               ground and in the landing rollout) -> no throttle added (autopilots held at idle), reversers switched
               back to forward if possible, else idle until forward again
  parts        part count drops without staging -> wings level 30 s
  overheat     a part at >= 95 % of its max (skin) temperature (clear < 85 %)
  fuel         liquid fuel < 5 % in flight (clear > 8 %)
  stall        stock: angle of attack > 20 deg while sinking (FAR: stall_fraction > 0.5) for 1.5 s -> wings level; nothing
               engaged -> SAS on and full power (unless reversed)
  blackout     crew aboard + control lost + recent G > 4 (G-LOC) -> protect's blackout mode (wings level, G eased)
  config       sabotage / misconfiguration: control surfaces losing pitch/yaw/roll duty (e.g. elevators -> flaps),
               inversion toggled, deploy direction flipped, authority cut, reaction wheels switched off, engines shut
               down or mode-changed, intakes closed, parachutes deployed, gear wrong for the phase, lights/SAS/RCS /
               resource flow locks flipped (own tool changes ignored for 5 s; landing gear/flaps/brakes/reversers left
               alone on approach and rollout) -> call it out, fumble 2-4 s, revert to the pre-takeoff snapshot, fly
               safe (bank limited to 15 deg) and probe the control response
  probe        on the first engage of an autopilot per vessel and after a control-config change: a small pitch / roll
               input (0.1 for 0.4 s, added to the autopilot's output) vs the measured attitude change; an inverted axis
               is adapted (shape() flips that axis' sign for the autopilots), a dead axis -> fly safe.
Each event posts ONE short, panicked chat line in the pilot's voice plus what was done ('Sidry: MAYDAY MAYDAY! Engine 2
flamed out! [wings level 30 s; relight tried on Engine 2]') and one line when it is resolved. Active alerts feed the
STATUS window (GET /status 'alert') and the Systems dashboard (GET /systems: master caution/warning + system lights).

Detector.tick(sample) / diff_config / eval_probe are pure logic (tested offline); the thread only samples and acts.
"""
import logging
import math
import random
import threading
import time
from collections import deque

log = logging.getLogger("kspchat.emergency")

AUTO = True                 # tests switch the kRPC thread off
POLL_S = 1.0
SCAN_S = 3.0                # control-config + temperature scan interval
FLAMEOUT_S = 3.0            # thrust ~0 this long (jets spool slowly - no false alarm on throttle-up)
THRUST_DEAD = 0.03          # fraction of the engine's recent peak thrust
FUEL_LOW, FUEL_OK = 0.05, 0.08
TEMP_ALERT, TEMP_OK = 0.95, 0.85
STALL_AOA, STALL_OK_AOA, STALL_S, STALL_OK_S = 20.0, 12.0, 1.5, 2.0
LEVEL_S, LEVEL_BANK, SAFE_BANK = 30.0, 10.0, 15.0
ONE_SHOT_S = 20.0           # one-shot alerts (parts lost, config change) stay on the dashboard this long
OWN_GRACE_S = 5.0
PROBE_DELTA, PROBE_T, PROBE_MIN_AGL = 0.1, 0.4, 500.0
PROBE_SETTLE_S, PROBE_MAX_BANK = 20.0, 10.0   # only probe a settled autopilot in near-level flight
FLIGHT_SITS = ("flying", "sub_orbital", "orbiting", "escaping")
GROUND_SITS = ("landed", "splashed", "pre_launch")
WARNING_KINDS = ("flameout", "reverse", "config", "blackout", "overheat", "parts", "stall", "upside_down", "prop_out",
                 "heli_rpm", "heli_tail", "rotor_brake", "rotor_torque")
# upside down (Luke 2026-10-08: the plane flipped on landing): roll beyond INV_ROLL (only while the nose is within
# INV_MAX_PITCH of the horizon - roll means nothing pointing straight up / down) or, at rest, the craft's top
# pointing down (UP_DOWN = vertical component of its 'up' vector)
INV_ROLL, INV_OK_ROLL, INV_MAX_PITCH, INV_S = 120.0, 60.0, 60.0, 2.0
UP_DOWN, UP_OK = -0.5, 0.3
# prop out (one engine out on a prop plane): a group's RPM below PROP_DEAD x its reference (recent peak, capped by its
# own RPM limit so Luke turning the limit down isn't a failure) or its motor disengaged, for PROP_OUT_S, while flying
PROP_DEAD, PROP_OK, PROP_OUT_S, PROP_MIN_RPM = 0.3, 0.7, 2.0, 30.0
PROP_YAW_TRIM = 0.15        # rudder bias toward the live side while a side's prop is out (autopilots only)
# helicopters (heli.py): main rotor RPM loss, spin / tail loss, vortex ring state
HELI_RPM_DEAD, HELI_RPM_OK, HELI_RPM_S = 0.6, 0.85, 1.5
SPIN_RATE, SPIN_OK, SPIN_S = 45.0, 10.0, 3.0      # deg/s heading rate
VRS_VS, VRS_HSPD, VRS_OK_VS, VRS_OK_HSPD, VRS_MIN_AGL, VRS_S = -6.0, 8.0, -3.0, 12.0, 5.0, 1.0

LINES = {
    "flameout": ["MAYDAY MAYDAY! {what} flamed out! We're losing thrust!",
                 "{what} just DIED on me! Come on, come on, light up!",
                 "MAYDAY! {what} went quiet! That's... that's bad!",
                 "{what} flameout! I did NOT sign up to fly a glider!"],
    "flameout_ok": ["{what} is back! Oh, thank the Kraken.", "{what} relit! Phew - nobody saw me panic, right?"],
    "reverse": ["Why are we going BACKWARDS?!", "WHO TOUCHED THE REVERSERS?!", "Reverse thrust in FLIGHT?! Who did that?!",
                "The engines are pushing BACKWARDS! Hands off the reversers!"],
    "reverse_ok": ["Forward thrust again. Nobody touch ANYTHING.", "Reversers stowed. My heart can't take this."],
    "parts": ["Something just fell off! {what} gone! Was that important?!", "MAYDAY! We lost {what}! That's NOT normal!",
              "Bits are coming off! {what}! Mommy!"],
    "overheat": ["{what} is cooking at {pct}%! It's getting toasty in here!",
                 "Heat warning! {what} at {pct}% of max! I can smell it melting!"],
    "overheat_ok": ["Temperatures coming down. Okay. Okay. Breathing.", "Things cooled off. Phew."],
    "fuel": ["Fuel's almost gone - {pct}% left! Find me a runway, ANY runway!",
             "Bingo fuel! {pct}% left! We're about to be a very expensive glider!"],
    "fuel_ok": ["Fuel's okay again. Crisis averted."],
    "stall": ["STALL! STALL! She's not flying anymore!", "We're stalling! Nose down, nose DOWN!", "The wings quit! STALL!"],
    "stall_ok": ["Flying again. That was WAY too exciting.", "Stall recovered. Can we never do that again?"],
    "blackout": ["G's... too much... seeing... stars...", "Ugh... everything's going... grey..."],
    "blackout_ok": ["Wha-?! I'm awake! I'm awake! What did I miss?", "Ow. Who's flying? Oh - me. Back on it!"],
    "pitch_lost": ["The elevators just turned into FLAPS?! Who's messing with my plane!"],
    "roll_lost": ["The ailerons stopped answering?! Who's messing with my plane!"],
    "yaw_lost": ["The rudder quit on me?! Who's messing with my plane!"],
    "inverted": ["The controls are BACKWARDS! Who's messing with my plane!"],
    "deploy_dir": ["Who flipped the deploy direction?! The surfaces are going the WRONG way!"],
    "deploy": ["Who deployed a control surface in cruise?! Put that back!"],
    "authority": ["Somebody turned my control authority down! Who's messing with my plane!"],
    "wheels": ["Who switched off the reaction wheels?!"],
    "engine_off": ["Who shut down an engine?! I need those!"],
    "mode": ["The engine mode just changed by itself?! Who's messing with my plane!"],
    "intake": ["Who closed the intakes?! The engines need to BREATHE!"],
    "chute": ["A chute just deployed?! We're dragging like a brick - cutting it!"],
    "gear": ["Who touched the landing gear?! Put it back where it belongs!"],
    "brakes": ["Who hit the brakes in flight?!"],
    "lights": ["Who's flipping the lights?! Hands off!"],
    "sas": ["Who touched SAS?!"],
    "rcs": ["Who touched RCS?!"],
    "flow": ["Someone locked a fuel line! Unlock it!"],
    "ag": ["An action group just flipped by itself?!"],
    "config": ["Somebody's messing with my plane!"],
    "config_ok": ["Controls back to normal. Phew. Whoever that was - stop it."],
    "probe_inverted": ["The {what} is working BACKWARDS! I'm flipping my inputs to match!"],
    "probe_dead": ["The {what} isn't responding at all! Flying gentle!"],
    "upside_down": ["MAYDAY! We're UPSIDE DOWN! Which way is up?!", "The sky is on the WRONG SIDE! MAYDAY!",
                    "MAYDAY MAYDAY! We're flying on our ROOF!"],
    "upside_down_ok": ["Right side up again! Nobody tell the flight school.", "Upright. Phew. Sky's back on top."],
    "upside_down_ground": ["We're upside down! Did we land? Is this landing?",
                           "Uh... the ground is on the wrong side. Is that a landing? That counts as a landing.",
                           "Everything's upside down! Can someone come flip us? Asking for a friend."],
    "upside_down_ground_ok": ["Right way up again. Let's pretend that never happened."],
    "heli_rpm": ["MAYDAY! Rotor RPM is dropping! We're coming down!", "MAYDAY MAYDAY! The main rotor's dying!",
                 "The big fan is slowing down! That's the one that keeps us UP!"],
    "heli_rpm_ok": ["Main rotor is back up to speed. Breathing again.", "Rotor RPM good. I'm never complaining about noise again."],
    "heli_tail": ["MAYDAY! We're SPINNING! I can't stop the yaw!", "Everything is going round and round! MAYDAY!"],
    "heli_tail_ok": ["Spinning stopped. The horizon is staying put.", "Yaw's back under control. Dizzy, but alive."],
    "heli_vrs": ["We're sinking in our own downwash! Vortex ring! Need forward speed!",
                 "Collective up and we're still FALLING? Vortex ring state!"],
    "heli_vrs_ok": ["Out of the vortex ring. Clean air again."],
    "rotor_brake": ["WHO PUT THE ROTOR BRAKE ON?! The {what} is stopping!",
                    "Rotor brake ON in flight?! Somebody's sabotaging the {what}!"],
    "rotor_brake_ok": ["Rotor brake off on the {what}. Spinning free again."],
    "rotor_torque": ["The {what} torque just went to ZERO! Who did that?!", "Zero torque on the {what}?! Put it BACK!"],
    "rotor_torque_ok": ["{what} torque is back. Phew."],
    "prop_out": ["MAYDAY! Lost the {what}! She's yawing!", "MAYDAY MAYDAY! The {what} quit! Hold her straight!",
                 "The {what} just stopped! Rudder, rudder, RUDDER!"],
    "prop_out_ok": ["The {what} is turning again! Phew.", "{what} back up to speed. My hands are still shaking."],
}
_rng = random.Random()

_lock = threading.Lock()
ALERTS = {}                 # key -> {"kind", "short", "level", "seq", "until" (one-shot) or None}
SYSTEMS = []                # [(level ok|caution|fail, name, detail)] for the dashboard
SIGN = {"pitch": 1.0, "roll": 1.0, "yaw": 1.0}       # physical sign of each axis (probe-adapted)
PROBE = {"axis": None, "delta": 0.0, "until": 0.0}
_flags = {"reverse": False, "level_until": 0.0, "safe": False, "own_t": -1e9, "seq": 0, "upside": False,
          "yaw_trim": 0.0}
DAMAGE = {"vid": None, "vessel": "", "lost": [], "inverted": ""}   # parts actually lost this flight + attitude
_watcher = None


# ---------------------------------------------------------------- hooks used by the autopilots / tools
def running():
    return _watcher is not None and _watcher.is_alive()


def shape(axis, val):
    """Final control value for the autopilots: throttle idle while reversed in flight; axis sign adapted from the
    probe; the probe's small test input added while it runs."""
    if axis == "throttle":
        return 0.0 if _flags["reverse"] else val
    if axis == "pitch" and _flags["upside"]:
        return 0.0  # inverted: elevator neutral while the roll comes upright - no split-S, no altitude for speed
    if axis == "yaw" and _flags["yaw_trim"]:
        val = max(-1.0, min(1.0, val + _flags["yaw_trim"]))  # a prop out: rudder toward the live side
    out = SIGN.get(axis, 1.0) * val
    if PROBE["axis"] == axis and time.time() < PROBE["until"]:
        out = max(-1.0, min(1.0, out + PROBE["delta"]))
    return out


def reverse_active():
    return _flags["reverse"]


def bank_cap():
    """Bank limit (deg) while an emergency asks for wings level / gentle flying, else None."""
    if time.time() < _flags["level_until"]:
        return LEVEL_BANK
    return SAFE_BANK if _flags["safe"] else None


def own_change():
    """Our own tools change engines / modes / intakes: don't call it sabotage for a few seconds."""
    _flags["own_t"] = time.time()


def reset():
    with _lock:
        ALERTS.clear()
        SYSTEMS[:] = []
    SIGN.update(pitch=1.0, roll=1.0, yaw=1.0)
    PROBE.update(axis=None, delta=0.0, until=0.0)
    _flags.update(reverse=False, level_until=0.0, safe=False, own_t=-1e9, upside=False, yaw_trim=0.0)
    DAMAGE.update(vid=None, vessel="", lost=[], inverted="")
    DET.reset()


# ---------------------------------------------------------------- alerts / dashboard
def _level(kind):
    return "warning" if kind in WARNING_KINDS else "caution"


def _alert(key, kind, short, one_shot=False):
    with _lock:
        _flags["seq"] += 1
        ALERTS[key] = {"kind": kind, "short": short, "level": _level(kind), "seq": _flags["seq"],
                       "until": time.time() + (60.0 if kind == "config" else ONE_SHOT_S) if one_shot else None}


def _clear_alert(key):
    with _lock:
        ALERTS.pop(key, None)


def _live_alerts():
    now = time.time()
    with _lock:
        for k in [k for k, a in ALERTS.items() if a["until"] is not None and a["until"] < now]:
            ALERTS.pop(k)
        return sorted(ALERTS.values(), key=lambda a: a["seq"])


def alerts_text():
    """Active alerts for the STATUS window ('' = none)."""
    return " | ".join(a["short"] for a in _live_alerts())


def master():
    """(level 'warning'|'caution'|'', newest alert seq, text) for the master caution / warning lights."""
    al = _live_alerts()
    if not al:
        return "", 0, ""
    lvl = "warning" if any(a["level"] == "warning" for a in al) else "caution"
    return lvl, max(a["seq"] for a in al), " | ".join(a["short"] for a in al)


def systems_text():
    """GET /systems?format=text: 'master<TAB>level<TAB>seq<TAB>text' then 'sys<TAB>ok|caution|fail<TAB>name<TAB>detail'."""
    lvl, seq, text = master()
    with _lock:
        rows = list(SYSTEMS)
    clean = lambda x: str(x).replace("\t", " ").replace("\n", " ")  # noqa: E731
    lines = [f"master\t{lvl}\t{seq}\t{clean(text)}"]
    lines += [f"sys\t{a}\t{clean(b)}\t{clean(c)}" for a, b, c in rows]
    lines.append(f"lights\t{'1' if _lights_wanted() else '0'}")
    return "\n".join(lines) + "\n"


def _lights_wanted():
    try:
        from . import settings
        return bool(settings.get("mayday_lights", True))
    except Exception:  # noqa: BLE001
        return True


# ---------------------------------------------------------------- pure logic
def _pick(kind, **kw):
    lines = LINES.get(kind) or LINES["config"]
    s = _rng.choice(lines)
    try:
        return s.format(**kw)
    except (KeyError, IndexError, ValueError):
        return s


def _names(names, n=2):
    return ", ".join(names[:n]) + (f" +{len(names) - n}" if len(names) > n else "")


def _surf(s):
    """Pad a surface scan tuple to 8 fields: title, P, Y, R, inverted, authority, deployed, deploy_dir."""
    t = list(s) + [False] * 8
    return t[:8]


def diff_config(old, new, own=False, landing=False, want_gear=None):
    """Changes between two config scans -> [(bad, text, tag)]. Lists line up by index (callers re-baseline when the
    part count changes). Own tool changes (own=True) to engines / intakes / toggles are ignored. During landing
    (approach / rollout) flaps (deploy), brakes and reversers are flown by the autopilot and never count as
    sabotage; gear is checked against want_gear (True=down, False=up, None=don't enforce) instead."""
    out = []
    so, sn = old.get("surfaces") or [], new.get("surfaces") or []
    if len(so) == len(sn):
        lost = {"pitch": [], "yaw": [], "roll": []}
        for a0, b0 in zip(so, sn):
            a, b = _surf(a0), _surf(b0)
            for j, ax in ((1, "pitch"), (2, "yaw"), (3, "roll")):
                if a[j] and not b[j]:
                    lost[ax].append(b[0])
            if bool(a[4]) != bool(b[4]):
                out.append((True, f"{b[0]}: {'INVERTED' if b[4] else 'inversion removed'}", "inverted"))
            if bool(a[7]) != bool(b[7]):
                out.append((True, f"{b[0]}: deploy direction {'INVERTED' if b[7] else 'normal'}", "deploy_dir"))
            if b[5] < a[5] - 5 or (a[5] > 0) != (b[5] > 0):
                out.append((True, f"{b[0]}: authority {a[5]:.0f} -> {b[5]:.0f}%",
                            "inverted" if (a[5] > 0) != (b[5] > 0) else "authority"))
            if not landing and not own and bool(a[6]) != bool(b[6]):
                out.append((True, f"{b[0]}: {'deployed' if b[6] else 'retracted'}", "deploy"))
        for ax in ("pitch", "roll", "yaw"):
            if lost[ax]:
                n = len(lost[ax])
                out.append((True, f"{n} surface{'s' if n != 1 else ''} lost {ax} ({_names(lost[ax])})", ax + "_lost"))
    wo, wn = old.get("wheels") or [], new.get("wheels") or []
    if len(wo) == len(wn):
        off = [b[0] for a, b in zip(wo, wn) if a[1] and not b[1]]
        if off:
            out.append((True, f"reaction wheel{'s' if len(off) != 1 else ''} OFF ({_names(off)})", "wheels"))
    if not own:
        eo, en = old.get("engines") or [], new.get("engines") or []
        if len(eo) == len(en):
            for i, (a, b) in enumerate(zip(eo, en), 1):
                if a[1] and not b[1]:
                    out.append((True, f"Engine {i} ({b[0]}) shut down", "engine_off"))
                if (a[2] or "") != (b[2] or ""):
                    out.append(("revers" in (b[2] or "").lower(), f"Engine {i} mode {a[2]} -> {b[2]}", "mode"))
        io, inn = old.get("intakes") or [], new.get("intakes") or []
        if len(io) == len(inn):
            closed = [b[0] for a, b in zip(io, inn) if a[1] and not b[1]]
            if closed:
                out.append((True, f"intake{'s' if len(closed) != 1 else ''} closed ({_names(closed)})", "intake"))
        co, cn = old.get("chutes") or [], new.get("chutes") or []
        if len(co) == len(cn):
            opened = [b[0] for a, b in zip(co, cn) if not a[1] and b[1]]
            if opened:
                out.append((True, f"parachute{'s' if len(opened) != 1 else ''} deployed ({_names(opened)})", "chute"))
        if (want_gear is not None and old.get("gear") is not None and new.get("gear") is not None
                and bool(old["gear"]) != bool(new["gear"]) and bool(new["gear"]) != bool(want_gear)):
            out.append((True, f"gear {'down' if new.get('gear') else 'up'} (want {'down' if want_gear else 'up'})", "gear"))
        if not landing:
            if old.get("brakes") is not None and new.get("brakes") is not None and bool(old["brakes"]) != bool(new["brakes"]):
                out.append((True, f"brakes {'on' if new['brakes'] else 'off'}", "brakes"))
            for key, tag, on, off in (("lights", "lights", "on", "off"), ("sas", "sas", "on", "off"),
                                      ("rcs", "rcs", "on", "off")):
                if old.get(key) is not None and new.get(key) is not None and bool(old[key]) != bool(new[key]):
                    out.append((True, f"{key} {on if new[key] else off}", tag))
            fo, fn = old.get("flow") or [], new.get("flow") or []
            if len(fo) == len(fn):
                locked = [b[0] for a, b in zip(fo, fn) if a[1] and not b[1]]
                if locked:
                    out.append((True, f"resource flow locked ({_names(locked)})", "flow"))
            ao, an = old.get("ag") or [], new.get("ag") or []
            if len(ao) == len(an):
                flipped = [f"AG{i}" for i, (a, b) in enumerate(zip(ao, an)) if bool(a) != bool(b)]
                if flipped:
                    out.append((True, f"action group{'s' if len(flipped) != 1 else ''} flipped ({_names(flipped)})", "ag"))
    return out


def degraded(ref, cur, landing=False, want_gear=None):
    """True while the control config is worse than the reference (axes lost, inversion / authority / deploy-dir
    changed, wheels off, gear wrong) - drives the 'fly safe' mode and the 'back to normal' line."""
    skip = ("engine_off", "mode", "intake", "chute", "brakes", "lights", "sas", "rcs", "flow", "ag")
    return any(bad and tag not in skip
               for bad, _, tag in diff_config(ref, cur, own=True, landing=landing, want_gear=want_gear))


def eval_probe(delta, before, during, wrap=False):
    """before / during: [(t, angle deg)]. The test input's effect = the angle change beyond the extrapolated
    pre-probe trend. -> 'normal' | 'inverted' | 'dead' | 'unclear'."""
    if len(before) < 2 or len(during) < 1:
        return "unclear"

    def d(a, b):
        x = a - b
        return (x + 180.0) % 360.0 - 180.0 if wrap else x
    (t0, a0), (t1, a1) = before[0], before[-1]
    rate = d(a1, a0) / max(t1 - t0, 1e-3)
    te, ae = during[-1]
    excess = d(ae, a1) - rate * (te - t1)
    if abs(excess) < 0.1:
        return "dead"
    if abs(excess) < 0.4:
        return "unclear"
    return "normal" if excess * delta > 0 else "inverted"


class Detector:
    """Pure emergency logic: tick(sample) -> events [{"kind", "key", "start", "quiet", "what", "pct", "short",
    "actions", "changes", "engines", "one_shot"}]. One event when something starts, one when it is resolved."""

    def __init__(self):
        self.reset()

    def reset(self):
        self.vid = None
        self.active = {}
        self.eng = {}
        self.parts = None
        self.stage = None
        self.stage_t = -1e9
        self.parts_said = -1e9
        self.stall_since = self.stall_ok_since = None
        self.inv_since = self.upr_since = None
        self.prop = {}
        self.heli = {"peak": 0.0, "low": None, "hdg": None, "ht": None, "spin": None, "tail_n0": None, "vrs": None}
        self.crew = None
        self.g_hist = deque(maxlen=12)
        self.spd_hist = deque(maxlen=6)
        self.ref_cfg = self.last_cfg = None

    def _ev(self, out, kind, key, start, one_shot=False, quiet=False, **kw):
        ev = {"kind": kind, "key": key, "start": start, "quiet": quiet, "one_shot": one_shot, "what": "", "pct": None,
              "short": "", "actions": [], "changes": [], "engines": []}
        ev.update(kw)
        out.append(ev)
        if start and not one_shot:
            self.active[key] = kind
        elif not start:
            self.active.pop(key, None)

    def tick(self, s):
        out = []
        t = float(s.get("t", time.time()))
        if s.get("vid") != self.vid:  # new / switched vessel: baseline only, no alerts
            self.reset()
            self.vid = s.get("vid")
            self.parts, self.stage, self.crew = s.get("parts"), s.get("stage"), s.get("crew")
            if s.get("cfg") is not None:
                self.ref_cfg = self.last_cfg = s["cfg"]
            return out
        sit = str(s.get("sit") or "")
        in_flight = sit in FLIGHT_SITS
        flying = sit == "flying" and not s.get("rollout")
        thr = float(s.get("throttle") or 0.0)
        engines = s.get("engines") or []
        multi = len(engines) > 1

        # --- staging / part loss
        st = s.get("stage")
        if st is not None and st != self.stage:
            self.stage, self.stage_t = st, t
        p = s.get("parts")
        crew = s.get("crew")
        left = max(0, int(self.crew) - int(crew)) if crew is not None and self.crew is not None else 0
        if crew is not None:
            self.crew = crew
        if p is not None and self.parts is not None and p != self.parts:
            n = self.parts - p
            if n > 0 and in_flight and t - self.stage_t > 3.0 and t - self.parts_said >= 5.0 and left < n:
                # (a kerbal leaving an external command seat drops the part count too - that's not a lost part)
                lost = [x for x in (s.get("lost") or []) if "kerbal" not in str(x).lower()]
                what = _names(lost, 3) if lost else f"{n} part{'s' if n != 1 else ''}"
                self.parts_said = t
                self._ev(out, "parts", f"parts:{t:.0f}", True, one_shot=True, what=what, lost=lost,
                         short=f"PARTS LOST: {what}"[:60] if lost else f"PARTS LOST ({n})",
                         actions=["level"] if flying else [])
            self.ref_cfg = self.last_cfg = None  # different part list: re-baseline the config
            if "config_bad" in self.active:
                self._ev(out, "config", "config_bad", False, quiet=True)
        if p is not None:
            self.parts = p

        # --- engines: flameout
        seen = set()
        air = s.get("intake_air")
        for i, e in enumerate(engines, 1):
            seen.add(i)
            name = f"Engine {i}" if multi else "The engine"
            stt = self.eng.setdefault(i, {"peak": 0.0, "low": None})
            key = f"flameout:{i}"
            thrust = float(e.get("thrust") or 0.0)
            if not e.get("active"):
                stt.update(peak=0.0, low=None)
                self.active.pop(key, None)
                continue
            stt["peak"] = max(thrust, stt["peak"] * 0.995)
            dead = flying and thr >= 0.2 and stt["peak"] > 1000.0 and thrust < THRUST_DEAD * stt["peak"] \
                and not (e.get("reversed") or e.get("rev_mode"))
            if dead:
                stt["low"] = t if stt["low"] is None else stt["low"]
                if key not in self.active and t - stt["low"] >= FLAMEOUT_S:
                    starved = bool(e.get("air")) and air is not None and air < 0.05
                    cause = "intake air starvation" if starved else ("out of fuel" if e.get("has_fuel") is False else "")
                    acts = ["level", f"relight:{i}", "keep_throttle"] + (["intakes"] if starved else [])
                    self._ev(out, "flameout", key, True, what=name, short=f"{name.upper()} FLAMEOUT" +
                             (f" ({cause})" if cause else ""), actions=acts, cause=cause, engines=[i])
            else:
                stt["low"] = None
                if key in self.active and (thrust >= 0.2 * stt["peak"] or not flying):
                    self._ev(out, "flameout", key, False, quiet=not flying, what=name)
        for i in [k for k in self.eng if k not in seen]:
            self.eng.pop(i)
            self.active.pop(f"flameout:{i}", None)

        # --- reverse thrust (only an emergency in flight). A 'reverse' engine mode counts at once; thrust pointing out of
        # the nose (geometry) only with real thrust AND the craft decelerating (a sideways control point can't fool it)
        spd_now = s.get("speed")
        if spd_now is not None:
            self.spd_hist.append((t, float(spd_now)))
        old = [x for tt, x in self.spd_hist if t - tt >= 1.5]
        decel = bool(old) and spd_now is not None and float(spd_now) < old[-1] - 0.5
        rev = [i for i, e in enumerate(engines, 1) if e.get("active") and (e.get("rev_mode") or e.get("rev_module") or (
            e.get("reversed") and float(e.get("thrust") or 0) > 0.05 * float(e.get("max_thrust") or 1e12) and decel))]
        if flying and rev and not _own_reversal():
            if "reverse" not in self.active:
                what = ", ".join(f"Engine {i}" for i in rev) if multi else "the engine"
                self._ev(out, "reverse", "reverse", True, what=what, short="REVERSE THRUST IN FLIGHT",
                         actions=["no_throttle", "forward"], engines=rev)
        elif "reverse" in self.active:
            self._ev(out, "reverse", "reverse", False, quiet=not flying)

        # --- fuel
        fuel = s.get("fuel")
        if fuel is not None:
            pct = round(100 * fuel)
            if flying and fuel < FUEL_LOW and "fuel" not in self.active and any(e.get("active") for e in engines):
                self._ev(out, "fuel", "fuel", True, pct=pct, short=f"FUEL LOW ({pct}%)", actions=["fuel_tip"])
            elif "fuel" in self.active and (fuel > FUEL_OK or not in_flight):
                self._ev(out, "fuel", "fuel", False, quiet=not in_flight)

        # --- overheating
        temp = s.get("temp")
        if temp is not None:
            r, title = temp
            if in_flight and r >= TEMP_ALERT and "overheat" not in self.active:
                pct = round(100 * r)
                self._ev(out, "overheat", "overheat", True, what=title, pct=pct, short=f"OVERHEAT {title} {pct}%",
                         actions=["heat_tip"])
            elif "overheat" in self.active and (r < TEMP_OK or not in_flight):
                self._ev(out, "overheat", "overheat", False, quiet=not in_flight)

        # --- stall
        aoa, vs, spd = s.get("aoa"), s.get("vs"), s.get("speed")
        stalled = flying and aoa is not None and (spd or 0) > 5.0 and \
            ((s.get("stall") or 0.0) > 0.5 or (aoa > STALL_AOA and (vs or 0.0) < -1.0))
        if stalled:
            self.stall_ok_since = None
            self.stall_since = t if self.stall_since is None else self.stall_since
            if "stall" not in self.active and t - self.stall_since >= STALL_S:
                self._ev(out, "stall", "stall", True, short="STALL", actions=["level", "stall"])
        else:
            self.stall_since = None
            if "stall" in self.active:
                if not flying:
                    self._ev(out, "stall", "stall", False, quiet=True)
                elif aoa is not None and aoa < STALL_OK_AOA:
                    self.stall_ok_since = t if self.stall_ok_since is None else self.stall_ok_since
                    if t - self.stall_ok_since >= STALL_OK_S:
                        self._ev(out, "stall", "stall", False)
                else:
                    self.stall_ok_since = None

        # --- G-LOC blackout
        g = float(s.get("g") or 0.0)
        self.g_hist.append((t, g))
        recent = max((x for tt, x in self.g_hist if t - tt <= 5.0), default=g)
        crew, ctrl = int(s.get("crew") or 0), s.get("ctrl")
        if "blackout" not in self.active and in_flight and crew > 0 and ctrl is False and recent > 4.0:
            self._ev(out, "blackout", "blackout", True, short="PILOT BLACKED OUT", actions=["blackout"])
        elif "blackout" in self.active and (ctrl is True or not in_flight):
            self._ev(out, "blackout", "blackout", False, actions=["wake"])

        # --- prop out (per side group): MAYDAY 'lost the left prop', rudder toward the live side
        props = s.get("props") or {}
        hs = s.get("heli")
        if hs and not hs.get("compound"):
            props = {}  # single / coaxial helis: the rotor watch below covers it
        elif hs:
            props = {g: d for g, d in props.items() if g in ("left", "right")}
        for gname, d in props.items():
            st = self.prop.setdefault(gname, {"peak": 0.0, "low": None})
            key = f"prop_out:{gname}"
            rpm = d.get("rpm")
            if rpm is None:
                continue
            st["peak"] = max(float(rpm), st["peak"] * 0.995)
            ref = min(st["peak"], d["rpm_limit"]) if d.get("rpm_limit") else st["peak"]
            dead = flying and (thr >= 0.2 or hs) and ref >= PROP_MIN_RPM and (
                rpm < PROP_DEAD * ref or d.get("motor_on") is False or (d.get("brake") or 0) > 0)
            if dead:
                st["low"] = t if st["low"] is None else st["low"]
                if key not in self.active and t - st["low"] >= PROP_OUT_S:
                    what = f"{gname} prop" if gname != "center" else "center prop"
                    cause = _rotor_cause(d)
                    what += f" ({cause})" if cause else ""
                    live = [g for g in props if g != gname and g in ("left", "right")]
                    acts = ["heli_side_out"] if hs else (["level", f"prop_yaw:{gname}"] if live else ["level"])
                    self._ev(out, "prop_out", key, True, what=what, short=f"{what.upper()} OUT", actions=acts,
                             group=gname)
            else:
                st["low"] = None
                if key in self.active and (rpm >= PROP_OK * ref or not flying):
                    self._ev(out, "prop_out", key, False, quiet=not flying, what=f"{gname} prop",
                             actions=[f"prop_yaw_off:{gname}"])

        if hs:
            self._heli(out, s, hs, t, flying)

        # --- rotor Brake / Torque Limit tamper in flight (Luke: Brake must be 0, torque > 0 for a rotor to spin)
        for c in s.get("rotors") or []:
            kb, kt = f"rotor_brake:{c['i']}", f"rotor_torque:{c['i']}"
            braked = (c.get("brake") or 0) > 0
            if flying and braked and kb not in self.active:
                self._ev(out, "rotor_brake", kb, True, what=c["label"], short=f"ROTOR BRAKE {c['brake']:.0f}: {c['label']}"[:60],
                         actions=[f"rotor_release:{c['i']}"])
            elif kb in self.active and (not braked or not flying):
                self._ev(out, "rotor_brake", kb, False, quiet=not flying, what=c["label"])
            zero = c.get("torque") is not None and c["torque"] < 1.0
            if flying and zero and kt not in self.active and not s.get("own"):
                self._ev(out, "rotor_torque", kt, True, what=c["label"], short=f"ZERO TORQUE: {c['label']}"[:60],
                         actions=[f"rotor_torque:{c['i']}"])
            elif kt in self.active and (not zero or not flying):
                self._ev(out, "rotor_torque", kt, False, quiet=not flying, what=c["label"])

        # --- upside down: in flight (planes) -> MAYDAY + roll upright; at rest after a landing / crash -> funny panic
        roll, pit, up = s.get("roll"), s.get("pitch"), s.get("up")
        level_nose = pit is None or abs(pit) < INV_MAX_PITCH
        on_ground = sit in GROUND_SITS
        if on_ground and up is not None:
            inv, upr = up < UP_DOWN, up > UP_OK
        elif roll is not None:
            inv, upr = abs(roll) > INV_ROLL and level_nose, abs(roll) < INV_OK_ROLL
        else:
            inv = upr = False
        if flying and not (s.get("plane") or hs):
            inv = False
        if inv and (flying or on_ground):
            self.upr_since = None
            self.inv_since = t if self.inv_since is None else self.inv_since
            if t - self.inv_since >= INV_S:
                if flying and "inverted_air" not in self.active:
                    self._ev(out, "upside_down", "inverted_air", True, short="INVERTED",
                             actions=["heli_upright"] if hs else ["upright"])
                if on_ground and "inverted_ground" not in self.active:
                    if "inverted_air" in self.active:
                        self._ev(out, "upside_down", "inverted_air", False, quiet=True)
                    self._ev(out, "upside_down_ground", "inverted_ground", True, short="INVERTED (on the ground)")
        else:
            self.inv_since = None
            for key, kind in (("inverted_air", "upside_down"), ("inverted_ground", "upside_down_ground")):
                if key not in self.active:
                    continue
                if upr:
                    self.upr_since = t if self.upr_since is None else self.upr_since
                    if t - self.upr_since >= 1.0:
                        self._ev(out, kind, key, False)
                elif key == "inverted_air" and not flying:
                    self._ev(out, kind, key, False, quiet=True)
                elif key == "inverted_ground" and not on_ground:
                    self._ev(out, kind, key, False, quiet=True)

        # --- control config (sabotage / misconfiguration vs the pre-takeoff snapshot)
        cfg = s.get("cfg")
        if cfg is not None:
            landing = bool(s.get("landing"))
            want_gear = s.get("want_gear")  # True=down, False=up, None=don't enforce (takeoff manages gear)
            if self.ref_cfg is None:
                self.ref_cfg = self.last_cfg = cfg
            else:
                ch = [c for c in diff_config(self.last_cfg, cfg, own=bool(s.get("own")),
                                             landing=landing, want_gear=want_gear) if c[0]]
                self.last_cfg = cfg
                bad = degraded(self.ref_cfg, cfg, landing=landing, want_gear=want_gear)
                if ch and in_flight:
                    tags = [tag for _, _, tag in ch]
                    axis = any(tag not in ("engine_off", "mode", "intake", "chute", "gear", "brakes",
                                           "lights", "sas", "rcs", "flow", "ag") for tag in tags)
                    acts = (["safe", "probe"] if axis else ["level"])
                    if flying and "engine_off" in tags:
                        acts.append("relight_off")
                    if "chute" in tags:
                        acts.append("cut_chutes")
                    # everything else: fumble 2-4 s then restore the pre-takeoff snapshot (once per change)
                    if any(tag != "chute" for tag in tags):
                        acts.append("revert")
                    self._ev(out, "config", f"config:{t:.0f}", True, one_shot=True, changes=[c[1] for c in ch],
                             tag=ch[0][2], short="CONFIG: " + "; ".join(c[1] for c in ch)[:80],
                             actions=acts, tags=tags, ref=self.ref_cfg)
                if bad and "config_bad" not in self.active and in_flight:
                    self.active["config_bad"] = "config"
                elif not bad and "config_bad" in self.active:
                    self._ev(out, "config_ok", "config_bad", False)
                if not in_flight:  # on the runway: refresh the pre-takeoff snapshot
                    self.ref_cfg = cfg
        return out


    def _heli(self, out, s, h, t, flying):
        """Helicopter watch: main rotor RPM loss, spin / tail rotor loss, vortex ring state."""
        st = self.heli
        rpm, lim = h.get("rpm"), h.get("rpm_limit")
        if rpm is not None:
            st["peak"] = max(float(rpm), st["peak"] * 0.995)
            ref = min(st["peak"], lim) if lim else st["peak"]
            dead = flying and ref >= PROP_MIN_RPM and (rpm < HELI_RPM_DEAD * ref or h.get("motor_on") is False
                                                       or (h.get("brake") or 0) > 0)
            if dead:
                st["low"] = t if st["low"] is None else st["low"]
                if "heli_rpm" not in self.active and t - st["low"] >= HELI_RPM_S:
                    locked = bool(h.get("locked"))
                    cause = _rotor_cause(h)
                    self._ev(out, "heli_rpm", "heli_rpm", True, what="main rotor" + (f" ({cause})" if cause else ""),
                             short="MAIN ROTOR RPM LOW" + (f" - {cause.upper()}" if cause else ""),
                             actions=["heli_glide"] if locked or h.get("compound") else ["autorotate"],
                             locked=locked, compound=bool(h.get("compound")))
            else:
                st["low"] = None
                if "heli_rpm" in self.active and (rpm >= HELI_RPM_OK * ref or not flying):
                    self._ev(out, "heli_rpm", "heli_rpm", False, quiet=not flying, what="main rotor")
        # spin (heading rate) or a lost tail rotor
        hdg = h.get("hdg")
        rate = None
        if hdg is not None and st["hdg"] is not None and t > st["ht"]:
            rate = ((hdg - st["hdg"] + 180.0) % 360.0 - 180.0) / (t - st["ht"])
        if hdg is not None:
            st["hdg"], st["ht"] = hdg, t
        if st["tail_n0"] is None:
            st["tail_n0"] = h.get("tail_n") or 0
        tail_lost = (h.get("tail_n") or 0) < st["tail_n0"]
        spinning = rate is not None and abs(rate) > SPIN_RATE
        st["spin"] = (t if st["spin"] is None else st["spin"]) if spinning else None
        if flying and "heli_tail" not in self.active and (tail_lost or (st["spin"] is not None and t - st["spin"] >= SPIN_S)):
            what = "tail rotor" if tail_lost or st["tail_n0"] else "yaw control"
            self._ev(out, "heli_tail", "heli_tail", True, what=what, short="SPIN / " + what.upper(),
                     actions=["heli_spin"], compound=bool(h.get("compound")))
        elif "heli_tail" in self.active and (not flying or (not tail_lost and rate is not None and abs(rate) < SPIN_OK)):
            self._ev(out, "heli_tail", "heli_tail", False, quiet=not flying)
        # vortex ring state: fast descent at low airspeed, clear of the ground
        vs, spd, agl = float(s.get("vs") or 0.0), float(s.get("speed") or 0.0), h.get("agl")
        hspd = math.sqrt(max(0.0, spd * spd - vs * vs))
        vrs = flying and vs < VRS_VS and hspd < VRS_HSPD and (agl is None or agl > VRS_MIN_AGL)
        st["vrs"] = (t if st["vrs"] is None else st["vrs"]) if vrs else None
        if vrs and "heli_vrs" not in self.active and t - st["vrs"] >= VRS_S:
            self._ev(out, "heli_vrs", "heli_vrs", True, short="VORTEX RING", actions=["vrs_exit"])
        elif "heli_vrs" in self.active and (not flying or vs > VRS_OK_VS or hspd > VRS_OK_HSPD):
            self._ev(out, "heli_vrs", "heli_vrs", False, quiet=not flying)


def _rotor_cause(d):
    """Why a rotor isn't turning, from its fields ('' = unknown)."""
    if (d.get("brake") or 0) > 0:
        return f"Brake {d['brake']:.0f}"
    if d.get("torque") is not None and d["torque"] < 1.0:
        return "Torque Limit 0"
    if d.get("motor_on") is False:
        return "Motor disengaged"
    return ""


DET = Detector()


# ---------------------------------------------------------------- acting on events (kRPC, in the watcher thread)
def _engaged():
    from . import heli, hold, plane
    return bool(hold.active() or plane.active() or heli.active())


def _act(code, ev, v, s):
    """Run one safety action; returns the report text ('' = nothing to say)."""
    from . import protect
    eng = _engaged()
    if code == "level":
        _flags["level_until"] = time.time() + LEVEL_S
        return f"autopilot: wings level {LEVEL_S:.0f} s" if eng else "nothing engaged - keep the wings level"
    if code.startswith("prop_yaw:"):  # left prop out -> the right side pulls the nose left -> rudder right (+yaw)
        side = code.split(":")[1]
        _flags["yaw_trim"] = PROP_YAW_TRIM if side == "left" else (-PROP_YAW_TRIM if side == "right" else 0.0)
        return (f"autopilot: rudder trim {'right' if side == 'left' else 'left'} to hold her straight"
                if eng else f"nothing engaged - {'right' if side == 'left' else 'left'} rudder!")
    if code.startswith("prop_yaw_off:"):
        _flags["yaw_trim"] = 0.0
        return ""
    if code.startswith("rotor_release:") or code.startswith("rotor_torque:"):
        from . import propulsion
        i = int(code.split(":")[1])
        if v is None:
            return ""
        if code.startswith("rotor_release:"):  # a brake in flight is never ours: release it (like the reversers)
            n = propulsion.set_brake(v, 0.0, rotors={i})
            return "Brake released (0)" if n else "couldn't release the Brake - set it to 0 by hand!"
        tq = propulsion.flight_floor("torque", 100.0 * float(s.get("throttle") or 0.0))
        if s.get("heli"):
            tq = propulsion.TORQUE_MAX
        r = propulsion.set_rotor(v, torque=tq, rotors={i}, flying=True)
        return f"Torque Limit restored: {r}"
    if code.startswith("heli_") or code in ("autorotate", "vrs_exit"):
        return _heli_act(code, ev)
    if code == "upright":
        _flags["upside"] = True
        _flags["level_until"] = time.time() + LEVEL_S
        return ("autopilot: rolling upright the shortest way, elevator neutral (no pull-through - not trading altitude "
                "for speed)") if eng else "nothing engaged - roll upright the short way, DON'T pull!"
    if code == "safe":
        _flags["safe"] = True
        return f"flying safe: bank limited to {SAFE_BANK:.0f} deg" if eng else "nothing engaged - fly gently"
    if code.startswith("relight:"):
        i = int(code.split(":")[1])
        try:
            e = list(v.parts.engines)[i - 1]
            e.active = False
            time.sleep(0.3)
            e.active = True
            return f"relight tried on {ev['what']}"
        except Exception as ex:  # noqa: BLE001
            log.debug("relight: %s", ex)
            return ""
    if code == "intakes":
        n = 0
        try:
            for it in v.parts.intakes:
                if not it.open:
                    it.open = True
                    n += 1
        except Exception:  # noqa: BLE001
            pass
        return f"opened {n} intake{'s' if n != 1 else ''}" if n else "intakes already open - descend into thicker air to relight"
    if code == "keep_throttle":
        n = sum(1 for e in (s.get("engines") or []) if e.get("active") and float(e.get("thrust") or 0) > 0)
        return f"throttle kept on the remaining {n} engine{'s' if n != 1 else ''}" if n else "no engine left running - glide (best: nose slightly down)"
    if code == "no_throttle":
        _flags["reverse"] = True
        try:
            v.control.throttle = 0.0
        except Exception:  # noqa: BLE001
            pass
        return "throttle to idle - no power added while reversed"
    if code == "forward":
        _forward_later(v, ev.get("engines") or [], s.get("who") or "Pilot")
        return "reversed engine shut down - pilot is flipping it back to forward"
    if code == "relight_off":  # an engine shut down in flight: fumble 3-5 s, then back on (once per shutdown)
        from . import engine_restart
        note = engine_restart.maybe_restart(v, s.get("who") or "Pilot")
        return "pilot is restarting the engine" if note else ""
    if code == "cut_chutes":
        n = _cut_chutes(v)
        return (f"cut {n} parachute{'s' if n != 1 else ''} - can't repack mid-flight, living with the drag"
                if n else "couldn't cut the chute")
    if code == "revert":
        _revert_later(v, ev.get("ref") or DET.ref_cfg, ev.get("tags") or [], s.get("who") or "Pilot", s)
        return "pilot is fumbling for the switches to put it back"
    if code == "stall":
        if eng:
            return "autopilot: nose down, stall protection"
        out = []
        try:
            v.control.sas = True
            out.append("SAS on")
            if not _flags["reverse"] and v.control.throttle < 1.0:
                v.control.throttle = 1.0
                out.append("full power")
        except Exception:  # noqa: BLE001
            pass
        return ", ".join(out + ["lower the nose!"])
    if code == "blackout":
        protect.set_blackout(True, s.get("who"))
        return "autopilot: wings level, easing the G until they come to" if eng else \
            "nothing engaged and nobody at the stick"
    if code == "wake":
        protect.set_blackout(False)
        return "resuming the previous command" if eng else ""
    if code == "fuel_tip":
        return "say 'land at <runway>' or 'land here' - or glide it in"
    if code == "heat_tip":
        return "slow down or climb out of the heat" + (" - easing any override" if eng else "")
    if code == "probe":
        _PENDING_PROBE["want"] = True
        return ""
    return ""


def _heli_act(code, ev):
    """Helicopter emergency reports; heli.py reads DET.active itself and flies the response."""
    from . import heli
    eng = heli.active()
    if code == "heli_glide":
        note = ("the main rotor is set to 'On Power Loss: Locked' - no autorotation possible; " if ev.get("locked")
                else "")
        if ev.get("compound"):
            return note + ("autopilot: gliding on the side props and wings, controlled descent to a run-on landing"
                           if eng else "nothing engaged - side props full, nose level, glide her down on the wings!")
        return note + ("autopilot: controlled descent" if eng else "nothing engaged - brace, level the skids!")
    if code == "autorotate":
        return ("autopilot: AUTOROTATION - collective down to keep the rotor turning, flare near the ground" if eng
                else "nothing engaged - collective DOWN, keep the rotor spinning, flare at the bottom!")
    if code == "heli_spin":
        if ev.get("compound"):
            return ("autopilot: full differential on the side props, landing" if eng
                    else "nothing engaged - opposite pedal on the side props and put her down!")
        return ("autopilot: keeping forward speed to weathervane, then landing" if eng
                else "nothing engaged - get forward speed or put her down!")
    if code == "vrs_exit":
        return ("autopilot: easing the descent and adding forward speed to fly out of our own downwash" if eng
                else "nothing engaged - push the nose forward, get out of the downwash!")
    if code == "heli_upright":
        return ("autopilot: collective down, levelling the rotor disc" if eng
                else "nothing engaged - collective DOWN before the rotor drives us into the ground!")
    if code == "heli_side_out":
        return ("autopilot: yaw on what's left (reaction wheels), landing" if eng
                else "nothing engaged - hold the yaw and land!")
    return ""


def _own_reversal():
    """The autoland rollout reversed the engines itself (on the ground) - never an emergency."""
    try:
        from . import reversers
        return bool(reversers.engaged())
    except Exception:  # noqa: BLE001
        return False


FWD_FUMBLE_S = (2.0, 4.0)
FWD_DONE = ["Engines back on - forward thrust again.", "Engines back on. Reversers stowed, we're going FORWARD.",
            "Got it! Engines back on, thrust pointing the right way."]
REVERT_DONE = ["Got it - put back the way it was.", "Switches back. Don't touch anything.",
               "Restored. Whoever that was - STOP."]


def _cut_chutes(v):
    """Cut every deployed parachute (can't repack in flight). Returns how many were cut."""
    n = 0
    if v is None:
        return 0
    own_change()
    try:
        for c in v.parts.parachutes:
            try:
                if c.deployed:
                    c.cut()
                    n += 1
            except Exception:  # noqa: BLE001
                pass
    except Exception:  # noqa: BLE001
        pass
    return n


def _set_deploy_dir(cs, inverted):
    """Restore deploy direction: flip the sign of Deploy Angle when it doesn't match the snapshot."""
    try:
        mod = next(m for m in cs.part.modules if m.name == "ModuleControlSurface")
    except Exception:  # noqa: BLE001
        return False
    key = None
    try:
        for k in mod.fields:
            if "deploy" in k.lower() and "angle" in k.lower():
                key = k
                break
    except Exception:  # noqa: BLE001
        return False
    if not key:
        return False
    try:
        ang = float(str(mod.get_field(key)).replace(",", "."))
    except Exception:  # noqa: BLE001
        return False
    want_neg = bool(inverted)
    if (ang < 0) == want_neg:
        return True
    try:
        mod.set_field_float(key, -abs(ang) if want_neg else abs(ang))
        return True
    except Exception:  # noqa: BLE001
        try:
            mod.set_field(key, str(-abs(ang) if want_neg else abs(ang)))
            return True
        except Exception:  # noqa: BLE001
            return False


def _apply_revert(v, ref, tags, s=None):
    """Restore sabotaged toggles from the pre-takeoff snapshot. Returns a short report."""
    if v is None or not ref:
        return 0
    own_change()
    n, tags = 0, set(tags or [])
    s = s or {}
    try:
        surfs = list(v.parts.control_surfaces)
    except Exception:  # noqa: BLE001
        surfs = []
    refs = ref.get("surfaces") or []
    if len(surfs) == len(refs) and tags & {"inverted", "deploy_dir", "deploy", "authority", "pitch_lost",
                                           "yaw_lost", "roll_lost"}:
        from . import propulsion
        for cs, r0 in zip(surfs, refs):
            r = _surf(r0)
            try:
                if propulsion.is_blade(cs.part.title):
                    continue
                if "inverted" in tags:
                    cs.inverted = bool(r[4])
                if "authority" in tags or "inverted" in tags:
                    cs.authority_limiter = float(r[5])
                if "deploy" in tags:
                    cs.deployed = bool(r[6])
                if "deploy_dir" in tags:
                    _set_deploy_dir(cs, r[7])
                if tags & {"pitch_lost", "yaw_lost", "roll_lost"}:
                    cs.pitch_enabled, cs.yaw_enabled, cs.roll_enabled = bool(r[1]), bool(r[2]), bool(r[3])
                n += 1
            except Exception:  # noqa: BLE001
                pass
    if "wheels" in tags:
        try:
            for w, r in zip(v.parts.reaction_wheels, ref.get("wheels") or []):
                if r[1] and not w.active:
                    w.active = True
                    n += 1
        except Exception:  # noqa: BLE001
            pass
    if "intake" in tags:
        try:
            for it, r in zip(v.parts.intakes, ref.get("intakes") or []):
                if r[1] and not it.open:
                    it.open = True
                    n += 1
        except Exception:  # noqa: BLE001
            pass
    if "gear" in tags:
        want = s.get("want_gear")
        if want is None and ref.get("gear") is not None:
            want = bool(ref["gear"])
        if want is not None:
            try:
                v.control.gear = bool(want)
                n += 1
            except Exception:  # noqa: BLE001
                pass
    for key in ("brakes", "lights", "sas", "rcs"):
        if key in tags and ref.get(key) is not None:
            try:
                setattr(v.control, key, bool(ref[key]))
                n += 1
            except Exception:  # noqa: BLE001
                pass
    if "flow" in tags:
        try:
            for p in v.parts.all:
                for res in p.resources:
                    try:
                        if not res.enabled:
                            res.enabled = True
                            n += 1
                    except Exception:  # noqa: BLE001
                        pass
        except Exception:  # noqa: BLE001
            pass
    if "ag" in tags:
        for i, want in enumerate(ref.get("ag") or []):
            try:
                if bool(v.control.get_action_group(i)) != bool(want):
                    v.control.set_action_group(i, bool(want))
                    n += 1
            except Exception:  # noqa: BLE001
                pass
    return n


def _revert_later(v, ref, tags, who, s=None, timer=None):
    """Call out already happened; fumble 2-4 s, then restore the snapshot and say so."""
    def run():
        n = _apply_revert(v, ref, tags, s)
        _post(f"{who}: {random.choice(REVERT_DONE)}" if n else f"{who}: I can't find the right switch!")
    t = (timer or threading.Timer)(random.uniform(*FWD_FUMBLE_S), run)
    t.daemon = True
    t.start()


def _set_active(v, idxs, on):
    """Engine on/off for these 1-based engine indices only (the others keep running). Marked as our own change so
    the config watcher doesn't report it as a shutdown / relight it a second time."""
    n = 0
    try:
        engines = list(v.parts.engines)
    except Exception:  # noqa: BLE001
        return 0
    own_change()
    for i in idxs:
        try:
            engines[i - 1].active = on
            n += 1
        except Exception:  # noqa: BLE001
            pass
    return n


def _forward_later(v, idxs, who, timer=None):
    """Reversed in flight: shut down ONLY the reversed engines now, fumble 2-4 s, switch them to forward, turn them
    back on, 'Engines back on'. A throttle command in between doesn't relight them early (engine_restart is held)."""
    from . import engine_restart
    with engine_restart._lock:
        engine_restart._pending["on"] = True
    _set_active(v, idxs, False)

    def run():
        try:
            n = _forward(v, idxs)
            _set_active(v, idxs, True)
        finally:
            with engine_restart._lock:
                engine_restart._pending["on"] = False
        _post(f"{who}: {random.choice(FWD_DONE)}" if n else f"{who}: I can't find the reverser switch! Engines back on anyway.")
    t = (timer or threading.Timer)(random.uniform(*FWD_FUMBLE_S), run)
    t.daemon = True
    t.start()


def _post(text):
    try:
        from . import science
        science.post_event(text)
    except Exception:  # noqa: BLE001
        log.warning("%s", text)


def _forward(v, idxs):
    """Switch reversed engines back to forward thrust: a 'reverse' engine mode is toggled, else a part-module event /
    action naming the reverser is used. Returns how many engines were switched."""
    n = 0
    try:
        engines = list(v.parts.engines)
    except Exception:  # noqa: BLE001
        return 0
    for i in idxs:
        try:
            e = engines[i - 1]
        except IndexError:
            continue
        from . import reversers  # same mechanism as the autoland rollout (mode or reverser event / action)
        try:
            n += 1 if reversers.switch(e, False, toggle_ok=True) else 0
        except Exception:  # noqa: BLE001
            pass
    return n


_PENDING_PROBE = {"want": False}


def compose(ev, who, report):
    """The chat line: '<who>: <panicked line> [<what was done>]'."""
    kind = ev["kind"]
    if ev["start"]:
        k = ev.get("tag") if kind == "config" else kind
        line = _pick(k, what=ev.get("what") or "", pct=ev.get("pct"))
        if kind == "config":
            report = ["changed: " + "; ".join(ev.get("changes") or [])] + list(report)
    else:
        line = _pick(kind if kind == "config_ok" else kind + "_ok", what=ev.get("what") or "")
    rep = "; ".join(r for r in report if r)
    return f"{who or 'Pilot'}: {line}" + (f" [{rep}]" if rep else "")


def handle(events, v, s, post=None):
    """Act on detector events and post one line each (resolved: one 'ok' line unless quiet). Returns the lines."""
    if post is None:
        from . import science
        post = science.post_event
    said = []
    for ev in events:
        if not ev["start"]:
            _clear_alert(ev["key"])
            if ev["kind"] == "reverse":
                _flags["reverse"] = False
            if ev["kind"] in ("config", "config_ok"):
                _flags["safe"] = False
            if ev["kind"] == "upside_down":
                _flags["upside"] = False
            if ev["kind"].startswith("upside_down"):
                DAMAGE["inverted"] = ""
            report = [_act(a, ev, v, s) for a in ev.get("actions") or []]
            if ev.get("quiet"):
                continue
        else:
            _alert(ev["key"], ev["kind"], ev["short"], one_shot=ev.get("one_shot", False))
            report = [_act(a, ev, v, s) for a in ev.get("actions") or []]
            if ev["kind"] == "parts":
                record_lost(s.get("vid"), getattr(v, "name", "") if v is not None else "", ev.get("lost") or [ev["what"]])
            elif ev["kind"].startswith("upside_down"):
                DAMAGE["inverted"] = "on the ground" if ev["kind"] == "upside_down_ground" else "in flight"
        line = compose(ev, s.get("who"), report)
        log.warning("emergency: %s", line)
        said.append(line)
        try:
            post(line)
        except Exception:  # noqa: BLE001
            pass
        if ev["start"]:
            try:  # an emergency cuts any small talk off at once
                from . import crew
                crew.TALK.cut.set()
            except Exception:  # noqa: BLE001
                pass
        if ev["start"] and s.get("crew_list"):  # the rest of the crew on the intercom (background, never waited on)
            try:
                from . import crew
                crew.speak(ev, s["crew_list"], s.get("who_full") or s.get("who"), s.get("g"), post)
            except Exception as ex:  # noqa: BLE001
                log.debug("crew chatter: %s", ex)
    return said


def probe_result(axis, result, post=None):
    """Adapt to a probe result; returns the chat line ('' = nothing to say)."""
    if result == "inverted":
        SIGN[axis] = -1.0
        _alert(f"probe:{axis}", "config", f"{axis.upper()} INVERTED - adapted", one_shot=True)
        kind, rep = "probe_inverted", f"{axis} input flipped for the autopilots"
    elif result == "dead":
        _flags["safe"] = True
        _alert(f"probe:{axis}", "probe", f"NO {axis.upper()} RESPONSE", one_shot=True)
        kind, rep = "probe_dead", f"bank limited to {SAFE_BANK:.0f} deg, gentle inputs"
    else:
        if result == "normal":
            SIGN[axis] = 1.0
        return ""
    names = {"pitch": "elevator", "roll": "roll control", "yaw": "rudder"}
    line = f"{_who()}: {_pick(kind, what=names.get(axis, axis))} [{rep}]"
    if post is None:
        from . import science
        post = science.post_event
    try:
        post(line)
    except Exception:  # noqa: BLE001
        pass
    return line


_WHO = {"name": None}


def _who():
    return _WHO["name"] or "Pilot"


# ---------------------------------------------------------------- sampling (kRPC)
def _surf_deploy_dir(cs):
    """True when Deploy Angle is negative (KSP's 'Deploy Direction: Inverted')."""
    try:
        mod = next(m for m in cs.part.modules if m.name == "ModuleControlSurface")
        for k in mod.fields:
            if "deploy" in k.lower() and "angle" in k.lower():
                return float(str(mod.get_field(k)).replace(",", ".")) < 0
    except Exception:  # noqa: BLE001
        pass
    return False


def _scan_cfg(v):
    """Pre-takeoff / in-flight snapshot of every toggle the saboteur might flip."""
    cfg = {"surfaces": [], "wheels": [], "engines": [], "intakes": [], "gear": None, "brakes": None, "chutes": [],
           "lights": None, "sas": None, "rcs": None, "flow": [], "ag": []}
    try:
        from . import propulsion
        for cs in v.parts.control_surfaces:
            try:
                if propulsion.is_blade(cs.part.title):
                    continue  # propeller blades: their pitch / authority is Luke's prop control, not sabotage
                cfg["surfaces"].append((cs.part.title, bool(cs.pitch_enabled), bool(cs.yaw_enabled), bool(cs.roll_enabled),
                                        bool(cs.inverted), float(cs.authority_limiter), bool(cs.deployed),
                                        bool(_surf_deploy_dir(cs))))
            except Exception:  # noqa: BLE001
                pass
    except Exception:  # noqa: BLE001
        pass
    for attr, fn in (("wheels", lambda w: (w.part.title, bool(w.active), bool(getattr(w, "broken", False)))),
                     ("engines", lambda e: (e.part.title, bool(e.active), str(e.mode) if e.has_modes else "")),
                     ("intakes", lambda i: (i.part.title, bool(i.open))),
                     ("chutes", lambda c: (c.part.title, bool(c.deployed), bool(c.armed)))):
        src = {"wheels": "reaction_wheels", "engines": "engines", "intakes": "intakes", "chutes": "parachutes"}[attr]
        try:
            for x in getattr(v.parts, src):
                try:
                    cfg[attr].append(fn(x))
                except Exception:  # noqa: BLE001
                    pass
        except Exception:  # noqa: BLE001
            pass
    try:
        c = v.control
        cfg["gear"], cfg["brakes"] = bool(c.gear), bool(c.brakes)
        cfg["lights"], cfg["sas"], cfg["rcs"] = bool(c.lights), bool(c.sas), bool(c.rcs)
        cfg["ag"] = [bool(c.get_action_group(i)) for i in range(10)]
    except Exception:  # noqa: BLE001
        pass
    try:  # resource flow locks (right-click 'no flow' on a tank)
        for p in v.parts.all:
            title = str(getattr(p, "title", "") or "part")
            try:
                for res in p.resources:
                    try:
                        cfg["flow"].append((f"{title}/{res.name}", bool(res.enabled)))
                    except Exception:  # noqa: BLE001
                        pass
            except Exception:  # noqa: BLE001
                pass
    except Exception:  # noqa: BLE001
        pass
    try:
        from . import propulsion
        cfg["rotors"] = propulsion.rotor_status(v)
        cfg["props"] = propulsion.prop_status(v) if cfg["rotors"] else None
    except Exception:  # noqa: BLE001
        cfg["rotors"], cfg["props"] = [], None
    try:
        cfg["ship"] = _scan_ship(v)
    except Exception as ex:  # noqa: BLE001
        log.debug("ship scan: %s", ex)
        cfg["ship"] = None
    return cfg


def _phase_flags(plane_mod=None):
    """(landing, want_gear) for the active plane/heli autopilot. want_gear: True=down, False=up, None=don't enforce."""
    try:
        from . import plane as plane_mod0, hold, lander
        plane_mod = plane_mod or plane_mod0
    except Exception:  # noqa: BLE001
        return False, False
    phase = ""
    try:
        if plane_mod.active():
            phase = str(plane_mod.STATUS.get("phase") or "")
    except Exception:  # noqa: BLE001
        pass
    landing = phase.startswith(("final", "flare", "rollout", "recover"))
    try:
        if hold.active() and str(hold.STATUS.get("phase") or "") == "takeoff":
            return False, None  # takeoff roll / rotate: hold manages gear-up timing
    except Exception:  # noqa: BLE001
        pass
    if phase == "takeoff":
        return False, None
    if landing or phase.startswith("rollout"):
        return True, True
    try:
        if lander.active():
            return True, True  # lander legs / chutes are its job
    except Exception:  # noqa: BLE001
        pass
    return False, False  # cruise / climb / holds: gear up


SHIP_RES = (("LiquidFuel", "LF"), ("Oxidizer", "OX"), ("MonoPropellant", "Mono"), ("XenonGas", "Xenon"),
            ("SolidFuel", "SRB"), ("ElectricCharge", "EC"))


def _scan_ship(v):
    """Everything the Systems dashboard needs for rockets / landers / rovers / stations / probes (every SCAN_S).
    Each part is optional: missing kRPC features leave the value None and the row out."""
    def tryv(fn, default=None):
        try:
            return fn()
        except Exception:  # noqa: BLE001
            return default
    ship = {"res": {}, "stage_res": {}, "stage": None, "next_stage": None, "gimbals": [], "solar": None,
            "rcs": None, "rcs_n": 0, "sas": None, "sas_mode": "", "comms": None, "antennas": None, "wheels": None,
            "wings": False, "legs": 0, "docking": 0, "body": "", "sit": ""}
    res = v.resources
    for name, short in SHIP_RES:
        mx = tryv(lambda: float(res.max(name)), 0.0)
        if mx > 0:
            ship["res"][short] = (tryv(lambda: float(res.amount(name)), 0.0), mx)
    st = tryv(lambda: int(v.control.current_stage))
    ship["stage"] = st
    if st is not None and st > 0:
        sr = tryv(lambda: v.resources_in_decouple_stage(st - 1, False))
        if sr is not None:
            for name, short in SHIP_RES[:5]:
                mx = tryv(lambda: float(sr.max(name)), 0.0)
                if mx > 0:
                    ship["stage_res"][short] = (tryv(lambda: float(sr.amount(name)), 0.0), mx)
        parts = tryv(lambda: list(v.parts.in_stage(st - 1)), []) or []
        nxt = {"engines": 0, "decouplers": 0, "chutes": 0, "other": 0}
        for p in parts:
            k = ("engines" if tryv(lambda: p.engine) is not None else "decouplers" if tryv(lambda: p.decoupler) is not None
                 else "chutes" if tryv(lambda: p.parachute) is not None else "other")
            nxt[k] += 1
        ship["next_stage"] = (st - 1, nxt)
    for e in tryv(lambda: v.parts.engines, []) or []:
        ship["gimbals"].append((tryv(lambda: bool(e.gimballed), False), tryv(lambda: bool(e.gimbal_locked), False),
                                tryv(lambda: float(e.gimbal_limit), None)))
    panels = tryv(lambda: list(v.parts.solar_panels), []) or []
    if panels:
        ship["solar"] = (sum(1 for p in panels if tryv(lambda: bool(p.deployed), False)), len(panels),
                         sum(tryv(lambda: float(p.energy_flow), 0.0) for p in panels))
    ship["rcs_n"] = len(tryv(lambda: list(v.parts.rcs), []) or [])
    ship["rcs"] = tryv(lambda: bool(v.control.rcs))
    ship["sas"] = tryv(lambda: bool(v.control.sas))
    ship["sas_mode"] = tryv(lambda: str(v.control.sas_mode).split(".")[-1], "")
    comms = tryv(lambda: v.comms)
    if comms is not None:
        ship["comms"] = (tryv(lambda: bool(comms.can_communicate)), tryv(lambda: float(comms.signal_strength)))
    ants = tryv(lambda: list(v.parts.antennas), []) or []
    if ants:
        ship["antennas"] = (sum(1 for a in ants if tryv(lambda: bool(a.deployed), True)), len(ants))
    wheels = tryv(lambda: list(v.parts.wheels), []) or []
    if wheels:
        ship["wheels"] = [(tryv(lambda: bool(w.grounded)), tryv(lambda: bool(getattr(w, "broken"))),
                           tryv(lambda: bool(w.has_motor), False), tryv(lambda: bool(w.motor_enabled)))
                          for w in wheels]
    ship["legs"] = len(tryv(lambda: list(v.parts.legs), []) or [])
    ship["docking"] = len(tryv(lambda: list(v.parts.docking_ports), []) or [])
    try:
        from . import ksp_actions
        ship["wings"] = bool(ksp_actions._has_wings(v))
    except Exception:  # noqa: BLE001
        pass
    ship["body"] = tryv(lambda: str(v.orbit.body.name), "")
    return ship


def ship_kind(s, ship):
    """heli / plane / rover / station / probe / lander / rocket (pure)."""
    if s.get("heli"):
        return "heli"
    ship = ship or {}
    engines = s.get("engines") or []
    motors = sum(1 for w in ship.get("wheels") or [] if w[2])
    if motors >= 2 and not ship.get("wings"):
        return "rover"
    air = any(e.get("air") for e in engines) or bool(s.get("rotors"))
    if s.get("plane") or (ship.get("wings") and (air or not engines) and ship.get("legs", 0) < 3):
        return "plane"  # winged + jets / props (or a glider); a finned rocket stays a rocket
    sit = str(s.get("sit") or "")
    any_engine = bool(s.get("engines"))
    if not any_engine or sit in ("orbiting", "escaping", "docked"):
        if not any_engine or ship.get("docking", 0) >= 2:
            return "station" if (s.get("crew") or 0) > 0 else "probe"
    if ship.get("legs", 0) >= 3:
        return "lander"
    return "rocket"


def _pct(a_m):
    a, m = a_m
    return a / m if m > 0 else 0.0


def ship_rows(s, ship, kind, flying):
    """Systems rows for the non-plane craft types (pure); only the rows that apply."""
    rows = [("ok", "Craft", {"heli": "HELICOPTER", "plane": "PLANE", "rover": "ROVER", "station": "STATION",
                             "probe": "PROBE", "lander": "LANDER", "rocket": "ROCKET"}.get(kind, kind.upper()))]
    if not ship:
        return rows
    res, sres = ship.get("res") or {}, ship.get("stage_res") or {}
    if kind in ("rocket", "lander", "probe", "station"):
        if sres:
            parts = [f"{k} {100 * _pct(v):.0f}%" for k, v in sres.items()]
            low = min(_pct(v) for v in sres.values())
            rows.append(("fail" if low < FUEL_LOW else ("caution" if low < 0.15 else "ok"),
                         f"Fuel (stage {ship['stage'] - 1})", " ".join(parts)))
        for k in ("LF", "OX", "Mono", "Xenon", "SRB"):
            if k in res and (k not in sres or kind in ("probe", "station")):
                p = _pct(res[k])
                rows.append(("fail" if p < FUEL_LOW else ("caution" if p < 0.15 else "ok"), f"{k} total",
                             f"{100 * p:.0f}% ({res[k][0]:.0f}/{res[k][1]:.0f})"))
    elif kind == "rover":
        pass
    if "EC" in res:
        p = _pct(res["EC"])
        det = f"{100 * p:.0f}% ({res['EC'][0]:.0f}/{res['EC'][1]:.0f})"
        sol = ship.get("solar")
        if sol:
            det += f", solar {sol[0]}/{sol[1]} out {sol[2]:+.1f}/s"
        rows.append(("fail" if p < 0.10 else ("caution" if p < 0.25 else "ok"),
                     "Battery" if kind == "rover" else "Power", det))
    if kind == "rover":
        wh = ship.get("wheels") or []
        broken = sum(1 for w in wh if w[1])
        grounded = sum(1 for w in wh if w[0])
        motors = [w for w in wh if w[2]]
        on = sum(1 for w in motors if w[3])
        lvl = "fail" if broken else ("caution" if grounded < len(wh) or on < len(motors) else "ok")
        rows.append((lvl, "Wheels", f"{grounded}/{len(wh)} grounded, motors {on}/{len(motors)} on"
                     + (f", {broken} BROKEN" if broken else "")))
        rows.append(("ok", "Speed", f"{float(s.get('speed') or 0.0):.1f} m/s"))
    if kind in ("rocket", "lander", "probe", "station") and ship.get("rcs_n"):
        mono = res.get("Mono")
        lvl = "caution" if ship.get("rcs") and mono and _pct(mono) < 0.15 else "ok"
        rows.append((lvl, "RCS", ("on" if ship.get("rcs") else "off") + f", {ship['rcs_n']} thrusters"
                     + (f", mono {100 * _pct(mono):.0f}%" if mono else "")))
    if kind in ("rocket", "lander", "probe", "station", "rover") and ship.get("sas") is not None:
        rows.append(("ok", "SAS", ("on, " + (ship.get("sas_mode") or "").replace("_", " ")) if ship["sas"] else "off"))
    if kind in ("probe", "station") or (kind in ("rocket", "lander", "rover") and not s.get("crew")):
        c, a = ship.get("comms"), ship.get("antennas")
        if c is not None or a is not None:
            ok = c[0] if c else None
            det = ("connected" if ok else "NO SIGNAL" if ok is False else "?") + \
                (f", signal {100 * c[1]:.0f}%" if c and c[1] is not None else "") + \
                (f", antennas {a[0]}/{a[1]} deployed" if a else "")
            lvl = "ok" if ok else ("fail" if not s.get("crew") else "caution")
            rows.append((lvl, "Comms", det))
    ns = ship.get("next_stage")
    if kind in ("rocket", "lander", "probe") and ns and any(ns[1].values()):
        n = ns[1]
        bits = [f"{n[k]} {w}" for k, w in (("engines", "engine"), ("decouplers", "decoupler"), ("chutes", "chute"),
                                            ("other", "other")) if n[k]]
        rows.append(("ok", "Next stage", f"S{ns[0]}: " + ", ".join(bits)))
    return rows


def _hottest(v):
    hot, title = 0.0, ""
    for p in v.parts.all:
        try:
            r = max(p.temperature / max(p.max_temperature, 1.0), p.skin_temperature / max(p.max_skin_temperature, 1.0))
            if r > hot:
                hot, title = r, p.title
        except Exception:  # noqa: BLE001
            pass
    return hot, title


def _engine_sample(v):
    out = []
    for e in v.parts.engines:
        d = {"title": "", "active": False, "thrust": 0.0, "max_thrust": 0.0, "has_fuel": None, "air": False,
             "reversed": False, "rev_mode": False, "mode": ""}
        try:
            d["title"] = e.part.title
            d["active"] = bool(e.active)
            if d["active"]:
                d["thrust"], d["max_thrust"] = float(e.thrust), float(e.max_thrust)
                d["has_fuel"] = bool(e.has_fuel)
                try:
                    d["air"] = "IntakeAir" in list(e.propellant_names)
                except Exception:  # noqa: BLE001
                    pass
                try:
                    if e.has_modes:
                        d["mode"] = str(e.mode)
                        d["rev_mode"] = d["reversed"] = "revers" in d["mode"].lower()
                except Exception:  # noqa: BLE001
                    pass
                try:  # reverser part module that only offers 'Forward Thrust' (or says reversed) = reversed now
                    from . import reversers
                    d["rev_module"] = reversers.module_reversed(e)
                    d["reversed"] = d["reversed"] or d["rev_module"]
                except Exception:  # noqa: BLE001
                    pass
                if not d["reversed"]:
                    try:  # thrust force pointing out of the nose (vessel frame: +y = forward)
                        for th in e.thrusters:
                            if th.thrust_direction(v.reference_frame)[1] < -0.5:
                                d["reversed"] = True
                                break
                    except Exception:  # noqa: BLE001
                        pass
        except Exception:  # noqa: BLE001
            pass
        out.append(d)
    return out


def _ratio(v, name):
    try:
        mx = v.resources.max(name)
        return None if mx <= 0 else v.resources.amount(name) / mx
    except Exception:  # noqa: BLE001
        return None


def _rollout_rev():
    """The autoland rollout reversed the engines on purpose (normal on the ground - no MAYDAY, no switch-back)."""
    try:
        from . import reversers
        return reversers.engaged()
    except Exception:  # noqa: BLE001
        return False


_LIFT = None


def _learn_liftoff(v, s):
    """Learn the craft's liftoff speed from any real takeoff (hand-flown or autopilot) and log it (Luke 2026-10-08:
    the Aeris 4A needs ~120 m/s, not the default ~56)."""
    global _LIFT
    if s.get("heli"):  # a vertical liftoff isn't a takeoff speed
        return None
    from . import plane
    from . import takeoff as tko
    if _LIFT is None:
        _LIFT = tko.LiftoffLearner()
    agl = None
    if _LIFT.pending:  # only while confirming a liftoff (one extra kRPC read a second)
        try:
            agl = float(v.flight().surface_altitude)
        except Exception:  # noqa: BLE001
            agl = None
    r = _LIFT.feed(s["t"], s.get("sit"), float(s.get("speed") or 0.0), s.get("vid"), agl)
    if not r:
        return None
    kind, spd = r
    who = "autopilot" if _engaged() else "by hand"
    if kind == "not_learned":
        log.info("takeoff: %s liftoff at ~%.0f m/s not learned (stayed below %.0f m AGL or too fast)", v.name, spd,
                 tko.LIFT_MIN_AGL)
        return r
    if kind == "liftoff":
        try:
            pitch = float(v.flight(v.surface_reference_frame).pitch)
        except Exception:  # noqa: BLE001
            pitch = float("nan")
        log.info("takeoff: %s lifted off at ~%.0f m/s (pitch %.1f, %s)", v.name, spd, pitch, who)
        return r
    tko.record_liftoff(plane._craft_keys(v), spd)
    log.info("takeoff: learned liftoff %.0f m/s for %s (%s) -> next autopilot Vr %.0f m/s", spd, v.name, who,
             tko.rotate_speed(0.0, spd))
    return r


# ---------------------------------------------------------------- damage memory (what the watcher actually saw)
def record_lost(vid, vessel, labels):
    if vid != DAMAGE["vid"]:
        DAMAGE.update(vid=vid, vessel=vessel, lost=[], inverted="")
    DAMAGE["vessel"] = vessel or DAMAGE["vessel"]
    DAMAGE["lost"].append((time.strftime("%H:%M:%S"), ", ".join(str(x) for x in labels)))


def vessel_changed(vid, vessel=""):
    if vid != DAMAGE["vid"]:
        DAMAGE.update(vid=vid, vessel=vessel, lost=[], inverted="")


def damage_facts():
    """Plain facts from the watcher: lost parts this flight, attitude, active alerts. Never guesses."""
    lost = "; ".join(f"{what} (at {t})" for t, what in DAMAGE["lost"])
    facts = [f"Parts lost this flight: {lost}." if lost else "Parts lost this flight: none detected."]
    if DAMAGE["inverted"]:
        facts.append(f"Attitude: UPSIDE DOWN ({DAMAGE['inverted']}).")
    al = alerts_text()
    if al:
        facts.append(f"Active alerts: {al}.")
    return " ".join(facts)


def has_damage():
    return bool(DAMAGE["lost"] or DAMAGE["inverted"] or alerts_text())


def damage_context():
    """System-prompt block for the AI ('' when the watcher isn't running)."""
    if not running():
        return ""
    return ("Emergency watcher facts (trust these over guesses; never invent damage, never claim repairs or "
            "readiness the tools didn't report): " + damage_facts())


def damage_text():
    """'damage report' / 'what are we missing': what the watcher actually saw."""
    if not running():
        return "Damage report unavailable: the emergency watcher isn't running (no part-loss tracking right now)."
    out = "Damage report: " + damage_facts()
    if any("wheel" in w.lower() or "gear" in w.lower() for _, w in DAMAGE["lost"]):
        out += " With a wheel gone the landing will be rough: touch down slow and gentle, expect it to pull."
    if DAMAGE["inverted"] == "on the ground":
        out += " We're on our roof - that flight is over; recover the craft or revert."
    return out


def gear_note():
    """Appended to set_gear: lost gear / wheels this flight (only what was actually seen)."""
    gone = [w for _, w in DAMAGE["lost"] if "wheel" in w.lower() or "gear" in w.lower()]
    return f" Note: lost this flight: {'; '.join(gone)} - landing will be rough." if gone else ""


class PartTracker:
    """Which parts disappeared since the last tick (by kRPC object id), labelled once when first seen: title, and for
    wheels / landing gear their place (left / right main, nose, center) from the vessel frame (x right, y forward)."""

    def __init__(self):
        self.vid, self.labels = None, {}

    def update(self, vid, parts, v=None):
        ids = {getattr(p, "_object_id", id(p)): p for p in parts}
        if vid != self.vid:
            self.vid, self.labels, lost = vid, {}, []
        else:
            lost = [lab for i, lab in self.labels.items() if i not in ids]
        new = [i for i in ids if i not in self.labels]
        if new:
            gear = _gear_ids(v) if v is not None else set()
            for i in new:
                self.labels[i] = part_label(ids[i], v, i in gear)
        for i in [i for i in self.labels if i not in ids]:
            del self.labels[i]
        return lost


def _gear_ids(v):
    out = set()
    for attr in ("wheels", "legs"):
        try:
            for w in getattr(v.parts, attr):
                out.add(getattr(w.part, "_object_id", id(w.part)))
        except Exception:  # noqa: BLE001
            pass
    return out


def part_label(p, v, is_gear):
    try:
        title = str(p.title)
    except Exception:  # noqa: BLE001
        title = "a part"
    if not is_gear or v is None:
        return title
    try:
        x, y = p.position(v.reference_frame)[:2]
    except Exception:  # noqa: BLE001
        return f"a wheel ({title})"
    if abs(x) > 0.3:
        where = ("left" if x < 0 else "right") + " main wheel"
    else:
        where = "nose wheel" if y > 0.5 else "center wheel"
    return f"{where} ({title})"


PARTS = PartTracker()


def _sample(v, scan, sc=None):
    from . import plane
    landing, want_gear = _phase_flags(plane)
    f = v.flight(v.orbit.body.reference_frame)
    sit = str(v.situation).split(".")[-1]
    if sit in GROUND_SITS:
        want_gear = True
    s = {"t": time.time(), "vid": getattr(v, "_object_id", None) or v.name, "sit": sit,
         "g": float(f.g_force), "speed": float(f.speed), "vs": float(f.vertical_speed), "aoa": float(f.angle_of_attack),
         "throttle": float(v.control.throttle), "stage": int(v.control.current_stage), "parts": len(v.parts.all),
         "engines": _engine_sample(v), "intake_air": _ratio(v, "IntakeAir"), "fuel": _ratio(v, "LiquidFuel"),
         "crew": 0, "ctrl": None, "temp": None, "cfg": None, "stall": None,
         "own": time.time() - _flags["own_t"] < OWN_GRACE_S,
         "landing": landing, "want_gear": want_gear,
         "rollout": (plane.active() and str(plane.STATUS.get("phase") or "").startswith("rollout")) or _rollout_rev()}
    try:
        s["stall"] = float(f.stall_fraction)
    except Exception:  # noqa: BLE001
        pass
    try:
        crew = list(v.crew)
        s["crew"] = len(crew)
        k = next((c for c in crew if str(getattr(c, "trait", "")).lower() == "pilot"), crew[0] if crew else None)
        s["who"] = str(k.name).split()[0] if k is not None else None
        s["who_full"] = str(k.name) if k is not None else None
        s["crew_list"] = [(str(c.name), str(getattr(c, "trait", "") or "")) for c in crew]
        try:  # personalities: generated once per kerbal (KSP stats read only the first time)
            from . import personality
            for c in crew:
                personality.ensure(str(c.name), lambda c=c: personality.stats_of(c))
        except Exception:  # noqa: BLE001
            pass
    except Exception:  # noqa: BLE001
        s["who"] = None
    _WHO["name"] = s.get("who")
    try:
        src, state = str(v.control.source).lower(), str(v.control.state).lower()
        s["ctrl"] = not (src.endswith("none") or state.endswith("none"))
    except Exception:  # noqa: BLE001
        pass
    try:
        s["roll"], s["pitch"] = float(f.roll), float(f.pitch)
    except Exception:  # noqa: BLE001
        pass
    if sc is not None and s["sit"] in GROUND_SITS:
        try:  # the craft's top (vessel frame -z) in the surface frame (x = up): < 0 = on its back
            s["up"] = float(sc.transform_direction((0.0, 0.0, -1.0), v.reference_frame, v.surface_reference_frame)[0])
        except Exception:  # noqa: BLE001
            pass
    if scan:
        s["cfg"] = _scan_cfg(v)
        try:
            s["temp"] = _hottest(v)
        except Exception:  # noqa: BLE001
            pass
    return s


def props_detail(rot, ps):
    """Props dashboard text: per side group (L / R / C) RPM (limit), torque, blade pitch, motor; else one summary."""
    from . import propulsion
    grp = (ps or {}).get("groups") or {}
    if grp:
        parts = []
        for gname, d in grp.items():
            b = [f"{gname[0].upper()}{d['n'] if d['n'] > 1 else ''}:"]
            if d.get("rpm") is not None:
                b.append(f"{d['rpm']:.0f} RPM" + (f"/{d['rpm_limit']:.0f}" if d.get("rpm_limit") is not None else ""))
            if d.get("torque") is not None:
                b.append(f"tq {d['torque']:.0f}%")
            if d.get("pitch") is not None:
                b.append(f"pitch {d['pitch']:+.0f}")
            if d.get("motor_on") is False:
                b.append("motor OFF")
            parts.append(" ".join(b))
        if ps.get("counter_pairs"):
            parts.append("counter-rotating")
        if ps.get("reverse"):
            parts.append("REVERSE")
        return "; ".join(parts)
    bits = [f"{len(rot)} rotor{'s' if len(rot) != 1 else ''}"]
    r0 = ((ps or {}).get("rotors") or [{}])[0]
    rpm = r0.get("rpm")
    if rpm is None:
        rpm = next((propulsion.num(r) for _, _, r in rot if propulsion.num(r) is not None), None)
    if rpm is not None:
        bits.append(f"{rpm:.0f} RPM" + (f" (limit {r0['rpm_limit']:.0f})" if r0.get("rpm_limit") is not None else ""))
    if r0.get("torque") is not None:
        bits.append(f"torque {r0['torque']:.0f}%")
    if (ps or {}).get("pitch") is not None:
        bits.append(f"pitch {ps['pitch']:+.0f} deg")
    if (ps or {}).get("reverse"):
        bits.append("REVERSE")
    if propulsion.motor_off(rot):
        bits.append("motor OFF")
    return ", ".join(bits)


def heli_detail(h, s):
    """Dashboard 'Mode' row for a helicopter: HELI, kind, rotor RPM, collective, vertical speed."""
    bits = [f"HELI ({h.get('kind') or '?'})"]
    if h.get("rpm") is not None:
        bits.append(f"rotor {h['rpm']:.0f} RPM" + (f"/{h['rpm_limit']:.0f}" if h.get("rpm_limit") is not None else ""))
    if h.get("coll") is not None:
        bits.append(f"collective {h['coll']:.1f} deg")
    bits.append(f"V/S {float(s.get('vs') or 0.0):+.1f} m/s")
    if h.get("hdg") is not None:
        hb = f"HDG {h['hdg']:03.0f}"
        if h.get("trk") is not None and (h.get("gs") or 0.0) > 1.0:
            drift = (h["trk"] - h["hdg"] + 180.0) % 360.0 - 180.0
            hb += f" / TRK {h['trk']:03.0f} ({h['gs']:.0f} m/s), drift {drift:+.0f}"
        else:
            hb += " / TRK - (hovering)"
        bits.append(hb)
    if h.get("locked"):
        bits.append("Locked on power loss")
    return ", ".join(bits)


def build_systems(s, cfg, ref):
    """Dashboard rows [(ok|caution|fail, name, detail)] from the latest sample + config scan (pure)."""
    rows = []
    act = DET.active
    flying = s.get("sit") == "flying" and not s.get("rollout")
    if "inverted_air" in act or "inverted_ground" in act:
        rows.append(("fail", "Attitude", "INVERTED" + (" (on the ground)" if "inverted_ground" in act else "")))
    elif s.get("roll") is not None:
        rows.append(("ok", "Attitude", f"upright, bank {float(s['roll']):.0f}"))
    h = s.get("heli")
    if h:
        rows.append(("fail" if ("heli_rpm" in act or "heli_tail" in act) else ("caution" if "heli_vrs" in act else "ok"),
                     "Mode", heli_detail(h, s)))
    rc = s.get("rotors") or []
    if rc:  # Luke: every rotor's 'Brake' must be 0 to spin; applied in flight = tamper
        on = [c for c in rc if (c.get("brake") or 0) > 0]
        det = ", ".join(f"{c['group']} {c['title']} {c['brake']:.0f}" if c.get("brake") is not None
                        else f"{c['group']} {c['title']} ?" for c in rc)
        rows.append(("fail" if on and flying else ("caution" if on else "ok"), "Rotor brakes", det))
    ship = (cfg or {}).get("ship")
    kind = ship_kind(s, ship)
    gimbals = (ship or {}).get("gimbals") or []
    for i, e in enumerate(s.get("engines") or [], 1):
        mx = float(e.get("max_thrust") or 0.0)
        pct = 100.0 * float(e.get("thrust") or 0.0) / mx if mx > 0 else 0.0
        rev = e.get("rev_mode") or (e.get("reversed") and "reverse" in act)
        det = (f"{pct:.0f}% thrust" if e.get("active") else "off") + (f", {e['mode']}" if e.get("mode") else "") + \
            (", REVERSE" if rev else "") + (", NO FUEL" if e.get("active") and e.get("has_fuel") is False else "")
        if kind in ("rocket", "lander", "probe", "station") and i <= len(gimbals) and gimbals[i - 1][0]:
            g = gimbals[i - 1]
            det += ", gimbal LOCKED" if g[1] else (f", gimbal {g[2]:.0f}%" if g[2] is not None else ", gimbal")
        lvl = "fail" if f"flameout:{i}" in act or (rev and flying) else \
            ("caution" if not e.get("active") and flying else "ok")
        rows.append((lvl, f"Engine {i} {e.get('title', '')}", det))
    if cfg:
        its = cfg.get("intakes") or []
        if its:
            air = s.get("intake_air")
            n_open = sum(1 for x in its if x[1])
            lvl = "fail" if air is not None and air < 0.05 and flying else ("caution" if n_open < len(its) or (air is not None and air < 0.2) else "ok")
            rows.append((lvl, "Intakes", f"{n_open}/{len(its)} open" + (f", air {100 * air:.0f}%" if air is not None else "")))
        refs = (ref or {}).get("surfaces") or []
        surf = cfg.get("surfaces") or []
        for j, x in enumerate(surf[:16]):
            axes = "".join(c for c, on in (("P", x[1]), ("Y", x[2]), ("R", x[3])) if on) or "none"
            xs = _surf(x)
            det = (f"{axes}{' INV' if xs[4] else ''}{' DEP-INV' if xs[7] else ''}, auth {xs[5]:.0f}%"
                   + (", deployed" if xs[6] else ""))
            changed = len(refs) == len(surf) and diff_config({"surfaces": [refs[j]]}, {"surfaces": [x]}, own=True)
            lvl = "fail" if changed else ("caution" if axes == "none" and not x[6] else "ok")
            rows.append((lvl, f"Surface {j + 1} {x[0]}", det + (" CHANGED" if changed else "")))
        if len(surf) > 16:
            rows.append(("ok", "Surfaces", f"+{len(surf) - 16} more"))
        wh = cfg.get("wheels") or []
        if wh:
            on = sum(1 for x in wh if x[1])
            broken = sum(1 for x in wh if x[2])
            rows.append(("fail" if broken else ("caution" if on < len(wh) else "ok"), "Reaction wheels",
                         f"{on}/{len(wh)} active" + (f", {broken} broken" if broken else "")))
        if cfg.get("gear") is not None:
            rows.append(("ok", "Gear", "down" if cfg["gear"] else "up"))
            rows.append(("caution" if cfg["brakes"] and flying else "ok", "Brakes", "on" if cfg["brakes"] else "off"))
        rot = cfg.get("rotors") or []
        if rot:
            from . import propulsion
            off = propulsion.motor_off(rot)
            rows.append(("caution" if off else "ok", "Props", props_detail(rot, cfg.get("props"))))
        ch = cfg.get("chutes") or []
        if ch:
            rows.append(("ok", "Chutes", f"{sum(1 for x in ch if x[2])} armed, {sum(1 for x in ch if x[1])} deployed of {len(ch)}"))
    if kind not in ("plane", "heli"):
        at = next((k for k, r in enumerate(rows) if r[1] == "Attitude"), -1) + 1
        rows[at:at] = ship_rows(s, ship, kind, flying)  # rockets / landers / rovers / stations / probes
    fuel = s.get("fuel")
    if fuel is not None and not (kind in ("rocket", "lander", "probe", "station") and (ship or {}).get("res")):
        rows.append(("fail" if fuel < FUEL_LOW else ("caution" if fuel < 0.15 else "ok"), "Fuel", f"{100 * fuel:.0f}%"))
    temp = s.get("temp")
    if temp is not None:
        rows.append(("fail" if temp[0] >= TEMP_ALERT else ("caution" if temp[0] >= TEMP_OK else "ok"), "Temperature",
                     f"{100 * temp[0]:.0f}% ({temp[1]})"))
    g = float(s.get("g") or 0.0)
    rows.append(("fail" if g > 7.0 else ("caution" if g > 5.0 else "ok"), "G-load", f"{g:.1f} g"))
    out = "blackout" in act
    rows.append(("fail" if out else "ok", "Pilot", f"{s.get('who') or 'none'} - {'BLACKED OUT' if out else ('conscious' if s.get('crew') else 'no crew')}"))
    if any(v != 1.0 for v in SIGN.values()):
        rows.append(("caution", "Control adapt", ", ".join(f"{k} inverted" for k, v in SIGN.items() if v != 1.0)))
    return rows


class _Watcher(threading.Thread):
    def __init__(self):
        super().__init__(daemon=True, name="emergency-watcher")
        self.conn = None
        self.last_scan = 0.0
        self.last_parts = None
        self.cfg = None
        self.was_engaged = False
        self.engaged_since = None
        self.probed = set()
        self.planeish = {}

    def run(self):
        while True:
            try:
                self.tick()
                time.sleep(POLL_S)
            except Exception as ex:  # noqa: BLE001  KSP closed, scene change, ...
                self.conn = None
                DET.reset()
                log.debug("emergency watcher: %s", ex)
                time.sleep(5.0)

    def tick(self):
        if self.conn is None:
            import krpc
            from . import config
            self.conn = krpc.connect(name="KSPChatBridge-emergency", address=config.KRPC_ADDRESS,
                                     rpc_port=config.KRPC_RPC_PORT, stream_port=config.KRPC_STREAM_PORT)
        if str(self.conn.krpc.current_game_scene).split(".")[-1] != "flight":
            DET.reset()
            with _lock:
                ALERTS.clear()
                SYSTEMS[:] = []
            _flags.update(reverse=False, level_until=0.0, safe=False)
            return
        try:
            if self.conn.krpc.paused:
                return
        except Exception:  # noqa: BLE001
            pass
        v = self.conn.space_center.active_vessel
        now = time.time()
        parts_all = v.parts.all
        n_parts = len(parts_all)
        scan = now - self.last_scan >= SCAN_S or n_parts != self.last_parts
        if scan:
            self.last_scan, self.last_parts = now, n_parts
        s = _sample(v, scan, self.conn.space_center)
        try:
            s["lost"] = PARTS.update(s["vid"], parts_all, v)
        except Exception as ex:  # noqa: BLE001
            log.debug("part tracker: %s", ex)
        if s["vid"] not in self.planeish:
            try:
                from . import ksp_actions
                self.planeish[s["vid"]] = bool(ksp_actions._planeish(v))
            except Exception:  # noqa: BLE001
                self.planeish[s["vid"]] = False
        try:
            from . import heli
            s["heli"] = heli.sample(v, self.conn.space_center)
        except Exception as ex:  # noqa: BLE001
            s["heli"] = None
            log.debug("heli sample: %s", ex)
        s["plane"] = self.planeish[s["vid"]] and not s["heli"]
        vessel_changed(s["vid"], v.name)
        try:
            _learn_liftoff(v, s)
        except Exception as ex:  # noqa: BLE001
            log.debug("liftoff learner: %s", ex)
        try:  # prop blade-pitch schedule + throttle -> torque while an autopilot flies a prop craft
            from . import propulsion
            s["rotors"] = propulsion.rotor_checks(v) if propulsion.classify(v)["rotors"] else None
            s["props"] = propulsion.group_sample(v, s["rotors"]) if s["rotors"] is not None else None
            did = propulsion.GOV.tick(v, s, _engaged(), now)
            if did:
                log.info("props: %s (spd %.0f)", did, float(s.get("speed") or 0.0))
        except Exception as ex:  # noqa: BLE001
            log.debug("prop governor: %s", ex)
        if s["cfg"] is not None:
            self.cfg = s["cfg"]
        handle(DET.tick(s), v, s)
        if s.get("crew_list") and DAMAGE.get("lost") and DAMAGE.get("vid") == s["vid"]:
            try:  # stopped with damage: the engineer offers repairs (once per damage list)
                from . import crew
                crew.speak_repair(s["vid"], s["crew_list"], [x[1] for x in DAMAGE["lost"]], s["sit"],
                                  float(s.get("speed") or 0.0))
            except Exception as ex:  # noqa: BLE001
                log.debug("crew repair offer: %s", ex)
        if s.get("crew_list"):
            try:  # 'are we there yet?' on quiet trips (never during / right after an emergency)
                from . import crew
                emerg = bool(DET.active) or bool(_live_alerts())
                crew.TALK.tick(now, s["vid"], s["sit"], s["crew_list"], s.get("who_full"), crew.trip_active(), emerg,
                               crew.trip_facts)  # small talk first: an emergency cuts it off at once
                crew.TRIP.tick(now, s["vid"], s["sit"], s["crew_list"], s.get("who_full"), crew.trip_active(), emerg,
                               crew.trip_facts)
            except Exception as ex:  # noqa: BLE001
                log.debug("crew trip chatter: %s", ex)
        if s["vid"] != getattr(self, "vid", None):
            self.vid = s["vid"]
            SIGN.update(pitch=1.0, roll=1.0, yaw=1.0)
            _flags["upside"] = False
            _flags["yaw_trim"] = 0.0
            _clear_alert("inverted_air")
            _clear_alert("inverted_ground")
            _flags["safe"] = False
        # keep idle while reversed with nothing engaged (the autopilots idle via shape())
        if _flags["reverse"] and not _engaged():
            try:
                if v.control.throttle > 0.0:
                    v.control.throttle = 0.0
            except Exception:  # noqa: BLE001
                pass
        with _lock:
            SYSTEMS[:] = build_systems(s, self.cfg, DET.ref_cfg)
        eng = _engaged()
        if eng and not self.was_engaged:
            self.engaged_since = now
            if s["vid"] not in self.probed:
                _PENDING_PROBE["want"] = True
        if not eng:
            self.engaged_since = None
        self.was_engaged = eng
        if _PENDING_PROBE["want"] and self._probe_ok(v, s):
            _PENDING_PROBE["want"] = False
            self.probed.add(s["vid"])
            for axis in ("roll", "pitch"):
                r = self._probe(v, axis)
                if r in ("inverted", "dead"):  # never adapt on one sample: the sign flip must be confirmed
                    r2 = self._probe(v, axis)
                    r = r if r2 == r else "unclear"
                probe_result(axis, r)

    def _probe_ok(self, v, s):
        from . import plane, protect
        if not _engaged() or s["sit"] != "flying" or protect.blackout() or DET.active or s.get("heli"):
            return False
        if self.engaged_since is None or time.time() - self.engaged_since < PROBE_SETTLE_S:
            return False  # let the autopilot settle first (it probed in the first second of a turning to_entry)
        if plane.active() and plane.STATUS.get("phase") in ("takeoff", "final", "flare", "rollout", "recover"):
            return False
        try:
            f = v.flight(v.orbit.body.reference_frame)
            return f.surface_altitude > PROBE_MIN_AGL and abs(f.roll) < PROBE_MAX_BANK and abs(f.vertical_speed) < 30.0
        except Exception:  # noqa: BLE001
            return False

    def _probe(self, v, axis):
        f = v.flight(v.orbit.body.reference_frame)
        get = (lambda: f.pitch) if axis == "pitch" else (lambda: f.roll)
        before = []
        for _ in range(4):
            before.append((time.time(), float(get())))
            time.sleep(0.05)
        PROBE.update(axis=axis, delta=PROBE_DELTA, until=time.time() + PROBE_T)
        during = []
        try:
            while time.time() < PROBE["until"]:
                time.sleep(0.05)
                during.append((time.time(), float(get())))
        finally:
            PROBE.update(axis=None, delta=0.0, until=0.0)
        res = eval_probe(PROBE_DELTA, before, during, wrap=axis == "roll")
        log.info("emergency: %s probe -> %s", axis, res)
        time.sleep(1.0)  # let the autopilot settle before the next axis
        return res


def start():
    """Start the watcher (once; no-op in offline tests)."""
    global _watcher
    if not AUTO or running():
        return
    _watcher = _Watcher()
    _watcher.start()