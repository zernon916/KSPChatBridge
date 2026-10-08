"""Takeoff throttle from the vessel's specs (Luke 2026-10-08: a very fast plane went to full power on the roll,
overspeeded and crashed into the water).

Before the roll the vessel's mass and available thrust (kRPC) give the thrust-to-weight ratio; the starting throttle
comes from that (high TWR -> low start, low TWR -> near full; never 0). During the roll / climb-out the throttle moves
in 5 % steps with 2-5 s waits for jet spool-up, and HOLDS while the speed is still changing, except when under the
stall margin (add power now) or over / heading over the cap (take power off). Speed comes only from the throttle; the
takeoff never trades altitude (pitch) for speed.
Pure logic (no kRPC in here except vessel_specs) so it is unit-tested offline: tests/test_takeoff_throttle.py.
"""
import math

STEP = 0.05            # Luke's standard throttle step
THR_MIN = 0.35         # lowest starting throttle (very high TWR); never 0
TWR_LO, TWR_HI = 0.4, 1.5   # TWR <= 0.4 -> 100 %, TWR >= 1.5 -> THR_MIN, linear in between (rounded to 5 %)
FLOOR_AIR = 0.30       # airborne floor while climbing out (Luke: >= 30 % in climb) ...
FLOOR_OVER = 0.10      # ... lower only when over / heading over the cap; never 0 in flight
WAIT_SPOOL = 4.0       # s after the start before the first change (jets spooling up from idle)
WAIT_ROLL = 3.0        # s between steps on the roll
WAIT_AIR = 4.0         # s between normal steps airborne (spool lag)
WAIT_URGENT = 2.0      # s between steps under the stall margin / over the cap
A_ROLL_MIN = 1.5       # m/s^2: slower than this on the roll (below Vr) -> more power (runway is finite)
A_ROLL_LOW = 0.8       # m/s^2: much slower than that (after the spool-up wait) -> full takeoff power at once
WEAK_LOW_SPEED = ("whiplash", "ramjet", "scramjet")   # engines with poor static / low-speed thrust: start at full
A_STEADY = 0.3         # m/s^2: |accel| below this = speed settled
LEAD_S = 5.0           # s look-ahead of the speed trend against the cap (spool lag)


def twr_of(mass_kg, thrust_n, g):
    if not mass_kg or mass_kg <= 0 or not g or g <= 0:
        return 0.0
    return max(0.0, float(thrust_n)) / (float(mass_kg) * float(g))


def vessel_specs(v, body=None):
    """Mass (t), available / max thrust (kN), surface gravity and TWRs of a kRPC vessel."""
    mass = float(v.mass)
    avail = float(v.available_thrust)
    try:
        maxt = float(v.max_thrust)
    except Exception:  # noqa: BLE001
        maxt = avail
    g = float((body or v.orbit.body).surface_gravity)
    return {"mass_t": mass / 1000.0, "avail_kn": avail / 1000.0, "max_kn": maxt / 1000.0, "g": g,
            "twr": twr_of(mass, avail, g), "max_twr": twr_of(mass, maxt, g)}


def weak_low_speed(titles):
    """True if any engine is a ramjet type (J-X4 Whiplash, ...) whose static thrust says little about the roll."""
    return any(w in str(t).lower() for t in titles or () for w in WEAK_LOW_SPEED)


def start_throttle(twr, weak=False):
    """Starting takeoff throttle for a thrust-to-weight ratio (unknown / 0 -> full, the old behavior). Ramjets
    (weak=True) start at full takeoff throttle - the cap rules still take power off near the speed cap."""
    if weak or not twr or twr <= TWR_LO:
        return 1.0
    if twr >= TWR_HI:
        return THR_MIN
    thr = 1.0 - (twr - TWR_LO) / (TWR_HI - TWR_LO) * (1.0 - THR_MIN)
    return round(max(THR_MIN, min(1.0, round(thr / STEP) * STEP)), 2)


