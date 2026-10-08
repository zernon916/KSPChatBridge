# KSPChatBridge - HANDOVER

_Last updated: 2026-10-08 evening ET (Flight Plan runner + all AICS stubs wired, chat window x; earlier: in-game API key entry in AICS Settings; earlier: AI backend dropdown in AICS Settings; free clouds Gemini/Groq/OpenRouter + Hugging Face + Custom OpenAI-compatible backends, by Grok Bot). Keep this file current; it is written so another
assistant (e.g. ChatGPT) can continue the work cold._

## Current state (summary)
- API keys in game (2026-10-08): AICS > Settings shows an API key field (password-style, Paste, Save/Clear, status configured ••••last4 / missing) for Gemini, Groq, OpenRouter, Hugging Face, ChatGPT API mode and Custom (+ Base URL / Model). Saved via `POST /api_key` into `.env` and applied in-process; no restart.
- Phase 1 smoke test: DONE. RealChute.dll loads, 0 exceptions; kRPC.MechJeb logs "MechJeb found!" with 0 "not found" warnings.
  Kerbal X launched via kRPC, `mech_jeb.api_ready == True`, ascent autopilot reachable.
- Phase 2 (how FunRobotAgent exposed MCP): DONE. stdio MCP server, official SDK, launched by the
  ChatGPT (Codex) desktop app from `%USERPROFILE%\.codex\config.toml`; no HTTP/tunnel. Reused here in Python.
- Phase 3 bridge: DONE. Chat loop tested with LM Studio qwen/qwen3.5-9b ("what is my ship status", "stage",
  "launch to an 80 km orbit", memory, science). HTTP endpoint, `/model`, and MCP server (25 tools) tested. get_delta_v verified on Kerbal X. Landing FIXED (2026-10-08 morning): own suicide-burn controller `kspchat/lander.py`; 3 clean landings (hop, from 80 km orbit, targeted KSC) - see 'Landing' below.
  Ollama backend written but untested (Ollama not installed). ChatGPT backend: default `api` mode needs OPENAI_API_KEY (paste in AICS Settings);
  `mcp` mode is OPT-IN (no key, but heavy token use per Luke 2026-10-08 - ChatGPT polls), see 'ChatGPT desktop chat (MCP, no key)'.
- Phase 4 mod: DONE (v1 verified in game: loads with 0 exceptions, test-hook message round-tripped, Luke chatted
  live, science notices show). v2 (fixed-size window, resize grip, saved position/size/backend, Ollama button,
  event polling) is installed and verified in game (see below).
  Note: the GameData DLL is locked while KSP runs; use tools\install_mod.ps1 with KSP closed.
- Phase 5 flight test: DONE. Direct call: 80.3 x 79.7 km orbit in ~3.5 min. Via the in-game window (test hook):
  model called list_craft -> launch_craft -> mechjeb_ascent; ascent ran to circularization. Bonus Mun flyby
  (transfer_to + course correction + warp to Mun SOI).
- Science watcher: DONE, working in game (posts "Science:" notices, auto-ran crew reports).
- Plane autoland (Task C): DONE - own controller `kspchat/plane.py` (MechJeb spaceplane autoland skipped per Luke).
  Aeris 3A runway landings: see 'Plane landing' below. Local model picks land_plane for "land the plane".
- Bridge auto-start from the mod (`KSPChatMod/BridgeLauncher.cs`, `PluginData/bridge.cfg`), landing distance/ETA
  (`get_landing_eta`, live line in the chat window), landing spots + Landing menu (V/H, saved spots, mark position,
  lat/lon, KSP target / vessel / flag / waypoint as "radio beacon"), `dock_with` (MechJeb rendezvous + docking,
  EXPERIMENTAL, untested): code done; see 'Landing menu / ETA / docking' below for what was tested.

