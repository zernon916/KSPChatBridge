# ROCKET_PLAN - AICS rockets: planner, tablet, flight, landing (MICRO tasks for the local model)
Run: powershell -ExecutionPolicy Bypass -File .\qwen_pass.ps1 -Plan ROCKET_PLAN.md -Model openai/bonsai-27b -MaxTokens 29000 -Loop N
Original long version: ROCKET_PLAN.md.pre-micro. Tasks written "ID [BIG - Grok/Cursor]:" or "ID [LUKE]:" are skipped by qwen_pass (no colon right after the ID) - do them by hand.

## RULES (read every pass)
- Do exactly ONE task. Touch only the files the task names. Do not open other files.
- Keep each edit small and at the named anchor. Never write diff markers (+/-) into code. No switch/else-if rewrites: add ONE line where told.
- Pure math goes in static classes with NO KSP/Unity types (only System). C# 7.3, .NET 4.7.2, namespace KSPChatBridge, `internal static class`.
- MechJeb is inspiration only (GPL). NEVER copy MechJeb code, internal names or constant tables.
- No separate rocket Learn step: read live data (thrust, mass, Isp, density) every tick and adapt.
- Plan at about the middle of what the craft can do (TWR, turn rate, throttle) to keep margin both ways.
- Log decisions with ChatLog.Write(kind, text); chat lines with ChatWindow.Notice(text); crew lines with IntercomTalk.Fmt(name, trait, text). Signatures: qwen_ctx/RocketApi_snippet.txt when a task names it.
- Scroll fields are ALWAYS `static Vector2` (never int). .NET 4.7.2: no `Math.Clamp` (use Min/Max). CelestialBody: `Radius`, `atmosphere`, `referenceBody`, `bodyName`.

## Big picture (notes, not tasks)
- Screen split (Luke): cockpit MFD = only what matters for flying now (map, chart/ILS, live flight info). Tablet = planning + tests.
- Tablet tabs already exist: Mission(0) Burns(1) Map(2) Graphs(3) Tests(4) ... Mission/Burns/Graphs show "coming soon" until wired.
- Flight functions live in a new partial NativeRocket.cs on NativeFlightController; they write FlightCtrlState only in the Fly path. Tools: rocket_point, rocket_node, rocket_ascent, rocket_stop.
- Pure math test harness lives in tests/csharp (Program.cs is 2400+ lines; qwen_pass cannot pick test files) -> test wiring is a Grok/Cursor task (RKT1).
- Open questions for Luke: which MFD pages stay in cockpit (S2)? keep MechJeb buttons when our own rocket functions exist?

## TASKS

### 1. Pure math (new small files, System only)
- [x] RK1a: Hohmann - create KSPChatMod/RocketMath.cs (new) copying the usings/namespace/header style of OrbitMath.cs (template); `internal static class RocketMath` with `internal static void Hohmann(double mu, double r1, double r2, out double dv1, out double dv2, out double time)`: a=(r1+r2)/2; dv1=sqrt(mu/r1)*(sqrt(2*r2/(r1+r2))-1); dv2=sqrt(mu/r2)*(1-sqrt(2*r1/(r1+r2))); time=PI*sqrt(a^3/mu). Return NaN outs if mu<=0 or r<=0. Files: OrbitMath.cs (template), RocketMath.cs (new). Check: builds; Kerbin mu 3.5316e12, r1 700000, r2 12000000 gives dv1 ~ 860 m/s.
- [x] RK1b: Circularize - in KSPChatMod/RocketMath.cs add `internal static double CircularizeAtAp(double mu, double rAp, double rPe)` = sqrt(mu/rAp) - sqrt(mu*(2/rAp - 2/(rAp+rPe))). NaN on bad input. Only this method. Files: RocketMath.cs. Check: builds.
- [x] RK1c: Phase angle - in KSPChatMod/RocketMath.cs add `internal static double PhaseAngleDeg(double mu, double r1, double r2)`: transfer time t from Hohmann (call it), target angular rate n2=sqrt(mu/r2^3); return 180 - n2*t*180/PI, normalised to [-180,180). Only this method. Files: RocketMath.cs. Check: builds.
  done: qwen_pass 2026-10-13 13:43, files KSPChatMod/RocketMath.cs
