# CLINE LOG — P5 execution (Cline)

Working log for the P5-1…P5-8 finish plan (CURSOR_TODO.md). Updated after **each finished part**, so work survives an interruption. Rule from the plan: an item is **DONE (tests; needs live check)** only when tests cover it; **DONE (live)** only after Luke validates (P5-7). Commits as Luke Benko; nothing pushed.

Baseline before P5-1: 478 pytest passed, C# suite green, `KSPChatBridge-0.1.1-prelive.zip` in dist.

---

## P5-1 Part 1 — `native_chat` defaults ON for new installs — DONE (tests; needs live check)

**Problem:** `BridgeLauncher.cs` shipped `native_chat = false`, so new installs depended on the Python bridge at `:8765` even when a cloud/local provider was configured.

**Changed:**
- `KSPChatMod/BridgeConfigDefaults.cs` (NEW): pure, Unity-free `BridgeConfigDefaults.Create()` holding the `bridge.cfg` defaults — now `native_chat = "true"`. Testable offline in the C# suite (BridgeLauncher itself is Unity-bound and not compiled into tests).
- `KSPChatMod/BridgeLauncher.cs`: `cfg` dictionary initializer replaced with `BridgeConfigDefaults.Create()`. Behavior note: an **existing** `bridge.cfg` always wins (saved files are never rewritten), so this changes defaults for *new installs only* — Luke's current install keeps whatever is on disk.
- `tests/csharp/Aics.Tests.csproj`: compiles `BridgeConfigDefaults.cs` into the suite.
- `tests/csharp/Program.cs`: new block "P5-1 config defaults: 3 behavior checks passed" — asserts `native_chat=ai_enabled=autostart=true`, bridge fallback keys (`bridge_dir`, `python`) retained.

**Tests run:** C# suite green incl. new block (3 checks). Mod Release build: 0 errors.

**Still needs live check:** fresh PluginData (no bridge.cfg) → chat works with no bridge process when a provider resolves; opt-out via `native_chat = false` still falls back to bridge.

**Not yet done in this part (other P5-1 items):** Claude/Grok wiring, crew/personality/memory port, ChatOrchestrator ownership, AicsMenu stubs, tool matrix, native emergency/parking/sabotage, dashboard honesty, dual-ID, capture_plan/list_*/trim_panel_open/set_flight_plan, packaging detectors/SHA pin.

---

## P5-1 Part 2 — Claude + Grokbot wired as real providers (no stub replies) — DONE (tests; needs live check)

**Verified contracts before coding** (plan requires this):
- **Claude:** Anthropic's official OpenAI-SDK compatibility layer (`platform.claude.com/docs/en/api/openai-sdk`) — `POST https://api.anthropic.com/v1/chat/completions`, `Authorization: Bearer <ANTHROPIC_API_KEY>`, `choices[].message.tool_calls` fully supported, model ids `claude-*` (default `claude-sonnet-5-5`, override `CLAUDE_MODEL`).
- **Grok:** xAI REST `docs.x.ai` — `POST https://api.x.ai/v1/chat/completions`, Bearer `XAI_API_KEY` (alt `GROK_API_KEY`), OpenAI-compatible schema, model ids `grok-*` (default `grok-4.7`, override `XAI_MODEL`).

**Python (`kspchat/`):**
- `config.py`: `CLAUDE_URL`/`CLAUDE_MODEL`, `XAI_URL`/`XAI_MODEL` (env `CLAUDE_BASE_URL`, `XAI_BASE_URL`).
- `backends.py`: `claude` + `grokbot` moved from `STUBS` into `KEYED` (normal key-missing path, model listing filters `claude*`/`grok*`), added to `CLOUD` (429 Retry-After backoff applies) and `KEY_HELP`. `STUBS` dict deleted; `resolve()`/`list_models()` no longer reference it. Old aliases (`claude (soon)`, `grok`, `grok bot`) kept so saved `/ai` commands still resolve.
- `chat.py` docstring updated.

**C# (`KSPChatMod/`):**
- `OpenAiBackend.cs`: real `Resolve()` branches for claude/grokbot (URL/key/model + missing-key errors).
- `SecretsStore.cs`: `BackendKeyVar` += claude→`ANTHROPIC_API_KEY`, grokbot→`XAI_API_KEY`; `AltKeys` += `GROK_API_KEY`.
- `ChatWindow.cs`: labels now "Claude" / "Grok Bot"; `IsStub()` **deleted** (with its 3 call sites) — no stub concept remains in the shipping UI.
- `AicsMenu.cs`: dropdown always enabled; "not wired yet" warning removed; `NeedsKey` includes claude/grokbot (key box shows in Settings).