## File map
| Path | What |
|---|---|
| `run_bridge.py` | CLI entry: `serve` (HTTP :8765), `mcp` (stdio MCP), `chat "msg"`, `repl`, `tools` |
| `start_bridge.cmd` | Double-click launcher for `serve` |
| `kspchat/config.py` | All settings (ports, LM Studio model/context, OpenAI model, caps); env-var overridable |
| `kspchat/ksp_actions.py` | kRPC/MechJeb actions = the tool set; `TOOLS`, `tool_schemas()`, `call_tool()` |
| `kspchat/chat.py` | Chat loop, system prompt, `/model` command handling |
| `kspchat/backends.py` | Backends (local=LM Studio, ollama, chatgpt, grokbot stub): model discovery, loaded-model detection (`/api/v0/models`), `lms` load/unload |
| `kspchat/science.py` | Science watcher thread (own kRPC connection) + event queue for `GET /events`; mode in `bridge_settings.json` |
| `kspchat/memory.py` | `playstyle_notes.md` memory (dedupe, max 100) + rotating `logs/sessions-*.jsonl` |
| `kspchat/http_server.py` | `POST /chat`, `POST /reset`, `GET /health`, `GET /events`, `GET /landing`, `GET /spots`, `POST /tool` (menu), `POST /setting`, `GET /api_keys` + `POST /api_key` (in-game keys), `POST /shutdown` |
| `kspchat/mcp_server.py` | stdio MCP server (python `mcp` 2.3, `MCPServer`) exposing the same tools + `game_chat_pending/wait/reply` |
| `kspchat/mcp_chat.py` | In-game <-> ChatGPT desktop reverse channel: queue/pending/reply/drain on `chatgpt_chat.json` (lock file, two processes) |
| `tests/test_mcp_chat.py` | Offline round trip of that channel (temp file) + checks the 3 MCP chat tools are listed |
| `playstyle_notes.md` | Luke's preferences, injected into the system prompt (git-ignored; created from `playstyle_notes.example.md`) |
| `kspchat/keys.py` | In-game API key entry: writes keys / Custom URL+model to the git-ignored `.env` and applies them live (no restart); masked `••••last4` status. Test: `tests/test_keys.py` |
| `kspchat/settings.py` | get/put for `bridge_settings.json` |
| `bridge_settings.json` | Runtime settings shared by bridge/MCP (`science_mode`, `ai_name`). Git-ignored. |
| `KSPChatMod/` | C# KSP plugin (`ChatWindow.cs`, `KSPChatMod.csproj`) -> `GameData\KSPChatBridge\Plugins\KSPChatBridge.dll` |
| `tests/test_mcp.py` | Starts the MCP server over stdio, lists tools, calls get_status |
| `kspchat/plane.py` | Plane autoland (background thread): to_entry -> (stall_test once per craft) -> final -> flare -> rollout; `STRIPS` runways; `STATUS`, `TRACE` |
| `kspchat/spots.py` | Landing spots (built-in + `bridge_settings.json` `landing_spots`), target resolution (spot / KSP target / vessel / flag / waypoint), `landing_eta()` |
| `kspchat/docking.py` | Rendezvous + dock via MechJeb (port size match, nearest free port, dv/RCS check) - experimental |
| `KSPChatMod/BridgeLauncher.cs` | Starts `python run_bridge.py serve` hidden at KSP start if /health fails; POST /shutdown on quit |
| `tests/plane_test.py` | Plane harness: `"Aeris 3A" <hdg> <km> <alt> [noreset] [takeover] [askmodel] [probe]`; writes `logs/plane_trace_*.csv` |
| `kspchat/lander.py` | Own landing controller (background thread, own kRPC connection): deorbit, reentry burn, suicide burn, legs/chutes; `STATUS`, `TRACE` |
| `tests/landing_test.py` | Landing test harness: `hop "Swivel Hopper" 3000`, `orbit "Kerbal X Lander" 80`, `ksc "Kerbal X Lander" 80`; writes `logs/landing_trace_*.csv` |
| `tests/model_eval.py` | LM Studio model comparison (fixed 8-prompt script, auto-scored) -> `docs/model_eval_raw.jsonl` |
| `docs/model_comparison.md` | Results table + recommended default / lightweight model |
| `tools/summarize_eval.py` | Turns `docs/model_eval_raw.jsonl` into the markdown table |
| `tools/ksp_auto.ps1` | Copy of the KSP launch/load/quit automation (original: `%TEMP%\kspauto\ksp_auto.ps1`) |
| `tools/restart_bridge.ps1` | Graceful bridge restart (waits for `/health` busy == 0) |
| `tools/install_mod.ps1` | Build + install the mod DLL (KSP must be closed) |
| `tools/clean_test_vessels.py` | Remove ships/debris from a save (KSP closed), free crew, keep asteroids; dry run by default |
| `tools/krpc_ping.py` | Prints the current KSP scene via kRPC |
| `logs/` | bridge.log (1 MB x 3), sessions-*.jsonl (2 MB x 10). Not source. |

