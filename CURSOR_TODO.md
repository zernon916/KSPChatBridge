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

Status: CODE/TESTS/PACKAGE ADVANCED; PARTIAL PARITY; needs live check. Bridge fallback retained.

Progress (Oct 9 continuation): Vertical landing hardened (idle thrust capacity, terrain clearance floor, persistent thrust-loss abort); helicopter `Classify` for main+tail/quad/soft-lift; plan rejects orbital/fly-to/touch-and-go + taxi route validation; `NativeCommands` + `PluginData/native_control.json` exclusive ownership blocks bridge steer tools. Commits: `5ab9ea6` (descent checkpoint), `30dc788` (integration). Package: `dist/KSPChatBridge-0.1.1-phase3.zip`. Checks: 474 pytest; C# suite includes Phase 3 integration policies (12) + prior blocks; Release + package DLL scan passed (only Plugins/KSPChatBridge.dll). Still unported: targeted/orbital vertical landings, parachutes, touch-and-go execution, full fake-game controller lifecycle against Vessel, circuits beyond heading integral. No live acceptance.

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

Status: HTTP IN-MOD CHAT WIRED (testable) — ChatWindow → InModAiHost → OpenAiBackend/InModChatSession tool loop; SecretsStore PluginData/.env; ModelManager download + AICS Settings (native_chat toggle, keys, offload/context). Bridge remains fallback when native_chat is off or bridge is healthy. Embedded llama (4A LoadLibrary/generate) still gated — no LLamaSharp NuGet in GameData. Claude/Grokbot remain stub backends.

Compatibility notes (4A research, Oct 9): Plugin targets net472/C# 7.3 (KSP Unity Mono). LLamaSharp supports net472; Unity/KSP requires natives outside GameData scan — use `PluginData/native/*.bin` + full-path `LoadLibrary` (`NativeAiLoader`, `NativeLibraryLayout`). LLamaSharp 0.19.0 has known Unity CUDA reports; newer backends need CPU ggml copy workarounds. Candidate wrapper recorded in `NativeAiLoader.CandidateWrapper`. Target model: `Qwen2.5-3B-Instruct-Q4_K_M.gguf` via `ModelManager`. Offline: `AiRuntimePolicy` + `ChatOrchestrator` + packaging detectors. Live 4A gate and embedded generate still outstanding; HTTP cloud/LM Studio/Ollama path is the testable stub finish.

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