- [x] RK1d: Capture dv - in KSPChatMod/RocketMath.cs add `internal static double CaptureDv(double mu, double rPe, double vInf, double rApTarget)`: vPe = sqrt(vInf^2 + 2*mu/rPe); vOrbit = sqrt(mu*(2/rPe - 2/(rPe+rApTarget))); return vPe - vOrbit. Files: RocketMath.cs. Check: builds.
  done: qwen_pass 2026-10-13 13:44, files KSPChatMod/RocketMath.cs
- [x] RK1e: Ejection - in KSPChatMod/RocketMath.cs add `internal static double EjectionDv(double mu, double rPark, double vInf)` = sqrt(vInf^2 + 2*mu/rPark) - sqrt(mu/rPark), and `internal static double EjectionAngleDeg(double mu, double rPark, double vInf)`: e = 1 + rPark*vInf^2/mu; return 180 - acos(1/e)*180/PI. Patched conics, own formulas. Files: RocketMath.cs. Check: builds.
  done: qwen_pass 2026-10-13 13:45, files KSPChatMod/RocketMath.cs
- [x] RK1f: Burn time - in KSPChatMod/RocketMath.cs add `internal static double BurnTime(double dv, double mass, double thrust, double isp)`: g0=9.80665; flow=thrust/(isp*g0); m1=mass/exp(dv/(isp*g0)); return (mass-m1)/flow. NaN if thrust<=0 or isp<=0. Files: RocketMath.cs. Check: builds.
- [x] RK1g: Burn time across stages - in KSPChatMod/RocketMath.cs add `internal static double BurnTimeStages(double dv, double[] stageDv, double[] mass, double[] thrust, double[] isp)`: loop stages, take min(dv left, stageDv[i]), add BurnTime for that part, stop when dv left <= 0; NaN if dv left > 0 at the end. Files: RocketMath.cs. Check: builds.
  done: qwen_pass 2026-10-13 14:03, files KSPChatMod/RocketMath.cs
- [x] RK1h: Suicide burn - create KSPChatMod/SuicideBurn.cs (new), `internal static class SuicideBurn` with `internal static double StartAlt(double vDown, double g, double thrust, double mass, double margin)`: a = thrust/mass - g; if a <= 0 return double.PositiveInfinity; return vDown*vDown/(2*a)*(1+margin). Files: OrbitMath.cs (template), SuicideBurn.cs (new). Check: builds.
  done: qwen_pass 2026-10-13 14:04, files KSPChatMod/SuicideBurn.cs
- [x] RK1i: Staging policy - create KSPChatMod/StagingPolicy.cs (new), `internal static class StagingPolicy` with `internal static string ShouldStage(bool flameout, bool tankEmpty, double secondsSinceEmpty)`: returns null unless (flameout or tankEmpty) and secondsSinceEmpty >= 0.5; then returns "flameout" or "tank empty". Files: OrbitMath.cs (template), StagingPolicy.cs (new). Check: builds.
  done: qwen_pass 2026-10-13 14:05, files KSPChatMod/StagingPolicy.cs
- [x] RK1j: Fairing policy - create KSPChatMod/FairingPolicy.cs (new), `internal static class FairingPolicy` with `internal static string CanDeploy(double qKpa, double altM, double heatRatio)`: return null (= not yet) if heatRatio > 0.6; return "q low" if qKpa < 1; "above 40 km" if altM > 40000; else null. Files: OrbitMath.cs (template), FairingPolicy.cs (new). Check: builds.
  done: qwen_pass 2026-10-13 14:05, files KSPChatMod/FairingPolicy.cs
- [x] RK1k: Abort policy - create KSPChatMod/AbortPolicy.cs (new), `internal static class AbortPolicy` with `internal static string Check(double angRateRadS, double attErrDeg, double thrustDropFrac)`: "tumbling" if angRate > 1.5 and attErr > 45; "thrust loss" if thrustDropFrac > 0.5; else null. Files: OrbitMath.cs (template), AbortPolicy.cs (new). Check: builds.
  done: qwen_pass 2026-10-13 14:06, files KSPChatMod/AbortPolicy.cs
- [x] RK1l: Ascent turn start - create KSPChatMod/AscentProfile.cs (new), `internal static class AscentProfile` with `internal static double TurnStartSpeed(double twr)`: clamp(100 - (twr-1.2)*40, 40, 100) m/s (mid-capability: higher TWR turns earlier). Files: OrbitMath.cs (template), AscentProfile.cs (new). Check: builds.
  done: qwen_pass 2026-10-13 14:07, files KSPChatMod/AscentProfile.cs
