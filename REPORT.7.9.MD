# Learn audit — Step 7 (max safe turn) & Step 9 (sink map)

**Scope:** Review only (no code changes). Traced `LearnFlight.cs`, `NativeLearn.cs` (partial `NativeFlightController`), `PerfCurves.cs` / `CurveBook`, `PilotPolicy.cs` (`RateMeter`, `ThrottleGate`, `SpeedHold`, `LowEnergy`, `ClimbBackPitch`, `FlightPathAngle`), `NativeFlightController.cs` (hold / pitch / bank / gate path), `FlightMissions.cs` (`ApproachProfile.SafeBank` / `TurnPitch` / `PhaseBank`), `NativeFlaps.cs` (learn flap demand).

**Step numbering:** UI / `StepNo` is 1-based. Step **7** = `turnprep` / `turn` / `recover` (idx &lt; 100). Step **9** = `sinkprep` / `sinkdown` / `sinkmap` / `sinkclimb`. Checklist label `StepNames[6]` still says “Turns 5-30 deg” while `Banks` goes to 70°.

**Note:** Several issues from the prior `REPORT.7.9.MD` are already fixed in tree (G spike vs abort, TurnPitch, SpeedTarget-only throttle, filtered `nearLimit`, envelope NaN→null, one sink target per step, `vsF` reset, filtered `SinkLimit`, altitude-scheduled sink floor). This report is against **current** code only.

---

## Path map

| Step | Phases | NFC / helpers | Stored data |
|------|--------|---------------|-------------|
| 7 | `turnprep` → `turn` → `recover` | `LearnTick` / `LearnApply`, `SpeedTarget`→speed PI, `directPitch`/`directBank`+`bankOverride`, `learnPull` gLim, `RateMeter`, `ApproachProfile.TurnPitch` | `P.Turns`, `P.Envelope`, `Curves.Turn*` / `CourseRate` |
| 9 | `sinkprep` → `sinkdown` → `sinkmap` ↔ `sinkclimb` | Same; `TestThrottle` + learn `Throttle` via gate `"test"`; `SpeedPitch` / FPA; `SinkLimit` / `SinkMaxAt`; flaps via `learn.Flaps` | `P.Sink` / `SinkFlaps`, `Curves.Sink`, `ThrottleLag` |

Consumers (not in the loop, but eat the data): `ApproachProfile.PhaseBank` (85% of envelope), `SinkThrottle` → flare/final blend.

---

## Step 7 — Max safe turn

### High

1. **`NativeFlightController.cs:691–696` + `PilotPolicy.cs:156` — Low-energy override clamps bank to ±15° during LEARN turns**  
   - **What’s wrong:** Approach pass flies ~1.5× stall. LE enters below **1.2×** stall (hysteresis exit 1.3×). While LE is active, NFC forces `bank = Clamp(bank, -15, 15)` even though Learn commands 40–70° via `directBank` / `bankOverride`.  
   - **Why it matters:** A small speed bleed mid-ladder fights the stress test: bank never reaches command → `ReachTimeout` / pitch-tracking / false “limit”, or envelope ends early.  
   - **Fix:** While `learn != null` and phase is `turn` / `turnprep` / turn-`recover`, skip the LE bank clamp (keep LE power if desired, or rely on step-7’s own 1.3× stall stop).

2. **`LearnFlight.cs:636–647` + `412–416` + `377` — Pitch “not tracking” still compares attitude to a noisy Level+TurnPitch command**  
   - **What’s wrong:** `badPitch` uses `|Pitch - i.Pitch| > 8` for 8 s. Command is `Level` (raw `i.Vs`, ±15°) + `TurnPitch` (up to +8°), then NFC `PitchShaper` rate-limits toward it. Heavy craft / roll-in lag routinely exceed 8° nose error without a real altitude failure. First-bank fail with `passBest == null` still **invalidates the whole step** (`Redo.Add(7)`, envelope not saved).  
   - **Why it matters:** False invalidate after a good cruise settle, especially on heavies.  
   - **Fix:** Gate on altitude / VS error (same idea as `TurnAltLoss`), or widen tolerance while `|Bank|` high / during roll-in; do not wipe the step on pitch alone when speed PI is healthy.