## How to build / run
- Python deps: `python -m pip install --user -r requirements.txt` (krpc 0.6.0, protobuf, mcp 2.3.0 installed).
- Bridge: `python run_bridge.py serve` (or `start_bridge.cmd`). Health: `curl http://127.0.0.1:8765/health`.
- One-shot test: `python run_bridge.py chat "what is my ship status" --model local`.
- Mod: `cd KSPChatMod; dotnet build -c Release` (dotnet SDK 8.0.425 on PATH; NuGet package
  `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3 supplies net472 refs; references come from
  `KSP_x64_Data\Managed`). Release build auto-copies the DLL into GameData.
- **The DLL is locked while KSP runs.** To build without installing:
  `dotnet build -c Release -p:KSPDir=%TEMP%\kspauto\stage -p:Managed="%KSPDir%\KSP_x64_Data\Managed"`
  then copy `%TEMP%\kspauto\stage\GameData\KSPChatBridge\Plugins\KSPChatBridge.dll` into GameData after quitting KSP.
- In game: Alt+K or the stock toolbar speech-bubble button (Flight, Map, Space Center). Backend = dropdown in
  AICS -> Settings (no toolbar row on the chat window any more) or `/ai <id>` typed in the chat (handled by the mod).
  `/model`, `/model <name>`, `/model default` in chat.
  Window: fixed size (GUI.Window - GUILayout.Window auto-grew to full screen width in testing), `//` grip bottom-right resizes (min 300x200); position/size/backend saved to
  `GameData\KSPChatBridge\PluginData\window.txt`.
- Science watcher: runs inside `serve`; modes auto / remind (default) / off via chat ("science auto on/off")
  or tool `set_science_watcher`; notices appear in the in-game window prefixed "Science:".
- Test hook without typing: write text to
  `GameData\KSPChatBridge\PluginData\test_message.txt`; the mod sends it within ~2 s, deletes the file,
  and logs `[KSPChatBridge] reply: ...` to `KSP.log`.
- ChatGPT app: add the `[mcp_servers.ksp_bridge]` block from README.md to `%USERPROFILE%\.codex\config.toml`
  (NOT done automatically - outside allowed folders), fully restart the app, new thread, `/mcp`.

## ChatGPT desktop chat (MCP, no key) - added 2026-10-08 afternoon
- Why: MCP is inverted (ChatGPT = client), so the game can't call ChatGPT without the API. Reverse channel instead.
- Default changed 2026-10-08 ~15:30: `chatgpt_mode` defaults to `api`; MCP is opt-in only (Luke: polling burns too many
  tokens). Settings label: "ChatGPT desktop (MCP) - heavy token use". Never make MCP a default backend.
- Backend `chatgpt` + `chatgpt_mode` = `mcp` (opt-in; `bridge_settings.json`, `/chatgpt mcp|api`, AICS Settings
  "ChatGPT via", `POST /setting`): `Session.send` calls `mcp_chat.queue()` and returns a status line spoken by
  "Bridge" (X-AI-Name) instead of resolving a model - no OPENAI_API_KEY error any more.
- ChatGPT desktop (thread with `ksp_bridge`): "check KSP chat" -> `game_chat_pending` -> KSP tools -> `game_chat_reply`;
  "keep answering KSP chat" -> loop `game_chat_wait` (<= 55 s per call; Codex tool timeout is 60 s).
- Replies go to the outbox in `chatgpt_chat.json`; the HTTP bridge drains it on every `GET /events` into
  `science.post_event("@ChatGPT: ...")`. New mod (`&chat=1`) shows `ChatGPT: ...` and a "waiting for ChatGPT desktop"
  line until it arrives; the old DLL shows `Bridge: ChatGPT: ...` (works without a rebuild).
- AICS Settings has the chat backend picker (now a dropdown; see 'AI backends' below).
- Deploy: restart the HTTP bridge (`tools\restart_bridge.ps1`) and start a NEW ChatGPT thread (or restart the app) so
  the MCP server reloads (40 tools). Mod changes (waiting line, Settings toggles) need `tools\install_mod.ps1` with KSP closed.

## KSP automation script (`tools/ksp_auto.ps1`, original in `%TEMP%\kspauto\ksp_auto.ps1`)
Run: `powershell -ExecutionPolicy Bypass -File "<path>\ksp_auto.ps1" -Action <Start|Quit|Status|Click|Shot> [-Save "Grok Test"] [-X n -Y n -Name s]`
- Writes to `%TEMP%\kspauto` (summary.log capped at 200 KB -> last 300 lines; jpgs deleted at each Start/Quit/Status).
  Aborts with exit 2 if C: has < 15 GB free.
- **Start**: launches `KSP_x64.exe -single-instance -popupwindow -screen-fullscreen 0` (window 1920x1080
  borderless; client origin ~960,540 on the 4K desktop; script is DPI-aware and converts client->screen
  coords itself). Waits (<=15 min) for `Scene Change : From X to MAINMENU` in `KSP.log`, sleeps 8 s, then
  clicks (client coords): Start Game **(622,514)** -> Resume Saved **(772,378)** -> search box **(960,188)**,
  types the save name with SendKeys -> first result **(952,266)** -> Load **(1196,926)**. Waits (<=240 s)
  for `... to SPACECENTER`, sleeps 15 s, then 4 checks for the Kopernicus terrain popup: pixels
  (740,580) and (1180,580) ~ RGB(72,75,84) and (740,632),(1180,632) ~ RGB(52,56,65), tolerance +-20;
  if matched clicks OK at **(960,604)**. Saves `loaded.jpg` (half scale).
  Exit codes: 0 ok, 2 low disk, 3 KSP already running, 4 no main menu (fail_menu.jpg), 5 save didn't load (fail_load.jpg).
  Typical: ~2 min to main menu + ~6 s load + ~45 s settle = ~3 min total (measured 179 s tonight).
- **Quit**: `CloseMainWindow()`, waits 90 s for exit, else kills. Logs "clean exit".
- **Status**: logs running/not running + last scene line. **Click** `-X -Y -Name`: click client coords then
  save `<Name>.jpg`. **Shot** `-Name`: screenshot only.
- Full cycle: `-Action Start -Save "Grok Test"` -> work via kRPC (`python tools\krpc_ping.py` prints scene)
  -> `-Action Quit`. Run Start with a long timeout (>= 10 min) or in the background.
- kRPC auto-start: `GameData\kRPC\PluginData\settings.cfg` has `autoStartServers = True`,
  `autoAcceptConnections = True` (127.0.0.1, RPC 50000, stream 50001).
- From Space Center, kRPC can launch craft: `sc.launch_vessel('VAB','Kerbal X','LaunchPad', crew=[], recover=True)`
  (signature is dir, name, site, crew, recover, flag). Craft must be in `saves\Grok Test\Ships\VAB\`
  (Kerbal X and Kerbal 2 were copied there from stock `Ships\VAB`). `sc.revert_to_launch()`, `sc.quicksave()` work.

## Known bugs / gotchas
- kRPC `space_center.warp_to()` hung (stuck at 50x past the target) - replaced with our own rails-warp loop `_warp_until()`.
- MechJeb node executor does NOT autostage on its own; we enable `mj.staging_controller` before node burns.
- MechJeb transfer adds a 2nd (capture) node; `transfer_to` deletes it and executes only the departure burn.
  First transfer test arrived on an impact course (Mun Pe -197 km); `course_correction(periapsis_km)` fixes that.
- Correct kRPC attribute is `course_correct_final_pe_a` (setting a wrong attribute name on a kRPC object silently does nothing!).
- Stock craft names come through as `#autoLOC_...`; `launch_craft` renames the vessel to the craft name.
- Hyperbolic orbits have infinite apoapsis -> `get_status` uses `_r()` (None instead of inf).
- Qwen 3.5 9B sometimes returns empty content after tool calls; chat.py then asks once more without tools.
- SAS modes other than stability assist are refused on the pad / by low-level probe cores; tool returns a friendly message.
- RealChute (incl. RealChuteForStock) chutes appear in kRPC `parts.parachutes`; `deploy_parachutes` refuses on
  the ground or while climbing unless `force=true`.
- Running KSP + the 9B model pushed Windows' pagefile to ~43 GB (system-managed; C: free dropped 273 -> 240 GB).
- `KSP.log` is flushed lazily; mod messages (`[KSPChatBridge] ...`) show up immediately in
  `%USERPROFILE%\AppData\LocalLow\Squad\Kerbal Space Program\Player.log`.
- Ollama is not installed on this PC; the backend is implemented but untested against a live Ollama.
- kRPC can't terminate orbiting vessels (`vessel.recover()` only works landed/splashed/on the pad). Use
  `python tools\clean_test_vessels.py "<save dir>" [--apply]` with KSP closed (backup + frees crew; done for
  Grok Test at 02:38). Formerly: leftover
  test vessels in Grok Test (old Kerbal X ships + debris) still need removing: with KSP closed, delete their
  VESSEL nodes from `saves\Grok Test\persistent.sfs` (back it up first) and set their crew back to Available,
  or terminate them in the Tracking Station.
- Before land_at_ksc existed, Qwen flailed on "vertical landing" (called course_correction, circularize,
  transfer_to, abort ... until the 8-step cap). Prompt now forbids unrequested maneuvers and requires
  confirmation for orbit-changing burns. land_at / land_at_ksc flight-tested OK (see Landing).
- Delta-v (`_stage_dvs`): simulates staging per phase k (parts with decouple_stage < k aboard, engines with
  stage >= k fire, fuel in decouple_stage == k-1 burned). kRPC part.mass and Resources.density are both kg.
  Kerbal X on the pad: 302/351/420/3031/2537 m/s = 6641 total (asparagus crossfeed makes it approximate).
  Landing need = speed*a/(a-g) (orbit: 100 + 250*a/(a-g)).
- Landing (FIXED). Root causes of the 140 m/s crashes: (1) Kerbal X's last stage and the stock landers use
  vacuum engines (Poodle / Terrier, sea-level TWR ~0.5 on Kerbin) - a powered landing on Kerbin was physically
  impossible; (2) MechJeb's landing autopilot driven through kRPC from a low hop never braked in time.
  Now `land_here`/`land_at`/`land_at_ksc` run `kspchat/lander.py`: velocity-profile suicide burn
  (v_des = -(touchdown + sqrt(2*0.6*(a_max-g)*h)), feed-forward a_req = v^2/2h + feedback KP=2, 0.2 s look-ahead),
  surface-retrograde hold, vertical hold below 40 m, legs < 4 km, chutes < 8 km, deorbit to Pe 30 km,
  reentry burn above 1100 m/s between 15 km and 45 km (keeps a 700 m/s reserve), parts-lost check.
  `land_at*` = hybrid: MechJeb guides/deorbits to the target down to 5 km, then our controller lands.
  Test crafts in Grok Test VAB: `Swivel Hopper` (Super-Heavy Lander with 4 Swivels, legs, no chutes) and
  `Kerbal X Lander` (Kerbal X with the Poodle swapped for a Swivel; reaches orbit, 3 legs + chute).
  Tests (logs/landing_trace_*.csv): #1 hop crash (no trace; harness bugs), #2 hop 42 m/s crash (burn started
  at 55 m: feedback-only controller lagged), #3 hop 1.6 m/s OK, #4 orbit 1.4 m/s but 2 batteries burned up on
  reentry, #5 orbit 1.4 m/s OK 0 parts lost, #6 targeted KSC 1.4 m/s OK, 3.6 km west of the pad.
  Note: from orbit the chute does most of the braking (~10 m/s), the engine does the last few hundred metres.
