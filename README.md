# AICS - KSP Chat Bridge

![AICS - KSP Chat Bridge](docs/images/aics-header.jpg)

*Repository, CKAN identifier and DLL name: KSPChatBridge.*

**New here? Start with the [wiki](https://github.com/zernon916/KSPChatBridge/wiki)**: installation, a five-minute quick start,
every command, the autopilots, the crew, and troubleshooting.

Chat with your Kerbal Space Program vessel. A small Python bridge turns chat messages into
kRPC + MechJeb actions; an in-game IMGUI window (KSPChatMod) and the ChatGPT desktop app (MCP)
both talk to the same tool set.

```
 KSP (kRPC server :50000, MechJeb2, kRPC.MechJeb)
   ^  kRPC
   |
 Python bridge (kspchat/)  <-- HTTP :8765 --  in-game chat window (GameData\KSPChatBridge)
   |  ^
   |  +-- stdio MCP  <-- ChatGPT desktop app (Codex)
   v
 LLM backend: LM Studio (local, default) | Ollama | ChatGPT (desktop MCP or API) | free clouds: Gemini, Groq,
              OpenRouter | Hugging Face | Custom OpenAI-compatible | Claude (soon) | Grok Bot (soon)
```

## Requirements
- **KSP 1.12.x** (developed on 1.12.5).
- **kRPC 0.6.x** (required; enable the server's auto-start). The bridge talks to KSP only through kRPC.
- **MechJeb2 + kRPC.MechJeb** (recommended): rocket ascent / landing / docking tools use them; planes, helicopters,
  props, crew chatter and the systems dashboard work without (the AICS menu shows `no MJ`).
- Python 3.12 with `pip install --user -r requirements.txt` (krpc 0.6.0, protobuf, mcp>=2.3).
- LM Studio with `qwen/qwen3.5-9b` (or set `LMSTUDIO_MODEL`). The bridge auto-loads it with a 16k context
  via `lms` if nothing is loaded (needs ~7 GB VRAM next to KSP).

## Install
The mod is two parts: the in-game plugin (GameData) and the Python bridge (this repo). CKAN / the release zip install
only the plugin; the bridge always comes from this repository.

1. **Plugin via CKAN** (once listed): search for *KSP Chat Bridge* (`KSPChatBridge`); CKAN pulls in kRPC and offers
   MechJeb2 / kRPC.MechJeb. **Manual**: unzip `KSPChatBridge-<version>.zip` from the GitHub releases into the KSP folder
   so you get `GameData/KSPChatBridge/Plugins/KSPChatBridge.dll`. Install kRPC (and optionally MechJeb2 +
   kRPC.MechJeb) yourself.
2. **Bridge**: clone or download this repo, install Python 3.12 and run `pip install --user -r requirements.txt`.
3. Start KSP once, then set `bridge_dir` (and `python` if it isn't on PATH) in
   `GameData/KSPChatBridge/PluginData/bridge.cfg` so the plugin can auto-start the bridge.
4. Pick an AI backend (LM Studio locally, or a free cloud key in `.env`, see *Backends and models*).

Release packaging (maintainers): `powershell -File tools\package_release.ps1` builds the plugin without touching the
live GameData and writes `dist/KSPChatBridge-<version>.zip`; the CKAN metadata draft is `ckan/KSPChatBridge.netkan`.

## Run
1. Start KSP and load a save (kRPC auto-starts). The mod **auto-starts the bridge** (hidden window) if
   `GET /health` doesn't answer, and stops it again when KSP quits (only if it started it). Settings in
   `GameData\KSPChatBridge\PluginData\bridge.cfg` (created on first run): `autostart = true`,
   `stop_on_quit = true`, `bridge_dir = C:\path\to\KSPChatBridge` (set it to where you cloned this repo), `python = python`.
   Failures show up as `[bridge] ...` lines in the chat window.
2. Manual alternative: double-click `start_bridge.cmd` (or `python run_bridge.py serve`).
3. In game press **Alt+K** or click the speech-bubble button on the stock toolbar, type, press Enter. Pick
   the AI in **AICS -> Settings -> AI backend** (dropdown; right-click the toolbar button or Alt+J opens AICS)
   or type `/ai gemini` (`/ai` alone lists them). The choice is saved. Keyboard controls are locked while the
   text box has focus, so Space won't stage.

Restart the bridge without killing an in-flight chat: `powershell -File tools\restart_bridge.ps1`
(waits until `/health` reports `busy: 0`).

Terminal testing: `python run_bridge.py chat "what is my ship status"` or `python run_bridge.py repl`.

## Backends and models
| Settings dropdown | id (`/ai <id>`) | Needs | Default model |
|---|---|---|---|
| LM Studio | `local` | LM Studio server on :1234 (auto-started) | whatever model is **loaded** in LM Studio; if none, loads `qwen/qwen3.5-9b` with 16k context |
| Ollama | `ollama` | `ollama serve` on :11434 (not installed on this PC yet) | `OLLAMA_MODEL` or the first installed model |
| ChatGPT (MCP, default) | `chatgpt` | ChatGPT desktop app with `ksp_bridge` - **no key** (see below) | whatever the ChatGPT thread uses |
| ChatGPT (API) | `chatgpt` + "ChatGPT via: OpenAI API key" / `/chatgpt api` | `OPENAI_API_KEY` | `OPENAI_MODEL` (default `gpt-4.1-mini`) |
| Gemini | `gemini` | `GEMINI_API_KEY` (or `GOOGLE_API_KEY`), free key at aistudio.google.com | `GEMINI_MODEL` (default `gemini-flash-latest`) |
| Groq | `groq` | `GROQ_API_KEY`, free key at console.groq.com | `GROQ_MODEL` (default `openai/gpt-oss-120b`) |
| OpenRouter | `openrouter` | `OPENROUTER_API_KEY`, free key at openrouter.ai | `OPENROUTER_MODEL` (default `openrouter/free` = random tool-capable free model) |
| Hugging Face | `huggingface` (`/ai hf`) | `HF_TOKEN` with "Inference Providers" permission | `HF_MODEL` (default `openai/gpt-oss-120b:fastest`) |
| Custom (OpenAI-compatible) | `custom` | `CUSTOM_AI_URL` + `CUSTOM_AI_MODEL` (+ `CUSTOM_AI_KEY`) | `CUSTOM_AI_MODEL` |
| Claude (soon) | `claude` | stub; meanwhile use Custom with Anthropic's OpenAI-compat endpoint | - |
| Grok Bot (soon) | `grokbot` | stub (planned: file-queue channel like ChatGPT MCP) | - |

Keyed backends without a key just answer "set X" in the chat (nothing breaks).
**Enter keys in game:** AICS -> Settings -> pick the backend -> paste the key in the **API key** field (password-style,
`Paste` button or Ctrl+V) -> **Save** (or Enter). Custom also has **Base URL** and **Model** fields. The bridge
(`POST /api_key`, localhost only, JSON only) writes it to the git-ignored repo **`.env`** and applies it in-process:
no bridge restart. Status shows `configured (••••last4)` / `missing`; the save reply says whether the API accepted the
key. **Clear key** (click twice) removes it. Keys are never logged or shown in full (`GET /api_keys` = masked status).
By hand still works: `.env` in the repo root (`GEMINI_API_KEY=...`, one per line; real env vars win at bridge start),
then `tools\restart_bridge.ps1`. The Settings dropdown marks keyed backends `(configured)` / `(not configured)` from
`GET /health` `backends_ready` (names only, never the keys).

### Free cloud AIs (no GPU needed)
All optional: no key = that entry just says which key to add. Get a free key, paste it in AICS > Settings.
Limits as published around 2026-10 (they change often; free tiers may also use your prompts for training):
| Backend | Free limit | What it means here |
|---|---|---|
| Gemini (AI Studio key) | per-model RPM / requests-per-day caps (Flash models are the generous ones; Pro is tight) | fine for casual play; see ai.google.dev/gemini-api/docs/rate-limits |
| Groq | 30 req/min, 1,000 req/day, **8K tokens/min**, 200K tokens/day (gpt-oss-120b/20b, qwen3.8-27b) | each request carries ~5K tokens of tools + system prompt, so a tool step can hit the per-minute cap; the bridge waits `Retry-After` (<= 30 s, `CLOUD_RETRY_MAX_WAIT`) and retries twice. ~40 messages/day |
| OpenRouter `:free` | 20 req/min, **50 req/day** (1,000/day after a one-time $10 credit purchase) | one chat message = 1-4 requests -> ~15-25 messages/day |
| Hugging Face router | free accounts get little or no monthly credit (HF's own docs disagree: $0.10 vs none) | effectively pay-as-you-go; included for completeness |
Rate-limited replies say so ("rate limit hit ... switch AI in AICS > Settings"); bad keys say "rejected the API key".

Custom examples: `https://api.mistral.ai/v1`, DeepSeek `https://api.deepseek.com/v1`, xAI `https://api.x.ai/v1`,
Anthropic `https://api.anthropic.com/v1` (OpenAI SDK compatibility), any local vLLM / llama.cpp server. The model must
support OpenAI-style tool calls or it can only chat, not fly.

### "Any VS Code AI?" - what they actually map to
VS Code AI extensions are **clients**, not free local APIs: Copilot, Gemini Code Assist, Claude Code, Cline, Continue,
Cursor etc. call a vendor API with their own login/key; there is no localhost endpoint the bridge can borrow.
- **GitHub Copilot**: no public chat API for other apps (its models are only reachable inside VS Code through the
  extension `vscode.lm` API; proxying that out is fragile and against the spirit of its terms). Not a backend.
  What does work: Copilot agent mode is an **MCP client**, so it can use the `ksp_bridge` MCP server (all KSP tools,
  and `game_chat_pending/wait/reply` to answer the in-game chat, shown as "ChatGPT" today) - add it in `.vscode/mcp.json`.
- **Gemini Code Assist / Gemini CLI**: same Google account -> make an AI Studio key -> `gemini` backend (free tier).
- **Claude Code / Claude for VS Code**: Anthropic key -> `custom` now (OpenAI-compat endpoint), native `claude` later.
- **Cline / Continue / Roo**: bring-your-own-key clients; reuse that key here via `chatgpt` (API), `gemini` or `custom`.
- **Grok Bot**: planned file-queue backend (`grokbot`, soon).
Rule of thumb: anything with an OpenAI-compatible (or plain HTTP) chat API + a key/endpoint we have can be added;
anything that is only an editor extension can at most act as an MCP client of `ksp_bridge`.

Chat commands (in game or `repl`): `/model` lists the current backend's models (LM Studio marks the
loaded one), `/model <name or part of it>` switches for this session (LM Studio unloads the old model
and loads the new one with 16k context, ~30 s), `/model default` goes back to the default.
Env overrides: `LMSTUDIO_MODEL` (force a model), `LMSTUDIO_FALLBACK_MODEL`, `LMSTUDIO_CONTEXT`,
`OLLAMA_URL`, `OLLAMA_MODEL`, `OPENAI_MODEL`, `OPENAI_BASE_URL`, `GEMINI_MODEL`, `GEMINI_BASE_URL`,
`GROQ_MODEL`, `OPENROUTER_MODEL`, `HF_MODEL` (+ `*_BASE_URL`), `CUSTOM_AI_URL`, `CUSTOM_AI_MODEL`, `CUSTOM_AI_KEY`,
`CLOUD_RETRY_MAX_WAIT`. Offline check: `python tests/test_backends.py`.

Which local model? See `docs/model_comparison.md`: default `qwen/qwen3.5-9b`, lightweight `google/gemma-4-e4b`.
Re-run the comparison with `python tests/model_eval.py <model> [<model> ...]` (KSP running, Grok Test).

## In-game window
Alt+K or the stock toolbar speech-bubble button. Fixed-size window (drag the title to move, drag the
`//` grip upper-right (grows up/right) or bottom-right to resize, min 300x200); history scrolls and jumps to the
newest message; multi-line input (word wrap; Enter sends, Shift+Enter = new line) that grows downward up to ~5
lines, then scrolls. No backend buttons on the window any more (title shows backend / model); switch in
AICS -> Settings or with `/ai <id>`. Position, size and selected backend (by id) are saved to
`GameData\KSPChatBridge\PluginData\window.txt`. "Clear" wipes the window and the bridge-side history.
While a landing is in progress (land_here / land_at / land_at_ksc / land_plane / land_at_spot, or a parachute
descent) a live line under the history shows the estimated distance and time to touchdown (`GET /landing`).

**Landing menu** ("Land" button next to Send): mode **V** (vertical, rockets) or **H** (horizontal, planes:
runways only). Pick a spot from the list (built-ins: KSC Pad, Runway 09, Runway 27, Island Airfield; plus your
own) and press *Land at spot*; *Land at KSP target* lands next to whatever is targeted on the map (vessel,
flag, rover/probe "beacon"; a station in orbit triggers rendezvous + docking instead); *Mark current position*
saves where you are (H mode: a runway starting here in your current heading, ~1 km long); or type lat/lon
(+ landing heading for H) and save. *ETA* and *Abort* buttons. Results appear in chat as `Landing: ...`.
The AI does the same by chat: "land at Island Airfield", "save this spot as Mun base", "land next to my flag".

## Tools (same for chat, HTTP and MCP)
get_status, stage, set_throttle, set_sas, set_sas_mode, run_science, mechjeb_ascent,
circularize, warp_to_apoapsis, transfer_to, course_correction, warp_to_soi_change,
get_delta_v, deorbit_burn, land_at, land_at_ksc, land_here, land_plane, land_at_spot, fly_to, get_landing_eta,
list_landing_spots, save_landing_spot, dock_with, deploy_parachutes, abort, list_craft, launch_craft,
recover_vessel, set_science_watcher, set_ai_name, remember_preference.
The system prompt tells the model to do only what was asked and to confirm orbit-changing burns
(ascent, circularize, deorbit, transfer, landing) unless the request explicitly asks for them now.
`land_here` runs our own landing controller (`kspchat/lander.py`: deorbit if in orbit, reentry burn,
suicide burn to ~1.5 m/s, legs + chutes, reports touchdown speed/tilt/parts lost in chat). `land_at_ksc` /
`land_at(lat, lon)` use MechJeb to deorbit and steer towards the target, then hand over to the same controller
below 5 km. Needs engines that work at sea level (Swivel/Reliant, not Terrier/Poodle) on Kerbin.
Tested: hop 1.6 m/s, from 80 km orbit 1.4 m/s, targeted KSC 1.4 m/s (3.6 km from the pad).
Landing/deorbit tools first run a delta-v feasibility check (`get_delta_v`: per-stage vacuum delta-v from a
staging simulation + total, surface TWR, and a needed-dv estimate `v*a/(a-g)` with no drag credit)
and refuse with "NOT ENOUGH DELTA-V: have ~X, need ~Y, suggest chutes..." unless `force=true`.

**Plane landing** (`land_plane`, aircraft only - rockets use `land_here`): our own autoland
(`kspchat/plane.py`; MechJeb's spaceplane autoland was skipped - sloppy in Luke's experience). Picks runway 09/27
by the side you approach from, flies to an 11 km final on the extended centerline (teardrop if it arrives
pointing the wrong way), 3 deg glideslope, gear down, flare at ~20 m to ~1-2 m/s sink, touches down on the mains
nose-up, lets the nose settle, pulsed then full brakes. **Speeds adapt per plane**: the first time a craft is
landed it flies a short slow-flight test at altitude to measure its stall speed (AoA limit 15 deg, flaps out if
it has dedicated flap parts), then approaches at 1.3 x stall and touches down at ~1.15 x stall; the stall speed
is cached per craft name in `bridge_settings.json` (`stall_speeds`). Flaps only on dedicated flap parts
(elevons/ailerons are never used as flaps), stepped 1/5/15/25/30 with per-setting speed limits; airbrakes if
fitted. Feasibility check first: wheels + wings, within 150 km, below 20 km, fuel or glide range.
Tested with the stock Aeris 3A (stall 30.5 m/s -> approach 40 m/s).
`land_at_spot` also flies the plane autoland to other runways (Island Airfield; your saved H spots).
**Plane flight rules** (Luke): speed limits are equivalent airspeed - 200 m/s target / 220 m/s hard cap down low,
rising with altitude; supersonic dashes fly Mach 1.5 above 8 km. Throttle moves in 5 % steps with ~4 s for jet
spool-up and only when the speed is stable (never 0 in flight, >= 30 % in climb/cruise); speed always comes from
the throttle - it never trades altitude for speed (climbs hold 5-15 / 5-10 deg, nose-down only on final or a planned
descent). Bank: 20 deg at low speed, 10 deg above 250 m/s. Takeoff uses the original smooth takeoff, then hands
off at 100 m AGL. Details and crash lessons: HANDOVER.md "Plane flight rules".
**Takeoff / fly_to**: `land_plane`, `land_at_spot` and `fly_to` also work from the ground: full power, wheel
steering on the current heading, rotate at ~1.25 x stall to 9 deg, gear up at 25 m, pattern from 150 m.
`fly_to(name, cruise_altitude_m, cruise_speed)` flies to a runway spot and lands: climbs (up to 25 m/s),
cruises (supersonic only up high: a dash above 300 m/s is lifted to >= 6 km), then a planned 6 deg descent that
also bleeds speed early (~2.5 m/s^2) so it reaches the approach slow; fuel-range check first. Short strips get an
earlier aim point (30% of the strip). Island Airfield = rough 500 m strip measured from the stock launch-site
spawn (lands southbound, "18"). `launch_craft(..., site="Island_Airfield")` launches there.
Gear goes down late (drag wastes fuel): ~2.5 km / 30 s before the threshold, or below 80 m. Shape checks: `land_plane` refuses craft without
wheels or real wings (fins/winglets don't count) and points to `land_here`; `land_here`/`land_at` refuse a plane
with surface TWR < 1.6 (can't hover tail-first) and point to `land_plane` (`force=true` overrides).
**One autopilot at a time:** a running lander / plane autoland / docking claims the craft (heartbeat in
`bridge_settings.json`, works across the bridge, MCP server and test scripts); other landing tools answer
"Busy: ... say 'abort' first", and `abort` stops controllers in any process.

**Distance / ETA** (`get_landing_eta`, all values labelled estimates): planes = great-circle distance to the
runway threshold (via the approach entry point if not yet on final) / ground speed; rockets = vacuum ballistic
impact point from the current orbit (drag makes the real one shorter) and ETA from the suicide-burn model
(2 x height / sink rate while braking), height / sink rate under chutes, or the ballistic time to impact.

**Docking** (`dock_with`, EXPERIMENTAL, not yet flight-tested): rendezvous with a vessel in orbit (KSP target or
name) with MechJeb's rendezvous autopilot, then MechJeb's docking autopilot to the nearest free station port of
the same size as one of ours (Jr / standard / Sr). Checks free ports, RCS + monoprop, and delta-v (Hohmann +
plane change + 40 m/s) first. Add a tool = add a typed, documented function in `kspchat/ksp_actions.py` and list it in `TOOLS`.

## ChatGPT desktop app (MCP)
A **stdio** MCP server launched by the app
from `%USERPROFILE%\.codex\config.toml` (no HTTP, no tunnel). Add this block, restart the app fully,
open a new thread and type `/mcp`:

```toml
[mcp_servers.ksp_bridge]
enabled = true
command = 'C:\path\to\Python312\python.exe'
args = ['C:\path\to\KSPChatBridge\run_bridge.py', 'mcp']
cwd = 'C:\path\to\KSPChatBridge'
```
(Use the output of `where python` for `command` if Python lives elsewhere.) Test without the app:
`python tests\test_mcp.py`.

## In-game chat with the ChatGPT desktop app (no API key; opt-in, heavy token use)
**Opt-in only.** `chatgpt_mode` defaults to `api` (OpenAI key, paste it in AICS > Settings). The MCP path burns a lot
of ChatGPT tokens (the desktop thread keeps polling/waiting), so it is labelled "ChatGPT desktop (MCP) - heavy token
use" in AICS > Settings and is never a default backend.
MCP is inverted (ChatGPT calls our tools; the bridge can't call ChatGPT), so the in-game chat uses a reverse
channel when the backend is **ChatGPT** and `chatgpt_mode` is `mcp`:
1. You type in the KSP chat (backend ChatGPT). The bridge queues the message in `chatgpt_chat.json`
   (git-ignored) and the window shows `Bridge: Waiting for ChatGPT desktop (MCP)... queued #N` - no
   `OPENAI_API_KEY` needed, no LM Studio.
2. In the ChatGPT desktop app, in a thread with the `ksp_bridge` MCP server, say **"check KSP chat"**
   (one pass: `game_chat_pending` -> act with the KSP tools -> `game_chat_reply`) or **"keep answering KSP
   chat"** (it loops `game_chat_wait`, which blocks up to 55 s per call, under the app's 60 s tool timeout).
3. The reply shows up in the game window within ~3 s as `ChatGPT: ...` (via `GET /events`).

MCP tools: `game_chat_pending()`, `game_chat_wait(timeout_s=45)`, `game_chat_reply(text, reply_to=0)`
(0 = close all open messages). Switch modes with `/chatgpt mcp` / `/chatgpt api` in the chat, AICS ->
Settings -> "ChatGPT via", or `"chatgpt_mode"` in `bridge_settings.json`. The chat window's Clear button
also drops unanswered queued messages. Offline test: `python tests\test_mcp_chat.py`.

## Science watcher
While `serve` runs, a background thread watches the active vessel. When it enters a new science
situation (landed, splashed, flying low/high, space low/high), a new biome, or a new SOI, it:
- **remind** (default): posts (only when there is something to do; max one idle notice per 20 s) a "Science:" notice in the in-game chat and auto-runs + transmits
  *rerunnable* experiments (crew report, thermometer, ...); lists one-shot experiments still available.
- **auto**: runs and transmits every available experiment that holds no data and whose subject isn't complete.
- **off**: nothing.
Experiments that already hold data or are inoperable are skipped; nothing runs below 10% electric charge
and nothing is transmitted below 25%. Switch by chat ("science auto on" / "science auto off" / "science
watcher off") - tool `set_science_watcher` - or edit `bridge_settings.json` (`{"science_mode": "remind"}`).
The mod polls `GET /events` every 3 s to show the notices.

## Naming the assistant
Say e.g. "I think I'll call you Bob": the model calls `set_ai_name`, the name is stored in
`bridge_settings.json` (`ai_name`), injected into the system prompt, and the in-game window shows
`Bob: ...` (sent via the `X-AI-Name` response header). The window title shows the backend and the
model that answered last (`X-AI-Model` header). Renaming overwrites it.

## Playstyle memory and logs
- `playstyle_notes.md` (created from `playstyle_notes.example.md` on first run; git-ignored): one `- ` bullet per preference, injected into every system prompt. The model
  adds notes with `remember_preference`; near-duplicates are merged, max 100 notes (oldest dropped).
  Edit by hand any time.
- `logs/sessions-*.jsonl`: one JSON line per chat turn (user text, tool calls, reply, backend), rotated
  at 2 MB, newest 10 files kept - usable later for fine-tuning. `logs/bridge.log` rotates at 1 MB x 3.

## HTTP API (used by the mod)
- `POST /chat` `{"message","model","session"}` -> `{"reply","tools"}`; `?format=text` returns plain text.
- `POST /reset` `{"session"}`; `GET /health` (+ `chatgpt_mode`, `chatgpt_open`, `backends_ready`); `GET /events?since=N[&format=text][&chat=1]`
  (science + landing notices + ChatGPT-desktop replies; `chat=1` tags those with `@` so the mod drops the `Bridge:` prefix).
- `POST /setting {"key":"chatgpt_mode","value":"mcp|api"}` (AICS Settings).
- `GET /landing?format=text` (live distance/ETA line, empty when no landing), `GET /spots?format=text`,
  `POST /tool {"name","args"}` (landing-menu tools only, no model), `POST /shutdown` (sent by the mod on quit).
- Flight Plan (AICS): `GET /flightplan/template?kind=circle|cruise|circuit|orbit`, `GET /flightplan?format=text&since=R`
  (`R<TAB>status` + plan pushed from chat), `POST /flightplan/draft {"request","from_chat"}` (AI fill),
  `POST /flightplan/check|fly {"plan"}`, `POST /flightplan/stop`. `GET /taxi` (taxi points). Grammar: docs/AICS_MENU.md.

## Building the mod
`cd KSPChatMod && dotnet build -c Release` (needs only the .NET SDK; .NET Framework 4.7.2 reference
assemblies come from NuGet). A Release build copies `KSPChatBridge.dll` to
`GameData\KSPChatBridge\Plugins\` (add `-p:SkipInstall=true` to build without installing). Set `-p:KSPDir=...` if KSP
is elsewhere.

## License
MIT - see [LICENSE](LICENSE). Copyright (c) 2026 Luke Benko (zernon916). kRPC, MechJeb2 and kRPC.MechJeb are separate
mods under their own licenses and are not included.