- [x] RK1m: Ascent pitch curve - in KSPChatMod/AscentProfile.cs add `internal static double PitchDeg(double alt, double turnStartAlt, double turnEndAlt, double twr)`: 90 below turnStartAlt, 0 above turnEndAlt; between: 90*(1 - pow(f, s)) with f=(alt-start)/(end-start), s = clamp(0.4 + 0.15*twr, 0.4, 0.8). Files: AscentProfile.cs. Check: builds.

### 2. Data readers
- [x] RK2a: StageInfo struct - create KSPChatMod/StageInfo.cs (new), `internal sealed class StageInfo` with public fields only (no KSP types): int Stage; double DvVac, DvAsl, DvActual, TwrVac, TwrAsl, BurnTime, StartMass, EndMass, FuelMass, IspVac, IspAsl. Files: OrbitMath.cs (template), StageInfo.cs (new). Check: builds.
  done: qwen_pass 2026-10-13 14:13, files KSPChatMod/StageInfo.cs
- [x] RK2b: BodyInfo - create KSPChatMod/BodyInfo.cs (new), `internal static class BodyInfo` with `internal static CelestialBody Find(string name)` (loop FlightGlobals.Bodies, case-insensitive bodyName) and `internal static string Line(CelestialBody b)` returning "Mun: mu .., R .. km, atmo .. km, SOI .. km, day .. s, parent .." (gravParameter, Radius, atmosphereDepth if atmosphere, sphereOfInfluence, rotationPeriod, referenceBody). Null-safe. Files: OrbitMath.cs (template), BodyInfo.cs (new). Check: builds.
### STOP HERE - first test batch (RK1a-RK2b). Luke checks the results, then deletes this line.

- [x] RK2c: StageReader - create KSPChatMod/StageReader.cs (new), `internal static class StageReader` with `internal static List<StageInfo> Read(VesselDeltaV dv)`: if dv == null or !dv.IsReady return empty list; foreach DeltaVStageInfo s in dv.OperatingStageInfo copy stage, deltaVinVac, deltaVatASL, deltaVActual, TWRVac, TWRASL, stageBurnTime, startMass, endMass, fuelMass, ispVac, ispASL into a new StageInfo. Files: StageInfo.cs (read only), StageReader.cs (new). Check: builds.
- [x] RK2d: StageReader not ready log - in KSPChatMod/StageReader.cs add `static double lastNotReady;` and in Read, when not ready, `ChatLog.Write("rocket", "stage dv not ready")` at most once per second (UnityEngine.Time.realtimeSinceStartup). Files: StageReader.cs. Check: builds.
- [x] RK2e: StageReader vessel/editor - in KSPChatMod/StageReader.cs add `internal static List<StageInfo> ForFlight()` (FlightGlobals.ActiveVessel?.VesselDeltaV) and `internal static List<StageInfo> ForEditor()` (EditorLogic.fetch?.ship?.vesselDeltaV), both calling Read. Files: StageReader.cs. Check: builds.
- [x] RK2f: LiveRocket snapshot - create KSPChatMod/LiveRocket.cs (new), `internal sealed class LiveRocket` with public double fields Alt, RadarAlt, Vs, Hs, QKpa, StaticPressure, Density, Mass, LocalG, MaxTempRatio, AngRate; int Stage; and `internal static LiveRocket Read(Vessel v)` filling them from v.altitude, v.radarAltitude, v.verticalSpeed, v.horizontalSrfSpeed, v.dynamicPressurekPa, v.staticPressurekPa, v.atmDensity, v.totalMass, v.graviticAcceleration.magnitude, v.currentStage, v.angularVelocity.magnitude (MaxTempRatio: max part.temperature/part.maxTemp). Null returns null. Files: StageInfo.cs (template), LiveRocket.cs (new). Check: builds.
- [x] RK2g: LiveRocket thrust - in KSPChatMod/LiveRocket.cs add fields Thrust, FuelFlow and fill them in Read from active engines only: foreach part, foreach ModuleEngines e with e.EngineIgnited && !e.flameout: Thrust += e.GetMaxThrust()*e.thrustPercentage/100; FuelFlow += e.maxFuelFlow*e.thrustPercentage/100. Files: LiveRocket.cs. Check: builds.