- Plane landing (`kspchat/plane.py`, tool `land_plane`; `land_at_spot` for other runways). Settings (Aeris 3A):
  stall measured once per craft in a slow-flight test (AoA 15 deg) = 30.5 m/s, cached in `bridge_settings.json`
  `stall_speeds`. Speed schedule: to_entry 110 m/s (= max(vapp+30, 110)) until 6 km from the entry point, then
  ~75 m/s; final starts 11 km from the aim point (aim = 350 m past the threshold) at ~640 m; final speed vapp+12
  (~52 m/s) beyond 5 km, linear down to vapp = 1.3 x stall (39.6) at 2.5 km, then vtd = 1.15 x stall (35.1) below
  60 m wheel height (~0.8 km before the threshold); flare at 20 m radar height (sink -2.8 -> -0.7 m/s), throttle
  idle except it holds >= stall + 1.5 m/s; wings levelled (bank <= 2 deg, 0 below 5 m), rudder holds heading;
  touchdown on the mains nose-up, pulsed then full brakes once the nose wheel is down.
  Lateral: turn-rate command -> coordinated bank (speed-scaled), heading-rate damping, bank slew 8 deg/s, max 25 deg;
  on an established final (|xt| < 300 m, heading err < 15 deg) gain 0.12/s, 0.5 deg deadband, bank <= 8 deg.
  Pitch: vertical-speed -> pitch cascade with dynamic-pressure gain scheduling ((60/V)^2, 1..2.5) and low-passed
  rate damping. Entry: if the plane reaches the entry point pointing away (e.g. flying outbound along the
  centerline) it first flies 5 km further out (teardrop). The east pattern over the ocean is the test harness
  (outbound 12 km east, then a runway-27 final from the east) - intentional.
  Fixed bugs on the way: water crashes (wrong turn-direction/heading math early on), porpoising (no flare),
  side-to-side wobble on final (fixed bank-per-degree gain over-controlled at 40 m/s; raw derivative noise
  made surfaces chatter), firm 4.3-4.9 m/s touchdowns (elevator ran out of authority in the flare at 30 m/s
  and speed bled below stall at idle). No control surface is ever deployed on planes without dedicated flaps.
