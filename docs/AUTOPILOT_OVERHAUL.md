# Autopilot overhaul: curve-driven for most craft

Goal: the autopilot reads each craft's **learned profile** (LEARN data) and flies from those numbers rather than fixed per-class constants. If no profile exists, it falls back to today's conservative defaults and says it is doing so.

Data already available (example: `PluginData/native_settings.json` -> `plane_profiles["Stearwing A300"]`):
`m0, liftoff, stall, stall_alt_loss, climb_accel, climb_vs, accel, decel, lag, turns[[bank, g, ?, alt_dv, ?, mass, throttle]], dives, sink_table, samples (cruise_speed, max_level_speed, roll_rate, pitch_rate, turn_throttle_N, sink_thr@N), perf.ground_roll`, plus `stall_speeds`, `liftoff_speeds`, `stall_learn` (regression sums).
Code to build on: `LearnFlight.cs`, `NativeLearn.cs`, `PerfCurves.cs` (in progress), `ThrottleGate` (one throttle path), `ChartStore.cs`, `NativeFlaps.cs`, `HelicopterPolicy.cs`, `RotorSpool.cs`, `KscRoverPolicy.cs`, `NativeVerticalLanding.cs`, `NativeOrbital.cs`, `MechJebLink.cs`.

## Rules (every phase)
- [ ] One small chunk per commit, with offline tests for the curve math.
- [ ] Every curve lookup is **mass-scaled** (profile mass vs current mass) and **clamped** to the learned range. No extrapolation beyond 10%.
- [ ] Missing/old profile -> defaults + one log line "no profile: defaults".
- [ ] Log which curve value drove each decision (so test flights can be checked).

## Phase 0: profile access layer
- [ ] `PerfCurves` API: `Get(craft, key, mass)` with interpolation, mass scaling, staleness (date / part-count signature).
- [ ] Profile version field and migration for older profiles.
- Success: unit tests pass for A300 data; a craft with no profile returns defaults.

## Phase 1: planes
1. **Speed/throttle**: feed-forward throttle from `turn_throttle_N` / cruise samples, with PI trim on top (through ThrottleGate). Use `lag` for lead.
   - Success: speed holds within ±3 m/s in level cruise, without throttle hunting.
2. **Speed envelope**: min speed = 1.3 × stall (mass-scaled); max = 0.95 × `max_level_speed`; climb at the learned `climb_vs`.
   - Success: never below 1.3 Vs outside the flare.
3. **Turns / lead-turns**: bank limit = 85% of the learned max safe bank; radius and roll-in lead from `roll_rate` and true speed; add throttle in turns from the turns table.
   - Success: rolls out within ±3° of the target heading; altitude within ±30 m.
4. **Approach / charts**: chart sizes, glide speed and descent planning from `decel` and `sink_table`; entry chart picked by bearing (already done).
   - Success: stabilised by 1000 m out (on speed ±5, on glideslope).
5. **Flare / flaps**: flap schedule by speed vs stall; flare height from sink rate and `pitch_rate`.
   - Success: touchdown at under 2 m/s vertical speed, within the first third of the runway.
6. **Ferry / descent**: ferry cruise at `econ_speed`/throttle once learned (currently null, so learn it); top-of-descent from `decel` and `dives`.
   - Success: no overspeed in the descent; arrives at the entry fix on altitude.

## Phase 2: helicopters
- [ ] Learn: hover collective, spool time (`RotorSpool`), max pitch-for-speed table, turn radius at speed, stop distance.
- [ ] Pitch-for-speed controller using the learned table; hover hold using the collective curve.
- [ ] Turn radius limits for path following; emergency stop uses the learned stop distance + margin.
- [ ] Spool-aware: no aggressive commands until RPM is in band.
- Success: hover ±1 m; stop within the predicted distance +20%; no rotor overspeed.

## Phase 3: rovers
- [ ] Learn: braking distance vs speed, max lateral g before wheel lift (tip), slope climb/descend limits.
- [ ] Speed limit from the curvature of the path ahead vs the tip curve; braking starts from the learned distance.
- [ ] Slope: limit speed downhill and set the torque target uphill.
- Success: no tip-overs on the KSC tour; stops within predicted distance +15%.

## Phase 4: rockets (replace MechJeb step by step; keep MechJebLink optional)
- [ ] Learn/derive: TWR vs stage and altitude (thrust curves, ISP vs pressure), steering response (time to rotate X°).
- [ ] Ascent profile: gravity turn shaped by TWR; limit AoA from steering response.
- [ ] Burn timing: start time from the rocket equation + learned thrust ramp; split long burns.
- [ ] Landing: suicide-burn start with margin = max(10%, sensor lag × v) plus throttle headroom ≥ 15%.
- Success: orbit within 5 km of target Ap/Pe; node burns within 2 m/s dV error; landing at under 3 m/s without MechJeb installed.

## Phase 5 (optional): Kerbal Wind Tunnel cross-check
- [ ] Manual only: Luke compares KWT envelope plots (stall, max level speed, climb rate) with the AICS profile in the editor.
- [ ] AICS never reads KWT; there is no dependency. Note any gaps over 10% in CURSOR_TODO.md.

## What the 5 plane test flights must confirm
Run LEARN first, then a standard circuit (takeoff, climb, 2 turns, ferry leg, approach, land).

| Craft | Must confirm |
|---|---|
| Heavy | Mass scaling as fuel burns; slow roll lead; long decel and descent planning; flare under 2 m/s |
| Normal (A300) | Baseline: all Phase 1 success criteria |
| Fighter | High roll_rate does not overshoot; g/bank cap holds; max-speed clamp; no PIO |
| Twin prop | Throttle lag handling; asymmetric/engine-out behaviour logged; low stall approach |
| Single prop | Torque/yaw trim; low-speed turns; short field flare and flaps |

Pass = every row confirmed + logs show curve values used (not defaults).
