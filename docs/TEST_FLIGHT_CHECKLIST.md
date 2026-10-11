# AICS test-flight checklist

Printable checklist for the 5 plane test flights in [AUTOPILOT_OVERHAUL.md](AUTOPILOT_OVERHAUL.md) ("What the 5 plane test flights must confirm").

For every craft: run **LEARN** first, then a standard circuit - takeoff, climb, 2 turns, ferry leg, approach, land.
**Pass** = every item confirmed **and** the logs show curve values used (not defaults).

Tick `[x]` in the Pass or Fail box for each item, then write anything notable on the Notes line.

---

## 1. Heavy

- [ ] Pass   [ ] Fail - Mass scaling as fuel burns
- [ ] Pass   [ ] Fail - slow roll lead
- [ ] Pass   [ ] Fail - long decel and descent planning
- [ ] Pass   [ ] Fail - flare under 2 m/s

Notes: ______________________________________________

---

## 2. Normal (A300)

- [ ] Pass   [ ] Fail - Baseline: all Phase 1 success criteria

Notes: ______________________________________________

---

## 3. Fighter

- [ ] Pass   [ ] Fail - High roll_rate does not overshoot
- [ ] Pass   [ ] Fail - g/bank cap holds
- [ ] Pass   [ ] Fail - max-speed clamp
- [ ] Pass   [ ] Fail - no PIO

Notes: ______________________________________________

---

## 4. Twin prop

- [ ] Pass   [ ] Fail - Throttle lag handling
- [ ] Pass   [ ] Fail - asymmetric/engine-out behaviour logged
- [ ] Pass   [ ] Fail - low stall approach

Notes: ______________________________________________

---

## 5. Single prop

- [ ] Pass   [ ] Fail - Torque/yaw trim
- [ ] Pass   [ ] Fail - low-speed turns
- [ ] Pass   [ ] Fail - short field flare and flaps

Notes: ______________________________________________