### 3. Planning math (pure)
- [ ] RK3a: LegBudget launch - create KSPChatMod/LegBudget.cs (new), `internal static class LegBudget` with `internal static double LaunchDv(double mu, double radius, double orbitAlt, double atmoDepth)`: v = sqrt(mu/(radius+orbitAlt)); loss = atmoDepth > 0 ? 1.15 + atmoDepth/70000*0.35 : 1.05; return v*loss (rough guess, comment it). Files: OrbitMath.cs (template), LegBudget.cs (new). Check: builds.
- [ ] RK3b: LegBudget legs - in KSPChatMod/LegBudget.cs add `internal sealed class Leg { public string Name; public double Dv; }` and `internal static List<Leg> Legs(double launch, double transfer, double capture, double land, double ret)` returning one Leg per value > 0 named launch/transfer/capture/land/return. Files: LegBudget.cs. Check: builds.
- [ ] RK3c: StageAllocator - create KSPChatMod/StageAllocator.cs (new) with `internal sealed class LegResult { public string Leg; public int Stage; public double Spare; }` and `internal static List<LegResult> Allocate(double[] stageDv, string[] legNames, double[] legDv)`: walk stages bottom-up, burn legs in order, one result per (leg, stage) part; a leg may split across stages; last result per leg gets Spare = remaining dv of that stage, negative if short. System only. Files: OrbitMath.cs (template), StageAllocator.cs (new). Check: builds.
- [ ] RK3d: WindowFinder - create KSPChatMod/WindowFinder.cs (new), `internal static double SecondsToPhase(double currentDeg, double targetDeg, double n1, double n2)` (n = rad/s): rel = n2-n1 in deg/s; wait = (targetDeg-currentDeg)/rel, add 360/|rel| until >= 0. Files: OrbitMath.cs (template), WindowFinder.cs (new). Check: builds.
- [x] RKT1 [BIG - Grok/Cursor]: Test harness: add RocketMath/SuicideBurn/StagingPolicy/FairingPolicy/AbortPolicy/AscentProfile/LegBudget/StageAllocator/WindowFinder to tests csproj, new tests/csharp RocketTests group called from Program runner (LKO->Mun Hohmann, Kerbin->Duna ejection, split leg, tumble, thrust loss).
- [x] RKM10 [BIG - Grok/Cursor]: Porkchop grid (Lambert solver, departure x travel time -> dv). Tests vs known Kerbin-Duna window.

### 4. Screen split (Luke)
- [ ] S1 [LUKE]: Table of every MfdNav group/item (id, label, cockpit or tablet). Read-only; Luke decides each row.
- [ ] S2 [LUKE]: Final cockpit page list.
- [ ] RK4a: Tablet flag - in KSPChatMod/MfdNav.cs inside `internal sealed class Item` add one field `internal bool Tablet;` (default false) next to `internal string Id;`. No other change. Files: MfdNav.cs. Check: builds, nothing changes in game.
- [ ] RK4b [BIG - Grok/Cursor]: Hide Tablet=true items from cockpit MFD (AicsMfd + IvaMfd page lists) after S2.
- [ ] RK4c [BIG - Grok/Cursor]: Tests tab per-test cards + small graphs (S6/S7; TabletTests in TabletWindow).
- [ ] S8 [BIG - Grok/Cursor]: Full cockpit vs tablet overhaul once Luke finalises S2.

