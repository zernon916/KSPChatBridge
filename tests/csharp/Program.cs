using System;
using System.Collections.Generic;
using System.Threading;
using KSPChatBridge;

class Program
{
    static string Root() { var d = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "personalities.txt"))) d = d.Parent; return d == null ? "." : d.FullName; }
    class Servo { private float transformRateOfMotion = 380; }
    class Rotor : Servo { public float currentRPM = 0; }
    internal static string DirectOrNull(string m) { var d = ToolRouter.Direct(m, "plane"); return d == null ? "null" : d.Value.Key; }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static void Main()
    {
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
        {   // Luke 5:15 PM new plane: rotate near 1.15 x stall with progressive back-pressure (heavier craft, stall 70 m/s)
            var tk = new TakeoffMission(0); double v = 0, t = 0, firstPull = double.NaN, lift = double.NaN; bool ground = true;
            while (t < 120 && ground) { t += .1; v += .25; double p = tk.Step(t, v, 0, true, 70); if (p > 0 && double.IsNaN(firstPull)) firstPull = v; if (p >= 8.5 && v >= 1.1 * 70) { ground = false; lift = v; } }
            Check(!double.IsNaN(firstPull) && firstPull < 1.15 * 70 && firstPull > .9 * 70, "back-pressure starts near Vr, not at the runway end (" + firstPull.ToString("0") + ")");
            Check(lift <= 1.2 * 70 + 1, "heavier craft lifts off by ~1.2 x stall (" + lift.ToString("0") + ")");
            Check(tk.Step(60, 1.35 * 80.5, 0, true, 70) == 12, "still on the ground well past Vr: more back-pressure");
            Check(Math.Abs(TakeoffMission.RotateSpeed(70, 75) - 72.75) < .01 && TakeoffMission.RotateSpeed(70, 300) <= 1.2 * 80.5 + 1e-9, "measured liftoff speed refines Vr (bounded)");
            var tk2 = new TakeoffMission(0); tk2.Step(1, 82, 5, false, 70); Check(Math.Abs(tk2.LiftoffSpeed - 82) < 1e-9, "liftoff speed is measured");
            Check(!PilotPolicy.TrimAllowed(true, 0, 99, 0, 0) && !PilotPolicy.TrimAllowed(false, 80, 99, 0, 0) && !PilotPolicy.TrimAllowed(false, 500, 5, 0, 0) && PilotPolicy.TrimAllowed(false, 500, 30, .5, 1), "auto-trim never on ground / rotation / just after takeoff");
            double tr = 0; for (int i = 0; i < 200; i++) tr = PilotPolicy.TrimStep(tr, .8, 3); Check(Math.Abs(tr - PilotPolicy.TrimPitchMax) < 1e-9, "trim bounded");
            Check(PilotPolicy.TrimStep(.05, .8, 12) == .05, "anti-windup: no nose-up trim while nose-high");
            // level-off sim: nose-up trim + climb; hold pushes the elevator, trim bleeds, pitch comes down
            double trim = .15, pitch = 15, elev = 0; for (int i = 0; i < 100; i++) { double want = -2; elev = Math.Max(-1, Math.Min(1, .05 * (want - pitch))); trim = PilotPolicy.TrimBleed(trim, elev, .1); pitch += (elev * 6 + trim * 10) * .1; }
            Check(trim < .12 && pitch < 2, "altitude hold levels off despite nose-up trim (pitch " + pitch.ToString("0.0") + ")");
            Check(PilotPolicy.ResourceLevel(0, 7.5, false, false, true, 0) == "ok" && PilotPolicy.ResourceLevel(0, 7.5, true, false, true, 1) == "ok", "no MonoPropellant alarm without RCS or on the runway");
            Check(PilotPolicy.ResourceLevel(.02, 100, true, true, true, 1) == "fail" && PilotPolicy.ResourceLevel(.02, 100, true, true, false, .02) == "ok", "engine fuel still alarms on the ground; started-empty tanks don't");
            Console.WriteLine("Takeoff rotation / auto-trim / resource alarms: 12 checks passed.");
        }        {   // Luke 5:36 PM: no throttle / steering jolt at brake release
            var gr = new TakeoffGround(-0.0486, -74.7245, 90);
            Check(gr.Throttle(0) == 0 && gr.Release(10) && !gr.Release(11), "release recorded once");
            Check(gr.Throttle(10) == 0 && Math.Abs(gr.Throttle(12) - .5) < 1e-9 && gr.Throttle(15) == 1, "throttle ramps 25%/s after release");
            Check(TakeoffGround.Elevator(15, 60) == -.05 && double.IsNaN(TakeoffGround.Elevator(55, 60)), "elevator slightly nose-down below rotate");
            double maxStep = 0, prev = 0; for (int i = 0; i < 50; i++) { gr.Steer(18, 15, .02); maxStep = Math.Max(maxStep, Math.Abs(gr.Wheel - prev)); prev = gr.Wheel; }
            Check(maxStep <= .02 + 1e-9 && Math.Abs(gr.Yaw) <= .3, "steering and rudder rate-limited");
            var g2 = new TakeoffGround(0, 0, 90); g2.Release(0); for (int i = 0; i < 500; i++) g2.Steer(18, 5, .02);
            var g3 = new TakeoffGround(0, 0, 90); g3.Release(0); for (int i = 0; i < 500; i++) g3.Steer(18, 15, .02);
            Check(Math.Abs(g2.Wheel) < Math.Abs(g3.Wheel) && Math.Abs(g2.Yaw) < Math.Abs(g3.Yaw), "low-speed steering gains scaled down");
            double south = gr.CrossTrack(-0.0486 - 100 / (Math.PI / 180 * 600000), -74.7245, 600000);
            Check(Math.Abs(south - 100) < 1 && Math.Abs(gr.HeadingError(90, 100) + 5) < 1e-9 && Math.Abs(gr.HeadingError(90, 2) + .6) < 1e-9, "gentle bounded centerline correction (" + south.ToString("0.0") + ")");
            Check(!SciencePolicy.CanRun(4, 1, 0, false, true, false, true) && SciencePolicy.CanRun(4, 1, 1, false, true, false, true) && !SciencePolicy.CanRun(0, 1, 1, false, true, true, false), "science: skip crew-in-part / atmosphere experiments that can't run");
            Check(CraftClass.Classify(120, 60, 300) == "heavy" && CraftClass.Classify(8, 9, 25) == "light" && CraftClass.Effective("heavy", "fighter") == "light" && CraftClass.Effective("light", "gentle") == "heavy", "craft class + override");
            Check(CraftClass.MaxBank("heavy", 75) == 25 && CraftClass.MaxBank("light", 75) == 75 && CraftClass.JoinG("heavy", 4) == CraftClass.HeavyG && CraftClass.JoinG("light", 4) == 4, "airliner bank/g limits; fighters keep 4 g");
            double bc = 0; for (int i = 0; i < 10; i++) bc = CraftClass.RollStep("heavy", bc, 30, .1); Check(Math.Abs(bc - 6) < 1e-9 && CraftClass.RollStep("light", 0, 30, .1) == 30, "heavy roll rate 6 deg/s");
            Check(DirectOrNull("fly gentle") == "craft_class" && ToolRouter.Direct("fighter mode", "plane").Value.Value.Contains("fighter"), "chat override routes");
            Console.WriteLine("Takeoff ground roll, science reqs, craft class: 11 checks passed.");
        }        {   // heavy craft in a 25 deg turn holds altitude within 50 m (pitch lead + inertia-lagged plant)
            string cls = "heavy"; double alt = 0, vs = 0, pitch = 2, integ = 0, bank = 0, worst = 0;
            for (int i = 0; i < 1200; i++)
            {
                double dt = .1; bank = CraftClass.RollStep(cls, bank, CraftClass.MaxBank(cls, 75), dt);
                double tvs = Math.Max(-15, Math.Min(15, -.15 * CraftClass.VsScale(cls) * alt)); integ = Math.Max(-5, Math.Min(5, integ + (tvs - vs) * dt * .25 * .7));
                double cmd = Math.Max(-5, Math.Min(15, 1 + .8 * .7 * (tvs - vs) + integ + CraftClass.PitchLead(cls, bank)));
                pitch += (cmd - pitch) * dt / 2.5;   // heavy: slow pitch response
                double nz = 1 + .12 * (pitch - 1); vs += 9.81 * (nz * Math.Cos(bank * Math.PI / 180) - 1) * dt; alt += vs * dt; worst = Math.Max(worst, Math.Abs(alt));
            }
            Check(worst < 50 && Math.Abs(bank - 25) < 1e-6, "heavy turn holds altitude within 50 m (worst " + worst.ToString("0") + " m)");
            Check(CraftClass.FloorRecover("heavy", 140, 0) && CraftClass.FloorRecover("heavy", 300, -40) && !CraftClass.FloorRecover("heavy", 300, -10) && !CraftClass.FloorRecover("light", 100, -40), "heavy AGL floor: max(150 m, 10 s of sink)");
            Console.WriteLine("Heavy craft altitude: 2 checks passed.");
        }        {   // Luke 5:44 PM: asymptotic localizer-style leg intercept, no crossing > 2% of the turn radius
            double v = 120, bankD = 25, R = v * v / (9.81 * Math.Tan(bankD * Math.PI / 180)), wmax = 9.81 * Math.Tan(bankD * Math.PI / 180) / v * 180 / Math.PI;
            double worstCross = 0, lastX = 0;
            foreach (double start in new[] { 0.0, -45, -90, 30 })   // relative heading at start (course = 90; plane 4 turn radii right of the line)
            {
                double x = 4 * R, psi = 90 + start, crossMax = 0, rollC = 0;
                for (int i = 0; i < 12000; i++)
                {
                    double dt = .05, cmd = LocalizerCourse.Heading(90, x, R), err = FlightPolicy.Wrap(cmd - psi);
                    double wantRate = Math.Max(-wmax, Math.Min(wmax, .5 * err)); rollC += Math.Max(-8 * dt * wmax / 25, Math.Min(8 * dt * wmax / 25, wantRate - rollC));   // ~8 deg/s roll onset
                    psi += rollC * dt; x += v * Math.Sin((psi - 90) * Math.PI / 180) * dt * -1 * -1;
                    // x = metres right of course: heading right of course increases x
                    if (x < 0) crossMax = Math.Max(crossMax, -x);
                }
                worstCross = Math.Max(worstCross, crossMax / R); lastX = Math.Max(lastX, Math.Abs(x));
            }
            Check(worstCross < .02, "leg intercept: no crossing > 2% of the turn radius (worst " + (100 * worstCross).ToString("0.0") + "% of R)");
            Check(lastX < 20, "leg intercept: rolls out on the line (" + lastX.ToString("0") + " m)");
            Check(LocalizerCourse.InterceptAngle(5000, R) == 45 && LocalizerCourse.InterceptAngle(0, R) == 0 && LocalizerCourse.InterceptAngle(.1 * R, R) < 45, "intercept angle shrinks with cross-track");
            Console.WriteLine("Localizer-style leg intercept: 3 checks passed.");
        }        {   // Luke 5:46 PM: arcs tracked on the centre, not the outside edge (curvature feed-forward + 1.5 s look-ahead)
            double v = 120, Ra = 5000, bmax = 30, Rt = v * v / (9.81 * Math.Tan(bmax * Math.PI / 180));
            var pts = new List<double[]>(); for (int i = 0; i <= 10; i++) pts.Add(new[] { -3000 + 300.0 * i, 0.0 });
            for (int i = 1; i <= 40; i++) { double a = i * 3.0 * Math.PI / 180; pts.Add(new[] { Ra * Math.Sin(a), -Ra + Ra * Math.Cos(a) }); }   // right turn, centre (0,-Ra)
            Func<int, double> brg = j => Math.Atan2(pts[j + 1][0] - pts[j][0], pts[j + 1][1] - pts[j][1]) * 180 / Math.PI;
            Func<int, double> len = j => Math.Sqrt(Math.Pow(pts[j + 1][0] - pts[j][0], 2) + Math.Pow(pts[j + 1][1] - pts[j][1], 2));
            double e = -3000, n = 0, psi = 90, bank = 0, sum = 0, sumAbs = 0; int seg = 0, cnt = 0;
            for (int s = 0; s < 40000 && seg < pts.Count - 2; s++)
            {
                double dt = .05, b = brg(seg) * Math.PI / 180, de = e - pts[seg][0], dn = n - pts[seg][1];
                double along = de * Math.Sin(b) + dn * Math.Cos(b), xte = de * Math.Cos(b) - dn * Math.Sin(b), rem = len(seg) - along;
                if (rem <= 0) { seg++; continue; }
                double kB = seg > 0 ? PathCurvature.AtVertex(brg(seg - 1), brg(seg), len(seg - 1), len(seg)) : 0, kN = PathCurvature.AtVertex(brg(seg), brg(seg + 1), len(seg), len(seg + 1));
                double cmd = LocalizerCourse.Heading(brg(seg), xte, Rt), ff = PathCurvature.Bank(v, PathCurvature.Ahead(kB, kN, rem, v), bmax);
                double want = Math.Max(-bmax, Math.Min(bmax, ff + .5 * FlightPolicy.Wrap(cmd - psi)));
                bank += Math.Max(-8 * dt, Math.Min(8 * dt, want - bank));
                psi += 9.81 * Math.Tan(bank * Math.PI / 180) / v * 180 / Math.PI * dt;
                e += v * Math.Sin(psi * Math.PI / 180) * dt; n += v * Math.Cos(psi * Math.PI / 180) * dt;
                if (seg > 18 && seg < pts.Count - 4) { double rad = Math.Sqrt(e * e + (n + Ra) * (n + Ra)) - Ra; sum += rad; sumAbs += Math.Abs(rad); cnt++; }
            }
            Check(cnt > 0 && Math.Abs(sum / cnt) < 10 && sumAbs / cnt < 10, "arc: mean cross-track < 10 m, no outside bias (bias " + (sum / Math.Max(1, cnt)).ToString("0.0") + " m, mean |e| " + (sumAbs / Math.Max(1, cnt)).ToString("0.0") + " m)");
            Check(PathCurvature.AtVertex(90, 180, 5000, 5000) == 0 && PathCurvature.Ahead(0, .001, 100, 120) == .001 && PathCurvature.Ahead(0, .001, 1000, 120) == 0, "corners between long legs aren't arcs; 1.5 s look-ahead");
            Console.WriteLine("Arc feed-forward: 2 checks passed.");
        }        { var crew1 = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Sidry Kerman", "Pilot") };
            { var noPilot = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Bill Kerman", "Engineer"), new KeyValuePair<string, string>("Bob Kerman", "Scientist") };
              Check(CrewVoice.Speaker(noPilot, "Max") == "AUTOPILOT" && CrewVoice.Line("AUTOPILOT", "Gear down.") == "AUTOPILOT: Gear down." && CrewVoice.Speaker(crew1, "") == "Sidry", "no Pilot aboard: AUTOPILOT voices flight replies"); }
        }
        Check(PilotPolicy.ApproachFloorVs("final", 18800, 200, 200, double.NaN, -10, -24, false) >= 3 && PilotPolicy.ApproachFloorVs("entry", 20000, 300, 300, double.NaN, -10, -24, true) >= 3
            && PilotPolicy.ApproachFloorVs("entry", 20000, 300, 300, double.NaN, -10, -5, false) == -10 && PilotPolicy.FloorAgl(-24, true) == 360, "5:50 PM water landing: floor = max(150 m, 10-15 s of sink) recovers a -24 m/s dive at 200-300 m");
        Check(MfdNav.ApStatus("idle", false, false) == "AP OFF" && MfdNav.ApStatus("landing", true, true) == "AP ENGAGED APPROACH LOC GS" && MfdNav.ApStatus("hold", false, false) == "AP ENGAGED HOLD" && MfdNav.FuelStatus(0, 100) == "!Empty!" && MfdNav.FuelStatus(85, 100) == "LF 85%" && MfdNav.FuelStatus(0, 0) == "", "MAP/CHART status line: AP annunciator + fuel");
        Check(NativeRecovery.InOwnGrace(10, 9) && !NativeRecovery.InOwnGrace(13.5, 10) && !NativeRecovery.InOwnGrace(5, double.NegativeInfinity), "own trim/flap/airbrake actuations whitelisted for 3 s (no self-tamper alarm)");
        {   // Luke 5:59 PM: in-flight stall learning from AoA vs CL (no stalling)
            double m = 20000, A = 10, trueVs = Math.Sqrt(2 * m * 9.81 / (1.225 * A * (.08 * 13 + .1)));
            foreach (double sgn in new[] { 1.0, -1.0 })
            {
                var L = new StallLearner(); var rnd = new Random(1);
                for (int i = 0; i < 300; i++) { double a = 1 + 5 * rnd.NextDouble(), cl = .08 * a + .1, q = m * 9.81 / (A * cl); L.Add(sgn * a, q, m, 1, A, 2, .5); }
                double ms = L.MeasuredStall(m, A);
                Check(Math.Abs(ms - trueVs) / trueVs < .01 && L.Confidence > .99, "stall learned from level flight (" + ms.ToString("0.0") + " vs " + trueVs.ToString("0.0") + " m/s, sign " + sgn + ")");
                var L2 = StallLearner.Load(L.Save()); Check(Math.Abs(L2.MeasuredStall(m, A) - ms) < 1e-6, "stall learning persists per craft");
            }
            var few = new StallLearner(); for (int i = 0; i < 5; i++) few.Add(3, 5000, m, 1, A, 0, 0);
            Check(double.IsNaN(few.MeasuredStall(m, A)) && !few.Add(3, 5000, m, 1, A, 40, 0) && !few.Add(3, 100, m, 1, A, 0, 0), "too few / unsteady samples are not used");
            Check(StallLearner.Effective(60, 50, .3) == 60 && StallLearner.Effective(50, 60, .3) == 60 && StallLearner.Effective(60, 50, .9) == 50 && StallLearner.Effective(60, double.NaN, 1) == 60, "conservative: higher estimate until confident");
            Console.WriteLine("Stall learning: 7 checks passed.");
        }
        { var rmx = new RunwayMission(); Check(rmx.Ils == null && double.IsNaN(RunwayMission.BelowGsM(rmx.Ils)) && RunwayMission.BelowGsM(new Ils.Reading { AboveGsM = -40 }) == 40, "6:09 PM NRE: below-GS reading is safe before the ILS exists (approach entry)"); }
        Check(MfdNav.DefaultPage(0) == "map" && MfdNav.DefaultPage(1) == "ils" && MfdNav.DefaultPage(2) == "aircraft" && MfdNav.DefaultPage(3) == "chart" && MfdNav.DefaultPage(4) == "all" && MfdNav.DefaultPage(5) == "map", "IVA screens get MAP/ILS/AP/CHART/COMMS defaults");
        Check(NavigationMath.Distance(0, 0, 0, 0, 600000) == 0, "coincident distance");
        Check(Math.Abs(NavigationMath.Bearing(0, 0, 0, 1) - 90) < 1e-6, "east bearing");
        var landing = new RunwayMission { Lat=0, Lon=0, EndLat=0, EndLon=.2, Elevation=70, Phase="final" };
        landing.Step(0, -.1, 130, 60, 65, false, 600000, 45);
        Check(landing.Gear && Math.Abs(landing.DesiredSpeed - ApproachProfile.AppSpeed(45)) < .01 && landing.DesiredVs >= -4.5, "final speed/sink/gear");
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
        Check(!recovery.Tick(true, 1) && !recovery.Tick(true, 2.9), "recovery delay (fumble >= 2 s)");
        Check(recovery.Tick(true, 5.01), "stable mismatch restores (fumble <= 4 s)");
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
        Check(safeguards.Noticed.Count == 4, "each tampered module noticed once (pilot callout)");
        Check(safeguards.Tick(vessel, 4.01, true) == 4 && safeguards.Noticed.Count == 0, "restore changed modules by identity");
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
        Check(reversers.Noticed.Count == 2, "reverser tampering noticed (callout)");
        Check(reversers.Recover(vessel, 4.01, true) == 2 && multi.runningPrimary && reverseEvent.active, "forward recovery confirmed");
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
        Check(safeguards.Tick(vessel, 4.01, true) == 3 && resource.flowState, "flow and group configuration restored");
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
        Check(taxi.Throttle > 0 && taxi.Throttle <= TaxiMission.MaxThrottle(1) && taxi.Drive == 0, "engine taxi throttle low and capped");
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
        Check(PluginDataMigration.ParseAiEnabled("# x\nai_enabled = false\n", true) == false && PluginDataMigration.ParseAiEnabled("autostart = true\n", true) && PluginDataMigration.ParseAiEnabled("", false) == false, "aics.cfg / old bridge.cfg: ai_enabled read, default kept");
        Console.WriteLine("AI on/off setting: 1 behavior check passed.");
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
        Check(NativeSafety.ShouldRevert(true, true) && !NativeSafety.ShouldRevert(true, false) && !NativeSafety.ShouldRevert(false, true), "sabotage revert only in flight with the controller active");
        Check(NativeSafety.ParkingAction(true, true, true, true, true, true, false, false) == "rearm", "parking re-arms after landing");
        Check(NativeSafety.ParkingAction(true, false, false, false, true, true, false, false) == "set", "parking set when grounded idle with wheels");
        Check(NativeSafety.ParkingAction(true, false, false, true, true, true, false, true) == "released", "player brake-off releases parking");
        Check(NativeSafety.ParkingAction(true, false, false, false, false, true, false, false) == "none"
            && NativeSafety.ParkingAction(true, false, false, false, true, false, false, false) == "none"
            && NativeSafety.ParkingAction(false, false, false, false, true, true, false, false) == "none"
            && NativeSafety.ParkingAction(true, false, true, false, true, true, false, false) == "none", "parking exempt: busy mode, no wheels, airborne, released");
        Check(NativeSafety.ParkingAction(true, false, false, true, true, true, true, false) == "none", "parking holds while set");
        Console.WriteLine("Native safety tick: 7 behavior checks passed.");
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
        Check(lines.Count == crew.Count && lines[0].Name == "Sidry Kerman" && lines.Exists(l => l.ToString().Contains("Bob (Sci)")), "emergency: pilot first, then every named kerbal (Luke)");
        Check(cc.Emergency("parts", "Wing", crew, "Sidry Kerman", 105).Count == 0, "emergency chatter rate-limited per member");
        var tc = new CrewChatter(new Random(2)); int posted = 0;
        for (int s = 0; s < 1200; s++) { var o = tc.TripTick(s, "v1", true, crew, "Sidry Kerman", false, false, double.NaN); if (s < 120) Check(o.Count == 0, "no trip chatter in the first 2 min"); posted += o.Count; }
        Check(posted > 0, "trip chatter posts on a long flight");
        var off = new CrewChatter(new Random(2)) { Enabled = false }; int offPosted = 0;
        for (int s = 0; s < 1200; s++) offPosted += off.TripTick(s, "v1", true, crew, "Sidry Kerman", false, false, double.NaN).Count;
        Check(offPosted == 0, "chatter off = silent");
        int portedTools = 0; foreach (var pn in NativeCommands.Ported) if (!pn.Contains("/")) portedTools++;
        Check(NativeToolSchemas.Count == portedTools, "tool schemas cover every ported tool (" + NativeToolSchemas.Count + "/" + portedTools + ")");
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
            Check(el.Count > 1 && el[0].Prompt.Contains("an engine (Juno) just flamed out") && el.Exists(x => x.Prompt.Contains("Bob, a nervous scientist")), "emergency prompt carries facts + personality");
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
            foreach (string cmd in new[] { "circle_here", "land", "land_plane", "fly_to", "roll", "autopilot", "stop_current", "set_heading" })
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
            Check(ok3 && lp == "takeoff\nclimb 2000 m msl\nhead KSC bank 25 left\nland KSC 27 short final", "Luke's sentence -> " + lp.Replace("\n", " | ") + " unsure=" + string.Join(",", uns.ToArray()));
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
            double lat = -.07, lon = -74.56, hdg = 0, alt = 400, spd = 120; double crossAt1k = double.NaN, trkAt1k = double.NaN; bool descendedUnaligned = false; string firstFinal = null, dbgU = "";
            for (int s = 0; s < 3000 && rw3.Phase != "rollout" && rw3.Phase != "go around"; s++)
            {
                rw3.Step(lat, lon, alt, alt - 70, spd, alt <= 70.5, R, 45, hdg);
                if (rw3.Phase == "final" && firstFinal == null) firstFinal = Math.Abs(rw3.Cross).ToString("0");
                if (rw3.Phase != "final" && rw3.Phase != "flare" && rw3.Phase != "rollout" && rw3.Phase != "go around" && rw3.DesiredAltitude < 70 + 300) { descendedUnaligned = true; dbgU = rw3.Phase + " " + rw3.Kind + " des=" + rw3.DesiredAltitude.ToString("0") + " " + rw3.RouteLog; }
                double err = FlightPolicy.Wrap(rw3.DesiredHeading - hdg); double r3 = 9.81 * Math.Tan((rw3.TurnBank > 0 ? rw3.TurnBank : 20) * Math.PI / 180) / spd * 180 / Math.PI; hdg = (hdg + Math.Max(-r3, Math.Min(r3, err)) + 360) % 360;   // flies the commanded (g-rated) bank
                alt += Math.Max(-25, Math.Min(12, rw3.DesiredVs)); if (alt < 70) alt = 70;
                spd += Math.Max(-2, Math.Min(2, rw3.DesiredSpeed - spd)); double nl, no; NavigationMath.Offset(lat, lon, hdg, spd, R, out nl, out no); lat = nl; lon = no;
                if (double.IsNaN(crossAt1k) && rw3.Phase == "final" && rw3.Distance < 1000) { crossAt1k = Math.Abs(rw3.Cross); trkAt1k = Math.Abs(FlightPolicy.Wrap(hdg - course)); }
            }
            Check(firstFinal != null && double.Parse(firstFinal) <= RunwayMission.AlignCross, "glideslope only after centerline intercept (cross " + firstFinal + " m) " + rw3.Phase + " ri=" + rw3.RouteIndex + " " + rw3.RouteLog + " cross=" + rw3.Cross.ToString("0") + " along=" + rw3.Along.ToString("0") + " alt=" + alt.ToString("0") + " hdg=" + hdg.ToString("0") + " why=" + rw3.Why);
            Check(!descendedUnaligned, "no descent before alignment " + dbgU);
            Check(!double.IsNaN(crossAt1k) && crossAt1k < RunwayMission.GateCross && trkAt1k < RunwayMission.GateHeading, "aligned at 1 km: " + crossAt1k.ToString("0") + " m / " + trkAt1k.ToString("0.0") + " deg");
            var gate = new RunwayMission { Lat = -.0502119, Lon = -74.490300, EndLat = -.0485997, EndLon = -74.724375, Elevation = 70, Phase = "final" };
            double gl, gn; NavigationMath.Offset(gate.Lat, gate.Lon, course + 180, 800, R, out gl, out gn); NavigationMath.Offset(gl, gn, course + 90, 120, R, out gl, out gn);
            gate.Step(gl, gn, 120, 50, 62, false, R, 45, course + 20);   // on speed (1.3 Vs + 3): the alignment gate fires
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
            // ---- Round 4: plan pitch/bank steps, turn-around bank, sabotage notice + fumble + model context ----
            {
                Func<string, NativePlan.Step> one = l => NativePlan.Parse(l).Steps[0];
                Check(one("pitch 10").Op == "pitch" && one("pitch 10").Pitch == 10 && one("pitch 10 degrees").Pitch == 10, "pitch 10 / pitch 10 degrees");
                Check(one("pitch up 10").Pitch == 10 && one("pitch down 5").Pitch == -5 && one("max pitch 15").Pitch == 15, "pitch up/down/max");
                bool badP = false; try { NativePlan.Parse("pitch 70"); } catch (ArgumentException) { badP = true; } Check(badP, "absurd pitch rejected");
                double cmax = 15, dmin = -5;
                PilotPolicy.ApplyPitchStep(10, ref cmax, ref dmin); Check(cmax == 10 && dmin == -5, "pitch 10 sets climb limit");
                PilotPolicy.ApplyPitchStep(-8, ref cmax, ref dmin); Check(dmin == -8 && cmax == 10, "pitch down sets descent limit");
                PilotPolicy.ApplyPitchStep(29, ref cmax, ref dmin); Check(cmax == 25, "explicit pitch capped at 25");
                Check(one("turn around bank 25").Op == "turnaround" && one("turn around bank 25").Bank == 25, "turn around bank 25");
                Check(one("turn around bank 25 left").Bank == -25 && one("turn around bank 25 degrees left").Bank == -25, "turn around bank 25 left");
                Check(one("turn around").Bank == 0, "plain turn around keeps normal bank");
                Check(one("bank 25").Op == "bank" && one("bank 25").Bank == 25 && one("bank 25 degrees").Bank == 25 && one("max bank 25").Bank == 25 && one("bank 25 left").Bank == -25, "bank step forms");
                string pl; List<string> un, nt;
                Check(PilotPolicy.TryPlan("take off, climb to 2k, use a 25 degree bank to turn around, land at 27 ksp", true, out pl, out un, out nt) && pl.Contains("turn around bank 25"), "chat: '25 degree bank to turn around' keeps bank: " + pl);
                Check(PilotPolicy.TryPlan("take off, pitch up 10, climb to 2k, land at 27", true, out pl, out un, out nt) && pl.Contains("pitch up 10"), "chat: pitch up 10");
                Check(PilotPolicy.TryPlan("take off, max pitch 15, climb to 2k, pitch down 5, land at 27", true, out pl, out un, out nt) && pl.Contains("max pitch 15") && pl.Contains("pitch down 5"), "chat: max pitch / pitch down");
                Check(PilotPolicy.TryPlan("take off, climb to 2k, max bank 25, land at 27", true, out pl, out un, out nt) && pl.Contains("max bank 25"), "chat: max bank 25");
                foreach (string l in pl.Split('\n')) NativePlan.Parse(l);
                var g = new RecoveryGate();
                Check(!g.Tick(true, 100) && g.JustNoticed && g.Fumble >= 2 && g.Fumble <= 4, "sabotage noticed at once, fumble 2-4 s");
                Check(!g.Tick(true, 101) && !g.JustNoticed, "callout fires once");
                Check(g.Tick(true, 104.1), "restored after the fumble");
                PilotEvents.Clear(); PilotEvents.Add("Wheesley settings were changed in flight; pilot is restoring them", 10);
                Check(PilotEvents.Context(20).Contains("Wheesley") && PilotEvents.Context(10 + PilotEvents.KeepS + 1) == "", "sabotage injected into model context, then ages out");
                var cc4 = new CrewChatter(new Random(3));
                var crew1 = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Sidry Kerman", "pilot") };
                var em = cc4.Emergency("tamper", "Wheesley", crew1, "Sidry Kerman", 50);
                Check(em.Count == 1 && em[0].Name == "Sidry Kerman", "pilot calls out the tampering");
            }
            Console.WriteLine("Round-4 plan pitch/bank / sabotage callout: 19 behavior checks passed.");
            // ---- Round 4b: tight-circle fix, max speed, runway priority, turn-around alias, all-crew reactions ----
            {
                Check(PilotPolicy.ThrottleFloor(.05, 50, 150, 45) == 1 && PilotPolicy.ThrottleFloor(.05, 57, 150, 45) >= .8 && PilotPolicy.ThrottleFloor(.05, 60, 150, 45) >= .8 && PilotPolicy.ThrottleFloor(.05, 120, 150, 45) >= .35 && PilotPolicy.ThrottleFloor(.2, 150, 150, 45) == .2, "never trade airspeed: power back below band/stall margin");
                bool cut4; Check(PilotPolicy.SpeedCapThrottle(.5, 215, 1000, 5, out cut4) == .5 && !cut4, "over-cap cut stops inside the band");
                Check(Math.Abs(PilotPolicy.SafeBank(-20, 57, 45, 0)) <= 5 && Math.Abs(PilotPolicy.SafeBank(-20, 200, 45, 6)) <= 10 && PilotPolicy.SafeBank(-20, 200, 45, 0) == -20, "no tight turns slow or decelerating");
                var rm = new RunwayMission { Lat = -.0502119, Lon = -74.490300, EndLat = -.0485997, EndLon = -74.724375, Elevation = 69 };
                rm.Step(-.05, -72.0, 1500, 1400, 330, false, 600000, 45, 90);
                
                Check(rm.DesiredSpeed <= 150 && rm.DesiredSpeed >= 60, "approach speed in band");
                double hdgErr = NavigationMath.Distance(-.05, -72.0, rm.Route[0].Lat, rm.Route[0].Lon, 600000) / 1000;
                Check(hdgErr < 25, "entry fix near the runway at 330 m/s (was ~86 km away): hdg err " + hdgErr.ToString("0") + " des " + rm.DesiredHeading.ToString("0") + " " + rm.Phase + " " + rm.Kind + " along " + rm.Along.ToString("0") + " cross " + rm.Cross.ToString("0"));
                bool lifted; string rep;
                double vt = PilotPolicy.ResolveSpeed("max", 310, 1000, false, out lifted, out rep);
                Check(vt == 310 && lifted && rep.Contains("310") && rep.Contains("220"), "max = vehicle max, cap only a note: " + rep);
                vt = PilotPolicy.ResolveSpeed(2800, 310, 1000, false, out lifted, out rep);
                Check(vt == 220 && !lifted && rep.Contains("220") && rep.Contains("310") && !rep.Contains("updated"), "2800 clamped, real target stated: " + rep);
                vt = PilotPolicy.ResolveSpeed(2800, 310, 9000, false, out lifted, out rep); Check(vt == 310, "high up: clamped to vehicle max");
                Check(!double.IsNaN(PilotPolicy.VMax(10, 20, 1.0, 100, 150)) && double.IsNaN(PilotPolicy.VMax(10, 20, 1, 100, 10)), "vmax estimate (unknown when slow)");
                Check(!PilotPolicy.PreemptsPlan("set_speed") && !PilotPolicy.PreemptsPlan("set_altitude") && !PilotPolicy.PreemptsPlan("set_throttle") && PilotPolicy.PreemptsPlan("land"), "tweaks modify the plan, land replaces");
                Check(ToolRouter.Direct("max speed", "plane").Value.Key == "set_speed" && ToolRouter.Direct("set speed to max", "plane").Value.Value.Contains("max"), "max speed alias");
                Check(ToolRouter.Direct("turn around", "plane").Value.Key == "set_heading" && ToolRouter.Direct("turn around", "plane").Value.Value.Contains("180"), "turn around -> turn 180");
                Check(FlightResidualPolicy.PreferBuiltIn("KSC 27 short final", new[] { "Island 27" }) && !FlightResidualPolicy.PreferBuiltIn("Island 27", new[] { "Island 27" }) && FlightResidualPolicy.PreferBuiltIn("27", new[] { "Island 27" }), "explicit KSC/built-in beats saved 'Island 27' spot");
                Check(FlightResidualPolicy.DuplicatesBuiltIn(-1.5161, -71.8567) && !FlightResidualPolicy.DuplicatesBuiltIn(-1.0, -70), "duplicate Island spot hidden");
                Check(PilotPolicy.ToolRules.Contains("never compute or invent"), "prompt: numbers only from tool results");
                var cc5 = new CrewChatter(new Random(5));
                var crew3 = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Sidry Kerman", "pilot"), new KeyValuePair<string, string>("Bob Kerman", "scientist"), new KeyValuePair<string, string>("Val Kerman", "pilot") };
                var ev = cc5.Emergency("tamper", "Wheesley", crew3, "Sidry Kerman", 10);
                Check(ev.Count == 3 && ev[0].Name == "Sidry Kerman" && ev[1].Delay >= 2 && ev[2].Delay >= 2, "problem -> pilot first, then every named kerbal, staggered");
                Check(cc5.Emergency("alarm", "low fuel", crew3, "Sidry Kerman", 12).Count == 0, "rate-limited per member");
                CrewLine amb = null; for (int s2 = 0; s2 < 2000 && amb == null; s2 += 5) amb = cc5.SoloTick(500 + s2, true, crew3, "Sidry Kerman", "altitude 900 m");
                Check(amb != null, "randomized ambient chatter with a full crew (2-6 min)");
            }
            Console.WriteLine("Round-4b circle/maxspeed/runway/crew: 19 behavior checks passed.");
            // ---- Overnight audit: alert spam, override phrase, telemetry position, crew scene ----
            {
                var ag = new AlertGate();
                Check(ag.Allow("caution", "G-load: 5.0 g", 0) && !ag.Allow("caution", "G-load: 6.2 g", 5) && ag.Allow("warning", "G-load: 8.3 g", 6) && !ag.Allow("warning", "G-load: 8.6 g", 20) && ag.Allow("caution", "G-load: 5.1 g", 40), "G-load alerts de-spammed (30 s, escalation passes)");
                Check(ag.Allow("caution", "Low fuel: 10%", 7), "other alarm kinds independent");
                var so = ToolRouter.Direct("set speed 250 Authorize, DO NOT GO SLOWER", "plane");
                Check(so != null && so.Value.Key == "set_speed" && so.Value.Value.Contains("\"override\":true") && so.Value.Value.Contains("250"), "'authorize' passes the cap override");
                bool lf; string rp; Check(PilotPolicy.ResolveSpeed(250, double.NaN, 900, true, out lf, out rp) == 250 && lf, "override honored: 250");
                string tl = ChatTelemetry.Line(900, 850, 150, 90, 0, 2, .5, 0, false, false, "landing", TelemetryPos.Part(-0.04861, -74.72441, 12.34, -90));
                Check(tl.Contains("lat=-0.0486 lon=-74.7244 rwy=12.3km brg=270"), "telemetry: lat/lon + runway distance/bearing: " + tl);
                Check(!TelemetryPos.Part(1, 2, double.NaN, 0).Contains("rwy"), "no runway part when no target");
                var names = new List<string> { "Sidry Kerman", "Bob Kerman", "Val Kerman" };
                var sc = CrewScene.Parse("Sidry: Engine's out, working it.\n**Bob Kerman**: We're going to die!\nNarrator: the sky darkens\nSidry (Pilot): Got it half lit...\n- Val: Breathe, Bob.\nSidry: Fixed. All good.", names);
                Check(sc.Count == 5 && sc[0].Key == "Sidry Kerman" && sc[1].Key == "Bob Kerman" && sc[3].Key == "Val Kerman" && sc[4].Value == "Fixed. All good.", "scene split into Name: lines, unknown speakers dropped");
                Check(CrewScene.Parse("just one rambling paragraph", names).Count == 0, "unparseable scene -> canned fallback");
                string sp = CrewScene.Prompt("an engine (Wheesley) just flamed out", "Sidry Kerman", new List<string> { "Bob Kerman" });
                Check(sp.Contains("'Name: line'") && sp.Contains("partial fix") && sp.Contains("Bob Kerman") && sp.Contains("finally"), "scene prompt: announce, partial fix, update, final fix, others react");
                var rg = new Random(1); double gp = CrewScene.Gap(rg); Check(gp >= 2 && gp <= 3, "scene lines 2-3 s apart");
            }
            Console.WriteLine("Overnight audit: 11 behavior checks passed.");
            // ---- Approach charts: entries from 0/90/180 deg, terrain, AGL fixes; morning bugs ----
            {
                const double R0 = 600000;
                Func<double, double, Func<double, double, double>, RunwayMission> fly = (brgFromRwy, hdg0, terr) =>
                {
                    var m = new RunwayMission { Lat = -.0502119, Lon = -74.4903, EndLat = -.0485997, EndLon = -74.724375, Elevation = 69, Terrain = terr };
                    double la, lo; NavigationMath.Offset(m.Lat, m.Lon, brgFromRwy, 25000, R0, out la, out lo);
                    string trace = ""; double h = hdg0, alt = 1500, v = 130, turned = 0, worstCross = 0; bool final = false;
                    for (int k = 0; k < 2500 && !final; k++)
                    {
                        string ph0 = m.Phase + m.RouteIndex; m.Step(la, lo, alt, alt - 69, v, false, R0, 45, h); if (m.Phase + m.RouteIndex != ph0 && trace.Length < 900) trace += k + ":" + m.Phase + m.RouteIndex + "(" + (m.Along/1000).ToString("0.0") + "," + (m.Cross/1000).ToString("0.0") + "," + h.ToString("0") + ") ";
                        if (m.Phase == "final" && m.Distance < 3000) { final = true; worstCross = Math.Abs(m.Cross); break; }
                        double err = FlightPolicy.Wrap(m.DesiredHeading - h), rate = 9.81 * Math.Tan(Math.Min(m.TurnBank > 0 ? m.TurnBank : 20, 70) * Math.PI / 180) / v * 180 / Math.PI;   // the plane flies the bank the autopilot commands
                        double dh = FlightPolicy.Clamp(err, -rate, rate); h = (h + dh + 360) % 360; turned += Math.Abs(dh);
                        alt += FlightPolicy.Clamp(m.DesiredVs, -25, 12); if (m.Phase == "final") v = Math.Max(m.DesiredSpeed, v - 4);   // the craft slows to the forced approach speed
                        NavigationMath.Offset(la, lo, h, v, R0, out la, out lo);
                    }
                    m.Why = (final ? "ok" : "noFinal") + " turned=" + turned.ToString("0") + " cross=" + worstCross.ToString("0") + " " + m.RouteLog + " T: " + trace;
                    return m;
                };
                var head = fly(90, 270, null);   // from the east, flying west: head-on to runway 27
                Check(head.Why.StartsWith("ok") && head.Kind == "short", "0 deg (head-on) -> short final: " + head.Why);
                var abeam = fly(0, 180, null);   // from the north, 90 deg to the runway
                Check(abeam.Why.StartsWith("ok") && abeam.Kind == "long" && abeam.JoinLog.StartsWith("joined") && double.Parse(System.Text.RegularExpressions.Regex.Match(abeam.Why, @"turned=(\d+)").Groups[1].Value) < 720, "90 deg -> nearest safe join, rolls out on centerline: " + abeam.JoinLog + " / " + abeam.Why);
                var behind = fly(270, 90, null);   // from the west, flying east (180 deg opposite)
                Check(behind.Why.StartsWith("ok") && behind.JoinLog.StartsWith("joined") && double.Parse(System.Text.RegularExpressions.Regex.Match(behind.Why, @"turned=(\d+)").Groups[1].Value) < 720, "180 deg -> nearest safe join, long final (no circles): " + behind.Why);
                var ch = ApproachChart.Build(-1.516092, -71.856744, 270, 134.6, 130, 20, R0, null);
                Check(Math.Abs(ch.LongAlt - (134.6 + 629)) < 2 && Math.Abs(ch.ShortAlt - (134.6 + 210)) < 2, "continuous 3 deg glideslope: ~630 m at 12 km, ~210 m at 4 km");
                Check(Math.Abs(ApproachChart.Radius(130, 20) - 1.3 * 130 * 130 / (9.81 * Math.Tan(20 * Math.PI / 180))) < 1, "join spacing from r = v^2/(g tan 20) + 30% margin");
                Func<double, double, double> hill = (la2, lo2) => Math.Abs(lo2 - -71.40) < .05 ? 1400 : 100;   // ridge east of the Island runway, on final
                var chh = ApproachChart.Build(-1.516092, -71.856744, 270, 134.6, 130, 20, R0, hill);
                Check(chh.LongAlt > 134.6 + 629 + 100, "glide path raised over the ridge (Island hill)");
                var rh = chh.Route(-1.40, -71.40, 180, "long", R0); bool clear = true; foreach (var w in rh) clear &= w.Alt >= 100 + ApproachChart.Clearance;
                Check(clear, "every waypoint clears sampled terrain");
                Check(PilotPolicy.ApproachFloorVs("entry", 20000, 90, 500, double.NaN, -5) >= 3 && PilotPolicy.ApproachFloorVs("final", 1000, 40, 100, double.NaN, -4) == -4, "AGL floor on approach, not in the last km");
                Check(ApproachChart.Pad(0, 0, 70).Alt == 370, "launch pad vertical approach point");
                PlayerIntent.Clear(); PlayerIntent.Note("overide authorized", 100);
                Check(PlayerIntent.OverrideRecent(150) && !PlayerIntent.OverrideRecent(300), "override said separately is remembered for 2 min");
                Check(ToolRouter.Direct("overide authorized", "plane").Value.Value.Contains("last") && ToolRouter.Direct("override", "plane").Value.Key == "set_speed", "'override' re-applies the last asked speed");
                var b60 = ToolRouter.Direct("bank 60 land nearest", "plane");
                Check(b60 != null && b60.Value.Key == "land_plane" && b60.Value.Value.Contains("\"bank\":60") && b60.Value.Value.Contains("nearest"), "'bank 60, land nearest' keeps bank 60");
                Check(ToolRouter.Direct("current job?", "plane").Value.Key == "get_status", "'current job?' -> status (no stale tool replay)");
                Check(GearPolicy.Violation(GearPolicy.Intent("hold", "", 900), true, false) && !GearPolicy.Violation(GearPolicy.Intent("hold", "", 900), false, false), "gear lowered in cruise = sabotage, raised = fine");
                Check(GearPolicy.Violation(GearPolicy.Intent("landing", "flare", 10), false, false) && !GearPolicy.Violation(GearPolicy.Intent("hold", "", 900), true, true), "gear raised in flare = sabotage; player order honored");
                Check(AtomicFile.Torn("\0\0\0  ") && !AtomicFile.Torn("{}"), "torn (power-loss) file detected");
                string tmpf = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aics_atomic.json"); AtomicFile.Write(tmpf, "{\"a\":1}"); AtomicFile.Write(tmpf, "{\"a\":2}");
                Check(System.IO.File.ReadAllText(tmpf) == "{\"a\":2}", "atomic write replaces");
                Check(PilotPolicy.ApLine("landing (entry, 12.0 km)", "step 2/4: climb 1000 m", 1000, 150, 271, 20, true).Contains("bank limit 20") , "autopilot window summary");
            }
            Console.WriteLine("Approach charts + morning bugs: 18 behavior checks passed.");
            {
                var res = new List<string> { "Target 220 m/s (low-altitude cap; say override for more)." };
                Check(PilotPolicy.GuardReply("Overriding max speed to 400 m/s.", res) == res[0], "reply contradicting the tool result is replaced by it");
                Check(PilotPolicy.GuardReply("Holding 220 m/s, Captain.", res) == "Holding 220 m/s, Captain." && PilotPolicy.GuardReply("On it!", res) == "On it!", "consistent replies kept");
                Check(PilotPolicy.MaxPitchRate(130) < 10 && PilotPolicy.MaxPitchRate(130) > 7 && PilotPolicy.GScale(6) == .5 && PilotPolicy.GScale(2) == 1, "3 g limit: pitch rate + elevator scaling");
            }
            Console.WriteLine("Reply guard / G limit: 3 behavior checks passed.");
            {
                Check(RunwayMission.PredictCross(0, 170, 100, 90, 10) > 250 && RunwayMission.PredictCross(-200, 170, 80, 90, 10) < -400 && RunwayMission.PredictCross(50, 170, double.NaN, 90, 10) == 50, "predicted cross-track damps the intercept");
                double eta = PilotPolicy.ApproachEta(3000, new List<double> { 4000, 5000 }, 150, 10);
                Check(Math.Abs(eta - 12000.0 / 160) < .01, "ETA = (next fix + legs) / (speed + |vs|)");
                Check(PilotPolicy.ApproachEtaText("entry", 3000, new List<double> { 9000 }, 150, 0, new List<string> { "long final 12 km 698 m" }).Contains("long final 12 km") && PilotPolicy.ApproachEtaText("entry", 3000, new List<double> { 9000 }, 150, 0, new List<string>()).Contains("ETA 80 s"), "ETA text lists fixes");
                var rates = new float[] { 1, 2, 3, 4 };
                Check(PilotPolicy.WarpIndexCap(true, 3, rates) == 2 && PilotPolicy.WarpIndexCap(false, 3, rates) == 3 && PilotPolicy.WarpIndexCap(true, 1, rates) == 1, "warp cap 3x in atmosphere only");
                string ser = PilotPolicy.ApSerialize(new[] { "1000", "5", "090", "0", "180" }, new[] { true, false, true, false, true, true });
                string[] av; bool[] af; Check(PilotPolicy.ApParse(ser, 5, 6, out av, out af) && av[2] == "090" && af[0] && !af[1] && af[5] && !PilotPolicy.ApParse("\0\0", 5, 6, out av, out af), "autopilot window settings round-trip (restart-safe)");
            }
            Console.WriteLine("Tracking / ETA / warp / AP persistence: 5 behavior checks passed.");
            {
                string why;
                string good = "{\"alt_ref\":\"msl\",\"fixes\":[{\"role\":\"faf\",\"name\":\"KAPPA\",\"lat\":-0.05,\"lon\":-74.85,\"alt\":700}],\"flare_start_m\":20,\"flare_sink_ms\":1,\"touchdown_m\":300,\"tch_m\":12}";
                var ov = ApproachOverride.Parse(MiniJson.Deserialize(good), -.0486, -74.7244, 69, 600000, out why);
                Check(ov != null && ov.Fixes.Count == 1 && ov.FlareStartM == 20 && ov.TchM == 12, "approaches.json end parsed: " + why);
                var chO = ApproachChart.Build(-.0486, -74.7244, 90.4, 69, 150, 20, 600000, null); chO.Apply(ov);
                Check(chO.Long.Name == "KAPPA" && chO.LongAlt == 700 && chO.Tch == 12 && Math.Abs(chO.GlideAlt(0) - 81) < .01, "edited fix + TCH override the computed chart");
                Check(ApproachOverride.Parse(MiniJson.Deserialize("{\"fixes\":[{\"role\":\"faf\",\"lat\":10,\"lon\":10,\"alt\":700}]}"), -.0486, -74.7244, 69, 600000, out why) == null && why.Contains("60 km"), "fix far away rejected -> defaults");
                Check(ApproachOverride.Parse(MiniJson.Deserialize("{\"flare_start_m\":500}"), -.0486, -74.7244, 69, 600000, out why) == null && ApproachOverride.Parse("x", 0, 0, 0, 600000, out why) == null, "bad flare / garbage rejected");
            }
            Console.WriteLine("Editable approach charts: 4 behavior checks passed.");
            {
                string why2; var thr = new[] { -.0486, -74.7244 };
                string js = "{\"fixes\":[{\"role\":\"dw_left\",\"lat\":-.12,\"lon\":-74.72,\"alt\":700},{\"role\":\"wp\",\"name\":\"EXTRA\",\"side\":\"left\",\"lat\":-.12,\"lon\":-74.83,\"alt\":700},{\"role\":\"faf\",\"lat\":-.0478,\"lon\":-74.832,\"alt\":700},{\"role\":\"sf\",\"lat\":-.0483,\"lon\":-74.760,\"alt\":280}]}";
                var ov2 = ApproachOverride.Parse(MiniJson.Deserialize(js), thr[0], thr[1], 69, 600000, out why2);
                Check(ov2 != null && ov2.Fixes.Count == 4, "added waypoint parsed: " + why2);
                var c2 = ApproachChart.Build(thr[0], thr[1], 90.4, 69, 150, 20, 600000, null); c2.Apply(ov2);
                var rt = c2.Route(.05, -74.70, 270, "long", 600000);
                Check(rt.Count == 3 && rt[1].Name == "EXTRA" && rt[2].Lat == c2.Long.Lat, "custom route: dw_left, EXTRA, FAF (removed base/dwend not flown): " + c2.Describe(rt));
                Check(ApproachOverride.Parse(MiniJson.Deserialize("{\"fixes\":[{\"role\":\"wp\",\"side\":\"up\",\"lat\":-.05,\"lon\":-74.8,\"alt\":700}]}"), thr[0], thr[1], 69, 600000, out why2) == null, "bad side rejected");
                var c3 = ApproachChart.Build(thr[0], thr[1], 90.4, 69, 150, 20, 600000, null); c3.Apply(ApproachOverride.Parse(MiniJson.Deserialize("{\"flare_start_m\":20}"), thr[0], thr[1], 69, 600000, out why2));
                Check(c3.CustomLeft == null, "no fix list -> computed joins kept");
            }
            Console.WriteLine("Chart waypoint add/remove: 4 behavior checks passed.");
            {
                string why3; string agl = "{\"alt_ref\":\"agl\",\"fixes\":[{\"role\":\"faf\",\"lat\":-.05,\"lon\":-74.85,\"alt\":600}]}";
                var oa = ApproachOverride.Parse(MiniJson.Deserialize(agl), -.0486, -74.7244, 69, 600000, out why3, (la, lo) => 0);
                Check(oa != null && oa.Fixes[0].Alt == 600 && oa.Fixes[0].Agl == 600 && oa.AltRef == "agl", "AGL over the sea (terrain 0) -> 600 m MSL: " + why3);
                var ob = ApproachOverride.Parse(MiniJson.Deserialize(agl), -.0486, -74.7244, 69, 600000, out why3, (la, lo) => 250);
                Check(ob.Fixes[0].Alt == 850, "AGL over a 250 m hill -> 850 m MSL");
                var oc = ApproachOverride.Parse(MiniJson.Deserialize(agl), -.0486, -74.7244, 69, 600000, out why3);
                Check(oc.Fixes[0].Alt == 669, "no terrain sampler -> runway elevation + AGL");
                Check(ApproachOverride.Parse(MiniJson.Deserialize("{\"alt_ref\":\"ft\"}"), 0, 0, 0, 600000, out why3) == null && ApproachOverride.Parse(MiniJson.Deserialize("{\"fixes\":[{\"role\":\"faf\",\"lat\":-.05,\"lon\":-74.85,\"alt\":700}]}"), -.0486, -74.7244, 69, 600000, out why3).AltRef == "agl", "bad alt_ref rejected; untagged file = agl");
                var mix = ApproachOverride.Parse(MiniJson.Deserialize("{\"alt_ref\":\"agl\",\"fixes\":[{\"role\":\"faf\",\"alt_ref\":\"msl\",\"lat\":-.05,\"lon\":-74.85,\"alt\":700},{\"role\":\"sf\",\"lat\":-.049,\"lon\":-74.76,\"alt\":200}]}"), -.0486, -74.7244, 69, 600000, out why3, (la, lo) => 40);
                Check(mix != null && mix.Fixes[0].Alt == 700 && mix.Fixes[0].AltRef == "msl" && mix.Fixes[1].Alt == 240 && mix.Fixes[1].AltRef == "agl", "per-fix msl overrides file agl; others use terrain: " + why3);
                Check(ApproachOverride.Parse(MiniJson.Deserialize("{\"fixes\":[{\"role\":\"faf\",\"alt_ref\":\"m\",\"lat\":-.05,\"lon\":-74.85,\"alt\":700}]}"), -.0486, -74.7244, 69, 600000, out why3) == null, "bad per-fix alt_ref rejected");
            }
            Console.WriteLine("AGL/MSL approach altitudes: 6 behavior checks passed.");
            {
                var tg = TerrainGrid.Make("KSC", -.0494, -74.6074);
                double la0, lo0, la1, lo1; TerrainGrid.Point(tg, 0, out la0, out lo0); TerrainGrid.Point(tg, tg.H.Length - 1, out la1, out lo1);
                Check(tg.N == 176 && Math.Abs((la1 - la0) * Math.PI / 180 * 600000 - 70000) < 1 && Math.Abs((la0 + la1) / 2 + .0494) < 1e-9, "terrain grid covers +-35 km at 400 m");
                tg.H[1] = 87; var tj = MiniJson.Deserialize(TerrainGrid.Json(new List<TerrainGrid.Site> { tg }, "Kerbin")) as Dictionary<string, object>;
                var site = (Dictionary<string, object>)((System.Collections.IList)tj["sites"])[0];
                Check(Convert.ToInt32(site["n"]) == 176 && Convert.ToInt32(((System.Collections.IList)site["h"])[1]) == 87 && ((System.Collections.IList)site["h"]).Count == 176 * 176, "terrain json round-trips");
            }
            Console.WriteLine("Terrain export grid: 2 behavior checks passed.");
            {
                MapReveal.Clear(); const double Rk = 600000;
                Check(MapReveal.RadiusFor(0) == 3000 && MapReveal.RadiusFor(4000) == 5000 && MapReveal.RadiusFor(1e6) == 12000, "reveal radius 3 km, grows with height, capped 12 km");
                Check(MapReveal.Revealed("Kerbin", -1.52, -71.91, Rk, null) && MapReveal.Revealed("Kerbin", -6.6, -144.04, Rk, null) && !MapReveal.Revealed("Kerbin", 10, 10, Rk, null), "airports always revealed, the rest fogged");
                int added = MapReveal.Fly("Kerbin", 10, 10, 0, Rk);
                Check(added > 100 && MapReveal.Revealed("Kerbin", 10.02, 10, Rk, null) && MapReveal.Revealed("Kerbin", 10.2, 10, Rk, null) && !MapReveal.Revealed("Kerbin", 10.4, 10, Rk, null), "flight path reveals ~3 km around the craft");
                Check(MapReveal.Fly("Kerbin", 10.001, 10, 0, Rk) == 0, "no re-reveal until the craft moved 500 m");
                Check(MapReveal.Revealed("Kerbin", 20, 20, Rk, (la, lo) => la > 19) && !MapReveal.Revealed("Kerbin", 10.02, 10, Rk, (la, lo) => false), "SCANsat coverage drives reveal when installed");
                var ser = MapReveal.Serialize(); int before = MapReveal.Count("Kerbin"); MapReveal.Clear(); MapReveal.Load("Kerbin", ser["Kerbin"]);
                Check(MapReveal.Count("Kerbin") == before && MapReveal.Revealed("Kerbin", 10.02, 10, Rk, null), "reveal persists (save round-trip)");
                Check(MapReveal.Revealed("Mun", 0, 0, Rk, null, new[] { new[] { 0.0, 0.0 } }), "saved spots revealed like airports");
                MapReveal.Clear();
                string other = "{\"Island 09\":{\"alt_ref\":\"agl\",\"fixes\":[],\"flare_start_m\":22}}";
                var ef = new List<ApproachFile.EditFix> { new ApproachFile.EditFix { Role = "faf", Name = "FAF", Lat = -.0478, Lon = -74.832, AltMsl = 700 }, new ApproachFile.EditFix { Role = "wp", Name = "WP1", Side = "left", Ref = "msl", Lat = .05, Lon = -74.83, AltMsl = 800 } };
                string merged = ApproachFile.Merge(other, "KSC 09", ef, (la, lo) => 40);
                var mj = MiniJson.Deserialize(merged); string whyM;
                var back = ApproachOverride.Parse(mj["KSC 09"], -.0486, -74.7244, 69, Rk, out whyM, (la, lo) => 40);
                Check(mj.ContainsKey("Island 09") && back != null && back.Fixes.Count == 2 && back.Fixes[0].Agl == 660 && back.Fixes[0].Alt == 700 && back.Fixes[1].AltRef == "msl" && back.Fixes[1].Alt == 800, "map editor save keeps other ends and round-trips agl/msl: " + whyM);
                Check(ApproachFile.Merge("garbage{", "KSC 27", ef, null).Contains("KSC 27"), "corrupt file replaced, not crashed");
            }
            Console.WriteLine("Map fog + chart save: 9 behavior checks passed.");
            {   // Luke 13:11: 4-6 km west of WP5->WP7 (fly-over turns at 170 m/s). Lead turns + leg tracking at 130 m/s on his left join.
                var pts = new[] { new[] { 1.58064, -76.60771 }, new[] { 1.30422, -76.88414 }, new[] { .86445, -76.95953 }, new[] { .31159, -76.95953 }, new[] { .10427, -76.86529 }, new[] { -.03419, -76.7839 } };
                double la = 1.70, lo = -76.40, hdg = 230, v = 130, R = 600000, worst = 0; int leg = 1, steps = 0;
                while (leg < pts.Length && steps++ < 4000)
                {
                    double nb = leg + 1 < pts.Length ? NavigationMath.Bearing(pts[leg][0], pts[leg][1], pts[leg + 1][0], pts[leg + 1][1]) : 90.4;
                    bool adv; double xte; double want = RunwayMission.LegSteer(la, lo, pts[leg - 1][0], pts[leg - 1][1], pts[leg][0], pts[leg][1], nb, v, 20, R, out adv, out xte);
                    if (steps > 60) worst = Math.Max(worst, Math.Abs(xte));
                    if (adv) { leg++; continue; }
                    double rate = 9.81 * Math.Tan(20 * Math.PI / 180) / v * 180 / Math.PI;   // deg/s at 20 deg bank
                    hdg += FlightPolicy.Clamp(FlightPolicy.Wrap(want - hdg), -rate, rate);
                    NavigationMath.Offset(la, lo, hdg, v, R, out la, out lo);
                }
                Check(leg == pts.Length && worst < 900, "lead turns + leg tracking keep Luke's join within 900 m of the legs (worst " + Math.Round(worst) + " m)");
                bool a1; double x1; RunwayMission.LegSteer(0, -.5, 0, -1, 0, 0, 90, 130, 20, R, out a1, out x1);
                Check(!a1 && Math.Abs(x1) < 1, "straight leg: no early switch");
                bool a2; RunwayMission.LegSteer(0, -.5, 0, -1, 0, 0, 180, 130, 20, R, out a2, out x1); RunwayMission.LegSteer(0, -.4, 0, -1, 0, 0, 180, 130, 20, R, out a1, out x1);
                Check(a1 && !a2, "90 deg turn starts ~r (4.8 km) before the fix at 130 m/s");
                Check(ApproachProfile.Speed(45) == 80 && ApproachProfile.Speed(60) == 90, "join speed = 1.5 x stall, floor 80 m/s");
            }
            Console.WriteLine("Lead turns / leg tracking: 4 behavior checks passed.");
            {   // Luke 13:13: settled 20-60 m right of 09 then went around 4x in one frame. Sim with a 1.5 deg steady track bias.
                foreach (double bias in new[] { 1.5, -1.5 })
                {
                    var rf = new RunwayMission { Lat = -.0485997, Lon = -74.724375, EndLat = -.0502119, EndLon = -74.490300, Elevation = 69.1, Phase = "final" };
                    double crs = NavigationMath.Bearing(rf.Lat, rf.Lon, rf.EndLat, rf.EndLon), fla, flo; NavigationMath.Offset(rf.Lat, rf.Lon, crs + 180, 7000, 600000, out fla, out flo);
                    NavigationMath.Offset(fla, flo, crs + 90, 60, 600000, out fla, out flo);
                    double hh = crs, at1k = double.NaN; string ph = "";
                    for (int i = 0; i < 400; i++)
                    {
                        double tr = hh + bias;
                        rf.Step(fla, flo, rf.Elevation + 300, 300, 62, false, 600000, 45, tr, hh);
                        if (rf.Phase != "final") { ph = rf.Phase + " " + rf.Why; break; }
                        hh += FlightPolicy.Clamp(FlightPolicy.Wrap(rf.DesiredHeading - hh), -3, 3);
                        NavigationMath.Offset(fla, flo, tr, 62, 600000, out fla, out flo);
                        if (double.IsNaN(at1k) && rf.Distance < 1500) at1k = rf.Cross;
                        if (rf.Distance < 300) break;
                    }
                    Check(ph == "" && Math.Abs(at1k) < 5, "centerline converges < 5 m by 1.5 km despite a " + bias + " deg bias (" + at1k.ToString("0.0") + " m) " + ph);
                }
                var g = new RunwayMission { Phase = "go around", Kind = "short" }; g.Route = new List<ApproachChart.Wp>(); g.RouteIndex = 3; g.GoAroundReset();
                Check(g.Phase == "entry" && g.Route == null && g.Kind == "long", "go-around rebuilds a fresh route (no 4x go-around in one frame)");
            }
            Console.WriteLine("Centerline integral + go-around reset: 3 behavior checks passed.");
            {   // Luke: +-1 m from the short final (4 km) to touchdown, no back-and-forth. 0.25 s sim: heading controller with 1.5 s
                // turn-rate lag, 3 deg/s limit, steady crab bias (wind/sideslip), 0.2 deg track noise; 60 m off at 12 km.
                foreach (var cse in new double[][] { new double[] { 1.5, 75, 60 }, new double[] { -1.5, 75, 60 }, new double[] { 0, 75, 60 }, new double[] { 4, 75, 60 }, new double[] { 1.5, 130, 60 }, new double[] { 2, 40, 3 } })
                {
                    double bias = cse[0], v = cse[1], x = cse[2], crs = 90.4, hdg = crs, rate = 0, ci = 0, crab = 0, dt = .25, worst = 0, along = -12000, maxAfter = 0; bool onLine = false;
                    var rnd = new Random(1);
                    while (along < -100)
                    {
                        double tr = hdg + bias, meas = tr + .2 * Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
                        double want = RunwayMission.CenterlineHeading(x, meas, crs, v, ref ci, v * dt, hdg, ref crab);
                        double cmd = FlightPolicy.Clamp(FlightPolicy.Wrap(want - hdg) * .5, -3, 3); rate += (cmd - rate) * dt / 1.5; hdg += rate * dt;
                        x += v * dt * Math.Sin((tr - crs) * Math.PI / 180); along += v * dt * Math.Cos((tr - crs) * Math.PI / 180);
                        if (along > -4000) worst = Math.Max(worst, Math.Abs(x));
                        if (along > -4000) { if (Math.Abs(x) < 1) onLine = true; else if (onLine) maxAfter = Math.Max(maxAfter, Math.Abs(x)); }
                    }
                    Check(worst < 1 && maxAfter < 1, "centerline within 1 m from 4 km to touchdown, no swinging (bias " + bias + ", " + v + " m/s, worst " + worst.ToString("0.00") + ", after " + maxAfter.ToString("0.00") + ")");
                }
                // Luke's KSC 09 left join at 130 m/s: smoothed arcs, fixes kept, then flown with LegSteer
                var rt = new List<ApproachChart.Wp>();
                foreach (var q in new[] { new[] { 1.6686, -74.63502 }, new[] { 1.69373, -75.89779 }, new[] { 1.58064, -76.60771 }, new[] { 1.30422, -76.88414 }, new[] { .86445, -76.95953 }, new[] { .31159, -76.95953 }, new[] { .10427, -76.86529 }, new[] { -.03419, -76.7839 } })
                    rt.Add(new ApproachChart.Wp { Name = "F" + rt.Count, Lat = q[0], Lon = q[1], Alt = 700 });
                string slog; var sm = ApproachChart.Smooth(rt, 90.4, -.0485997, -74.724375, 130, 20, 600000, out slog);
                bool allKept = true; foreach (var w in rt) if (!sm.Exists(z => z.Name == w.Name)) allKept = false;
                Check(sm.Count > rt.Count && allKept && slog.Contains("smoothed"), "smoothing inserts arc points and keeps every fix name: " + slog);
                double la = rt[0].Lat, lo = rt[0].Lon, h = 270, worstX = 0; int leg = 1, st = 0;
                while (leg < sm.Count && st++ < 20000)
                {
                    double nb = leg + 1 < sm.Count ? NavigationMath.Bearing(sm[leg].Lat, sm[leg].Lon, sm[leg + 1].Lat, sm[leg + 1].Lon) : 90.4; bool adv; double xt;
                    double want = RunwayMission.LegSteer(la, lo, sm[leg - 1].Lat, sm[leg - 1].Lon, sm[leg].Lat, sm[leg].Lon, nb, 130, 20, 600000, out adv, out xt);
                    if (st > 100) worstX = Math.Max(worstX, Math.Abs(xt));
                    if (adv) { leg++; continue; }
                    h += FlightPolicy.Clamp(FlightPolicy.Wrap(want - h), -2.1 * .5, 2.1 * .5); NavigationMath.Offset(la, lo, h, 65, 600000, out la, out lo);
                }
                Check(leg == sm.Count && worstX < 350, "smoothed join flown within 350 m of the adjusted route (worst " + Math.Round(worstX) + " m)"); Console.WriteLine("  join: " + slog + "; worst " + Math.Round(worstX) + " m");
            }
            Console.WriteLine("Tight lateral control: 8 behavior checks passed.");
            {   // Luke: arcs depend on the plane. Profiles: light trainer, the Aeris-class default, heavy cargo, fast jet, explicit bank 45.
                var luke = new List<ApproachChart.Wp>();
                foreach (var q in new[] { new[] { 1.6686, -74.63502 }, new[] { 1.69373, -75.89779 }, new[] { 1.58064, -76.60771 }, new[] { 1.30422, -76.88414 }, new[] { .86445, -76.95953 }, new[] { .31159, -76.95953 }, new[] { .10427, -76.86529 }, new[] { -.03419, -76.7839 } })
                    luke.Add(new ApproachChart.Wp { Name = "F" + luke.Count, Lat = q[0], Lon = q[1], Alt = 700 });
                Func<double, double, int> tightCount = (stallV, chartBank) => { double v = ApproachProfile.Speed(stallV); string lg; ApproachChart.Smooth(luke, 90.4, -.0485997, -74.724375, v, ApproachProfile.Bank(chartBank, v), 600000, out lg); int t = lg.IndexOf("TIGHT"); return t < 0 ? 0 : lg.Substring(t).Split(new[] { " deg" }, StringSplitOptions.None).Length - 1; };
                double rLight = ApproachProfile.Radius(ApproachProfile.Speed(30), ApproachProfile.Bank(20, 45)), rHeavy = ApproachProfile.Radius(ApproachProfile.Speed(80), ApproachProfile.Bank(20, 120));
                Check(Math.Abs(rLight - 80 * 80 / (9.81 * Math.Tan(20 * Math.PI / 180))) < 1 && rHeavy > 2 * rLight, "radius grows with v^2: trainer " + Math.Round(rLight) + " m vs cargo " + Math.Round(rHeavy) + " m");
                Check(tightCount(30, 20) < tightCount(80, 20), "heavier craft has more tight turns on the same chart (" + tightCount(30, 20) + " vs " + tightCount(80, 20) + ")");
                Check(ApproachProfile.Bank(20, 300) == 10 && ApproachProfile.Bank(45, 300) == 45 && ApproachProfile.Bank(20, 100) == 20, "usable bank: 10 deg above 250 m/s, explicit bank wins");
                Check(Math.Abs(ApproachProfile.StallAt(45, 10, 7.5) - 45 * Math.Sqrt(.75)) < 1e-9, "stall scales with sqrt(mass) (fuel burn)");
                Check(Math.Abs(ApproachProfile.EstimateStall(6000, 6) - 45) < 1 && ApproachProfile.EstimateStall(20000, 6) > 80, "wing-loading stall estimate");
                Check(!ApproachProfile.NeedsReplan(67.5, 70) && ApproachProfile.NeedsReplan(67.5, ApproachProfile.Speed(ApproachProfile.StallAt(45, 10, 8))), "replan only on a significant speed/mass change");
                // replan keeps the current target
                var rm = new RunwayMission { Lat = -.0485997, Lon = -74.724375, EndLat = -.0502119, EndLon = -74.490300, Elevation = 69.1 };
                rm.RawRoute = luke; rm.Plan(90.4, 600000, ApproachProfile.Speed(60), -1); int n1 = rm.Route.Count; rm.RouteIndex = n1 / 2; var tgt = rm.Route[rm.RouteIndex];
                rm.Plan(90.4, 600000, ApproachProfile.Speed(40), rm.RouteIndex);
                Check(rm.PlanSpeed == ApproachProfile.Speed(40) && rm.SmoothLog.StartsWith("replanned") && NavigationMath.Distance(tgt.Lat, tgt.Lon, rm.Route[rm.RouteIndex].Lat, rm.Route[rm.RouteIndex].Lon, 600000) < 6000, "replan after fuel burn: new arcs, target kept nearby");
            }
            Console.WriteLine("Per-craft approach profile: 7 behavior checks passed.");
            {   // Luke: join at the nearest fix/leg point this plane can safely align with (Dubins reach, altitude, terrain), shortest to touchdown
                Check(Math.Abs(ApproachChart.Dubins(0, 0, 90, 5000, 0, 90, 1000) - 5000) < 1, "Dubins: aligned straight = distance");
                double rev = ApproachChart.Dubins(0, 0, 90, 0, 0, 270, 1000);
                Check(Math.Abs(ApproachChart.Dubins(0, 0, 90, 0, 2000, 270, 1000) - Math.PI * 1000) < 1 && rev > Math.PI * 1000 && rev < 4 * Math.PI * 1000, "Dubins: U-turn to 2r abeam = pi r; same-point reversal is a loop (" + Math.Round(rev) + " m)");
                double thrLa = -.0485997, thrLo = -74.724375, v = ApproachProfile.Speed(45), r = ApproachProfile.Radius(v, 20);
                var chj = ApproachChart.Build(thrLa, thrLo, 90.4, 69, v, 20, 600000, null);
                string jw; double bLa, bLo; NavigationMath.Offset(chj.ApexLeft.Lat, chj.ApexLeft.Lon, 0, 3000, 600000, out bLa, out bLo);
                var j1 = chj.BestJoin(bLa, bLo, 180, chj.LongAlt, v, r, 600000, out jw);
                Check(j1 != null && j1[0].Name != chj.DownLeftA.Name && j1[j1.Count - 1].Lat == chj.Long.Lat && (j1[0].Name.StartsWith("line") || j1[0].Name == chj.ApexLeft.Name || j1[0].Name == chj.Long.Name), "near the left base heading south: joins late in the sequence, not at the first fix (" + jw + ")");
                double sLa, sLo; NavigationMath.Offset(thrLa, thrLo, 270.4, 20000, 600000, out sLa, out sLo);
                var j2 = chj.BestJoin(sLa, sLo, 90.4, chj.LongAlt, v, r, 600000, out jw);
                Check(j2 != null && j2[j2.Count - 1].Lat == chj.Long.Lat && j2.Count <= 2, "on the extended centerline 20 km out: straight to the FAF (" + jw + ")");
                {   // Luke 5:56 PM heavy craft: 330/330 'unreachable' -> fall back to a reachable point on the long final line
                    double hv = 120, hr = ApproachProfile.Radius(hv, CraftClass.HeavyBank), hLa, hLo; NavigationMath.Offset(thrLa, thrLo, 120, 28000, 600000, out hLa, out hLo);
                    var jh = chj.BestJoin(hLa, hLo, 30, chj.LongAlt, hv, hr, 600000, out jw);
                    Check(jh != null && jh.Count >= 1, "heavy craft far off-axis always gets a reachable join (" + jw + ")");
                }
                var j3 = chj.BestJoin(bLa, bLo, 180, chj.LongAlt + 4000, v, r, 600000, out jw);
                Check(jw.Contains("altitude") && (j3 == null || j3[0].Name != chj.ApexLeft.Name), "4 km too high: close fixes rejected for descent (" + jw + ")");
                {   // Luke 3:12 PM bug: joins along the LINE, forward in chart order only, prefer the leg we are on, no 40 g structure
                    var sideR = chj.Side(true);
                    Func<List<ApproachChart.Wp>, List<ApproachChart.Wp>, bool> forward = (rt, sd) => { int at = -1; for (int q = 1; q < rt.Count; q++) { int ix = sd.FindIndex(w => w.Lat == rt[q].Lat && w.Lon == rt[q].Lon); if (ix <= at) return false; at = ix; } return true; };
                    var A = sideR[0]; var B = sideR[1]; double lt = NavigationMath.Bearing(A.Lat, A.Lon, B.Lat, B.Lon);
                    double mLa = A.Lat + (B.Lat - A.Lat) * .4, mLo = A.Lon + (B.Lon - A.Lon) * .4;
                    var jr1 = chj.BestJoin(mLa, mLo, lt, A.Alt, v, r, 600000, out jw);
                    Check(jr1 != null && jw.Contains("right") && jw.Contains("on the leg ahead") && forward(jr1, sideR), "on the right downwind heading along it: joins that leg ahead, chart order (" + jw + ")");
                    Check(NavigationMath.Distance(mLa, mLo, jr1[0].Lat, jr1[0].Lon, 600000) < 6000 && Math.Abs(FlightPolicy.Wrap(NavigationMath.Bearing(mLa, mLo, jr1[0].Lat, jr1[0].Lon) - lt)) < 60, "join point is ahead on the line, not behind");
                    var jr2 = chj.BestJoin(mLa, mLo, lt + 180, A.Alt, v, r, 600000, out jw);
                    Check(jr2 == null || forward(jr2, jw.Contains("right") ? sideR : chj.Side(false)), "heading against the leg: never flies the route backwards (" + jw + ")");
                    var jr3 = chj.BestJoin(mLa, mLo, lt, A.Alt + 2500, v, r, 600000, out jw);
                    Check(jr3 != null, "2.5 km high: joins with S-turns/slowing instead of rejecting (" + jw + ")");
                    Check(ApproachProfile.StructuralG(new[] { 50.0, 60 }) <= 9 && ApproachProfile.StructuralG(new[] { 5.0 }) == 4, "structure g capped ~9 g (part tolerances are not airframe limits)");
                    Console.WriteLine("Line joins: 5 behavior checks passed.");
                }
                var hill = ApproachChart.Build(thrLa, thrLo, 90.4, 69, v, 20, 600000, (la, lo) => 5000);
                Check(hill.BestJoin(bLa, bLo, 180, 800, v, r, 600000, out jw) == null && jw.Contains("terrain"), "terrain in the way: no join, fall back to the long final (" + jw + ")");
                var rmj = new RunwayMission { Lat = thrLa, Lon = thrLo, EndLat = -.0502119, EndLon = -74.490300, Elevation = 69.1 };
                rmj.Step(bLa, bLo, 700, 700, v, false, 600000, 45, 180, 180);
                Check(rmj.JoinLog.StartsWith("joined") && rmj.RawRoute != null && rmj.RawRoute[0].Name != rmj.Chart.DownLeftA.Name, "approach start uses the best join: " + rmj.JoinLog);
            }
            Console.WriteLine("Nearest safe join: 7 behavior checks passed.");
            {
                var ir = Ils.Compute(-.0485997, -74.724375 - 5000 / (600000 * Math.PI / 180), 69 + 300 * Math.Tan(3 * Math.PI / 180) + 5000 * Math.Tan(3 * Math.PI / 180), -.0485997, -74.724375, -.0502119, -74.490300, 69, 600000);
                Check(Math.Abs(ir.DmeM - 5000) < 5 && Math.Abs(ir.CrossM) < 40 && Math.Abs(ir.AboveGsM) < 1 && Math.Abs(ir.GsDeg) < .02 && ir.Front, "ILS: on the 3 deg path 5 km out: DME 5 km, G/S centred (" + ir.AboveGsM.ToString("0.0") + " m, cross " + ir.CrossM.ToString("0") + " m)");
                double ola, olo; NavigationMath.Offset(-.0485997, -74.724375, 270.4, 5000, 600000, out ola, out olo); NavigationMath.Offset(ola, olo, 0.4, 100, 600000, out ola, out olo);
                var il = Ils.Compute(ola, olo, 2000, -.0485997, -74.724375, -.0502119, -74.490300, 69, 600000);
                Check(il.CrossM < -95 && il.LocDeg < 0 && il.GsDeg > .7 && Ils.Needle(il.GsDeg, Ils.GsFullScale) == 1 && il.AboveGsM > 1000, "ILS: 100 m left and high -> LOC negative, G/S pegged high");
                double pla = 0, plo = 0; MapReveal.Pan(ref pla, ref plo, 90, 40000, 600000); MapReveal.Pan(ref pla, ref plo, 0, 40000, 600000);
                Check(Math.Abs(NavigationMath.Distance(0, 0, 0, plo, 600000) - 10000) < 1 && Math.Abs(NavigationMath.Distance(0, plo, pla, plo, 600000) - 10000) < 1, "map pan moves 25% of the shown width per press");
            }
            Console.WriteLine("ILS + map pan: 3 behavior checks passed.");
            {   // Luke 13:33-13:44: "46 m/s, r 0.6 km" while flying 70-260 m/s -> got lost around DW-R/WP3
                Check(ApproachProfile.EstimateStall(3000, 30) == ApproachProfile.UnmeasuredStallFloor && ApproachProfile.Speed(ApproachProfile.EstimateStall(3000, 30)) >= 80, "unmeasured stall estimate floored (no 31 m/s -> 46 m/s joins)");
                var fast = new RunwayMission { Lat = -.0485997, Lon = -74.724375, EndLat = -.0502119, EndLon = -74.490300, Elevation = 69.1 };
                fast.Step(-1.7, -72.09, 700, 700, 200, false, 600000, 31, 55, 55);
                Check(fast.PlanSpeed >= 199 && Math.Abs(fast.PlanRadius - ApproachProfile.RadiusForG(fast.PlanSpeed, 4)) < 1, "arcs planned at the speed actually flown (200 m/s -> r " + Math.Round(fast.PlanRadius) + " m)");
                bool ab; double xa;
                RunwayMission.LegSteer(.3, -74.0, 0, -74.2, 0, -74.05, 90, 200, 20, 600000, out ab, out xa);
                Check(ab && xa < -3000, "fix sequenced when passed abeam even 3 km off the line (no capture radius needed)");
            }
            Console.WriteLine("Approach speed/radius sanity: 3 behavior checks passed.");
            {   // Luke: turns rated in g, not a lazy fixed 20 deg
                Check(Math.Abs(ApproachProfile.BankForG(2) - 60) < .01 && Math.Abs(ApproachProfile.BankForG(1.5) - 48.19) < .01, "bank = acos(1/n): 2 g -> 60 deg, 1.5 g -> 48 deg");
                Check(Math.Abs(ApproachProfile.RadiusForG(100, 2) - 100 * 100 / (9.81 * Math.Sqrt(3))) < 1e-6 && Math.Abs(ApproachProfile.RadiusForG(100, 2) - ApproachProfile.Radius(100, 60)) < 1, "radius = v^2/(g sqrt(n^2-1)) = same as tan(bank)");
                ApproachProfile.CrewG = 6; ApproachProfile.StructG = 20; ApproachProfile.Backoff = 1;
                Check(ApproachProfile.LoadFactor(4, 150, 45) == 4 && ApproachProfile.Limit == "rating", "default join rating 4 g when the craft allows it");
                Check(ApproachProfile.LoadFactor(9, 300, 45) == 6 && ApproachProfile.Limit == "crew g", "crew g tolerance limits (6 g sustained)");
                ApproachProfile.StructG = ApproachProfile.StructuralG(new double[] { 50, 4.5, 0 }); Check(Math.Abs(ApproachProfile.StructG - 3.6) < 1e-9 && ApproachProfile.LoadFactor(4, 300, 45) == 3.6 && ApproachProfile.Limit == "structure", "weakest part (80% of 4.5 g) limits");
                ApproachProfile.StructG = 20;
                Check(ApproachProfile.LoadFactor(4, 60, 45) < 1.5 && ApproachProfile.Limit == "stall", "stall margin at low speed (" + ApproachProfile.LoadFactor(4, 60, 45).ToString("0.00") + " g at 60 m/s)");
                double bo = 1; for (int i = 0; i < 10; i++) bo = ApproachProfile.StressStep(bo, 17, 20, .1); ApproachProfile.Backoff = bo;
                Check(bo < .65 && ApproachProfile.LoadFactor(4, 150, 45) < 2.6 && ApproachProfile.Limit == "stress back-off", "part stress > 80% backs off the rating");
                for (int i = 0; i < 200; i++) bo = ApproachProfile.StressStep(bo, 5, 20, .1); Check(bo == 1, "recovers once stress is low"); ApproachProfile.Backoff = 1;
                Check(ApproachProfile.TurnPitch(0) == 0 && Math.Abs(ApproachProfile.TurnPitch(60) - 4) < .01 && ApproachProfile.TurnPitch(85) == 0, "back-pressure scales with 1/cos(bank)");
                var gm = new RunwayMission { Lat = -.0485997, Lon = -74.724375, EndLat = -.0502119, EndLon = -74.490300, Elevation = 69.1 };
                gm.Step(1.0, -74.7, 700, 700, 120, false, 600000, 45, 180, 180);
                Check(Math.Abs(gm.PlanG - 4) < 1e-9 && Math.Abs(gm.PlanBank - 75.52) < .01 && gm.TurnBank > 75 && gm.PlanRadius < 400, "join planned and flown at 4 g (bank " + Math.Round(gm.PlanBank) + ", r " + Math.Round(gm.PlanRadius) + " m)");
                var gb = new RunwayMission { Lat = -.0485997, Lon = -74.724375, EndLat = -.0502119, EndLon = -74.490300, Elevation = 69.1, BankDeg = 30 };
                gb.Step(1.0, -74.7, 700, 700, 120, false, 600000, 45, 180, 180);
                Check(gb.PlanBank == 30 && gb.TurnBank == 30, "explicit bank 30 overrides the g rating");
            }
            Console.WriteLine("G-rated turns (crew/structure/stall): 11 behavior checks passed.");
            {   // Luke: choppy g at turn entry. 4 g turn entry at 50 Hz: g-onset ramp, then the plane (0.3 s roll lag) holds 4 g within 0.3 g
                double cmd = 0, act = 0, dt2 = .02, tgt = ApproachProfile.BankForG(4), maxRate = 0, prevN = 1, t = 0, tReach = -1, lo = 99, hi = 0;
                for (int i = 0; i < 1000; i++, t += dt2)
                {
                    cmd = TurnOnset.Bank(cmd, tgt, dt2); double nCmd = 1 / Math.Cos(cmd * Math.PI / 180);
                    maxRate = Math.Max(maxRate, Math.Abs(nCmd - prevN) / dt2); prevN = nCmd;
                    act += (cmd - act) * dt2 / .3; double nAct = 1 / Math.Cos(act * Math.PI / 180);
                    if (tReach < 0 && Math.Abs(cmd - tgt) < .01) tReach = t;
                    if (tReach >= 0 && t > tReach + 1.5) { lo = Math.Min(lo, nAct); hi = Math.Max(hi, nAct); }
                }
                Check(maxRate <= TurnOnset.GRate + 1e-6 && tReach > 2 && tReach < 4, "g onset limited to 1.25 g/s (max " + maxRate.ToString("0.00") + ", 4 g reached at " + tReach.ToString("0.0") + " s)");
                Check(hi - lo < .3 && Math.Abs(hi - 4) < .3, "no g oscillation > 0.3 g after the ramp (" + lo.ToString("0.00") + ".." + hi.ToString("0.00") + " g)");
                double b2 = 40; bool zero = false; for (int i = 0; i < 400; i++) { b2 = TurnOnset.Bank(b2, -40, .02); if (Math.Abs(b2) < 1) zero = true; }
                Check(zero && Math.Abs(b2 + 40) < .01, "reversal ramps through wings-level");
                var sc = new SCurve(); sc.Step(0, .02); double mx = 0, y = 0, t99 = -1; for (int i = 0; i < 300; i++) { y = sc.Step(10, .02); mx = Math.Max(mx, y); if (t99 < 0 && y > 9.9) t99 = i * .02; }
                Check(mx <= 10 + 1e-6 && t99 > 0 && t99 < 3, "pitch S-curve: no overshoot, settles in " + t99.ToString("0.0") + " s");
                Check(TurnOnset.ElevatorFF(0) == 0 && TurnOnset.ElevatorFF(60) > .03 && TurnOnset.ElevatorFF(60) <= .3, "elevator feed-forward from commanded g");
            }
            Console.WriteLine("Turn onset smoothing: 5 behavior checks passed.");
            Check(MapReveal.Zoom(30000, .5) == 15000 && MapReveal.Zoom(3000, .5) == 2000 && MapReveal.Zoom(200000, 2) == 300000 && Math.Abs(MapReveal.Zoom(MapReveal.Zoom(30000, .8), 1.25) - 30000) < 1e-6, "map zoom +/- and wheel, clamped 2-300 km");
        // ---- Luke's approach rules: short vs long final, nearest runway + best end ----
        {
            Check(PilotPolicy.ApproachKind(275, 270, 10000, false) == "short" && PilotPolicy.ApproachKind(180, 270, 10000, false) == "long", "head-on (<=20 deg) -> short final, 90 deg -> long");
            Check(PilotPolicy.ApproachKind(230, 270, 10000, true) == "short" && PilotPolicy.ApproachKind(180, 270, 10000, true) == "long" && PilotPolicy.ApproachKind(270, 270, 3000, true) == "long", "short final asked: only if geometry allows");
            Check(PilotPolicy.SwapEnd(90, 270) && !PilotPolicy.SwapEnd(90, 100), "lands on the end matching our heading");
            Check(PilotPolicy.Nearest(-1.4, -72.0, new List<double[]> { new[] { -.0494, -74.607 }, new[] { -1.5154, -71.909 }, new[] { 2.0, -70.0 } }, 600000) == 1, "nearest of built-ins + saved runways");
            Check(ToolRouter.Direct("land at 27 short final", "plane").Value.Value == "{\"where\":\"27 short final\"}" && FlightResidualPolicy.BuiltInRunway("27 short final", "").Item2 == "27", "'short final' in chat");
            Check(NativePlan.Parse("land KSC 27 short final", n => false, r => false).Steps[0].Op == "land", "'short final' in plans");
            double R = 600000;
            Func<double, double, double, RunwayMission> Sim = (lat0, lon0, h0) =>
            {
                var m = new RunwayMission { Lat = -.0502119, Lon = -74.490300, EndLat = -.0485997, EndLon = -74.724375, Elevation = 70 };
                double la = lat0, lo = lon0, h = h0, al = 800, sp = 120;
                for (int s = 0; s < 1500 && m.Phase != "rollout" && m.Phase != "go around"; s++)
                {
                    m.Step(la, lo, al, al - 70, sp, al <= 70.5, R, 45, h);
                    double e = FlightPolicy.Wrap(m.DesiredHeading - h); h = (h + Math.Max(-1.7, Math.Min(1.7, e)) + 360) % 360;
                    al += Math.Max(-25, Math.Min(10, m.DesiredVs)); if (al < 70) al = 70; sp += Math.Max(-2, Math.Min(2, m.DesiredSpeed - sp));
                    double nl, no; NavigationMath.Offset(la, lo, h, sp, R, out nl, out no); la = nl; lo = no;
                }
                return m;
            };
            double crs = NavigationMath.Bearing(-.0502119, -74.490300, -.0485997, -74.724375), sl, so;
            NavigationMath.Offset(-.0502119, -74.490300, crs + 180, 10000, R, out sl, out so);
            var headOn = Sim(sl, so, (crs + 8) % 360);
            Check(headOn.Kind == "short" && headOn.FixDistance == 4000 && (headOn.Phase == "rollout" || headOn.Phase == "flare" || headOn.Phase == "final"), "head-on sim: short final, lands (" + headOn.Kind + "/" + headOn.Phase + " " + headOn.Why + " " + headOn.RouteLog + " along=" + headOn.Along.ToString("0") + ")");
            NavigationMath.Offset(sl, so, crs + 90, 6000, R, out sl, out so);
            var abeam = Sim(sl, so, (crs + 90) % 360);
            Check(abeam.Kind == "long" && abeam.Phase != "go around", "90 deg sim: long final (" + abeam.Kind + "/" + abeam.Phase + ")");
        }
        Console.WriteLine("Approach rules (short/long final, nearest + best end): 8 behavior checks passed.");
        // ---- dashboard rows come from the in-mod controller ----
        Check(DashboardRows.Local("hold", "p")[1][1] == "Local hold" && DashboardRows.Local(null, null)[1][1] == "Local idle", "dashboard shows the in-mod controller state");
        Console.WriteLine("Dashboard rows: 1 behavior check passed.");
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
        {   // crash 047208f: takeoff -> hold/climb handoff must not spike g
            var ps = new PitchShaper(); double pitch = 9, rate = 0, maxG = 0, v = 110;
            for (int i = 0; i < 200; i++) { double d = ps.Step(9, pitch, v, 3, 0, .02); pitch += (d - pitch) / .4 * .02; }
            ps.Reset(pitch);   // mode change: takeoff -> hold
            for (int i = 0; i < 600; i++)
            {
                double d = ps.Step(15 + 3, pitch, v, 4.5, 0, .02);   // climb target + stray back-pressure-sized step, joins-level g limit requested
                double np = pitch + (d - pitch) / .4 * .02; rate = (np - pitch) / .02; pitch = np;
                double g = Math.Cos(pitch * Math.PI / 180) + v * rate * Math.PI / 180 / 9.81; maxG = Math.Max(maxG, g);
            }
            Check(maxG < 2, "takeoff->climb handoff: no g spike above 2 g (max " + maxG.ToString("0.00") + ")");
            Check(PitchShaper.BackPressure(0) == 0 && PitchShaper.ElevatorFF(2) == 0 && PitchShaper.BackPressure(45) > 0, "no turn feed-forward when wings level");
            Check(PitchShaper.Limit(4.5, 0) == PitchShaper.ClimbG && PitchShaper.Limit(4.5, 40) == 4.5, "high g only in commanded turns");
            Check(PitchShaper.ElevatorRate(3.5, 3, -.1, .2) > 1, "over-g unloads the elevator fast");
            Console.WriteLine("Climb handoff g: 4 behavior checks passed.");
        }
        {   // "follow the flight plan" runs the existing plan; only make/new plan replaces it
            foreach (var m in new[] { "Follow flight plan", "follow the flight plan", "fly the plan", "resume flight plan", "run my plan", "continue the plan." })
            { Check(PilotPolicy.IsFollowPlan(m), "follow phrase: " + m); var d = ToolRouter.Direct(m, "plane"); Check(d != null && d.Value.Key == "flightplan/follow", "routes to follow: " + m); }
            foreach (var m in new[] { "make a plan: takeoff, land", "new flight plan", "clear plan", "follow terrain" }) Check(!PilotPolicy.IsFollowPlan(m), "not follow: " + m);
            var mk = ToolRouter.Direct("Make a plan, Takeoff, Climb to 2000, land ksc 09", "plane"); Check(mk != null && mk.Value.Key == "make_flight_plan", "make a plan still replaces");
            Console.WriteLine("Follow flight plan: 14 behavior checks passed.");
        }
        {   // coupled ILS: the AP flies the same LOC/GS deviations the ILS tab shows
            double tLa = -.0485997, tLo = -74.724375; var rmI = new RunwayMission { Lat = tLa, Lon = tLo, EndLat = -.0502119, EndLon = -74.490300, Elevation = 69.1 };
            double crsI = NavigationMath.Bearing(tLa, tLo, rmI.EndLat, rmI.EndLon), pLa, pLo, qLa, qLo;
            NavigationMath.Offset(tLa, tLo, crsI + 180, 3000, 600000, out pLa, out pLo); NavigationMath.Offset(pLa, pLo, crsI + 90, 20, 600000, out qLa, out qLo);   // 3 km out, 20 m right
            double gAlt = 69.1 + 3350 * Math.Tan(3 * Math.PI / 180);
            rmI.Phase = "final"; rmI.Kind = "long";   // established inbound
            for (int i = 0; i < 3; i++) rmI.Step(qLa, qLo, gAlt, gAlt - 0, 65, false, 600000, 45, crsI, crsI);
            Check(rmI.Phase == "final" && rmI.Coupled && rmI.GsCoupled, "short final aligned: LOC then GS coupled (" + rmI.Phase + ")");
            Check(Math.Abs(rmI.Ils.CrossM - rmI.Cross) < 2 && rmI.Ils.CrossM > 15, "ILS cross-track = controller input (same sign, 20 m right)");
            Check(Math.Abs(rmI.DesiredAltitude - (gAlt - rmI.Ils.AboveGsM)) < .01 && FlightPolicy.Wrap(rmI.DesiredHeading - crsI) < 0, "GS path and a left correction come from the ILS reading");
            rmI.GoAroundReset(); Check(!rmI.Coupled && !rmI.GsCoupled, "go-around uncouples");
            Console.WriteLine("Coupled ILS: 4 behavior checks passed.");
        }
        {   // crash 15:18: 200 m/s at the FAF. Forced approach speed (idle + airbrakes), speed gates, soft flare
            Func<double, string> sim = (dragK) =>
            {
                double tLa = -.0485997, tLo = -74.724375, el = 69.1; var m = new RunwayMission { Lat = tLa, Lon = tLo, EndLat = -.0502119, EndLon = -74.490300, Elevation = el };
                double crs = NavigationMath.Bearing(tLa, tLo, m.EndLat, m.EndLon), la, lo; NavigationMath.Offset(tLa, tLo, crs + 180, 12000, 600000, out la, out lo);
                double alt = el + 12350 * Math.Tan(3 * Math.PI / 180), v = 200, vs = 0, thr = .6, dt = .05, touchVs = double.NaN, fastAt1k = double.NaN; m.Phase = "final"; m.Kind = "long";
                for (int i = 0; i < 20000; i++)
                {
                    bool gnd = alt <= el + .01;
                    m.Step(la, lo, alt, alt - el, v, gnd, 600000, 45, crs, crs);
                    if (m.Phase == "go around") return "GA " + m.Why + " v=" + v.ToString("0") + " d=" + m.Distance.ToString("0") + " des=" + m.DesiredSpeed.ToString("0") + " vs=" + vs.ToString("0.0");
                    if (gnd) { touchVs = vs; break; }
                    if (m.Distance < 1000 && double.IsNaN(fastAt1k)) fastAt1k = v - 1.3 * 45;
                    thr = PilotPolicy.ApproachThrottle(thr, v, m.DesiredSpeed, dt); bool brakes = v > m.DesiredSpeed + 8;
                    vs += FlightPolicy.Clamp(m.DesiredVs - vs, -3 * dt, 3 * dt);
                    double a = 6 * thr - dragK * v * v * (brakes ? 2 : 1) - 9.81 * vs / Math.Max(30, v);
                    v = Math.Max(20, v + a * dt); alt = Math.Max(el, alt + vs * dt);
                    NavigationMath.Offset(la, lo, crs, v * dt, 600000, out la, out lo);
                }
                return "touch " + touchVs.ToString("0.0") + " fast1k " + fastAt1k.ToString("0");
            };
            string draggy = sim(.00012), slick = sim(.000005);
            Check(draggy.StartsWith("touch") && double.Parse(draggy.Split(' ')[3]) <= 10 && Math.Abs(double.Parse(draggy.Split(' ')[1])) < 2, "200 m/s at the FAF, idle + airbrakes: stabilized by 1 km, touchdown sink < 2 m/s (" + draggy + ")");
            Check(slick.StartsWith("GA too fast"), "slick craft still fast: go-around at the speed gate, never a fast landing (" + slick + ")");
            Check(PilotPolicy.ApproachThrottle(.6, 80, 60, .05) == 0 && PilotPolicy.ApproachThrottle(.2, 50, 60, 1) > .2, "approach throttle: idle when fast, spools when slow");
            Console.WriteLine("Forced approach speed: 3 behavior checks passed.");
        }
        {   // Luke 3:28 PM: "Make a plan, Take off, CLimb to 2000, Hold heading for 3k, turnaround, Land runway 27"
            string pl; List<string> un, no;
            Check(PilotPolicy.TryPlan("Make a plan, Take off, CLimb to 2000, Hold heading for 3k, turnaround, Land runway 27", true, out pl, out un, out no), "Luke's sentence parses confidently (" + string.Join("|", un.ToArray()) + ")");
            Check(pl == "takeoff\nclimb 2000 m msl\ncruise for 3 km\nturn around\nland KSC 27", "steps: " + pl.Replace("\n", " / "));
            var np = NativePlan.Parse(pl); Check(np.Steps[2].Op == "cruise" && np.Steps[2].Distance == 3000, "hold heading for 3k = 3 km ground distance, not seconds");
            var dm = Program.DirectOrNull("Make a plan, Take off, CLimb to 2000, Hold heading for 3k, turnaround, Land runway 27"); Check(dm == "make_flight_plan", "routes straight to the plan tool (" + dm + ")");
            Check(NativePlan.Parse("cruise for 3000 m").Steps[0].Distance == 3000 && NativePlan.Parse("hold heading for 2 km").Steps[0].Distance == 2000 && NativePlan.Parse("cruise for 3k").Steps[0].Distance == 3000, "runner: distance steps in m / km / k");
            Func<string, bool> ok = l => { try { NativePlan.Parse(l); return true; } catch (Exception) { return false; } };
            string badS, fx = PilotPolicy.FixPlan("takeoff\nclimb 2000 m msl\ncruise for 3000 m\nheading hold for 300 s\nturn around\nland KSC 27", ok, out badS);
            Check(badS == null && ok(fx), "the model's plan from the log is auto-corrected to runner syntax (" + fx.Replace("\n", " / ") + ")");
            fx = PilotPolicy.FixPlan("takeoff\nloop the loop\nland KSC 27", ok, out badS); Check(badS == "loop the loop", "one bad step is reported alone");
            string msg = ""; try { NativePlan.Parse("takeoff\nclimb 2000 m msl\nbarrel roll twice\nland KSC 27"); } catch (ArgumentException ex) { msg = ex.Message; }
            Check(msg.Contains("step 3") && msg.Contains("barrel roll twice") && !msg.Contains("climb 2000"), "error names the exact bad step, no plan dump (" + msg + ")");
            Console.WriteLine("Distance plan steps: 8 behavior checks passed.");
        }
        {   // 15:30 bugs: AP airbrakes are not tampering; duplicated result tails; ILS auto-tab zone
            Check(PilotPolicy.BrakesRowLevel(true, true, true) == "ok" && PilotPolicy.BrakesRowLevel(true, true, false) == "caution" && PilotPolicy.BrakesRowLevel(false, true, false) == "ok", "airbrakes deployed by the approach logic: no Brakes caution");
            string fin = InModChatSession.Final("Flying to KSC.", new List<string> { "Flying to KSC (30 km); will circle on arrival.", "Throttle 50%.", "Not on a local approach.", "Throttle 50%.", "Not on a local approach.", "Throttle 50%." });
            Check(fin.Split(new[] { "[Throttle 50%.]" }, StringSplitOptions.None).Length == 2 && fin.Split(new[] { "[Not on a local approach.]" }, StringSplitOptions.None).Length == 2, "each tool result tail shown once (" + fin + ")");
            double tLa = -.0485997, tLo = -74.724375, la, lo; double crs = NavigationMath.Bearing(tLa, tLo, -.0502119, -74.4903);
            NavigationMath.Offset(tLa, tLo, crs + 180, 15000, 600000, out la, out lo);
            Check(Ils.InLocCaptureZone(Ils.Compute(la, lo, 900, tLa, tLo, -.0502119, -74.4903, 69, 600000), RunwayMission.LocRange), "15 km out on the centerline: localizer capture zone");
            NavigationMath.Offset(tLa, tLo, crs + 180, 30000, 600000, out la, out lo);
            Check(!Ils.InLocCaptureZone(Ils.Compute(la, lo, 900, tLa, tLo, -.0502119, -74.4903, 69, 600000), RunwayMission.LocRange), "30 km out: not yet");
            {   // ILS hand-flying guidance
                double qLa, qLo; NavigationMath.Offset(tLa, tLo, crs + 180, 2500, 600000, out qLa, out qLo); NavigationMath.Offset(qLa, qLo, crs + 90, 100, 600000, out qLa, out qLo);
                var rr = Ils.Compute(qLa, qLo, 69 + 180, tLa, tLo, -.0502119, -74.4903, 69, 600000, 350);
                Check(FlightPolicy.Wrap(Ils.FdHeading(rr, 60) - crs) < -1 && Ils.FdVs(rr, 60) < -3, "100 m right, 30 m high: steer left, descend");
                var co = Ils.Callouts(rr, 50, false, 80, 60); Check(co.Contains("GEAR") && co.Contains("TOO FAST") && co.Contains("MINIMUMS"), "callouts: " + string.Join(",", co.ToArray()));
                Check(Ils.SpeedBand(62, 60) == "green" && Ils.SpeedBand(68, 60) == "amber" && Ils.SpeedBand(75, 60) == "red", "IAS band colours");
            }
            {   // IVA MFD text pages (RasterPropMonitor)
                double pLa, pLo; NavigationMath.Offset(tLa, tLo, crs + 180, 5000, 600000, out pLa, out pLo);
                var ri = Ils.Compute(pLa, pLo, 69 + 280, tLa, tLo, -.0502119, -74.4903, 69, 600000, 350);
                string ip = MfdText.IlsPage(ri, "KSC 09", "COUPLED LOC+GS", false, false, 80, 60, 280, -4, 40, 20);
                Check(ip.StartsWith("AICS ILS KSC 09") && ip.Contains("COUPLED") && ip.Contains("TOO FAST") && Array.TrueForAll(ip.TrimEnd('\n').Split('\n'), l => l.Length <= 40), "ILS MFD page fits 40 cols, coupling + callouts");
                string mp = MfdText.MapPage(pLa, pLo, 600000, 20000, new List<double[]> { new[] { tLa, tLo }, new[] { -.0502119, -74.4903 } }, null, null, 40, 20);
                Check(mp.Contains("^") && mp.Contains("=") && mp.Split('\n').Length >= 20, "MAP MFD page: plane and runway drawn");
            }
            Console.WriteLine("Airbrakes/dedupe/ILS zone/MFD: 9 behavior checks passed.");
        }
        {   // Luke 3:41 PM: final speed schedule (not 1.3x stall from 20 km)
            Check(ApproachProfile.FinalSchedule(12000, 45) > 100 && ApproachProfile.FinalSchedule(12000, 45) <= 150 && ApproachProfile.FinalSchedule(8000, 80) == 150, "12->4 km: ~2.3x stall, cap 150");
            double s3 = ApproachProfile.FinalSchedule(3000, 45), s2 = ApproachProfile.FinalSchedule(2000, 45);
            Check(s3 < ApproachProfile.FinalSchedule(4000, 45) && s3 > s2 && Math.Abs(s2 - 1.35 * 45) < .01 && ApproachProfile.FinalSchedule(500, 45) == s2, "smooth decel to 1.35x stall by 2 km, held to the flare");
            Check(ApproachProfile.SaneStall(33) == 40 && ApproachProfile.SaneStall(double.NaN) >= 40, "stall estimate sanity floor");
            Console.WriteLine("Final speed schedule: 3 behavior checks passed.");
        }
        {   // Luke 3:44 PM: KSC 27 localizer must be the TRUE runway centerline (landed 230 m south)
            var r27 = Ils.Compute(-.0486, -74.60, 69.1, KscRunway.Lat, KscRunway.Lon27, KscRunway.Lat, KscRunway.Lon09, 69.1, 600000);
            Check(Math.Abs(r27.CrossM) < 2 && Math.Abs(r27.LocDeg) < .02, "point on the runway at lat -0.0486: ILS 27 cross-track ~0 (" + r27.CrossM.ToString("0.0") + " m)");
            var r09 = Ils.Compute(-.0486, -74.75, 400, KscRunway.Lat, KscRunway.Lon09, KscRunway.Lat, KscRunway.Lon27, 69.1, 600000);
            Check(Math.Abs(r09.CrossM) < 2 && Math.Abs(FlightPolicy.Wrap(r09.Course - 90)) < .1, "ILS 09 course 090, on centerline west of the runway");
            var south = Ils.Compute(-.0507, -74.48, 120, KscRunway.Lat, KscRunway.Lon27, KscRunway.Lat, KscRunway.Lon09, 69.1, 600000);
            Check(south.CrossM < -15 && south.CrossM > -30, "the 15:4x landing line (lat -0.0507 = 0.0021 deg = ~22 m) reads south of the 27 course (" + south.CrossM.ToString("0") + " m)");
            Console.WriteLine("KSC runway centerline: 3 behavior checks passed.");
        }
        {   // Luke 3:45 PM taxi: low throttle, wheel brakes when fast, full stop first
            var tx = new TaxiMission("0,0.05", "Kerbin", 25); double v = 30, thrMax = 0;
            for (int i = 0; i < 40; i++) { tx.Step(i * .1, 0, 0, 90, v, 600000, true, false, 2); if (tx.Brakes) v = Math.Max(0, v - 1.5); }
            Check(v < .5, "rolling at 30 m/s: brakes to a full stop before taxiing");
            for (int i = 40; i < 2000; i++) { tx.Step(i * .1, 0, 0, 90, v, 600000, true, false, 2); thrMax = Math.Max(thrMax, tx.Throttle); v = Math.Max(0, v + (tx.Throttle * 20 - (tx.Brakes ? 3 : .2)) * .1); }
            Check(thrMax <= TaxiMission.MaxThrottle(2) + 1e-9 && TaxiMission.MaxThrottle(2) <= .15 && TaxiMission.MaxThrottle(.5) <= .3, "taxi throttle capped (TWR-scaled, max " + thrMax.ToString("0.00") + ")");
            Check(v > 5 && v < 11.5, "taxi speed ~8-10 m/s (" + v.ToString("0.0") + ")");
            tx.Step(300, 0, 0, 90, 16, 600000, true, false, 2); Check(tx.Brakes && tx.Throttle == 0, "over speed: wheel brakes, idle");
            Console.WriteLine("Taxi speed: 4 behavior checks passed.");
        }
        {   // taxi ground charts: join the nearest point ahead, follow the centerline, full stop (Luke 4:24 / 4:26 PM)
            const double R = 600000; double sphLat = -.0915, sphLon = -74.6300, hubLat = -.0930, hubLon = -74.6000;
            var defs = TaxiRoute.KscDefaults(sphLat, sphLon, hubLat, hubLon);
            Check(defs.Count == 4 && defs.TrueForAll(r => r.Points.Count >= 4), "4 default KSC taxi charts (both runway ends <-> SPH)");
            string why; var back = TaxiRoute.Parse(defs[0].ToJson(), out why);
            Check(back != null && back.Points.Count == defs[0].Points.Count && TaxiRoute.Parse("{oops", out why) == null && TaxiRoute.Parse("{\"points\":[{\"lat\":0,\"lon\":0}]}", out why) == null, "taxi chart JSON round trip; bad file rejected (old route kept)");
            Check(TaxiRoute.Pick("hangar", KscRunway.Lat, -74.55) == "RWY27_TO_SPH" && TaxiRoute.Pick("hangar", KscRunway.Lat, -74.68) == "RWY09_TO_SPH" && TaxiRoute.Pick("runway 27", sphLat, sphLon) == "SPH_TO_RWY27", "route picked from where the plane stopped (either runway end)");
            var r27 = TaxiRoute.Parse(System.Linq.Enumerable.First(defs, d => d.Name == "RWY27_TO_SPH").ToJson(), out why);
            int leg = r27.Join(KscRunway.Lat + .002, -74.55, R);   // stopped after a 27 landing, ~21 m off the centerline
            Check(leg == 0, "joins the runway leg ahead (not the threshold behind)");
            var tm = new TaxiMission(r27, leg, 9); double lat = KscRunway.Lat + .002, lon = -74.55, hdg = 270, v = 0, t = 0, maxCrossLate = 0; bool halted = false;
            for (int i = 0; i < 6000 && !halted; i++)
            {
                tm.Step(t, lat, lon, hdg, v, R, true, false, 1); t += .1;
                if (tm.Result != null) { halted = true; break; }
                hdg = (hdg + FlightPolicy.Clamp(tm.Wheel * -40 * 0 + 0, -1, 1)) % 360;
                double err = FlightPolicy.Wrap(NavigationMath.Bearing(lat, lon, lat, lon) - 0);
                double acc = tm.Brakes ? -3 : tm.Throttle * 6 - .3; v = Math.Max(0, v + acc * .1);
                // steer: heading follows the wheel command (simple kinematic model)
                hdg = (hdg - tm.Wheel * 25 * .1 * Math.Min(1, v / 3) + 360) % 360;
                double dl = v * .1 / (Math.PI / 180 * R); lat += dl * Math.Cos(hdg * Math.PI / 180); lon += dl * Math.Sin(hdg * Math.PI / 180) / Math.Cos(lat * Math.PI / 180);
                if (tm.Leg == 0 && lon < -74.575 && lon > -74.598) maxCrossLate = Math.Max(maxCrossLate, Math.Abs(tm.Cross));
            }
            Check(halted && tm.Result.StartsWith("Taxi arrived") && tm.Brakes, "taxi follows the chart to the SPH apron and stops (" + (tm.Result ?? "running") + ")");
            Check(maxCrossLate < 5, "on the runway leg the centerline is tracked (cross " + maxCrossLate.ToString("0.0") + " m)");
            var d1 = ToolRouter.Direct("taxi to hangar", "plane"); var d2 = ToolRouter.Direct("taxi to runway 09", "plane");
            Check(d1 != null && d1.Value.Key == "taxi_route" && d1.Value.Value.Contains("hangar") && d2 != null && d2.Value.Value.Contains("runway 09"), "chat: taxi to hangar / taxi to runway 09");
            Console.WriteLine("Taxi ground charts: 7 behavior checks passed.");
        }
        {   // derotation: nose lowered at <= 2.5 deg/s, no forward-stick spike, brakes only after nose-wheel contact (Luke 4:26 PM)
            var dr = new Derotation(); double pitch = 7, q = 0, maxRate = 0, minElev = 1; bool brakesEarly = false;
            for (int i = 0; i < 200; i++)
            {
                bool nose = pitch < .3; double before = dr.Command; double c = dr.Step(pitch, .05, nose);
                if (!double.IsNaN(before)) maxRate = Math.Max(maxRate, (before - c) / .05);
                double e = dr.Elevator(pitch, q); if (!dr.NoseDown) { minElev = Math.Min(minElev, e); if (dr.BrakesAllowed) brakesEarly = true; }
                q = FlightPolicy.Clamp(10 * (c - pitch), -3, 3); pitch = Math.Max(0, pitch + q * .05);
            }
            Check(maxRate <= Derotation.Rate + 1e-6, "nose lowered at <= 2.5 deg/s (" + maxRate.ToString("0.00") + ")");
            Check(minElev >= Derotation.MinElevator - 1e-9, "no forward-stick spike before nose contact (min " + minElev.ToString("0.00") + ")");
            Check(dr.NoseDown && dr.BrakesAllowed && !brakesEarly, "wheel brakes only after the nose wheel is down");
            Console.WriteLine("Derotation: 3 behavior checks passed.");
        }

        {   // AICS MFD navigation (Luke 4:29 PM): HOME groups, paged items, BACK fixed bottom-left, every old menu item reachable
            Check(MfdNav.CoversPanels(15, false) && MfdNav.CoversPanels(15, true), "all 15 former menu items have a soft key");
            var hk = MfdNav.HomeKeys(true); var hn = MfdNav.HomeKeys(false);
            Check(Array.IndexOf(hk, "MECHJEB") >= 0 && Array.IndexOf(hn, "MECHJEB") < 0 && Array.IndexOf(hk, "AUTOPILOT") == 0 && Array.IndexOf(hk, "SETTINGS") >= 0, "HOME groups; MECHJEB only when installed");
            var ap = MfdNav.Groups(false)[0]; int pages = MfdNav.Pages(ap);
            var b0 = MfdNav.Bottom(false, "aircraft", 0, pages); var b1 = MfdNav.Bottom(false, "orbitap", 1, pages); var bh = MfdNav.Bottom(true, null, 0, 1);
            Check(pages == 2 && MfdNav.PageItems(ap, 1).Count == ap.Items.Count - MfdNav.Side && b0[MfdNav.NextKey] == "NEXT" && b0[MfdNav.PrevKey] == null && b1[MfdNav.PrevKey] == "PREV", "groups with more items page with PREV / NEXT");
            bool back = bh[MfdNav.BackKey] == null; foreach (var g in MfdNav.Groups(true)) foreach (var it in g.Items) back &= MfdNav.Bottom(false, it.Id, 0, MfdNav.Pages(g))[MfdNav.BackKey] == "BACK";
            Check(back, "BACK at the same key (bottom-left) on every page except HOME");
            Check(Array.IndexOf(MfdNav.Right("taxi"), "HANGAR") >= 0 && MfdNav.SmartMode("RETRO") == "RETROGRADE" && Array.IndexOf(MfdNav.Right("mjguide"), "EXEC NODE") >= 0, "context keys: taxi to hangar, MechJeb modes");
            Check(MfdNav.ChatKind("You: hi") == "pilot" && MfdNav.ChatKind("AICS: Taxi arrived") == "system" && MfdNav.ChatKind("[SYSTEM] CAUTION: x") == "system" && MfdNav.ChatKind("Jebediah: roger") == "intercom" && MfdNav.ChatShows("all", "anything"), "COMMS filter pages: PILOT / SYSTEM / INTERCOM / ALL");
            Check(MfdNav.IsMultiplayer(new[] { "KSPChatBridge", "LmpClient" }) && MfdNav.IsMultiplayer(new[] { "DarkMultiPlayer" }) && !MfdNav.IsMultiplayer(new[] { "MechJeb2" }), "Luna Multiplayer / DMP detected -> single-player warning");
            Check(MfdNav.RpmHidden("auto", true) && !MfdNav.RpmHidden("auto", false) && MfdNav.RpmHidden("off", false) && !MfdNav.RpmHidden("on", true), "RPM AICS pages auto/on/off: auto hides them where the native MFD is");
            Console.WriteLine("AICS MFD: 8 behavior checks passed.");
        }

        {   // native IVA MFD (Phase 2, Luke 4:41 PM): typing fields, face layout / hit test, stock cockpits patched
            var f = new MfdField(false); var r1 = f.Feed("helo\bp", false, false); var r2 = f.Feed(" me\n", false, false);
            Check(r1 == MfdField.Result.None && r2 == MfdField.Result.Submit && f.Text.ToString() == "help me", "IVA chat field: type, backspace, Enter submits (" + f.Text + ")");
            var pf = new MfdField(true); pf.Feed("takeoff", false, false); pf.Feed("\n", true, false); pf.Feed("land", false, false);
            Check(pf.Text.ToString() == "takeoff\nland" && pf.Feed("", false, true) == MfdField.Result.Cancel, "plan field: Shift+Enter new line, Esc unfocuses");
            var keys = IvaLayout.Keys(); int n = 0; bool apart = true;
            foreach (var k in keys) { if (k.Id != "H0" && k.Id != "A0") n++; if (IvaLayout.Hit(k.X + k.Wd / 2, k.Y + k.Ht / 2) != k.Id) apart = false; }
            var sc = IvaLayout.Screen;
            Check(n == 32 && keys.Count == 34 && apart && IvaLayout.Hit(sc.X + 50, sc.Y + 50) == "S" && IvaLayout.Hit(2, 2) == null, "face: 8 keys per side + 8 bottom + 8 spare top, annunciator, COMMS; each hit-tests to itself");
            var bk = keys.Find(k => k.Id == "B0"); Check(bk.X < 20 && bk.Y > IvaLayout.H - 60, "IVA BACK key is bottom-left");
            bool scaled = true; foreach (var wh in new[] { new[] { 560f, 490f }, new[] { 1280f, 1120f } }) { var big = IvaLayout.KeysOf(wh[0], wh[1]); var sb2 = IvaLayout.ScreenOf(wh[0], wh[1]); foreach (var k in big) scaled &= k.X >= 0 && k.Y >= 0 && k.X + k.Wd <= wh[0] + .5f && k.Y + k.Ht <= wh[1] + .5f; scaled &= sb2.Wd > 200 && sb2.Ht > 150; }
            var kr0 = keys.Find(k => k.Id == "R0"); int kp = IvaLayout.KeyFont("MASTER WARNING", kr0.Wd, kr0.Ht, 15); var kl = IvaLayout.KeyLines("MASTER WARNING", kr0.Wd, kp);
            Check(scaled && kp >= 8 && kl.Count <= 2 && kl.TrueForAll(x => x.Length * .6f * kp <= kr0.Wd), "layout stretches to any size; key labels keep glyph aspect and wrap (" + kp + " px, " + kl.Count + " lines)");
            Check(IvaLayout.Wrap("one two three four", 9).Count == 3 && IvaLayout.Wrap("a\nb", 9).Count == 2, "screen text wraps to the columns");
            string root2 = System.IO.Directory.GetCurrentDirectory(); while (root2 != null && !System.IO.Directory.Exists(System.IO.Path.Combine(root2, "KSPChatMod"))) root2 = System.IO.Path.GetDirectoryName(root2);
            string iva = System.IO.File.ReadAllText(System.IO.Path.Combine(root2, "KSPChatMod", "AICS_IVA.cfg"));
            bool all = true; foreach (var ii in new[] { "mk1CockpitInternal", "mk2InlineInternal", "Mk1-3", "mk2CockpitStandardInternals", "mk1InlineInternal" }) all &= iva.Contains("@INTERNAL[" + ii + "]");
            Check(all && iva.Contains("name = AicsIvaMfd") && System.IO.File.ReadAllText(System.IO.Path.Combine(root2, "tools", "package_release.ps1")).Contains("AICS_IVA.cfg"), "AICS_MFD prop patched into 5 stock cockpits and shipped");
            Console.WriteLine("IVA MFD: 7 behavior checks passed.");
        }

        {   // repo checks ported from the removed Python suite (bridge removal, P5-8)
            string root = System.IO.Directory.GetCurrentDirectory();
            while (root != null && !System.IO.Directory.Exists(System.IO.Path.Combine(root, "KSPChatMod"))) root = System.IO.Path.GetDirectoryName(root);
            Check(root != null, "repo root found");
            string pkg = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "tools", "package_release.ps1"));
            Check(pkg.Contains("personalities.txt") && pkg.Contains("AICS_RPM.cfg") && pkg.Contains("Defaults\\charts") && !pkg.Contains("AICSBridge") && !pkg.Contains("PyInstaller"), "release ships personalities, RPM pages, default charts; no bridge exe");
            Check(PersonalityPrompts.All.Length == 36, "36 personality prompts generated from personalities.txt (" + PersonalityPrompts.All.Length + ")");
            string crewSrc = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "KSPChatMod", "NativeCrew.cs"));
            Check(crewSrc.Contains("class AicsCrewScenario : ScenarioModule") && crewSrc.Contains("AddToAllGames"), "kerbal personalities persist per save");
            foreach (var f in System.IO.Directory.GetFiles(System.IO.Path.Combine(root, "KSPChatMod"), "*.cs"))
            {
                string src = System.IO.File.ReadAllText(f);
                Check(!src.Contains("127.0.0.1:8765") && !src.Contains("KRPC.") && !src.Contains("run_bridge"), "no bridge / kRPC code left: " + System.IO.Path.GetFileName(f));
            }
            string netkan = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "ckan", "KSPChatBridge.netkan"));
            Check(!netkan.Contains("kRPC") && !netkan.Contains("\"depends\""), "CKAN draft: no kRPC dependency");
            Console.WriteLine("Repo / packaging checks: 6 behavior checks passed.");
        }

        {   // measured deceleration drives the decel start (Luke 3:43 PM)
            var dl = new DecelLearner(); for (int i = 0; i < 200; i++) dl.Learn("Aeris 3A", 60 + i % 60, 2.5, true);
            double a = dl.Estimate("Aeris 3A", 104, 54, true); Check(Math.Abs(a - 2.5) < .01 && double.IsNaN(dl.Estimate("Unknown", 104, 54, true)), "decel learned per craft (" + a.ToString("0.00") + ")");
            var back = DecelLearner.FromJson(dl.ToJson()); Check(Math.Abs(back.Estimate("Aeris 3A", 104, 54, true) - 2.5) < .01, "decel profile persists (JSON round trip)");
            double fast = ApproachProfile.DecelStartDist(45, 2.5), slow = ApproachProfile.DecelStartDist(45, double.NaN), v1 = ApproachProfile.HiSpeed(45), v2 = ApproachProfile.AppSpeed(45);
            Check(Math.Abs(fast - (2000 + (v1 * v1 - v2 * v2) / 5 + 500)) < 1 && slow > fast, "decel start = (v1^2-v2^2)/2a + margin before 2 km; unmeasured = conservative (" + Math.Round(fast) + " vs " + Math.Round(slow) + " m)");
            Check(Math.Abs(ApproachProfile.FinalSchedule(fast, 45, 2.5) - v1) < .01 && Math.Abs(ApproachProfile.FinalSchedule(2000, 45, 2.5) - v2) < .01, "schedule: long-final speed until the decel point, approach speed by 2 km");
            Console.WriteLine("Measured decel: 4 behavior checks passed.");
        }
        {   // hot-swappable chart files
            string cd = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aics_charts_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(cd);
            try
            {
                string ap = System.IO.Path.Combine(cd, "approaches.json");
                System.IO.File.WriteAllText(ap, "{\"KSC 09\":{\"alt_ref\":\"agl\",\"fixes\":[]},\"Island 27\":{\"fixes\":[]}}");
                ChartStore.Dir = System.IO.Path.Combine(cd, "charts"); ChartStore.ResetWatch();
                Check(ChartStore.Migrate(ap) == 2 && System.IO.File.Exists(System.IO.Path.Combine(ChartStore.Dir, "KSC_09.json")) && System.IO.File.Exists(ap + ".premigrate.bak"), "approaches.json split into per-end files + backup");
                string v; Check(ChartStore.KeyOf("KSC_09_tight.json", out v) == "KSC 09" && v == "tight" && ChartStore.FileName("Island 27", "") == "Island_27.json", "file names <-> runway keys/variants");
                string why; Check(ChartStore.Read("KSC 09", out why) != null, "default chart read");
                ChartStore.Write("KSC 09", "tight", "{\"fixes\":[],\"tight\":true}");
                Check(((Dictionary<string, object>)ChartStore.Read("KSC 09", out why)).ContainsKey("tight") == false, "Luke's chart stays default");
                ChartStore.SetActive("KSC 09", "tight");
                Check(((Dictionary<string, object>)ChartStore.Read("KSC 09", out why)).ContainsKey("tight"), "active variant picked");
                ChartStore.Changed();   // prime
                System.Threading.Thread.Sleep(20); System.IO.File.WriteAllText(System.IO.Path.Combine(ChartStore.Dir, "Island_27.json"), "{bad json");
                System.IO.File.SetLastWriteTimeUtc(System.IO.Path.Combine(ChartStore.Dir, "Island_27.json"), DateTime.UtcNow.AddSeconds(5));
                var ch = ChartStore.Changed(); Check(ch.Count == 1 && ch[0] == "Island 27", "mtime watch reports the changed end");
                object badC = null; try { badC = ChartStore.Read("Island 27", out why); } catch (Exception) { }
                Check(badC == null, "bad JSON -> null (caller keeps the old chart)");
                Check(ChartStore.Changed().Count == 0, "no change -> nothing reloaded");
            }
            finally { ChartStore.Dir = null; try { System.IO.Directory.Delete(cd, true); } catch (Exception) { } }
            Console.WriteLine("Chart files: 8 behavior checks passed.");
        }
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
