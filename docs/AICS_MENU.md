# AICS — in-game menu design (KSPChatBridge)

AICS ("AI Control System") is the KSPChatBridge mod's main menu. It sits alongside the chat window.

| Input | Action |
|---|---|
| **Left-click** the toolbar button (or Alt+K) | Chat window (existing). Its **x** (top right) closes it, like the AICS panels; the Landing window has one too. The `//` grip in the upper-right corner resizes it up/right (min 300x200; the bottom-right grip still works); the input box is multi-line (word wrap, Shift+Enter = new line, Enter sends) and grows downward up to ~5 lines, then scrolls. Replies are labelled with the pilot's name. |
| **Right-click** the toolbar button (or Alt+J) | **AICS** top menu. It is *sticky*: it stays open until you right-click again or press its X. Clicking a menu item expands or collapses that item's panel underneath, and more than one panel can be open. (Popping a panel out into its own window is planned.) |

Style: dark, MechJeb-like (dark grey panels, light text, green accents, small fonts). On every panel, a **MechJeb when available** principle applies. If MechJeb2 + kRPC.MechJeb is installed, the panel drives MechJeb's own autopilot (ascent, landing, rendezvous, docking, node executor, attitude). The Python bridge fills the gaps MechJeb doesn't cover, or does badly, such as plane autoland, suicide burns, the science watcher and runway spots. The header shows `MJ` (green) or `no MJ` (grey).

Every panel button maps to either (a) a bridge `/tool` call (whitelisted in `http_server.MENU_TOOLS`), (b) the Flight Plan runner (`/flightplan/*`), (c) a local C# action (KSP API: power, crew transfer, EVA boarding, map pick), or (d) a chat prompt to the AI. As of 2026-10-08 every panel is wired; the remaining hard limits are listed at the end.

Status legend: **[W]** wired, **[P]** partial, **[S]** stub (UI only).

---