**Tests:** `tests/test_backends.py` (missing-key errors now name the real key vars; alias + alt-key `GROK_API_KEY` resolution; `configured()` only reports backends with keys) — run standalone + in pytest. C# suite "P5-1 wired providers: 4 behavior checks passed" (resolve url/model for both + missing-key errors). Results: 478 pytest passed, C# suite green, Release build 0 errors.

**Still needs live check:** Claude/Grok chat round trip in KSP with a real key (tool_calls path incl. `strict` ignored by Claude compat layer — noted limitation), key boxes save/mask, `/ai claude` + `/ai grok` switching.

---

## P5-1 Part 3 — crew / personality / memory / talk essentials ported to C# — DONE (tests; needs live check)

Ports of the bridge's crew-side behavior so in-mod chat has it without `:8765`. Unity-free + injectable paths = fully covered by the C# suite.

**New files (`KSPChatMod/`):**
- `KerbalPersonality.cs`:
  - `PlaystyleNotes` (memory.py): load/remember with near-dupe refresh (Levenshtein-based similarity > 0.85), 100-note cap with "dropped N oldest", `NotesBlock()` for system prompts — persists `playstyle_notes.md` in PluginData.
  - `KerbalPersonality` (personality.py): deterministic per-name temperament (2 max) + 2 likes + 1 dislike, `Ensure` persists once to `kerbal_personalities.json` (shared file with bridge), `Describe` → "a nervous scientist who loves … and hates …". NOTE: C# RNG is seeded xorshift over the same crc32(name) as Python's MT — deterministic per language, not bit-identical across languages; the shared JSON keeps one source of truth after first generation.
- `IntercomTalk.cs` (talk.py + crew.py fmt/norm_trait/who essentials): `Route` (addressing forms @Bob / Hey Bob / Bob: / ", Bill?" tail; pilot-is-the-voice skip; absent kerbals; orders → pilot), `Clean` (think-block strip, toolish/action-claim rejection, word/char caps), `Reply` with **8 s deadline → canned line on timeout** (busy AI never blocks), per-kerbal 4-exchange memory, `Fmt` ([INTERCOM] Bob (Sci): …, engineers on [COMMS]), `Prompt` (personality + facts + memory).

**Wiring:** `InModChatSession` system prompt now appends `PlaystyleNotes.NotesBlock()` (parity with bridge chat).

**Tests:** C# suite "P5-1 crew/personality/memory/talk: 22 behavior checks passed" — mirrors `tests/test_talk.py` fixtures (Sidry pilot, Bob/Bill crew, Jebediah/Valentina roster) for route parity; timeout→canned verified with a slow generator; persistence round-trips through temp PluginData.

**Gotcha found while testing:** Python's think-block strip uses `` HTML-ish tags (not markdown ``` fences) — byte-verified against `talk.py:139` after a test failure; C# regex `(?s)<think.*?</think>` now matches exactly.

**Results:** 478 pytest passed, C# suite green, Release build 0 errors.

**Still needs live check:** intercom reply from KSP chat ("Hey Bob, …") with a real model; personality file shared correctly between mod and running bridge (same PluginData dir).

---

## P5-1 Part 4 — `ChatOrchestrator` is the real queue owner — DONE (tests; needs live check)

**Changed:**
- `ChatOrchestrator.cs`:
  - Cancel set is now **pruned** on dequeue/run (fixes "cancel set never pruned" NIT from the Phase 4 review).
  - New `CancelAll()` (chat window Clear) and exactly-once `ChatRequest.Settled` callback — dropped requests (cancelled/expired) settle so the UI "thinking" counter never leaks.
  - `ToolLoopGuard`: bounded per-request tool-execution budget; `MaxToolLoops` 6→8 (parity with `config.MAX_TOOL_ROUNDS`). `RunTools` uses the guard.
- `InModAiHost.cs`: **rewired to pump through the orchestrator** — `EnqueueChat` (user, 60 s deadline) vs `EnqueueCrew` (crew, 20 s deadline, best-effort); one model call at a time; each completion pumps the next request (user-before-crew). Replaces the old "busy — wait for the current reply" refusal. `CancelQueued()` exposed.
- `InModChatSession.cs`: per-user-request tool budget via `ToolLoopGuard` (was unbounded per round); system prompt now injects `PlaystyleNotes.NotesBlock()` (moved from Part 3 wiring note — actually wired here).
- `ChatWindow.cs`: Clear button also calls `InModAiHost.CancelQueued()`.