class TakeoffThrottle:
    """Throttle governor for the roll and climb-out. update() returns the throttle to command (5 % steps)."""

    def __init__(self, start, now, v_target=200.0, v_cap=220.0):
        self.cmd = float(start)
        self.t_step = float(now) + WAIT_SPOOL - WAIT_ROLL  # first change only after the spool-up time
        self.v_target, self.v_cap = float(v_target), float(v_cap)
        self.acc = 0.0
        self._last = None  # (t, spd)
        self.why = "start"

    def _trend(self, now, spd):
        if self._last is not None:
            dt = now - self._last[0]
            if dt > 0.05:
                self.acc += ((spd - self._last[1]) / dt - self.acc) * min(1.0, dt / 1.5)
                self._last = (now, spd)
        else:
            self._last = (now, spd)
        return self.acc

    def update(self, now, spd, eas, airborne, vr, v_min_safe, accel=None):
        a = self._trend(now, spd) if accel is None else float(accel)
        if accel is not None:
            self.acc = a
        want, wait, floor, why = self.cmd, WAIT_AIR, FLOOR_AIR, "hold"
        over = eas > self.v_cap or eas + max(a, 0.0) * LEAD_S > self.v_cap
        if not airborne:
            floor = 0.05
            if eas > self.v_cap - 12.0:
                want, wait, why = self.cmd - STEP, WAIT_URGENT, "roll over cap"
            elif spd < vr and a < A_ROLL_LOW and now - self.t_step >= WAIT_ROLL and self.cmd < 1.0:
                self.cmd, self.t_step, self.why = 1.0, now, f"roll accel {a:.1f} < {A_ROLL_LOW} - full power"
                return self.cmd
            elif spd < vr and a < A_ROLL_MIN:
                want, wait, why = self.cmd + STEP, WAIT_ROLL, f"roll accel {a:.1f} < {A_ROLL_MIN}"
        elif spd < v_min_safe:
            want, wait, why = self.cmd + STEP, WAIT_URGENT, "under stall margin"
        elif over:
            want, wait, floor, why = self.cmd - STEP, WAIT_URGENT, FLOOR_OVER, "over cap"
        elif abs(a) > A_STEADY:
            want, why = self.cmd, "speed changing - hold"
        elif eas < self.v_target - 10.0:
            want, why = self.cmd + STEP, "below target"
        elif eas > self.v_target + 5.0:
            want, why = self.cmd - STEP, "above target"
        want = max(floor, min(1.0, want))
        if abs(want - self.cmd) >= STEP / 2 and now - self.t_step >= wait:
            self.cmd = round(max(floor, min(1.0, self.cmd + math.copysign(min(STEP, abs(want - self.cmd)),
                                                                          want - self.cmd))), 2)
            self.t_step = now
            self.why = why
        elif self.cmd < floor:
            self.cmd = floor
        return self.cmd

    def force_full(self, now, why):
        """Full takeoff power now (rotation assist at its limit and the nose still down: keep accelerating)."""
        if self.cmd < 1.0:
            self.cmd, self.t_step, self.why = 1.0, float(now), why
            return True
        return False

# ---- rotation + runway-end abort (Luke 2026-10-08: the Whiplash delta "Aeris 4A" ran the whole runway with ~20 %
# up elevator and never rotated - it went off the end at ~200 m/s and skimmed the water)
ROT_RAMP = 0.15        # elevator / s added while at/above Vr on the ground and the nose isn't rising
ROT_MAX = 0.8          # max extra rotation elevator (on top of the attitude loop's own command)
ROT_RATE_OK = 1.0      # deg/s: nose rising at least this fast = it is rotating, stop adding
ROT_RATE_HI = 3.0      # deg/s: rising faster than this (or past the target attitude) -> take some back
ROT_BACKOFF = 0.5      # / s taken back when rising too fast / past the attitude (avoid tail strike / over-rotation)
ROT_BLEED = 0.1        # / s once airborne: hand the extra over to the climb integrator smoothly
STOP_DECEL = 3.5       # m/s^2 assumed braking on the runway (conservative)
STOP_MARGIN = 100.0    # m


class RotateAssist:
    """Extra up-elevator for the rotation: ramps up (ROT_RAMP/s, max ROT_MAX) while at/above Vr on the ground and the
    nose is not actually rising (pitch-rate feedback), backs off when it rises fast or overshoots the attitude, bleeds
    away once airborne. Below Vr on the ground: 0."""

    def __init__(self):
        self.extra = 0.0

    def update(self, on_ground, spd, vr, pitch_err, q, dt):
        if on_ground and spd < vr:
            self.extra = 0.0
        elif q > ROT_RATE_HI or pitch_err < 0.0:
            self.extra = max(0.0, self.extra - ROT_BACKOFF * dt)
        elif on_ground:
            if q < ROT_RATE_OK and pitch_err > 1.0:
                self.extra = min(ROT_MAX, self.extra + ROT_RAMP * dt)
        else:
            self.extra = max(0.0, self.extra - ROT_BLEED * dt)
        return self.extra

    @property
    def maxed(self):
        return self.extra >= ROT_MAX - 1e-6