### 5. Mission tab (tablet tab 0)
- [ ] RK5a: Mission stub - create KSPChatMod/TabletMission.cs by copying TabletTrim.cs (template) (same usings, namespace, `public partial class AicsMenu`); method `void TabletMission(Rect area)`, scroll field `tabletMissionScroll`; one TabletUi.CardPanel "MISSION" with TabletUi.Label "MISSION - planner coming". Files: TabletTrim.cs (template), TabletMission.cs (new). Check: builds.
- [ ] RK5b: Mission wire - in KSPChatMod/TabletWindow.cs add ONE line `case 0: TabletMission(area); break;` directly ABOVE the line `case 2: TabletMap(area); break;`. Nothing else. Files: TabletWindow.cs. Check: Mission tab shows the card.
- [ ] RK5c: Destination picker - in KSPChatMod/TabletMission.cs add static fields `missionBody = "Mun"`, `missionAltT = "30000"`, `missionLand`, `missionReturn = true`; draw TabletUi.Segments of body names (FlightGlobals.Bodies bodyName array, cached once) selecting missionBody, a TabletUi.Field for missionAltT, two TabletUi.Button toggles LAND and RETURN. Files: TabletMission.cs. Check: picks change on click.
- [ ] RK5d: Stage table - in KSPChatMod/TabletMission.cs below the picker draw one TabletUi.Label row per StageInfo from StageReader.ForFlight() (or ForEditor() when HighLogic.LoadedSceneIsEditor): "S3 dv 1234 m/s  TWR 1.45  burn 62 s  Isp 310". Files: TabletMission.cs, StageReader.cs (read only). Check: rows match stock dv readout.
- [ ] RK5e: Legs table - in KSPChatMod/TabletMission.cs add `List<LegBudget.Leg> MissionLegs()` building legs from BodyInfo.Find(missionBody) + RocketMath.Hohmann (Kerbin orbit radius -> body orbit radius) + LegBudget.LaunchDv; draw one Label per leg "transfer 860 m/s". Files: TabletMission.cs, LegBudget.cs (read only). Check: Mun shows launch ~3400, transfer ~860.
- [ ] RK5f: Pass/fail bar - in KSPChatMod/TabletMission.cs call StageAllocator.Allocate with stage dv + legs; draw TabletUi.Bar "HAVE vs NEED" (green TabletUi.Good if all Spare >= 0 else red) and one Label per LegResult "transfer on S2, spare 120". Files: TabletMission.cs, StageAllocator.cs (read only). Check: removing a stage turns it red.
- [ ] RK5g: Crew margin comment - in KSPChatMod/TabletMission.cs add a button CREW SAYS that calls ChatWindow.Notice(IntercomTalk.Fmt("Jebediah", "Pilot", text)) with text "Plenty of spare, N m/s." or "We won't make it home." from the total spare. See qwen_ctx/RocketApi_snippet.txt. Files: TabletMission.cs. Check: one chat line.
- [x] RK5h [BIG - Grok/Cursor]: Live in-flight dv used vs planned per leg + log big differences (P6/F8).
- [x] RK5i [BIG - Grok/Cursor]: Save/load chosen mission per craft (MiniJson, AtomicFile) (P7).

### 6. Burns tab (tablet tab 1) + nodes
- [ ] RK6a: NodeReader - create KSPChatMod/NodeReader.cs (new), `internal static class NodeReader` with `internal static List<string> Lines(Vessel v)`: for each ManeuverNode n in v.patchedConicSolver.maneuverNodes (null-safe): "dv {n.DeltaV.magnitude:F0} m/s at T-{n.UT-Planetarium.GetUniversalTime():F0} s". Files: OrbitMath.cs (template), NodeReader.cs (new). Check: builds.
- [ ] RK6b: Burns stub - create KSPChatMod/TabletBurns.cs by copying TabletTrim.cs (template); method `void TabletBurns(Rect area)`, scroll `tabletBurnsScroll`; one CardPanel "BURNS" with one TabletUi.Label per NodeReader.Lines(FlightGlobals.ActiveVessel) (null-check), or "No maneuver nodes". Files: TabletTrim.cs (template), TabletBurns.cs (new). Check: builds.
- [ ] RK6c: Burns wire - in KSPChatMod/TabletWindow.cs add ONE line `case 1: TabletBurns(area); break;` directly ABOVE the line `case 2: TabletMap(area); break;`. Nothing else. Files: TabletWindow.cs. Check: Burns tab lists nodes.
- [ ] RK6d: Burn time on cards - in KSPChatMod/NodeReader.cs extend each line with "burn N s" using RocketMath.BurnTime(dv, v.totalMass*1000, thrust, isp) with thrust/isp from LiveRocket.Read(v) (Thrust; isp = Thrust/(FuelFlow*9.80665)). Files: NodeReader.cs, LiveRocket.cs (read only). Check: burn time close to stock.
- [ ] RK6e: NodeWriter - create KSPChatMod/NodeWriter.cs (new), `internal static ManeuverNode Add(Vessel v, double ut, double radial, double normal, double prograde)`: n = v.patchedConicSolver.AddManeuverNode(ut); n.DeltaV = new Vector3d(radial, normal, prograde); v.patchedConicSolver.UpdateFlightPlan(); ChatLog.Write("rocket", "node at UT .. dv .."). Null-safe. Files: OrbitMath.cs (template), NodeWriter.cs (new). Check: builds.
- [ ] RK6f: Circularize node - in KSPChatMod/NodeWriter.cs add `internal static string CircularizeAtAp(Vessel v)`: o=v.orbit; dv = RocketMath.CircularizeAtAp(o.referenceBody.gravParameter, o.ApR, o.PeR); Add(v, Planetarium.GetUniversalTime()+o.timeToAp, 0, 0, dv); return a short line. Files: NodeWriter.cs. Check: node appears at Ap.
- [ ] RK6g: Burns buttons - in KSPChatMod/TabletBurns.cs add, when TabletLink, a TabletUi.Button "CIRC AT AP" calling ChatWindow.Notice(NodeWriter.CircularizeAtAp(FlightGlobals.ActiveVessel)). Files: TabletBurns.cs. Check: button grey with LIVE LINK off.
- [ ] RK6h: Hohmann node - in KSPChatMod/NodeWriter.cs add `internal static string TransferTo(Vessel v, CelestialBody target)` (same SOI only, else return "different SOI"): RocketMath.Hohmann + PhaseAngleDeg + WindowFinder.SecondsToPhase for the wait, then Add(..., 0, 0, dv1). Files: NodeWriter.cs. Check: Mun transfer node gives an encounter.
- [ ] RK6i: Capture node - in KSPChatMod/NodeWriter.cs add `internal static string CaptureAtPe(Vessel v, double apTarget)`: dv = -RocketMath.CaptureDv(mu, o.PeR, o.getOrbitalSpeedAt(...)-escape speed, apTarget) at UT of Pe; Add retrograde. Files: NodeWriter.cs. Check: node at Pe.
- [ ] RK6j: Warp to node - in KSPChatMod/NodeWriter.cs add `internal static void WarpToFirst(Vessel v, double burnTime, double margin)` using OrbitMath.WarpUt(now, node.UT, burnTime/2 + margin) and TimeWarp.fetch.WarpTo; ChatLog.Write it. Files: NodeWriter.cs. Check: warp stops before node.
- [x] N6 [BIG - Grok/Cursor]: Interplanetary ejection node (RocketMath ejection) + refine on patched conics until encounter.
- [x] N7 [BIG - Grok/Cursor]: Mid-course correction node to fix periapsis at target.

