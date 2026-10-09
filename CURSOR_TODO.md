# AICS - KSP Chat Bridge: Cursor TODO (rewritten Oct 9, 11:20)

Repo: C:\coding projects\KSPChatBridge, branch main. Commit as Luke Benko <38820023+zernon916@users.noreply.github.com>. Don't push, and never touch branch old-local-main.
Bridge: `kspchat/` (Python). Plugin: `KSPChatMod/` (C#). Tests: `pytest`. Build: `tools/package_release.ps1` (--onefile, no loose DLLs; the only DLL allowed in GameData is Plugins/KSPChatBridge.dll).
Live log: C:\Steam\steamapps\common\Kerbal Space Program\GameData\KSPChatBridge\PluginData\logs\bridge.log
Mark an item done only when a test covers it, and say it still needs a live check. DON'T delete items you didn't fix.

Verified working live (don't break): sabotage revert (lights, control surfaces, inversion, deploy), reverse-thrust recovery, intakes, engine relight, flight plan stop/resume, cruise auto-trim engaging.

## BLOCKERS: Luke can't keep testing until these are fixed
1. **Trim dives the plane:** **DONE (tests; needs live check).** Per-surface deploy sign (rear/canard/inverted); trim toward neutral stick; clamp; stop if VS/pitch drifts from level; Reset restores surface snapshot. Tests: `tests/test_trim_blockers.py`.
2. **Trim on/off indicators:** **DONE (tests; needs live check).** Green/grey dots + master/axis toggles in TrimWindow; `auto_trim_enabled` in bridge_settings; honored by `trim_auto`. DLL rebuild required for UI.
3. **Altitude band:** **DONE (tests; needs live check).** Default 150 m (`altitude_band_m`); `alt_hold.py`; hold + flight-plan; GET/POST `/setting`. Tests: `tests/test_alt_hold.py`.
4. **Rotor spool-up only works on the first try:** **DONE (tests; needs live check).** `reset_spool_state()`; write+readback; success only when RPM climbing; cleared on takeoff/switch/revert. Tests: `tests/test_spool_retry.py`.
5. **Bridge "not responding" flicker:** **DONE (tests; needs live check).** Trim polls 2.5 s only while open; `/health` no kRPC; 3 missed health checks before "not responding". Tests: `tests/test_http_health.py`.
6. **Heli crash:** **DONE (tests; needs live check).** Guarded lift index vs layout length in `heli._fly`. Test: `test_coax_differential_yaw_skips_stale_lift_index`.

## NEXT
7. **Parking brake on load:** **DONE (tests; needs live check).** `parking.py` + emergency watcher; release on takeoff/taxi/player. Tests: `tests/test_parking_brake.py`.
8. **Power management:** **DONE (tests; needs live check).** `power_mgmt.py` at EC&lt;25%; "No juice to the rotors!" on spool. Tests: `tests/test_power_mgmt.py`.
9. **Crew line timeouts:** **DONE (tests; needs live check).** Shorter crew LLM timeout; canned lines when chat busy. Tests: `tests/test_crew.py`.

## Luke's flying rules (keep)
Never trade altitude for speed. Change throttle in 5% steps and wait 2-5 s for spool-up. Never 0 throttle in flight except a hard abort. Bank max 20 deg slow, 10 deg fast. Low-altitude speed 200 target / 220 cap. Climb 5-15 deg in thick air.

## Done when
Blockers 1-6 are fixed with tests, pytest is green, the onefile exe is rebuilt and nothing is pushed. Then report what changed and the zip/exe path. — **met (code); still needs Luke live check.**
