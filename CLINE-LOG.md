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