### Medium

3. **`LearnFlight.cs:230` vs `232` — Re-running step 7 does not clear `Turns` / `Envelope`; step 9 clears `Sink`**  
   - **What’s wrong:** Manual `StartTest(7)` resets pass state but appends new rows onto prior `P.Turns` / `P.Envelope`. `StartTest(9)` clears the sink table.  
   - **Why it matters:** Redo / second LEARN pass leaves stale or duplicate envelope points; `SafeBankAt` interpolates across mixed runs → wrong approach banks.  
   - **Fix:** On starting step 7 (and on invalidate), clear or replace envelope/turns for that run (mirror sink clear), or version by date/pass.

4. **`LearnFlight.cs:659–661` + `321–322` — `ReachTimeout` (20 s) starts the 20 s sample window before bank is reached**  
   - **What’s wrong:** `Reached` treats timeout as “reached”. Sampling then averages `TurnRate` / G while still rolling in. `directBank` also skips `TurnOnset` (`NativeFlightController.cs:670`), so command is a step, but the airframe may not follow in 20 s.  
   - **Why it matters:** Understated turn rate / odd radius; envelope looks worse than sustained capability.  
   - **Fix:** Extend roll-in wait for learn, or require `|bank - cmd| < 2.5` before sampling (fail the bank on timeout instead of sampling incomplete).

5. **`NativeFlightController.cs:454–456` — `CapHot` when `geeForce > 0.9 * StructG` shrinks speed cap mid-turn**  
   - **What’s wrong:** Near the intentional TurnStop band, CapHot can cut throttle via `SpeedCapThrottle` while the turn PI is trying to hold `SpeedTarget`.  
   - **Why it matters:** Extra speed bleed → earlier `slow` / LE / false limit.  
   - **Fix:** Ignore CapHot (or raise threshold) while `learnPull` / turn phases.

6. **`LearnFlight.cs:668` — Envelope speed tag is target `vT`, not measured speed**  
   - **What’s wrong:** `passBest[7]` is the commanded pass speed. Actual IAS may be lower when the bank is recorded.  
   - **Why it matters:** `SafeBankAt(speed)` can allow that bank at a higher speed than was truly sustained.  
   - **Fix:** Store mean / end speed for the segment.

### Low

7. **`LearnFlight.cs:442` — Checklist name “Turns 5-30 deg” vs `Banks` to 70°**  
   - Operator expects a mild check; this is a structure probe. Rename to “Max safe turn / envelope”.

8. **`LearnFlight.cs:533` — `turnprep` sub-progress always says “pass 2: slowing to 1.5× stall”**  
   - Misleading on cruise pass (`turnPass == 0`). Show `passSpeed` / pass index.

9. **`LearnFlight.cs:324` — `passBad` unused; `GuessThr` / `down` unused**  
   - Dead state from older designs. Remove or wire.

10. **`PilotPolicy.cs:711–715` — `RateMeter` zeros rates after a &gt;2 s UT gap**  
    - Hitch / warp gap → short stretch of 0°/s in turn averages. Prefer hold-last or skip sample.

### Step 7 — verified correct (one line each)