- Model/prompt failures seen live (06:08-06:15, qwen3.5-9b, before the fixes): (1) "we are landing at ksc on the
  runway" in the Aeris -> called land_at_ksc (rocket lander) -> its thread engaged the kRPC autopilot and fought
  the plane autoland; (2) TRAP TEST FAILED: "orbit at 100k, then land" in the jet-only Aeris -> called
  mechjeb_ascent; MechJeb pitched the plane up into a near flat spin. Fixes: `_orbit_capability_check` (jets only
  -> refuse orbit), `_plane_vertical_check` (planes can't land_here/land_at), cross-process guard (one autopilot at
  a time), prompt rules, and an upset/stall/spin recovery in plane.py (wings level, nose down, full power, then
  re-enter the pattern). The plane autoland recovered from the ~4 km off-course start after (1) on its own.
- Crash 06:25 (Jeb lost, counted toward the cap): NOT the control law - I killed the running harness to swap in new
  controller code; the plane flew on with frozen inputs (20 deg bank), and the replacement land_plane was refused
  by the guard because the dead process's heartbeat was still < 10 s old. Fixes: guard ignores claims whose owner
  pid is dead (OpenProcess check); never kill a controller mid-flight - use abort (guard stop request) instead.
- Plane sink profile (Luke): final above 15 m at ~-4.5 m/s (glide path = atan(4.5 / vapp), 3-7 deg, ~6.5 deg for
  the Aeris), 15 -> 5 m smooth to -2.0, catch 5 -> 2 m with a power burst (0.3-0.55) + gentle nose-up to -1.0,
  touchdown -0.5..-1.0 (throttle back toward idle below 2 m). Results report sink at 15/5/2 m and touchdown.
  Gentle inputs: control outputs slew-limited (pitch 0.6/s, roll 0.8/s, yaw 0.8/s full-scale), pitch attitude
  command rate-limited 2.5 deg/s and capped -8..12 deg, bank max 20 deg (8 on final), load-factor cap 1.4 g.
- `launch_craft` over a crewed vessel on the pad: kRPC launched the new craft uncrewed behind a modal
  "No Control" dialog and the call hung. Fixed: `_clear_launch_site()` goes to the Space Center, recovers what
  sits on the pad, waits 10 s, then launches.
- This kRPC build's AutoPilot has no engage()/disengage(); set `auto_pilot.engaged = True/False`.
- RealChute: `arm()` did nothing in testing and the chute opened only near the ground; `deploy_parachutes` now
  calls `deploy()` (RealChute activates and opens when safe). Verify on the next descent.
- When the 8-step tool limit is hit, the model is asked (without tools) to explain what it tried and what is missing.
- AI name: `set_ai_name` -> `bridge_settings.json` -> system prompt + `X-AI-Name` header -> mod shows "<name>:"; title shows backend / model (X-AI-Model header)
  (needs the v2+ DLL; the v1 DLL still shows "AI (...)").