**Tests:** "P5-1 orchestrator ownership: 10 behavior checks passed" — user-before-crew, cancel+settle-once, cancel-set pruning (requeued id runs), expired-deadline drop+settle, CancelAll settles all, tool budget bounded.

**Results:** 478 pytest passed, C# suite green, Release build 0 errors.

**Still needs live check:** burst chat while a reply is generating (queued second message answers after, not refused); Clear cancels queued crew chatter.

---

## P5-1 Part 5 — AicsMenu Queued()/Stub() stubs removed — DONE (tests; needs live check)

- Deleted the dead `Stub()` helper + `Queued()` "is a stub for now" notice from `AicsMenu.cs` (verified zero call sites first).
- Reworded "Best-face (side panels) is queued." → "not available yet."; removed the stale "W = working, P = partial, S = stub" legend (no tabs use the markers anymore).
- Guarded by `test_tool_matrix.py::test_no_stub_strings_in_shipping_ui` — no "stub for now"/"is a stub"/"not wired yet" string may ship in any `KSPChatMod/*.cs` again.

## P5-1 Part 6 — per-tool matrix for all 92 BY_NAME tools (replaces 5A prose) — DONE (tests; needs live check)

**New:** `tests/test_tool_matrix.py` — the matrix IS the test:
- `MATRIX`: all 92 `ksp_actions.BY_NAME` tools → `native` / `p5-2` / `p5-3` / `p5-4` / `drop` (drop reasons inline: `capture_plan` → P5-4 orbital helpers, `list_craft` → menu feature, `trim_panel_open`/`set_flight_plan` → in-mod UIs fill those directly, `captain_order`/`set_override`/`authorise_all` → speech parser / settings).
- 4 checks: full BY_NAME coverage + plausible statuses; `native` rows must be in `NativeCommands.Ported`; `Ported` chat-tools must be reviewed in the matrix (HTTP routes like `taxi/list`/`flightplan/*` exempt); no stub strings in shipping UI.

**Thin ports landed with it (P5-1 checklist: capture_plan, list_*, trim_panel_open, set_flight_plan — port or drop with explicit note):**
- PORTED `list_taxi_points` (alias of `taxi/list`), `list_landing_spots` (`NativeSpots.List()` name/body/mode rows), `set_ai_name` (settings `ai_name`, 24-char clamp parity), `remember_preference` (`PlaystyleNotes.Remember`) — added to `NativeCommands.Ported` + `NativeFlightController.Command`.
- DROPPED with explicit notes in the matrix: `capture_plan`, `list_craft`, `trim_panel_open`, `set_flight_plan`, `captain_order`, `set_override`, `authorise_all`.

**Tests:** `tests/test_tool_matrix.py` 4 passed; C# "P5-1 read-only ports: 2 behavior checks passed" (spots list rows + empty list). Full results: **482 pytest passed** (478 prior + 4 matrix), C# suite green, Release build 0 errors.

**Still needs live check:** `/ai` chat tool calls hitting the new list_landing_spots/set_ai_name/remember_preference paths with native_control busy flag active.

---

## P5-1 Part 7 — Native emergency/sabotage/parking/power when bridge absent — PARTIAL (NOT committed, NOT tested)

**Done so far (uncommitted working-tree changes):**
- `KSPChatMod/NativeSafety.cs` (NEW, pure/testable): `ShouldRun(aiEnabled, bridgeResponding)` — safety runs when AI off OR bridge absent; `IsFresh` (10 s health freshness); `ShouldRevert(safetyNet, nativeActive, flying)`; `ParkingAction(...)` parity with `parking.py` (rearm after landing, set-once on ground, player brake-off releases, taxi/takeoff exempt).
- `BridgeLauncher.cs`: `BridgeHealthy()` now stamps `lastBridgeOkUtc` on success; new `BridgeResponding` property (fresh within 10 s).
- `NativeFlightController.cs`: `Update()` gate reworked — computes `nativeMode` (AI off) vs `standInForBridge` (AI on + `!BridgeResponding`); added `wasAirborne`/`prevBrakes` fields.

**NOT done (the actual wiring):**
- Safety tick body (power.Tick / engines.Tick / recovery.Tick / reversers.Recover / parking block) still gated for nativeMode flow only — must run in `standInForBridge` mode too, while control loops (plan/spool/hold/trim) stay nativeMode-only. Parking block not yet rewritten onto `NativeSafety.ParkingAction` (still old inline logic; `wasAirborne`/`prevBrakes` unused so far).
- `recovery.Tick` enabled arg should use `NativeSafety.ShouldRevert`.
- NO tests written for `NativeSafety` (need `tests/csharp/Program.cs` block + `Aics.Tests.csproj` include of `NativeSafety.cs`).
- Build/tests not run since these edits — tree may not even compile cleanly until the wiring lands.

