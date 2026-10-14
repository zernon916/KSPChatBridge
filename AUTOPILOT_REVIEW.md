# Autopilot audit — curve-driven flight across craft classes

**Scope:** Full AP audit + fixes #1–#14 (✅). Traced `NativeFlightController.cs`, `NativeLearn.cs`, `PilotPolicy.cs` (`SpeedHold`, `ThrottleGate`, `LearnGuards`, LE / caps), `FlightMissions.cs` (`CraftClass`, `ApproachProfile`, `RunwayMission`, `TurnOnset`, `PitchShaper`, takeoff), `LearnFlight.cs` / `PlaneProfile`, `PerfCurves.cs` / `CurveBook`, `NativeFlaps.cs`, plus `FlightPolicy.cs` helpers they call.

**Goal checked:** Fly most aircraft from learned curves (stall, sink, turn envelope, accel/decel, throttle lag, climb, fuel burn), not hardcoded constants.

**Call order (airborne hold/land):** `LearnTick` (if LEARN) → mode mission (`takeoff` / `landing` / ferry) → altitude→VS → bank (onset / LE / SafeBank) → pitch shape → `shold.Step` → LE/stall throttle floors → `UpdateCap` / speed-cap cut → `HoldThrottleOut` → `LearnApply` → `gate.Step`.

---

## Critical / high

### 1. ✅ Approach banks fight learned envelope via decelerating `SafeBank`
- **Where:** `NativeFlightController.cs:678–680`, `PilotPolicy.cs:602–606`; envelope set at `UpdateCap` `456`.
- **Problem:** Join/entry bank is sized from `ApproachProfile.SafeBank` / `PhaseBank` (learned), then every frame `PilotPolicy.SafeBank` clamps to **5°** below 1.3× stall or **10°** when `decel > 4` or within +20 m/s of that floor — and approach is *always* decelerating on the schedule.
- **In flight:** Planned 35–50° joins collapse to 5–10°; arcs don’t fit; go-arounds / wide intercepts, especially heavies and fighters after LEARN.
- **Fix (Y):** While `mode == "landing"` and `runway.TurnBank > 0` (or `ApproachProfile.SafeBank != null`), skip `PilotPolicy.SafeBank` (same idea as `bankOverride` for LEARN). Keep LE / stall emergency.

### 2. ✅ Non-landing bank ceiling ignores turn envelope
- **Where:** `FlightPolicy.cs:9` (`BankLimit` 10° if speed&gt;250 else 20°); `NativeFlightController.cs:667–670`; `CraftClass.MaxBank` heavy 25°.
- **Problem:** `fly_to` / hold heading / ferry use `BankLimit`, not `profile.SafeBankAt(speed)`. Envelope is wired only for landing `landBank` and chart planning.
- **In flight:** A LEARN’d 45° jet is held to 20° (10° fast); long course corrections; fighters feel “mushy,” heavies already capped at 25° by class.
- **Fix (Y):** `blim = min(CraftClass.MaxBank(...), SafeBankAt(v) * EnvelopeUse)` when envelope exists; else keep `BankLimit`. Apply in the hold bank path and plan bank defaults.

### 3. ✅ Climb / cruise / takeoff handoff ignore learned climb & econ speeds
- **Where:** Takeoff handoff `NativeFlightController.cs:531` (`1.8 * stall`, clamp 80–200); roll target `539` (`speed = 200`); hold pitch uses VS loop only (`662–703`). `BestClimbSpeed` / `EconThrottle` only used inside LEARN (`LearnFlight.cs:41–45`, cruise phase) and ABOUT text. Ferry uses econ for *cruise speed* (`1128–1130`) but not throttle.
- **Problem:** After rotate, AP chases a generic 1.8× stall (or 200 m/s on the roll). No Vy from profile; no econ throttle for level cruise.
- **In flight:** Props claw at too-high IAS; jets linger slow; cruise burns wrong fuel; climb porpoises while pitch chases altitude instead of Vy.
- **Fix (Y):** On climbout complete / climb hold: `speed = profile?.BestClimbSpeed(stall, mass) ?? 1.8*stall`. For level cruise when player asked econ or ferry cruise: set `speed` from scaled `EconSpeed` and optionally seed throttle toward `EconThrottle` once (then PI). Fallback unchanged when no profile.

### 4. ✅ Approach speed schedule clamps stall into 40–120 m/s
- **Where:** `FlightMissions.cs:277` `SaneStall`; used by `AppSpeed` / `HiSpeed` / `FinalSchedule` / join `Speed` (`275–292`, `968`).
- **Problem:** Light fighters (true Vs ~25–35) are scheduled as Vs≥40 → final ~54, join ~60+. Fast jets with Vs&gt;120 get compressed. Profile `StallAt(mass)` is not the schedule input after this clamp.
- **In flight:** Light craft final is too hot; go-around “too fast” gates fire; or slow jets scheduled too slow vs energy.
- **Fix (Y):** Prefer `profile.StallAt(mass)` (and flap stall when flaps≥2) with a wide sanity band (e.g. 15–180) or only clamp when no profile / low learner confidence. Keep `HeavyStallFloor` for heavies without LEARN.

