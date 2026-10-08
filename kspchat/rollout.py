"""Gentle autoland rollout braking (Luke 2026-10-08: his prop plane - main gear behind the CoM, narrow track - flipped
over on the landing roll when braked too hard).

kRPC's Control.brakes is on/off (the Brakes action group), so braking is a duty cycle: on for duty x PERIOD of every
PERIOD seconds. No brakes until every wheel is down and settled for SETTLE_S; then the duty ramps from DUTY_MIN to the
current maximum over RAMP_S while the pitch is watched against the settled rollout attitude. A nose dip of more than
NOSE_DIP deg, a fast nose-down rate, or a growing bank (tip-over risk) releases the brakes until the craft has settled
again (attitude back within RESETTLE_DIP, rates calm for RESETTLE_S); each release lowers the maximum duty a step.
Thrust reversers (reversers.py) keep working independently.
"""

SETTLE_S = 1.5        # all wheels down and calm this long before the first brake pulse
PERIOD = 0.5          # s per duty cycle
DUTY_MIN, RAMP_S = 0.2, 4.0
DUTY_MAX0, DUTY_STEP, DUTY_FLOOR = 1.0, 0.2, 0.3   # max duty; lowered a step after each release, never below the floor
NOSE_DIP = 2.5        # deg below the settled attitude -> release
NOSE_RATE = -3.0      # deg/s nose-down pitch rate -> release
ROLL_MAX = 4.0        # deg bank on the ground -> release (tip-over)
ROLL_RATE = 6.0       # deg/s roll rate -> release
RESETTLE_DIP, RESETTLE_S = 1.0, 0.5
CALM_VS, CALM_Q = 1.0, 1.0


class RolloutBrakes:
    def __init__(self):
        self.settle_t = None      # wheels down + calm since
        self.ref = None           # settled rollout pitch attitude (deg)
        self.ramp_t = None        # duty ramp start
        self.released = None      # reason while released, else None
        self.calm_t = None
        self.duty_max = DUTY_MAX0
        self.duty = 0.0
        self.why = "waiting for all wheels down"
        self.releases = 0

    def _calm(self, vs, q, roll, p):
        return abs(vs) < CALM_VS and abs(q) < CALM_Q and abs(roll) < ROLL_MAX and abs(p) < ROLL_RATE

    def update(self, now, wheels_down, pitch, q, roll, p=0.0, vs=0.0):
        """-> brakes on (bool) for this instant. pitch / roll deg, q / p deg/s (nose-up / right positive)."""
        if self.ref is None:  # not settled yet: no brakes at all
            if wheels_down and self._calm(vs, q, roll, p):
                self.settle_t = now if self.settle_t is None else self.settle_t
                if now - self.settle_t >= SETTLE_S:
                    self.ref, self.ramp_t, self.why = pitch, now, "ramping in"
            else:
                self.settle_t, self.why = None, "waiting for all wheels down"
            self.duty = 0.0
            return False
        dip = self.ref - pitch
        bad = ("nose dipping %.1f deg" % dip if dip > NOSE_DIP else
               "nose dropping %.1f deg/s" % q if q < NOSE_RATE else
               "bank %.1f deg" % roll if abs(roll) > ROLL_MAX else
               "rolling %.1f deg/s" % p if abs(p) > ROLL_RATE else
               "a wheel lifted" if not wheels_down else None)
        if bad:
            if self.released is None:
                self.releases += 1
                self.duty_max = max(DUTY_FLOOR, self.duty_max - DUTY_STEP)
            self.released, self.calm_t, self.duty, self.why = bad, None, 0.0, "released: " + bad
            return False
        if self.released is not None:  # wait until it has settled again, then ramp in from the bottom
            if dip < RESETTLE_DIP and wheels_down and self._calm(vs, q, roll, p):
                self.calm_t = now if self.calm_t is None else self.calm_t
                if now - self.calm_t >= RESETTLE_S:
                    self.released, self.ramp_t, self.why = None, now, "ramping in again"
            else:
                self.calm_t = None
            if self.released is not None:
                self.duty = 0.0
                return False
        frac = min(1.0, (now - self.ramp_t) / RAMP_S)
        self.duty = DUTY_MIN + (self.duty_max - DUTY_MIN) * frac
        if frac >= 1.0:
            self.why = "braking %.0f%%" % (100 * self.duty)
        return self.duty >= 1.0 - 1e-9 or ((now - self.ramp_t) % PERIOD) < self.duty * PERIOD