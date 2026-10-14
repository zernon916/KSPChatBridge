# QWENCHANGED - what each QWEN_PLAN task actually changed (append-only, newest at the end)

## T1 - antenna unsafe message shows the safe speed (+ SYSTEM comms, max once per 60 s)
- Commit: `370a3bd` - "QWEN T1: antenna unsafe message shows the safe speed and posts to SYSTEM comms, max once per 60 s"
- Files: `KSPChatMod/NativeLifecycle.cs` only (33 insertions, 4 deletions)
- Build: `cd KSPChatMod && dotnet build -c Release` -> Build succeeded, **0 errors** (6 warnings, all pre-existing, none in this file)

### What changed
1. Constants / state
   - `AntennaUnsafeGapS = 60` added to the existing science constants line (next to `AntennaMaxQ = 1.0` kPa).
   - `float sciAntennaUnsafeAt = -999;` - when the unsafe warning was last put on SYSTEM comms.
2. `static double AntennaSafeSpeed(double atmDensity)` (new)
   - `sqrt(2 * AntennaMaxQ * 1000 / atmDensity)` = `sqrt(2000/rho)` m/s - the speed that keeps dynamic pressure at or below the 1 kPa breakable limit (KSP: q kPa = 0.0005 * rho * v^2). Kerbin sea level (rho ~1.2) -> ~41 m/s.
   - No air / NaN density -> +infinity (out of the atmosphere any speed is safe).
3. `string AntennaUnsafeText()` (new)
   - Exactly the planned text: `Antenna unsafe: slow below N m/s (now M m/s) or climb` (N = safe speed, M = `vessel.srfSpeed`, whole m/s).
   - Infinite safe speed (vacuum / bad reading) -> `Antenna unsafe: climb out of the atmosphere`.
4. `void PostAntennaUnsafe(string text)` (new)
   - Posts `[SYSTEM] <text>` via `ChatWindow.Notice` (that is how SYSTEM comms lines reach the chat window / COMMS page) and writes one `[science] SYSTEM ...` chat-log line with q, rho and altitude.
   - Throttled to one comms post per `AntennaUnsafeGapS` (60 s) - the transmit retry runs every 5-30 s, so without the gate the pilot would get a line every retry. The extra `t >= sciAntennaUnsafeAt` guard covers a `Time.realtimeSinceStartup` reset on scene change (same style as `AlertGate`).
5. `ExtendAntennaIfSafe()`
   - Remembers the first antenna refused by the airstream (`streamTitle`).
   - When nothing could be extended and the airstream was the reason, it calls `PostAntennaUnsafe` and returns the new text + part title + q, so the existing refusal log line becomes
     `[science] Antenna unsafe: slow below 41 m/s (now 240 m/s) or climb (Communotron 16, q 4.20 kPa) - STORED`
     instead of `[science] antenna Communotron 16 unsafe: airstream (q 4.20 kPa) - STORED`.
   - Unchanged: shielded / hot / "no usable antenna" refusals, the EXTEND path, `RetractIfDone`, EC gating, store + retry timing.

### Notes / risks
- Left alone on purpose (T1 scope): `RetractIfDone`'s danger line still reads `[science] RETRACT <part> (airstream q X kPa)` - it says what happened but not the safe speed. Easy follow-up if wanted.
- No unit test: `NativeLifecycle.cs` is not compiled into `tests/csharp/Aics.Tests.csproj` (it needs live KSP types), so the math stayed in the mod file instead of moving to `SciencePolicy.cs`. Verified by build only.
- `dotnet run --project tests/csharp/Aics.Tests.csproj` fails the same way at HEAD ("approach turned away from the runway ...", Program.cs:213) - pre-existing and unrelated; confirmed by re-running with this change reverted.
- Not committed, per the RULES: CURSOR_TODO.md, tools/package_release.ps1, AUTOPILOT_REVIEW.md, REPORT.7.9.MD, MECHJEB_IDEAS.md, QWEN_PLAN.md. `QWEN_PLAN.md` T1 is marked `[x]` locally with its done line.
- Next unchecked task: **T2** (solar panels) - not started.

## T2 - 2026-10-13 00:17 - solar panels use the same safe extend/retract rule as antennas
- Commit: `155615c` - "QWEN T2: solar panels follow the science-antenna deployable rule (shared DeployableUnsafe: not shielded, not hot, q <= 1 kPa or out of the atmosphere); AICS remembers the panels it opened and stows only those when the rule turns unsafe"
- Files: `KSPChatMod/NativePower.cs` and `KSPChatMod/NativeLifecycle.cs` (58 insertions, 6 deletions)
- Build: `dotnet build KSPChatMod/KSPChatMod.csproj -c Release` -> **0 errors** (6 warnings, all pre-existing); `dotnet build tests/csharp/Aics.Tests.csproj` -> **0 errors** as well

### What changed
1. `NativeLifecycle.cs` - one rule for every deployable
   - New `internal static string DeployableUnsafe(Part part, bool isBreakable, double qKPa, bool inAtmo)`: returns null when the part may go out (or stay out), else `"shielded/in a closed bay"`, `"hot (reentry heating)"` (temp or skin temp over half its max), `"airstream"` (in atmosphere + breakable + q over `AntennaMaxQ` = 1 kPa), or `"missing part"`.
   - `ExtendAntennaIfSafe()` now calls that helper instead of its three inline `if`s - same check order and same strings, so T1's refusal text and log lines are unchanged; `streamTitle` is still set only for `"airstream"`.
   - `AntennaMaxQ` comment updated: the 1 kPa stock break limit now covers antennas AND solar panels.
2. `NativePower.cs` - panels gated, remembered, and stowed again
   - New `extended` list = the panels AICS deployed. Panels the pilot opened are never touched ("only retract panels we extended").
   - Extend path (unchanged triggers: charge < 25% and `FlightPolicy.SafeSolar`): each retracted panel must now also pass `DeployableUnsafe`. Safe ones `Extend()` and are remembered + logged; unsafe ones are skipped and reported once per tick.
   - New `PanelSafetyTick(vessel)`, called every 2 s **before** the `charge >= 25%` early-out - panels already out have to be stowed at a healthy charge too (that early-out used to stop the whole tick). Per remembered panel: drop it if null / part gone / another vessel / RETRACTED / RETRACTING / BROKEN (pilot took it back, or it snapped); otherwise re-check the shared rule and `Retract()` when unsafe. Shielded in a closed bay -> left out; `retractable == false` -> logged and left out.
   - New lines: `[power] EXTEND <part> (low charge, q N kPa is safe; stowed again if the airstream turns)`, `[power] solar panel not extended - <part>: <why> (q N kPa)`, `[power] RETRACT <parts> (<why>, q N kPa)`, `[power] <part> unsafe (<why>) but not retractable - left deployed`, plus the chat line `Solar panel retracted: airstream q X kPa is over the 1.0 kPa it survives - deployable lost.` (or the heat reason).
   - `using System.Globalization;` and a one-line `Q(vessel)` q formatter (InvariantCulture, like the rest of the code).

