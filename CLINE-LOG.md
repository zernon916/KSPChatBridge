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