## 1. Aircraft Autopilot  [W]
Classic airliner autopilot holds. Each hold has its own checkbox and target field:
- **Altitude Hold**: target altitude, with an AGL/MSL toggle. AGL means above the *runway / ground below*, never sea level. *Fix queued: radar alt + runway elevation.*
- **Vertical Speed Hold**: m/s target, driven by THROTTLE in a climb (ignores airspeed while chasing V/S). Pitch stays in its band.
- **Heading Hold**: degrees. Shows the signed heading error (+ right / − left).
- **Roll Hold**: bank angle (capped 20° low speed / 10° above 250 m/s).
- **Speed Hold**: actual airspeed (TAS) target, throttle only. Never trades altitude for speed.
- **Takeoff throttle from the craft's specs** (`kspchat/takeoff.py`, used by both the plane autopilot and the hold/Flight Plan takeoff): before the roll the bridge reads mass + available/max thrust over kRPC and picks the start throttle from the thrust-to-weight ratio: TWR <= 0.4 -> 100 %, 0.6 -> 90 %, 0.8 -> 75 %, 1.0 -> 65 %, 1.2 -> 55 %, >= 1.5 -> 35 % (linear, 5 % steps, never 0). Then 5 % steps with spool waits (4 s after start, 3 s on the roll, 4 s airborne, 2 s urgent): more power on the roll only if accelerating < 1.5 m/s^2 below Vr; airborne it HOLDS while the speed is still changing, except under the stall margin (add power) or over / heading over the 220 EAS cap (5 s look-ahead; take power off, floor 10 %, else 30 %). Target 200 / cap 220 EAS. bridge.log: `takeoff TWR x (mass, thrust) -> start throttle y%` and every step.
- **Speed cap override authority** (`kspchat/speedcap.py`): a speed above the active cap (220 m/s EAS, e.g. `set speed 300` low down, a supersonic `fly_to` at a low altitude, a Flight Plan step) is not flown; the tool answers `needs_override: That's above the 220 m/s low-altitude cap (...). Grant override authority? (yes/no)`. A plain **yes** in that chat session raises the cap to the requested speed and applies the request; **no** keeps the cap. A Flight Plan step waits up to 2 min for the answer (no / no answer = flies the step at the cap). `/override speed on|off` (blanket, this bridge session; off restores 200/220). Not persisted: a bridge restart is back to the defaults.
- **Safe ceiling + override**: the autopilot estimates its own safe max altitude from the craft (air-breathing sea-level TWR via kRPC `max_thrust_at(1 atm)`, intakes, rocket engines): `8000 m x ln(TWR / 0.1)` clamped 3-18 km for jets, 20 km with rocket engines, 3 km with no engines or intakes (logged as `safe ceiling ...`). An altitude target above it (plane_hold, fly_to, Flight Plan climb/cruise) asks `That's above the estimated safe ceiling of X m. Grant override authority? (yes/no)` with the same yes/no flow; `/override altitude on|off`; `/override` = status.
- **Max speed estimate** (`kspchat/maxspeed.py`): a requested speed (plane_hold, fly_to cruise_speed, chat `set speed N` / `speed N`) above the craft's best (conservative) estimate is clamped and reported: `Best estimate max is ~326 m/s here; you asked 400. Flying 326.` Method: drag area from kRPC now (|drag| / dynamic pressure), drag ~ rho V^2 at the target altitude, thrust = jets' current available thrust x density ratio + rockets' available_thrust_at(target pressure), level flight T = D, x0.9 safety (no transonic drag-rise model). No estimate on the ground / below 40 m/s (nothing clamped). Separate from the 220 EAS cap: the clamped speed then goes through the override question if it's above the cap.
- **Pitch command** (`plane_pitch`; chat: `pitch up 10`, `pitchup 90`, `pitch down 5`, `nose up|down N`, N 0-90 deg, default 5): an ABSOLUTE attitude for the running holds (replaces the V/S loop and the 5-15 deg climb bands; it's the player's explicit command, no override question). Nose up: throttle up; near the stall margin (1.3 Vs + 10 m/s, or AoA > 15) it levels off at ~1 deg with full power (never cut), rebuilds +20 m/s, returns to the pitch, and repeats until the command changes or it reaches the target altitude (if one is set above) or the estimated safe ceiling; then it holds altitude. Nose down: ends at a target altitude below or the terrain floor (lead from V/S and the pull-out time); over the EAS cap it levels off until the speed is back. Above 60 deg the wings are held level. A pitch command drops the altitude target being held; `set altitude` / V/S ends the pitch hold. Each level-off / resume / end is in bridge.log (`hold: Pitch ...`) and posted as a chat note. Nothing engaged -> "Nothing is engaged".
- **Direct chat commands** (`chat.parse_direct`, handled before the AI so tiny local models work; each is also an AI tool): `throttle 60` / `throttle to 60%` / `throttle max` / `throttle idle` / `throttle auto` (`set_throttle`; numbers are percent; in atmospheric flight never 0, idle = 5 %; while the holds fly it is a *manual throttle override*: they stop managing the throttle, stall protection can still add power, until `throttle auto` or a new speed/altitude/V-S/pitch command; refused while the landing / fly-to autopilot runs), `gear|wheels up|down` (`set_gear`; not raised on the ground), `airbrake|brakes on|off` (`set_brakes`, Brakes group; says so if the craft has no airbrake parts), `deploy chutes` / `chutes` (`deploy_parachutes`; "No parachutes on this craft"), `eject` / `eject kerbal` (`eject_kerbal`: asks `Eject <name>? ... (yes/no)`; on yes the bridge queues `!cmd eject <unix time> <name>` on `/events` and the mod (polls `/events?cmd=1` every 3 s) EVAs that kerbal - else the first crew member of a command part - via KSP's `FlightEVA.fetch.spawnEVA` out of the part's hatch; commands older than 30 s are ignored (no replay after a KSP restart). With an old mod build (no `cmd=1` poll in the last 20 s) it explains that the mod needs the update instead of asking).
- **Captain's orders, batch 1** (`kspchat/orders.py`, parsed before the AI; the AI reaches them all through one tool, `captain_order(order)`, so small models aren't swamped): `turn left|right N` (from the current heading, default 90, max 175; holds only), `heading NNN` (holds only), `fly to KSC|Island|<spot>` (runways -> `fly_to`, which lands; KSC picks runway 09 when we're west of it, else 27; a non-runway spot/waypoint -> the holds fly its bearing once, no landing), `return to base` / `RTB` (= fly to KSC), `circle here [left|right]` (bank hold at the current altitude, 15 deg, max 20 / 15 above 250 m/s), `report` (alt, AGL, speed, V/S, heading, fuel %, KSC distance/bearing, autopilot), `fuel check` (fuel % + endurance/range at the current burn rate vs KSC with 30 % reserve), `how far to <place>` (distance, bearing, ETA at ground speed), `time to target` / `eta` (landing ETA or the KSP target vessel), `level off` / `wings level` (cancels pitch/turn/circle; holds the current MSL altitude and heading), `stage`, `SAS on|off`, `hold prograde|retrograde`, `RCS on|off`, `lights on|off`, `AG N` / `action group N [on|off]` (1-10). Anything longer ("fly to KSC and land at 3 km") goes to the AI.
- **Captain's orders, batch 2**: `engines on|off` (off = cut engines), `cut engines` (asks yes/no: autopilots off, throttle 0, all engines shut down), `switch engine mode` (multi-mode engines, e.g. RAPIER), `afterburner on|off` (Wet/Dry-mode engines), `flaps 1|2|up` (default flaps 1 = AG 5, flaps 2 = AG 5 + AG 6, up = both off; change in bridge_settings.json `"flaps_action_groups": {"1": 5, "2": 6}`), `trim nose up|down N` (% of the trim range, default 5; `reset trim`; needs a kRPC with pitch_trim), `land` / `land at KSC|Island` (planes: holds hand over to the autoland / fly_to; rockets: land_here / land_at_ksc), `go around` (drops the approach, climbs to 600 m above the field on the runway heading with the holds), `touch and go` (one, then full stop), `abort` (asks yes/no, then stops the bridge autopilots and fires the ABORT action group; `abort autopilot` = the old safe abort without the AG, no question), `crew report`. Yes/no for destructive orders uses the same per-session question as the speed override (`yes` / `confirm ...` / `no`).
- **Pilot voice**: with crew aboard, chat replies (direct orders and AI replies) come from the kerbal at the controls (Pilot trait first, else the first crew member; first name, read via kRPC and cached ~10 s), e.g. `Sidry: Gear down, Captain.` No crew / no vessel: the AI's name (or `Bridge` for bridge commands like /override).
- **Altitude orders** (`orders.parse_altitude` -> `set_altitude`, before the AI): `descend to N`, `drop to N`, `go down to N`, `climb to N`, `altitude N`, `set altitude N` (N in m, `5km` / `5k` = 5000; a bare number under 100 is left to the AI; MSL by default - the KSP altimeter - or add `agl`). Loose phrasing works when it's unambiguous: fillers like now / max / asap / please / KSP / Kerbin are ignored, so `NOW MAX DOWN TO KSP ALTITUDE 5km` = descend to 5000 m MSL, urgent. It sets the holds' altitude target (engages them if nothing flies the plane; refused while the landing / fly-to autopilot flies). Descents fly at up to 15 m/s V/S (30 m/s when urgent: max / now / asap), still inside the hold's -5 deg nose-down limit and overspeed level-off - never a dive for speed. Climbs above the estimated safe ceiling ask for override authority (yes/no). A target on the wrong side ('descend to' above us) is refused with a hint.
- **Aircraft guard**: when the active vessel is an aircraft (plane autopilot / holds on, or flying with intakes, lift > 30 % of the weight, or wings) the rocket powered-descent / suicide-burn tools (`land_here`, `land_at`, `land_at_ksc`) are hidden from the AI's tool list (cached 10 s) and refuse even with force, pointing to `set_altitude` / `plane_hold` / `land_plane`. Delta-v answers carry an explicit verdict so the AI never compares numbers itself: `get_delta_v` -> `enough_fuel: true|false` (false for aircraft, with a note), `landing_check`, deorbit / dock / fly-to fuel refusals and `fuel_check` say `enough_fuel: ...`, `capture_plan` gives one per option.
- **Typo tolerance**: command keywords of 6+ letters are fuzzily corrected before parsing (`Overide speed to 325`, `overrride`, `over ride`, `authorse all`, `throtle 60`, `altitdue 5000`, `circel here`); words that already contain a keyword (`pitchup`) are left alone; anything that still doesn't parse goes to the AI as typed.
- **Orders while the landing / fly-to autopilot flies** (`plane.LIVE`): `speed N` sets its en-route cruise speed (max-speed estimate clamps, cap/override question as usual); on final / flare it is flown too with a one-line caution (Luke's call). `climb to N` / `descend to N` en route set the cruise altitude (MSL; the planned descent to the runway stays); on final it says 'go around' first. `speed N` with nothing engaged (flying) engages the holds at the current altitude / heading with that speed.
- **Overrides always work** - holds, fly-to, autoland (every phase, incl. final / flare with a caution), Flight Plan, or nothing engaged (stored; applies to whatever engages next). `override speed N` also flies N when an autopilot is up. `override speed max` = no cap and full throttle (holds: manual 100 %, autoland/fly-to: full on the leg), the physical estimate is reported.
- **Airframe / crew watcher** (`kspchat/protect.py`, 1 s kRPC sampling while an override or autopilot is active): an overridden bank / pitch is flown as commanded, but above 6 g, a part at >= 90 % of its max temperature or parts breaking off it eases the overridden excess in 25 % steps (back up 5 %/s when clear) and says why. G-force blackout: crew aboard + vessel control lost (`Control.source` / `state` = none) + recent high G (fallback: > 6.5 g for 3 s) -> 'Sidry blacked out!', wings level and gentle pitch; when control returns 'Sidry is back - resuming the previous command' and the stored commands carry on.
- **Limit overrides in chat**: `override bank|pitch|speed|altitude N [*|***]`, `override [kind] off`, `authorise all` - same rules as the Flight Plan steps (section 4).
- **No-tool claims**: if the chat AI replies that it changed holds/settings but called no tool, the bridge re-prompts it once to call the tool; if it still claims it, the reply gets `(no action taken)`.
- **Engage / Disengage**: one autopilot owner at a time (cross-process guard).

Backend: `kspchat/hold.py` + the `plane_hold` tool. Targets live in `bridge_settings.json` ("plane_hold") and are re-read twice a second, so Engage / Update and the chat set commands ("set altitude 7200", "hold heading 270", "climb at 50 m/s") apply *live, without a restart*. AGL = above the nearest known runway within 60 km (else the ground under the plane). On the ground and lined up on a runway, it takes off on the runway heading first. `autopilot_status` reports targets, AGL/radar altitude and the signed heading error.

## 2. Approach & Autoland  [W]
- Runway picker (H spots: KSC 09/27, Island 27 (E-W, from Luke's position), Island Airfield (approx), saved strips).
- **Short final** (5 km) / **Long final** (11 km, default). [W] `final_km` on `land_plane` / `land_at_spot` (settings `plane_final_m`, read by `plane._fly`)
- Approach heading override, or auto from runway + approach direction. [W] `approach_heading` = the heading you land on; picks 09/27 (or the matching end of a saved strip)
- **V vs Plane** checkbox: V = vertical (rocket) landing at the spot, Plane = runway autoland. [W] via `land_at_spot(mode)`
- **Touch-and-go** count (0 = full stop, -1 = until told). [W] KSC via `land_plane(touch_and_go)`
- **Chute check**: before a V landing, warn if there are no parachutes / not enough dV for a powered landing. [W] `landing_check` (button) and `land_at_spot(chute_check=true)`, which refuses when neither is enough.
- **Land** [W], **Abort** [W].

## 3. Landing Guidance  [W]
- Coordinates entry (lat/lon, plus runway heading for H). [W] (existing Landing window)
- **Map pick**: in map view, a green **4-prong** cursor. MechJeb's is a yellow 3-prong, so the two are easy to tell apart. Click the body to set the target (Esc cancels). [W] local C# (ray from the map camera → body sphere → lat/lon). Then **Land at pick (V)** = `land_at(lat, lon)` (MJ landing autopilot + our suicide burn) or **Save spot** = `save_landing_spot(lat, lon)`.
- Saved spots list, Mark current position, delete. [W]
- **Land at Target**: KSP's current target (vessel / flag / waypoint, "radio location" beacons). [W] `land_at_spot("target")`

## 4. Flight Plan  [W]
The AI (or a template) writes a plan of one step per line into the text box; you edit it; **Fly** runs it step by step (`kspchat/flightplan.py`, a background runner on the bridge). Planes use the bridge controllers (`plane_hold` takeoff/climb/cruise/turns, `land_plane` / `land_at_spot` autoland); rocket steps use MechJeb (ascent, maneuver planner + node executor, landing autopilot).

Buttons:
- Templates: **Cruise + circle** (takeoff → climb → cruise → circle laps around the field → land), **Out & back**, **T&G circuit**, **Orbit (MJ)**.
- **Ask AI** field + **AI fill**: one tool-less call to the current AI backend (`POST /flightplan/draft`) with the step grammar; the reply is normalized to steps and written into the box. If the model rambles, a rule-based parser turns the request itself into steps (e.g. "take off, climb at 50 m/s and circle KSC 5 times, then land"), else a template.
- **From chat**: turns the last chat request/reply into steps. Asking in chat ("make a flight plan: ...") also lands in the box (`set_flight_plan` tool, or auto-detected plan-like replies); the panel polls `/flightplan?format=text`.
- **Check** (parse only), **Fly** (strict parse, then run), **Stop** (ends the plan; the current autopilot keeps flying - use Abort to drop everything). Status line shows the running step.

Step grammar (case-insensitive; `#` = comment):
| Plane | Rocket / orbit | Any craft |
|---|---|---|
| `takeoff` | `ascent 80 km inc 0` | `wait 30 s` |
| `climb 3000 m agl vs 50 [speed 200]` | `circularize` | `science [no transmit]` |
| `descend 1000 m agl vs 20` | `transfer Mun` | `stage` |
| `cruise [hdg 090] [alt 3000 m agl] [speed 180] for 2 min` (or `for 40 km`) | `warp to soi` / `warp to apoapsis` | |
| `circle 3 laps right bank 20 [around field\|KSC\|<spot>] [radius 5 km]` | `course correction 30 km` | |
| `fly to <spot> [alt 2000 m] [speed 150]` | `set ap 2863 km` / `set pe 32 km` (aerocapture) | |
| `land [Runway 09\|<spot>] [tg 2]` | `inclination 6` / `match plane` / `apsis longitude -74.6` | |
| `taxi to Runway 09 start [speed 8]` | `station keep [lon -74.6]` / `deorbit 30 km` / `land ksc\|here\|<spot>` / `chutes` | |

Circling: without "around", the plane banks in place and counts heading change; "around field/KSC/<spot>" flies a circle around that point (radius ≥ 1.6 × turn radius, so fast planes fly big circles: at 320 m/s it is ~50+ km).

**Limit overrides** (plan steps AND chat orders; `kspchat/speedcap.py`, tool `set_override`): `override bank N` (bank limit, 5-80 deg, replaces 20 / 15 above 250 m/s in the holds, circles, `circle here` and the autoland's en-route turns - never on final/flare), `override pitch N` (climb attitude of the holds, 5-90 deg; stall protection still levels off), `override speed N` (m/s EAS hard cap, 50-3000), `override altitude N [km]` (safe ceiling, 500-70000 m), `override off` (every default back, also ends `authorise all`) / `override bank|pitch|speed|altitude off`. They last for the rest of the bridge session (not persisted).
Authority markers: `override bank 50 *` or unmarked = **ask**: when the step runs (or the chat order arrives) Luke gets `Override the bank limit 50 deg for the rest of the session? (yes/no)` in chat; yes applies it, no / no answer in 2 min keeps the defaults and the plan goes on. `override bank 50 ***` = **pre-authorized**, applied directly. `authorise all` / `authorize all` (plan line or chat) = every override (and speed-cap / safe-ceiling question) applies without asking until `override off`. Check shows the markers (`override bank 50 *`, `override speed 400 ***`, `authorise all`). AI fill writes `*` by default; an AI-written `***` is downgraded to `*` and `authorise all` dropped unless Luke's own request contains `***` / "authorise". The physical max-speed estimate still clamps speeds (with a note).
## 5. Orbit Plan  [W]
All MechJeb (maneuver planner → node → node executor; the menu buttons ARE the confirmation, they burn right away):
- **Ascent to Ap** `mechjeb_ascent`, **Launch to target plane** `launch_to_target_plane` (MJ launch window + ascent into the target's plane - the "LAN / launch window" item), **Circularize**.
- **Set Ap** `change_apoapsis`, **Set Pe** `change_periapsis`, **Set Inc** `change_inclination` (at the AN/DN), **Match target plane** `match_target_plane`.
- **Transfer to <body>** `transfer_to` (MJ Hohmann), **Ask AI** for a dV-checked plan.

## 6. Capture Assist (was PBCP)  [W]
Arriving at a body: **Capture numbers** `capture_plan` (Pe, v at Pe, capture/circularize dV vs available, atmosphere + suggested aerocapture Pe), **Capture (circularize)** at Pe via MJ, **Aerocapture Pe** field + **Set Pe (MJ burn)** `change_periapsis` (works on a hyperbolic approach: burns 60 s from now), "Ask AI: capture plan". Course correction: Flight Plan `course correction 30 km`.

## 7. Docking  [W]
**Dock with target**: auto-rendezvous plus dock at any *free compatible* port. Matches port size, picks the nearest free one, and checks fuel and that our own port exists. [W] `dock_with("target")`

## 8. Orbital Autopilot  [W]
- **Station keep over target / KSC / lon** [W] `station_keep`: synchronous orbit with the sub-satellite point over the target's longitude (else KSC, or the Lon field). Runs as a Flight Plan: MJ raises Ap to synchronous altitude, MJ "apsis longitude" puts that Ap over the longitude, MJ circularizes there. **Sync alt?** shows the synchronous altitude.
- **Antenna Lock** [W] `antenna_lock`: nose to nadir, held by the kRPC autopilot.

## 9. Sun Lock  [W]
Space only. Points the craft's best face (the one with the most fixed solar-panel area) at the sun, then holds it with SAS/MJ SmartASS. v1 (`sun_lock`): nose to the sun via the kRPC autopilot. Best-face (side panels): queued.

## 10. Power  [W]
Electric charge: amount / max, % bar, net rate (EC/s, smoothed), **time to empty / time to full**. This is local C#, so it works with no bridge.

## 11. Science  [W]
- **Collect all**: run every rerunnable experiment. [W] `run_science(transmit=false)`
- **Transmit** toggle: run + transmit. [W] `run_science(transmit=true)`
- Watcher mode (off / remind / auto). [W] `set_science_watcher`
- **Reset Experiments** [W] `reset_experiments`: inoperable ones (needs a scientist aboard), and optionally discards held data (after a transmit).

## 12. Crew Transfer / Station Life / EVA  [W]
- Crew list per part (local C#). [W]
- Transfer crew between parts of this vessel, including ships docked to it (they are one vessel): **move** next to a kerbal, then pick a part with a free seat. [W] local C# (`RemoveCrewmember` / `AddCrewmember` / `SpawnCrew`)
- EVA: **return to ship** boards the nearest free seat with a hatch within 50 m. [W] `KerbalEVA.BoardPart`

## 13. Taxi / Base Run  [W]
Waypoint list (`GET /taxi`: Runway 09/27 start, Runway middle, KSC Pad, Island 27/09 start, plus saved spots) with distance/bearing from the craft, **Go** per point, a **Route** field (`;`-separated names or lat,lon), a speed cap, **Mark here as point**, **Stop**. Backend `kspchat/taxi.py` (`taxi_to`): straight-line steering with wheel steering + yaw; rovers drive with wheel motors, planes with engine throttle (≤ 40 %) + brakes. Also a Flight Plan step (`taxi to Runway 09 start`).

## 14. Abort / Status  [W]
- **Abort**: stops any bridge autopilot cleanly and releases the controls. [W]
- **Status** [W]: radar alt, MSL alt, speed, heading (local); the `/landing` line now includes the **signed heading error ±** (autoland and holds); the `autopilot_status` button.

## 15. Settings  [W]
MechJeb detected / prefer MechJeb [W display], UI opacity [W], bridge autostart (PluginData/bridge.cfg) [display], window positions saved [W].

## Remaining hard limits (2026-10-08)
- **Multi-vessel crew transfer**: only within one vessel (docked = one vessel). Kerbals can't jump between separate ships except by EVA (then "return to ship").
- **Taxi**: straight lines between waypoints, no obstacle/building avoidance; route around buildings with intermediate points. No KSC map overlay drawing.
- **Station-keep**: one-shot placement (no continuous trim); with inclination ≠ 0 the ground track still swings N-S. Phasing depends on MechJeb's apsis-longitude op (it may take an extra orbit).
- **Sun Lock**: nose to the sun only; "best face" for side-mounted fixed panels is not done.
- **Planes**: MechJeb's aircraft autopilot / spaceplane autoland is deliberately not used (unreliable); bridge controllers fly planes.
- **launch_to_target_plane**: MJ waits for the window; if it doesn't stage at T-0 on your craft, press space.
- One autopilot at a time (guard): a Flight Plan step waits for the previous controller; Abort stops everything.

---

## Flight fixes (from the 2026-10-08 dash / island flights)
Status as of commit after 31b7fc6: 1-9 DONE in code, except where noted (they still need in-game testing).
1. **Radar altitude + runway elevation.** Terrain clearance and the "AGL" targets must use radar altitude / runway threshold elevation, not sea level or "runway = 0 m". (Luke hit a mountain.)
2. **Signed heading error** (+/−°) in chat, autoland status and the Status panel, plus target heading and distance to the entry point.
3. **Fly-heading mode** (hold a heading/altitude/speed, no destination).
4. **Set commands** (set altitude / V/S / heading / roll / speed / pitch) that apply live via settings, with no controller restart.
5. **Island Airfield E-W runway.** The built-in spot is a N-S "approx" line at lon −71.885, which is likely wrong. The real strip runs E-W: Luke's takeoff point is −1.51608, −71.85674, hdg 270, MSL 134.6, saved as **Island 27** (threshold = east end). It should be the default Island landing target. Also choose the takeoff runway direction deliberately, not from the spawn heading (a N-facing spawn rolled off the cliff).
6. **Overspeed / throttle floor.** The 30 % climb floor let it reach 254–284 m/s at < 2 km. Cut faster above the cap; the floor yields when over-speed. Also: rotation came late (112 m/s, should be ≤ 75).
7. **Bank.** 10° above 250 m/s made a ~100 km dash turnaround. Allow ~15° up high or slow down before the turn; smooth the takeoff→plan handoff (a 58° roll spike was seen).
8. **Climb V/S via throttle.** In the climb, throttle chases a V/S target (Luke: 50–60 m/s) and ignores airspeed (no overspeed cuts during the climb). Pitch stays in its band.
9. **EAS vs TAS.** The 200 m/s climb target below 8 km is applied as TAS (should be EAS); the EAS cap also clamps the Mach 1.5 target.

## Emergencies, parts & control-config awareness (bridge `kspchat/emergency.py`)

- Always-on watcher (own kRPC connection, 1 s, flight scene only): engine flameout (incl. intake-air starvation / out
  of fuel), reverse thrust in flight (normal on the ground / rollout), part loss without staging, overheating (>= 95 %),
  fuel < 5 %, stall (AoA > 20 deg sinking; FAR stall fraction), G-LOC blackout, and control-config sabotage
  (surfaces losing pitch/yaw/roll duty, inversion / authority changes, reaction wheels off, engines shut down or
  mode-changed, intakes closed; our own engine tools are ignored for 5 s).
- One panicked line in the pilot's voice per event + what was done, one line when resolved (no spam), e.g.
  `Sidry: MAYDAY MAYDAY! Engine 2 flamed out! We're losing thrust! [autopilot: wings level 30 s; relight tried on Engine 2; throttle kept on the remaining 1 engine]`.
- Safety actions: wings level 30 s (bank cap 10 deg) after flameout / part loss / stall; relight (engine off/on) and
  intakes opened; reverse thrust: autopilots can't add power (idle) and reversers are switched back to forward
  (engine mode or the part's reverser event), else idle until forward; stall with nothing engaged: SAS on + full power;
  blackout: protect's wings-level / G-easing mode; sabotage: fly safe (bank 15 deg) and probe the control response.
- Control probe (first autopilot engage per vessel and after a config change, > 500 m AGL, not on takeoff / final): a
  0.1 roll / pitch input for 0.4 s vs the measured attitude change; an inverted axis is adapted (the autopilots flip
  that axis), a dead axis -> fly safe.
- Feeds: `GET /status` key `alert` (STATUS window) and `GET /systems?format=text` (Systems dashboard: master
  caution/warning line + one `sys` light per system; `lights` = setting `mayday_lights`, default on).
## STATUS window, Systems dashboard and menu indicators (mod `StatusWindow.cs`, next KSP restart)

- AICS menu header: `status` toggles the STATUS window, `systems` the Systems dashboard (both draggable, resizable from
  the lower-right `//` grip, closable with `x`; visibility + position saved in `PluginData/status_window.txt`).
- STATUS window (polls `GET /status?format=text` ~1 s while open or while the AICS menu is expanded): active
  autopilot + phase, altitude / speed / heading / bank / pitch (current -> target), throttle (auto / manual %), live
  orders, overrides with authority marks (`*` / `***` / `all`, eased %), pilot (conscious / BLACKED OUT), protect,
  distance / ETA, Flight Plan step, active ALERTs. The live landing distance / ETA line moved here from the chat window
  (chat keeps replies and alerts only).
- Menu indicators: green filled circle = engaged, grey empty circle = not, next to Aircraft Autopilot (holds), Approach
  & Autoland (autoland / fly-to), Landing Guidance (rocket lander), Flight Plan, Orbit Plan + Orbital Autopilot
  (MechJeb), Docking and Taxi; fed by the same `/status` poll.
- Systems dashboard (flight; polls `GET /systems?format=text` ~1 s): MASTER WARNING / MASTER CAUTION lamps that flash
  red / amber during an active emergency with the alert text - click a lamp to acknowledge (stops flashing); one light
  per system (green OK / amber caution / red failure): each engine (thrust %, mode, REVERSE), intakes (open, air %),
  each control surface (P/Y/R axes, INV, authority, deployed, CHANGED vs the engage baseline), reaction wheels, gear,
  brakes, chutes, fuel, hottest part, G-load, pilot, control adaptation. Option (default on): blink the craft's Light
  action group during an unacknowledged MAYDAY (restored afterwards; the bridge setting `mayday_lights=false` vetoes it).
## Autoland rollout thrust reversers (bridge `kspchat/reversers.py`)

- Full-stop landings only (not touch-and-go): once the mains and nose are down, wings level and the wheels on the
  runway (> 40 m/s), every running engine that can reverse (an engine mode named 'reverse', or the part's reverser
  event / action - the same switch the emergency watcher uses) goes to reverse at 60 % throttle, brakes as before.
  A bounce or a swerve (> 15 deg off the runway heading) = idle. Below 30 m/s: idle, engines back to forward, brakes
  until stopped. Abort / error / timeout also switches them back. No reverser on the craft = the old rollout.
- `/status` phase shows `rollout: reversers` meanwhile; the emergency watcher treats it as normal (no MAYDAY, no
  switch-back). Setting `autoland_reversers` (default on): `/reversers off` / `/reversers on` in chat.

## Live-test fixes (2026-10-08 17:40)

- Eject: 'eject' / 'EJECT!' asks yes/no; one-step 'eject confirm', 'confirm eject', 'eject now' (any case, punctuation)
  runs at once. The model's captain_order can never skip the yes. 'deorbit' / 'Deorbit!' is a direct order (deorbit_burn).
- No-tool claim guard also catches action verbs (ejecting, deploying, staging, engaging, initiated, burn executed, ...).
- Hold takeoff: climb-attitude integrator once airborne (nose-heavy deltas reach the 9 deg climb), and hand-off to the
  holds after 25 s airborne or on a pitch order (previously stuck in the takeoff loop, ignoring 'PULL UP').
- Emergency: an engine shut down mid-flight keeps the original tamper alert ('Who shut down an engine?!', wings level) -
  Luke shut Engine 1 down himself at 17:44, so it was a correct detection. The control probe waits until the autopilot has been engaged 20 s and is near level (< 10 deg bank, |V/S| < 30).

## Takeoff rotation, ramjet throttle and runway-end abort (2026-10-08, bridge `kspchat/takeoff.py`)

The Aeris 4A (Whiplash delta, 19.4 t, TWR 1.25) ran the whole runway at 50 % throttle with only ~20 % up elevator,
never rotated, went off the end at ~200 m/s and skimmed the water. Vr was fine (56 m/s, default stall 45 - the craft
isn't in the stall cache) and the roll accelerated normally; the nose simply never came up.

- Ramjets (J-X4 Whiplash, any 'ramjet'/'scramjet' engine title) start the takeoff at full throttle; on any craft a roll
  slower than 0.8 m/s^2 after the spool-up wait jumps straight to full (0.8-1.5 m/s^2 still adds 5 % steps).
- Rotation assist (plane autoland takeoff and hold takeoff): at/above Vr on the ground, extra up-elevator ramps
  0.15/s (max 0.8) while the nose isn't rising (< 1 deg/s); holds once it rises, backs off 0.5/s if it rises > 3 deg/s
  or passes the 9 deg attitude, and bleeds off 0.1/s once airborne.
- If the assist reaches its limit and the nose still isn't up: full throttle, keep accelerating, assist held (it backs
  off and re-ramps if the nose jumps). Rotated = nose rising > 1 deg/s, nose > 3 deg above the roll attitude, climbing
  > 1.5 m/s, or wheels off.
- Learned Vr: the emergency watcher spots every real takeoff (hand-flown or autopilot: a ground run from < 15 m/s, then
  airborne 8 s and above 30 m AGL, liftoff <= 180 m/s), logs 'takeoff: <craft> lifted off at ~N m/s (pitch P, by
  hand)' and stores it in settings `liftoff_speeds` (craft name + part/mass signature). Next takeoff: Vr = 0.92 x the
  learned liftoff (Luke's ~120 -> ~110). Uncached: Vr = 1.25 x stall (default 45 -> 56), the assist + full power do
  the rest.
- Runway-end abort (on the ground only; never 0 throttle in flight): only at the last point it can still stop
  (v^2 / 7 + 100 m from the actual speed) AND when it can't reach the speed it needs (learned liftoff, else up to
  140 m/s) plus 3 s to rotate before the end at the measured acceleration. Then idle + brakes and a chat message
  saying why.

## Propeller planes, upside-down detection, damage facts (2026-10-08 18:35)

- Propellers count as propulsion (`kspchat/propulsion.py`): Breaking Ground rotors (ModuleRoboticServoRotor) with
  blade parts, and electric-only engines. No more 'no engines' for a prop plane; 'take off' / 'takeoff' is a direct
  order (and a model tool) for a runway takeoff - never a MechJeb ascent for a plane on a runway. Props show no thrust
  in kRPC: the takeoff never stages them, starts at full throttle and is governed by the measured acceleration; the
  max-speed estimate measures their thrust from the acceleration (drag + m dV/dt); safe ceiling 5 km. The takeoff
  warns if a rotor motor reads disengaged. Prop blades are left out of the control-sabotage scan; the Systems
  dashboard gets a Props row (rotors, RPM, motor).
- Upside down (emergency watcher): in flight (planes only, nose within 60 deg of the horizon) roll beyond 120 deg for
  2 s -> MAYDAY line + INVERTED warning; with an autopilot engaged it rolls upright the shortest way with the elevator
  neutral (no pull-through: altitude isn't traded for speed; throttle untouched), wings level 30 s. On the ground /
  splashed (the craft's top pointing down, or roll > 120) -> a funny line ('We're upside down! Did we land? Is this
  landing?') and Attitude INVERTED on the Systems dashboard / STATUS alerts.
- Lost parts are named (left / right main wheel, nose wheel, or the part title); a kerbal leaving an external seat is
  not a lost part. 'damage report' / 'what are we missing' lists only what the watcher actually saw (lost parts,
  upside down, alerts); set_gear adds a note only if gear really was lost.
- State-claim guard: a reply claiming a state no tool reported ('down and ready', 'equipped for takeoff', 'fixed',
  'all set'; with damage on record also 'everything is fine', 'no damage', ...) is re-prompted once with what the tools
  did and the watcher facts, else flagged '(Bridge: ... is not confirmed by any tool. <facts>)'. The watcher facts go
  into the AI's context when there is damage or Luke's message mentions it.

## Gentle rollout brakes (2026-10-08 18:40, bridge `kspchat/rollout.py`)

Luke's prop plane (main gear behind the CoM, narrow track) flipped onto its back under hard braking. The autoland
rollout no longer brakes until every wheel reports grounded (kRPC Wheel.grounded; broken wheels ignored; fallback
after 8 s on the roll) and the craft has been calm for 1.5 s (|V/S| < 1 m/s, pitch rate < 1 deg/s, bank < 4 deg).
Brakes are on/off in kRPC, so they pulse: duty 20 % ramping to the maximum over 4 s (0.5 s cycle). A nose dip > 2.5 deg
below the settled rollout attitude, a nose-down rate > 3 deg/s, bank > 4 deg, roll rate > 6 deg/s or a wheel lifting
releases them until it settles again (within 1 deg, calm 0.5 s); each release lowers the maximum duty by 20 % (never
below 30 %). Reversers work as before. /status keeps the brake state (STATUS 'brakes'); releases are logged.

## Propeller control (2026-10-08 18:45, bridge `kspchat/propulsion.py`)

Breaking Ground props = rotor (RPM Limit, Torque Limit(%), Motor engaged) + blade pitch (blade Deploy Angle, blades
deployed). Field names are matched by keyword at runtime and logged once per craft ('props: rotor ... fields ...').
- Takeoff (hold / autoland takeoff, 'take off'): motor on, RPM 460 (BG max), torque 100 %, blades deployed at a fine
  pitch (8 deg).
- Blade pitch schedule while an autopilot flies (emergency watcher, every 2 s): blade AoA = deploy angle - inflow
  angle, inflow = atan(V / (RPM * 2pi/60 * r)); the deploy angle keeps AoA ~8 deg (8-60 deg). r = each blade's
  distance to its own rotor + 0.6 m. In flight the torque limit mirrors the autopilot throttle (floor 10 %).
- Never cut props in flight: torque >= 10 %, RPM limit >= 115, motor stays on, blade pitch >= 2 deg, no reverse.
- Reverse (beta): blade pitch -15 deg = negative AoA = reverse thrust, ground only. Autoland rollout: after the
  mains + nose are down (same gate as the jet reversers, setting autoland_reversers), torque 60 %; below 10 m/s back
  to fine pitch and torque 0. With left + right props, a heading error > 3 deg puts full reverse on the side it must
  turn toward and half on the other (differential steering).
- Groups: rotors are grouped by their side of the CoM (vessel frame x: left / right / center within 0.5 m); blades
  belong to their nearest rotor. Counter-rotating left/right pairs are detected and shown. Each blade keeps its own
  deploy-angle sign (a blade built with a negative angle is mirrored); KSP applies 'Invert Deploy Direction' itself.
- Orders: 'prop pitch N|auto', 'rpm N|max', 'torque N|max|auto', 'props on|off', 'props reverse|forward', each with
  an optional side: 'left props pitch 15', 'right rpm max', 'left props reverse', 'center torque 50'. Throttle orders
  also set the torque limit on prop craft.
- One prop out: a side's RPM below 30 % of its reference (recent peak, capped by its own RPM limit) or its motor
  disengaged, 2 s in flight with throttle >= 20 % -> MAYDAY 'Lost the left prop!', wings level and a rudder trim of
  0.15 toward the live side for the autopilots, cleared when it spins up again.
- Systems dashboard Props row per group: 'L: 440 RPM/460 tq 100% pitch +24; R: ...; counter-rotating'.

## Helicopters (2026-10-08 18:50, bridge `kspchat/heli.py`) - NOT yet flown in game

- Classification per craft (logged with each rotor's role): rotor spin axis = the rotor part's 'up' direction.
  Vertical (|cos| >= 0.8 vs world up) = lift rotor; horizontal with the axis fore-aft and off to one side = side prop
  (left / right group); horizontal with the axis sideways = tail rotor. Lift rotor + left and right side props =
  COMPOUND (Luke's: M-32S main rotor, two side props, no tail rotor, small wings; yaw = differential side-prop
  pitch). Lift rotor + tail rotor = conventional (yaw = tail rotor pitch). Two lift rotors close together = coaxial
  (counter-rotating: differential torque), apart = tandem. Lift rotor on a winged craft without side props / tail =
  still a plane. Helis never get the plane holds, autoland, runway takeoff, liftoff learning or plane upright logic.
- Control (decoupled; conservative gains): collective (main-rotor blade 'Deploy Angle') = PI on vertical speed
  (target from the AGL/radar altitude target, max 3 m/s up/down). Tilt (pitch + roll, cap 12 deg; compound 6 deg as
  the side props add forward thrust) = the ground-velocity vector resolved into forward / lateral relative to the
  nose: nose down = accelerate, nose up = slow / stop. Yaw loop = HEADING (nose). HEADING and TRACK are separate: the
  nose follows the track above 15 m/s unless pinned by 'face N'. kRPC AutoPilot (surface frame) holds the attitude; a
  VAB-built craft (control point looking up) gets SAS + vertical control only. The yaw actuator sign is checked online
  (3 wrong 1-s windows -> flip, logged).
- Orders: 'take off' (vertical to a 20 m hover), 'hover [at N] [facing N]', 'climb to N' / 'descend [to N]' (AGL;
  heights under 500 m are AGL), 'land' (3 m/s, 2 below 30 m, 1 below 10 m, under 1.5 m/s at touchdown), 'fly N [at S]'
  / 'fly heading N speed S' (track), 'face N' (nose only), 'sidestep left/right [N m]', 'back up [N m]',
  'hold position', 'fly to <place>' (hover + hold there), 'heading N' / 'turn' (moving: track; hovering: nose),
  'speed N'. 'pitch up/down' is refused for helis.
- Rotor checks (Luke): every rotor (props and helis) must have 'Brake' 0, 'Torque Limit(%)' > 0, 'Motor' Engaged.
  Pre-flight at takeoff / hover fixes and reports them in chat ('Pre-flight: center rotor (M-32S Rotor) Brake was 100
  -> released (0)'); the heli then waits up to 12 s for the main rotor to pass 60 RPM before lifting, else aborts with
  the rotor's Brake / Torque / Motor state. In flight a Brake > 0 = tamper MAYDAY + auto release; Torque Limit 0 =
  restored; prop-out / rotor-RPM alerts name the cause. Dashboard 'Rotor brakes' row per rotor. Optional park brake
  after a heli landing: setting 'rotor_brake_park' (default off): torque 0, wait for < 20 RPM, Brake 100.
- Emergencies: main rotor RPM < 60 % of its reference for 1.5 s -> MAYDAY; 'On Power Loss: Locked' (Luke's) means
  no autorotation -> glide on the side props + wings / controlled descent, said in chat; otherwise autorotation (low
  collective, flare below 12 m, cushion below 6 m). Spin > 45 deg/s for 3 s or a lost tail rotor -> MAYDAY, land
  (conventional: weathervane with forward speed first). Vortex ring (descent > 6 m/s below 8 m/s airspeed, above 5 m)
  -> limit the descent, add 12 m/s forward. Upside down -> MAYDAY, collective down, level the disc. A side prop out
  on the compound -> land.
- Dashboard 'Mode' row: HELI (layout), rotor RPM/limit, collective, V/S, HDG vs TRK + drift, Locked on power loss.

## Crew intercom chatter (2026-10-08 19:00, bridge `kspchat/crew.py`)

- The PILOT makes the MAYDAY radio call and flies the recovery (unchanged: 'Sidry: MAYDAY! ...'). The rest of the
  crew (names + traits read from kRPC) react on the intercom: '[INTERCOM] Bob (Sci): WHAT IS GOING ON?!',
  '[COMMS] Bill (Eng): Left main wheel is GONE!' (engineers on COMMS), tourists scream; an occasional '-- over'.
- Rate limits: one line per crew member per 10 s, at most 2 non-pilot lines per emergency, none on 'resolved'.
  Damage events: the engineer speaks first and names the real lost part from the watcher.
- Lines are AI-generated with the chat's current backend (LM Studio only if a model is already loaded; never loads
  one), Luke's prompt: 'You are <Name>, a <trait> aboard a Kerbal spacecraft. This is happening: <watcher facts>.
  What do you shout over the intercom? One short line.' Each runs in a background thread with a 4 s deadline; slow /
  failed replies, tool-call text, action claims ('I fixed ...') or over-long lines fall back to the canned pools.
  The pilot's MAYDAY and recovery never wait on it.
- Landed and stopped with damage: the engineer offers a repair once per damage list.
- '/crew chatter on|off' (default on).

## Trip chatter, personalities, language option (2026-10-08 19:08)

- 'Are we there yet?' (`kspchat/crew.py` Trip): on quiet trips only - an autopilot leg / fly-to / heli / flight plan
  running, or more than 2 min in the air; never during an emergency or within 60 s after one. Short trips (ETA < 5 min):
  at most one line, ~1 in 3 trips. Long trips: the first after 1.5-3.5 min, then every 3-6 min, the gap shrinking as
  the impatience grows (from the 3rd ask: 'ARE. WE. THERE. YET?!'). Scientists, engineers and tourists ask; the pilot
  answers about half the time with the REAL ETA / distance (from the landing-ETA estimate; AI answers containing any
  number that isn't in the facts fall back to the canned reply). Same AI-with-canned-fallback approach, same
  '/crew chatter on|off' and per-member 10 s limit.
- Personalities (`kspchat/personality.py`): per kerbal name, generated once and kept in kerbal_personalities.json
  (gitignored, next to bridge_settings.json): a temperament from KSP's stats where kRPC has them (low courage =
  nervous / panicky, high stupidity = goofy, badass = unflappable, veteran = seasoned; else a random one), two likes and
  one dislike (often job-ironic: 'hates flying', 'is afraid of heights', 'gets airsick', 'is terrified of space').
  Prompts read 'You are Bob, a nervous scientist who loves rocks and snacks and is afraid of heights, aboard ...'.
- Language: '/language filter on|off' (bridge_settings.json 'language_filter', default ON). ON: a no-cursing line in
  the chat and crew prompts plus a filter that masks cuss words in AI replies and intercom lines ('s***'). OFF: neither.
  Slurs / hate: always forbidden in the prompts and removed by the filter. AICS Settings: POST /setting
  {"key": "language_filter" | "crew_chatter", "value": "on|off"}; /health reports both (the mod has no toggle for
  them yet - use the chat commands).

- Dad jokes (2026-10-08 19:10): 'loves dad jokes' is in the likes pool. On quiet trips such a kerbal's line is a clean
  dad joke ~1 in 3 times, at most one joke per 15 min. In an emergency they crack a nervous dad joke half the time as
  their intercom line (it counts toward the 2 lines per emergency; never the pilot). AI line with a canned joke
  fallback; the language filter and '/crew chatter off' apply. Note: kerbals already saved in
  kerbal_personalities.json keep their likes (delete an entry to re-roll it).
- Pilot snaps back: only when crew chatter is happening in that emergency (2+ crew lines: 1 in 4; after a crew dad
  joke: 6 in 10), once per emergency max, after the crew lines, e.g. '[RADIO] Sidry: Everyone QUIET, I need to
  concentrate!' (language filter applies). Async: never delays the recovery. Never unprompted, never with chatter off.

- Small talk (2026-10-08 19:12, crew.SmallTalk): calm cruise only (same eligibility as the trip chatter, no emergency
  within 60 s). Long trips: the first conversation after 4-10 min, then one every 8-15 min; short trips (ETA < 5 min):
  1 in 5, at most once. Two non-pilot crew (or the pilot plus the only other kerbal) trade 4-6 alternating lines,
  5-10 s apart, about ship life (cafeteria stew, snacks, Jeb gossip, the view, science, the bunks). Each line is
  AI-generated from that kerbal's personality and the conversation so far ('You are Bob, ... You're chatting with
  Bill about snacks. So far: ... What do you reply? One short line.'), with a canned script as the fallback. An
  emergency cuts it off at once; '/crew chatter off' and the language filter apply; no 'are we there yet' over it.

## Systems dashboard for every craft type

GET /systems picks rows by craft type (Craft row: ROCKET / LANDER / ROVER / STATION / PROBE; planes and helicopters keep their rows):
- Rockets / landers: per-engine thrust, gimbal (LOCKED / limit %), NO FUEL on a starved engine; fuel of the next decouple stage per resource (LF/OX/Mono/Xenon/SRB; <15% amber, <5% red); Power (EC %, solar panels out and flow; <25% amber, <10% red); RCS on/off + thrusters + mono; SAS + mode; next stage contents; reaction wheels, chutes, temperature (existing rows).
- Rovers: wheels grounded / motors on / BROKEN (red), battery, speed, SAS.
- Stations / probes: power + solar, per-resource totals, comms (connected / NO SIGNAL, signal %, antennas deployed; red on an uncrewed craft), RCS, SAS.
Bridge-side only (kRPC); no mod change.

## Talk to a kerbal

Address a crew member ABOARD the active vessel by first name in chat ('Hey Bob, are you having a good time?', 'Bob: ...', '@Bob ...', '..., Bob?'; 3+ letter prefixes work, e.g. 'Jeb') and that kerbal answers in character over the intercom: '[INTERCOM] Bob (Sci): ...' (engineers on [COMMS]). Replies use the kerbal's personality and trait and only real flight facts (situation, altitude, speed, destination/ETA, active emergencies); kerbals can't act (tool-ish text and lines claiming an action are dropped). An order to a kerbal ('Bob, gear down') goes to the pilot as usual. A kerbal who isn't aboard doesn't answer; the pilot says so. Each kerbal remembers the last 4 exchanges (until the bridge restarts). Language filter applies. Addressing the pilot by name is the normal chat.