- Verified 02:50: v2 window is fixed-size with Send/Clear visible, title shows "LM Studio / qwen/qwen3.5-9b", `/model` lists models. window.txt reset to 520x460 after the old GUILayout build had saved a 1200-wide rect.
- On first poll after a KSP restart the mod replays the bridge's recent science notices (since=0). Cosmetic.
- Restarting the bridge kills an in-flight chat; use tools/restart_bridge.ps1.
- Only one chat is processed at a time (lock); warp/ascent tools block or return immediately as documented in docstrings.

- Model comparison (docs/model_comparison.md): qwen/qwen3.5-9b and google/gemma-4-e4b both 8/8 on the
  fixed script; qwen3-4b-2507 6/8 (invents numbers when it skips tools); qwen3-vl-4b fine-tune 5/8 (claimed an
  SAS change it never made); qwen2.5-vl-3b and qwen2.5-coder-7b can't tool-call (coder writes JSON as text).
  Recommended: default qwen3.5-9b, lightweight gemma-4-e4b. KSP + desktop already use ~7.4 GB of the 10 GB GPU.

## Plane flight rules (Luke, 2026-10-08 06:45-07:35) - all in `kspchat/plane.py`
- HARD RULE: never trade altitude for speed. Speed comes only from the THROTTLE; no speed term feeds pitch
  (audited: the only speed-dependent pitch terms left are takeoff rotation at Vr and the final glide path).
- Takeoff: the OLD (b94c425) takeoff block (full power on the roll, rotate at 1.25 x stall to 9 deg, heading hold
  with rudder + nose wheel) plus only: 220 m/s cap, never chop once airborne (5 % steps, ease off from 130 m/s),
  gear up after vs > 3 m/s and > 30 m AGL for 2 s, hand-off at >= 100 m AGL climbing with gear up (or 120 s).
- Speed limits as EQUIVALENT airspeed (sqrt(2q/rho0)): 200 m/s target / 220 m/s HARD cap down low; the true
  airspeed limit rises as the air thins. Supersonic dash target = Mach 1.5 from kRPC `speed_of_sound`
  (fly_to cruise_speed >= 300, above 8 km), accelerating already in the climb above 8 km.
- Throttle (everywhere except the takeoff roll and the flare cushion): 5 % steps; >= 4 s between steps (3 s on
  final, 2 s near the cap / recovery) and only once actual thrust has settled; change only when the speed is stable:
  accelerating -> no more power, decelerating -> no further cut (over-speed: step down while not yet slowing, then
  HOLD and let it bleed). Safety exceptions (1 s steps): speed < 1.3 x stall + 10, or EAS > 220. Never 0 in flight;
  >= 30 % in climb/cruise (planned descent / final may go to 5 %).
- Pitch: climbs hold a fixed attitude 10 deg (< 5.5 km), 7.5 (< 8 km), 5 (above), clamped to 5-15 / 5-10; level
  flight tracks altitude. Nose-down only on final/flare or a planned descent (max -5 deg); stalling -> power +
  gentle pull-up; any other nose-down is blocked (logged "NOSE RULE"). 300 m AGL climb floor en route.
- Bank caps: 20 deg at low speed, 10 deg above 250 m/s. Upset guard: bank > 60 -> cut pitch/yaw inputs, level the
  wings. Gains scale with dynamic pressure (gsched; pitch floor 0.25).
- Dash routes add an outbound leg on the extended centerline, searched over the real geometry so climb + cruise +
  planned descent fit. No slow-flight stall test on cruise/dash flights; cached stall speeds are keyed by name,
  de-localized name (stock craft spawn as "#autoLOC_501174") and a parts/mass signature.
- Circling-turn hold: `settings.put("plane_orbit_until", time.time() + secs)` -> steady bank at the cap, altitude hold.
- Splashdown outside final ends the flight. fly_to/land_plane range check 150 -> 600 km.
- Test tooling: `tests/flight_watchdog.py <pid> <log>` (own process: logs pitch/thr/speed/vs every second; takes
  over with the kRPC autopilot if the controller heartbeat goes stale > 5 s, or nose < -10 deg sinking < -25 m/s
  outside final); `tests/circuit_test.py` writes its trace every 5 s and dumps thread stacks to
  `logs/circuit_stacks.log` if it stalls 8 s. Start long-running things with `tools/launch_detached.ps1`
  (WMI + pythonw `tools/spawn_hidden.py`: windowless and detached); `tools/restart_bridge.ps1` uses it too.
