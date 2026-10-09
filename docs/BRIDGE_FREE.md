# Bridge-free release candidate (P5-6)

This build runs entirely inside KSP: no `AICSBridge.exe` is shipped in the bridge-free zip.

## What you get
- In-mod chat (LM Studio, Ollama, cloud keys, or **Embedded Qwen**) and native tools (flight, lifecycle/science, orbital).
- **Download runtime** (llama.cpp b11538, ~33 MB, MIT) and **Download model** (Qwen2.5-3B-Instruct Q4_K_M, ~1.9 GB,
  Qwen Research License) buttons in AICS > Settings. Nothing is bundled; both are SHA-256 checked.
  Runtime files are stored as `PluginData/native/*.bin` and loaded from `%LOCALAPPDATA%\KSPChatBridge\native\b11538`.
- Offload: GPU (Vulkan, all layers) / Hybrid (half) / CPU; context 16k / 20k / 24k.
- MechJeb2 is optional: maneuver nodes are planned natively; MechJeb auto-executes them and provides ascent, docking and
  targeted landing. Without MechJeb those say "needs MechJeb".

## Migration
- First start runs a versioned PluginData migration: user files (settings, `.env` keys, landing spots, craft notes,
  memory/personalities, window layouts) are **backed up** to `PluginData/backups/` and never deleted or overwritten.
- ChatGPT Desktop stdio-MCP is discontinued in the bridge-free build. Use in-mod chat (any provider above).
- Still bridge-only (unported): transfer_to, match_target_plane, launch_to_target_plane, course_correction,
  station_keep, apsis_longitude. Use the rollback package if you need them.

## Rollback
The `-bridge-fallback` zip (built from the same commit with `tools\package_release.ps1`) still ships `AICSBridge.exe`
and keeps the bridge as a fallback. A local git tag `rollback/bridge-fallback-<version>` marks that commit.