### 7. Graphs tab (tablet tab 3)
- [ ] RK7a: Graphs stub - create KSPChatMod/TabletGraphs.cs by copying TabletTrim.cs (template); method `void TabletGraphs(Rect area)`, scroll `tabletGraphsScroll`; one CardPanel "DV PER STAGE" and one TabletUi.Bar per StageInfo from StageReader.ForFlight() (frac = dv / max dv, text "S2 1234 m/s"). Files: TabletTrim.cs (template), TabletGraphs.cs (new). Check: builds.
- [ ] RK7b: Graphs wire - in KSPChatMod/TabletWindow.cs add ONE line `case 3: TabletGraphs(area); break;` directly BELOW the line `case 2: TabletMap(area); break;`. Nothing else. Files: TabletWindow.cs. Check: Graphs tab shows bars.
- [ ] G3 [BIG - Grok/Cursor]: Fuel/burn timeline (stages along time, burns marked).
- [ ] G4 [BIG - Grok/Cursor]: Transfer diagram (orbits, transfer ellipse, planet positions).
- [ ] G5 [BIG - Grok/Cursor]: Porkchop plot (needs RKM10).

### 8. Flight functions (NativeRocket partial)
- [ ] RK8a: Rocket partial stub - create KSPChatMod/NativeRocket.cs (new): `public partial class NativeFlightController` (same usings as qwen_ctx/RocketHook_snippet.txt) with field `string rocketMode = "off";` and `internal string RocketCommand(string name)` handling only "rocket_stop" (rocketMode = "off"; ChatLog.Write("rocket","stop"); return "Rocket autopilot off.") else return null. Do NOT open the big controller file. Files: NativeRocket.cs (new). Check: builds.
- [ ] RK8b: Point-at math - create KSPChatMod/PointAt.cs (new), `internal static Vector3d Dir(Vessel v, string mode)` for prograde, retro, normal, antinormal, radialin, radialout, target, node (orbit velocity / h vector / position from body / target / first node DeltaV burn vector); Vector3d.zero if unknown. Files: OrbitMath.cs (template), PointAt.cs (new). Check: builds.
- [ ] RK8c: Point-at PD - in KSPChatMod/NativeRocket.cs add `void RocketSteer(FlightCtrlState c, Vector3d dir)`: error axis = cross(vessel forward (transform.up), dir) in vessel frame; c.pitch/yaw/roll = clamp(kp*err - kd*angularVelocity, -0.5, 0.5) (mid-strength); kp 2, kd 0.8. Files: NativeRocket.cs, PointAt.cs (read only). Check: builds.
- [ ] RK8d: Point-at command - in KSPChatMod/NativeRocket.cs extend RocketCommand: name "rocket_point" with arg mode sets rocketMode = "point:"+mode, ChatLog.Write("rocket", "point " + mode), returns "Pointing " + mode + ".". Add method `internal bool RocketFly(FlightCtrlState c)`: if rocketMode starts with "point:" call RocketSteer(c, PointAt.Dir(vessel, rest)) and return true; else false. Files: NativeRocket.cs. Check: builds.
- [ ] RK8e [BIG - Grok/Cursor]: Hook NativeRocket into NativeFlightController.cs (1399 lines): `if (RocketFly(c)) return;` near top of Fly, rocket_* routing to RocketCommand in Command/Execute, names in NativeCommands.Ported.
- [ ] RK8f: Auto-staging - in KSPChatMod/NativeRocket.cs add `double rocketEmptyT = -1;` and `void RocketAutoStage()`: flameout = any ignited engine flameout; when StagingPolicy.ShouldStage(...) returns a reason: StageManager.ActivateNextStage(); ChatWindow.Notice("Stage " + vessel.currentStage + " empty, staging."); ChatLog.Write("rocket", "stage: " + reason). Files: NativeRocket.cs, StagingPolicy.cs (read only). Check: builds.
- [ ] RK8g: Fairings - in KSPChatMod/NativeRocket.cs add `bool rocketFairingsDone; void RocketFairings(LiveRocket s)`: when FairingPolicy.CanDeploy(s.QKpa, s.Alt, s.MaxTempRatio) returns a reason, call DeployFairing() on every ModuleProceduralFairing once; log the reason. Files: NativeRocket.cs, FairingPolicy.cs (read only). Check: builds.
- [ ] RK8h: Abort monitor - in KSPChatMod/NativeRocket.cs add `void RocketAbortCheck(FlightCtrlState c, LiveRocket s, double attErrDeg, double thrustDrop)`: reason = AbortPolicy.Check(...); if not null: c.mainThrottle = 0; SetGroup(vessel, KSPActionGroup.Abort, true); ChatWindow.Notice(IntercomTalk.Fmt("Jebediah","Pilot","Abort! " + reason)); ChatLog.Write("rocket","abort: "+reason); rocketMode = "off". Files: NativeRocket.cs, AbortPolicy.cs (read only). Check: builds.
- [ ] RK8i: Node executor - in KSPChatMod/NativeRocket.cs add mode "node": RocketFly points at PointAt.Dir(vessel,"node"); throttle 0 until UT - burnTime/2; then 1, easing to dvLeft/(accel*1) in the last second; stop and remove node at dvLeft < 0.1; log start and end. Only this mode. Files: NativeRocket.cs. Check: builds.
- [x] F5 [BIG - Grok/Cursor]: Ascent guidance (AscentProfile from live TWR, throttle near max q, coast to Ap, circularize node).
- [x] F2 [BIG - Grok/Cursor]: MFD cockpit point-at buttons (AicsMfd, replace mjatt buttons when MechJeb absent).

