"""Offline test of the AICS Flight Plan (no KSP, no network):
parsing (canonical lines, AI markdown, Luke's own sentences), templates, and the step runner flying the
"Cruise + circle" template (takeoff -> climb -> cruise -> 3 laps around the field -> land) against a tiny fake world.
  python tests/test_flightplan.py"""
import math
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from kspchat import flightplan as fp, hold, plane, spots  # noqa: E402

# ---- parsing
for kind in fp.TEMPLATES:
    steps, errs = fp.parse(fp.template(kind))
    assert steps and not errs, (kind, errs)
canon = ["takeoff", "climb 3000 m agl vs 50", "descend 1000 m msl", "cruise hdg 090 alt 3000 m agl speed 180 for 3 min",
         "cruise for 20 km", "circle 3 laps left bank 15", "circle 5 laps right bank 20 around field radius 12 km",
         "fly to Island Airfield alt 4000 m speed 200", "land", "land Runway 09 tg 2", "land Island Airfield",
         "wait 30 s", "ascent 80 km inc 6", "circularize", "transfer Mun", "warp to soi", "course correction 30 km",
         "deorbit 30 km", "land KSC", "land here", "chutes", "science", "science no transmit", "stage"]
steps, errs = fp.parse("\n".join(canon))
assert not errs, errs
assert [fp.fmt(s) for s in steps] == canon, [fp.fmt(s) for s in steps]
assert fp.normalize("Take off, Climb at 50 m/s and circle ksp 5 times, then land on runway") == \
    ["takeoff", "climb 3000 m agl vs 50", "circle 5 laps right bank 15 around KSC", "land"]
assert fp.normalize("take off, climb to 8k speed at 320 m/s. I want you to climb at a rate of 40 m/s then circle the "
                    "airfield at max speed, 5 laps, and land again.") == \
    ["takeoff", "climb 8000 m agl vs 40 speed 320", "circle 5 laps right bank 15 around field", "land"]
md = ("**Flight plan**\n1. **Takeoff** \u2014 Align on the runway, full throttle.\n"
      "2. **Climb** \u2014 hold +50 m/s until 3000 m AGL.\n3. **Circle** \u2014 circle the field 4 times.\n"
      "4. **Landing** \u2014 lower gear, flare, brake on the runway.")
assert fp.normalize(md) == ["takeoff", "climb 3000 m agl vs 50", "circle 4 laps right bank 15 around field", "land"], \
    fp.normalize(md)
_, errs = fp.parse("climb at 50 m/s\nhello\nfly to Nowhere")
assert [e[0] for e in errs] == [1, 2, 3], errs
assert fp.start("hello there").startswith("Can't fly")