---
# P5 REMAINING WORK (what's left overall)

## P5-1 leftovers
- [x] Part 1 native_chat default — DONE (75ffa5c)
- [x] Part 2 Claude/Grok wired — DONE (3d75620)
- [x] Part 3 crew/personality/memory/talk port — DONE (7147911)
- [x] Part 4 ChatOrchestrator ownership — DONE (42ec704)
- [x] Part 5 UI stubs removed — DONE (47b36fb)
- [x] Part 6 tool matrix + thin ports — DONE (47b36fb)
- [ ] **Part 7 native safety when bridge absent — FINISH WIRING + TESTS (see above)**
- [ ] **Part 8 Dashboard honesty** — StatusWindow: label bridge-sourced rows (heading/phase/AP) as stale/unavailable after disconnect, never "live"; Systems/Rotors/Trim fully local; wire or delete dead `PollSystems()`.
- [ ] **Part 9 Dual-ID cleanup** — one vessel/part identity story for native tools (flightID vs kRPC object id); `telemetry.reset()` on vessel switch; document single source of truth.
- [ ] **Part 11 Packaging detectors + model SHA pin** — `package_release.ps1` always runs ForbiddenDlls/ModelBundled-style checks (verify `-SkipExe` path too); pin `ModelManager` SHA when available (currently size-check only).

## P5-2 — Flight residual ports (all `p5-2` rows in tests/test_tool_matrix.py)
land/land_at/land_at_ksc, fly_to/fly_to_place, touch_and_go/go_around/circle_here, prop_control/afterburner/engine_mode, flaps completion, set_throttle/set_engines/cut_engines/set_altitude/level_off/set_sas_mode, abort_ag/action_group, turn/plane_pitch/course_correction, reports (fuel_check, get_delta_v, get_landing_eta, how_far, landing_check, crew_report, flight_report, damage_report). C# tests per family; MATRIX rows → `native`; bridge fallback until P5-8.

## P5-3 — Vessel lifecycle + science
stage, recover_vessel, launch_craft, deploy_parachutes, eject_kerbal, run_science/reset_experiments/set_science_watcher (EC thresholds + safeguards from `kspchat/science.py`); rewire menu buttons off bridge HTTP onto native/InModAiHost; update MATRIX.

## P5-4 — Orbital / docking / MechJeb-optional
mechjeb_ascent, circularize, transfer_to, deorbit_burn, warp_*, dock_with, station_keep, change_apoapsis/periapsis/inclination, apsis_longitude, sun_lock/antenna_lock, sync_orbit_altitude, match_target_plane/launch_to_target_plane/time_to_target. Clear "needs MechJeb" when absent; update MATRIX.

## P5-5 — Embedded llama.cpp download buttons
Download model → `PluginData/models/` (progress/cancel, refuse when AI off, pin URL/version/license/SHA); Download runtime → `PluginData/native/*.bin` (never GameData-scanned `.dll`); wire embedded load/generate/cancel/unload (`InModAiHost.UnloadForAiOff`); LM Studio/Ollama/cloud unchanged; packaging still rejects `.gguf`/forbidden DLLs.

## P5-6 — Bridge-free release candidate
Ship zip: DLL + templates + docs (no exe, no weights, no native `.dll`s); rollback archive/tag with last bridge build; versioned PluginData migration (settings/keys/spots/craft notes/memory — never wipe user data); smoke with exe absent (AI-off, downloads, embedded+cloud chat, native tools from P5-1…5).

## P5-7 — Live closure (LUKE — not automatable)
Run + record results; only then flip "needs live check" → **DONE (live)**. Phase 1–3 debt (Systems RPM vs PAW, panels with exe absent, AI-off flight/trim/heli/descent/plans, ownership), P5-1…5 items, scene switch/revert/save-reload, API failure behavior. Failures stay listed.

## P5-8 — Remove the bridge (ONLY after P5-7 passes)
Delete bridge launch/watchdog/shutdown + obsolete HTTP polling from shipping plugin; stop packaging `AICSBridge.exe`; update README/wiki/CKAN (download buttons; Desktop MCP discontinued; MJ-optional); repo keeps `kspchat/` source; final report (commits, zip/DLL paths, intentional limitations).

---

