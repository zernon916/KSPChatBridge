using System;
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
        Console.WriteLine("Placement and rotor status: 12 behavior checks passed.");
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
        foreach (string invalid in new[] { "climb 500 vs 50", "cruise speed 150 garbage", "wait", "wait 0 s", "circle 0 laps", "cruise heading 999", "cruise speed 300", "land nowhere", "climb NaN", "wait 999999 min" })
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
    }
}