### 5. ✅ Approach decel distance prefers live `DecelLearner` / mass estimate over profile curve
- **Where:** `NativeFlightController.cs:586`; ferry correctly uses `profile.DecelAt` (`1131`). `SaveProfile` seeds `Decel.Learn` (`NativeLearn.cs:156`) but landing tick still `Decel.Estimate` first.
- **Problem:** Schedule start distance can disagree with LEARN idle decel / `Curves.Decel`, especially after mass change or empty learner bucket.
- **In flight:** Starts slowing too late (hot gate / go-around) or too early (dragging in).
- **Fix (Y):** `runway.DecelA = profile?.DecelAt(mass) ?? Decel.Estimate(...) ?? MassDecel(...)`, then still `/ sqrt(shold.K)`. Same source as ferry.

---

## Medium

### 6. ✅ Flare vertical speed hardcoded; sink map only drives throttle
- **Where:** `FlightMissions.cs:976–977` (flare sink 1–1.9 m/s); throttle blend `NativeFlightController.cs:733–736` + `SinkThrottle` (`NativeLearn.cs:173`).
- **Problem:** LEARN sink table maps throttle↔sink near 1.2× stall; flare still commands a fixed VS. Without a table, throttle is pure speed PI.
- **In flight:** Floats / hard touchdowns when the map’s idle sink ≠ 1.5 m/s; map unused for path.
- **Fix (Y):** Keep VS command for path; if `ThrottleForSink` exists, prefer it in flare (already) and optionally ease `DesiredVs` toward the sink the map associates with current throttle (or target 1–2 m/s via map invert). No map → keep today’s defaults.

### 7. ✅ Roll / pitch inner-loop gains ignore learned rates
- **Where:** Roll `NativeFlightController.cs:723` (`.014` / `.01`); pitch `.022` / `.016` (`716`); `TurnOnset.RollRate = 30` (`FlightMissions.cs:339`). `Perf.RollRate` / `PitchRate` / curves only shown on ABOUT.
- **Problem:** Heavies and fighters share the same surface P/D; onset roll cap doesn’t use measured roll rate.
- **In flight:** Sluggish heavies or twitchy light craft; choppy g at turn entry when onset ≠ airframe.
- **Fix (Y):** Scale roll/pitch P by `clamp(learnedRate / refRate, 0.5, 1.5)` when `Perf.RollRate`/`PitchRate` known; set `TurnOnset` roll limit from learned rate (with floor). No profile → current constants.

### 8. ✅ `SpeedHold.SetLag` does not retune Smith lead from learned lag
- **Where:** `PilotPolicy.cs:83–88` (`Lead` from mass/TWR/spool only); `SetLag` (`78`) only sets `Lag`. Used in `Step` pending term (`97–99`). Load/save call `SetLag` (`NativeLearn.cs:155,168`).
- **Problem:** Lag model delay updates; derivative *Lead* still from spool estimate. Big turbofan lag with low spool estimate under-anticipates.
- **In flight:** Speed PI oscillates or steps late after LEARN measured 3–8 s lag.
- **Fix (Y):** In `SetLag`, also `Lead = max(Lead, 0.5 * lag)` (or `Lead = 1.5*(K-1) + lag`). Re-call after `Configure` so Configure doesn’t wipe it — order: `Configure` then `SetLag` (already true on bind).

### 9. ✅ Low-energy override vs approach / hold near 1.2–1.3× stall
- **Where:** `PilotPolicy.cs:156`; NFC `683–697`, `738`. LEARN sink LE suppressed (`learnSinkLe`); turn LE bank clamp skipped via `learnTurnLe` (already). Normal landing excludes only short final / flare / rollout.
- **Problem:** Intercept/final outside short fix still arms LE when speed dips &lt;1.2× stall → 90% power + ±15° bank while the schedule wants 1.35× and join banks.
- **In flight:** Power surges and bank cuts on final intercept; fights `DesiredSpeed`.
- **Fix (Y):** Also suppress LE bank clamp (or full LE) when `mode == "landing" && runway != null && Distance < LongFix` (or while `ApproachSpeedLocked`). Keep stall emergency &lt;1.15×.

### 10. ✅ Binary craft class (mass/span/parts) mis-labels props and small airliners
- **Where:** `FlightMissions.cs:50–67` (`heavy` if mass≥40 or span≥30 or parts≥150).
- **Problem:** Class drives bank 25°, roll 6°/s, JoinG 1.15, pitch envelope 15°, gain scales — not LEARN curves.
- **In flight:** A 35 t regional jet gets “light” aggressiveness; a part-heavy fighter gets “heavy” mush.
- **Fix (Y):** If envelope + roll rate exist, prefer curve-driven limits; use class only as fallback. Optional: classify with wing loading / LEARN roll rate.