- Crash root causes (Aeris 3A, KSC -> Island Airfield dash, 06:47-07:30; all reverted by Luke):
  1) full-power takeoff to 214 m/s with sea-level gains -> barrel rolls -> water (fixed: gain scheduling, upset guard);
  2) q-scaled gains on the ground -> never rotated -> off the runway end (fixed: old takeoff);
  3) nose-up trim faded after liftoff -> sank into the grass (fixed: old takeoff);
  4) 07:04, 07:11, 07:14: the controller PROCESS was killed when a foreground Shell call on the PC was interrupted
     (the persistent PowerShell session kills its child processes) -> frozen inputs -> dive. The heartbeat in
     bridge_settings.json stopped at the same second as the trace. Fixed: detached, windowless launch;
  5) the cached stall speed wasn't found ("#autoLOC_501174") -> slow-flight stall test right after takeoff (fixed);
  6) 07:30: a hot-swapped replacement controller was REFUSED by the old 150 km range check (plane 165 km out) ->
     nothing flying -> frozen inputs (98 % throttle) -> dive at 558 m/s. Lesson: never stop the running controller
     before the replacement is confirmed flying; avoid hot-swaps.
  Also: mid-flight hot-swaps re-planned the dash leg from wherever the plane was (unplanned descents).
  None of the newest rules has completed a full flight yet. Last stable state: steady 10 km climb at ~300 m/s,
  throttle 98 % (at 7.5 deg the Aeris couldn't accelerate further; 5 deg above 8 km is the latest change).

## Next steps / ideas
000. NEXT FLIGHT: one clean KSC -> Island Airfield dash with the rules above, launched with
    `tools/launch_detached.ps1` + `tests/flight_watchdog.py`, NO hot-swaps; then a touch-and-go circuit
    (`tests/circuit_test.py 2`, untested). Future ideas: go-arounds; landing-spot labels / map markers in the
    Landing menu; show the active autopilot and rule events (NOSE RULE, throttle holds) in the chat window.
00. UNTESTED as of 06:45: plane takeoff phase, `fly_to` (cruise / supersonic dash + descent planning), the
    Island Airfield strip (measured from the spawn point only; 500 m, lands southbound), the new launch_craft
    crew/site handling, the mod DLL (BridgeLauncher auto-start, Landing menu, live ETA line) - the DLL is built
    (compile-checked) but not installed: install with KSP closed (`tools\install_mod.ps1`) and verify.
0. Plane autoland future: go-arounds (abort the approach if unstable / off the centerline / too high or fast on
   short final, climb out on runway heading, re-enter the pattern); smarter teardrop (offset the turn so it
   rolls out on the centerline instead of a 50 deg intercept); test Island/Desert Airfield landings;
   map-click target picking in the Landing menu; flight-test dock_with with a real station.
1. Landing works; next: try land_here on the Mun (no chutes, low g) and tune MARGIN/KP for low-TWR craft.
2. Re-test gemma-4-e4b after the tool-description tweaks (inclination / force).
2b. Re-run tools\clean_test_vessels.py on Grok Test whenever test vessels pile up (done once at 02:38).
3. Grok Bot backend: implement in `kspchat/backends.py` (currently a STUBS entry, "soon"). Claude likewise.
4. ChatGPT API / Gemini / Groq / OpenRouter / HF / Custom: put the key in the repo `.env` (`OPENAI_API_KEY=`,
   `GEMINI_API_KEY=`, `GROQ_API_KEY=`, `OPENROUTER_API_KEY=`, `HF_TOKEN=`, `CUSTOM_AI_*=`)
   and restart the bridge. Optionally install Ollama to use that backend.
5. Connect the ChatGPT desktop app: add the `[mcp_servers.ksp_bridge]` block from README.md to `%USERPROFILE%\.codex\config.toml`.
6. Ideas: landing autopilot tool (`mj.landing_autopilot`), Mun/Minmus return (`operation_moon_return`), rendezvous/docking,
   a bridge auto-start from the mod, showing the active model name in the window title.

## AI backends (2026-10-08 ~15:10)
- `kspchat/backends.py`: `BACKENDS = local, ollama, chatgpt, gemini, custom, claude, grokbot`. Keyed HTTP APIs are one
  table (`KEYED`: url, key env vars, default model, /models filter) - adding another OpenAI-compatible API = one entry
  + config vars + mod label. `claude` / `grokbot` are `STUBS` (clean "soon" message, greyed out in the dropdown).
- Gemini = Google's OpenAI-compatible endpoint (`GEMINI_BASE_URL`, default `.../v1beta/openai`), key `GEMINI_API_KEY`
  or `GOOGLE_API_KEY`, model `GEMINI_MODEL` (default `gemini-flash-latest`). `/models` ids come back as `models/...`;
  `normalize_model()` strips that. Tool-call messages are passed back verbatim, so Gemini 3 thought signatures survive.
  Not yet tested against the live API (no key on this PC).
- Free clouds (all KEYED, skipped cleanly without a key): `groq` (GROQ_API_KEY, gpt-oss-120b; free 8K tokens/min is
  less than one tool round of ~10K tokens -> `chat._post` honours Retry-After on 429 up to CLOUD_RETRY_MAX_WAIT=30 s,
  2 retries), `openrouter` (OPENROUTER_API_KEY, `openrouter/free`; 50 req/day free), `huggingface` (HF_TOKEN; free
  credits ~none). 429 -> "rate limit hit" reply, 401/403 -> "rejected the API key". Limits table in README.
  Token diet idea if free tiers are too tight: send a reduced tool list to cloud backends.
- `custom` = any OpenAI-compatible API (`CUSTOM_AI_URL`, `CUSTOM_AI_MODEL`, optional `CUSTOM_AI_KEY`).
- `config._load_dotenv()` reads the git-ignored repo `.env` (real env vars win) - avoids the stale-env problem of keys
  set with setx while Steam/KSP are already running.
- `/health` -> `backends_ready` (keyed backends with a key, e.g. "gemini,groq"; names only). The mod's dropdown shows
  (configured)/(not configured).
- Mod: `ChatWindow.ModelIds` order changed (gemini/groq/openrouter/huggingface/custom/claude inserted); `window.txt` now stores the backend **id**,
  old index-based files are mapped via `LegacyIds`. ChatGPT mcp/api toggle shows under the dropdown when ChatGPT is picked.
- Test: `python tests/test_backends.py` (offline: aliases, missing-key messages, mocked Gemini tool round trip, .env).
- VS Code AIs are clients, not local APIs (see README "Any VS Code AI?"). Copilot can only join as an MCP client
  of `ksp_bridge` (`.vscode/mcp.json`); in-game replies via game_chat_* would currently be labelled "ChatGPT".
- Bridge port is bound exclusively on Windows (`http_server._Server.allow_reuse_address = False` on nt) and before the
  science watcher starts: on 2026-10-08 the mod's autostart (BridgeLauncher `Healthy(1500)`; `/health` probes kRPC and
  was slow while KSP loaded) launched a 2nd bridge next to a manually restarted one and BOTH listened on :8765
  (SO_REUSEADDR). Now a duplicate prints "port 8765 already in use" and exits. Possible mod tweak later: longer
  health timeout, or a kRPC-free `/ping`.
