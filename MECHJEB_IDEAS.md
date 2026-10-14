# MechJeb2 ideas for AICS (inspiration only — MechJeb is GPL, no code copied)

Read Oct 12 2026 from github.com/MuMech/MechJeb2 (dev + v2.15.0 for the airplane/spaceplane autopilots, which are no longer on dev).
Effort: S = an evening, M = 1–3 sessions, L = a week+.

## Aircraft / spaceplane
1. **Glide-path aim point instead of fix-to-fix tracking** — MJ autoland steers at "the point on the glideslope ahead at my lateral distance" and blends lateral intercept with an exponential offset multiplier. AICS: on final, command heading/VS toward a moving aim point 1–2 km ahead on the 3° path (our own geometry), so cross-track and glide errors shrink smoothly. (M)
2. **Lateral approach cone + fixed intercept angle** — MJ only accepts the final when inside a 30° cone, else flies an IAP that intercepts at 30°. AICS: replace the many chart-join cases with one rule: "outside the cone → go to an intercept fix at the learned turn radius, intercept at ≤30°". Fits the fly-out-and-turn we just added. (M)
3. **Flare by AoA, then AoA bleed on rollout** — MJ flares to a target AoA at a set height and records touchdown AoA/speed, then lowers the nose exponentially with speed for a soft nosewheel. AICS: drive flare/derotation by measured AoA (learned per plane) rather than pitch only. (S–M)
4. **Speed hold on acceleration, not throttle error** — MJ wants accel = (target−speed)/4 s and PIDs the accel error. AICS: our throttle lag learning would pair nicely with an acceleration-target loop; less overshoot on heavies. (S)
5. **Altitude→VS→pitch cascade with 60-to-1 rule** — MJ converts VS error to a pitch delta with Δpitch = ΔVS / TAS (rad). AICS: use that as the feed-forward under our pitch loop so VS holds are speed-independent. (S)
6. **Small heading errors with yaw, large with roll** — MJ uses rudder for < 0.2° heading error, bank above. AICS: add a rudder trim term on final for the last few degrees (less wing rocking near the runway). (S)
7. **PID integrator seeding/anti-windup** — MJ seeds the integrator from the current stick when a hold engages and clamps it. AICS: seed our holds from current control state to remove the bump when an autopilot takes over. (S)

## Attitude control
8. **Torque/MOI-aware attitude controller** — MJ's BetterController / TorquePI use the vessel's available torque and moment of inertia to set gains, so one tuning works on every craft. AICS: compute available pitch/roll authority (control surfaces × q, reaction wheels) and scale our gains per craft; LEARN can measure the response. (M–L)
9. **Smart A.S.S.-style modes** — named attitude targets (prograde, retro, normal, radial, surface hdg/pitch/roll, target, node, kill rotation). AICS: expose the same as one "point at" tool for chat + MFD keys, using stock SAS where possible. (S–M)

## Rockets / orbital
10. **Autostage rules** — MJ stages on flameout/empty tanks, with pre/post delays, hot staging lead time, drop solids at a TWR fraction, and fairing jettison only below max q / above min altitude / below aero heat flux. AICS: one policy function with these gates (and log why), even without MechJeb installed. (M)
11. **Delta-v / stage stats simulation** — MJ simulates staging for per-stage Δv/TWR/burn time. AICS: use stock `VesselDeltaV` (KSP 1.12 has it) for burn times and "can we make orbit" advice instead of rough estimates. (S)
12. **Closed-loop ascent guidance (PVG)** — MJ's primer-vector guidance optimizes the whole ascent. AICS: a simpler own approach is a gravity turn with apoapsis-targeted pitch program + final circularize node; keep MJ optional for PVG. (L)
13. **Node executor with lead time + ullage/settle** — MJ starts burns at half burn time, throttles down near the end, and handles RCS ullage. AICS: same rules in our maneuver executor using stock Δv data. (M)
14. **Landing: deorbit → course correction → coast → suicide burn → final descent** — MJ uses a reentry simulation to predict the impact point and corrects. AICS: predict impact with a simple drag-free ballistic integrator for airless bodies first; atmosphere later. (L)
15. **Rendezvous phases** — MJ: plane match → Hohmann/phasing → intercept → kill relative velocity at a standoff → close in. AICS: break our rendezvous into those phases with clear chat status lines. (M)
16. **Docking with RCS translation and a safe approach corridor** — MJ aligns ports, moves to a point in front of the target port and approaches on the axis at limited speed. AICS: same concept on our own code for when MechJeb isn't installed. (L)

## Utilities
17. **Deployable controllers** — MJ auto-extends/retracts antennas and solar panels by q / atmosphere. AICS: generalize our new science-antenna safety gate to solar panels too ("retract above q 1 kPa, extend in vacuum"). (S)
18. **Warp helper** — warp to node/SOI/apoapsis with safe drop-out. AICS: a chat tool "warp to X" with guards. (S)
19. **Flight recorder graphs** — MJ records alt/speed/q/Δv over time. AICS: we log lots; a small in-game graph of the last approach (alt vs plan, speed vs plan) would make tuning easier. (M)
20. **Rover autopilot with waypoints** — heading/speed hold with traction/brake control. AICS: reuse taxi code for rover drives. (M)

## Top picks (impact / effort)
1 aim-point glide path, 2 intercept cone, 4 acceleration speed hold, 5 60-to-1 pitch feed-forward, 8 torque-aware gains, 10 autostage gates, 11 stock Δv for burns, 3 AoA flare.