# Luke flies the Aeris 4A off by hand at full power, pulling back at ~120 m/s (the default Vr was ~56): a delta can
# need far more than 1.25 x the guessed stall speed. The liftoff speed is learned per craft (hand or autopilot, by
# the emergency watcher) and the next takeoff rotates just before it.
VR_CACHE = "liftoff_speeds"   # settings key: craft key -> learned liftoff speed (m/s)
VR_LEARN_FACTOR = 0.92        # start the rotation a little before the learned liftoff speed
V_LIFT_GUESS = 140.0          # uncached craft: assume it may need up to this to fly before calling the takeoff hopeless
ROT_ALLOW_S = 3.0             # runway (seconds at speed) allowed for the rotation itself once the speed is there
RISE_PITCH = 3.0              # deg above the roll attitude = rotated
RISE_VS = 1.5                 # m/s climbing = rotated / wheels off
LIFT_MIN_SPD = 25.0           # learner: below this it's no takeoff (hop / taxi bump)
LIFT_START_SPD = 15.0         # learner: the ground run must have started below this (not a landing bounce)
LIFT_CONFIRM_S = 8.0          # learner: airborne this long (no touch back) = a real liftoff ...
LIFT_MIN_AGL = 30.0           # ... and climbed above this (running off the runway end into a water skim isn't one)
LIFT_MAX_SPD = 180.0          # learner: never learn a liftoff faster than this (a runaway roll, not a rotation)


def learned_liftoff(keys):
    from . import settings
    cache = settings.get(VR_CACHE) or {}
    return next((float(cache[k]) for k in keys if cache.get(k)), None)


def record_liftoff(keys, spd):
    from . import settings
    cache = dict(settings.get(VR_CACHE) or {})
    for k in keys:
        cache[k] = round(float(spd), 1)
    settings.put(VR_CACHE, cache)


def rotate_speed(vs0, learned=None):
    """Vr: just before the learned liftoff speed if known, else 1.25 x the (cached or default) stall speed."""
    return max(35.0, VR_LEARN_FACTOR * learned) if learned else max(1.25 * vs0, 35.0)


def lift_need(vr, learned=None):
    """Speed the craft may need before it flies (for the runway-end decision)."""
    return float(learned) if learned else max(vr, V_LIFT_GUESS)


def rotation_achieved(q, dpitch, vs, on_ground):
    """Rotated: nose rising > ROT_RATE_OK deg/s, nose RISE_PITCH above the roll attitude, climbing, or wheels off."""
    return (not on_ground) or q > ROT_RATE_OK or dpitch > RISE_PITCH or vs > RISE_VS


def stop_distance(spd):
    """Runway needed to stop from the actual speed (idle + brakes, conservative STOP_DECEL, + STOP_MARGIN)."""
    return float(spd) ** 2 / (2.0 * STOP_DECEL) + STOP_MARGIN


def can_continue(spd, accel, remaining, v_need):
    """True if, at the measured acceleration, the craft reaches v_need and has ROT_ALLOW_S to rotate before the end."""
    if accel is None or accel < 0.3:
        return False
    v = max(float(v_need), float(spd))
    return (v * v - spd * spd) / (2.0 * accel) + ROT_ALLOW_S * v <= remaining


def runway_abort(on_ground, spd, remaining, rising, accel=None, v_need=None):
    """Abort the takeoff (idle + brakes; on the ground only) only when the runway left truly can't support
    continuing: on the ground, not rotating, at the last point it can still stop (stopping distance from the actual
    speed), AND it can't reach the speed it needs (learned liftoff, else V_LIFT_GUESS) plus room to rotate before the
    end. remaining None (not on a known runway) -> never."""
    if not on_ground or remaining is None or rising or spd < 5.0:
        return False
    if remaining > stop_distance(spd):
        return False          # can still stop later: keep accelerating
    return v_need is None or not can_continue(spd, accel, remaining, v_need)