- Next: native `claude` (Anthropic Messages API or its OpenAI-compat endpoint, needs ANTHROPIC_API_KEY), `grokbot`
  file queue, maybe rename the MCP reverse channel label to "desktop AI" so Copilot/Claude Code answers aren't called ChatGPT.

## AICS menu (2026-10-08)
Right-click the KSPChatBridge toolbar button (or Alt+J) opens the sticky, expandable **AICS** top menu (`KSPChatMod/AicsMenu.cs`); left-click / Alt+K stays the chat. Full menu design, wired/partial/stub status per panel and the queued flight fixes (radar alt + runway elevation, signed heading error, fly-heading, set commands, Island E-W runway 'Island 27', overspeed/throttle floor, bank, climb V/S via throttle, EAS vs TAS) are in `docs/AICS_MENU.md`. Menu buttons call whitelisted bridge tools (`http_server.MENU_TOOLS`, now also land_plane, run_science, set_science_watcher, get_status).


### Flight Plan runner + all AICS stubs wired (2026-10-08 evening)
- `kspchat/flightplan.py`: step grammar (docs/AICS_MENU.md section 4), lenient parser for LLM/markdown/Luke's sentences, templates (circle = takeoff -> climb -> cruise -> circle laps around the field -> land), `draft()` (one tool-less LLM call + rule fallback), and a background runner (`start/stop/status_line`). Endpoints: `GET /flightplan/template?kind=`, `GET /flightplan?format=text&since=R`, `POST /flightplan/{draft,check,fly,stop}`. Chat: `set_flight_plan` tool + auto-detect of plan-like replies push the plan into the panel. Abort also stops the runner. Test: `python tests/test_flightplan.py` (offline fake world flies the circle template + in-place circle).
- New tools (ksp_actions): change_apoapsis / change_periapsis / change_inclination / station_keep / capture_plan / taxi_to / set_flight_plan (LLM), plus menu-only match_target_plane / apsis_longitude / launch_to_target_plane / sync_orbit_altitude / landing_check / list_taxi_points. `land_plane` + `land_at_spot` take final_km / approach_heading (settings `plane_final_m`); `land_at_spot(chute_check)`.
- `kspchat/taxi.py`: straight-line ground taxi (KSC/Island points + saved spots), `GET /taxi`.
- C#: Approach (final/heading/chute check), Landing Guidance map pick (green 4-prong cursor), Orbit Plan / Capture / Orbital AP MechJeb buttons, Crew transfer + EVA board (local), Taxi panel; chat window + Landing window got an x close button. No font/color changes. Remaining hard limits: end of docs/AICS_MENU.md.

### AICS layout (MechJeb style) + holds
A sticky collapsed tab '? AICS ?' sits top-center (flight + KSC). Click/right-click it (or right-click the toolbar button / Alt+J) to expand the two-column module menu; right-click the menu or the tab to collapse. Each module opens its own window (x to close). Opacity slider in the menu header and Settings; tab position, open modules, opacity saved in PluginData/aics.txt. Holds: `kspchat/hold.py` + `plane_hold` tool (live targets in bridge_settings.json 'plane_hold'); tested in-game 2026-10-08: takeoff on runway heading, level-off at 1500 m AGL (+-2 m), live retarget to 2000 m / hdg 180 / +15 V/S without restart. Known: a V/S climb at idle throttle bleeds speed (153 -> 93 m/s) since speed is ignored while chasing V/S (Luke's rule); hold takeoff climb reached 257 m/s at 650 m.