- Global G abort during turn/turnprep/turn-recover uses raw spike only at **1.0×** `GLimit` (`TurnSpikeG`); soft stop at **0.8× filtered** (`TurnStop` / `gF`) so the ladder can save `passBest` (`LearnFlight.cs:293,558–561,659`).  
- Stress abort in turn is `TurnSpikeStress` 0.97 vs 0.9 elsewhere (`558–560`).  
- Throttle authority is `SpeedTarget` only; learn `Throttle` is NaN so `LearnApply` does not fight the PI (`634`, `NativeLearn.cs:63,142`).  
- `ThrottleNotTracking` ignores a rising lever and recovering speed trend (`380–384`).  
- Pitch command includes load-factor back-pressure: `Level + ApproachProfile.TurnPitch` (`377,635`; `FlightMissions.cs:330`).  
- NFC `learnPull` widens pitch gLim for turn phases (`NativeFlightController.cs:706–707`); `bankOverride` skips slow/decel bank clamp (`NativeLearn.cs:65–66`).  
- Bank ladder 10…70°, 20 s dwell after reach, two passes (cruise then ~1.5× stall) (`294,323,627–691`).  
- On limit/slow/alt-loss with a prior good bank: save `Envelope` from `passBest`, continue; failed banks still logged in `Turns` / curves (`672–684`).  
- Envelope NaN radius/roll-in serialize as `null`, round-trip as NaN (`89,105`).  
- `RateMeter.WrapRate` + `FlightPolicy.Wrap`: 359→1 = +2°/s (`PilotPolicy.cs:705`; `FlightPolicy.cs:8`).  
- Radius `v / (ω_deg * π/180)` in metres; divide-by-zero guarded (`rate > .01`) (`666`).  
- Phase change resets pitch/VS integrators and elevator (`NativeLearn.cs:51–55`).  
- SKIP keeps partial turn data and marks step 7 (`160–179`).  
- Approaches use 85% of learned `SafeBankAt` (`FlightMissions.cs:301–307`).

---

## Step 9 — Sink map

### High

11. **`LearnFlight.cs:408` vs `726–728` — `limThr` / `limT` not reset when resuming after `sinkclimb`**  
    - **What’s wrong:** `Go("sinkmap")` resets `vsF`, `overAt`, `recThr` but **not** `limThr`. After a deep sink near 4 km, `SinkLimit` winds `limThr` up; climb-back then resume keeps that floor → throttle stuck high vs the 1% ramp.  
    - **Why it matters:** Post-climb map points share an artificial throttle floor; approach `ThrottleForSink` reads too much power for a given sink.  
    - **Fix:** Clear `limThr` (and `limT`) on `sinkdown`/`sinkmap` entry and on sinkclimb resume (same place as `vsF` reset).

### Medium

12. **`LearnFlight.cs:232,537,712–716,347` — Prep / UI / map speed factors disagree (1.25 / 1.3 / 1.2)**  
    - **What’s wrong:** Start text “1.25× stall”; prep exit `Speed <= 1.3×`; map `SpeedPitch` default **1.2×**; unused `SinkPitch = 12`.  
    - **Why it matters:** Setup speed ≠ measurement speed; maps hard to reproduce across craft.  
    - **Fix:** One documented factor for prep + map; use or delete `SinkPitch`.

13. **`LearnFlight.cs:753–757` — Lag defaults to `RampWait` (5 s) when VS never moves 0.2 m/s**  
    - **What’s wrong:** `lagS` stays NaN → recorded lag = 5 s even if the step did nothing.  
    - **Why it matters:** Pollutes `ThrottleLag` / per-row lag used for spool expectations.  
    - **Fix:** Store NaN / omit lag curve point when `!lagDone`.

14. **`LearnFlight.cs:556–561` — Full LEARN G/stress/fuel abort still active in sink phases**  
    - **What’s wrong:** Sink is not in the `turning` exception. Climb-back (`ClimbBackPitch` up to ~30°) or pitch-for-speed can trip 0.9× G / 0.9 stress and abort the whole flight.  
    - **Why it matters:** Loses a partial sink table (unless SKIP).  
    - **Fix:** On abort in sink*, finish the step with partial data / redo flag instead of wiping the flight; or soften abort during sinkclimb only.

15. **`NativeFlightController.cs:690` + `PilotPolicy.cs:156` — Hold speed variable forced to 1.3× stall; LE arms near map speed**  
    - **What’s wrong:** Even with `holdSpeed = false`, NFC raises `speed` to `MinSafeSpeed`. Map flies ~1.2× via pitch-for-speed; LE can arm below 1.2×. LearnApply overwrites throttle target, so power usually still follows the map, but LE side-effects (logs, bank clamp, pitch assists when `directPitch` drops) add noise.  
    - **Why it matters:** Edge of the stall band is where the map stops (`1.15×`); LE chatter near that edge.  
    - **Fix:** Disable LE (or LE bank/pitch extras) while learn phase starts with `sink`.