## Commit/state summary at stop
- Committed: `75ffa5c` (P5-1.1), `3d75620` (P5-1.2), `7147911` (P5-1.3), `42ec704` (P5-1.4), `47b36fb` (P5-1.5-6). Nothing pushed. `old-local-main` untouched. `CURSOR_TODO.md` still shows your pre-existing edit (unstaged).
- Uncommitted working tree: Part 7 partial (`NativeSafety.cs` new; `BridgeLauncher.cs` + `NativeFlightController.cs` gate edits) — needs wiring + tests + green build before commit.
- Baseline at last full green run (after Part 6): **482 pytest passed**, C# suite green (P5-1 blocks: 3+4+22+10+2 checks), Release build 0 errors.
## P5-1 Part 7 - native safety tick runs in-mod - DONE (tests; needs live check)
**Problem:** power recovery / sabotage revert / parking / engine restart only ran in AI-off native mode; Cline's partial gate ran the whole flight loop when the bridge was absent.
**Changed:** `NativeFlightController.Update` split into `SafetyTick` (power, engine restart, sabotage revert via `NativeSafety.ShouldRevert`, parking via `NativeSafety.ParkingAction` incl. re-arm after landing) which runs whenever `NativeSafety.ShouldRun`; local flight tick (plans/hold/spool/trim) still AI-off only. No bridge grace/dual-control logic (per Luke: bridge is being folded in).
**Tests run:** C# "P5-1 native safety tick: 9 behavior checks passed"; pytest 482 passed; Release build 0 errors.
**Still needs live check:** AI on with no bridge: parking set on runway, brake-off releases, re-arm after landing, sabotage revert in flight, power/engine recovery.

## P5-1 Part 8 - dashboard honesty - DONE (tests; needs live check)
**Problem:** Status AP/phase rows depended on bridge /status ("unavailable" when down); trim panel still posted to bridge when AI on.
**Changed:** new `DashboardRows` (pure): bridge rows only when AI on and the answer is <5 s old, labeled `source=bridge`; otherwise local in-mod controller rows (`source=local`). Failed poll clears bridge rows. `TrimWindow` uses native get/set_trim whenever AI off or `native_chat` on. Systems/Rotors were already local (LocalVesselState/RotorTelemetry).
**Tests run:** C# "P5-1 dashboard honesty: 4 behavior checks passed"; pytest 482; Release build 0 errors.
**Still needs live check:** Status window with bridge absent shows local rows; trim panel works with AI on.

## P5-1 Part 9 - dual ID cleanup - DONE (tests; needs live check)
**Problem:** native code formatted vessel GUIDs / part flightIDs ad hoc next to kRPC remote ids.
**Changed:** `NativeIds` (Vessel = GUID D form, Part = invariant flightID, tolerant compare); used by NativeFlightController (lease/status), NativePropulsion, RotorTelemetry. kRPC RpcId kept only as a bridge-side extra in the telemetry payload.
**Tests run:** C# "P5-1 native ids: 3 behavior checks passed"; pytest 482; Release 0 errors.
**Still needs live check:** lease/status and rotor rows still match the active vessel.

## P5-1 Part 11 - packaging detectors + model SHA pin - DONE (tests; needs live check)
**Changed:** `package_release.ps1` re-scans the finished zip (non-approved .dll, .gguf, .pyd/.so/.dylib, PluginData/models, PluginData/native, .env) and deletes it on a hit; stage detectors stay unconditional. `ModelManager` default URL pinned to HF revision f302c64a, SHA-256 9c9f56a3...3f94, 1,929,903,264 bytes; license noted (Qwen Research License). Instance now verifies the SHA. (Part 10 capture_plan/list_*/trim_panel_open/set_flight_plan was decided by Cline in the matrix; box ticked.)
**Tests run:** new tests/test_packaging.py (2); pytest 484; C# suite green; Release 0 errors; `package_release.ps1 -SkipExe` produced dist/KSPChatBridge-0.1.1.zip cleanly.
**Still needs live check:** real model download verifies against the pinned SHA.