Resume checkpoint: Phase 4 HTTP stubs finished and wired (SecretsStore, OpenAiBackend, InModChatSession, InModAiHost, ChatWindow DispatchChat, settings UI, AI-off unload). Next: Luke live check with native_chat + a cloud/LM Studio key; then 4A LoadLibrary/generate with PluginData/native/*.bin. Do not wire LLamaSharp NuGet into GameData until 4A passes.

### Phase 5 - Full parity, live acceptance and bridge removal

Status: PARITY AUDIT STARTED; BRIDGE REMOVAL BLOCKED. Depends on Phases 1-4 plus Luke's live acceptance before removing AICSBridge.exe.

#### 5A Parity audit (92 BY_NAME tools, Oct 9)
Native-ported / exclusive when busy (via NativeCommands + ownership file; AI-off Execute): abort, stop_current, plane_hold, takeoff, land_here, land_plane, land_at_spot, heli_control, taxi_to, get/set trim family, save_craft_notes, save_landing_spot, gear/brakes/lights/rcs/sas, set_heading/set_speed, autopilot_status/get_status, flightplan/check|fly|stop (+ resume listed, thin).
Bridge-only / unported (blocks bridge-free claim): mechjeb_ascent, circularize, transfer_to, deorbit_burn, warp_*, dock_with, station_keep, launch_*, recover_vessel, stage, run_science/reset_experiments/set_science_watcher, deploy_parachutes, fly_to/fly_to_place, touch_and_go/go_around, circle_here, capture_plan, orbital apo/peri/inclination helpers, eject_kerbal (mod !cmd path exists), prop_control, afterburner, engine_mode, flaps (partial native flaps exist separately), captain_order, remember_preference, set_ai_name, set_override, authorise_all, fuel/delta-v/landing ETA reports, list_* helpers, trim_panel_open, abort_ag/action_group, sun_lock/antenna_lock, sync_orbit_altitude, match_target_plane, launch_to_target_plane, time_to_target, how_far, landing_check, crew_report, flight_report, damage_report, cut_engines, set_engines, set_throttle, set_altitude, level_off, land/land_at/land_at_ksc aliases, set_flight_plan (bridge), set_sas_mode.
MCP/desktop: still launches bridge over stdio — equivalent in-mod transport not chosen (5C blocked).
Removal of AICSBridge.exe: **not performed**; fallback required until live acceptance matrix (5E) passes.

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

Resume checkpoint (implementation): Phase 1 `3166574`; Phase 2 `b583eee`; Phase 3 through `30dc788` + package `dist/KSPChatBridge-0.1.1-phase3.zip`; Phase 4/5 foundations pending commit with this HANDOFF. Bridge fallback retained. No live checks, no model download, no bridge removal.

## HANDOFF

Last commits: `5ab9ea6` (powered-descent), `30dc788` (Phase 3 harden + ownership), `12b7991` (Phase 4/5 foundations + this HANDOFF). No push; `old-local-main` untouched.

Finished this session:
- Uncommitted vertical-landing work tested (473 pytest → 474; C# green) then committed.
- Phase 3 NEXT STEPS advanced: thrust/terrain/abort on powered descent; heli Classify/labels; plan rejects + taxi validation; NativeCommands + native_control.json exclusive ownership; packaging `dist/KSPChatBridge-0.1.1-phase3.zip` (bridge exe retained; single Plugins DLL).
- Phase 4 offline foundations: NativeLibraryLayout, ModelManager finalize/checksum/AI-off, AiRuntimePolicy offload/context, ChatOrchestrator queue/tools, NativeAiLoader LoadLibrary-by-.bin path (no model download, no live llama load).
- Phase 5A parity audit recorded for all 92 BY_NAME tools; bridge removal explicitly blocked pending live acceptance and remaining ports (orbital/docking/science/MCP).

Still needs Luke live check: spool RPM panel, bridge restart, AI-off flight, powered descent, panels with bridge down. Not done: in-KSP llama experiment, model download UI, cloud provider C# ports, touch-and-go/orbital landings, MCP replacement, shipping without AICSBridge.exe.

NEXT STEPS:
1. Live-validate Phase 3 package (AI-off holds/takeoff/heli/descent; kill bridge and confirm panels + ownership file).
2. Phase 4A gate: install pinned natives as PluginData/native/*.bin and prove load/generate/cancel inside KSP once.
3. Finish 4B–4F (download manager network path, settings UI, providers) only after 4A passes.
4. Phase 5: port remaining audit blockers; keep bridge until 5E acceptance; then 5F removal.

Tests and checks (this session):
- Passed: pytest 474 (includes native_control steer block).
- Passed: C# behavior suite (prior + 12 Phase 3 integration + 14 Phase 4 AI policy).
- Passed: Release `-p:SkipInstall=true`; package `KSPChatBridge-0.1.1-phase3.zip` with only `Plugins/KSPChatBridge.dll` as DLL.
- Not run: KSP install/startup, live flight, real model download, GPU/Hybrid inference, bridge-free package.

Artifacts:
- Zip: `C:\Coding Projects\KSPChatBridge\dist\KSPChatBridge-0.1.1-phase3.zip`
- Exe: `GameData/KSPChatBridge/Bridge/AICSBridge.exe` (inside zip)
- DLL: `KSPChatMod/bin/Release/KSPChatBridge.dll`
Keep the legacy bridge fallback available.

## PRE-LIVE REVIEW LOG (Oct 9) — Phases 1–5

Reviewer: Cursor agents + human verification of MUST-FIX items. Scope: code and CURSOR_TODO claims before Luke’s live check. **No fixes applied in this pass** (log only). Severity: MUST-FIX / HIGH / NIT / OVERCLAIM / ABNORMALITY.

### Method
- Parallel agents reviewed Phase 1 (RPM/recovery), Phase 2 (dashboards/telemetry), Phase 3 (AI-off/autopilot), Phase 4–5 (AI stubs + parity).
- Independently confirmed: rotor Label argument mix-up, telemetry ID matching, `set_speed` ownership bypass, `flightplan/resume`/`status` Ported-without-handler, `PollSystems` dead, `NativeReady` Awake gate.

---

### Phase 1 — Real rotor RPM + bridge recovery

| Sev | Finding | Evidence |
|-----|---------|----------|
| MUST-FIX | Bridge matches rotors only by `rpc_part_id` / kRPC `_object_id`. If RpcId is empty, snapshots arrive but `rotor()` returns seen+no row → unknown RPM; typed API fallback never runs once “seen”. | `kspchat/telemetry.py` `rotor`/`state`; `RotorTelemetry.cs` sends `part_id` + optional `rpc_part_id` |
| MUST-FIX | `_live_rotor`: any accepted snapshot sets `seen=True` and blocks `part.robotic_rotor` fallback — broken telemetry worse than none. | `kspchat/propulsion.py` `_live_rotor` |
| MUST-FIX | Spool coerces unknown RPM with `float(c["rpm"] or 0.0)` — fabricates zero; contradicts “missing ≠ zero”. | `propulsion.py` spool_up |
| MUST-FIX | No bounded telemetry-loss abort during spool (Phase 1 plan required it). | spool wait loop only |
| MUST-FIX | Bridge recovery watches `Process.HasExited` only; hung-but-alive process not restarted. After ~20s health fail, process may be left zombie so restart never fires. | `BridgeLauncher.cs` |
| HIGH | Telemetry push skipped when AI off — native/AI-off never feeds Python (OK if AI-off doesn’t use bridge; bad if bridge left running). | `RotorTelemetry.cs` `if (!AiEnabled) return` |
| HIGH | Vessel switch clears propulsion/heli caches but not `telemetry.reset()`. | `emergency.py` |
| NIT | `transformRateOfMotion` assumed to be RPM units; no captured IL note in-repo. | `RotorMeasurements.cs` |
| NIT | RestartPolicy unit-tested; launcher process/HTTP integration not. Double `Failed()` backoff possible. | `RestartPolicy.cs` / `BridgeLauncher.cs` |
| OVERCLAIM | Phase 1 “CODE/TESTS/PACKAGE COMPLETE” without live check; original PAW-vs-bridge RPM bug may still hit via ID mismatch. | CURSOR_TODO Phase 1 status |
| ABNORMALITY | TODO still cites phase1 zip naming; `package_release.ps1` versions generically; test counts in Phase 1 progress line are stale vs current suite. | docs drift |

**What looks solid:** C# physics sampler via `transformRateOfMotion`; duplicate-sample spool guard; core telemetry contract tests for stale field vs authoritative RPM when IDs match.

---

### Phase 2 — In-mod dashboards + batched telemetry

| Sev | Finding | Evidence |
|-----|---------|----------|
| MUST-FIX | **Rotor placement labels wrong in flight.** Frame stores Position/Axis as `(right, up, −forward)` but `RotorPlacement.Label(right, forward, axisRight, axisForward, axisUp)` is called with `(x, y, ax, ay, az)` — **up passed as forward, −forward as axisUp**. Lift count uses `Abs(Axis.z)≥.7` (forward), not up. Unit tests call Label with correct semantics; live wiring does not — tests would not catch this. | `RotorTelemetry.cs` ~72–79; `RotorPlacement.cs` signature |
| MUST-FIX | `PollSystems()` never called; MAYDAY `mayday_lights` veto from bridge never applied to in-game Systems blink. | `StatusWindow.cs` |
| MUST-FIX | Empty `rpc_vessel_id` ⇒ Python `state()`/`rotor()` ignore batch (ties to Phase 1 ID issue). | `telemetry.py` |
| HIGH | AICS menu panels gated on `bridgeOk`; when AI off, health not polled → many panels stay disabled even though native tools exist. | `AicsMenu.cs` DepsOk / PollBridge |
| HIGH | Status window mixes local physics rows with unlabeled bridge `/status` controller memory (heading/phase/autopilot) — can look “live” when stale. | `StatusWindow.cs` DrawStatus; `status.py` |
| NIT | Dead `PollSystems`; Trim header still says POST for physical sliders; dual Overview+Rotors duplicate; Python motor_label vocabulary ≠ C# LF/MR codes. | various |
| NIT | Emergency watcher still kRPC-heavy each tick; batch only covers resources/temp/rotors partially. | `emergency.py` `_sample` |

**What looks solid:** Local Systems/Rotors sampling path when AI off for physical rows; placement API tests (API only); telemetry age/stale session tests when IDs match.

---

### Phase 3 — AI-off + C# autopilot (includes this session’s work)

| Sev | Finding | Evidence / owner |
|-----|---------|------------------|
| MUST-FIX | **Ownership hole:** `NATIVE_EXCLUSIVE` only checked inside `call_tool`. `set_speed` / `set_altitude` / `set_throttle` call `plane_hold`/`heli_control`/kRPC **directly** and bypass the busy check. HANDOFF “exclusive ownership” overstated. | `ksp_actions.py` (~1558+); **introduced/left open in Phase 3 ownership work (Cursor)** |
| MUST-FIX | `flightplan/resume` and `flightplan/status` in `NativeCommands.Ported` but **no Command cases** → “Not yet ported”. ChatOrchestrator/IsPorted lie. | `NativeCommands.cs`; `NativeFlightController.cs` — **Cursor Phase 3** |
| MUST-FIX | AI-off Awake: `NativeReady = !Healthy(1500)` — if bridge still answering, **all local Execute blocked** until settings toggle. Toggle path sets `NativeReady=!enabled` inconsistently. | `BridgeLauncher.cs` — prior Phase 3 foundation |
| HIGH | `native_control.json` path: mod uses `BridgeLauncher.DataDirectory` (can be `bridge_dir`); frozen bridge uses PluginData — path mismatch ⇒ busy flag invisible. | `BridgeLauncher.cs` / `config.py` — **Cursor Phase 3** |
| HIGH | Powered descent: `Acceleration` catch passes accel=0 → policy ramps toward **full throttle** for several faults before abort — unsafe on low clearance. | `NativeVerticalLanding.Fly` + `VerticalLandingPolicy` — **Cursor** |
| HIGH | `Classify` thresholds ≠ Python `heli.classify` (horiz band 0.4–0.8, left/right 0.25 vs GROUP_X 0.5). Soft-lift/edge layouts can disagree. | `HelicopterPolicy.cs` vs `heli.py` — **Cursor** |
| HIGH | ControlLease is C#-local only; never synced to Python; `VesselChanged` unused in production. | `FlightPolicy.cs` |
| HIGH | Powered descent requires already-ignited engines; no auto-ignite; no chutes/orbital — OK if documented, but not parity with bridge `land_here`. | `NativeVerticalLanding` / Command |
| NIT | TouchdownGate uses realtime in heli vs UT in vertical landing (warp inconsistency). Silent catch on WriteStatus disk failure. Manual override ignores throttle. | various |
| OVERCLAIM | “Phase 3 integration policies: 12 checks” are pure-policy/lease/plan units — not `NativeFlightController` / adapter / JSON I/O. | `Program.cs` — **Cursor** |
| OVERCLAIM | Outcome text still says full holds/takeoff/landing/… with no bridge — true only for ported subset; orbital/docking/science remain bridge. | CURSOR_TODO Phase 3 Outcome |

**What looks solid:** VerticalLandingPolicy feasibility/ramp/touchdown unit tests; plan rejects orbital/fly-to/TG; taxi route validation attempt; AI-off menu powered descent entry; lease exclusivity in unit tests; 474 pytest including one busy-file steer test.

**Abnormality:** Same C# `Main` prints Phase 3 then Phase 4 checks — easy to mistake “green suite” for Phase 3 completion.

---

### Phase 4 — In-mod AI foundations

| Sev | Finding | Evidence |
|-----|---------|----------|
| MUST-FIX | `AiRuntimePolicy.Apply`: when `freeGpuBytes==0` (unknown), Hybrid still sets `GpuLayers=20` with **no warning** — silent offload claim. | `AiRuntimePolicy.cs` — **Cursor** |
| HIGH | Phase 4 types compile into DLL but are **unreferenced** by ChatWindow/settings (dead until wired). | no call sites |
| HIGH | `NativeAiLoader` has **zero tests**; not in packaging path; “LoadLibrary-by-.bin proven” is overclaim. | `NativeAiLoader.cs` / csproj |
| HIGH | `ModelManager` is finalize/checksum only — **no download**, no pinned production SHA in repo. | `ModelManager.cs` |
| HIGH | `package_release.ps1` has stray-DLL check but does **not** call `ForbiddenDlls` / `ModelBundled` (tested only in C# harness). | `package_release.ps1` — **Cursor overclaim in HANDOFF** |
| OVERCLAIM | HANDOFF “Finished Phase 4 offline foundations” ≠ product-ready AI; 4A live KSP gate not run. | HANDOFF |
| NIT | ChatOrchestrator MaxToolLoops is one-batch cap, not multi-turn agent loop; cancel set never pruned. | `ChatOrchestrator.cs` |

**What looks solid:** ForbiddenDlls/model-bundle unit tests; AI-off blocks finalize; checksum failure path; user-before-crew dequeue; no silent local→cloud fallback helper.

---

### Phase 5 — Parity audit + bridge removal

| Sev | Finding | Evidence |
|-----|---------|----------|
| MUST-FIX | 5A lists `flightplan/resume` as native (even “thin”) but handler missing — audit inaccurate. | CURSOR_TODO 5A + NativeCommands |
| HIGH | Audit is category prose, not per-tool replacement/test/live matrix required by Phase 5 plan 5A. | CURSOR_TODO |
| HIGH | MCP still needs bridge stdio — correctly blocked, but blocks any “all AICS in mod” claim. | plan 5C |
| OK | Bridge **not** removed; zip still ships `AICSBridge.exe`; 92 BY_NAME count verified. | package + Python |
| NIT | Ported includes 6 HTTP-style routes not in BY_NAME; methodology blur in “92 tools” header. | NativeCommands vs BY_NAME |
| NIT | HANDOFF package still named phase3 after Phase 4/5 commits — naming drift. | dist zip name |

---

### Cross-cutting abnormalities

1. **Test theater:** Placement Label unit tests pass while live `RotorTelemetry` wiring is wrong — false confidence for Phase 2 Rotors tab and speech labels.
2. **Dual control pipelines:** Native uses `flightID`; bridge uses kRPC object id + optional RpcId — two worlds, easy desync.
3. **Documentation ahead of code:** Phase Outcome/HANDOFF language often reads like finished product; status lines correctly say “needs live check” but “COMPLETE/ADVANCED/Finished” nearby invites over-trust.
4. **Own-work debt (Cursor session):** ownership bypass via `set_speed`/`set_altitude`/`set_throttle`; Ported resume/status; Classify threshold drift; vertical landing max-throttle-on-sensor-fault; overclaimed packaging detectors and exclusive ownership.

---

### Recommended fix order before live check

1. Fix `RotorTelemetry` → `RotorPlacement.Label` argument order + liftCount axis (Phase 2).
2. Match telemetry by `part_id`/`vessel_id` when rpc ids empty; allow API fallback when row missing (Phase 1).
3. Gate **all** steer/throttle entry points (or whole `call_tool` when busy); align `native_control.json` path (Phase 3).
4. Remove or implement `flightplan/resume`/`status`; fix `NativeReady` on AI-off Awake (Phase 3/5).
5. Soften powered-descent thrust-fault response (hold throttle / abort without ramp-to-1) (Phase 3).
6. Wire `ForbiddenDlls`/`ModelBundled` into `package_release.ps1`; fix Hybrid unknown-VRAM silence (Phase 4).
7. Then live check with clear “known remaining holes” list.

### Review verdict (pre-fix)
**Do not treat Phases 1–5 as live-ready.** Automated suites are green for what they cover, but several MUST-FIX items can reproduce the original “0 RPM / wrong labels / dual autopilot” class of failures in flight. Bridge fallback should stay until ownership + telemetry ID + rotor labels are fixed and re-tested.

---

### FIXES APPLIED (Oct 9, post-review) — before live check

Parallel agents + follow-up. Tests after fixes: **478 pytest passed**; C# suite green (incl. Phase 4 path/VRAM checks); Release build OK. Nothing pushed.

| Item | Resolution |
|------|------------|
| Telemetry ID match | `telemetry.rotor`/`state` match `vessel_id`/`part_id` when RPC ids empty; wrong-vessel/part → API fallback; vessel switch calls `telemetry.reset()` |
| `_live_rotor` / spool | API fallback when unmatched; unknown RPM stays None (not `0`); 8s telemetry-loss abort; spool sample gate only when telemetry `sample` present (API path must not send `sample: null`) |
| Rotor Label “bug” | **False positive** (KSP `frame.up` = vessel forward). Documented + `LabelFromVesselFrame` / `IsLiftRotorAxis` + tests |
| MAYDAY lights / PollSystems | Replaced dead poll with `PollBridgeMaydayLights` (AI on only); AI-off defaults lights on |
| Status AP labeling | Autopilot section header + `AP:` prefix on bridge/controller rows |
| AICS menu AI-off | `DepsOk` allows local panels when `!AiEnabled` without `bridgeOk` |
| Ownership bypass | `call_tool` blocks all non–read-only tools when native busy (incl. `set_speed`/`set_throttle`) |
| `native_control.json` path | Mod always writes GameData PluginData path; Python scans `ROOT` + `KSPCHAT_PLUGIN_DATA` |
| NativeReady Awake | AI-off sets `NativeReady=true` immediately (no `/health` gate) |
| flightplan resume/status | Implemented in `NativeFlightController.Command` |
| Powered descent thrust fault | Hold throttle; abort to 0 after 4 faults (no ramp-to-1) |
| Classify thresholds | `SideUp=0.4`, `GroupX=0.5` aligned with `heli.py` |
| Hung bridge | 3 failed health checks → kill owned process + backoff restart; timeout start also terminates |
| Hybrid unknown VRAM | Falls back to CPU with explicit status string |
| Packaging | `package_release.ps1` rejects `.gguf` / bundled model paths; ForbiddenDlls-style check retained |
| NativeAiLoader tests | `TryValidatePath` covered in C# suite |
| Test isolation | `conftest` autouse calls `telemetry.reset()` |

**Still needs Luke live check** (not automated): Systems RPM vs PAW, kill bridge recovery, AI-off flight, powered descent, ownership file with real GameData install. Bridge fallback retained. Phase 4 HTTP in-mod chat is wired for live test (`native_chat` + key/LM Studio); embedded llama load/generate experiment still outstanding.

### Phase 4 HTTP stubs finish (Oct 9 follow-up)
Wired: `SecretsStore`, `OpenAiBackend`, `InModChatSession`, `InModAiHost`, `AiSettings`, ChatWindow `DispatchChat`/`UseInModChat`, Settings native_chat + keys, AI-off session unload. C# suite includes In-mod AI stack checks. `.gitignore` no longer blocks `SecretsStore.cs` via `*secret*`. Embedded 4A still gated.

---

## Phase 5 finish plan — eight mini-phases (stubs→DONE, then bridge removal)

Status: PLANNED (Oct 9). Execute in order. Commit as Luke Benko; no push unless asked; never touch `old-local-main`. Mark **DONE (tests; needs live check)** only with tests; mark **DONE (live)** only after Luke validates in P5-7. Do not infer DONE from silence or pytest alone.

### Locked decisions
- Order: foundation/stubs → flight → science/lifecycle → orbital → llama downloads → bridge-free package → live DONE → delete bridge.
- Bridge-free = no `AICSBridge.exe` in shipping zip; no `BridgeLauncher` autostart/watchdog after P5-8.
- Model + runtime via **download buttons** (not zip). Never bundle `.gguf` or loose native `.dll` under GameData.
- ChatGPT Desktop stdio-MCP discontinued for bridge-free release; document migration.
- `kspchat/` Python source stays in repo as history/rollback; leaves shipping package only.
- MechJeb optional for orbital/docking; clear “needs MechJeb” when absent.

### P5-1 — Foundation: stubs and not-dones → code DONE
Do first so later ports are not dual-wired to a dying bridge path.

AI / UI stubs:
- [ ] Default `native_chat=true` for new installs; chat/tools must not require `:8765` once providers resolve
- [ ] Wire Claude and Grokbot (OpenAI-compatible / official HTTP) or remove from shipping UI — no stub replies
- [ ] Port crew / personality / memory essentials (`crew.py` / `personality.py` / `memory.py` / `talk.py`): timeouts, canned lines when busy, PluginData persistence
- [ ] Make `ChatOrchestrator` the real owner (or delete dead type): user-before-crew, cancel, deadlines, tool-loop limits
- [ ] Clear `AicsMenu` Queued() stubs: implement or remove; no “stub for now” in shipping UI

Architecture not-dones:
- [ ] Per-tool replacement / test / live matrix for all BY_NAME tools (replace 5A prose); fill as P5-2…4 land
- [x] **DONE (tests; needs live check)** Native emergency / sabotage / parking / power when bridge absent (AI-off must not need kRPC watcher)
- [x] **DONE (tests; needs live check)** Dashboard honesty: no stale bridge AP/phase rows looking live; Systems/Rotors/Trim fully local
- [ ] Dual ID cleanup: one vessel/part identity story for native tools
- [ ] `capture_plan`, `list_*`, `trim_panel_open`, `set_flight_plan` — port or drop with explicit note
- [ ] Packaging detectors always run in `package_release.ps1`; pin model SHA when available

### P5-2 — Flight residual ports
- [ ] Aliases: `land` / `land_at` / `land_at_ksc`
- [ ] `fly_to` / `fly_to_place`, `touch_and_go` / `go_around` / `circle_here`
- [ ] `prop_control` / `afterburner` / `engine_mode`, flaps completion
- [ ] `set_throttle` / `set_engines` / `cut_engines` / `set_altitude` / `level_off` / `set_sas_mode`, abort AGs
- [ ] Telemetry reports: fuel / delta-v / landing ETA / how_far / landing_check / crew_report / flight_report / damage_report
- [ ] C# tests per family; update matrix; bridge fallback until P5-8

### P5-3 — Vessel lifecycle + science
- [ ] `stage`, `recover_vessel`, `launch_*`, `deploy_parachutes`, `eject_kerbal`
- [ ] `run_science` / `reset_experiments` / `set_science_watcher` (EC thresholds + safeguards from `kspchat/science.py`)
- [ ] Rewire menu buttons off bridge HTTP onto native / InModAiHost; update matrix

### P5-4 — Orbital / docking / MechJeb-optional
- [ ] `mechjeb_ascent`, `circularize`, `transfer_to`, `deorbit_burn`, `warp_*`
- [ ] `dock_with`, `station_keep`, apo/peri/inclination helpers, `sun_lock` / `antenna_lock`, plane-match helpers
- [ ] Clear “needs MechJeb” when absent; no silent kRPC for AI-off core flight; update matrix

### P5-5 — Embedded llama.cpp (download buttons)
After P5-1 chat ownership and P5-2…4 native tools:
- [ ] **Download model** → `PluginData/models/` (progress, cancel, refuse when AI off; pin URL/version/license/SHA)
- [ ] **Download runtime** → `PluginData/native/*.bin` (same UI pattern; never GameData-scanned `.dll`)
- [ ] Wire `embedded` load / generate / cancel / unload; AI-off unloads (`InModAiHost.UnloadForAiOff`)
- [ ] LM Studio / Ollama / cloud remain without downloads
- [ ] `package_release.ps1` still rejects bundled `.gguf` / forbidden DLLs

### P5-6 — Bridge-free release candidate
- [ ] Shipping zip: DLL + templates + docs; no exe, no weights, no native `.dll`s
- [ ] Separate rollback archive/tag with last bridge-with-fallback build
- [ ] Versioned PluginData migration (settings, keys, spots, craft notes, memory); never wipe user data
- [ ] Smoke with exe absent: AI-off, downloads, embedded + cloud chat, native tools from P5-1…5

### P5-7 — Live closure → “needs live check” becomes DONE
Luke runs and records results here. Only then flip lines to **DONE (live)**.

- [ ] Phase 1–3 debt: Systems RPM vs PAW; panels with exe absent; AI-off flight/trim/heli/descent/plans; ownership; blockers 1–9 if not already signed
- [ ] P5-1…5: stubs gone; science/orbital claims; download model+runtime; embedded + cloud chat; AI-off unload; Hybrid/CPU note if measurable
- [ ] Scene switch / revert / save-reload; API failure behavior
- [ ] Failures stay listed; do not infer DONE

### P5-8 — Remove the bridge
Only after P5-7 pass:
- [ ] Delete bridge launch / watchdog / shutdown and obsolete HTTP telemetry polling from shipping plugin
- [ ] Stop packaging `AICSBridge.exe`; update README / wiki / CKAN (download buttons; Desktop MCP discontinued; MJ-optional)
- [ ] Repo keeps `kspchat/` source
- [ ] Final report: commits, zip/DLL paths, intentional limitations

Flow: P5-1 Foundation → P5-2 Flight → P5-3 Science → P5-4 Orbital → P5-5 Llama downloads → P5-6 Bridge-free RC → P5-7 Live DONE → P5-8 Remove bridge.