### 11. ✅ Join / final speed caps (80–200 / HiSpeed≤150) fight fast LEARN’d craft
- **Where:** `ApproachProfile.MinSpeed/MaxSpeed` `265`; `HiSpeed` min(150, 2.3×Vs) `280`.
- **Problem:** Fast jets with Vs 80 want join 120 but HiSpeed cap 150 is OK; join MaxSpeed 200 OK; problem is more the SaneStall floor (#4) and MinSpeed 80 for ultralights.
- **In flight:** Very slow LEARN’d planes cannot schedule below 80 m/s join.
- **Fix (Y):** `MinSpeed = max(1.3*stall, 40)` when profile present; keep 80 only as no-profile default.

### 12. ✅ Takeoff rotate / climb pitch not using profile liftoff beyond Vr helper
- **Where:** `TakeoffMission.RotateSpeed` uses liftoff (`FlightMissions.cs:190–200`); climbout pitch from mission, not `BestClimbPitch`. Handoff speed issue is #3.
- **Problem:** Weak spot: fixed climb pitch schedule vs LEARN Vy.
- **Fix (Y):** After gear-up / climbout, switch `speed` to `BestClimbSpeed` (#3); optional slight pitch bias from speed error like LEARN `BestClimbPitch`.

### 13. ✅ Fuel-burn / endurance curves unused in ferry or bingo
- **Where:** `Curves.FuelBurn`, `Perf.CruiseFracPerS`, `ReserveFrac` exist (`LearnFlight.cs` / `PerfData`); ferry doesn’t call them for altitude/speed choice.
- **Problem:** Ferry picks altitude from climb rate ×0.6 and dive sink (`1137–1139`) but not fuel-optimal cruise.
- **In flight:** Wrong cruise altitude/speed for range; bingo tools may be separate.
- **Fix (Y):** When `HasCruise`, prefer `EconSpeed` (already) and cap ferry distance warning via `ReserveFrac(dist)`. Don’t block land without it.

### 14. ✅ `AccelAt` / climb accel unused for spool or speed slew
- **Where:** `PlaneProfile.AccelAt`; `SpeedHold` TargetSlew from mass only (`PilotPolicy.cs:85`).
- **Problem:** LEARN accel never schedules how fast the IAS target may ramp.
- **Fix (Y):** `TargetSlew = clamp(AccelAt(m), 0.5, 3)` when known.

---

## Phase weak spots (summary)

| Phase | Weak spot |
|-------|-----------|
| **Takeoff** | Ground speed target 200; handoff 1.8× stall not Vy; pitch shaper helps g but climbout energy wrong (#3). |
| **Climb** | Altitude-hold VS loop, not LEARN climb curve / Vy; `ClimbRateAt` only for ferry TOD. |
| **Cruise** | Econ speed used on ferry; econ *throttle* unused; hold uses PI only. |
| **Ferry** | Good: econ, decelAt, dives→sink, ceiling. Weak: fuel curve; descent pitch floor toggles −12/−5. |
| **Descent** | Dive table → sink OK; no sink-map throttle for idle descent. |
| **Approach** | Envelope vs `SafeBank` fight (#1); SaneStall (#4); decel source (#5). |
| **ILS** | LOC/GS coupling solid; maxSink heuristics; heavy gentler — not curve-based. |
| **Flare** | Hardcoded VS (#6); sink map throttle helps when present. |
| **Rollout** | Reversers + derotate; LEARN rollout distance not used to plan brake point. |
| **Taxi** | Separate path; not curve-driven (OK). |

---

## Overrides (cross-check)

| Override | False / fight risk |
|----------|-------------------|
| Low-energy | Yes on approach intercept (#9); LEARN sink/turn mostly guarded. |
| Stall emergency &lt;1.15× | Appropriate; wins over PI. |
| G / CapHot | LEARN turn excluded via `LearnGuards.CapHot` — good. Heat still caps. |
| Speed cap &lt;6 km | Uses `PlaneCap(MaxLevel)` when known — good; else mass heuristic. |
| Terrain / approach floor | Can raise VS vs GS when low AGL — intentional; watch double with LE. |
| Heavy floor recover | Wings level + climb — can abort a tight hold turn near terrain. |

---

## Throttle lag / spool

| Path | Anticipates lag? |
|------|------------------|
| `SpeedHold` (hold / approach speed) | Yes — Smith pending × `Lag`, step-and-wait ≥ `Lag` (`PilotPolicy.cs:97–119`). |
| `ThrottleGate` | Rate only (PI/test/emergency), not lag preview. |
| LEARN apply | Gate `"test"` / PI; turn uses PI. |
| Takeoff ground ramp | Fixed 25%/s — OK. |
| Flare sink throttle | Steps through gate after LearnApply/hold — lag felt as slow spool to map throttle; no extra preview. |

**Gap:** ~~`SetLag` without Lead retune (#8)~~ fixed — `SetLag` raises `Lead`; profile lag/accel loaded on bind.

---

## Fallbacks (no profile)

- Stall: lift estimate + learner + `HeavyStallFloor` / liftoff bound (`LearnedStall`).
- Decel: `MassDecel` / live `DecelLearner`.
- Bank: `BankLimit` + craft class.
- Cap: mass/class `PlaneCap`.
- Ferry sink: default from `DescentSink` empty dives → 25.
- Flare throttle: speed PI only.
- These are reasonable; the bug is **not switching to curves when present** (#1–5).

---

## Time warp / scene / save-load

- Physics warp ≤3× in atmo while Active (`NativeFlightController.cs:394–395`).
- LEARN rails warp: one-shot notice (`LearnGuards.RailsWarpNotice`).
- `vessel.packed` → Fly returns (`476`) — AP pauses in warp/unload; integrators not explicitly frozen (minor).
- `Bind()` on vessel switch: `Stop`, re-`LoadProfile` by **vessel name** — rename loses profile; same name different craft collides.
- Settings: atomic write + `.bak` (`207–216`); load failure disables save — good.
- Profiles in `settingsData["plane_profiles"]` via MiniJson — OK.

---

## Dead / duplicate / stale

- `FlightExtrasPolicy.MaxBank` duplicates `FlightPolicy.BankLimit` (hold_pattern / extras).
- ~~ABOUT shows accel/roll/pitch curves the AP never reads (#7, #14)~~ — AP now scales gains / TargetSlew from those rates.
- Older dual-throttle LEARN issues largely fixed; see `REPORT.7.9.MD` for LEARN-step residuals.

---

## Fix check (Y = works in real call order)

| # | Y/N | Notes |
|---|-----|--------|
| 1 Skip SafeBank on landing envelope | **Y** | Runs after bank compute, before pitch; doesn’t touch LearnApply/gate. |
| 2 Envelope blim in hold | **Y** | Same bank path; UpdateCap already sets `SafeBank` delegate each 1 s. |
| 3 Vy / econ on handoff & cruise | **Y** | Set `speed` / optional throttle seed before `shold.Step`; gate still smooths. |
| 4 Widen / bypass SaneStall with profile | **Y** | Pure schedule inputs into `runway.Step`; no loop fight. |
| 5 DecelA from profile first | **Y** | Assigned before `runway.Step` each tick. |
| 6 Flare map VS optional | **Y** | Only changes `DesiredVs` / existing SinkThrottle path. |
| 7 Gain scale from rates | **Y** | Local multiply on P gains / TurnOnset const. |
| 8 SetLag updates Lead | **Y** | Call order Configure→SetLag already. |
| 9 LE suppress on approach | **Y** | Same flag pattern as `learnSinkLe`. |
| 10 Class fallback after curves | **Y** | Policy-only. |
| 11 MinSpeed from stall | **Y** | Schedule-only. |
| 12 Climb pitch bias | **Y** | Optional with #3. |
| 13 ReserveFrac ferry warn | **Y** | Advisory. |
| 14 TargetSlew from AccelAt | **Y** | Inside SpeedHold.Configure/Step. |

No N remaining for the listed fixes.

---

## Top 5 (all ✅)

1. ✅ **Stop `PilotPolicy.SafeBank` from crushing LEARN’d approach banks while decelerating** (#1).  
2. ✅ **Use turn envelope for hold / enroute bank limits, not only landing** (#2).  
3. ✅ **Drive climbout & cruise from `BestClimbSpeed` / econ; stop hardcoding 200 / 1.8× stall** (#3).  
4. ✅ **Feed approach schedule from real `StallAt` / flap stall — fix `SaneStall` 40–120 clamp** (#4).  
5. ✅ **One decel source: `profile.DecelAt` for approach distance, same as ferry** (#5).

---

## What’s already in good shape

- Single throttle output path (`gate`) with PI / test / emergency rates.  
- `SpeedHold` Smith lag + anti-windup + mass inertia `K`.  
- Ferry uses econ speed, `DecelAt`, dive-based sink, climb curve for TOD.  
- Landing uses sink-map throttle in final/flare when present.  
- Envelope → `PhaseBank` / roll-in lead on chart legs.  
- LEARN guards for CapHot G and sink LE.  
- Flaps from approach phase + learned flap limit.  
- Settings atomic save; profile mass scaling helpers exist (underused by AP).

---

*Audit date: 2026-10-11. Fixes #1–#14 implemented and checked off.*