### Low

16. **`LearnFlight.cs:298–301` — `GuessThr` / `down` sweep helpers never fed**  
    - Dead path; map uses idle cut + 1% ramp only. Remove clutter.

17. **Time warp:** Dwells / `RampWait` / `SinkCap` use Planetarium UT; atmosphere physics warp capped at 3× when AP active (`NativeFlightController.cs:394–395`, `PilotPolicy.WarpIndexCap`). Rails warp while “Active” is still a foot-gun — no learn-specific lock.

### Step 9 — verified correct (one line each)

- Prep climbs to ~5 km (`SinkAlt`), then `sinkdown` at idle until filtered sink &gt; 10.5 m/s for 3 s (`712–745`).  
- Soft floor **off** in `sinkdown` (`limThr = 0`); **on** in `sinkmap` / prep via `SinkLimit` on **filtered** `vsF` (`332–336,741,752`).  
- Altitude schedule: ~28 m/s allowed above 4.5 km, 15 m/s at 4 km (`SinkMaxAt`, `339–343`) — matches “25–30 up high” intent.  
- Map +1% every 5 s (`RampWait`); one target per settled throttle step via `recThr` (`750–758`).  
- `vsF` reset to current VS on `sinkdown`/`sinkmap` entry (`408`).  
- Below 4 km → `sinkclimb` full power `ClimbBackPitch`, resume same phase keeping `stepThr` (`726–728,736`).  
- 10 min cap keeps partial points (`SinkCap`, `734`).  
- Rows store sink target, throttle, lag, **mass**, pitch; `Curves.Sink.Add(sink, mass, thr, speed, flaps)` (`757–758`).  
- `ThrottleForSink` interpolates table and mass-scales (`PlaneProfile` `51–70`).  
- Optional flaps-full pass (`SinkDone` / code 91); `NativeFlaps` reads `learn.Flaps` (`NativeFlaps.cs:40`).  
- Stop under 1.15× stall with points kept (`735`).

---

## Cross-cutting

18. **`NativeLearn.cs:142` + `NativeFlightController.cs:746–748` — Dual throttle paths are intentional now**  
    - Turn: PI only. Sink: learn `Throttle` + gate `"test"` (1/s). Correct split; keep it.  
    - Remaining risk is LE / CapHot / MinSafeSpeed side channels above, not LearnApply itself.

19. **Abort / timeout / warp summary**  
    - Aborts: fuel &lt; 40%, part lost, stress, G (turn spike rules above).  
    - Timeouts: turnprep 180 s, reach 20 s, bank dwell 20 s, sinkprep/down 300 s, map 600 s, recover 12 s.  
    - Warp: UT-based timers; physics ≤3× in atmo while flying; no learn-only warp ban.

20. **`FlightMissions.cs` consumers** — Bad envelope / sink rows propagate into join bank and flare throttle; data bugs in 7/9 are landing bugs later.

---

## Top 3 fixes

1. **Don’t let low-energy clamp LEARN turn banks to ±15°** — skip LE bank (and preferably CapHot) during turn phases so the ladder can reach the real limit and save the envelope. (`NativeFlightController.cs:691–696`, turn phases)  
2. **Clear / replace `Turns`+`Envelope` on step-7 start (and reset `limThr` on sinkmap entry/resume)** — stop stale envelope and post-climb throttle floor from corrupting saved curves. (`LearnFlight.cs:230`, `408`, `726–728`)  
3. **Retire nose-error invalidate as a whole-step killer** — judge turn pitch by altitude/VS hold (or widen during roll-in); keep throttle-fault logic as-is. (`LearnFlight.cs:636–647`)

---

*Audit date: 2026-10-11. Review only — report file rewritten; no other files edited.*

---

## Proposed fixes (concrete, not applied)

Yes — suggested patches below, keyed to the finding numbers. Prefer small, local changes; no redesign.

### Step 7

