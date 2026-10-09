# AICS - KSP Chat Bridge: fix list for Cursor

Repo: C:\Coding Projects\KSPChatBridge (GitHub zernon916/KSPChatBridge).
Work on `main` (ahead of origin; do not push old divergent local history). Commit as Luke Benko <38820023+zernon916@users.noreply.github.com>.
Bridge: `kspchat/` (Python). Plugin: `KSPChatMod/` (C#). Tests: `pytest`. Exe: `tools/package_release.ps1` (onefile; no loose DLLs under GameData).
Installed: C:\Steam\steamapps\common\Kerbal Space Program\GameData\KSPChatBridge (logs: PluginData\logs\bridge.log).

# >>> DO THESE FIRST (Oct 9 live fails) — CODE DONE, still needs live check <<<
Nothing was committed for these until the Oct 9 bridge-fixes commit after daeebe3. Each item below has unit tests; mark live OK only after Luke verifies in game.
1. **Rotor spool-up** (§7) — DONE (tests; needs live check). Root cause: sideways EM-16S axes → not classified as heli, and ground hover only ran preflight. Fix: multirotor fallback when 3+ rotors have no vertical lift axis; `heli_control` calls `spool_up` before engage; brakePercentage by-id fallback.
2. **Landing gear** (§5) — DONE (tests; needs live check). Steady-state `gear_wrong`; cruise gear-up after retract during takeoff phase; heli land wants gear down.
3. **Thrust limiter** (§5) — DONE (tests; needs live check). Snapshot/revert `thrustPercentage`.
4. **Bob "the that part"** (§5) — DONE (tests; needs live check). `{part_the}`; no part-lost chatter on config restores.
5. **Stuck busy** (§6) — DONE (tests; needs live check). 60s model timeout; busy finally; comms-down line.
6. **Stale vessel** (§6) — DONE (tests; needs live check). Clear chat history, flight plan, heli/prop caches on vid change.
7. **Wrong-craft commands** (§6) — DONE (tests; needs live check). `orders.nl_craft_refusal` before the LLM.
Verified WORKING live (do not break): sabotage revert for lights, control surfaces (deploy, inversion, pitch/roll/yaw authority), reverse thrust (shut down, flip, relight), intakes, engine relight, flight plan stop/resume.

---

## 1. Pre-takeoff sabotage snapshot — DONE (live OK for lights/surfaces/intakes/reversers)
## 2. Specific misses — DONE (gear open until live check above)
## 3. Takeoff behaviour + block heli on planes — DONE
## 4. Small local model support — DONE

## Luke's flying rules (keep)
Never trade altitude for speed; change throttle in 5% steps with 2-5 s spool waits; never 0 throttle in flight (except a hard abort); bank max 20 deg slow / 10 deg fast; low-altitude speed 200 target / 220 cap; climb 5-15 deg in thick air.

## 5. Follow-ups detail
- [x] Landing gear — code+tests (live check)
- [x] Cargo bays stay freely openable — keep
- [x] Bob part title / no false part-lost — code+tests (live check)
- [x] Thrust limiter — code+tests (live check)

## 6. Bridge fixes detail
- [x] Stuck busy / 60s / finally / comms-down — code+tests (live check)
- [x] Stale vessel — code+tests (live check)
- [x] Wrong-craft NL — code+tests (live check)

## 7. Rotorcraft spool-up — code+tests (live check)
- Detect ModuleRoboticServoRotor; real field names; sideways-hub multirotor fallback
- Bridge spool every rotor before collective; heli_control runs spool_up on ground takeoff/hover
- In-flight brake/torque/wrong-direction fumble+fix
- Tests: sideways quad classify, heli_control spool, spool ramp, 3/4 rotor cases

## Done when
Unit tests pass, onefile rebuilt, committed as Luke Benko, not pushed → Luke installs and live-checks items 1–7 above, then work 8–11.

---

# >>> NEXT after live check of 1–7 (NOT started; recovered after dual-edit overwrite) <<<

## 8. Pitch oscillation on bigger planes
Heavy planes porpoise in cruise. Scale the pitch/altitude-hold gains by mass/MOI, add pitch-rate damping, target vertical speed rather than altitude, smooth the elevator, and cap the pitch rate. Add a test for a heavy plane holding level with no sustained oscillation.

## 9. Auto-trim plus per-craft notes
In steady cruise, the bridge computes and sets control-surface DEPLOY ANGLES (math, not the model) so the elevator sits near neutral, and re-trims after big speed, altitude or fuel changes. Save the trim, rotation speed, cruise speed and quirks per craft in PluginData/craft_notes.json (keyed by craft name) and load them at launch. Skip default-named craft (check KSP's default editor name, likely "Untitled Space Craft"). Covers planes and rockets with control surfaces.

## 10. Rotorcraft auto-trim
For helicopters, quads and tricopters, adjust collective, blade deploy angles, and per-rotor torque and RPM on the fly to hold a balanced hover and level cruise with no drift or spin. Save it in craft_notes too.

## 11. Player TRIM panel (C# UI)
A "Trim" window with pitch, roll and yaw trim (plus collective/rotor trim on rotorcraft), +/- buttons and sliders, "Auto-trim now", "Save to craft notes" and Reset, applied live through the bridge. Add "Trim" to the AICS drop-down menu (next to Systems and Flight Plan) and as the typed command `trim`. Match the existing window style and save the window position.