class LiftoffLearner:
    """Watches situation + speed (the emergency watcher's 1 s samples, hand-flown or autopilot) for a real takeoff: a
    ground run that started below LIFT_START_SPD, then flying for LIFT_CONFIRM_S without touching back down.
    feed() returns ("liftoff", spd) on the first airborne sample, then ("learned", spd) once confirmed (airborne
    LIFT_CONFIRM_S and above LIFT_MIN_AGL) or ("not_learned", spd) if it stayed low / was too fast."""

    def __init__(self):
        self.reset()

    def reset(self, vid=None):
        self.vid, self.ground_min, self.last_ground, self.pending = vid, None, None, None

    def feed(self, t, sit, spd, vid=None, agl=None):
        if vid != self.vid:
            self.reset(vid)
        if sit in ("landed", "pre_launch"):
            self.ground_min = spd if self.ground_min is None else min(self.ground_min, spd)
            self.last_ground, self.pending = spd, None
            return None
        if sit != "flying":
            self.reset(vid)
            return None
        if self.ground_min is not None:
            lift = None
            if self.ground_min < LIFT_START_SPD and spd >= LIFT_MIN_SPD:
                lift = 0.5 * (spd + max(self.last_ground or spd, LIFT_MIN_SPD))  # 1 s samples: between the two
                self.pending = (t, lift)
            self.ground_min = self.last_ground = None
            return ("liftoff", lift) if lift else None
        if self.pending and t - self.pending[0] >= LIFT_CONFIRM_S:
            lift, self.pending = self.pending[1], None
            ok = lift <= LIFT_MAX_SPD and (agl is None or agl >= LIFT_MIN_AGL)
            return ("learned" if ok else "not_learned", lift)
        return None


def runway_remaining(along, length, forward):
    """Metres of runway ahead: along = distance from threshold a toward b, forward = rolling a -> b."""
    return max(0.0, (length - along) if forward else along)


# ---- safe ceiling (Luke 2026-10-08): the autopilot works out its own safe max altitude from the craft's specs.
# Simple and conservative: jet thrust falls off roughly with air density (and intake air with it), so the ceiling
# is where the sea-level air-breathing thrust-to-weight has lapsed to ~0.1 (about what level flight above the stall
# margin needs at a typical L/D): h = LAPSE_H * ln(TWR_sl / 0.1), clamped 3-18 km. Rocket engines on board ->
# 20 km (wings / control authority, not air, limit it). No engines / no intakes -> 3 km.
CEIL_MIN, CEIL_JET_MAX, CEIL_ROCKET = 3000.0, 18000.0, 20000.0
CEIL_PROP = 5000.0     # propellers (rotor blades / electric props): no thrust reading; props lose bite in thin air
TWR_LEVEL_MIN = 0.1
LAPSE_H = 8000.0       # m: effective e-folding height of jet thrust (density^0.7 with Kerbin's ~5.6 km scale height)
_CEIL_CACHE = {}


def ceiling_from_specs(twr_air, jets, rockets, intake_air=True, props=False):
    """Conservative safe ceiling (m MSL) from the sea-level air-breathing TWR and the engine mix."""
    if rockets:
        return CEIL_ROCKET
    if not jets or not intake_air or not twr_air or twr_air <= TWR_LEVEL_MIN:
        return CEIL_PROP if props else CEIL_MIN
    h = LAPSE_H * math.log(twr_air / TWR_LEVEL_MIN)
    return math.floor(max(CEIL_MIN, min(CEIL_JET_MAX, h)) / 500.0) * 500.0


def vessel_ceiling(v, body=None):
    """(ceiling m MSL, info) from kRPC: engines (air-breathing or not), their sea-level thrust, mass, intakes.
    Cached per craft (name + mass); (None, {}) if kRPC can't tell (then no ceiling check)."""
    import logging
    try:
        key = (v.name, round(float(v.mass) / 100.0))
        if key in _CEIL_CACHE:
            return _CEIL_CACHE[key]
        from . import propulsion
        engines = list(v.parts.engines)
        kinds = [propulsion.engine_kind(e.propellant_names) for e in engines]
        jets = [e for e, k in zip(engines, kinds) if k == "jet"]
        rockets = [e for e, k in zip(engines, kinds) if k == "rocket"]
        props = propulsion.classify(v)["props"]
        thrust = 0.0
        for e in jets:
            try:
                thrust += float(e.max_thrust_at(1.0))  # sea-level (1 atm) thrust, whatever altitude we're at now
            except Exception:  # noqa: BLE001
                thrust += float(e.max_thrust)
        try:
            intake = float(v.resources.max("IntakeAir")) > 0
        except Exception:  # noqa: BLE001
            intake = True
        g = float((body or v.orbit.body).surface_gravity)
        twr = twr_of(float(v.mass), thrust, g)
        ceil = ceiling_from_specs(twr, len(jets), len(rockets), intake, props)
        info = {"twr_air": round(twr, 2), "jets": len(jets), "rockets": len(rockets), "intake": intake}
        if props:
            info["props"] = True
        logging.getLogger("kspchat").info("safe ceiling %.0f m for %s (%s)", ceil, v.name, info)
        _CEIL_CACHE[key] = (ceil, info)
        return ceil, info
    except Exception:  # noqa: BLE001
        return None, {}
