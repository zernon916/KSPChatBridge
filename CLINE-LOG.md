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
