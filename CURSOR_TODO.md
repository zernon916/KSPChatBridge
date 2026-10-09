# AICS - KSP Chat Bridge: Cursor TODO (rewritten Oct 9, 11:20)

Repo: C:\coding projects\KSPChatBridge, branch main. Commit as Luke Benko <38820023+zernon916@users.noreply.github.com>. Don't push, and never touch branch old-local-main.
Bridge: `kspchat/` (Python). Plugin: `KSPChatMod/` (C#). Tests: `pytest`. Build: `tools/package_release.ps1` (--onefile, no loose DLLs; the only DLL allowed in GameData is Plugins/KSPChatBridge.dll).
Live log: C:\Steam\steamapps\common\Kerbal Space Program\GameData\KSPChatBridge\PluginData\logs\bridge.log
Mark an item done only when a test covers it, and say it still needs a live check. DON'T delete items you didn't fix.

Verified working live (don't break): sabotage revert (lights, control surfaces, inversion, deploy), reverse-thrust recovery, intakes, engine relight, flight plan stop/resume, cruise auto-trim engaging.

## LIVE TEST 11:46 (a9abaac): NEW BLOCKERS
A. **Spool-up WRITE now works, but the RPM READ is wrong.** The bridge released Brake 100 -> 0 and Torque 0 -> 100, and the part window shows Current RPM 380.0 (rotors spinning), yet the bridge reads 'Current RPM': '0' from the part fields, so it declares "spool-up FAILED after 45 s" and aborts the takeoff. The AICS Systems panel also shows 0 RPM. Don't trust the KSP part field string for RPM: use kRPC's robotic rotor API (e.g. `part.robotic_rotor.current_rpm` / `RoboticRotor`), or re-query the module fresh each poll instead of a cached Part/Module object. Fix the Systems panel the same way. Add a test with a mocked rotor whose field string stays 0 while the API reports real RPM.
B. **The bridge stops responding / disappears:** after the failed spool and a crash ("Bits are coming off"), there's no AICSBridge process and /health fails, so the panels show "Bridge not responding". Find out why the bridge exits or crashes (check the end of bridge.log and any unhandled exception), and make the plugin restart the bridge automatically if it dies.
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


## NEW FEATURE: Rotors tab in AICS Systems
- Inside the AICS Systems (status) window, add a tab row at the top, in the same style as the AICS menu tabs, e.g. "Overview | Rotors". The Rotors tab lists every rotor on the vessel.
- Work out each rotor's placement relative to the CoM and the control/forward direction, and its orientation (spin axis: lift = vertical, tail/anti-torque = horizontal and sideways, pusher/puller = horizontal and forward).
- Label each one with the short code and the full words, e.g. "LF - Left Front", "RF - Right Front", "LR - Left Rear", "RR - Right Rear", "TR - Tail Rotor", "MR - Main Rotor", "C - Center", "F - Front", "R - Rear", "PU - Pusher".
- One row per rotor: label, part name, spin direction (CW/CCW), live RPM / RPM limit, torque %, brake, motor on/off, and a green/yellow/red status dot. Use the same live RPM source as fix A, not the stale field string.
- Use the same labels in Sidry's emergency lines and the log (e.g. "Left Rear rotor lost RPM!").
- Tests for 1-rotor heli + tail rotor, a tricopter and a quad layout.
- **ARCHITECTURE for the Rotors tab and rotor RPM (and fix A):** the C# plugin reads rotor data DIRECTLY in-game (ModuleRoboticServoRotor: currentRPM, rpmLimit, brake, torque, motor state, direction, part position/orientation relative to the CoM) every ~0.5 s, works out the placement labels itself, and renders the Rotors tab with NO bridge involvement. It then PUSHES a compact rotor snapshot to the bridge (e.g. POST /telemetry/rotors), and the bridge's spool-up check, autopilot and emergency lines use that pushed data instead of polling kRPC. The tab must keep working even when the bridge is down. Apply the same pattern to other cheap in-game state where it makes sense (fewer kRPC round-trips, less lag).

## BIG REFACTOR: mod-side dashboards and an autopilot-only mode
- **All dashboards in the mod:** move EVERY AICS dashboard and status panel (Systems, Rotors, Trim, Flight Plan progress, alarms, fuel/EC, gear, brakes, props, G-load, pilot) to read game state directly in C#, rendering with no bridge involved. The mod pushes compact telemetry snapshots to the bridge (one batched POST every ~0.5-1 s) for the AI, crew chatter and emergencies. The bridge stops polling kRPC for anything the mod already sends. Panels must keep working when the bridge is down.
- **"AI & Bridge" off setting (autopilot-only mode):** a Settings toggle (saved in bridge.cfg/settings) that turns off the AI, crew chatter and the bridge process entirely (no exe launched, no model loaded). The autopilot, flight plan, trim, sabotage revert, parking brake and dashboards still work, and the chat window hides or shows "AI off". This means the core autopilot logic needed in that mode must run in the C# plugin (or a bridge-free path), not only in the Python bridge, so plan the port: list which autopilot functions currently live in Python, and move the essentials (holds, takeoff, landing, trim, spool-up, brake, revert) into C# in stages.
- Do this as staged commits. Stage 1 is the dashboards plus the telemetry push. Stage 2 is the AI-off toggle with what already runs in C#. Stage 3 ports the autopilot core. Report the plan before Stage 3.

## FINAL GOAL: everything inside the mod (NO bridge)
Luke's decision (Oct 9): condense ALL of AICS into the KSP mod. ChatGPT does the coding from this file.
- **Autopilot in C#:** port the bridge's flight code (holds, takeoff, landing, trim, rotor spool-up, sabotage revert, parking brake, emergencies, flight plan) into the plugin, reading game state directly (no kRPC).
- **Dashboards in C#:** all panels render from in-game data.
- **Built-in local model:** load it in the mod via llama.cpp (e.g. LLamaSharp, or the native llama.dll by full path). The default is Qwen2.5-3B-Instruct Q4_K_M running hybrid (about 1 GB GPU, the rest RAM). Settings for GPU / Hybrid / CPU-only plus context length (16-24k). Native DLLs go in PluginData/native/, NOT loose in GameData, or KSP hangs at loading. Download the model on first run into PluginData/models, and never bundle it in the zip.
- **Keep the API option:** Groq/OpenAI/Gemini keys still work as an alternative.
- **AI off setting:** the autopilot and dashboards work with no model loaded.
- **Staged:** (1) dashboards plus AI-off, (2) the autopilot core in C#, (3) the in-mod model, (4) remove the bridge exe. Keep the bridge working until stage 4 passes Luke's live tests. Report a plan before each stage.

## Codex phase plans and resume notes - October 9, 2026

Luke requested that planning and progress stay in THIS file, with a report after each phase. These execution phases follow the five-phase plan in the chat: Phase 1 = RPM/recovery; Phase 2 = dashboards; Phase 3 = AI-off/autopilot port. They subdivide the earlier final-goal stages; the in-mod model and bridge removal remain later work.

Planning record retained below; implementation has since started. Phase 1/2 results and Phase 3 checkpoint are authoritative. Existing DONE labels above are historical unless reverified below. Preserve all existing TODO items and user edits.

After every implementation phase (and before stopping mid-phase), record: completed changes and files, tests actually run and their results, remaining failures, live-check status, commit/build paths if produced, and the exact next step. Mark an implementation item done only with test coverage; retain "needs live check" until Luke validates it.

### Phase 1 - Real rotor RPM and bridge recovery

Status: CODE/TESTS/PACKAGE COMPLETE; needs live check.

Progress (Oct 9): physics RPM sampler + bounded push/cache; stale/session/vessel checks; spool requires distinct fresh samples; typed-API fallback for old plugin. Systems/heli/props use measured RPM. Restart/backoff + exit/exception logs added. KSP IL confirmed currentRPM is a PAW display cache; sampler reads underlying transformRateOfMotion. Log ends in normal /shutdown; reported crash cause unproven. Tests: 468 pytest passed; 12 C# checks passed; Release build passed. Package: dist/KSPChatBridge-0.1.1-phase1.zip; exe: dist/pyi/dist/AICSBridge.exe. Build warning: NuGet vulnerability feed unavailable; package succeeded. Next: Phase 2 dashboards. No live test yet.

Outcome: spool-up and Systems consume actual in-game RPM; a dead bridge is diagnosed and restarted without a restart loop.

Implementation order:
1. Capture a baseline: run the existing offline regression suite, inspect the end of the installed bridge.log and relevant KSP/process diagnostics. Record the crash cause only if evidence establishes it. Review run_bridge.py and HTTP startup/shutdown paths for unhandled errors.
2. Add a C# rotor sampler. Verify the actual ModuleRoboticServoRotor members against installed game assemblies before implementing access. Read RPM, limit, torque, brake, motor/direction and geometry on the game thread about every 0.5 seconds; handle missing robotics modules, destroyed parts and vessel/revert changes.
3. Define a versioned snapshot with session/vessel identity, sequence and stable part IDs. POST compact immutable snapshots to /telemetry/rotors on a worker, with timeouts and at most one request in flight. The Python cache uses local receive time for freshness and rejects old/out-of-order/wrong-vessel data. Missing data is unknown, never a fabricated zero or spool success.
4. Route propulsion.py spool-up/RPM, related heli.py reads, emergency.py rotor reporting and Systems RPM through that snapshot. Inventory remaining rotor reads so the stale field path is not silently retained. Preserve working write/readback controls. Require fresh rising/adequate RPM for the relevant rotors and reset spool state on retry, abort, switch and revert. Establish a bounded telemetry-loss abort for spool-up; do not cut throttle in flight on telemetry loss.
5. Extend BridgeLauncher.cs beyond its current startup-only exit checks. Capture exit code and startup/runtime diagnostics, restart an owned unexpectedly exited process with bounded backoff, and avoid duplicate launches. Distinguish unhealthy-but-running from exited. Respect autostart, intentional shutdown and the future AI-off setting; marshal UI notifications to the game thread.

Validation and exit criteria:
- Add meaningful C# sampler/lifecycle tests with fake game/process adapters and Python telemetry-consumer tests; do not substitute source-text assertions for behavior tests.
- Cover stale field RPM=0 while authoritative RPM is positive, actual zero RPM, stale/missing/malformed/out-of-order snapshots, second takeoff, destroyed rotor, vessel switch and revert.
- Cover unexpected process exit, repeated startup failures, slow health responses, intentional stop and duplicate-start prevention. Extend test_spool_retry.py and test_http_health.py where appropriate.
- Run relevant tests then the full offline pytest suite and C# tests/build (SkipInstall=true). Package the onefile release only after checks pass; record exact artifact paths and any blockers.
- Luke live check: two spool/takeoff attempts, compare Systems RPM to the part window, then verify recovery after an unexpected bridge exit. Test failures without risking an active flight.

Resume checkpoint: planning saved; first implementation action is the baseline/log investigation. No blocker is marked fixed by this plan.

### Phase 2 - In-mod dashboards and shared telemetry

Status: DASHBOARD BASE IMPLEMENTED/TESTED/PACKAGED; needs live check. Controller-owned displays/alarm parity continue in Phase 3.

Progress (Oct 9): Systems physical rows + alarms local; Overview/Rotors tabs, control-frame labels, physics RPM/direction/limits/torque/brakes/motor. Batched resources/temperature reused by bridge; physical trim local, unavailable controller actions disabled; disconnected plan status explicit. Tests: 470 pytest; 24 C# checks; Release build passed. Phase 1 commit: 3166574. Remaining: finish controller-state/collective/advanced alarm parity in Phase 3; broader removal of legacy polling follows controller migration. Package: dist/KSPChatBridge-0.1.1-phase2.zip (bridge exe: dist/pyi/dist/AICSBridge.exe). No live test.

Outcome: physical vessel status and rotor panels render from local C# state even while the bridge is down. Controller-owned progress becomes fully local as its controller moves in Phase 3.

Implementation order:
1. Inventory StatusWindow.cs, TrimWindow.cs, AicsMenu.cs and ChatWindow.cs data dependencies. Separate game measurements, derived alarms, controller state and AI text. Define a shared local vessel snapshot plus explicit unavailable/stale states; do not infer active autopilot or flight-plan progress from vessel motion.
2. Extend the Phase 1 sampler to fuel/EC, engines, intakes, surfaces, gear/brakes, props, temperatures, G-load and pilot state. Read Unity/KSP objects on the game thread and use measured sampling budgets. Cache part discovery and invalidate on vessel/part/scene changes; avoid rescanning every GUI repaint.
3. Build the Systems Overview/Rotors tab row in the existing menu style. Classify spin axes and position relative to CoM and the selected control frame; define tolerances and a documented viewing convention for CW/CCW. Display short/full placement labels, part name, RPM/limit, torque, brake, motor and status dots. Handle symmetric, center, tail and pusher layouts without forcing ambiguous rotors into incorrect labels.
4. Move physical status rendering and deterministic alarm evaluation into local services. Display local trim measurements and settings where available, but clearly disable bridge-dependent commands when unavailable. Retain working panels when HTTP requests fail. Until Phase 3 owns flight-plan/hold state, show last received controller status as unavailable/stale after disconnect, never as continuing progress.
5. Consolidate outgoing data into one versioned telemetry batch every approximately 0.5-1 seconds, including rotor data. Keep /telemetry/rotors compatible during migration. Bound payload/request size and queued work; send latest state instead of replaying a backlog after reconnection.
6. Update Python status, crew and emergency consumers to reuse telemetry for migrated fields and use the exact C# rotor labels in speech/logs. Remove redundant kRPC polling field by field after parity checks. Preserve control writes and any genuinely unmigrated reads until their replacements exist.

Validation and exit criteria:
- C# behavior tests for main+tail heli, tricopter, quad, center/pusher and changed control orientation; test geometry transforms, labels and status thresholds.
- Test bridge unavailable, no active vessel, missing resources/modules, scene changes, destroyed parts and reconnection; verify stale controller status cannot look live.
- Python contract tests for batched data and shared rotor names. Verify migrated consumers do not issue redundant kRPC reads, and transport cannot queue unbounded requests.
- Run regression suites and C# build/tests, package a reviewable release, and record outputs here. Luke live check: open all panels with bridge running, stop the bridge, confirm local values still change and restart cleanly.
- Explicit dependency: fully local flight-plan progress and autopilot indicators are not complete until Phase 3 supplies local controller state. Keep that requirement open rather than marking every dashboard complete early.

Resume checkpoint: planning saved; after Phase 1 validation, start with the panel dependency inventory. No UI changes or telemetry implementation have been made during planning.

### Phase 3 - AI-off and the C# autopilot core

Status: IN PROGRESS (3A/3B foundation, partial 3C-3F). Not complete; needs live check.

Progress (Oct 9): Phase 2 commit b583eee. Guarded AI-off handoff; native holds/trim/parking; measured-RPM spool; initial takeoff/heli/runway states; limited deterministic plans + local progress. Manual input/vessel switch/errors pause plans; strict syntax rejects unsupported options. Power recovery + cancellable staged-engine restart added. AI-off blocks plan drafting; unsupported templates explicit. Bridge remains default. Checks: 473 pytest; 194 C# behavior checks; Release build passed (NU1900 vulnerability-feed warning only); diff check clean. No Phase 3 package/live test yet.

Checkpoint commit: 74e33b6 (native foundation, local only; not pushed). Recovery checkpoint: delayed surface/intake/thrust-limit recovery; unambiguous engine-mode reverse recovery; own-trim baseline updates; detached-part protection. Release build passed; 210 C# checks passed (includes 6 production recovery checks with fake game modules); diff check clean. Python unchanged since 473-pass run. No live verification.

Continuation: custom named reverser events + rollout (mixed-engine/bounce guards); lights/groups/resource-flow recovery tracks own writes; roll/yaw trim + input-state sync; collective display; spherical terrain look-ahead; local taxi with timeouts. Checks: 245 C# passed; Release build passed (same NU1900 warning); Python unchanged since 473-pass run. Recovery commit: e9d339e. No live test/install.

Remaining: stall learning/flap sequencing, vertical landing, helicopter layout/rotor-trim parity, full plan grammar/spots/settings migration, shared AI-to-C# dispatch and controller integration tests. Current native flight code is a partial port, not parity-complete. Phases 4-5 not started.

Outcome: holds, takeoff, landing, trim, rotor control, sabotage restoration, parking, emergencies and flight plans operate with no bridge process or loaded model. Retain the bridge for optional AI and remaining legacy features until the later removal stage passes live tests.

Python responsibility inventory / intended C# migration order:
- Ownership and dispatch: guard.py, orders.py and ksp_actions.py; inspect their controller entry points and cross-process abort behavior before defining the C# command boundary.
- Ground/propulsion: parking.py, propulsion.py, power_mgmt.py, reversers.py, engine_restart.py and taxi.py.
- Holds and trim: hold.py, alt_hold.py, trim_auto.py, speedcap.py, maxspeed.py and protect.py.
- Flight controllers: takeoff.py, heli.py, plane.py, lander.py and rollout.py; landing location support in spots.py.
- Emergency detection and restoration: emergency.py plus propulsion sense/snapshot restoration helpers; preserve the tested sabotage and reverse-thrust recovery behavior.
- Plan parsing/execution: flightplan.py; preserve templates, validation, stop/resume and progress reporting.
- Follow-up inventory: docking.py and MechJeb/orbital actions in ksp_actions.py need explicit bridge-free adapters or ports before claiming full feature parity/removing the bridge. Science and remaining AI/chat features also stay on the later removal checklist.

Implementation substages (small staged commits):
3A. Define deterministic controller interfaces, vessel-specific state, one control owner, command validation and abort/manual override. Keep pure control calculations separate from KSP reads/writes for tests. Run flight control at the appropriate game/physics cadence, not the 0.5-second dashboard sampling rate. Reset ownership and cached references on vessel switch, revert, scene exit and destruction. Python must relinquish a migrated controller before C# can acquire it; prevent legacy MCP/HTTP commands from bypassing ownership.
3B. Add persisted AI & Bridge off in local configuration and the settings UI. Suppress bridge launch/restart, AI requests, chatter and model loading; show AI off in chat. During migration, advertise only ported capabilities and explicitly disable the remainder. Do not silently kill a Python controller mid-flight: require safe ownership handoff or defer the switch until the active controller stops. Stop only the bridge instance owned by this plugin. Test restart/settings persistence and toggle transitions.
3C. Port parking, rotor spool-up/brakes/torque, engine relight and reversers, then sabotage snapshots/restoration and EC protection. Preserve original surface settings and per-surface signs; distinguish commanded changes from damage. Validate repeated spool and recovery before integrating takeoff.
3D. Port heading/attitude/altitude/speed holds and trim with shared limits. Preserve the 150 m default altitude band, neutral-stick trimming, clamps, drift stop and reset snapshot. Encode Luke's exact flight rules in a shared policy with regression tests: altitude is not traded for speed; throttle 5% steps with 2-5 seconds settling; no zero in flight except hard abort; 20-degree slow/10-degree fast bank limits; low-altitude speed target 200/cap 220; thick-air climb 5-15 degrees.
3E. Port taxi/takeoff and helicopter modes, then plane approach/flare/rollout and vertical landing. Use explicit states with bounded transitions, abort handling and degraded-sensor behavior. Preserve feasibility checks, stall-speed adaptation, flap/gear sequencing, rotorcraft layout handling and safe control handoffs. Validate each aircraft family separately before enabling it by default.
3F. Port emergency responses and flight-plan grammar/execution, including pause/stop/resume, templates, landing spots and persisted settings migration. Move controller progress and indicators into the local dashboard service. Keep deterministic plan editing/execution usable with AI off; AI-assisted drafting remains optional. Route bridge AI tool requests to the same validated C# command layer for ported actions, rather than maintaining two independent flight implementations.

Validation and exit criteria:
- Establish a C# test project for pure control logic and fake game adapters compatible with the production target. Reuse representative input/output cases from Python tests to detect behavioral changes; keep existing Python tests for the still-supported bridge.
- Test exclusivity across entry points, abort/manual override, lifecycle resets, AI-off persistence, no bridge/model launch while off, trim reset/signs, repeated spool-up, restoration, throttle/bank/altitude limits and plan stop/resume.
- Add controller transition tests for takeoff, approach/flare, touchdown and helicopter failures. Verify no migrated control path requires kRPC or HTTP to operate.
- Run full offline Python regression tests, C# behavior tests and Release build with SkipInstall=true. Package with the existing script; record commit IDs (requested identity, no push), test results and exact zip/exe/DLL paths after each implemented substage.
- Luke live checks in steps: ground functions; holds/trim; repeated takeoff; helicopter layouts; plane and vertical landings; sabotage recovery; complete flight plan with AI off and bridge absent. Preserve a working fallback build until these pass.
- Phase 3 is complete only when the listed core functions and local controller dashboards work with AI off. Keep unported orbital/docking/other features explicitly tracked; do not call the whole mod bridge-free yet.

Resume checkpoint: all three requested phase PLANS are saved. No implementation, test run, build or commit was performed in this planning pass. Next implementation step is Phase 1 baseline tests and crash-log investigation, followed by the rotor sampler/telemetry contract. Later work remains in-mod AI, full parity audit and bridge removal after Luke's live acceptance.

### Phase 4 - In-mod local AI and cloud API alternatives

Status: PLAN COMPLETE; IMPLEMENTATION NOT STARTED. Depends on Phase 3's local command/ownership boundary. The bridge remains available throughout this phase.

Outcome: chat and crew AI run inside the mod with an embedded local inference runtime or a configured cloud API; autopilot and dashboards stay independent of AI availability.

Implementation substages (report each before implementation):
HARD RULE for 4A-4F: KSP loads EVERY .dll anywhere under GameData as a plugin and hangs at LOADING PARTS on native ones (this already happened once with the PyInstaller build). Native llama.cpp DLLs must live outside the scanned path or under a renamed extension (e.g. PluginData/native/*.bin, then copied or loaded by full path via LoadLibrary), and a packaging check must fail the build if any .dll other than Plugins/KSPChatBridge.dll (and approved managed wrappers) is in the zip.
4A. Prove runtime compatibility before committing to a wrapper. The current plugin targets net472/C# 7.3; inspect installed KSP/Unity runtime and verify current official llama.cpp/wrapper documentation, supported native ABI, redistribution licenses and Windows x64 dependencies. Build a minimal load/generate/cancel/unload experiment in KSP. Choose a compatible managed wrapper only if demonstrated; otherwise use a small version-pinned native interop layer. Resolve native dependencies explicitly under PluginData/native/ and verify startup without placing support DLLs loose in GameData. Record the exact tested versions and deployment layout here. Native faults may terminate KSP; managed exception handling is not proof of crash isolation, so this compatibility experiment is a release gate.
4B. Add a model manager. Use the requested Qwen2.5-3B-Instruct Q4_K_M as the target default, verify its official source/license and pin the selected file/checksum. On first use of local AI, download to PluginData/models with visible progress, cancellation, bounded retry and an atomic final rename after verification. Handle interrupted downloads, corrupt files, insufficient disk space and offline first launch. Never bundle model weights in the release zip, and never download/load a model while AI is off.
4C. Implement CPU-only, Hybrid and GPU settings plus the requested 16-24k context options. Treat approximately 1 GB GPU usage as a target to measure, not a guarantee: include context/KV-cache and runtime overhead in budgeting. Benchmark alongside KSP, bound generation/context growth and fall back to a tested lower-memory configuration with a clear status when allocation fails. Do not silently claim the requested context or offload mode was applied. Keep a working CPU path on systems without compatible GPU support.
4D. Port chat orchestration from chat.py/backends.py and related language/talk/personality/crew modules into a provider-neutral C# service. Use a bounded worker queue, cancellation, request deadlines and main-thread delivery of UI updates. All game actions pass through Phase 3's validated command layer on the game thread; model output cannot write game state directly. Preserve tool-call validation, limits on tool loops, actual-result reporting, command intent checks, crew timeouts and canned fallbacks. Prioritize user chat over optional crew chatter.
4E. Preserve Groq/OpenAI/Gemini API-key alternatives and inventory other existing provider modes before replacing them. Verify each provider's current official request/response contract during implementation; do not assume identical schemas. Port masked key display, save/clear behavior and settings migration without logging secrets. Add bounded rate-limit retries, request cancellation and actionable authentication/offline errors. Never send local conversations to a cloud provider automatically after a local-model failure.
4F. Wire settings/chat UI to local services, including provider/model selection, effective context/offload status, download/load state and AI off. Unload AI resources safely when disabled, stop queued chatter and leave flight control running. Preserve conversation/memory behavior with explicit versioned migration and avoid duplicate replies from the legacy bridge. Keep exactly one AI backend owner during the transition.

Validation and exit criteria:
- C# provider tests using fake responses: text and tool replies, invalid arguments, oversized/invalid output, tool-loop limits, cancellations, timeouts, rate limits, missing/rejected keys and no secret leakage.
- Model-manager tests: cancelled/resumed/corrupt download, checksum failure, disk error, missing native dependency, unavailable GPU, low memory, repeated load/unload and AI-off without network/model activity.
- Behavioral parity cases from test_backends.py, test_keys.py, test_crew.py, test_small_model.py and chat/tool-choice tests; assert claimed actions match actual command results.
- Run the offline Python regression suite while the bridge is supported, C# tests and Release build with SkipInstall=true. Verify package layout and license notices; record exact artifacts and runtime/model versions.
- Luke live check: first-run local setup, CPU and supported Hybrid/GPU modes with measured RAM/VRAM/frame-time impact, chat tool execution during flight, provider switching, repeated AI-off/on and continued autopilot after inference/network failure. Test cloud integrations only with configured credentials and account for actual usage.
- Phase 4 is complete only after inference works in the actual KSP runtime without blocking controls and cloud alternatives pass validation. Keep the bridge fallback and mark live checks outstanding until performed.

Resume checkpoint: Phase 4 planning saved; no runtime selected/downloaded, model loaded, API called, implementation changed, test run or build produced in this planning pass. When earlier phases pass, begin with the net472/Unity/native-runtime compatibility experiment.

### Phase 5 - Full parity, live acceptance and bridge removal

Status: PLAN COMPLETE; IMPLEMENTATION NOT STARTED. Depends on Phases 1-4 plus Luke's live acceptance of the bridge-free release candidate BEFORE removing the bridge from the final package.

Outcome: all supported AICS features run in the mod, with no AICSBridge.exe/Python/kRPC dependency for core operation. Local AI, cloud AI and AI-off are supported. Retain source history and a known working fallback release.

Implementation substages (small staged commits; report before each):
5A. Perform a full parity audit. Enumerate Python TOOLS, HTTP routes, MCP tools, background watchers, chat commands, settings, menus and packaging scripts; give each an in-mod replacement, behavioral test and live-check status. Specifically include science automation, orbital/ascent/deorbit/transfer actions, docking/rendezvous, launch/recover/stage, landing spots, craft notes, memory/personality, crew events, logging and settings/key migration. Every unresolved supported feature blocks the claim that ALL AICS moved into the mod; do not silently drop it or delete its TODO entry.
5B. Finish residual ports. Use direct game APIs or tested optional MechJeb integration instead of Python/kRPC for outstanding orbital/docking actions. Test both with and without MechJeb and keep dependency requirements visible in the affected controls. Port science watcher modes, EC thresholds and experiment safeguards, plus remaining persistence/events and command routing. Keep one controller/AI owner and the validated Phase 3 action boundary across every entry point.
5C. Resolve external integrations explicitly. The existing desktop MCP setup launches the bridge over stdio, so removing the exe breaks it unless replaced. Evaluate an in-mod supported transport against the actual client capabilities during implementation; retain tool names/semantics where practical, with bounded requests, loopback access by default and main-thread action dispatch. Verify chat pending/wait/reply and cancellation. If equivalent support is not viable, keep removal blocked and document the decision needed; do not present an external helper process as meeting the no-bridge goal or change the user's client configuration without a concrete migration plan.
5D. Build a bridge-free release candidate with the legacy fallback still retained separately. Add versioned, repeatable migration of existing PluginData settings, keys, saved locations, craft data and memory; preserve originals and never overwrite unrelated user data. Confirm clean install, upgrade and rollback behavior. Exercise the candidate without Python, a running bridge, kRPC or external local-model server. Verify that no hidden launch/polling path remains active and that AI-off does not load or download the native runtime/model.
5E. Run the acceptance matrix and record Luke's results here before final removal: local and cloud chat; AI-off; all dashboards; rotor layouts and repeat spool; holds/trim; takeoff/landing; flight-plan stop/resume; sabotage/reverse-thrust recovery; power/parking; science; orbital/docking where supported; scene/vessel changes and revert; save/reload; offline/model/API failures. Distinguish automated evidence from live results and retain every outstanding failure. Do not infer acceptance from silence or automated test success.
5F. After acceptance, remove bridge launch/watchdog/shutdown and obsolete HTTP polling/telemetry transport from the shipping plugin, retaining local snapshot services. Remove AICSBridge.exe and Python packaging from the release script, and update dependency/CKAN metadata only where the dependency has genuinely disappeared. Keep KSPChatBridge.dll under Plugins, native inference dependencies only in the validated PluginData/native layout, and model weights excluded. Do not delete the user's installed executable/data or historical Python source as an incidental cleanup; retain recoverable source history and rollback artifacts.
5G. Update README, installation/settings documentation, troubleshooting, external-client migration instructions, licenses, version metadata and release manifest/checksums. Record final DLL/zip paths and verified contents. Mark replaced onefile-exe packaging instructions as historical/superseded for the bridge-free release so future work does not rebuild or ship it accidentally. Use the requested commit identity, never touch old-local-main and do not push or publish.

Validation and exit criteria:
- Complete the parity checklist with C# behavior/integration tests for every migrated responsibility. Keep Python regressions running until the fallback is retired; then document which equivalent C# tests replace them rather than claiming pytest alone validates the new architecture.
- Add package-content checks: no bridge exe, no bundled model, no loose support DLLs, expected native assets/licenses only, and no mandatory kRPC dependency in core paths. Audit process starts and HTTP calls for leftover bridge dependencies.
- Test clean installation, upgrade with existing user data, repeated migration and rollback; verify secrets stay masked and migration logs do not reveal keys.
- Run C# tests and Release build with SkipInstall=true; produce and inspect the final archive. Re-run targeted smoke checks after packaging/removal changes and record results/artifact paths here.
- Luke's bridge-free live acceptance is a required gate before final removal. If unavailable or failed, record the exact blocker and retain the working bridge/fallback; do not mark Phase 5 complete.
- Final completion report: implemented parity, automated results, Luke's live results, remaining optional dependencies/limitations, commit IDs and final zip/DLL paths. No exe path is expected in the final bridge-free package.

Resume checkpoint (implementation): Phase 1 commit 3166574; Phase 2 b583eee; Phase 3 foundation 74e33b6, recovery e9d339e. Packages retained under dist. New native files: NativeReversers/ReverseRollout/TaxiMission; checks above passed. DLL: KSPChatMod/bin/Release/net472/KSPChatBridge.dll. Next: saved spots/settings migration, then remaining Phase 3 parity listed above. No live checks, installation, push, model download or Phase 4/5 implementation. Keep legacy fallback.

