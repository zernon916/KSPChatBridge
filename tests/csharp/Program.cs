using System;
using System.Collections.Generic;
using System.Threading;
using KSPChatBridge;

class Program
{
    static string Root() { var d = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "personalities.txt"))) d = d.Parent; return d == null ? "." : d.FullName; }
    class Servo { private float transformRateOfMotion = 380; }
    class Rotor : Servo { public float currentRPM = 0; }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static void Main()
    {
        var p = new RestartPolicy();
        Check(p.CanStart(0, true, false, false, false), "initial launch");
        Check(!p.CanStart(0, false, false, false, false), "autostart off");
        Check(!p.CanStart(0, true, true, false, false), "quit");
        Check(!p.CanStart(0, true, false, true, false), "already starting");
        Check(!p.CanStart(0, true, false, false, true), "alive but unhealthy");
        p.Failed(0);
        Check(!p.CanStart(1, true, false, false, false), "backoff");
        Check(p.CanStart(2, true, false, false, false), "retry");
        p.Failed(2);
        Check(!p.CanStart(5, true, false, false, false), "increased backoff");
        for (int i = 0; i < 20; i++) p.Failed(10);
        Check(p.CanStart(70, true, false, false, false), "bounded backoff");
        p.Stable(); p.Failed(100);
        Check(p.CanStart(102, true, false, false, false), "stable reset");
        Console.WriteLine("Restart policy: 10 behavior checks passed.");
        Check(RotorMeasurements.Rpm(new Rotor()) == 380, "physics RPM beats stale UI field");
        Check(float.IsNaN(RotorMeasurements.Rpm(new object())), "missing measurement is unknown");
        Console.WriteLine("Rotor measurement: 2 behavior checks passed.");
        Check(RotorPlacement.Label(0, 0, 0, 0, 1, 1) == "MR - Main Rotor", "single main");
        Check(RotorPlacement.Label(0, -3, 1, 0, 0, 1) == "TR - Tail Rotor", "tail");
        Check(RotorPlacement.Label(-1, 1, 0, 0, 1, 4) == "LF - Left Front", "quad LF");
        Check(RotorPlacement.Label(1, 1, 0, 0, 1, 4) == "RF - Right Front", "quad RF");
        Check(RotorPlacement.Label(-1, -1, 0, 0, 1, 4) == "LR - Left Rear", "quad LR");
        Check(RotorPlacement.Label(1, -1, 0, 0, 1, 4) == "RR - Right Rear", "quad RR");
        Check(RotorPlacement.Label(0, -1, 0, 0, 1, 3) == "R - Rear", "tricopter rear");
        Check(RotorPlacement.Label(0, 0, 0, 0, 1, 2) == "C - Center", "coax center");
        Check(RotorPlacement.Label(0, 1, 0, 1, 0, 0) == "PU - Pusher/Puller", "forward axis");
        Check(RotorPlacement.Status(380, 400, true, 0, true) == "ok", "normal RPM");
        Check(RotorPlacement.Status(0, 400, true, 0, true) == "fail", "RPM lost");
        Check(RotorPlacement.Status(double.NaN, 400, true, 0, true) == "caution", "RPM unknown");
        Check(RotorPlacement.LabelFromVesselFrame(0, 0, 0, 0, 0, 1, 1) == "MR - Main Rotor", "frame map main");
        Check(RotorPlacement.LabelFromVesselFrame(-1, 1, 0, 0, 0, 1, 4) == "LF - Left Front", "frame map quad LF");
        Check(RotorPlacement.IsLiftRotorAxis(1) && !RotorPlacement.IsLiftRotorAxis(.5), "lift axis threshold");
        Console.WriteLine("Placement and rotor status: 15 behavior checks passed.");
        var lease = new ControlLease();
        Check(lease.Acquire("v1", "hold"), "acquire hold");
        Check(!lease.Acquire("v1", "land"), "exclusive ownership");
        lease.VesselChanged("v2"); Check(lease.Owner == null, "vessel reset");
        Check(lease.Acquire("v2", "land"), "new vessel acquire");
        lease.Release(); Check(lease.Owner == null, "abort release");
        double? capture = null;
        Check(FlightPolicy.VerticalSpeed(1000, 1000, 25, 15, 1, 150, ref capture) == 0 && capture == 1000, "capture altitude");
        Check(FlightPolicy.VerticalSpeed(800, 1000, 25, 15, 1, 150, ref capture) > 0 && capture == null, "leave band");
        double eased = FlightPolicy.VerticalSpeed(800, 1000, 25, 15, 1, 150, ref capture);
        Check(eased < FlightPolicy.VerticalSpeed(700, 1000, 25, 15, 1, 150, ref capture), "ease band approach");
        Check(FlightPolicy.BankLimit(100) == 20 && FlightPolicy.BankLimit(300) == 10, "bank policy");
        double last = 0;
        Check(FlightPolicy.Throttle(.5, 1, true, 2, ref last) == .5, "spool delay");
        Check(Math.Abs(FlightPolicy.Throttle(.5, 1, true, 3, ref last) - .55) < 1e-9, "five percent step");
        Check(FlightPolicy.Throttle(.05, 0, true, 6, ref last) == .05, "airborne floor");
        Check(FlightPolicy.SurfaceSign(-2, false, false) == 1, "rear trim sign");
        Check(FlightPolicy.SurfaceSign(2, false, false) == -1, "canard trim sign");
        Check(FlightPolicy.SurfaceSign(-2, true, false) == -1, "inverted trim sign");
        Check(FlightPolicy.SurfaceSign(-2, true, true) == 1, "double inverted trim sign");
        Console.WriteLine("Flight policy and ownership: 16 behavior checks passed.");
        var spool = new RotorSpool(new[] { "left", "right" }, 0);
        var measurements = new[] {
            new RotorSpool.Measurement { Id="left", Rpm=460, Limit=460, Torque=100, Brake=0, Motor=true },
            new RotorSpool.Measurement { Id="right", Rpm=460, Limit=460, Torque=100, Brake=0, Motor=true }
        };
        Check(spool.Torque(0) == 0 && spool.Torque(3) == 60 && spool.Torque(8) == 100, "torque ramp");
        Check(!spool.Tick(4, 1, measurements), "wait for ramp");
        Check(!spool.Tick(5, 2, measurements), "first sample");
        Check(!spool.Tick(6, 2, measurements), "duplicate sample");
        Check(spool.Tick(6, 3, measurements), "two fresh good samples");
        spool = new RotorSpool(new[] { "left", "right" }, 0);
        Check(!spool.Tick(6, 1, new[] { measurements[0] }) && spool.Result.Contains("disappeared"), "missing rotor");
        spool = new RotorSpool(new[] { "left" }, 0);
        measurements[0].Rpm = double.NaN;
        Check(!spool.Tick(6, 1, measurements) && !spool.Tick(7, 2, measurements), "unknown RPM");
        Check(!spool.Tick(51, 3, measurements) && spool.Result.Contains("timeout"), "bounded spool failure");
        var collective = new CollectiveController();
        double previous = 4;
        for (int i = 0; i < 100; i++) { double output = collective.Step(3, 0, .1, false); Check(output <= 30 && output >= 2 && Math.Abs(output-previous) <= .300001, "collective clamp/slew"); previous = output; }
        Console.WriteLine("Rotor spool: 8 checks; collective: 100 bounded steps passed.");
        var takeoff = new TakeoffMission(0);
        Check(takeoff.Step(1, 20, 0, true, 45) == 0 && takeoff.Phase == "roll", "takeoff roll");
        Check(takeoff.Step(3, 60, 0, true, 45) == 9 && takeoff.Phase == "rotate", "rotation margin");
        Check(takeoff.Step(4, 45, 10, false, 45) == 1, "airborne stall margin");
        takeoff.Step(6, 80, 110, false, 45); Check(takeoff.Phase == "climbout complete", "climbout handoff");
        takeoff = new TakeoffMission(0); takeoff.Step(121, 20, 0, true, 45); Check(takeoff.Phase == "takeoff timeout", "bounded takeoff");
        Check(NavigationMath.Distance(0, 0, 0, 0, 600000) == 0, "coincident distance");
        Check(Math.Abs(NavigationMath.Bearing(0, 0, 0, 1) - 90) < 1e-6, "east bearing");
        var landing = new RunwayMission { Lat=0, Lon=0, EndLat=0, EndLon=.2, Elevation=70, Phase="final" };
        landing.Step(0, -.1, 130, 60, 65, false, 600000, 45);
        Check(landing.Gear && landing.DesiredSpeed == 58.5 && landing.DesiredVs >= -4.5, "final speed/sink/gear");
        landing.Step(0, -.005, 75, 5, 50, false, 600000, 45); Check(landing.Phase == "flare" && landing.DesiredVs == -1, "flare");
        landing.Step(0, .02, 70, 0, 40, true, 600000, 45); Check(landing.Phase == "rollout" && landing.Brakes, "touchdown");
        landing.Step(0, .04, 70, 0, .5, true, 600000, 45); Check(landing.Phase == "stopped", "stopped");
        landing.Phase="final"; landing.Step(0, .2, 120, 50, 65, false, 600000, 45); Check(landing.Phase == "go around", "missed touchdown go around");
        Console.WriteLine("Takeoff, navigation, approach: 12 behavior checks passed.");
        var plan = NativePlan.Parse("takeoff\nclimb 1.5 km agl\ncruise hdg 090 speed 150 for 2 min\ncircle 1 laps left bank 15\nland Runway 27");
        Check(plan.Steps.Count == 5 && plan.Steps[1].Altitude == 1500 && plan.Steps[2].Seconds == 120, "plan units/template");
        plan.Start(); plan.Advance(); plan.Entered = true; plan.Elapsed = 12; plan.Pause();
        Check(plan.Paused && !plan.Running && plan.Index == 1 && plan.Entered, "pause retains active step");
        plan.Start(); Check(plan.Running && plan.Elapsed == 12, "resume retains elapsed");
        plan.Interrupt(); Check(!plan.Entered && !plan.Running && plan.Index == 1, "manual override requires reentry");
        plan.Start(); while (plan.Running) plan.Advance(); plan.Pause();
        Check(!plan.Paused && plan.Status.StartsWith("Complete"), "completed plan not resumable beyond end");
        plan.Start(); Check(plan.Index == 0 && plan.Running && !plan.Entered, "completed plan restarts cleanly");
        foreach (string invalid in new[] { "climb 500 vs 100", "cruise speed 150 garbage", "wait", "wait 0 s", "circle 0 laps", "cruise heading 999", "cruise speed 300", "land nowhere", "climb NaN", "wait 999999 min" })
        {
            bool rejected = false; try { NativePlan.Parse(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "reject unsupported plan: " + invalid);
        }
        Console.WriteLine("Plan parsing and lifecycle: 16 behavior checks passed.");
        Check(!FlightPolicy.RotorPowerReady(.249), "rotor minimum charge");
        Check(FlightPolicy.RotorPowerReady(.25), "rotor charge boundary");
        Check(!FlightPolicy.RotorPowerReady(double.NaN), "unknown charge blocks spool");
        Console.WriteLine("Rotor power: 3 behavior checks passed.");
        Check(FlightPolicy.SafeSolar(true, 220, 12000), "solar boundary");
        Check(!FlightPolicy.SafeSolar(true, 221, 100), "solar speed guard");
        Check(!FlightPolicy.SafeSolar(true, 100, 12001), "solar pressure guard");
        Check(FlightPolicy.SafeSolar(false, 2500, 0), "solar in vacuum");
        Check(!FlightPolicy.SafeSolar(true, double.NaN, 100), "unknown speed blocks solar");
        Console.WriteLine("Solar policy: 5 behavior checks passed.");
        Check(EngineRestart<int>.Eligible(false, true, 5, 4), "relight fired stage");
        Check(!EngineRestart<int>.Eligible(false, true, 3, 4), "do not ignite future stage");
        Check(!EngineRestart<int>.Eligible(true, true, 5, 4), "leave flameout active engine alone");
        Check(!EngineRestart<int>.Eligible(false, false, 5, 4), "nonrestartable engine");
        var restart = new EngineRestart<int>(); int activated = 0;
        Check(restart.Schedule(new[] { 1, 2 }, 0), "schedule restart");
        Check(!restart.Schedule(new[] { 3 }, 2), "duplicate request retains deadline");
        Check(restart.Tick(3, e => true, e => activated++) == 0, "restart waits");
        Check(restart.Tick(4, e => e == 1, e => activated++) == 1 && activated == 1, "recheck removed or changed engines");
        Check(restart.Tick(5, e => true, e => activated++) == 0, "restart once");
        restart.Schedule(new[] { 1 }, 6); restart.Cancel();
        Check(restart.Tick(10, e => true, e => activated++) == 0, "abort cancels restart");
        Console.WriteLine("Engine restart: 10 behavior checks passed.");
        var recovery = new RecoveryGate();
        Check(!recovery.Tick(false, 0), "unchanged configuration");
        Check(!recovery.Tick(true, 1) && !recovery.Tick(true, 3.9), "recovery delay");
        Check(recovery.Tick(true, 4), "stable mismatch restores");
        recovery.Reset(); Check(!recovery.Tick(true, 5), "authorized change resets delay");
        Check(!recovery.Tick(false, 6) && !recovery.Tick(true, 8), "self corrected mismatch cancels");
        Check(!recovery.Tick(true, 0), "clock reset restarts delay");
        Console.WriteLine("Configuration recovery delay: 6 behavior checks passed.");
        Check(ReversePolicy.ForwardPrimary("forward", "Reverse thrust") == true, "secondary reverser");
        Check(ReversePolicy.ForwardPrimary("reverse", "forward") == false, "primary reverser");
        Check(ReversePolicy.ForwardPrimary("AirBreathing", "ClosedCycle") == null, "normal mode switch preserved");
        Check(ReversePolicy.ForwardPrimary("reverse A", "reverse B") == null, "ambiguous mode left alone");
        Console.WriteLine("Reverse mode classification: 4 behavior checks passed.");
        var vessel = new Vessel(); var part = new Part { vessel = vessel }; vessel.parts.Add(part);
        var surface = new ModuleControlSurface { part = part, deployAngle = 2 };
        var intake = new ModuleResourceIntake { part = part };
        var engine = new ModuleEngines { part = part };
        var multi = new MultiModeEngine { part = part };
        part.Modules.AddRange(new PartModule[] { surface, intake, engine, multi });
        var safeguards = new NativeRecovery(vessel);
        surface.deployInvert = true; surface.ignorePitch = true; intake.intakeEnabled = false;
        engine.thrustPercentage = 0; multi.runningPrimary = false;
        Check(safeguards.Tick(vessel, 0, true) == 0 && !safeguards.CanTrim(surface), "pending recovery blocks baseline adoption");
        Check(safeguards.Tick(vessel, 3, true) == 4, "restore changed modules by identity");
        Check(!surface.deployInvert && !surface.ignorePitch && intake.intakeEnabled && engine.thrustPercentage == 100 && multi.runningPrimary, "configuration actually restored");
        surface.deployAngle = 3; safeguards.AcceptSurface(surface);
        Check(safeguards.Tick(vessel, 7, true) == 0 && safeguards.CanTrim(surface), "authorized trim retained");
        surface.deployAngle = 9; safeguards.Tick(vessel, 8, true); safeguards.Tick(vessel, 12, false);
        Check(safeguards.Tick(vessel, 13, true) == 0 && surface.deployAngle == 9, "manual release cancels pending restoration");
        part.vessel = new Vessel();
        Check(safeguards.Tick(vessel, 20, true) == 0 && surface.deployAngle == 9, "detached parts never restored");
        Console.WriteLine("Configuration recovery game adapter: 6 behavior checks passed.");
        part.vessel = vessel;
        var named = new PartModule { part = part };
        var reverseEvent = new BaseEvent { guiName = "Reverse Thrust" };
        var forwardEvent = new BaseEvent { guiName = "Forward Thrust", active = false };
        reverseEvent.action = () => { reverseEvent.active = false; forwardEvent.active = true; };
        forwardEvent.action = () => { forwardEvent.active = false; reverseEvent.active = true; };
        named.Events.AddRange(new[] { reverseEvent, forwardEvent }); part.Modules.Add(named);
        var reversers = new NativeReversers(vessel);
        Check(reversers.Set(vessel, true) && !multi.runningPrimary && forwardEvent.active, "named and multimode reverse");
        Check(reversers.Recover(vessel, 0, true) == 0, "reverse recovery delay");
        Check(reversers.Recover(vessel, 3, true) == 2 && multi.runningPrimary && reverseEvent.active, "forward recovery confirmed");
        Check(ReversePolicy.EventDirection("Toggle Thrust Reverser") == null, "unknown toggle never guessed");
        Check(ReversePolicy.EventDirection("Forward Thrust") == false, "forward event identified");
        Check(ReversePolicy.EventDirection("Reverse Thrust") == true, "reverse event identified");
        Console.WriteLine("Custom reversers and game adapter: 6 behavior checks passed.");
        var rollout = new ReverseRollout(); bool reverseOn = false;
        Func<bool, bool> switchReverse = value => { reverseOn = value; return true; };
        Check(rollout.Tick(true, 60, 0, switchReverse) == 0 && reverseOn, "reverse before adding thrust");
        Check(rollout.Tick(true, 55, 2, switchReverse) == 0, "reverse throttle settle");
        Check(rollout.Tick(true, 50, 3, switchReverse) == .05, "reverse throttle five percent");
        Check(rollout.Tick(true, 30, 4, switchReverse) == 0 && !reverseOn, "cut reverse before taxi");
        Check(rollout.Tick(true, 60, 5, switchReverse) == 0 && !reverseOn, "no reverse rearm");
        rollout = new ReverseRollout(); rollout.Tick(true, 60, 0, switchReverse);
        Check(rollout.Tick(false, 60, 1, switchReverse) == 0 && !reverseOn, "bounce cancels reverse");
        var plainPart = new Part { vessel = vessel }; plainPart.Modules.Add(new ModuleEngines { part = plainPart }); vessel.parts.Add(plainPart);
        Check(!reversers.Set(vessel, true) && multi.runningPrimary, "mixed engines cannot use global reverse throttle");
        Console.WriteLine("Reverse rollout: 7 behavior checks passed.");
        var resource = new PartResource { part = part }; part.Resources.Add(resource);
        safeguards = new NativeRecovery(vessel);
        vessel.ActionGroups.SetGroup(KSPActionGroup.Light, true);
        vessel.ActionGroups.SetGroup(KSPActionGroup.Custom01, true);
        vessel.ActionGroups.SetGroup(KSPActionGroup.Stage, true); resource.flowState = false;
        safeguards.Tick(vessel, 0, true);
        Check(safeguards.Tick(vessel, 3, true) == 3 && resource.flowState, "flow and group configuration restored");
        Check(!vessel.ActionGroups[KSPActionGroup.Light] && !vessel.ActionGroups[KSPActionGroup.Custom01] && vessel.ActionGroups[KSPActionGroup.Stage], "staging is never replayed");
        vessel.ActionGroups.SetGroup(KSPActionGroup.Gear, true); safeguards.AcceptGroup(vessel, KSPActionGroup.Gear, true);
        Check(safeguards.Tick(vessel, 7, true) == 0 && vessel.ActionGroups[KSPActionGroup.Gear], "own gear command preserved");
        resource.flowState = false; safeguards.Tick(vessel, 8, true); part.vessel = null;
        Check(safeguards.Tick(vessel, 12, true) == 0 && !resource.flowState, "lost resource owner excluded");
        Console.WriteLine("Action groups and resource recovery: 4 behavior checks passed.");
        Check(FlightPolicy.Trim(.1, .01) == .1, "neutral trim deadband");
        Check(Math.Abs(FlightPolicy.Trim(.1, .3) - .115) < 1e-9, "trim toward neutral stick");
        Check(FlightPolicy.Trim(1, 1) == 1 && FlightPolicy.Trim(-1, -1) == -1, "trim bounded");
        Console.WriteLine("Axis trim: 3 behavior checks passed.");
        double offsetLat, offsetLon;
        NavigationMath.Offset(85, 179, 30, 50000, 600000, out offsetLat, out offsetLon);
        Check(Math.Abs(NavigationMath.Distance(85, 179, offsetLat, offsetLon, 600000) - 50000) < .001, "spherical offset near pole");
        Check(offsetLon >= -180 && offsetLon <= 180, "longitude wraps");
        int terrainSamples = 0;
        double terrain = NavigationMath.TerrainAhead(0, 0, 90, 100, 600000, (lat, lon) => { terrainSamples++; return terrainSamples == 3 ? 1200 : 30; });
        Check(terrainSamples == 5 && terrain == 1200, "terrain high point ahead");
        Check(double.IsNaN(NavigationMath.TerrainAhead(0, 0, 0, 100, 600000, (lat, lon) => double.NaN)), "unknown terrain remains unknown");
        Console.WriteLine("Terrain and spherical navigation: 4 behavior checks passed.");
        var taxi = new TaxiMission("0,0.1", "Mun", 8);
        taxi.Step(0, 0, 0, 90, 0, 600000, true, true);
        Check(taxi.Drive == 1 && !taxi.Brakes && Math.Abs(taxi.Wheel) < .001, "powered taxi straight ahead");
        taxi.Step(1, 0, 0, 0, 10, 600000, true, true);
        Check(taxi.Brakes && taxi.Drive < 0 && taxi.Wheel == -1, "taxi slows for turn");
        taxi.Step(2, 0, .1, 90, 0, 600000, true, true);
        Check(taxi.Result.Contains("arrived") && taxi.Brakes && taxi.Drive == 0, "taxi arrival stops");
        taxi = new TaxiMission("0,0.1", "Mun", 8); taxi.Step(0, 0, 0, 90, 0, 600000, true, false);
        Check(taxi.Throttle == .05 && taxi.Drive == 0, "engine taxi throttle steps");
        taxi.Step(2000, 0, 0, 90, 0, 600000, true, false);
        Check(taxi.Result.Contains("timeout") && taxi.Throttle == 0, "stuck taxi timeout");
        taxi = new TaxiMission("0,0.1", "Mun", 8); taxi.Step(0, 0, 0, 90, 0, 600000, false, true);
        Check(taxi.Result.Contains("not on the ground"), "taxi cannot fly");
        foreach (string route in new[] { "91,0", "NaN,0", "0,Infinity", "", "Runway 09 start" })
        {
            bool refused = false; try { new TaxiMission(route, "Mun", 8); } catch (ArgumentException) { refused = true; }
            Check(refused, "invalid taxi route " + route);
        }
        Console.WriteLine("Taxi: 11 behavior checks passed.");
        var legacySettings = new Dictionary<string, object> { { "altitude_band_m", 120 }, { "auto_trim_enabled", false }, { "openai_key", "test-secret" }, { "autoland_reversers", false } };
        var nativeSettings = NativeSettings.Migrate(new Dictionary<string, object> { { "altitude_band_m", 180 } }, legacySettings);
        Check((int)nativeSettings["altitude_band_m"] == 180 && !(bool)nativeSettings["auto_pitch"], "migration preserves native values and trim switch");
        Check(!nativeSettings.ContainsKey("openai_key") && !(bool)nativeSettings["autoland_reversers"], "migration excludes secrets");
        legacySettings["auto_trim_enabled"] = true;
        Check(!(bool)NativeSettings.Migrate(nativeSettings, legacySettings)["auto_pitch"], "migration once only");
        var localSpots = new NativeSpots(nativeSettings);
        localSpots.Save("Test Field", "Mun", "H", 0, 0, 90, 100);
        Check(localSpots.Point("test_field", "Mun").Lat == 0, "saved spot normalization");
        var strip = localSpots.Runway("Test Field", "Mun", 600000, (lat, lon) => double.NaN);
        Check(strip.Elevation == 100 && Math.Abs(NavigationMath.Distance(strip.Lat, strip.Lon, strip.EndLat, strip.EndLon, 600000) - 1000) < .001, "saved runway geometry");
        taxi = new TaxiMission("Test Field", "Mun", 8, name => localSpots.Point(name, "Mun"));
        Check(taxi.Points.Count == 1 && taxi.Points[0].Name == "Test Field", "saved taxi route");
        bool wrongBody = false; try { localSpots.Point("Test Field", "Kerbin"); } catch (ArgumentException) { wrongBody = true; }
        Check(wrongBody, "saved spot body isolation");
        bool builtinOverwrite = false; try { localSpots.Save("Runway 09", "Kerbin", "H", 0, 0, 90, 100); } catch (ArgumentException) { builtinOverwrite = true; }
        Check(builtinOverwrite, "built-in spot preserved");
        Check(!legacySettings.ContainsKey("landing_spots"), "legacy settings object preserved");
        Console.WriteLine("Settings migration and saved spots: 9 behavior checks passed.");
        plan = NativePlan.Parse("climb 3000 m agl vs 50\ncruise alt 3000 m for 12 km\ndescend 1000 m vs 10\nland Test Field", name => name == "Test Field");
        Check(plan.Steps[0].VerticalSpeed == 50 && plan.Steps[2].VerticalSpeed == -10, "plan climb descent rates");
        Check(plan.Steps[1].Altitude == 3000 && plan.Steps[1].Distance == 12000, "distance does not change altitude units");
        Check(plan.Steps[3].Destination == "Test Field", "plan saved destination");
        plan = NativePlan.Parse("taxi to Test Field", null, name => name == "Test Field");
        Check(plan.Steps[0].Op == "taxi" && plan.Steps[0].Destination == "Test Field", "plan taxi route");
        Console.WriteLine("Plan rates distance and saved routes: 4 behavior checks passed.");
        Check(FlightPolicy.WheelSteering(20, .5) == -.5 && FlightPolicy.WheelSteering(-20, .5) == .5, "wheel sign matches installed kRPC control mapping");
        var flapSchedule = new FlapSchedule();
        Check(flapSchedule.Step(0, 50, 45, 30) == 1 && flapSchedule.Step(1, 50, 45, 30) == 1, "flaps extend one step with delay");
        Check(flapSchedule.Step(2, 50, 45, 30) == 5 && flapSchedule.Step(4, 50, 45, 30) == 15, "flap sequencing");
        Check(flapSchedule.Step(4.1, 120, 45, 30) == 0, "overspeed retracts without waiting");
        var study = new StallStudy(0, 1000, 90); study.Step(10, 50, 15, -1, 950, 0);
        Check(study.Finished && study.Measured == 50, "stall measured at AoA threshold");
        study = new StallStudy(0, 1000, 90); study.Step(91, 100, 2, 0, 1000, 0);
        Check(study.Finished && study.Measured == null, "stall timeout is not measurement");
        study = new StallStudy(0, 1000, 90); study.Step(5, 60, 16, -9, 200, 0);
        Check(study.Finished && study.Measured == null, "stall study terrain guard");
        var flapVessel = new Vessel(); var flapPart = new Part { vessel = flapVessel }; flapPart.partInfo.title = "Dedicated flap";
        flapVessel.parts.Add(flapPart); var flap = new ModuleControlSurface { part = flapPart }; flapPart.Modules.Add(flap);
        var flapRecovery = new NativeRecovery(flapVessel); var nativeFlaps = new NativeFlaps(flapVessel);
        nativeFlaps.Tick(flapVessel, flapRecovery, 0, 50, 45, 30);
        Check(flap.deploy && flap.deployAngle == 1 && flapRecovery.CanTrim(flap), "flap change is authorized configuration");
        Console.WriteLine("Landing flap and stall policies: 8 behavior checks passed.");
        double forwardVelocity, rightVelocity;
        HelicopterPolicy.HoldVelocity(100, 90, 0, out forwardVelocity, out rightVelocity);
        Check(Math.Abs(forwardVelocity) < .001 && rightVelocity == 3, "hover position correction bounded");
        Check(HelicopterPolicy.Autorotation(100, -5, 400, 460) <= 6, "autorotation preserves RPM");
        Check(HelicopterPolicy.Autorotation(5, -2, 300, 460) == 21, "autorotation touchdown cushion");
        var yaw = new HelicopterYaw();
        Check(yaw.Step(20, 0, .1) > 0 && yaw.Step(0, 20, .1) < 0, "yaw error and rate damping");
        yaw.Observe(0, 1, 0); yaw.Observe(1, 1, -4); yaw.Observe(2, 1, -8);
        Check(yaw.Observe(3, 1, -12) && yaw.Sign == -1, "three adverse yaw windows reverse actuator sign");
        var rotorFailure = new RotorEmergency();
        Check(rotorFailure.Step(0, false, double.NaN, 460, false, false, false) == "normal", "unknown RPM is not fabricated failure");
        Check(rotorFailure.Step(1, true, 0, 460, false, false, false) == "normal", "rotor failure debounce");
        Check(rotorFailure.Step(2, true, 0, 460, false, false, false) == "autorotation", "power loss autorotation");
        Check(rotorFailure.Step(3, true, 0, 460, false, true, false).StartsWith("locked rotor"), "locked rotor cannot autorotate");
        Check(rotorFailure.Step(4, true, 450, 460, true, false, false) == "normal", "rotor power recovery");
        Console.WriteLine("Helicopter feedback and failure policies: 10 behavior checks passed.");
        var touchdown = new TouchdownGate();
        Check(!touchdown.Step(0, true, 0) && !touchdown.Step(2, true, 0), "touchdown waits");
        Check(!touchdown.Step(2.5, false, 1) && !touchdown.Step(3, true, 0), "bounce resets touchdown");
        Check(touchdown.Step(6, true, 0), "stable touchdown confirmed");
        collective = new CollectiveController();
        for (int i = 0; i < 20; i++) collective.Step(-.7, 0, .1, true);
        Check(collective.Step(-.7, 0, .1, true) == 0, "landed collective reaches zero");
        var rotorPark = new RotorPark(0);
        var parked = new[] { new RotorSpool.Measurement { Rpm = 100, Motor = false } };
        Check(rotorPark.Step(1, true, parked) == "wait", "do not brake spinning rotor");
        parked[0].Rpm = double.NaN; Check(rotorPark.Step(2, true, parked) == "wait", "unknown RPM blocks park");
        parked[0].Rpm = 19; Check(rotorPark.Step(3, true, parked) == "brake", "measured slow rotor can park");
        Check(rotorPark.Step(4, false, parked) == "cancel" && rotorPark.Step(31, true, parked) == "timeout", "parking cancellation and timeout");
        Console.WriteLine("Helicopter touchdown and parking: 8 behavior checks passed.");
        Check(HelicopterPolicy.RotorRole(1, 1, 0, 0) == "lift", "upward control point still identifies vertical lift axis");
        Check(HelicopterPolicy.RotorRole(0, 1, 0, -2) == "left" && HelicopterPolicy.RotorRole(0, 1, 0, 2) == "right", "compound side props");
        Check(HelicopterPolicy.RotorRole(0, 0, 1, 0) == "tail", "tail transverse axis");
        Check(HelicopterPolicy.RotorRole(.5, .5, .5, 0) == "canted", "ambiguous axis not forced into tail role");
        Console.WriteLine("Helicopter axis classification: 4 behavior checks passed.");
        var notes = new NativeCraftNotes(new Dictionary<string, object>());
        Check(!NativeCraftNotes.Named("Untitled Space Craft") && !NativeCraftNotes.Named(" "), "default craft notes refused");
        notes.Merge("Plane", new Dictionary<string, object> { { "pitch_trim", .1 }, { "quirks", "heavy nose" }, { "unrecognized", 7 } });
        notes.Merge("Plane", new Dictionary<string, object> { { "roll_trim", -.1 } });
        Check((double)notes.Load("Plane")["pitch_trim"] == .1 && notes.Load("Plane").ContainsKey("quirks"), "craft note merge retains fields");
        Check(!notes.Load("Plane").ContainsKey("unrecognized"), "craft note field allowlist");
        var noteCopy = notes.Load("Plane"); noteCopy["pitch_trim"] = 1;
        Check((double)notes.Load("Plane")["pitch_trim"] == .1 && notes.Load("Other").Count == 0, "craft note isolation");
        collective = new CollectiveController(8);
        Check(collective.Step(0, 0, .1, false) == 8, "saved hover collective baseline");
        Console.WriteLine("Craft notes and hover baseline: 5 behavior checks passed.");
        var departure = new TakeoffMission(0);
        departure.Step(1, 100, 101, true, 45);
        Check(departure.Phase != "climbout complete", "ground contact cannot enter approach after climbout");
        departure.Step(2, 100, 99, false, 45);
        Check(departure.Phase == "climbout", "approach waits for climbout clearance");
        departure.Step(3, 100, 100, false, 45);
        Check(departure.Phase == "climbout complete", "airborne climbout releases approach transition");
        departure = new TakeoffMission(0); departure.Step(121, 10, 0, true, 45);
        Check(departure.Phase == "takeoff timeout", "failed departure cannot release approach");
        bool verticalRejected = false;
        try { NativePlan.Parse("land Pad", name => { throw new ArgumentException("Saved point is not a runway."); }); }
        catch (ArgumentException) { verticalRejected = true; }
        Check(verticalRejected, "plan propagates runway validation before starting");
        Console.WriteLine("Departure and approach validation: 5 behavior checks passed.");
        Check(VerticalLandingPolicy.Feasibility(500, -2, 0, 20, 9.81, 1, .5) == null, "upright powered descent feasible");
        Check(VerticalLandingPolicy.Feasibility(20, -15, 0, 20, 9.81, 1, .05) != null, "throttle ramp clearance required");
        Check(VerticalLandingPolicy.Feasibility(500, -2, 0, 9, 9.81, 1, .5) != null, "underpowered descent refused");
        Check(VerticalLandingPolicy.Feasibility(500, -2, 4, 20, 9.81, 1, .5) != null, "lateral descent outside supported envelope");
        Check(VerticalLandingPolicy.Feasibility(double.NaN, -2, 0, 20, 9.81, 1, .5) != null, "unknown clearance refused");
        var descent = new VerticalLandingPolicy(.5, 1.5, 0);
        Check(descent.Step(1, 500, -2, 20, 9.81, 1, false) == .5, "vertical throttle settling time");
        double descentThrottle = descent.Step(3, 500, -2, 20, 9.81, 1, false);
        Check(Math.Abs(descentThrottle - .5) <= .050001, "vertical throttle step limit");
        Check(descent.Step(4, double.NaN, -2, 20, 9.81, 1, false) == descentThrottle, "sensor loss retains throttle");
        Check(descent.Step(5, 0, 0, 20, 9.81, 1, true) == 0 && descent.Phase == "touchdown", "contact cuts throttle");
        Check(descent.Step(6, 1, 0, 20, 9.81, 1, false) >= .05 && descent.Phase != "landed", "bounce resumes flight throttle");
        descent.Step(7, 0, 0, 20, 9.81, 1, true);
        Check(descent.Step(10, 0, 0, 20, 9.81, 1, true) == 0 && descent.Phase == "landed", "stable vertical touchdown");
        foreach (double gravity in new[] { 1.63, 9.81 })
        {
            double acceleration = gravity * 2, height = 500, velocity = 0, now = 0;
            descent = new VerticalLandingPolicy(.5, 1.5, 0);
            while (height > 0 && now < 500)
            {
                double output = descent.Step(now, height, velocity, acceleration, gravity, 1, false);
                velocity += (acceleration * output - gravity) * .05;
                height += velocity * .05; now += .05;
            }
            Check(height <= 0 && velocity > -3, "bounded simulated touchdown under gravity " + gravity);
        }
        Console.WriteLine("Powered descent policy: 13 behavior checks passed (idealized dynamics only).");
        Check(VerticalLandingPolicy.ClearanceFloor(100, 40) == 60, "terrain peak reduces clearance");
        Check(VerticalLandingPolicy.ClearanceFloor(100, double.NaN) == 100, "unknown terrain keeps clearance");
        var aborting = new VerticalLandingPolicy(.5, 1.5, 0);
        for (int i = 0; i < 3; i++)
        {
            Check(aborting.Step(i * 3, 200, -2, 1, 9.81, 1, false) == .5 && aborting.Phase == "insufficient thrust",
                "insufficient thrust holds throttle");
        }
        for (int i = 3; i < 5; i++) aborting.Step(i * 3, 200, -2, 1, 9.81, 1, false);
        Check(aborting.Phase == "abort" && aborting.Throttle == 0, "persistent thrust loss aborts descent");
        var single = HelicopterPolicy.Classify(new[] {
            new RotorDescriptor { Up = 1, Dir = 1, Blades = 4 },
            new RotorDescriptor { Up = 0, Right = 1, Side = 2, Dir = 1, Blades = 2 }
        }, false);
        Check(single.Heli && !single.Multirotor && single.Yaw == "tail rotor" && single.Kind.Contains("tail"), "main+tail layout");
        var quad = HelicopterPolicy.Classify(new[] {
            new RotorDescriptor { Up = 1, North = 1, East = -1, Dir = 1, Blades = 2 },
            new RotorDescriptor { Up = 1, North = 1, East = 1, Dir = -1, Blades = 2 },
            new RotorDescriptor { Up = 1, North = -1, East = -1, Dir = -1, Blades = 2 },
            new RotorDescriptor { Up = 1, North = -1, East = 1, Dir = 1, Blades = 2 }
        }, false);
        Check(quad.Multirotor && quad.Counter && quad.Yaw.Contains("differential"), "quad counter-rotating yaw");
        Check(HelicopterPolicy.LayoutLabel(quad, 0, -1).StartsWith("L"), "multirotor speech label");
        var soft = HelicopterPolicy.Classify(new[] {
            new RotorDescriptor { Up = .4, Forward = .9, Dir = 1, Blades = 2 },
            new RotorDescriptor { Up = .4, Forward = .9, Dir = -1, Blades = 2 },
            new RotorDescriptor { Up = .4, Forward = .9, Dir = 1, Blades = 2 }
        }, false);
        Check(soft.Multirotor && soft.Lift.Length >= 3, "sideways-hub soft lift reclassification");
        bool orbitRejected = false, tgRejected = false, flyRejected = false;
        try { NativePlan.Parse("ascent to 80 km"); } catch (ArgumentException) { orbitRejected = true; }
        try { NativePlan.Parse("land Runway 09 tg 2"); } catch (ArgumentException) { tgRejected = true; }
        try { NativePlan.Parse("fly to Island"); } catch (ArgumentException) { flyRejected = true; }
        Check(orbitRejected && tgRejected && flyRejected, "plan rejects orbital touch-and-go and fly-to");
        bool badTaxi = false;
        try { NativePlan.Parse("taxi to Nowhere", null, route => false); } catch (ArgumentException) { badTaxi = true; }
        Check(badTaxi, "plan taxi route validation");
        var controlLease = new ControlLease();
        Check(controlLease.Acquire("v1", "native") && !controlLease.Acquire("v1", "bridge") && !controlLease.Acquire("v2", "native"), "exclusive control lease");
        controlLease.Release();
        Check(controlLease.Acquire("v2", "native"), "lease reusable after release");
        controlLease.VesselChanged("v3");
        Check(controlLease.Owner == null && controlLease.Acquire("v3", "native"), "vessel switch clears lease");
        Console.WriteLine("Phase 3 integration policies: 12 behavior checks passed.");
        Check(NativeLibraryLayout.ForbiddenDlls(new[] { "GameData/KSPChatBridge/Plugins/KSPChatBridge.dll" }).Length == 0, "approved plugin DLL allowed");
        Check(NativeLibraryLayout.ForbiddenDlls(new[] { "GameData/KSPChatBridge/Bridge/llama.dll" }).Length == 1, "loose native DLL forbidden");
        Check(NativeLibraryLayout.ForbiddenDlls(new[] { "GameData/KSPChatBridge/PluginData/native/llama.bin" }).Length == 0, "encoded native bin allowed");
        Check(NativeLibraryLayout.ModelBundled(new[] { "GameData/KSPChatBridge/PluginData/models/x.gguf" }), "model bundle detector");
        Check(!NativeLibraryLayout.ModelBundled(new[] { "GameData/KSPChatBridge/Plugins/KSPChatBridge.dll" }), "release without model weights");
        string encoded = NativeLibraryLayout.EncodedPath("PluginData", "llama.dll");
        Check(encoded.EndsWith(".bin") && encoded.Replace('\\', '/').Contains("native/"), "native path rewritten to .bin");
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aics-model-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tmp);
        var models = new ModelManager(tmp, ModelManager.Sha256(CreateTempBytes(tmp, "partial", 64)), 32);
        System.IO.File.WriteAllBytes(models.PartialPath, System.IO.File.ReadAllBytes(System.IO.Path.Combine(tmp, "partial")));
        Check(models.FinalizeFromPartial(false) != null && !models.Ready, "AI-off blocks model finalize");
        Check(models.FinalizeFromPartial(true) == null && models.Ready && models.Phase == "ready", "atomic model finalize");
        var bad = new ModelManager(tmp, "deadbeef", 32);
        System.IO.File.WriteAllBytes(bad.PartialPath, new byte[40]);
        Check(bad.FinalizeFromPartial(true).Contains("Checksum"), "checksum failure");
        var runtime = new AiRuntimePolicy();
        Check(runtime.Apply(AiOffloadMode.Gpu, 20000, false, 0).Contains("CPU") && runtime.Offload == AiOffloadMode.Cpu, "GPU fallback");
        runtime = new AiRuntimePolicy();
        string unknownVram = runtime.Apply(AiOffloadMode.Hybrid, 16384, true, 0);
        Check(unknownVram != null && unknownVram.Contains("unknown") && runtime.Offload == AiOffloadMode.Cpu && runtime.GpuLayers == 0, "unknown VRAM hybrid fallback");
        runtime = new AiRuntimePolicy();
        Check(runtime.Apply(AiOffloadMode.Gpu, 20000, true, 0).Contains("unknown") && runtime.GpuLayers == 0, "unknown VRAM gpu fallback");
        runtime = new AiRuntimePolicy();
        Check(runtime.Apply(AiOffloadMode.Hybrid, 16384, true, 2L * 1024 * 1024 * 1024) == null && runtime.GpuLayers > 0, "hybrid budget accepted");
        string pathErr;
        Check(!NativeAiLoader.TryValidatePath(@"C:\KSP\GameData\KSPChatBridge\Bridge\llama.dll", out pathErr) && pathErr.Contains(".bin"), "reject non-bin native");
        Check(!NativeAiLoader.TryValidatePath(@"C:\KSP\GameData\KSPChatBridge\Plugins\evil.bin", out pathErr) && pathErr.Contains("PluginData"), "reject GameData outside PluginData");
        Check(NativeAiLoader.TryValidatePath(@"C:\KSP\GameData\KSPChatBridge\PluginData\native\llama.bin", out pathErr) && pathErr == null, "allow PluginData native bin");
        Check(AiRuntimePolicy.ProviderId("Groq") == "groq", "provider normalize");
        var chat = new ChatOrchestrator();
        chat.Enqueue(new ChatRequest { Id = "c1", Text = "crew", UserPriority = false, DeadlineUtc = DateTime.UtcNow.AddMinutes(1) });
        chat.Enqueue(new ChatRequest { Id = "u1", Text = "user", UserPriority = true, DeadlineUtc = DateTime.UtcNow.AddMinutes(1) });
        Check(chat.DequeueNext(DateTime.UtcNow).Id == "u1", "user chat priority");
        chat.Cancel("c1");
        Check(chat.DequeueNext(DateTime.UtcNow) == null, "cancelled crew dropped");
        var tools = chat.RunTools("t1", new[] { "plane_hold", "not_a_ported_tool" }, name => "ok", true);
        Check(tools.ToolCalls.Count == 1 && tools.Error.Contains("native command"), "tool boundary rejects unported");
        Check(!ChatOrchestrator.AllowCloudFallback("local", false), "no silent cloud fallback after local");
        Check(ChatOrchestrator.AllowCloudFallback("groq", true), "explicit cloud allowed");
        Console.WriteLine("Phase 4 AI policy foundations: 20 behavior checks passed.");
        string secretsDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aics-secrets-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(secretsDir);
        SecretsStore.DataDirectoryOverride = () => secretsDir;
        SecretsStore.Set("OPENAI_API_KEY", "sk-test-secret-key-1234567890");
        Check(SecretsStore.Get("OPENAI_API_KEY") == "sk-test-secret-key-1234567890", "secrets set/get");
        Check(SecretsStore.Mask("OPENAI_API_KEY").Contains("…"), "secrets mask");
        SecretsStore.Set("GROQ_API_KEY", "gq-key");
        Check(System.IO.File.Exists(System.IO.Path.Combine(secretsDir, ".env")), "secrets atomic file");
        var groq = OpenAiBackend.Resolve("groq");
        Check(groq.Ok && groq.Url == "https://api.groq.com/openai/v1" && groq.Model == "openai/gpt-oss-120b", "groq resolve url/model");
        Check(!OpenAiBackend.Resolve("cline").Ok && OpenAiBackend.Resolve("cline").Error.Contains("CLINE_API_KEY"), "cline without key -> friendly error");
        SecretsStore.Set("CLINE_API_KEY", "cl-key");
        var cline = OpenAiBackend.Resolve("cline");
        Check(cline.Ok && cline.Url == "https://api.cline.bot/api/v1" && cline.Model == "minimax/minimax-m2.5" && cline.Key == "cl-key", "cline resolve url/model/key");
        SecretsStore.Set("CLINE_MODEL", "anthropic/claude-sonnet-4-6");
        Check(OpenAiBackend.Resolve("cline").Model == "anthropic/claude-sonnet-4-6", "cline model override");
        var local = OpenAiBackend.Resolve("local");
        Check(local.Url == "http://localhost:1234/v1", "local lm studio url");
        int fakeCalls = 0;
        string toolResponse = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"get_status\",\"arguments\":\"{}\"}}]}}]}";
        string finalResponse = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Altitude 5 km.\"}}]}";
        var session = new InModChatSession((ep, body) =>
        {
            fakeCalls++;
            return fakeCalls == 1 ? toolResponse : finalResponse;
        });
        string toolLog = "";
        string answer = session.Process("status?", "groq", (name, args) =>
        {
            toolLog = name + ":" + args;
            return "alt 5000 m";
        });
        Check(fakeCalls == 2 && toolLog == "get_status:{}" && answer == "Altitude 5 km. [alt 5000 m]", "in-mod tool loop (reply carries the tool result)");
        Console.WriteLine("In-mod AI stack: 7 behavior checks passed.");
        // ---- P5-1 foundation ----
        var cfgDefaults = BridgeConfigDefaults.Create();
        Check(cfgDefaults["native_chat"] == "true", "new installs default to native chat (no :8765 needed)");
        Check(cfgDefaults["ai_enabled"] == "true" && cfgDefaults["autostart"] == "false", "defaults: AI on, bridge autostart off (bridge-free)");
        Check(cfgDefaults.ContainsKey("bridge_dir") && cfgDefaults.ContainsKey("python"), "bridge fallback keys kept");
        Console.WriteLine("P5-1 config defaults: 3 behavior checks passed.");
        // ---- P5-1: Claude + Grok are real providers, not stubs ----
        SecretsStore.Set("ANTHROPIC_API_KEY", "ant-key-1234567890");
        var claude = OpenAiBackend.Resolve("claude");
        Check(claude.Ok && claude.Url == "https://api.anthropic.com/v1" && claude.Model.StartsWith("claude"), "claude resolves via anthropic openai-compat");
        SecretsStore.Set("XAI_API_KEY", "xai-key-1234567890");
        var grok = OpenAiBackend.Resolve("grokbot");
        Check(grok.Ok && grok.Url == "https://api.x.ai/v1" && grok.Model.StartsWith("grok"), "grokbot resolves via xai openai-compat");
        SecretsStore.Set("ANTHROPIC_API_KEY", "");
        SecretsStore.Set("XAI_API_KEY", "");
        Check(!OpenAiBackend.Resolve("claude").Ok && OpenAiBackend.Resolve("claude").Error.Contains("ANTHROPIC_API_KEY"), "claude missing-key error");
        Check(!OpenAiBackend.Resolve("grokbot").Ok && OpenAiBackend.Resolve("grokbot").Error.Contains("XAI_API_KEY"), "grokbot missing-key error");
        Console.WriteLine("P5-1 wired providers: 4 behavior checks passed.");
        // ---- P5-1: crew / personality / memory / talk essentials ----
        var crewDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aics-crew-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(crewDir);
        PlaystyleNotes.PathProvider = () => System.IO.Path.Combine(crewDir, "playstyle_notes.md");
        KerbalPersonality.PathProvider = () => System.IO.Path.Combine(crewDir, "kerbal_personalities.json");
        Check(PlaystyleNotes.Remember("always keep crossfeed on").StartsWith("Remembered"), "notes remember");
        Check(PlaystyleNotes.Remember("always keep crossfeed on").StartsWith("Updated existing"), "near-dupe refreshes");
        Check(PlaystyleNotes.Load().Count == 1 && PlaystyleNotes.NotesBlock().Contains("crossfeed"), "notes persist + block");
        var stats = new Dictionary<string, object> { { "courage", 0.1 }, { "stupidity", 0.9 }, { "badass", false }, { "veteran", false } };
        var p1 = KerbalPersonality.Generate("Bob Kerman", stats);
        var p2 = KerbalPersonality.Generate("Bob Kerman", stats);
        Check(KerbalPersonality.Strings(p1, "temper")[0] == KerbalPersonality.Strings(p2, "temper")[0], "personality deterministic per name");
        Check(KerbalPersonality.Strings(p1, "temper").Contains("nervous") && KerbalPersonality.Strings(p1, "temper").Contains("scatterbrained"),
            "courage/stupidity shape temper");
        var val = KerbalPersonality.Generate("Val Kerman", new Dictionary<string, object> { { "badass", true } });
        Check(KerbalPersonality.Strings(val, "temper").Contains("unflappable"), "badass is unflappable");
        KerbalPersonality.Ensure("Bob Kerman", () => stats);
        Check(KerbalPersonality.Ensure("Bob Kerman", null) != null && KerbalPersonality.LoadAll().ContainsKey("Bob Kerman"), "personality persisted once");
        string who = KerbalPersonality.Describe("Bob Kerman", "scientist");
        Check(who.StartsWith("a ") && who.Contains("scientist") && who.Contains("who ") && who.Contains(" and hates "), "describe format");
        IntercomTalk.Reset();
        // mirror tests/test_talk.py fixtures: Sidry is the pilot (the chat's own voice) and IS aboard
        var crewList = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Sidry Kerman", "Pilot"),
            new KeyValuePair<string, string>("Bob Kerman", "Scientist"),
            new KeyValuePair<string, string>("Bill Kerman", "Engineer"),
        };
        var known = new List<string> { "Jebediah Kerman", "Valentina Kerman", "Bob Kerman" };
        var hit = IntercomTalk.Route("Hey Bob, are you having a good time?", crewList, "Sidry", known);
        Check(hit != null && hit.Item1 == "kerbal" && hit.Item2.Key == "Bob Kerman" && hit.Item3 == "are you having a good time?", "route hey");
        Check(IntercomTalk.Route("Bob: how's the view?", crewList, "Sidry", known).Item1 == "kerbal", "route lead");
        Check(IntercomTalk.Route("@Bob what's up", crewList, "Sidry", known).Item1 == "kerbal", "route at");
        Check(IntercomTalk.Route("How are you doing, Bill?", crewList, "Sidry", known).Item3 == "How are you doing?", "route tail");
        Check(IntercomTalk.Route("Hey Sidry, how are you?", crewList, "Sidry", known) == null, "pilot is the chat's voice");
        var absent = IntercomTalk.Route("Hey Jeb, you there?", crewList, "Sidry", known);
        Check(absent != null && absent.Item1 == "absent" && absent.Item2.Key == "Jebediah", "short-name match goes absent");
        Check(IntercomTalk.Route("@Gerdy hello", crewList, "Sidry", known).Item1 == "absent", "explicit at unknown is absent");
        Check(IntercomTalk.Route("Bob, land the plane", crewList, "Sidry", known).Item1 == "order", "order goes to pilot");
        Check(IntercomTalk.Route("/status", crewList, "Sidry", known) == null, "slash commands not routed");
        Check(IntercomTalk.Clean("{\"tool\": \"abort\"}") == null && IntercomTalk.Clean("I've deployed the chutes.") == null,
            "clean drops toolish and action claims");
        Check(IntercomTalk.Clean("<think>hmm</think>[INTERCOM] Bob (Sci): Having a blast, Captain!") == "Having a blast, Captain!",
            "clean strips think blocks and tags");
        string canned = IntercomTalk.Reply(new KeyValuePair<string, string>("Bob Kerman", "scientist"), "hi", "",
            prompt => { Thread.Sleep(3000); return "late"; }, null, 200);
        Check(canned == "[INTERCOM] Bob (Sci): " + IntercomTalk.Canned["scientist"][0], "busy AI -> canned line (timeout)");
        Check(IntercomTalk.Memory("Bob Kerman").Count == 1, "reply remembered");
        string okReply = IntercomTalk.Reply(new KeyValuePair<string, string>("Bill Kerman", "engineer"), "hi", "flying",
            prompt => prompt.Contains("Luke says: hi") ? "All good, Captain!" : "wrong", null, 2000);
        Check(okReply == "[COMMS] Bill (Eng): All good, Captain!", "engineer reply on COMMS");
        Console.WriteLine("P5-1 crew/personality/memory/talk: 22 behavior checks passed.");
        // ---- P5-1: ChatOrchestrator is the real queue owner ----
        var queue = new ChatOrchestrator();
        int settled = 0;
        queue.Enqueue(new ChatRequest { Id = "c1", Text = "crew", UserPriority = false, DeadlineUtc = DateTime.UtcNow.AddMinutes(1), Settled = () => settled++ });
        queue.Enqueue(new ChatRequest { Id = "u1", Text = "user", UserPriority = true, DeadlineUtc = DateTime.UtcNow.AddMinutes(1), Settled = () => settled++ });
        Check(queue.Pending == 2, "queued both");
        Check(queue.DequeueNext(DateTime.UtcNow).Id == "u1" && queue.Pending == 1, "user before crew");
        queue.Cancel("c1");
        Check(queue.DequeueNext(DateTime.UtcNow) == null && settled == 1, "cancelled crew dropped + settled once");
        queue.Enqueue(new ChatRequest { Id = "c1", Text = "reused id", UserPriority = false, DeadlineUtc = DateTime.UtcNow.AddMinutes(1) });
        Check(queue.DequeueNext(DateTime.UtcNow) != null, "cancel set is pruned (requeued id runs)");
        queue.Enqueue(new ChatRequest { Id = "old", Text = "stale", UserPriority = true, DeadlineUtc = DateTime.UtcNow.AddMinutes(-1), Settled = () => settled++ });
        Check(queue.DequeueNext(DateTime.UtcNow) == null && settled == 2, "expired deadline dropped + settled");
        queue.Enqueue(new ChatRequest { Id = "u2", Text = "a", UserPriority = true, Settled = () => settled++ });
        queue.Enqueue(new ChatRequest { Id = "c2", Text = "b", UserPriority = false, Settled = () => settled++ });
        Check(queue.CancelAll() == 2 && queue.Pending == 0 && settled == 4, "CancelAll settles every dropped request");
        var guard = new ToolLoopGuard(3);
        Check(guard.TryStep() && guard.TryStep() && guard.TryStep() && !guard.TryStep() && guard.Used == 3, "tool loop budget bounded");
        Console.WriteLine("P5-1 orchestrator ownership: 10 behavior checks passed.");
        // ---- P5-1: thin read-only ports (list_landing_spots / set_ai_name / remember_preference) ----
        var listSettings = new Dictionary<string, object>();
        var listSpots = new NativeSpots(listSettings);
        listSpots.Save("Island 27", "Kerbin", "H", -1.5, -71.8, 90, 134.6);
        string listRows = listSpots.List();
        Check(listRows.StartsWith("Island 27\tKerbin\tH"), "list_landing_spots rows");
        var emptySettings = new Dictionary<string, object>();
        Check(new NativeSpots(emptySettings).List() == "", "empty spot list");
        Console.WriteLine("P5-1 read-only ports: 2 behavior checks passed.");
        // ---- P5-1.7: native safety tick policy (power/sabotage/parking/engine restart run in-mod) ----
        Check(NativeSafety.ShouldRun(false, true) && NativeSafety.ShouldRun(true, false) && !NativeSafety.ShouldRun(true, true), "safety tick runs when AI off or bridge absent");
        Check(NativeSafety.IsFresh(100, 105) && !NativeSafety.IsFresh(100, 111) && !NativeSafety.IsFresh(0, 5), "bridge freshness window");
        Check(NativeSafety.ShouldRevert(true, false, true) && NativeSafety.ShouldRevert(false, true, true), "sabotage revert in flight (safety net or active pilot)");
        Check(!NativeSafety.ShouldRevert(true, true, false) && !NativeSafety.ShouldRevert(false, false, true), "no revert landed or when idle without safety net");
        Check(NativeSafety.ParkingAction(true, true, true, true, true, true, false, false) == "rearm", "parking re-arms after landing");
        Check(NativeSafety.ParkingAction(true, false, false, false, true, true, false, false) == "set", "parking set when grounded idle with wheels");
        Check(NativeSafety.ParkingAction(true, false, false, true, true, true, false, true) == "released", "player brake-off releases parking");
        Check(NativeSafety.ParkingAction(true, false, false, false, false, true, false, false) == "none"
            && NativeSafety.ParkingAction(true, false, false, false, true, false, false, false) == "none"
            && NativeSafety.ParkingAction(false, false, false, false, true, true, false, false) == "none"
            && NativeSafety.ParkingAction(true, false, true, false, true, true, false, false) == "none", "parking exempt: busy mode, no wheels, airborne, released");
        Check(NativeSafety.ParkingAction(true, false, false, true, true, true, true, false) == "none", "parking holds while set");
        Console.WriteLine("P5-1 native safety tick: 9 behavior checks passed.");
        // ---- Oct 9 decision: native controller owns the controls with AI on (AI issues commands only) ----
        Check(NativeSafety.NativeOwns(false, true, false) && !NativeSafety.NativeOwns(false, false, true), "AI off: native owns after handoff");
        Check(NativeSafety.NativeOwns(true, false, true) && !NativeSafety.NativeOwns(true, true, false), "AI on: native owns when in-mod chat/tools on");
        Console.WriteLine("Native ownership with AI on: 2 behavior checks passed.");
        // ---- MechJeb 2.15 planner ports ----
        Check(MechJebPolicy.VersionGate(new Version(2, 15, 0, 0)) == null && MechJebPolicy.VersionGate(new Version(2, 16)) == null, "MJ 2.15+ supported");
        Check(MechJebPolicy.VersionGate(new Version(2, 14, 1)).StartsWith("Unsupported MechJeb version") && MechJebPolicy.VersionGate(null) == MechJebPolicy.Missing, "old/missing MJ refused gracefully");
        Check(MechJebPolicy.TransferKind("Kerbin", "Sun", "Mun", "Kerbin") == "moon" && MechJebPolicy.TransferKind("Mun", "Kerbin", "Kerbin", "Sun") == "return", "moon / return transfer");
        Check(MechJebPolicy.TransferKind("Kerbin", "Sun", "Duna", "Sun") == "interplanetary" && MechJebPolicy.TransferKind("Mun", "Kerbin", "Duna", "Sun") == "unsupported" && MechJebPolicy.TransferKind("Mun", "Kerbin", "Mun", "Kerbin") == "here", "interplanetary / unsupported / here");
        {
            double inc = 30 * Math.PI / 180; var n = new[] { 0.0, -Math.Sin(inc), Math.Cos(inc) };
            bool north; double w = 2 * Math.PI / 100.0;
            double t1 = MechJebPolicy.LaunchWindow(n, new[] { 0.0, 0, 0 }, new[] { 0.0, -1, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, w, out north);
            Check(Math.Abs(t1 - 25) < 0.01, "launch window: site reaches node line after a quarter turn (" + t1 + ")");
            Check(north, "launch window: ascending-node pass launches north (+inclination)");
            double t2 = MechJebPolicy.LaunchWindow(n, new[] { 0.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { -1.0, 0, 0 }, new[] { 0.0, 0, 1 }, w, out north);
            Check(Math.Abs(t2 - 25) < 0.01 && !north, "launch window: descending-node pass launches south");
            double hi = MechJebPolicy.LaunchWindow(n, new[] { 0.0, 0, 0.9 }, new[] { 0.0, -0.436, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 0, 1 }, w, out north);
            Check(double.IsNaN(hi), "launch window: site latitude above target inclination -> none");
        }
        foreach (var tl in new[] { "transfer_to", "match_target_plane", "launch_to_target_plane", "course_correction", "station_keep", "apsis_longitude" })
            Check(NativeCommands.IsPorted(tl), tl + " ported in-process");
        Console.WriteLine("MechJeb planner ports: 14 behavior checks passed.");
        // ---- Live bugs Oct 9 (bridge-free build) ----
        Check(!NativeSafety.NeedsBridge(true, true) && NativeSafety.NeedsBridge(true, false) && !NativeSafety.NeedsBridge(false, false), "menus gate on bridge only for bridge chat");
        Check(NativeSafety.MissingDeps(false, "kRPC, kRPC.MechJeb") == "" && NativeSafety.MissingDeps(true, "kRPC") == "kRPC", "kRPC not required natively");
        Check(NativeSafety.ReleaseForTakeoff("takeoff", "roll", true, true) && !NativeSafety.ReleaseForTakeoff("takeoff", "roll", false, true) && !NativeSafety.ReleaseForTakeoff("hold", "roll", true, true), "takeoff releases parking brake on the roll");
        Check(!NativeSafety.ParkingIdle("idle", true) && !NativeSafety.ParkingIdle("takeoff", false) && NativeSafety.ParkingIdle("idle", false), "parking idle only without command/plan");
        Check(NativeSafety.ParkingAction(true, false, false, false, NativeSafety.ParkingIdle("takeoff", false), true, false, false) == "none", "parking never re-applies during takeoff");
        Func<string, string, string> rw = (n, r) => { var x = FlightResidualPolicy.BuiltInRunway(n, r); return x == null ? "null" : x.Item1 + " " + x.Item2; };
        Check(rw("KSC 27", "") == "ksc 27" && rw("KSP 27", "") == "ksc 27" && rw("runway 27", "") == "ksc 27" && rw("rwy 27", "") == "ksc 27" && rw("27", "") == "ksc 27", "fuzzy KSC 27 aliases");
        Check(rw("09", "") == "ksc 09" && rw("rwy9", "") == "ksc 09" && rw("KSC Runway", "") == "ksc " && rw("", "") == "ksc " && rw("ksc", "27") == "ksc 27", "KSC 09 / no direction / runway param");
        Check(rw("island", "") == "island " && rw("Island Runway 09", "") == "island 09" && rw("island airfield", "27") == "island 27", "island aliases");
        Check(rw("Desert Strip", "") == "null" && rw("Mun Base Alpha", "") == "null", "other names stay saved-spot lookups");
        Check(FlightResidualPolicy.RunwayAlias("KSP 27") == "KSC Runway 27" && FlightResidualPolicy.LandRoute("KSP 27", false, false, true, false) == "spot", "land where=KSP 27 routes to the KSC runway");
        var crew = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Bob Kerman", "Scientist"), new KeyValuePair<string, string>("Sidry Kerman", "Pilot"), new KeyValuePair<string, string>("Jeb Kerman", "Tourist") };
        Check(CrewVoice.Speaker(crew, "") == "Sidry" && CrewVoice.Speaker(new List<KeyValuePair<string, string>>(), "Max") == "Max" && CrewVoice.Speaker(null, "") == "AICS", "pilot voices replies");
        Check(CrewVoice.Line("Sidry", "Gear down.") == "Sidry: Gear down, Captain." && CrewVoice.Line(null, "Gear down.") == "AICS: Gear down." && CrewVoice.Line("Sidry", "Not airborne.") == "Sidry: Not airborne.", "reply line + Captain");
        Check(CrewVoice.Persona("Sidry Kerman", "Pilot", "calm", "Plane").Contains("You are Sidry, the pilot") && CrewVoice.Persona("", "", "", "") == "", "persona prompt");
        {
            string sentBody = null;
            var vs = new InModChatSession((ep, body) => { sentBody = body; return "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"On it.\"}}]}"; });
            vs.Persona = CrewVoice.Persona("Sidry Kerman", "Pilot", "", "Plane");
            vs.Process("land", "groq", (n, x) => "");
            Check(sentBody != null && sentBody.Contains("You are Sidry") && sentBody.Contains("\"land\"") && sentBody.Contains("find_tool") && !sentBody.Contains("\"transfer_to\""), "session sends persona + only matched tools + find_tool");
        Check(sentBody.Contains("\"stream\":false"), "requests are non-streaming (Cline streams by default)");
        }
        var cc = new CrewChatter(new Random(1));
        var lines = cc.Emergency("parts", "Wing", crew, "Sidry Kerman", 100);
        Check(lines.Count == 2 && !lines.Exists(l => l.ToString().Contains("Sidry")) && lines.Exists(l => l.ToString().Contains("Bob (Sci)")), "emergency chatter: two non-pilot lines");
        Check(cc.Emergency("parts", "Wing", crew, "Sidry Kerman", 105).Count == 0, "emergency chatter rate-limited per member");
        var tc = new CrewChatter(new Random(2)); int posted = 0;
        for (int s = 0; s < 1200; s++) { var o = tc.TripTick(s, "v1", true, crew, "Sidry Kerman", false, false, double.NaN); if (s < 120) Check(o.Count == 0, "no trip chatter in the first 2 min"); posted += o.Count; }
        Check(posted > 0, "trip chatter posts on a long flight");
        var off = new CrewChatter(new Random(2)) { Enabled = false }; int offPosted = 0;
        for (int s = 0; s < 1200; s++) offPosted += off.TripTick(s, "v1", true, crew, "Sidry Kerman", false, false, double.NaN).Count;
        Check(offPosted == 0, "chatter off = silent");
        int portedTools = 0; foreach (var pn in NativeCommands.Ported) if (!pn.Contains("/")) portedTools++;
        Check(NativeToolSchemas.Count == portedTools, "tool schemas cover every ported tool (" + NativeToolSchemas.Count + "/" + portedTools + ")");
        {
            BridgeHttp.Allowed = () => NativeSafety.NeedsBridge(true, true);   // AI on + in-mod chat
            int before = BridgeHttp.Blocked; bool threw = false;
            try { BridgeHttp.Create("tool"); } catch (BridgeOffException ex) { threw = ex.Message.StartsWith("Not available in-mod yet"); }
            Check(threw && BridgeHttp.Blocked == before + 1, "native mode: no request to 127.0.0.1:8765 is ever created");
            BridgeHttp.Allowed = () => NativeSafety.NeedsBridge(false, false);
            threw = false; try { BridgeHttp.Create("health"); } catch (BridgeOffException) { threw = true; }
            Check(threw, "AI off: bridge HTTP refused too");
            BridgeHttp.Allowed = () => false;
        }
        Check(EmbeddedLlm.UnloadOnScene("MAINMENU") && !EmbeddedLlm.UnloadOnScene("FLIGHT") && !EmbeddedLlm.UnloadOnScene("SPACECENTER"), "model unloads only at the main menu");
        EmbeddedLlm.Unload(); Check(!EmbeddedLlm.Loaded, "unload with nothing loaded is safe; next chat reloads lazily");
        Console.WriteLine("Live bug fixes (menus, brakes, runway, voice, chatter, tools): 25 behavior checks passed.");
        // ---- Model-written crew chatter + per-save personalities ----
        {
            var parsed = CrewPrompts.Parse(System.IO.File.ReadAllLines(System.IO.Path.Combine(Root(), "personalities.txt"), System.Text.Encoding.UTF8));
            Check(parsed.Length == 36 && parsed.Length == PersonalityPrompts.All.Length && parsed[0][0] == "Nervous Scientist" && parsed[0][1] == "scientist", "personalities.txt: 36 parsed (em dash ok), roles");
            var pr1 = CrewPrompts.Roll("Pilot", new Random(1)); var pr2 = CrewPrompts.Roll("Pilot", new Random(99));
            Check(pr1[0] != pr1[1] && (PersonalityPrompts.All[pr1[0]][1] == "pilot" || PersonalityPrompts.All[pr1[0]][1] == "any"), "roll: two different traits, primary fits the job");
            Check(pr1[0] != pr2[0] || pr1[1] != pr2[1], "different saves roll different personalities");
            string sys = CrewPrompts.SystemFor(pr1);
            Check(sys.Split(' ').Length < 60 && sys.Contains("Also a bit of a") && sys.Contains("at most 12 words"), "system prompt short (3B) with both traits");
            var line = new CrewLine { Name = "Bob Kerman", Trait = "scientist", Canned = "Are we there yet?", AllowedNumbers = CrewLine.Numbers("about 5 min to go") };
            Check(line.Accept("Bob: Five more minutes?! I'll never make it!") == "Five more minutes?! I'll never make it!", "model line accepted, name prefix stripped");
            Check(line.Accept("About 12 min, I think.") == null && line.Accept("{\"tool\":\"land\"}") == null, "invented numbers / toolish output -> canned");
            Check(line.Format(null) == "[INTERCOM] Bob (Sci): Are we there yet?", "canned fallback line");
            Check(ChatterPolicy.ModelFree(false, 0) && !ChatterPolicy.ModelFree(true, 0) && !ChatterPolicy.ModelFree(false, 1), "chatter never over player chat / busy model");
            Check(ChatterPolicy.ProviderReady(false, false) && !ChatterPolicy.ProviderReady(true, false) && ChatterPolicy.ProviderReady(true, true), "chatter never loads the model");
            Check(ChatterPolicy.Content("{\"choices\":[{\"message\":{\"content\":\"Hi!\"}}]}") == "Hi!" && ChatterPolicy.Content("garbage") == null, "reply content parse");
            var em = new CrewChatter(new Random(3)) { Describe = n => "a nervous scientist" };
            var el = em.Emergency("flameout", "Juno", crew, "Sidry Kerman", 50);
            Check(el.Count > 0 && el[0].Prompt.Contains("an engine (Juno) just flamed out") && el[0].Prompt.Contains("Bob, a nervous scientist") || el[0].Prompt.Contains("Jeb"), "emergency prompt carries facts + personality");
            Check(em.Command("chatter off") == "Crew intercom chatter off." && !em.Enabled && em.TripTick(1000, "v", true, crew, "Sidry Kerman", true, false, double.NaN).Count == 0, "/crew chatter off silences");
            Check(em.Command("on") == "Crew intercom chatter on." && em.Enabled && em.Command("").Contains("is on"), "/crew chatter on + status");
            var tk = new CrewChatter(new Random(5)); CrewTalk talk = null;
            for (int s = 0; s < 4000 && talk == null; s++) talk = tk.TalkTick(s, "v2", true, crew, "Sidry Kerman", false, false, double.NaN);
            Check(talk != null && talk.Lines >= 4 && talk.Lines <= 6 && talk.Pair[0].Key != talk.Pair[1].Key, "small talk starts on a long cruise (4-6 lines, two kerbals)");
            var tl = tk.TalkLine(talk, 1, new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Bob", "Hi") });
            Check(tl.Prompt.Contains("So far: Bob: \"Hi\"") && tl.Canned == talk.Script[1], "conversation line uses transcript, canned script fallback");
            tk.TalkRunning = true; Check(tk.TripTick(9000, "v2", true, crew, "Sidry Kerman", true, false, double.NaN).Count == 0, "no 'are we there yet' over a conversation");
        }
        Console.WriteLine("Crew chatter (model-written) + personalities: 17 behavior checks passed.");
        // ---- Tool trimming ----
        {
            Func<string, string, string> D = (m, c) => { var d = ToolRouter.Direct(m, c); return d == null ? "null" : d.Value.Key + " " + d.Value.Value; };
            Check(D("takeoff", "plane") == "takeoff {}" && D("Gear down", "plane") == "set_gear {\"down\":true}" && D("brakes off", "") == "set_brakes {\"on\":false}", "direct: takeoff / gear / brakes");
            Check(D("land at 27", "plane") == "land {\"where\":\"27\"}" && D("land at KSP 27", "plane") == "land {\"where\":\"ksp 27\"}" && D("flaps 2", "plane") == "flaps {\"setting\":\"2\"}", "direct: land at runway / flaps");
            Check(D("land at the mun base", "plane") == "null" && D("can you land?", "plane") == "null" && D("gear down and land", "plane") == "null", "vague / compound -> model");
            Check(D("takeoff", "rocket") == "null" && D("flaps 2", "heli") == "null", "craft filter on direct commands");
            Check(D("stage", "rocket") == "null" && D("abort", "") == "null", "destructive tools never auto-run");
            var rk = ToolRouter.Rank("put the gear down please", ToolRouter.Descriptions, "plane");
            Check(rk.Count >= 1 && rk.Count <= 3 && rk[0].Key == "set_gear", "rank: gear -> set_gear first");
            var rk2 = ToolRouter.Rank("circularize our orbit", ToolRouter.Descriptions, "plane");
            Check(!rk2.Exists(x => x.Key == "circularize"), "rank: orbital tools filtered out for a plane");
            var offer = ToolRouter.Offer("hmm what should we do", "plane");
            Check(offer.Count == 1 && MiniJson.Serialize(offer).Contains("find_tool"), "vague ask -> only find_tool");
            List<object> found; string fr = ToolRouter.Find("raise apoapsis", "rocket", out found);
            Check(found.Count >= 1 && found.Count <= 3 && fr.Contains("change_apoapsis"), "find_tool returns 1-3 schemas");
            int fullLen = NativeToolSchemas.Json.Length, trimLen = MiniJson.Serialize(ToolRouter.Offer("gear down", "plane")).Length;
            Check(trimLen * 10 < fullLen, "trimmed tools < 10% of full list (" + trimLen + " vs " + fullLen + " chars)");
            Console.WriteLine("TOOLTRIM full=" + fullLen + " trimmed=" + trimLen);
            int calls = 0; string sent2 = null;
            var two = new InModChatSession((ep, body) => { calls++; sent2 = body; return calls == 1
                ? "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"f1\",\"type\":\"function\",\"function\":{\"name\":\"find_tool\",\"arguments\":\"{\\\"query\\\":\\\"raise apoapsis\\\"}\"}}]}}]}"
                : "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Done.\"}}]}"; }) { Craft = "rocket" };
            string ran = null; two.Process("make the orbit bigger", "groq", (n, x) => { ran = n; return "ok"; });
            Check(calls == 2 && ran == null && sent2.Contains("change_apoapsis"), "2-step: find_tool adds the real schema for the next round");
        }
        Console.WriteLine("Tool trimming: 11 behavior checks passed.");
        // ---- POST-TESTING: hold_pattern ----
        {
            Check(FlightExtrasPolicy.MaxBank(150) == 20 && FlightExtrasPolicy.MaxBank(250) == 10, "Luke's bank rule: 20 slow / 10 fast");
            double r150 = FlightExtrasPolicy.MinRadius(150), r250 = FlightExtrasPolicy.MinRadius(250);
            Check(r150 > 7000 && r150 < 9000 && r250 > r150 * 3, "pattern radius respects the bank cap (" + r150.ToString("0") + " m @150)");
            Check(Math.Abs(FlightExtrasPolicy.PatternHeading(90, 3000, 3000, true) - 0) < 1e-6, "on the circle, right-hand: fly the tangent (center on right)");
            Check(Math.Abs(FlightExtrasPolicy.PatternHeading(90, 3000, 3000, false) - 180) < 1e-6, "left-hand tangent");
            double outR = FlightExtrasPolicy.PatternHeading(90, 9000, 3000, true);
            Check(Math.Abs(outR - 45) < 1e-6, "far outside -> turns 45 deg toward the center");
            double inR = FlightExtrasPolicy.PatternHeading(90, 0, 3000, true);
            Check(Math.Abs(inR - 315) < 1e-6, "inside -> turns away (45 deg)");
            Check(NativeCommands.IsPorted("hold_pattern") && ToolRouter.Schemas.ContainsKey("hold_pattern") && ToolRouter.Fits("hold_pattern", "plane"), "hold_pattern registered + schema");
        }
        Console.WriteLine("hold_pattern: 7 behavior checks passed.");
        // ---- POST-TESTING: follow_terrain ----
        {
            Check(FlightExtrasPolicy.TerrainTarget(100, 400, 200, double.NaN) == 600, "follow_terrain: agl above the highest ground ahead");
            Check(FlightExtrasPolicy.TerrainTarget(300, double.NaN, 150, double.NaN) == 450, "no lookahead -> ground below");
            Check(FlightExtrasPolicy.TerrainTarget(0, 0, 200, 900) == 892, "descends gently (8 m/tick), never dives");
            Check(FlightExtrasPolicy.TerrainTarget(800, 0, 200, 300) == 1000, "climbs at once for rising ground");
            Check(FlightExtrasPolicy.TerrainTarget(-50, double.NaN, 10, double.NaN) == 50, "sea: water level + clamped min AGL 50");
            Check(FlightExtrasPolicy.FloorMargin(200) == 120 && FlightExtrasPolicy.FloorMargin(1000) == 600 && FlightExtrasPolicy.FloorMargin(50) == 30, "safety floor margin under the AGL");
            Check(NativeCommands.IsPorted("follow_terrain") && ToolRouter.Rank("follow the terrain at 150 m", ToolRouter.Descriptions, "plane")[0].Key == "follow_terrain", "follow_terrain registered + routed");
        }
        Console.WriteLine("follow_terrain: 7 behavior checks passed.");
        // ---- POST-TESTING: fuel_check_return ----
        {
            double rate = FlightExtrasPolicy.BurnRate(double.NaN, 100, 2, double.NaN);
            Check(double.IsNaN(rate), "first sample: no burn rate yet");
            rate = FlightExtrasPolicy.BurnRate(100, 99, 2, rate); Check(Math.Abs(rate - .5) < 1e-9, "burn rate from samples");
            Check(Math.Abs(FlightExtrasPolicy.BurnRate(99, 97, 2, .5) - .6) < 1e-9 && FlightExtrasPolicy.BurnRate(97, 98, 2, .6) == .6, "smoothed; refuel keeps old rate");
            Check(FlightExtrasPolicy.Range(100, .5, 150) == 30000 && double.IsPositiveInfinity(FlightExtrasPolicy.Range(100, double.NaN, 150)), "range = time left x groundspeed");
            Check(!FlightExtrasPolicy.ReturnNow(100000, 50000, 15) && FlightExtrasPolicy.ReturnNow(72000, 50000, 15), "turn home when range ~ trip + 15% + approach margin");
            Check(!FlightExtrasPolicy.ReturnNow(double.PositiveInfinity, 50000, 15), "unknown range never triggers");
            Check(NativeCommands.IsPorted("fuel_check_return") && ToolRouter.Rank("bingo fuel return home", ToolRouter.Descriptions, "plane")[0].Key == "fuel_check_return", "fuel_check_return registered + routed");
        }
        Console.WriteLine("fuel_check_return: 7 behavior checks passed.");
        // ---- POST-TESTING: formation ----
        {
            double R = 600000, mPerDeg = Math.PI / 180 * R, al, cr;
            FlightExtrasPolicy.SlotError(0, 0, 0, -60 / mPerDeg, 60 / mPerDeg, 60, 1, R, out al, out cr);
            Check(Math.Abs(al) < .5 && Math.Abs(cr) < .5, "in the right echelon slot: zero error");
            FlightExtrasPolicy.SlotError(0, 0, 90, 0, -500 / mPerDeg, 60, 1, R, out al, out cr);
            Check(Math.Abs(al - 440) < 1 && Math.Abs(cr - 60) < 1, "lead heading east, wing 500 m behind: slot 440 m ahead, 60 m right");
            Check(FlightExtrasPolicy.WingHeading(90, 1000, 0) == 120 && FlightExtrasPolicy.WingHeading(90, 0, 0) == 90, "steer toward slot, capped 30 deg off lead");
            Check(FlightExtrasPolicy.WingSpeed(150, 1000) == 175 && FlightExtrasPolicy.WingSpeed(150, -100) == 142, "close on the slot +-25 m/s");
            Check(FlightExtrasPolicy.WingBank(90, 150) == 20 && FlightExtrasPolicy.WingBank(-90, 250) == -10, "wing bank obeys 20/10 rule");
            Check(FlightExtrasPolicy.ThrottleStep(.5, 20, 1) == .5 && Math.Abs(FlightExtrasPolicy.ThrottleStep(.5, 20, 3) - .55) < 1e-9, "throttle: 5% steps, waits 2.5 s");
            Check(Math.Abs(FlightExtrasPolicy.ThrottleStep(.05, -20, 3) - .05) < 1e-9, "never 0 throttle in flight");
            Check(FlightExtrasPolicy.HeadingError(10, 350) == 20 && FlightExtrasPolicy.WingVs(1000) == 15, "heading wrap + capped climb");
            Check(NativeCommands.IsPorted("formation") && ToolRouter.Rank("wingman join formation", ToolRouter.Descriptions, "plane")[0].Key == "formation", "formation registered + routed");
        }
        Console.WriteLine("formation: 9 behavior checks passed.");
        // ---- POST-TESTING: tech_advisor ----
        {
            Func<string, int, bool, bool, string[], TechAdvisorPolicy.Node> N = (id, c, u, any, ps) => { var n = new TechAdvisorPolicy.Node { Id = id, Cost = c, Unlocked = u, AnyParent = any }; n.Parents.AddRange(ps); return n; };
            var tree = new Dictionary<string, TechAdvisorPolicy.Node>
            {
                { "start", N("start", 0, true, false, new string[0]) }, { "basicRocketry", N("basicRocketry", 5, true, false, new[] { "start" }) },
                { "engineering101", N("engineering101", 5, false, false, new[] { "start" }) }, { "generalRocketry", N("generalRocketry", 20, false, false, new[] { "basicRocketry" }) },
                { "stability", N("stability", 18, false, false, new[] { "engineering101" }) }, { "survivability", N("survivability", 15, false, false, new[] { "basicRocketry" }) },
                { "advRocketry", N("advRocketry", 45, false, true, new[] { "generalRocketry", "survivability" }) },
                { "landing", N("landing", 90, false, false, new[] { "advRocketry", "stability" }) },
            };
            int total; var path = TechAdvisorPolicy.Path(tree, new[] { "advRocketry" }, out total);
            Check(string.Join(",", path.ToArray()) == "survivability,advRocketry" && total == 60, "any-parent: cheapest parent chain (15 < 20)");
            path = TechAdvisorPolicy.Path(tree, new[] { "landing" }, out total);
            Check(path.IndexOf("advRocketry") < path.IndexOf("landing") && path.Contains("stability") && path.Contains("engineering101") && total == 15 + 45 + 5 + 18 + 90, "all-parents node pulls every chain, parents first");
            Check(TechAdvisorPolicy.Path(tree, new[] { "basicRocketry", "nope" }, out total).Count == 0 && total == 0, "researched / unknown targets cost nothing");
            Check(TechAdvisorPolicy.Targets("I want a Mun landing").Contains("landing") && TechAdvisorPolicy.Targets("better planes").Contains("aviation"), "goal -> stock targets");
            Check(TechAdvisorPolicy.Targets("hmm").Count == 0 && TechAdvisorPolicy.Ask.Contains("goal"), "vague goal -> asks");
            Check(NativeCommands.IsPorted("tech_advisor") && ToolRouter.Rank("what should i research in the tech tree", ToolRouter.Descriptions, "")[0].Key == "tech_advisor", "tech_advisor registered + routed");
        }
        Console.WriteLine("tech_advisor: 6 behavior checks passed.");
        // ---- Rover drive-to-building ----
        {
            Check(KscRoverPolicy.Facility("SpaceCenter/VehicleAssemblyBuilding/Facility/mainBuilding") == "VehicleAssemblyBuilding" && KscRoverPolicy.Facility("Junk/Rock") == null, "facility from building id");
            Check(KscRoverPolicy.Match("the VAB") == "VehicleAssemblyBuilding" && KscRoverPolicy.Match("r&d") == "ResearchAndDevelopment" && KscRoverPolicy.Match("tracking station") == "TrackingStation", "player words -> facility");
            Check(KscRoverPolicy.Match("mission control") == "MissionControl" && KscRoverPolicy.Match("pizza") == null && KscRoverPolicy.Display("SpaceplaneHangar") == "SPH", "more names; unknown -> null");
            double R = 600000, m = Math.PI / 180 * R;
            var hub = new KscRoverPolicy.P(0, 0); var bld = new KscRoverPolicy.P(0, 500 / m);
            var far = KscRoverPolicy.Route(new KscRoverPolicy.P(0, -800 / m), bld, hub, R);
            Check(far.Count == 2 && far[0].Lon == 0 && Math.Abs(far[1].Lon * m - 455) < 1, "far: via hub, stop 45 m in front of the building");
            var near = KscRoverPolicy.Route(new KscRoverPolicy.P(0, 400 / m), bld, hub, R);
            Check(near.Count == 1, "close: drive straight to the front");
            var order = KscRoverPolicy.Order(new KscRoverPolicy.P(0, 0), new[] { new KscRoverPolicy.P(0, 900 / m), new KscRoverPolicy.P(0, 100 / m), new KscRoverPolicy.P(0, 400 / m) }, R);
            Check(string.Join(",", order.ConvertAll(i => i.ToString()).ToArray()) == "1,2,0", "tour order nearest-first");
            Check(NativeCommands.IsPorted("drive_to_building") && ToolRouter.Fits("drive_to_building", "rover") && !ToolRouter.Fits("drive_to_building", "plane"), "drive_to_building registered, rover-only");
        }
        Console.WriteLine("Rover drive-to-building: 7 behavior checks passed.");
        // ---- Live test round 2: multi-turn replay (tool calls stop firing after the first) ----
        {
            var logDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aics-chatlog-" + Guid.NewGuid().ToString("N"));
            ChatLog.Dir = logDir;
            Func<string, string> Qwen = txt => EmbeddedPrompt.ToOpenAi(txt);
            var bodies = new List<string>(); var script = new Queue<string>(new[]
            {
                "<tool_call>\n{\"name\": \"takeoff\", \"arguments\": {}}\n</tool_call>", "Taking off, Captain.",
                "I will set the throttle to full for Aeris 3A.",                                                     // talks instead of acting
                "<tool_call>\n{\"name\": \"set_throttle\", \"arguments\": {\"value\": 100}}\n</tool_call>", "Full throttle.",
                "<tool_call>\n{\"name\": \"circle_here\", \"arguments\": {\"bank\": 25}}\n</tool_call>", "ok",
            });
            var s = new InModChatSession((ep, body) => { bodies.Add(body); return Qwen(script.Dequeue()); }) { Craft = "plane" };
            var ran = new List<string>();
            Func<string, string, string> exec = (n, x) => { ran.Add(n + " " + x); return n == "circle_here" ? "Engage local holds in flight first." : "done " + n; };
            string r1 = s.Process("takeoff and circle", "groq", exec);
            string r2 = s.Process("full throttle", "groq", exec);
            string r3 = s.Process("increase bank to 25", "groq", exec);
            Check(ran.Count == 3 && ran[0].StartsWith("takeoff") && ran[1].StartsWith("set_throttle") && ran[2].StartsWith("circle_here"), "replay: every turn's tool call fires (" + string.Join("|", ran.ToArray()) + ")");
            Check(bodies.Exists(b => b.Contains("Do it now: call the set_throttle tool")), "talk-only reply -> one nudge with only the matching tool");
            Check(!bodies[bodies.Count - 1].Contains("Taking off, Captain") && !bodies[bodies.Count - 1].Contains("I will set the throttle"), "old narrated replies are not fed back to the 3B model");
            Check(r3.Contains("[Engage local holds in flight first.]") && r1.Contains("done takeoff"), "reply carries the real tool result (no bare 'ok')");
            Check(bodies[0].Contains("MUST call a tool") && bodies[0].Contains("Craft: plane"), "system prompt: tool rule + craft");
            string log = System.IO.File.ReadAllText(ChatLog.PathFor(DateTime.Now));
            Check(log.Contains("[player] full throttle") && log.Contains("[tools]") && log.Contains("[raw]") && log.Contains("[call] set_throttle") && log.Contains("[reply]") && log.Contains("[nudge] set_throttle"), "chat log has player/tools/raw/call/reply/nudge");
            Check(ChatLog.Redact("key sk-abcdefghijklmnop and Bearer abcdefghijkl123") == "key [redacted] and [redacted]" && ChatLog.Redact("\"api_key\": \"zzzzzzzzzz\"").Contains("[redacted]"), "chat log redacts keys");
            ChatLog.Write("x", new string('a', 7000)); Check(new System.IO.FileInfo(ChatLog.PathFor(DateTime.Now)).Length < ChatLog.MaxBytes, "chat log capped");
            Check(ChatTelemetry.Line(1020, 980, 182, 271, -12, 6, .65, 3, false, false, "circle") == "alt=1020m agl=980 spd=182 hdg=271 bank=-12 pit=6 thr=65% vs=+3 brk=0 gear=up ap=circle", "telemetry shorthand line");
            ChatLog.Dir = null;
            Check(PilotPolicy.FilterNotes("Luke's known playstyle preferences (respect these):\n- 80 km parking orbit\n- automate fuel transfers\n- likes 200 m/s cruise", "plane") == "Luke's known playstyle preferences (respect these):\n- likes 200 m/s cruise", "plane prompt drops orbit/transfer notes");
            Check(PilotPolicy.FilterNotes("x\n- 80 km parking orbit", "rocket").Contains("parking orbit"), "rockets keep them");
        }
        Console.WriteLine("Round-2 multi-turn tool replay + chat log: 11 behavior checks passed.");
        // ---- Live test round 2: Luke's exact circle-mission sequence, router, roll, plans, runways, engines ----
        {
            Func<string, string> D = m => { var d = ToolRouter.Direct(m, "plane"); return d == null ? "null" : d.Value.Key + " " + d.Value.Value; };
            Check(D("full throttle") == "set_throttle {\"value\":100}" && D("throttle 50%") == "set_throttle {\"value\":50}", "direct: full throttle / throttle N%");
            Check(D("roll inverted") == "roll {\"inverted\":true}" && D("roll level") == "roll {\"level\":true}" && D("bank left 30 override") == "roll {\"direction\":\"left\",\"degrees\":30,\"override\":true}", "direct: roll inverted / level / bank N override");
            Check(D("Turn off autopilot") == "autopilot {\"on\":false}" && D("turn back on the autopilot") == "autopilot {\"on\":true}", "autopilot off AND back on");
            Check(D("land at nearest runway") == "land {\"where\":\"nearest runway\"}" && D("Land and Circle Runway").StartsWith("make_flight_plan") && D("circle runway") == "circle_here {}", "land nearest; 'land and circle runway' = circle then land");
            Check(D("make a flight plan fly 100km out, turn around, fly back, land at 27 ksp").StartsWith("make_flight_plan"), "plan request -> make_flight_plan");
            foreach (string cmd in new[] { "circle_here", "set_throttle", "land", "land_plane", "fly_to", "roll", "autopilot", "stop_current", "set_altitude" })
                Check(PilotPolicy.PreemptsPlan(cmd), "active mission is preempted by " + cmd);
            Check(!PilotPolicy.PreemptsPlan("get_status") && !PilotPolicy.PreemptsPlan("flightplan/status"), "status reads don't stop the mission");
            bool ov; double rt = PilotPolicy.RollTarget("right", 45, false, false, false, 150, out ov);
            Check(rt == 20 && !ov && PilotPolicy.RollTarget("left", 45, false, false, true, 150, out ov) == -45 && ov, "roll: bank rule unless explicit override");
            Check(PilotPolicy.RollTarget("left", 0, true, false, false, 150, out ov) == 180 && ov && PilotPolicy.ClampBank(180, 150, true) == 180 && PilotPolicy.ClampBank(40, 150, false) == 20, "inverted is an explicit override");
            Check(PilotPolicy.PitchCommand(.3, 175) == -.3 && PilotPolicy.PitchCommand(.3, 10) == .3, "elevator reversed when inverted");
            Check(PilotPolicy.CircleBank("right", 25, 150, true) == 25 && PilotPolicy.CircleBank("left", 25, 150, false) == -20 && PilotPolicy.CircleBank("left", 80, 150, true) == -45, "'increase bank to 25' honoured (max 45)");
            Check(PilotPolicy.EngineWatch(true, false, false, true, false, true) == "restart" && PilotPolicy.EngineWatch(true, false, false, true, true, true) == "none" && PilotPolicy.EngineWatch(true, false, false, false, false, true) == "none", "engine watch: shut down in flight -> relight; ours/grounded -> no");
            var rng = new Random(1); double f = PilotPolicy.FumbleSeconds(rng); Check(f >= 2 && f <= 4, "pilot fumbles 2-4 s");
            string planTxt = PilotPolicy.PlanFromText("make a flight plan fly 100km out, turn around, fly back, land at 27 ksp", true);
            Check(planTxt == "takeoff\nclimb 1000 m agl\ncruise for 100 km\nturn around\ncruise for 88 km\nland KSC 27", "planTxt from chat: " + planTxt.Replace("\n", " | "));
            var np = NativePlan.Parse(planTxt, name => false, route => false);
            Check(np.Steps.Count == 6 && np.Steps[3].Op == "turnaround" && np.Steps[5].Op == "land", "native plan accepts turn around + fuzzy built-in runway");
            Check(PilotPolicy.PlanFromText("circle 1 lap, land at KSC", false) == "circle 1 laps left bank 15\nland KSC", "circle then land");
            Check(FlightResidualPolicy.BuiltInRunway("27 ksp", "").Item2 == "27" && FlightResidualPolicy.BuiltInRunway("nearest runway", "").Item1 == "nearest" && FlightResidualPolicy.RunwayAlias("land at the nearest runway") == "nearest runway", "runway: word order + nearest");
        }
        Console.WriteLine("Round-2 mission preemption / router / roll / plans / engines: 24 behavior checks passed.");
        // ---- Live round 3: Luke's plan sentence, speed cap, porpoise, telemetry cadence, approach lineup ----
        {
            string lp; List<string> uns, nts;
            bool ok3 = PilotPolicy.TryPlan("Make a flight plan, Take off and climb to 2k, then bank at 25 degrees to the left back to KSP and set for a shortfinal on runway 27, and land.", true, out lp, out uns, out nts);
            Check(ok3 && lp == "takeoff\nclimb 2000 m msl\nhead KSC bank 25 left\nland KSC 27", "Luke's sentence -> " + lp.Replace("\n", " | ") + " unsure=" + string.Join(",", uns.ToArray()));
            Check(nts.Exists(n => n.Contains("short final")), "short final reported, not silently dropped");
            var lpp = NativePlan.Parse(lp, n => false, r => false);
            Check(lpp.Steps[2].Op == "head" && lpp.Steps[2].Bank == -25 && lpp.Steps[3].Op == "land", "plan grammar: head KSC bank 25 left");
            Check(PilotPolicy.AltitudeM("2k") == 2000 && PilotPolicy.AltitudeM("2 km") == 2000 && Math.Abs(PilotPolicy.AltitudeM("6500 ft") - 1981.2) < .1 && PilotPolicy.AltitudeM("1500 m") == 1500, "units: k/km/ft/m");
            Check(!PilotPolicy.TryPlan("make a flight plan, climb to 2, then do a barrel roll over the mun", true, out lp, out uns, out nts) && uns.Count == 2, "unsure -> ask (" + string.Join(" / ", uns.ToArray()) + ")");
            Check(ToolRouter.Direct("make a flight plan, climb to 2, then do a barrel roll", "plane") == null, "unsure plan goes to the model, not straight to fly");
            PilotPolicy.TryPlan("plan: takeoff, climb 200 m, land", true, out lp, out uns, out nts);
            Check(nts.Exists(n => n.Contains("300 m")), "below-floor climb is reported");
            bool cut3; double th = PilotPolicy.SpeedCapThrottle(.9, 336, 300, 2, out cut3);
            Check(cut3 && Math.Abs(th - .6) < 1e-9 && PilotPolicy.SpeedCapThrottle(.9, 230, 300, 2, out cut3) == .75 && PilotPolicy.SpeedCapThrottle(.9, 230, 300, .5, out cut3) == .9 && PilotPolicy.SpeedCapThrottle(.9, 300, 7000, 2, out cut3) == .9, "over 220 low down: cut3 15-30% per second");
            Check(PilotPolicy.HandoffThrottle(1) == .65 && PilotPolicy.EffectiveAltitude(2, 300) == 300 && PilotPolicy.EffectiveAltitude(2000, 300) == 2000, "no full-power handoff; floor becomes the target (no porpoise)");
            Check(!PilotPolicy.LogsTelemetry("flightplan/status") && !PilotPolicy.LogsTelemetry("get_status") && PilotPolicy.LogsTelemetry("set_throttle"), "telemetry only on real commands (+30 s)");
            // approach sim: start over KSC buildings, crossing the runway at 70 deg, like Luke's screenshot
            double R = 600000;
            var rw3 = new RunwayMission { Lat = -.0502119, Lon = -74.490300, EndLat = -.0485997, EndLon = -74.724375, Elevation = 70 };   // land 27
            double course = NavigationMath.Bearing(rw3.Lat, rw3.Lon, rw3.EndLat, rw3.EndLon);
            double lat = -.07, lon = -74.56, hdg = 0, alt = 400, spd = 120; double crossAt1k = double.NaN, trkAt1k = double.NaN; bool descendedUnaligned = false; string firstFinal = null;
            for (int s = 0; s < 1200 && rw3.Phase != "rollout" && rw3.Phase != "go around"; s++)
            {
                rw3.Step(lat, lon, alt, alt - 70, spd, alt <= 70.5, R, 45, hdg);
                if (rw3.Phase == "final" && firstFinal == null) firstFinal = Math.Abs(rw3.Cross).ToString("0");
                if (rw3.Phase != "final" && rw3.Phase != "flare" && rw3.Phase != "rollout" && rw3.DesiredAltitude < 70 + 799) descendedUnaligned = true;
                double err = FlightPolicy.Wrap(rw3.DesiredHeading - hdg); hdg = (hdg + Math.Max(-1.7, Math.Min(1.7, err)) + 360) % 360;   // 20 deg bank at 120 m/s
                alt += Math.Max(-6, Math.Min(10, rw3.DesiredVs)); if (alt < 70) alt = 70;
                spd += Math.Max(-2, Math.Min(2, rw3.DesiredSpeed - spd)); double nl, no; NavigationMath.Offset(lat, lon, hdg, spd, R, out nl, out no); lat = nl; lon = no;
                if (double.IsNaN(crossAt1k) && rw3.Phase == "final" && rw3.Distance < 1000) { crossAt1k = Math.Abs(rw3.Cross); trkAt1k = Math.Abs(FlightPolicy.Wrap(hdg - course)); }
            }
            Check(firstFinal != null && double.Parse(firstFinal) < RunwayMission.AlignCross, "glideslope only after centerline intercept (cross " + firstFinal + " m)");
            Check(!descendedUnaligned, "no descent before alignment");
            Check(!double.IsNaN(crossAt1k) && crossAt1k < RunwayMission.GateCross && trkAt1k < RunwayMission.GateHeading, "aligned at 1 km: " + crossAt1k.ToString("0") + " m / " + trkAt1k.ToString("0.0") + " deg");
            var gate = new RunwayMission { Lat = -.0502119, Lon = -74.490300, EndLat = -.0485997, EndLon = -74.724375, Elevation = 70, Phase = "final" };
            double gl, gn; NavigationMath.Offset(gate.Lat, gate.Lon, course + 180, 800, R, out gl, out gn); NavigationMath.Offset(gl, gn, course + 90, 120, R, out gl, out gn);
            gate.Step(gl, gn, 120, 50, 70, false, R, 45, course + 20);
            Check(gate.Phase == "go around" && gate.Why.Contains("not lined up"), "misaligned at 1 km -> go around (" + gate.Why + ")");
        }
        {
            double R = 600000; var g = new RunwayMission { Lat = -.0502119, Lon = -74.490300, EndLat = -.0485997, EndLon = -74.724375, Elevation = 70, Phase = "final" };
            double c = NavigationMath.Bearing(g.Lat, g.Lon, g.EndLat, g.EndLon), la, lo;
            NavigationMath.Offset(g.Lat, g.Lon, c + 180, 6000, R, out la, out lo); g.Step(la, lo, 400, 330, 70, false, R, 45, c);
            bool far = g.Gear;
            NavigationMath.Offset(g.Lat, g.Lon, c + 180, 2000, R, out la, out lo); g.Step(la, lo, 180, 110, 60, false, R, 45, c);
            var o = new RunwayMission { Lat = g.Lat, Lon = g.Lon, EndLat = g.EndLat, EndLon = g.EndLon, Elevation = 70 };
            NavigationMath.Offset(g.Lat, g.Lon, c + 180, 3000, R, out la, out lo); o.Step(la, lo, 600, 530, 120, false, R, 45, c + 180);
            Check(!far && g.Gear && !o.Gear && o.Phase == "entry", "gear only on an aligned final inside 3 km, never on the outbound leg");
        }
        Console.WriteLine("Round-3 plan parser / speed cap / porpoise / telemetry / approach: 16 behavior checks passed.");
        // ---- Round 3 (end of session): eject confirm, chatter/alerts, "we passed KSP", status honesty ----
        {
            Check(DestructiveConfirm.Needs("eject_kerbal") && DestructiveConfirm.Needs("stage") && DestructiveConfirm.Needs("cut_engines") && !DestructiveConfirm.Needs("set_gear"), "destructive set");
            string q = DestructiveConfirm.Request("eject_kerbal", "{\"confirmed\":true}", 100);
            Check(q.StartsWith("NOT DONE") && q.Contains("yes"), "model path: eject held for a yes (even when the model sets confirmed itself)");
            string rep; Check(DestructiveConfirm.Answer("land at 27", 105, out rep) == null && rep == null && DestructiveConfirm.Pending == "eject_kerbal", "other chat doesn't confirm");
            var yes = DestructiveConfirm.Answer("Yes", 110, out rep);
            Check(yes != null && yes.Value.Key == "eject_kerbal" && yes.Value.Value.Contains("\"confirmed\":true") && DestructiveConfirm.Pending == null, "explicit yes runs it once");
            DestructiveConfirm.Request("stage", "{}", 200);
            Check(DestructiveConfirm.Answer("no", 205, out rep) == null && rep.StartsWith("Cancelled") && DestructiveConfirm.Pending == null, "no cancels");
            DestructiveConfirm.Request("stage", "{}", 300);
            Check(DestructiveConfirm.Answer("yes", 300 + DestructiveConfirm.WindowS + 1, out rep) == null, "a stale yes (after 60 s) does nothing");
            Check(ToolRouter.Direct("EJECT", "plane") == null && ToolRouter.Direct("eject", "") == null, "router never runs eject directly");
            var solo = new CrewChatter(new Random(2));
            var crew1 = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Sidry Kerman", "pilot") };
            var em = solo.Emergency("flameout", "Wheesley", crew1, "Sidry Kerman", 50);
            Check(em.Count == 1 && em[0].Name == "Sidry Kerman" && em[0].Trait == "pilot", "solo pilot reacts to emergencies (was silent)");
            CrewLine sl = null; for (int s = 0; s < 2000 && sl == null; s += 5) sl = solo.SoloTick(400 + s, true, crew1, "Sidry Kerman", "altitude 900 m, speed 150 m/s");
            Check(sl != null && sl.Name == "Sidry Kerman" && sl.AllowedNumbers.Contains("900"), "solo idle chatter every 4-8 min");
            Check(ToolRouter.Direct("we are flying away from ksp", "plane").Value.Key == "land", "'we are flying away from ksp' -> resume landing");
            Check(ToolRouter.Direct("we passed the runway", "plane").Value.Key == "land", "'we passed the runway' -> land");
            Check(PilotPolicy.StatusLine("hold", "Aeris 3A", null, double.NaN, 900, 150, 273, 0).Contains("NOT landing") && PilotPolicy.StatusLine("landing", "Aeris 3A", "intercept", 8000, 900, 150, 273, 0).Contains("LANDING (intercept, 8.0 km"), "status says plainly whether we're landing");
        }
        Console.WriteLine("Round-3 confirm / chatter / passed-KSP / status: 13 behavior checks passed.");
        // ---- P5-1.8: dashboard honesty ----
        var br = new List<string[]> { new[] { "autopilot", "BRIDGE hold" } };
        Check(DashboardRows.Choose(false, br, 1, "hold", "p")[1][1] == "Local hold", "AI off shows local rows");
        Check(DashboardRows.Choose(true, br, 1, "idle", "")[0][1] == "bridge" && DashboardRows.Choose(true, br, 1, "idle", "")[1][1] == "BRIDGE hold", "fresh bridge rows labeled bridge");
        Check(DashboardRows.Choose(true, br, 30, "idle", "")[0][1] == "local (in-mod)", "stale bridge rows replaced by local");
        Check(DashboardRows.Choose(true, null, 1, "idle", "")[1][1] == "Local idle" && DashboardRows.Choose(true, br, -1, null, null)[1][1] == "Local idle", "failed poll / never polled shows local");
        Console.WriteLine("P5-1 dashboard honesty: 4 behavior checks passed.");
        // ---- P5-1.9: single native identity ----
        var gid = new Guid("0123456789abcdef0123456789abcdef");
        Check(NativeIds.Vessel(gid) == "01234567-89ab-cdef-0123-456789abcdef", "vessel id is GUID D form");
        Check(NativeIds.Part(4294967295u) == "4294967295", "part id is invariant flightID");
        Check(NativeIds.SameVessel("01234567-89AB-CDEF-0123-456789ABCDEF", NativeIds.Vessel(gid)) && !NativeIds.SameVessel("1234", NativeIds.Vessel(gid)), "vessel id compare tolerant of case, rejects kRPC ints");
        Console.WriteLine("P5-1 native ids: 3 behavior checks passed.");
        // ---- P5-2: flight residual ports (policy) ----
        Check(FlightResidualPolicy.LandRoute("", true, false, true, false) == "grounded" && FlightResidualPolicy.LandRoute("", false, true, true, false) == "heli"
            && FlightResidualPolicy.LandRoute("", false, false, true, false) == "plane" && FlightResidualPolicy.LandRoute("ksc", false, false, true, false) == "spot"
            && FlightResidualPolicy.LandRoute("", false, false, false, false) == "vertical", "land alias routes by craft/where");
        Check(FlightResidualPolicy.RunwayAlias("ksc") == "KSC Runway" && FlightResidualPolicy.RunwayAlias("Island 09") == "Island Runway 09", "KSC runway alias");
        Check(FlightResidualPolicy.Throttle(0.5) == 0.5 && FlightResidualPolicy.Throttle(75) == 0.75 && FlightResidualPolicy.Throttle(-1) == 0 && FlightResidualPolicy.Throttle(500) == 1, "throttle fraction/percent clamp");
        Check(FlightResidualPolicy.SasMode("Radial Out") == "RadialOut" && FlightResidualPolicy.SasMode("node") == "Maneuver" && FlightResidualPolicy.SasMode("sideways") == null, "SAS mode names");
        Check(FlightResidualPolicy.FlapDegrees("up") == 0 && FlightResidualPolicy.FlapDegrees("2") == 20 && FlightResidualPolicy.FlapDegrees("full") == 30 && FlightResidualPolicy.FlapDegrees("7") == -1, "flap settings");
        Check(FlightResidualPolicy.CircleBank("right", 40, 100) == 20 && FlightResidualPolicy.CircleBank("left", 15, 300) == -10 && FlightResidualPolicy.CircleBank("left", 1, 100) == -5, "circle bank sign/limits");
        Check(FlightResidualPolicy.Turn(350, "right", 30) == 20 && FlightResidualPolicy.Turn(10, "left", 90) == 280, "turn wraps heading");
        Check(FlightResidualPolicy.ConfirmGate(false, "x") != null && FlightResidualPolicy.ConfirmGate(true, "x") == null, "destructive tools need confirmed");
        Check(FlightResidualPolicy.ActionGroup(3) == 3 && FlightResidualPolicy.ActionGroup(0) == 0 && FlightResidualPolicy.ActionGroup(11) == 0 && FlightResidualPolicy.ActionGroup(2.5) == 0, "action group range");
        Check(Math.Abs(FlightResidualPolicy.DeltaV(300, 10, 5) - 300 * 9.80665 * Math.Log(2)) < 1e-6 && FlightResidualPolicy.DeltaV(300, 5, 10) == 0, "delta-v rocket equation");
        Check(FlightResidualPolicy.LandingEta(100, -10) == 10 && double.IsNaN(FlightResidualPolicy.LandingEta(100, 2)) && FlightResidualPolicy.LandingEta(0, 0) == 0, "landing ETA");
        Check(FlightResidualPolicy.Distance(850) == "850 m" && FlightResidualPolicy.Distance(2500) == "2.5 km" && FlightResidualPolicy.Distance(42000) == "42 km"
            && FlightResidualPolicy.Duration(3725) == "1h 2m" && FlightResidualPolicy.Duration(75) == "1m 15s", "distance/duration text");
        var famt = new Dictionary<string, double> { { "LiquidFuel", 90 }, { "Oxidizer", 110 } }; var fcap = new Dictionary<string, double> { { "LiquidFuel", 360 }, { "Oxidizer", 440 }, { "Ablator", 10 } };
        Check(FlightResidualPolicy.FuelReport(famt, fcap) == "LiquidFuel 90/360 (25%); Oxidizer 110/440 (25%)" && FlightResidualPolicy.FuelReport(famt, new Dictionary<string, double>()) == "No fuel tanks.", "fuel report");
        Check(FlightResidualPolicy.LandingCheck(true, true, 60, -3, 100) == "Landing check OK." && FlightResidualPolicy.LandingCheck(true, false, 150, -20, 100).Contains("gear up")
            && FlightResidualPolicy.LandingCheck(true, false, 150, -20, 100).Contains("too fast") && FlightResidualPolicy.LandingCheck(true, false, 150, -20, 100).Contains("speed"), "landing check gates");
        Check(double.IsNaN(FlightResidualPolicy.PropField("", 0, 460)) && FlightResidualPolicy.PropField("600", 0, 460) == 460 && FlightResidualPolicy.PropField("50%", 0, 100) == 50
            && FlightResidualPolicy.PropSwitch("on") == true && FlightResidualPolicy.PropSwitch("") == null, "prop_control parsing");
        Check(NativeCommands.IsPorted("fly_to") && NativeCommands.IsPorted("cut_engines") && NativeCommands.IsPorted("damage_report") && NativeCommands.IsPorted("land"), "P5-2 tools ported");
        Console.WriteLine("P5-2 flight residuals: 16 behavior checks passed.");
        // ---- P5-3: lifecycle + science (policy; parity with kspchat/science.py) ----
        Check(SciencePolicy.NormalizeMode("on") == "auto" && SciencePolicy.NormalizeMode("auto off") == "remind" && SciencePolicy.NormalizeMode("OFF") == "off" && SciencePolicy.NormalizeMode("loud") == null, "watcher mode aliases");
        Check(SciencePolicy.RunBlocker(9.9) != null && SciencePolicy.RunBlocker(10) == null && SciencePolicy.EcPct(5, 0) == 0 && SciencePolicy.EcPct(25, 100) == 25, "EC run threshold 10%");
        Check(!SciencePolicy.ShouldTransmit(true, 2, 24.9) && SciencePolicy.ShouldTransmit(true, 2, 25) && !SciencePolicy.ShouldTransmit(true, 0, 90) && !SciencePolicy.ShouldTransmit(false, 2, 90), "EC transmit threshold 25%");
        Check(SciencePolicy.Eligible(false, false, true, false, false, false) && !SciencePolicy.Eligible(true, false, true, false, true, false) && !SciencePolicy.Eligible(false, true, true, false, true, false)
            && !SciencePolicy.Eligible(false, false, false, false, true, false) && !SciencePolicy.Eligible(false, false, true, true, true, false) && !SciencePolicy.Eligible(false, false, true, false, false, true), "experiment eligibility safeguards");
        Check(SciencePolicy.SituationChange("auto", null, null, "k", "v") == "first" && SciencePolicy.SituationChange("auto", "k1", "v", "k2", "v2") == "first"
            && SciencePolicy.SituationChange("auto", "k1", "v", "k2", "v") == "act" && SciencePolicy.SituationChange("auto", "k", "v", "k", "v") == "ignore" && SciencePolicy.SituationChange("off", "k1", "v", "k2", "v") == "ignore", "situation change gate (first load quiet)");
        Check(!SciencePolicy.ShouldPost("auto", 0, null, 3, 999) && SciencePolicy.ShouldPost("auto", 1, null, 0, 0) && !SciencePolicy.ShouldPost("remind", 0, null, 0, 999)
            && SciencePolicy.ShouldPost("remind", 0, null, 2, 61) && !SciencePolicy.ShouldPost("remind", 0, null, 2, 10), "watcher post gating");
        Check(SciencePolicy.ParachuteGate(false, "LANDED", -1) != null && SciencePolicy.ParachuteGate(false, "FLYING", 10) != null && SciencePolicy.ParachuteGate(false, "FLYING", -40) == null && SciencePolicy.ParachuteGate(true, "LANDED", 0) == null, "parachute gate");
        Check(SciencePolicy.LaunchSite("SPH", "") == "Runway" && SciencePolicy.LaunchSite("vab", null) == "LaunchPad" && SciencePolicy.LaunchSite("VAB", "Desert_Launch_Site") == "Desert_Launch_Site" && SciencePolicy.EditorFolder("sph") == "SPH", "launch site defaults");
        Check(SciencePolicy.SafeCraftName("Kerbal X") && !SciencePolicy.SafeCraftName("../x") && !SciencePolicy.SafeCraftName("a/b") && !SciencePolicy.SafeCraftName(" "), "craft name has no path");
        Check(NativeCommands.IsPorted("stage") && NativeCommands.IsPorted("run_science") && NativeCommands.IsPorted("set_science_watcher") && NativeCommands.IsPorted("launch_craft"), "P5-3 tools ported");
        Console.WriteLine("P5-3 lifecycle + science: 10 behavior checks passed.");
        // ---- P5-4: orbital math (Kerbin mu 3.5316e12, R 600 km) ----
        double kmu = 3.5316e12, kr = 600000;
        Check(Math.Abs(OrbitMath.VisViva(kmu, 700000, 700000) - Math.Sqrt(kmu / 700000)) < 1e-6 && double.IsNaN(OrbitMath.VisViva(kmu, 0, 1)), "vis-viva");
        double aSub = (kr + 80000 + kr - 100000) / 2;   // apo 80 km, peri -100 km
        double circ = OrbitMath.CircularizeDv(kmu, kr + 80000, aSub);
        Check(circ > 150 && circ < 220 && Math.Abs(OrbitMath.CircularizeDv(kmu, kr + 80000, kr + 80000)) < 1e-6, "circularize dv at apoapsis");
        double hoh = OrbitMath.ApsisChangeDv(kmu, kr + 80000, kr + 80000, kr + 2863330);
        Check(hoh > 600 && hoh < 800 && OrbitMath.ApsisChangeDv(kmu, kr + 80000, kr + 80000, kr + 30000) < 0, "apsis change dv sign/size (raise ~ +680, deorbit < 0)");
        double nrm, prg; OrbitMath.InclinationDv(2279, 10, true, out nrm, out prg);
        Check(Math.Abs(nrm - 2279 * Math.Sin(10 * Math.PI / 180)) < 1e-6 && prg < 0, "inclination dv at AN");
        OrbitMath.InclinationDv(2279, 10, false, out nrm, out prg);
        Check(nrm < 0, "inclination dv flips at DN");
        Check(Math.Abs(OrbitMath.SyncAltitude(kmu, 21549.425, kr) / 1000 - 2863.33) < 1, "Kerbin synchronous altitude ~2863 km");
        Check(OrbitMath.UseApoapsis(100, 900, 70000) && !OrbitMath.UseApoapsis(900, 100, 70000) && OrbitMath.UseApoapsis(900, 100, -5000), "circularize burn point");
        Check(OrbitMath.WarpUt(1000, 1100, 30) == 1070 && OrbitMath.WarpUt(1000, 1010, 30) == 1000, "warp lead never in the past");
        Check(OrbitMath.ApsisGate("Apoapsis", 50, 70000, true, 0) != null && OrbitMath.ApsisGate("Periapsis", 90, 80000, false, 0) != null && OrbitMath.ApsisGate("Apoapsis", 100, 70000, true, 0) == null
            && OrbitMath.ApsisGate("Periapsis", -1, 1, false, 0) != null, "apsis request gates");
        Check(NativeCommands.IsPorted("circularize") && NativeCommands.IsPorted("mechjeb_ascent") && NativeCommands.IsPorted("sun_lock") && NativeCommands.IsPorted("transfer_to"), "P5-4 ported set (planners included)");
        Console.WriteLine("P5-4 orbital math: 10 behavior checks passed.");
        // ---- P5-5: embedded llama.cpp (runtime layout, zip extract, prompt/tool parsing, ABI guard) ----
        Check(LlamaRuntime.BinName("llama.dll") == "llama.bin" && LlamaRuntime.BinName("bin/ggml-cpu-haswell.dll") == "ggml-cpu-haswell.bin" && LlamaRuntime.BinName("libomp.dll") == "libomp.bin"
            && LlamaRuntime.BinName("llama-server-impl.dll") == null && LlamaRuntime.BinName("llama-cli.exe") == null && LlamaRuntime.BinName("mtmd.dll") == null && LlamaRuntime.BinName("../llama.dll") == "llama.bin", "runtime keep-list -> .bin names");
        bool gdRefused = false; try { LlamaRuntime.CacheDir(@"C:\KSP\GameData\x"); } catch (InvalidOperationException) { gdRefused = true; }
        Check(gdRefused && LlamaRuntime.CacheDir(@"C:\Users\u\AppData\Local").EndsWith(LlamaRuntime.Tag), "load cache outside GameData");
        Check(LlamaRuntime.GpuLayers(AiOffloadMode.Gpu, 18) == 999 && LlamaRuntime.GpuLayers(AiOffloadMode.Hybrid, 18) == 18 && LlamaRuntime.GpuLayers(AiOffloadMode.Cpu, 18) == 0, "GPU/Hybrid/CPU layers");
        Check(LlamaRuntime.ContextTokens(4096) == 16384 && LlamaRuntime.ContextTokens(20480) == 20480 && LlamaRuntime.ContextTokens(65536) == 24576 && LlamaRuntime.Threads(16) == 8 && LlamaRuntime.Threads(1) == 1, "context 16-24k, threads");
        Check(LlamaRuntime.ZipUrl.Contains("/" + LlamaRuntime.Tag + "/") && LlamaRuntime.ZipSha256.Length == 64, "runtime pinned (tag + SHA-256)");
        string zdir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aics-zip-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(zdir);
        try
        {
            string src = System.IO.Path.Combine(zdir, "src"); System.IO.Directory.CreateDirectory(src);
            foreach (string nm in new[] { "llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu-x64.dll", "llama-server.exe", "llama-common.dll" }) CreateTempBytes(src, nm, 5000 + nm.Length);
            string zp = System.IO.Path.Combine(zdir, "rt.zip"); System.IO.Compression.ZipFile.CreateFromDirectory(src, zp);
            string nd = System.IO.Path.Combine(zdir, "PluginData", "native");
            var mgr = new LlamaRuntimeManager(nd);
            Check(mgr.Install(zp, "00") != null && !mgr.Ready, "runtime checksum mismatch refused");
            Check(mgr.Install(zp, LlamaRuntime.Sha256(zp)) == null && mgr.Ready, "runtime install from verified zip");
            var files = System.IO.Directory.GetFiles(nd);
            Check(Array.TrueForAll(files, f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".exe")) && files.Length == 5, "only kept libs stored, all non-.dll (4 .bin + manifest)");
            Check(System.IO.File.ReadAllBytes(System.IO.Path.Combine(nd, "ggml.bin")).Length == 5008, "MiniZip deflate round-trip");
            string cache = LlamaRuntime.Materialize(nd, System.IO.Path.Combine(zdir, "cache"));
            Check(System.IO.File.Exists(System.IO.Path.Combine(cache, "llama.dll")) && System.IO.File.Exists(System.IO.Path.Combine(cache, "ggml-cpu-x64.dll")), "materialize .bin -> cache .dll");
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(nd, "ggml.bin"), new byte[] { 1 });
            bool damaged = false; try { LlamaRuntime.Materialize(nd, System.IO.Path.Combine(zdir, "cache2")); } catch (InvalidOperationException) { damaged = true; }
            Check(damaged, "damaged runtime file refused");
        }
        finally { try { System.IO.Directory.Delete(zdir, true); } catch (Exception) { } }
        var msgs = new System.Collections.ArrayList {
            new Dictionary<string, object> { { "role", "system" }, { "content", "SYS" } },
            new Dictionary<string, object> { { "role", "user" }, { "content", "gear down" } },
            new Dictionary<string, object> { { "role", "assistant" }, { "content", "" }, { "tool_calls", new System.Collections.ArrayList { new Dictionary<string, object> { { "id", "call_1" }, { "function", new Dictionary<string, object> { { "name", "set_gear" }, { "arguments", "{\"down\":true}" } } } } } } },
            new Dictionary<string, object> { { "role", "tool" }, { "content", "Gear set." } } };
        var toolList = (System.Collections.IList)MiniJson.DeserializeObject("[{\"type\":\"function\",\"function\":{\"name\":\"set_gear\"}}]");
        string qprompt = EmbeddedPrompt.Render(msgs, toolList);
        Check(qprompt.StartsWith("<|im_start|>system\nSYS\n\n# Tools") && qprompt.Contains("<tools>\n{") && qprompt.Contains("<|im_start|>user\ngear down<|im_end|>")
            && qprompt.Contains("<tool_call>\n{\"name\": \"set_gear\", \"arguments\": {\"down\":true}}\n</tool_call><|im_end|>")
            && qprompt.Contains("<|im_start|>user\n<tool_response>\nGear set.\n</tool_response><|im_end|>") && qprompt.EndsWith("<|im_start|>assistant\n"), "Qwen ChatML render with tools / calls / responses");
        var reply = MiniJson.Deserialize(EmbeddedPrompt.ToOpenAi("Lowering gear.\n<tool_call>\n{\"name\": \"set_gear\", \"arguments\": {\"down\": true}}\n</tool_call>"));
        var rmsg = (Dictionary<string, object>)((Dictionary<string, object>)((System.Collections.IList)reply["choices"])[0])["message"];
        var rcall = (Dictionary<string, object>)((Dictionary<string, object>)((System.Collections.IList)rmsg["tool_calls"])[0])["function"];
        Check((string)rmsg["content"] == "Lowering gear." && (string)rcall["name"] == "set_gear" && ((string)rcall["arguments"]).Contains("\"down\""), "tool_call parsed to OpenAI shape");
        var plain = (Dictionary<string, object>)((Dictionary<string, object>)((System.Collections.IList)MiniJson.Deserialize(EmbeddedPrompt.ToOpenAi("Hi there<|im_end|>"))["choices"])[0])["message"];
        var cut = (Dictionary<string, object>)((Dictionary<string, object>)((System.Collections.IList)MiniJson.Deserialize(EmbeddedPrompt.ToOpenAi("Ok <tool_call>{\"name\": \"set_g"))["choices"])[0])["message"];
        Check((string)plain["content"] == "Hi there" && !plain.ContainsKey("tool_calls") && (string)cut["content"] == "Ok", "plain reply / truncated call hidden");
        Check(LlamaNative.CheckDefaults(512, 2048, 512, 1, 1) == null && LlamaNative.CheckDefaults(0, 0, 0, 0, 7) != null, "ABI default guard");
        Check(OpenAiBackend.Resolve("embedded").Error != null && OpenAiBackend.Resolve("embedded").Url == OpenAiBackend.EmbeddedUrl, "embedded provider reports missing downloads");
        Console.WriteLine("P5-5 embedded llama: 15 behavior checks passed.");
        // ---- P5-6: versioned PluginData migration (backup first, never wipe) ----
        string pd = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aics-pd-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(pd);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(pd, ".env"), "GROQ_API_KEY=x");
            System.IO.File.WriteAllText(System.IO.Path.Combine(pd, "bridge_settings.json"), "{\"science_mode\":\"auto\"}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(pd, "native_settings.json"), "{\"spots\":{\"Island\":{\"lat\":1}},\"craft_notes\":{\"A\":{}}}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(pd, "kerbal_personalities.json"), "{}");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(pd, "models"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(pd, "models", "big.gguf"), "w");
            Check(PluginDataMigration.Version(pd) == 0 && PluginDataMigration.UserFiles(pd).Count == 4, "user files found (models excluded)");
            string rep = PluginDataMigration.Run(pd, new DateTime(2026, 10, 9, 21, 0, 0, DateTimeKind.Utc));
            var bdir = System.IO.Path.Combine(pd, "backups", "v0-20261009-210000");
            Check(rep != null && rep.Contains("4 file(s)") && System.IO.File.Exists(System.IO.Path.Combine(bdir, ".env")) && System.IO.File.Exists(System.IO.Path.Combine(bdir, "native_settings.json")), "backup before migration");
            var ns = MiniJson.Deserialize(System.IO.File.ReadAllText(System.IO.Path.Combine(pd, "native_settings.json")));
            Check((string)ns["science_mode"] == "auto" && ns.ContainsKey("spots") && ns.ContainsKey("craft_notes"), "science mode imported, spots/notes kept");
            Check(System.IO.File.ReadAllText(System.IO.Path.Combine(pd, ".env")) == "GROQ_API_KEY=x" && System.IO.File.Exists(System.IO.Path.Combine(pd, "bridge_settings.json"))
                && System.IO.File.Exists(System.IO.Path.Combine(pd, "models", "big.gguf")), "keys/bridge settings/models untouched");
            Check(PluginDataMigration.Version(pd) == PluginDataMigration.Current && PluginDataMigration.Run(pd, DateTime.UtcNow) == null, "migration idempotent");
            System.IO.File.WriteAllText(System.IO.Path.Combine(pd, "data_version.txt"), "1");
            System.IO.File.WriteAllText(System.IO.Path.Combine(pd, "native_settings.json"), "{\"science_mode\":\"off\"}");
            PluginDataMigration.Run(pd, new DateTime(2026, 10, 9, 22, 0, 0, DateTimeKind.Utc));
            Check((string)MiniJson.Deserialize(System.IO.File.ReadAllText(System.IO.Path.Combine(pd, "native_settings.json")))["science_mode"] == "off", "existing native value never overwritten");
        }
        finally { try { System.IO.Directory.Delete(pd, true); } catch (Exception) { } }
        Console.WriteLine("P5-6 PluginData migration: 6 behavior checks passed.");
        string smoke = Environment.GetEnvironmentVariable("AICS_LLAMA_SMOKE");
        if (!string.IsNullOrEmpty(smoke))
        {
            // opt-in: AICS_LLAMA_SMOKE=<dir with the pinned runtime zip + a tiny .gguf>; runs real llama.cpp on net472, CPU only
            string zip = System.IO.Path.Combine(smoke, LlamaRuntime.ZipName), nd = System.IO.Path.Combine(smoke, "native");
            Check(LlamaRuntime.Sha256(zip) == LlamaRuntime.ZipSha256, "smoke: pinned zip SHA");
            var rtm = new LlamaRuntimeManager(nd); Check(rtm.Install(zip, LlamaRuntime.ZipSha256) == null, "smoke: real runtime extract");
            Check(!Array.Exists(System.IO.Directory.GetFiles(nd), f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)), "smoke: no .dll stored in native dir");
            string cache = LlamaRuntime.Materialize(nd, System.IO.Path.Combine(smoke, "cache", LlamaRuntime.Tag));
            LlamaNative.EnsureBackend(cache);
            string gguf = System.IO.Directory.GetFiles(smoke, "*.gguf")[0];
            using (var l = new LlamaNative(gguf, 0, 16384, 2))
            {
                string text = l.Generate("Once upon a time", 24, 0.3f, null);
                Check(l.ContextTokens >= 16384 && text.Length > 0, "smoke: real generate");
                Console.WriteLine("P5-5 llama smoke: generated " + text.Length + " chars on CPU: " + text.Replace("\n", " ").Substring(0, Math.Min(60, text.Length)));
            }
        }
    }
    static string CreateTempBytes(string dir, string name, int size)
    {
        string path = System.IO.Path.Combine(dir, name);
        var bytes = new byte[size]; for (int i = 0; i < size; i++) bytes[i] = (byte)i;
        System.IO.File.WriteAllBytes(path, bytes); return path;
    }
}