**1. LE bank clamp during turns**  
In `NativeFlightController` around the low-energy block (~691), when `learn != null` and phase is `turn` / `turnprep` / (`recover` && turn idx): skip `bank = Clamp(bank, -15, 15)`. Optionally still allow LE throttle floor (turn already uses speed PI). Cleaner: `bool learnTurn = learn != null && (learn.Phase == "turn" || learn.Phase == "turnprep" || (learn.Phase == "recover" && …)); if (lowEnergy && !learnTurn) bank = Clamp…`.

**2. Pitch tracking invalidate**  
Replace `badPitch` attitude compare with altitude/VS hold fault, e.g. accumulate when `a0 - i.Alt > 40` or `|i.Vs| > 8` for ≥8 s while bank reached; clear when holding. Keep current “pitch fail + `passBest == null` → invalidate” only if you retain a loose attitude check (`BadPitchDeg` ≥ 15 while `|Bank| ≥ 30`, or ignore until `!double.IsNaN(reachAt)`). Do not `Redo.Add(7)` on pitch alone when `ThrottleNotTracking` is false and alt loss &lt; `TurnAltLoss`.

**3. Clear Turns/Envelope on step-7 start**  
In `StartTest(7)` (and the auto entry into `turnprep` from `rates` if that path should be a fresh envelope): `P.Turns.Clear(); P.Envelope.Clear();` (or remove only rows for this session). On invalidate (`Redo.Add(7)`), also clear envelope added this pass / leave `Turns` as diagnostic — pick one policy and document it. Mirror step 9’s clear.

**4. ReachTimeout sampling**  
Leave shared `Reached()` alone (dives use it). In `case "turn"` only, **before** `Reached` / any `thrSum`/`trSum` accumulate:

```text
bool bankOk = Math.Abs(Math.Abs(i.Bank) - b) < 2.5;
if (!bankOk && i.T - t0 >= ReachTimeout) {
  // same end-of-bank fail path as nearLimit/slow/sinking (save passBest, next pass / recover)
  // do NOT call Reached, do NOT set reachAt, do NOT Turns.Add a one-frame average
  ...; return;
}
if (!nearLimit && !Reached(i, bankOk)) return;
```

Must `return` before the existing `if (double.IsNaN(reachAt)) reachAt = i.T` line (that line would start a bogus `Seg`). Optional: turn-only timeout const 45 s in that check only.

**5. CapHot mid-turn**  
Where `CapHot` is set (~455): `CapHot = heat > .85 || (vessel.geeForce > .9 * StructG && !learnPull)`. Or skip `SpeedCapThrottle` cut when `learnPull`.

**6. Envelope speed tag**  
When building `passBest` / `Envelope` row, store mean or end `i.Speed` (new accumulator or `spdF`) instead of `vT` in the speed slot; keep `vT` in a note if useful.

**7. Checklist label**  
`StepNames[6] = "Max safe turn / envelope"` (or similar).

**8. turnprep sub-progress**  
`return (turnPass == 0 ? "cruise" : "approach") + " settle " + vT.ToString("0") + " m/s (" + i.Speed.ToString("0") + ")"` using the same `vT` as the phase.

