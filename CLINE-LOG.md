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