### Notes / risks
- Hazard this closes: panels used to go out whenever `SafeSolar` allowed it (q up to 12 kPa, no shielded/heat check) and were **never** retracted - a low-EC plane that then accelerated or dived lost them. Now they cannot deploy above 1 kPa and are stowed the moment q or heat turns.
- API verified against the game DLL by reflection (no guessing): `ModuleDeployablePart.retractable`, `isBreakable`, `Extend()/Retract()`, `DeployState {RETRACTED, EXTENDED, RETRACTING, EXTENDING, BROKEN}`, `Part.ShieldedFromAirstream`, `PartModule.vessel`.
- `FlightPolicy.SafeSolar` was deliberately left untouched (its 5 checks in `tests/csharp/Program.cs` still hold); it stays as the extra vessel-level speed/q gate on top of the new per-part rule, so panel extends are strictly stricter than before.
- `extended` lives in the per-vessel `NativePower` instance, so it is dropped on a vessel switch / revert - same lifetime as the antenna's `sciExtended`.
- While the charge is low and a panel is unsafe, the "not extended" log line repeats every 2 s (the antenna's unsafe line repeats every ScienceTick the same way); the chat notice stays behind the existing 90 s gate.
- No unit test: both files need live KSP types and are not compiled into `tests/csharp`.
- Left alone on purpose: `RetractIfDone`'s antenna danger check still uses its own inline condition (heat is not "danger" there) - a later task can move it onto the shared helper.
- Next unchecked task: **T3** (speed hold by target acceleration) - not started.

## T3 - 2026-10-13 01:26 - speed hold asks for an ACCELERATION, clamped to what the craft learned
- Commit: `fb27ed8` - "QWEN T3: speed hold by target acceleration - the speed error becomes a target acceleration ((target - speed) / 4 s) clamped to the LEARNed accel/decel, and the throttle PI now drives that acceleration error"
- Files: `KSPChatMod/PilotPolicy.cs`, `KSPChatMod/NativeFlightController.cs`, `KSPChatMod/NativeLearn.cs`, `tests/csharp/ThrottleLearnTests.cs` (54 insertions, 6 deletions)
- Build: `dotnet build KSPChatMod/KSPChatMod.csproj -c Release` -> **0 errors** (6 warnings, all pre-existing); `dotnet build tests/csharp/Aics.Tests.csproj -c Debug` -> **0 errors** (22 warnings, pre-existing)

### What changed
1. `PilotPolicy.cs` - `SpeedHold` is now a cascade (speed -> acceleration -> throttle), the MECHJEB_IDEAS 4 ask
   - New `internal const double AccelTau = 4` - the seconds the hold gives itself to fix a speed error, and the new public read-outs `TargetAccel` (the acceleration it is asking for) and `AccelLimit` / `DecelLimit` (the clamp in use, NaN = unmeasured).
   - New `internal void SetAccelLimits(double accel, double decel)`: values must be positive and finite or that side stays unmeasured (NaN).
   - `Step()` keeps the same measured speed term (`speed + accF * Lead + pending`) and the same deadband, but the error now goes through the cascade: `TargetAccel = Clamp(err / AccelTau, -DecelLimit, AccelLimit)`, then `ae = TargetAccel - accF` is what the throttle loop drives. Unmeasured accel limit falls back to `Sens` (already mass/TWR scaled); unmeasured decel leaves the demand unclamped, so a craft with nothing learned behaves as before.
   - Gains rescaled for the new units, deliberately keeping the old response at the design point: P term `ka = Math.Max(Kd, Kp * AccelTau)` on the acceleration error (so `Kd` - the inertia modifier's authority floor - still dominates when it shrank `Kp`, and at the design modifier `Kp * 4` reproduces the old speed-error gain), and the integrator now integrates the acceleration error with `Ki * AccelTau` (= the old `Ki` on the speed error). Anti-windup, `RateLimited`, `Waiting`, spool-hold, cmd slew and airbrake hysteresis are untouched.
2. `NativeFlightController.cs` - limits wired on engage, plus the log line
   - `SetInertiaModifier` now computes the clamp and calls `shold.SetAccelLimits(accA, decA)`: accel = LEARN profile `AccelAt(vessel.totalMass)` (mass-scaled), decel = LEARN profile `DecelAt(vessel.totalMass)`, else the live idle/airbrake learner `Decel.Estimate(vessel.vesselName, 150, 100, true)`.
   - The existing engage chat line gained `... speed hold asks accel = speed error / 4 s clamped +<n/a (Sens N from TWR) | X learned> / -<n/a (slowdown unclamped) | Y learned> m/s2`.
3. `NativeLearn.cs` - one line in `RefreshProfile`: `shold.SetAccelLimits(profile.Accel NaN ? NaN : profile.AccelAt(m), profile.Decel NaN ? NaN : profile.DecelAt(m))`, so the clamp follows every profile swap (new LEARN, pilot profile, mass rescale) like `SetLag`/`SetTargetSlew` already do.
4. `tests/csharp/ThrottleLearnTests.cs` - new `AccelCascade()` group (5 checks, wired into `Run()`), because `PilotPolicy.cs` is already compiled into the harness: the 4 s constant; the unlearned demand equals `(target - speed - deadband) / 4`; and on a synthetic plant that could dive off at -4 m/s2 but only learned -0.5, the demand stays inside `[-0.5, +4]`, the craft is never slammed to idle / -4 m/s2 (slowest -0.50 m/s2 at 0.44 throttle) and the speed still settles on the target.

### Notes / risks
- Hazard this closes: the old PI asked for whatever throttle the speed error implied, so a heavy 40 m/s too fast got an idle slam it could not follow and then a full-throttle catch-up - the 98 <-> 117 m/s pumping in the A300 runs. The demand is now bounded by what the craft measured, so a slowdown can never be harder than the learned decel.
- Unmeasured decel is deliberately left unclamped (a craft with no LEARN profile and no decel sample keeps today's behavior instead of getting an arbitrary limit). Accel falls back to `Sens`, which is TWR-derived, so it degrades gracefully too.
- `AccelTau` is a hard-coded 4 s for every craft/speed. That is what the task asked for; a later pass could scale it with the learned lag or make it a profile value.
- The integrator is now on acceleration error, so a steady acceleration bias (e.g. a slope) is trimmed the same way a steady speed bias was; no re-tune was attempted beyond the unit rescale.
- Test evidence: with the harness `Check` temporarily made non-throwing (reverted afterwards, `tests/csharp/Program.cs` is byte-identical to HEAD), the whole suite showed **exactly the same 11 pre-existing failures before and after** this change (`diff=0`), and the throttle/learn group went 289 -> 294 passing. In the pristine harness the run still aborts at `Program.cs:213` ("approach turned away from the runway") - the pre-existing failure T23 owns - so the speed-hold groups are not reached in a normal `dotnet run`; the group results above came from the temporary non-throwing build.
- Not committed, per the RULES: CURSOR_TODO.md, tools/package_release.ps1, AUTOPILOT_REVIEW.md, REPORT.7.9.MD, MECHJEB_IDEAS.md, QWEN_PLAN.md (T3 is marked `[x]` locally with its done line). `QWENCHANGED.md` is still untracked, as in the T1/T2 passes - T24 is the task that adds it to the never-commit list.
- Next unchecked task: **T4** (vertical-speed/altitude hold pitch = atan(vsError / trueAirspeed)) - not started.

## T4 - 2026-10-13 01:37 - vertical-speed / altitude hold asks a pitch ANGLE: atan(vs error / true airspeed)
- Commit: `e940959` - "QWEN T4: vertical-speed/altitude hold asks a pitch ANGLE - atan(vs error / true airspeed) in degrees - instead of 0.8 deg per m/s, so the same vs error flies the same vertical speed at 40 m/s and at 250 m/s; the attitude trace logs the ask"
- Files: `KSPChatMod/PilotPolicy.cs`, `KSPChatMod/NativeFlightController.cs`, `tests/csharp/ThrottleLearnTests.cs` (32 insertions, 4 deletions)
- Build: `dotnet build KSPChatMod/KSPChatMod.csproj -c Release` -> **0 errors** (6 warnings, all pre-existing); `dotnet build tests/csharp/Aics.Tests.csproj -c Debug` -> **0 errors** (22 warnings, pre-existing)

### What changed
1. `PilotPolicy.cs` - one new pure policy function next to `FlightPathAngle`
   - `internal static double VsPitchDeg(double vsError, double tas, double gain = 1)` returns `gain * atan(vsError / tas)` **in degrees** - the nose change that produces the wanted vertical speed, because `vs = horizontal * tan(pitch)`. Plus `internal const double VsPitchLinGain = .8, VsPitchMinTas = 10`.
   - Guards: `NaN` error -> `0`; `NaN` gain -> `1`; no usable airspeed (`NaN` or `tas <= 10 m/s`, i.e. a ground roll) -> the **old** linear `.8 * gain * err`, clamped to `+-PitchEnvelope(false)` (25 deg) so the pure function can never return a wild value.
2. `NativeFlightController.cs` - 3 lines in the pitch loop (`desiredPitch`, ~line 755)
   - `1 + .8 * kinH * (targetVs - vessel.verticalSpeed)` became `1 + PilotPolicy.VsPitchDeg(targetVs - vessel.verticalSpeed, vessel.srfSpeed, kinH)`. `vessel.srfSpeed` is the TAS this codebase already uses for the speed floor ("speed hold is surface speed: floor 1.3 Vs as TAS at this density", line 745).
   - `kinH` (mass modifier `kin` x `CraftClass.GainScale`) is unchanged as the gain, but its declaration moved from line 754 up above the attitude trace so the log can use it. Nothing else moved: the 1 deg bias, `vsIntegral`, `CraftClass.PitchLead`, the `descentPitchMin / -2 .. CpMax` clamp, the sink-arrest floor, the climb + LEARN-Vy bias and `pitchShape` are all exactly as before.
   - New decision log: the existing 5 s `"att"` trace line now ends with `vs-pitch ask <N> deg for vs err <M> m/s (atan(err/tas), was 0.8 deg per m/s)`, so a flight log shows the angle the hold asked for next to the nose pitch / fpa it actually got.
3. `tests/csharp/ThrottleLearnTests.cs` - new `VsPitchAngle()` group (8 checks, wired into `Run()`), `PilotPolicy.cs` is already compiled into the harness: the ask equals `atan(err/tas)`; the ask flies **exactly** 8 m/s at 40 / 100 / 250 m/s (`tas * tan(pitch) == 8`) where the old 6.4 deg asked 45 / 114 / 288 m/s; a 5 m/s error needs > 4x the angle at 40 m/s than at 250 m/s; the gain scales the angle; ground roll / `NaN` tas falls back to `0.8 deg per m/s` and a `NaN` error returns 0; a negative error is symmetric nose-down.

### Notes / risks
- Hazard this closes: the hold's pitch authority was a fixed 0.8 deg per m/s of vs error, so the loop gain changed with airspeed - at 250 m/s a 5 m/s error asked 4 deg (~17 m/s of climb, overshoot/porpoise) and at 40 m/s the same 4 deg only gave ~3.5 m/s (slow, laggy capture). The angle form makes the vertical response the same at every speed, which is what T4 asked for.
- The angle branch is deliberately **not** clamped inside `VsPitchDeg` (a slow craft with a big error can ask 30-80 deg); the caller's existing `FlightPolicy.Clamp(..., targetVs < -1 ? descentPitchMin : -2, CpMax)` bounds it exactly as before, and the low-energy / sink-arrest / stall floors still own the nose. Only the fallback branch is self-clamped.
- Ground roll, takeoff rotation and flare are untouched in practice: on the roll `srfSpeed <= 10 m/s` takes the old linear path, and in takeoff / final / flare `directPitch` / `directVs` or `TakeoffGround.Elevator` own the pitch anyway.
- `vsIntegral` still adds up to +-5 deg on top of the angle, so a steady bias is trimmed as before; no re-tune of the integrator was attempted.
- Test evidence: with the harness `Check` temporarily made non-throwing (reverted afterwards - `tests/csharp/Program.cs` is byte-identical to HEAD, `git status` clean for it), the whole suite showed **exactly the same 11 pre-existing failures before and after** this change, and the throttle/learn group went 294 -> **302 passing, 0 failed**. In the pristine harness the run still aborts at `Program.cs:213` ("approach turned away from the runway") - the pre-existing failure T23 owns. (A 12th failure, "secrets mask", appeared once while `Program.cs` was patched through `Set-Content -Encoding UTF8`, which mangles the `…` literal in that check - an artifact of the patch method, gone once `Program.cs` was restored.)
- `tests/csharp/ThrottleLearnTests.cs` has mixed line endings in HEAD (695 CRLF / 175 bare LF); the first edit normalised the whole file, so the file was rebuilt from the HEAD bytes with only the 17 new lines + the `Run()` line changed (`git diff --numstat` = 17/1) - no whitespace churn in the commit.
- Not committed, per the RULES: CURSOR_TODO.md, tools/package_release.ps1, AUTOPILOT_REVIEW.md, REPORT.7.9.MD, MECHJEB_IDEAS.md, QWEN_PLAN.md (T4 is marked `[x]` locally with its done line). `QWENCHANGED.md` is still untracked, as in the T1/T2/T3 passes - T24 is the task that adds it to the never-commit list.
- Plan note: this pass was asked to mark **T3** as "SKIPPED - for Grok", but T3 was already `[x]` with its done line and committed as `fb27ed8`, so its record was left intact (RULES: do not redo checked tasks) and the next unchecked task after it - T4 - was done instead.
- Next unchecked task: **T5** (smooth takeover: start from the current control input and ramp in over 1 s when any hold engages) - not started.

## T5 - 2026-10-13 02:02 - Smooth takeover: holds ramp in from the pilot's stick over 1 s

### Files changed (commit `ae8abc4`, 4 files, +57/-5)
- `KSPChatMod/PilotPolicy.cs` (+15): new pure, testable helpers at :224-238 - `TakeoverSeconds = 1`, `TakeoverWeight(elapsed, rampSeconds)` (= elapsed/ramp clamped [0,1]; ramp <= 0 / NaN -> 1 "instant", checked FIRST; elapsed <= 0 / NaN -> 0) and `TakeoverBlend(pilotInput, commanded, elapsed, rampSeconds)` (= stick + (ask - stick) * weight; NaN seed -> the hold's ask at once). No KSP types, per the policy-class style rule.
- `KSPChatMod/NativeFlightController.cs` (+20/-4): new fields `takeoverT = 99, takePitch, takeRoll, takeYaw` (:31). `BeginHold` - the single funnel every engage goes through (hold/VS/approach/localizer/takeoff/ferry/heli/glide) - now sets `takeoverT = 0`, captures the pilot's REAL stick `vessel.ctrlState.pitch/roll/yaw`, calls `gate.Seed(vessel.ctrlState.mainThrottle)` (the throttle gate previously kept its stale `Out` at engage, so throttle could jump too) and writes the decision log (:492-493). In `Fly`: airplane owned-axis blend at :790-795 - pitch only when `pitchOwned` (`TakePitch` :786 or the takeoff-ground elevator :787), roll only when `rollOwned` (heading hold or directBank :789) - each owned axis lerps from the stick-at-engage to the hold's ask over 1 s (elevator field re-synced so `TrimBleed`/`MoveTowards` see the blended value); heli blends pitch/roll/yaw at :684-690. Axes the hold does NOT own (yaw/wheelSteer in airplane holds) pass the pilot's live input untouched. `FailSoft` sets `takeoverT = 99` (:120) - an error release ends the ramp because the seed is stale.
- `KSPChatMod/NativeLearn.cs` (+1): when the pilot lets go of the controls during a LEARN wait (:105, right where it already re-synced `elevator` + `gate.Seed`), the ramp re-arms (`takeoverT = 0` + fresh stick capture) so the hold resumes from the pilot's stick instead of snapping.
- `tests/csharp/ThrottleLearnTests.cs` (+21/-1): new group `TakeoverRamp()` (9 checks) wired into `Run()` after `VsPitchAngle()`.

### Verification
- `dotnet build -c Release -p:KSPDir=...` (KSPChatMod) and `dotnet build tests\csharp\Aics.Tests.csproj`: **0 errors** each.
- Pristine harness: still aborts at the pre-existing `Program.cs:213` ("approach turned away from the runway") exactly as before - T23 owns that.
- With `Program.Check` temporarily made non-throwing (then reverted via `git checkout` - `Program.cs` byte-identical to HEAD, not in the commit): throttle/learn went **302 -> 311 passing, 0 failed**, and the whole suite showed the **same 11 pre-existing failures** as the T4 baseline - no regressions.
- The new checks caught one real bug during the pass: the first `TakeoverWeight` draft checked `elapsed <= 0` before `ramp <= 0`, so a zero ramp did not take over instantly; guard order fixed, then green.
- `git diff --numstat` was minimal (20/4, 1/0, 15/0, 21/1) - no line-ending churn this time.

### Notes / risks
- Throttle is deliberately NOT blended: `gate.Seed(pilot throttle)` at engage + the gate's own rate limit remain the ONE smooth throttle path (the gate exists precisely so nothing else slews throttle).
- Rollout, vertical-landing and taxi modes return before the airplane blend and keep their own controllers - no ramp there (rollout engages long after `BeginHold`, vertical landing has its own state machine).
- During the 1 s ramp, `TrimBleed` (:788) still sees the pre-blend decision only in the sense that it runs before the blend; it trims toward the blended elevator next tick - effect is bounded and brief.
- The blend is a convex combination of two values in [-1,1], so no extra clamp is needed; KSP clamps `ctrlState` itself.
- In-flight verification pending (as with T3/T4): engage a hold while deflecting the stick and watch the surfaces walk over ~1 s.
- Not committed, per the RULES: CURSOR_TODO.md, tools/package_release.ps1, AUTOPILOT_REVIEW.md, REPORT.7.9.MD, MECHJEB_IDEAS.md, QWEN_PLAN.md (T5 marked `[x]` locally with its done line), NOTES.md + CHEATSHEET.md (each got one appended line; both are excluded via .git/info/exclude), QWENCHANGED.md (still untracked - T24 is the task that adds it to the never-commit list).
- Environment note: this pass ran entirely in `c:\Coding Projects\KSPChatBridge` (the `D:` copy referenced by earlier reads does not exist on this machine; all edits, builds, tests and the commit were verified against the C: working tree).
- Next unchecked task: **T6** (use rudder for heading errors under 3 deg instead of banking) - not started.

## T6 - 2026-10-13 02:16 - heading errors under 3 deg go to the RUDDER: the wings stay level
- Commit: `729637c` - "QWEN T6: heading errors under 3 deg are flown with the RUDDER, not by banking - PilotPolicy.RudderSteer/RudderYaw/HeadingBank/RudderStuck engage the rudder (hysteresis to 3.5 deg, 20 m/s floor, 6 s no-progress give-up then 30 s cool-off), the bank ask goes to 0 so the wings stay level for the last few degrees, c.yaw is written only while the rudder owns the correction (damped by the measured heading rate) and joins the 1 s takeover blend"
- Files: `KSPChatMod/PilotPolicy.cs` (+27/-0), `KSPChatMod/NativeFlightController.cs` (+14/-1), `tests/csharp/ThrottleLearnTests.cs` (+31/-1) - minimal diff, no line-ending churn
- Build: `dotnet build -c Release -p:KSPDir="C:\Steam\steamapps\common\Kerbal Space Program"` (KSPChatMod) and `dotnet build tests\csharp\Aics.Tests.csproj` -> **0 errors** each (6 / 22 warnings, all pre-existing)

### What changed
1. `PilotPolicy.cs:239-265` - the rudder-heading law, pure math (no KSP types) so the harness can test it
   - Constants: `RudderErrDeg 3` (engage under 3 deg of heading error), `RudderOffDeg 3.5` (release above it - the gap is the hysteresis that stops the two axes chattering at the crossover), `RudderMinSpeed 20` m/s (below that the rudder has no authority and the wheel / ground-roll steer owns yaw), `RudderGain .08` per deg, `RudderRateGain .04` (yaw-rate damping), `RudderMax .3` (travel cap), `HeadingBankGain .5` (the pre-existing 0.5 deg of bank per deg of heading error, now named), `RudderGiveUpS 6`, `RudderProgressDeg .5`, `RudderCoolOffS 30`.
   - `RudderSteer(headingError, wasOn, speed)`: `|err| < (wasOn ? 3.5 : 3)` and `speed > 20`. NaN error or NaN speed -> false, so the bank keeps the error.
   - `RudderYaw(headingError, yawRateDegS)`: `clamp(.08 * err - .04 * rate, +-0.3)`. The rate term is the Dutch-roll damper - a rudder driven by error alone swings past the heading and hunts; the measured heading rate kills the swing. NaN rate is ignored, NaN error -> 0.
   - `HeadingBank(headingError, rudderOn)`: `rudderOn ? 0 : .5 * err` - the bank ask is exactly 0 while the rudder owns the correction (that IS the task: wings level for the last few degrees), otherwise the old law unchanged.
   - `RudderStuck(secondsOnRudder, errorNow, errorAtHandover)`: `>= 6 s` on the rudder with less than `0.5 deg` of progress -> give up. Covers no rudder authority, a rudder out of the airflow, and a target that keeps moving.
2. `NativeFlightController.cs` - the heading hold flies the last few degrees with yaw
   - New state (:45): `rudderOn`, `rudderHdg0` (the error at handover), `rudderSince`, `rudderOffUntil` (cool-off deadline), `lastRudderHdg` (for the rate).
   - :723 one signed `hdgErr = FlightPolicy.Wrap(heading - ship_heading)` now feeds BOTH asks (it used to be computed inline inside the bank law only); :724 `hdgRate` = wrapped heading change / dt with `lastRudderHdg` updated every tick.
   - :725-726 engagement: `!directBank.HasValue && !vessel.LandedOrSplashed && mode != "takeoff" && !(now < rudderOffUntil) && PilotPolicy.RudderSteer(hdgErr, rudderOn, vessel.srfSpeed)`. An explicit bank ask (roll hold, plan circle, approach arc) keeps its own yaw and the ground roll keeps the wheel steer; the give-up check then forces release and sets `rudderOffUntil = now + 30 s`.
   - :727-732 every crossover writes an "att" decision line both ways ("rudder takes the last 3 deg (err X deg at Y m/s) - wings level, no bank ask" / "bank flies the heading again (err X deg) - rudder released; no progress on the rudder, off for N s"), so the in-game log shows which axis owns the heading and why it handed back.
   - :733 `bank = directBank ?? FlightPolicy.Clamp(arcFf + PilotPolicy.HeadingBank(hdgErr, rudderOn), -blim, blim)` - the landing entry arc keeps its curvature feed-forward, the heading-error trim goes to 0 while the rudder works.
   - :801 `if (rudderOn) c.yaw = (float)PilotPolicy.RudderYaw(hdgErr, hdgRate);` - `c.yaw` is written ONLY while the rudder owns the correction, so outside it the pilot's live yaw input passes through untouched (the same axis-ownership rule T5 established for pitch/roll).
   - :806 the rudder joins the T5 takeover blend (`takeYaw` was already captured in `BeginHold` at :493), so engaging a hold with the rudder deflected ramps the surface in over 1 s instead of snapping.

3. `tests/csharp/ThrottleLearnTests.cs` - new group `RudderHeading()` (13 checks) wired into `Run()` after `TakeoverRamp()`
   - Constants; engage under 3 deg on either side and release at 3.2 deg; hysteresis (once on, it holds to 3.5 deg); the 20 m/s floor plus the NaN error / NaN speed gates; the old 0.5 deg-per-deg bank ask still applies to big errors; `HeadingBank == 0` while the rudder owns it (and NaN -> 0); rudder gain, sign and the +-0.3 clamp; the rate damping reduces the ask, NaN rate ignored, NaN error -> 0; the WRAPPED error (target 359 flying 001 = 2 deg left -> rudder, not a 358 deg bank ask); the give-up guard (6 s with < 0.5 deg of progress) and the 30 s cool-off.
   - Plus a 50 Hz closed loop at 100 m/s starting 2.5 deg off (rudder authority 3 deg/s per unit deflection, bank turn rate `g*tan(bank)/V`): it converges on the heading with the rudder ALONE - 0 bank ticks, overshoot < 0.4 deg, rudder always inside +-0.3, i.e. it flies the last degrees without rocking the wings.

### Verification
- Both projects: **0 errors** (mod Release with `-p:KSPDir`, harness Debug).
- Pristine harness: still aborts at the pre-existing `Program.cs:213` ("approach turned away from the runway") exactly as before this pass - T23 owns that.
- With `Program.Check` temporarily made non-throwing (then reverted via `git checkout` - `Program.cs` byte-identical to HEAD, not in the commit): throttle/learn went **310 -> 323 passing, 0 failed**, and the whole suite showed the **same 11 pre-existing approach/teardrop failures** as the T5 baseline - no regressions. (Harness gotcha: build and run must be sequenced - running them in one parallel batch executed the stale exe and replayed the old abort.)
- `git diff --numstat HEAD~1 HEAD`: 27/0, 14/1, 31/1 - no line-ending churn.

### Notes / risks
- Rudder AUTHORITY is assumed, not measured: a craft with no rudder, or a tail-dragger whose rudder is out of the airflow at low speed, only finds out through the 6 s give-up + 30 s cool-off. Measuring the yaw response per craft (a LEARN step, like the accel/decel learner) would be the proper fix - candidate for a later task.
- The rudder is blended only during the 1 s after `BeginHold`; if it engages MID-hold (the error shrinking through 3 deg) `c.yaw` can step by up to +-0.3. Bounded and brief, but visible on the surface.
- `hdgRate` is a raw finite difference of `ship_heading`, so a very small `dt` makes it noisy; `RudderRateGain .04` keeps the term small and the +-0.3 clamp bounds the result. A filtered rate would be cleaner.
- While the rudder owns the heading the bank ask is 0, so on the landing entry arc the only roll command left is `arcFf` (curvature feed-forward). A strong crosswind on final could want the heading-error trim back; the window is only 3 deg and the bank returns above 3.5 deg.
- `directBank`, landed/splashed and `mode == "takeoff"` all keep the rudder off, so roll holds, plan circles, the ground roll, rollout and taxi are unaffected.
- In-flight verification pending (as with T3/T4/T5): fly a heading hold, watch the "att" log for the rudder crossover near 3 deg and the wings staying level there.
- Not committed, per the RULES: CURSOR_TODO.md, tools/package_release.ps1, AUTOPILOT_REVIEW.md, REPORT.7.9.MD, MECHJEB_IDEAS.md, QWEN_PLAN.md (T6 marked `[x]` locally with its done line), NOTES.md + CHEATSHEET.md (updated; both excluded via .git/info/exclude), QWENCHANGED.md (still untracked - T24 is the task that adds it to the never-commit list).
- Next unchecked task: **T7** (approach cone: an intercept point on the extended centerline when more than 30 deg off final course) - not started.

## T7 - 2026-10-13 03:03 - approach cone: <= 30 deg join onto the extended centerline
- Commit: `61e5a56` - "QWEN T7: approach cone"
- Files: `KSPChatMod/FlightMissions.cs` + `tests/csharp/Program.cs` (42 insertions, 4 deletions)
- Build: KSPChatMod Release (`-p:KSPDir`) and tests/csharp -> **0 errors** both.

### What changed
1. `ApproachChart` constants + pure helper (FlightMissions.cs ~:833)
   - `ConeDeg = 30`, `ConeMaxD = 45000`.
   - `ConeJoinD(along, cross, heading, course, turnR, finalMin)` (new static, KSP-free, testable): NaN when the heading is already within 30 deg of the final course; otherwise `clamp(max(0, -along) - (|cross|/tan(30 deg) + turnR), finalMin, ConeMaxD)` - the metres BEHIND the threshold of the cone intercept point: the capture covers the cross-track at a <= 30 deg angle plus one learned turn radius to settle on that heading first.
2. `BestJoin` (~:926) - right before the long-final fallback
   - Computes `coneOff = |wrap(heading - Course)|` and, when > 30, `coneD = ConeJoinD(...)` from the local E/N projection of `me`.
   - Fallback sweep: `coneCap = coneD >= finalMin + turnR`; when it binds, `dMax = min(coneD, 45 km)` instead of 45 km, so the chosen intercept point joins the centerline at <= 30 deg. When the cone point falls inside the shortest final (no room for a <= 30 deg capture) the full sweep runs unchanged.
   - Logs for every decision: the chosen fallback appends `, approach cone: N deg off the final course -> intercept at <fix> sized from r R m, join <= 30 deg` to `bestWhy`; the final `why` always appends `[approach cone: ... intercept capped at X km, join <= 30 deg]` or `[approach cone: ... cap X km is inside the shortest final, sweep unchanged]`, and `why` flows into `RunwayMission.JoinLog`.
   - Untouched: chart-join loop, `JoinAngleOk`, FlyOut (fly-out-and-turn kept as required), detour logic.
3. `tests/csharp/Program.cs` - "Nearest safe join" group 7 -> 12 checks: 3 `ConeJoinD` math checks (inside the cone -> NaN; sizing = cross/tan30 + r; clamp up to finalMin) + 2 behaviour checks (40 km out / 10 km left / 90 deg off -> why says "intercept capped at" + "join <= 30"; heavy craft 60 deg off with the cone inside the shortest final -> "sweep unchanged" note and a join still returned).

### Verification
- Both projects: **0 errors**.
- A/B: `Program.Check` temporarily non-throwing (reverted - Program.cs differs from HEAD only by the new T7 checks), run with the T7 block gated off (baseline) vs on: baseline = the same **11 pre-existing failures** the T5/T6 notes list (213 abort + 513, 1407, 1556/1558, 1776, 1779, 1805, 1806, 1815, 1818); T7 on = that same set minus my 2 behaviour checks (they pass only with T7 on), **0 new failures**. Where the cap does not bind, evaluation is byte-identical to pre-T7 (only the `why` text grows), so none of the 11 can be T7's.

### Notes / risks
- The suite still aborts at the pre-existing `Program.cs:213` (T23 owns it), so the new group only runs in record mode today - same situation as T3/T6.
- When the cap binds, the sweep can stop short of 45 km: a > 30 deg off craft gets a shallower (<= 30 deg) but possibly slightly longer entry than before - the removed far-out points were the steep joins T7 asked to remove.
- Cap infeasible (craft abeam/past the threshold) -> behaviour identical to pre-T7, with the no-op visible in the log.
- In-flight verification pending: watch JoinLog for "[approach cone: ... join <= 30 deg]" on a > 30 deg off entry.
- Not committed, per the RULES: CURSOR_TODO.md, tools/package_release.ps1, AUTOPILOT_REVIEW.md, REPORT.7.9.MD, MECHJEB_IDEAS.md, QWEN_PLAN.md (T7 marked [x] locally with its done line), NOTES.md + CHEATSHEET.md (one line each added), QWENCHANGED.md (still untracked - T24 adds it to the never-commit list).
- Next unchecked task: **T8** (steer to a moving point 1.5 km ahead on the 3 deg glide path) - not started.


- 2026-10-13 03:17 BLOCKED: pass 1 hit token limit (exceeds the).

- 2026-10-13 03:22 BLOCKED: pass 1 hit token limit (exceeds the).

## T23 - 2026-10-13 03:41
Uncommitted approach fix in FlightMissions.cs: preserve chart joins/preferences, use roll-in-only localizer prediction, and check fly-out climb/terrain along the path. Tests unchanged. Original and later approach regressions pass; full tests stop at Program.cs:2312 (NativeFlaps.cs access denied). Release build blocked by CS1504 reading NativeFlaps.cs and NativeLearn.cs. T23 remains unchecked; no commit or push.

## T23 - 2026-10-13 03:43
Codex fix in FlightMissions.cs: keep chart join, direct-final fallback only when no chart or detour; fly-out climb checks. Grok reverted its localizer tLead change (broke 4 lineup tests). All tests pass.

## T8 - 2026-10-13 03:50
Codex: 1.5 km lookahead point on final (AircraftLandingPolicy.cs, FlightMissions.cs), 8 new tests. Grok verified build + all tests, committed.

## T9 - 2026-10-13 03:54
Flare uses trusted learned touchdown AoA, saved from the last airborne autoland sample; missing/invalid data retains sink-rate control. Rollout lowers the nose as speed drops at <=2.5 deg/s. Changed AircraftLandingPolicy.cs, FlightMissions.cs, LearnFlight.cs, NativeFlightController.cs and tests/csharp/Program.cs. Added 11 checks; existing tests unchanged. Required Release build: 0 errors (8 warnings); dotnet run: all pass. In-flight verification pending.
Commit blocked: permission denied creating .git/index.lock (read-only .git); no commit created and nothing pushed. Intended message: CODEX T9: Flare using learned touchdown AoA.

## T10 - 2026-10-13 03:56
Added KSPChatMod/NativeAuthority.cs: log-only pitch/roll authority estimates every 10 s from control-surface area proxy x q x moment arm plus active pilot reaction-wheel torque, divided by vessel inertia. No control or test changes. Release build: 0 errors (8 warnings); dotnet run: all tests passed. Approximate unit-lift model, wheel power assumed available; live-flight verification pending.
Commit blocked: git add could not create .git/index.lock (permission denied). No commit created; nothing pushed.

## T11 - 2026-10-13 03:59
Scaled airplane pitch PID and roll PD by inverse estimated authority (nominal 1 rad/s2, bounded 0.5..2); refreshed estimates every 0.5 s with old-gain fallback for zero/invalid/stale or wrong-vessel data. Feed-forward and outer guidance unchanged. Logged gain decisions. Mod changes: NativeAuthority.cs, NativeFlightController.cs, PilotPolicy.cs. Added nine checks in ThrottleLearnTests.cs; existing tests unchanged. Required Release build: 0 errors, 8 warnings; dotnet run: all pass (332 throttle/learn checks). Approximate model requires in-flight validation.
Commit blocked: git add could not create .git/index.lock (permission denied). Intended commit: CODEX T11: Scale pitch and roll PID gains by authority. Nothing pushed.

## T9-T11 - 2026-10-13 04:01
Codex: AoA flare + touchdown AoA learning, authority estimate, gain scaling. Grok verified build + all tests, fixed garbled arrow chars, committed as one.

## TB1 - 2026-10-13 11:26
Reconciled already-completed aircraft tablet port via Cline c2a0cc5, as required by Section A. Release build succeeded (6 warnings, 0 errors); tests/csharp dotnet run passed (exit 0, NU1900 vulnerability-feed warning). No code or tests changed, so no new code commit. Updated QWEN_PLAN.md only and this log; stopped before TB2. Install not run: KSP directory is outside permitted writable roots. In-game UI remains unverified.

## TB2 - 2026-10-13 11:29
Ported Approach to Flight sub-tabs with TabletUi runway selection, landing options, status and LIVE LINK-gated LAND / GO AROUND / ABORT. Shared panel landing dispatch with OnKey so selection/options are honored. Mod code only; tests unchanged. Release build: 0 errors (6 warnings); dotnet run: exit 0, no failures. Commit CODEX TB2: Port approach controls to tablet blocked by .git/index.lock permission denied. Installation outside writable workspace; in-game UI unverified.

## TB3 - 2026-10-13 11:31
Ported taxi to the Flight tablet: shared shortcut handlers, point distances/bearings, speed cap, named routes, refresh and mark-here controls. Controls require LIVE LINK; actions log to ChatLog. Changed mod code only (TabletTaxi.cs and TabletAircraft.cs); no tests changed or weakened. Release build succeeded (0 errors, 6 warnings); dotnet run and Release tests both passed with no failures (NuGet vulnerability-feed warning). In-game UI not verified. Marked TB3 complete after green build. Commit CODEX TB3: Port taxi to tablet blocked by .git/index.lock permission denied; prior TB2 edits preserved. No install (KSP directory outside writable workspace), no push.

## TB4 - 2026-10-13 11:33
Ported ABOUT to the Flight tablet with profile cards, shared AboutLines/AboutCurve and OnKey handlers, LIVE LINK gating, and logged key actions. TabletUi.Graph supports real X spacing and single-sample markers; LEARN opens the Tests tab. Changed mod code only; no tests changed, skipped, or weakened. Release build succeeded (0 errors, 6 warnings); Debug and Release tests passed (exit 0, NU1900 vulnerability-feed warning). Commit CODEX TB4: Port About to tablet blocked: .git/index.lock permission denied. No push. In-game UI unverified; KSP was not running, but installation is outside the writable workspace and was not attempted. Earlier TB2/TB3 changes preserved.

## TB5 - 2026-10-13 11:36
Ported TRIM into the Flight tablet with a status card, scrollable layout, and TRIM WIN / AUTO TRIM through the existing OnKey handler. Both actions require LIVE LINK and log to ChatLog. Changed mod code only; no tests changed or weakened. Release build: 0 errors, 6 warnings. Debug and Release tests: exit 0; NU1900 vulnerability-feed warning. Marked TB5 complete. Commit CODEX TB5: Port trim to tablet blocked by .git/index.lock permission denied. Earlier tablet changes preserved. In-game UI unverified; installation outside writable workspace not performed. Nothing pushed.

## TB6 - 2026-10-13 11:37
Ported panel 13 Abort / Status to the Flight tablet with live telemetry tiles, autopilot/landing status, and scrollable ABORT / STOP / STATUS controls using existing OnKey handlers. Controls require LIVE LINK and log actions to ChatLog. Mod code only (TabletAbort.cs, TabletAircraft.cs); tests unchanged, none skipped or weakened. Release build passed: 0 errors, 6 warnings. Debug and Release tests passed, no failures; NU1900 vulnerability-feed warning. Marked TB6 complete. Commit CODEX TB6: Port abort status to tablet blocked by .git/index.lock permission denied. Earlier tablet edits preserved; nothing pushed. In-game UI unverified; KSP not running, installation outside writable workspace not performed.

- 2026-10-13 11:55 BLOCKED: pass 1 (TB11a) hit token limit (exceeds the).

- 2026-10-13 12:02 BLOCKED: pass 1 (TB11a) hit token limit (exceeds the).

## TB11a - 2026-10-13 12:06
FLIGHT PLAN stub - create KSPChatMod/TabletFlightPlan.cs by
```n KSPChatMod/TabletFlightPlan.cs | 0
 KSPChatMod/TabletPlanTab.cs    | 0
 2 files changed, 0 insertions(+), 0 deletions(-)
```

- 2026-10-13 12:07 REVERTED TB11a (fd61d62): Aider got the message split into words (PowerShell arg quoting) and only created two EMPTY files; reverted by the next commit, task unchecked. qwen_pass.ps1 now uses --message-file.

- 2026-10-13 12:11 BLOCKED: TB11a build failed; changes left uncommitted (KSPChatMod/TabletWindow.cs, KSPChatMod/TabletFlightPlan.cs, KSPChatMod/TabletPlanTab.cs).

- 2026-10-13 12:33 BLOCKED: TB11b task too big for the local model (~29971 tokens).

## TB11b - 2026-10-13 12:43
FLIGHT PLAN info (read only) - in KSPChatMod/TabletFlightPla
```n KSPChatMod/TabletFlightPlan.cs | 22 ++++++++++++++++++++--
 1 file changed, 20 insertions(+), 2 deletions(-)
```

## TB11c - 2026-10-13 12:48
FLIGHT PLAN controls - in KSPChatMod/TabletFlightPlan.cs add
```n KSPChatMod/TabletFlightPlan.cs | 7 ++++++-
 1 file changed, 6 insertions(+), 1 deletion(-)
```

- 2026-10-13 12:52 BLOCKED: TB12a build failed; changes left uncommitted (KSPChatMod/TabletPlanTab.cs, KSPChatMod/TabletOrbitPlan.cs).

- 2026-10-13 12:59 BLOCKED: TB12b hit the token limit.

- 2026-10-13 13:22 BLOCKED: RK1a build failed; changes left uncommitted (KSPChatMod/RocketMath.cs).

- 2026-10-13 13:36 BLOCKED: RK1b killed by loop protection (timeout > 6 min); edit files restored, new files removed.

## RK1c - 2026-10-13 13:43
Phase angle - in KSPChatMod/RocketMath.cs add `internal stat
```n KSPChatMod/RocketMath.cs | 12 ++++++++++++
 1 file changed, 12 insertions(+)
```

## RK1d - 2026-10-13 13:44
Capture dv - in KSPChatMod/RocketMath.cs add `internal stati
```n KSPChatMod/RocketMath.cs | 8 ++++++++
 1 file changed, 8 insertions(+)
```

## RK1e - 2026-10-13 13:45
Ejection - in KSPChatMod/RocketMath.cs add `internal static
```n KSPChatMod/RocketMath.cs | 15 +++++++++++++++
 1 file changed, 15 insertions(+)
```

- 2026-10-13 13:51 BLOCKED: RK1f killed by loop protection (timeout > 6 min); edit files restored, new files removed.

## RK1g - 2026-10-13 14:03
Burn time across stages - in KSPChatMod/RocketMath.cs add `i
```n KSPChatMod/RocketMath.cs | 18 ++++++++++++++++++
 1 file changed, 18 insertions(+)
```

## RK1h - 2026-10-13 14:04
Suicide burn - create KSPChatMod/SuicideBurn.cs (new), `inte
```n KSPChatMod/SuicideBurn.cs | 14 ++++++++++++++
 1 file changed, 14 insertions(+)
```

## RK1i - 2026-10-13 14:05
Staging policy - create KSPChatMod/StagingPolicy.cs (new), `
```n KSPChatMod/StagingPolicy.cs | 16 ++++++++++++++++
 1 file changed, 16 insertions(+)
```

## RK1j - 2026-10-13 14:05
Fairing policy - create KSPChatMod/FairingPolicy.cs (new), `
```n KSPChatMod/FairingPolicy.cs | 15 +++++++++++++++
 1 file changed, 15 insertions(+)
```

## RK1k - 2026-10-13 14:06
Abort policy - create KSPChatMod/AbortPolicy.cs (new), `inte
```n KSPChatMod/AbortPolicy.cs | 14 ++++++++++++++
 1 file changed, 14 insertions(+)
```

## RK1l - 2026-10-13 14:07
Ascent turn start - create KSPChatMod/AscentProfile.cs (new)
```n KSPChatMod/AscentProfile.cs | 14 ++++++++++++++
 1 file changed, 14 insertions(+)
```

- 2026-10-13 14:08 BLOCKED: RK1m build failed; changes left uncommitted (KSPChatMod/AscentProfile.cs).

## RK2a - 2026-10-13 14:13
StageInfo struct - create KSPChatMod/StageInfo.cs (new), `in
```n KSPChatMod/StageInfo.cs | 20 ++++++++++++++++++++
 1 file changed, 20 insertions(+)
```

- 2026-10-13 14:14 BLOCKED: RK2b build failed; changes left uncommitted (KSPChatMod/BodyInfo.cs).

## TB12c - 2026-10-13 14:30
ORBIT PLAN controls - in KSPChatMod/TabletOrbitPlan.cs add t
```n KSPChatMod/TabletOrbitPlan.cs | 49 +++++++++++++++++++++++++++++++++++++++++--
 1 file changed, 47 insertions(+), 2 deletions(-)
```

- 2026-10-13 14:31 BLOCKED: TB7a build failed; changes left uncommitted (KSPChatMod/TabletOrbitAp.cs).

## TB7t - 2026-10-13 14:43
Orbit tab file - create KSPChatMod/TabletOrbitTab.cs by copy
```n KSPChatMod/TabletOrbitTab.cs | 22 ++++++++++++++++++++++
 1 file changed, 22 insertions(+)
```

- 2026-10-13 15:09 BLOCKED: TB7u build failed; changes left uncommitted (KSPChatMod/TabletWindow.cs).

## TB8a - 2026-10-13 16:26
CAPTURE page - create KSPChatMod/TabletCapture.cs by copying
```n KSPChatMod/TabletCapture.cs | 25 +++++++++++++++++++++++++
 1 file changed, 25 insertions(+)
```

## TB8t - 2026-10-13 16:27
CAPTURE name - in KSPChatMod/TabletOrbitTab.cs append `, "CA
```n KSPChatMod/TabletOrbitTab.cs | 2 +-
 1 file changed, 1 insertion(+), 1 deletion(-)
```

## TB8u - 2026-10-13 16:28
CAPTURE wire - in KSPChatMod/TabletOrbitTab.cs add ONE new l
```n KSPChatMod/MissionStore.cs     | 116 +++++++++++++++++++++++++++++++++++++++++
 KSPChatMod/TabletOrbitTab.cs   |   1 +
 tests/csharp/Aics.Tests.csproj |   1 +
 tests/csharp/RocketTests.cs    |  47 +++++++++++++++++
 4 files changed, 165 insertions(+)
```

- 2026-10-13 16:40 BLOCKED: TB8b build failed; changes left uncommitted (KSPChatMod/TabletCapture.cs).

## TB8c - 2026-10-13 19:26
CAPTURE controls - in KSPChatMod/TabletCapture.cs add the "I
```n KSPChatMod/AscentFly.cs        | 127 +++++++++++++++++++++++++++++++++++++++++
 KSPChatMod/AscentGuidance.cs   |  51 +++++++++++++++++
 KSPChatMod/TabletCapture.cs    |  21 +++++--
 tests/csharp/Aics.Tests.csproj |   1 +
 tests/csharp/RocketTests.cs    |  29 ++++++++++
 5 files changed, 225 insertions(+), 4 deletions(-)
```

## TB9t - 2026-10-13 19:31
DOCKING name - in KSPChatMod/TabletOrbitTab.cs append `, "DO
```n KSPChatMod/AtmoLand.cs         | 139 +++++++++++++++++++++++++++++++++++++++++
 KSPChatMod/AtmoLandMath.cs     |  61 ++++++++++++++++++
 KSPChatMod/TabletOrbitTab.cs   |   2 +-
 tests/csharp/Aics.Tests.csproj |   1 +
 tests/csharp/RocketTests.cs    |  36 +++++++++++
 5 files changed, 238 insertions(+), 1 deletion(-)
```

## TB9u - 2026-10-13 19:33
DOCKING wire - in KSPChatMod/TabletOrbitTab.cs add ONE new l
```n KSPChatMod/SlopeCheck.cs     | 182 +++++++++++++++++++++++++++++++++++++++++++
 KSPChatMod/SlopeMath.cs      | 136 ++++++++++++++++++++++++++++++++
 KSPChatMod/TabletOrbitTab.cs |   1 +
 3 files changed, 319 insertions(+)
```

- 2026-10-13 19:39 BLOCKED: TB9b build failed; changes left uncommitted (KSPChatMod/SlopeMath.cs, KSPChatMod/TabletDocking.cs, tests/csharp/Aics.Tests.csproj, tests/csharp/RocketTests.cs).

- 2026-10-13 19:42 BLOCKED: TB9c killed by loop protection (repeated line: > Add file to the chat? (Y)es/(N)o/(A)ll/(S)kip all/(D)on't ); edit files restored, new files removed.

- 2026-10-13 20:09 BLOCKED: TB10a killed by loop protection (repeated line: > Add file to the chat? (Y)es/(N)o/(A)ll/(S)kip all/(D)on't ); edit files restored, new files removed.

## TB10a - 2026-10-13 20:18
SUN LOCK page - create KSPChatMod/TabletSunLock.cs by copyin
```n KSPChatMod/TabletSunLock.cs | 19 +++++++++++++++++++
 1 file changed, 19 insertions(+)
```

## TB10t - 2026-10-13 20:35
SUN LOCK name - in KSPChatMod/TabletOrbitTab.cs append `, "S
```n KSPChatMod/StageReader.cs    | 34 ++++++++++++++++++++++++++++++++++
 KSPChatMod/TabletOrbitTab.cs |  2 +-
 2 files changed, 35 insertions(+), 1 deletion(-)
```

## TB10u - 2026-10-13 20:36
SUN LOCK wire - in KSPChatMod/TabletOrbitTab.cs add ONE new
```n KSPChatMod/TabletOrbitTab.cs | 1 +
 1 file changed, 1 insertion(+)
```

- 2026-10-13 20:44 BLOCKED: TB10b stopped manually (Grok) - slow repair loop, CS0119 StatusWindow is a type; TabletSunLock.cs restored to 29cae32, task left unchecked.
- NOTE 2026-10-13: commit ff2103d (QWEN TB10t) also contains KSPChatMod/StageReader.cs, which is COPILOT RK2c work swept in by the old commit logic; fixed in qwen_pass.ps1.