### 9. Landing (airless first)
- [ ] RK9a: Deorbit node - in KSPChatMod/NodeWriter.cs add `internal static string DeorbitBelow(Vessel v)`: retrograde node now+60 s with dv = OrbitMath.ApsisChangeDv(mu, r, a, radius*0.98) (Pe below surface). Log. Files: NodeWriter.cs. Check: Pe under ground.
- [ ] RK9b: Suicide burn mode - in KSPChatMod/NativeRocket.cs add mode "land": each tick s = LiveRocket.Read(vessel); start = SuicideBurn.StartAlt(-s.Vs, s.LocalG, s.Thrust, s.Mass*1000, 0.15); point retro (surface); throttle 1 when s.RadarAlt < start else 0. Files: NativeRocket.cs, SuicideBurn.cs (read only). Check: builds.
- [ ] RK9c: Touchdown - in KSPChatMod/NativeRocket.cs mode "land": below 20 m radar alt hold Vs -2 m/s with throttle = clamp((LocalG + (-2 - Vs))*Mass/Thrust, 0, 1), gear down once (SetGroup Gear), stop when vessel.Landed. Files: NativeRocket.cs. Check: builds.
- [x] L3 [BIG - Grok/Cursor]: Slope check + sideways nudge.
- [x] L5 [BIG - Grok/Cursor]: Atmosphere landing (live drag, chutes, final burn).
- [x] L6 [BIG - Grok/Cursor]: Land at map-click target.