**9. Dead fields**  
Delete `passBad`, `GuessThr`, `down` (and `SinkPitch` if still unused after #12) in a cleanup pass.

**10. RateMeter gap**  
On `t - pt > MaxGap`: do not zero rates; re-seed `pt/ph/…` and return (hold last). Or set rates to NaN and skip adding that frame in turn averages (`if (!double.IsNaN(i.TurnRate)) trSum += …`).

### Step 9

**11. Reset limThr on sink entry/resume**  
In `Go` where `ph == "sinkdown" || ph == "sinkmap"`: add `limThr = 0; limT = double.NaN;` next to the `vsF` reset. Also set them when `sinkclimb` resumes before `Go(r)`.

**12. One speed factor**  
Introduce `const double SinkSpeedFactor = 1.25` (or 1.2). Prep exit: `Speed <= SinkSpeedFactor * stall` (with a small tolerance). Map: `SpeedPitch(i, SinkSpeedFactor)`. Align StartTest / SubProgress strings. Delete or wire `SinkPitch`.

**13. Lag only when measured**  
Clear stale lag on **both** ramp and resume:
- `if (i.T - prevT >= RampWait) { …; lagDone = false; lagS = double.NaN; }` (today only `lagDone = false`)
- sinkclimb resume (~728): also `lagS = double.NaN` next to `lagDone = false`
When recording: keep numeric table lag `lagDone ? lagS : RampWait`; curve `if (lagDone) P.Curves.ThrottleLag.Add(lagS, …)` (equivalent to clearing `lagS` + existing `!IsNaN` guard). Do **not** store NaN in `Sink[][2]` (`FromDict` drops the row if lag is JSON null).

**14. Softer sink abort**  
Do **not** call `SinkDone` (stall/flaps). Partial `Sink` rows already survive `Finish(true,…)`. Prefer one of:
- **A (end step, keep table):** for G/stress only while `Phase.StartsWith("sink")`, skip `Finish`; `Redo.Add(9)`; then `Go("stallprep", i, msg)` — **not** bare `Go("wait")`. In manual mode `ManualRoute` sees stallprep outside `ActiveSteps(9)` and clears `activeTest` → wait/climbback; in auto, stall runs with `Sink` intact. Fuel/part-lost still `Finish`.
- **B (fewer false aborts):** while `Phase` is `sinkclimb` or climbing `sinkprep` (or all `sink*`), use spike thresholds (`TurnSpikeG` / `TurnSpikeStress`) or skip G abort on climb-back only.

**15. LE / MinSafeSpeed during sink**  
When learn phase starts with `sink`: do not force `speed = Max(speed, MinSafeSpeed)`; set `lowEnergy = false` or skip LE bank/pitch extras (throttle already overwritten by `LearnApply`). Same `learnSink` flag pattern as `learnTurn`.

**16. Dead GuessThr/down**  
Remove with #9.

**17. Warp**  
Optional: if `learn != null && !Done`, clamp warp like AP atmosphere cap, or `ChatWindow.Notice` once when rails warp engaged. Low priority if pilots already stay on physics warp.

### Cross-cutting / top 3 mapping

| Top 3 | Implement via |
|-------|----------------|
| 1 LE bank | Finding **1** (+ **5** CapHot, **15** sink LE) |
| 2 Clear envelope + limThr | Findings **3** + **11** |
| 3 Pitch invalidate | Finding **2** |

Suggested apply order: **1 → 11 → 3 → 2 → 5 → 12 → 13**, then nits **7/8/9/16**.

---

## Fix check (Y = will fix the issue if applied as noted / N = will not)

Re-checked against current call order (`LearnTick` → hold/LE/pitch → `UpdateCap` → `HoldThrottleOut` → `LearnApply` → `gate`).

| # | Y/N | Verdict |
|---|-----|---------|
| **1** LE bank skip | **Y** | Bank is clamped only in the `if (lowEnergy)` block after `directBank` is applied; skipping that clamp for turn phases lets Learn’s 40–70° command through. Use a turn-only flag (not bare `learnPull` — that is also true for dive `recover`). |
| **2** Pitch invalidate | **Y** | Works if you **stop whole-step invalidate on nose error** (ignore until `reachAt`, and/or only treat as limit when `passBest != null` like today). **Do not** replace with a tight 40 m alt timer — `TurnAltLoss` (150 m) already ends the bank and can save `passBest`; a 40 m rule would be harsher, not kinder. |
| **3** Clear Turns/Envelope | **Y** | Clears the append-on-rerun bug **if done in both** `StartTest(7)` **and** the `rates` → `turnprep` entry (and any selective path into step 7). `StartTest` alone is not enough for full/Only pipelines. |
| **4** ReachTimeout | **Y** | Turn-local fail-on-timeout (leave `Reached()` shared for dives); no `Seg` average until bank matched. |
| **5** CapHot | **Y** | `CapHot` feeds `CapSpeed` → `SpeedCapThrottle` while turn uses `holdSpeed`; suppressing the G term when learn is in turn/`turnprep` stops the mid-ladder cut. Inline the phase check inside `UpdateCap` (`learnPull` is not in scope there). Heat &gt; 0.85 can stay. |
| **6** Envelope speed = measured | **Y** | `SafeBankAt` keys on slot 0; swapping `vT` for mean/end `i.Speed` in the envelope row fixes overstated speed tags. Keep `vT` only in notes/curves if needed. |
| **7** Rename checklist | **Y** | Label-only; no behavior risk. |
| **8** turnprep SubProgress | **Y** | Same `vT` expression as `case "turnprep"`; UI-only. |
| **9** Delete dead fields | **Y** | `passBad` / `GuessThr` / `down` / unused `SinkPitch` have no callers; safe cleanup (doesn’t change flight). |
| **10** RateMeter gap | **Y** | Hold-last (re-seed time, don’t zero) stops bogus 0°/s averages after a hitch. (Skipping `TurnRate==0` is weaker — real zero rates exist.) |
| **11** Reset limThr | **Y** | `Go(sinkdown/sinkmap)` already resets `vsF`; adding `limThr=0; limT=NaN` there covers sinkclimb → resume via `Go(r)`. sinkdown already zeros `limThr` every frame; the bug is **sinkmap** after climb. |
| **12** One SinkSpeedFactor | **Y** | Aligns prep exit, `SpeedPitch`, and strings; picks one physics target. Prefer **1.25** so map speed sits above LE’s 1.2× enter threshold. |
| **13** Lag per step | **Y** | Clear `lagS=NaN` on each +1%; add `ThrottleLag` only when `lagDone`; keep numeric lag in the sink row (no NaN → no FromDict row drop). |
| **14** Softer sink abort | **Y** | Skip `Finish` for G/stress in sink* → Redo+wait/stallprep keep table; or raise/spike-only abort on `sinkclimb`. Never `SinkDone` for abort. |
| **15** LE / MinSafeSpeed in sink | **Y** | LearnApply already overwrites throttle; skipping `speed = Max(…, MinSafeSpeed)` and LE side-effects while `Phase` starts with `sink` stops shold/LE fighting the map at the 1.2× edge. Low severity but the patch works. |
| **16** Remove GuessThr/down | **Y** | Same as #9. |
| **17** Warp notice/clamp | **Y** | Physics ≤3× already while `Active` in atmo; a rails-warp notice (or force physics) during learn addresses the remaining foot-gun. Optional. |

### Top 3 after check

| Top 3 item | Still good? |
|------------|-------------|
| 1 LE bank | **Y** — ship #1 |
| 2 Clear envelope + limThr | **Y** — ship #3 (both entry points) + #11 |
| 3 Pitch invalidate | **Y** — ship the safe half of #2 only (no 40 m alt invalidate) |

All listed fixes are **Y** after rewriting #4 / #13 / #14.

### Re-verify rewritten #4 / #13 / #14 (code walk)

| # | Y/N | Proof |
|---|-----|--------|
| **4** | **Y** | Dives keep calling `Reached()` unchanged (`703–707`). Turn-only early `return` on `!bankOk && dt>=ReachTimeout` never hits `Reached`’s `ok \|\| timeout` arm (`322`) nor the fallthrough `reachAt = i.T` (`661`), so no 20 s average of a bank that never arrived. Fail path can reuse the existing `passBest` / next-pass block (`675–682`). |
| **13** | **Y** | Root bug is `lagS` surviving `lagDone = false` on +1% (`750`) and on sinkclimb resume (`728`). Clearing `lagS` stops `if (!IsNaN(lagS)) ThrottleLag.Add` (`758`) from re-logging the previous step. Table stays numeric → `FromDict` `Convert.ToDouble(l[2])` (`110`) keeps the row. |
| **14** | **Y** | **B** is a one-line threshold change in the abort block (`558–561`) — same pattern as `turning`. **A** works only via `Go("stallprep")` so manual `ManualRoute` (`238–252`) clears `activeTest` (bare `Go("wait")` would leave `activeTest == 9`). Neither path calls `SinkDone`. |

---

*Updated: rewritten N-fixes re-verified Y. Still review-only — no game code changed.*