## P5-2 - flight residual ports - DONE (tests; needs live check)
**Changed:** `NativeFlightController` is now partial; new `NativeFlightResidual.cs` ports 28 tools: land (routes heli/plane/spot/vertical), fly_to/fly_to_place (bearing re-steer each 1 s, circle on arrival <3 km), touch_and_go (re-takeoff at rollout), go_around, circle_here, turn, plane_pitch, set_altitude, level_off, set_throttle (refused while local AP owns throttle), set_engines, cut_engines/abort_ag (confirmed=true required), afterburner/engine_mode (MultiModeEngine), flaps 0-3, prop_control (rpm/torque/motor/pitch; reverse/group refused), set_sas_mode, action_group 1-10, fuel_check, get_delta_v (stock VesselDeltaV), get_landing_eta, how_far, landing_check, crew/flight/damage reports. Pure decisions in `FlightResidualPolicy`. `land_at`/`land_at_ksc`/`course_correction` moved to P5-4 (MechJeb landing) and stay on bridge fallback; unported tools still fall back to the bridge.
**Tests run:** C# "P5-2 flight residuals: 16 behavior checks passed"; test_tool_matrix updated (native rows verified against NativeCommands.Ported); pytest 484; Release 0 errors.
**Still needs live check:** every tool above in flight. NOTE (pre-existing): native flight modes (hold/landing/fly_to) only actuate in AI-off mode - `Fly()`/flight tick gate on `!AiEnabled`; with AI on + native_chat the commands set state but the bridge/kRPC autopilot is still the actuator.

## P5-3 - vessel lifecycle + science - DONE (tests; needs live check)
**Changed:** new `NativeLifecycle.cs` (partial controller): stage (StageManager.ActivateNextStage), recover_vessel (IsRecoverable + OnVesselRecoveryRequested), launch_craft (saves/<save>/Ships/VAB|SPH, default crew, LaunchPad/Runway, FlightDriver.StartWithNewLaunch), deploy_parachutes (python gate: not landed / VS<=5 unless force), eject_kerbal (confirmed=true; reuses ChatWindow.Eject), run_science, reset_experiments (scientist needed for inoperable), set_science_watcher (native settings `science_mode`). Science watcher runs from SafetyTick every 3 s: situation/biome/body key, first observation quiet, EC>=10% to run, EC>=25% to transmit (2 s later), 60 s post gap, auto vs remind (rerunnable only + one-shot reminder). Pure rules in `SciencePolicy`. `ChatWindow.ToolFromMenu` runs ported tools in-mod when AI off or native_chat on (menu Science/Landing buttons no longer need bridge HTTP).
**Tests run:** C# "P5-3 lifecycle + science: 10 behavior checks passed"; matrix rows -> native; pytest 484; Release 0 errors.
**Still needs live check:** all tools in flight; launch_craft only works from the flight scene (Execute requires an active vessel); watcher posts; the native watcher mode is separate from the bridge's bridge_settings.json while the bridge is up.

## P5-4 - orbital / docking / MechJeb-optional - PARTIAL (tests; needs live check)
**Changed:** `NativeOrbital.cs`: circularize, change_apoapsis/periapsis, deorbit_burn (atmosphere gate), change_inclination (AN/DN) build native maneuver nodes from `OrbitMath` (vis-viva) and execute via MechJeb node executor when present, else 'node created; burn manually - needs MechJeb'. warp_to_apoapsis / warp_to_soi_change (TimeWarp.WarpTo with lead), sync_orbit_altitude, time_to_target native. sun_lock / antenna_lock via stock SAS LockRotation (no MechJeb/kRPC). mechjeb_ascent, dock_with, land_at, land_at_ksc via reflection-only `MechJebLink` (no compile dependency) with clear 'needs MechJeb' or 'MechJeb API mismatch' text; land_at_ksc for planes uses native autoland.
**Not ported (stay on bridge fallback, listed in matrix as p5-4):** transfer_to, match_target_plane, launch_to_target_plane, course_correction, station_keep, apsis_longitude (need MechJeb maneuver-planner operations; version-sensitive).
**Tests run:** C# "P5-4 orbital math: 10 behavior checks passed" (vis-viva, circularize, Hohmann/deorbit sign, plane change AN/DN, Kerbin sync alt 2863 km, warp lead, apsis gates); old orchestrator test now uses transfer_to as the unported example; pytest 484; Release 0 errors.
**Still needs live check:** node dv/direction in game (esp. inclination sign), MechJeb reflection names on Luke's MJ version (ascent module/settings names changed in MJ 2.14+), sun-lock LockRotation behaviour; attitude lock / native science only tick when SafetyTick runs (AI off or bridge not answering).