### 10. Later / BIG
- [ ] R1 [BIG - Grok/Cursor]: Rendezvous.
- [ ] R2 [BIG - Grok/Cursor]: Docking.

### 11. Campaigns (Luke, Oct 13)
- [ ] RK11a: Campaign model - create KSPChatMod/Campaign.cs (new), System only: `internal sealed class CampaignPhase { public string Goal, Craft, Status, VesselId; public double Alt, Inc; }` and `internal sealed class Campaign { public string Name; public List<CampaignPhase> Phases = new List<CampaignPhase>(); }`. Files: OrbitMath.cs (template), Campaign.cs (new). Check: builds.
- [ ] RK11b: Campaign json - in KSPChatMod/Campaign.cs add `internal Dictionary<string, object> ToJson()` and `static Campaign FromJson(Dictionary<string, object>)` (for MiniJson). Files: Campaign.cs. Check: builds.
- [ ] C2 [BIG - Grok/Cursor]: Tablet Campaign tab (create/edit/reorder phases).
- [ ] C3 [BIG - Grok/Cursor]: Per-phase dv check reusing Mission Planner.
- [ ] C4 [BIG - Grok/Cursor]: Track vessels per phase.
- [ ] C5 [BIG - Grok/Cursor]: Launch timing to an earlier vessel.
- [ ] C6 [BIG - Grok/Cursor]: Crew chatter at phase start/complete.
- [ ] C7 [BIG - Grok/Cursor]: Run a phase end-to-end.

### 12. Tablet info screens (Luke, Oct 13)
- [ ] RK12a: Body page - create KSPChatMod/TabletBodyInfo.cs by copying TabletTrim.cs (template); method `void TabletBodyInfo(Rect area)`; one CardPanel per CelestialBody in FlightGlobals.Bodies with a TabletUi.Label of BodyInfo.Line(b). Not wired yet. Files: TabletTrim.cs (template), TabletBodyInfo.cs (new). Check: builds.
- [ ] RK12b: Calculator - create KSPChatMod/TabletCalc.cs by copying TabletTrim.cs (template); fields calcR1T, calcR2T, calcBodyT; Fields + Labels showing RocketMath.Hohmann dv1/dv2/time for BodyInfo.Find(calcBodyT). Not wired yet. Files: TabletTrim.cs (template), TabletCalc.cs (new). Check: builds.
- [ ] RK12c: Resources lines - create KSPChatMod/ResourceLines.cs (new), `internal static List<string> Lines(Vessel v)`: for LiquidFuel, Oxidizer, MonoPropellant, ElectricCharge, Ore: v.GetConnectedResourceTotals(id, out amt, out max) -> "LiquidFuel 120/400". Files: OrbitMath.cs (template), ResourceLines.cs (new). Check: builds.
- [x] RK12d: Crew lines - create KSPChatMod/CrewLines.cs (new), `internal static List<string> Lines()`: HighLogic.CurrentGame.CrewRoster.Crew -> "name trait L{experienceLevel} {rosterStatus}". Files: OrbitMath.cs (template), CrewLines.cs (new). Check: builds.
- [ ] I1 [BIG - Grok/Cursor]: Full body encyclopedia (biomes, peaks, landing dv) - Encyclopedia tab exists.
- [ ] I3 [BIG - Grok/Cursor]: Ship info page extension.
- [ ] I4 [BIG - Grok/Cursor]: Science tracker.
- [ ] I5 [BIG - Grok/Cursor]: Comms page.
- [ ] I8 [BIG - Grok/Cursor]: Contracts page.
- [ ] I9 [BIG - Grok/Cursor]: Dv map.
- [x] I10 [BIG - Grok/Cursor]: Flight log page.
- [x] I1a [BIG - Grok/Cursor]: Planet live-view camera (RenderTexture, own blurbs, never copy KSPedia).