# ---- runner against a fake world
class World:
    def __init__(self):
        self.t = time.time()
        self.sit, self.alt, self.hdg, self.spd = "landed", 0.0, 90.0, 0.0
        self.lat, self.lon = -0.0486, -74.70           # on the KSC runway, heading 090
        self.hold_on = self.plane_on = False
        self.tgt_alt, self.roll, self.hdg_t, self.airborne_at, self.land_at = 300.0, None, None, None, None
        self.calls = []

    def tool(self, name, **a):
        self.calls.append((name, a))
        if name == "plane_hold":
            if a.get("engage") is False:
                self.hold_on = False
                return "Autopilot holds disengaged."
            self.hold_on = True
            if a.get("altitude_m") is not None:
                self.tgt_alt = a["altitude_m"]
            if a.get("roll") is not None:
                self.roll = a["roll"]
            if a.get("heading") is not None:
                self.hdg_t, self.roll = a["heading"], None
            if self.sit == "landed" and self.airborne_at is None:
                self.airborne_at = self.t + 25.0
                hold.STATUS.update(phase="takeoff")
            return "ok"
        if name in ("land_plane", "land_at_spot"):
            self.plane_on, self.land_at = True, self.t + 300.0
            return "Autoland engaged"
        return "?"

    def set_targets(self, **kw):
        if kw.get("heading") is not None:
            self.hdg_t, self.roll = kw["heading"], None

    def sleep(self, dt):
        self.t += dt
        if self.airborne_at and self.t >= self.airborne_at and self.sit == "landed":
            self.sit, self.spd, self.airborne_at = "flying", 150.0, None
            hold.STATUS["phase"] = "hold"
        if self.plane_on and self.t >= self.land_at:
            self.plane_on, self.sit, self.spd = False, "landed", 0.0
            plane.STATUS["result"] = "Landed on runway 09: grade A"
        if self.sit == "flying" and self.hold_on:
            self.alt += max(-40 * dt, min(40 * dt, self.tgt_alt - self.alt))
            if self.roll is not None:
                self.hdg = (self.hdg + math.copysign(3.0 * dt, self.roll)) % 360
            elif self.hdg_t is not None:
                e = fp._wrap(self.hdg_t - self.hdg)
                self.hdg = (self.hdg + max(-3 * dt, min(3 * dt, e))) % 360
            self.lat, self.lon = spots.offset(self.lat, self.lon, self.hdg, self.spd * dt)
        hd = self.hdg_t if self.roll is None else None
        hold.STATUS.update(alt_agl=round(self.alt), alt_msl=round(self.alt + 70), hdg=round(self.hdg),
                           hdg_des=None if hd is None else round(hd), speed=self.spd, lat=self.lat, lon=self.lon,
                           hdg_err=None if hd is None else round(fp._wrap(hd - self.hdg), 1))

    def vinfo(self):
        return {"sit": self.sit, "plane": True, "lat": self.lat, "lon": self.lon, "body": "Kerbin", "R": 600000.0,
                "spd": self.spd}


class FakeSettings:
    d = {}

    @classmethod
    def get(cls, k, default=None):
        return cls.d.get(k, default)

    @classmethod
    def put(cls, k, v):
        cls.d[k] = v


w = World()
events = []
fp._tool, fp._vinfo, fp._now, fp._sleep = w.tool, w.vinfo, (lambda: w.t), w.sleep
fp.settings = FakeSettings
fp.science.post_event = events.append
fp.guard.busy = lambda: None
hold.active = lambda: w.hold_on
hold.set_targets = w.set_targets
plane.active = lambda: w.plane_on
r = fp.start(fp.template("circle"))
assert r.startswith("Flying the plan: 5 steps"), r
fp._thread.join(60)
assert not fp.active(), "runner hung"
print("\n".join(events))
assert fp._state["result"].startswith("complete"), fp._state["result"]
assert any("3 lap(s) around KSC" in e for e in events), events
names = [c[0] for c in w.calls]
assert names[0] == "plane_hold" and names[-1] == "land_plane" and ("plane_hold", {"engage": False}) in w.calls, names
assert abs(w.alt - 2000) < 80, w.alt
print("flight plan test OK")

# in-place circling turns from cruise flight (roll hold, laps from the heading change), then Stop is respected
w2 = World()
w2.sit, w2.alt, w2.spd, w2.hold_on = "flying", 1500.0, 150.0, True
events.clear()
fp._tool, fp._vinfo, fp._now, fp._sleep = w2.tool, w2.vinfo, (lambda: w2.t), w2.sleep
hold.active = lambda: w2.hold_on
hold.set_targets = w2.set_targets
plane.active = lambda: w2.plane_on
r = fp.start("circle 2 laps left bank 15\ncruise for 1 min")
fp._thread.join(60)
assert fp._state["result"].startswith("complete"), (fp._state["result"], events)
assert ("plane_hold", {"roll": -15.0}) in w2.calls, w2.calls
assert abs(fp._wrap(w2.hdg - 90)) < 6, w2.hdg       # rolled out on the entry heading
print("in-place circle OK")

# orbit / station-keep / taxi steps parse and round-trip
orb = ["set ap 2863.33 km", "set pe 32 km", "inclination 0", "apsis longitude -74.56", "match plane",
       "station keep lon -74.5", "taxi to Runway 09 start speed 6", "taxi to KSC Pad"]
steps, errs = fp.parse("\n".join(orb))
assert not errs and [fp.fmt(s) for s in steps] == orb, ([fp.fmt(s) for s in steps], errs)
assert fp.normalize("aerobrake at 32 km") == ["set pe 32 km"]
print("orbit/taxi steps OK")
