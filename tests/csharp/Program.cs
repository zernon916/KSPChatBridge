using System;
using System.Collections.Generic;
using System.Threading;
using KSPChatBridge;

class Program
{
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
        var tools = chat.RunTools("t1", new[] { "plane_hold", "mechjeb_ascent" }, name => "ok", true);
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
        Check(fakeCalls == 2 && toolLog == "get_status:{}" && answer == "Altitude 5 km.", "in-mod tool loop");
        Console.WriteLine("In-mod AI stack: 7 behavior checks passed.");
        // ---- P5-1 foundation ----
        var cfgDefaults = BridgeConfigDefaults.Create();
        Check(cfgDefaults["native_chat"] == "true", "new installs default to native chat (no :8765 needed)");
        Check(cfgDefaults["ai_enabled"] == "true" && cfgDefaults["autostart"] == "true", "defaults keep AI + autostart");
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
    }
    static string CreateTempBytes(string dir, string name, int size)
    {
        string path = System.IO.Path.Combine(dir, name);
        var bytes = new byte[size]; for (int i = 0; i < size; i++) bytes[i] = (byte)i;
        System.IO.File.WriteAllBytes(path, bytes); return path;
    }
}