## P5-5 - embedded llama.cpp with download buttons - DONE (tests + real net472 smoke; needs live check in KSP/Unity)
**Wrapper decision:** no LLamaSharp (needs System.Memory/Buffers/Unsafe/etc.; KSP's Mono ships only System, System.Core, System.Xml, System.Configuration, System.Security - same class of problem as the System.Web.Extensions crash). Instead `LlamaNative`: LoadLibraryEx by full path + GetProcAddress delegates against llama.cpp **b11538** (pinned). Win x64 ABI: big param structs kept as opaque buffers; only fields at offsets pinned from llama.h@b11538 are written, after an ABI check of the defaults (refuses on mismatch).
**Changed:** `LlamaRuntime` (pinned URL llama-b11538-bin-win-vulkan-x64.zip, SHA-256 621ec0ed...ccf5, 33,459,184 bytes, MIT) + `MiniZip` (DeflateStream; no System.IO.Compression in KSP). Download runtime -> verify SHA -> extract only llama/ggml/ggml-base/ggml-vulkan/ggml-cpu-*/libomp to `PluginData/native/*.bin` + manifest (per-file SHA). At load the .bin are SHA-checked and copied to `%LOCALAPPDATA%\KSPChatBridge\native\b11538\*.dll` (outside GameData) so llama.dll->ggml.dll imports resolve; ggml backends loaded from there (Vulkan GPU + CPU variants). `EmbeddedLlm`: lazy load on first chat, GPU=999 / Hybrid=18 (half of Qwen2.5-3B's 36) / CPU=0 layers, context 16k/20k/24k, threads = cores/2 (<=8), stateless generate (KV clear, 2048-token prompt chunks, min-p/top-p/temp sampling, 512-token replies), cancel via abort callback (chat Clear), unload on AI off / settings change / Unload button. `EmbeddedPrompt`: OpenAI messages+tools <-> Qwen2.5 ChatML with Hermes `<tool_call>` blocks, so InModChatSession tool loop works unchanged. Provider "Embedded Qwen (in-mod)" added to the backend list; `OpenAiBackend.Resolve("embedded")` says which download is missing. Settings UI: Download runtime / Cancel / Unload model next to the existing model download; fixed `AiSettings.ApplyOffload` which forced every Hybrid/GPU choice back to CPU (freeGpuBytes: 0). Model download was already pinned in P5-1.11. Packaging still rejects .gguf / native .dll / PluginData/native / PluginData/models.
**Tests run:** C# "P5-5 embedded llama: 15 behavior checks passed" (keep-list, cache outside GameData, layers/context, checksum refusal, zip extract round-trip, materialize + damaged-file refusal, ChatML render, tool_call parse, truncated call hidden, ABI guard, provider readiness). Opt-in real smoke `AICS_LLAMA_SMOKE=dist\llama-cache`: pinned zip SHA OK, real extract (no .dll stored), LoadLibrary + ABI check + load stories260K.gguf + CPU generate on net472 -> passed. pytest 484; Release 0 errors.
**Still needs live check:** inside KSP (Unity 2019 Mono) - runtime download, model download (~1.9 GB), first chat load time, GPU (Vulkan) vs Hybrid vs CPU, 16k/24k context memory, tool calls from Qwen 3B, Clear cancels, AI-off unload. Requires MSVC 2015-2022 x64 runtime (normally present).

## P5-6 - bridge-free release candidate - DONE (tests; needs live check)
**Changed:** `package_release.ps1 -BridgeFree` builds `dist\KSPChatBridge-<ver>-bridgefree-rc.zip`: Plugins/KSPChatBridge.dll + version/LICENSE/README + `templates/` (env.example with key names only, playstyle_notes.example.md) + BRIDGE_FREE.md; refuses any exe; stage + zip detectors still reject non-approved .dll, .gguf, .pyd/.so, PluginData/models|native, .env. `-Suffix` names the rollback build. `PluginDataMigration` (v2) runs at startup from InModAiHost: backs up all PluginData root user files (.env keys, native_settings.json spots/craft notes, bridge_settings, personalities/memory, window layouts) to `PluginData/backups/v<from>-<utc>/` first, then only adds missing values (bridge science_mode -> native), never deletes/overwrites; idempotent. docs/BRIDGE_FREE.md documents download buttons, MechJeb-optional, Desktop MCP discontinued, remaining bridge-only tools and rollback.
**Tests run:** C# "P5-6 PluginData migration: 6 behavior checks passed"; pytest 486 (test_packaging: -BridgeFree switch + built RC zip contents: only the plugin DLL, no exe/gguf/bin); Release 0 errors. Built dist\KSPChatBridge-0.1.1-bridgefree-rc.zip.
**Still needs live check (P5-7, Luke):** smoke with AICSBridge.exe absent - AI-off flight, runtime+model downloads, embedded + cloud chat, native tools, migration backup on a real PluginData.

## Oct 9 decision - native controller flies with AI on - DONE (tests; needs live check)
**Changed:** `NativeSafety.NativeOwns(ai, nativeReady, nativeChat)`: AI off = after handoff (as before); AI on = whenever in-mod chat/tools (native_chat, default on) is enabled. `NativeFlightController.OwnsControls` now gates the flight tick, FlyByWire, `Execute` and the safety tick (so attitude lock + science watcher run with AI on). Menu flight plan / taxi / powered descent, ToolFromMenu and the status rows use native when it owns.
**Tests run:** C# "Native ownership with AI on: 2 behavior checks passed"; pytest 486; Release 0 errors.
**Still needs live check:** with AI on, chat-issued holds/autoland/fly_to actually fly; the bridge (if still running with native_chat on) must not fly too - its kRPC autopilot/watchers are not stopped by this change.

## Last 6 bridge-only tools -> in-process MechJeb planners - DONE (tests; needs live check)
**MechJeb found:** GameData\MechJeb2\Plugins\MechJeb2.dll file version 2.15.3.1 (assembly 2.15.0.0), plus MechJebLib/alglib. Types and members reflected from that exact dll (MuMech.Operation*.MakeNodes(Orbit, UT, MechJebModuleTargetController), TimeSelector._allowedTimeRef/_currentTimeRef, core fields Target/Node/Staging/Landing/AscentSettings + Ascent property, Ascent.StartCountdown, VesselExtensions.PlaceManeuverNode; OperationLongitude reads Target.targetLongitude - checked via IL).
**Changed:** NativeMechJebOps.cs: transfer_to (OperationGeneric for moons, OperationInterplanetaryTransfer w/ phase wait for sibling planets, OperationMoonReturn to parent; first node only), match_target_plane (OperationPlane @ REL_HIGHEST_AD), course_correction (OperationCourseCorrection, target = KSP target or the encounter body), apsis_longitude (SetPositionTarget + OperationLongitude @ APOAPSIS), station_keep (chained Apoapsis->Longitude->Circularize on patched orbits, ExecuteAllNodes), launch_to_target_plane (own launch-window solver MechJebPolicy.LaunchWindow + AscentSettings inclination/alt + StartCountdown). All run node executor with autowarp + autostage. MechJebPolicy.VersionGate: missing -> 'needs MechJeb', <2.15 -> 'Unsupported MechJeb version'. Fixed existing mechjeb_ascent/land_at/execute to 2.15 field names (Ascent/AscentSettings/Target/Node/Staging/Landing).
**Tests run:** C# "MechJeb planner ports: 14 behavior checks passed"; pytest 486 (tool matrix: 6 tools now native); Release 0 errors.
**Still needs live check:** every planner in game (reflection signatures only verified against the dll offline); launch-window sign (north/south) and lead time; station_keep chain on patched orbits.

## P5-7 live-test fixes (Oct 9, Luke's first bridge-free session) - DONE (tests; needs live check)
**Problems (live):** menu greyed out + 'Bridge not responding' with native_chat on; no crew chatter; replies voiced by 'AICS'; 'land' did nothing (in-mod chat only exposed 14 tools - land wasn't one); takeoff never released the parking brake; 'KSP 27' unknown runway; '[bridge error] ConnectFailure' from the landing panel; model stayed loaded at main menu.
**Changed:** NativeSafety.NeedsBridge/MissingDeps + BridgeLauncher.UseBridge: menus, warnings, kRPC dependency, polls (health/landing/events/telemetry/mayday lights) only in bridge-chat mode; labels 'AI', 'In-mod AI', 'Chat in-mod'. BridgeHttp.Create is the only path to :8765 and refuses in native mode ('Not available in-mod yet'); bridge watchdog never starts/polls natively; landing panel runs ported tools natively. tools/gen_tool_schemas.py -> NativeToolSchemas.cs: all 85 ported tools offered to the in-mod model. CrewVoice: pilot (Pilot trait, else first crew) voices replies with persona prompt + 'Captain'. CrewChatter: native canned port of crew.py emergency reactions (part loss, flameout) + 'are we there yet' trip chatter. Takeoff releases brakes at roll; parking never touches brakes with a command/plan active. FlightResidualPolicy.BuiltInRunway: KSC/KSP/runway/rwy/27/09/island fuzzy. Main menu: cancel reply, unload model, lazy reload.
**Tests run:** C# 'Live bug fixes: 25 behavior checks passed'; pytest 491 (new: test_no_bridge_native, test_tool_schemas); Release 0 errors.
**Still needs live check:** all of the above in game; tool list size (~6k tokens) vs 16k context on Qwen 3B; crew lines are canned (no LLM-flavoured lines yet); '@name' intercom talk isn't wired into native chat yet.
