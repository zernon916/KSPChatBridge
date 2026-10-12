using System;
using System.Collections.Generic;
using KSPChatBridge;

// Oct 11: throttle filter / step-and-wait / anti-windup, heading wrap-around, flight-path angle, manual learn test picker, ABOUT live.
internal static class ThrottleLearnTests
{
    static int fails, passes;
    static void Check(bool ok, string what) { if (ok) passes++; else { fails++; Console.WriteLine("FAIL throttle/learn: " + what); } }
    static bool Near(double a, double b, double tol = 1e-6) { return Math.Abs(a - b) <= tol; }

    internal static void Run()
    {
        Throttle(); Rates(); PathAngle(); Manual(); Live(); Audit(); Audit2(); Oct11Live(); ApReview(); Fingerprint(); LearnThrottleStuck(); LevelOffCapture(); PrepPorpoise(); HighLowTurns(); GraphSeries(); StallZoom(); LearnMenu(); A300Run(); Surfaces(); RatesAtCurrentSpeed(); TurnHoldDamp(); StallPrepDescend(); DescentToTestAlt();
        Console.WriteLine(fails == 0 ? "Throttle/learn (Oct 11): " + passes + " behavior checks passed." : "Throttle/learn (Oct 11): " + fails + " FAILED, " + passes + " passed.");
        if (fails > 0) throw new Exception("throttle/learn tests failed: " + fails);
    }

    static void Throttle()
    {
        Check(Near(ThrottleGate.NormalRate, .04) && Near(SpeedHold.Rate0, .04), "normal throttle rate 4%/s (was 15%/s)");
        // 1) noisy speed at the target: damping on FILTERED accel -> no throttle chatter (raw dv/dt of +-0.05 m/s jitter at 50 Hz is +-5 m/s2)
        {
            var sh = new SpeedHold(); double thr = .5, travel = 0, maxD = 0, prev = .5; var rnd = new Random(7);
            for (int s = 0; s < 3000; s++) { double v = 150 + (rnd.NextDouble() - .5) * .1; double o = sh.Step(150, v, .02, thr); travel += Math.Abs(o - prev); maxD = Math.Max(maxD, Math.Abs(o - prev)); prev = thr = o; }
            Check(travel < .03, "noisy speed at target: throttle travel " + travel.ToString("0.000") + " over 60 s (filtered damping)");
            Check(maxD <= .04 * .02 + 1e-9, "every output change within 4%/s (max " + maxD.ToString("0.0000") + " per 0.02 s)");
            Check(Math.Abs(sh.AccFiltered) < 3, "filtered acceleration stays small under jitter (" + sh.AccFiltered.ToString("0.00") + ")");
        }
        // 2) anti-windup: the integral never moves in a frame that is rate-limited, waiting, or spooling
        {
            var sh = new SpeedHold(); double v = 100, thr = .3, eng = .3; bool sawLim = false, sawWait = false, windup = false; double iPrev = double.NaN;
            for (int s = 0; s < 30000; s++)
            {
                double tgt = s < 500 ? 100 : 125;
                thr = sh.Step(tgt, v, .02, thr); eng += (thr - eng) * .02 / 2; v += (6 * eng - .00035 * v * v) * .02;
                double iNow = sh.Integral;
                if ((sh.RateLimited || sh.Waiting || sh.SpoolPending) && !double.IsNaN(iPrev) && Math.Abs(iNow - iPrev) > 1e-12) windup = true;
                sawLim |= sh.RateLimited; sawWait |= sh.Waiting; iPrev = iNow;
            }
            Check(sawLim && sawWait, "replay exercised rate limit and step-and-wait (limited " + sawLim + ", waiting " + sawWait + ")");
            Check(!windup, "anti-windup: no integral accumulation while rate-limited / waiting / spooling");
            Check(Math.Abs(v - 125) < 3, "speed still reaches the target (" + v.ToString("0.0") + " m/s)");
        }
        // 3) step-and-wait near the target: steps <= 0.5%, then a hold for the learned lag (or until the speed answers)
        {
            var sh = new SpeedHold(); sh.SetLag(4); double v = 120, thr = .5, eng = .5, prevO = .5; var q = new Queue<double>();
            double stepAcc = 0, maxStep = 0; int holds = 0; double holdT = 0, maxHold = 0; bool inStep = false;
            for (int s = 0; s < 20000; s++)
            {
                double tgt = 123;   // 3 m/s off: inside the near band from the start
                double o = sh.Step(tgt, v, .02, thr); q.Enqueue(o); double dly = q.Count > 100 ? q.Dequeue() : .5;
                eng += (dly - eng) * .02 / 2; v += (6 * eng - .00035 * v * v + (6 * .5 - .00035 * 120 * 120) * 0 - (6 * .5 - .00035 * 120 * 120)) * .02;
                double d = o - prevO;
                if (Math.Abs(d) > 1e-12) { if (!inStep) { inStep = true; stepAcc = 0; if (holdT > 0) { holds++; maxHold = Math.Max(maxHold, holdT); } holdT = 0; } stepAcc += d; maxStep = Math.Max(maxStep, Math.Abs(stepAcc)); }
                else { inStep = false; holdT += .02; }
                prevO = thr = o;
            }
            Check(sh.FineSteps > 0 && maxStep <= SpeedHold.FineStep + 1e-9, "near target: throttle moves in steps <= 0.5% (" + sh.FineSteps + " steps, largest " + (maxStep * 100).ToString("0.000") + "%)");
            Check(holds > 0 && maxHold >= 1, "and holds between steps until the speed responds / lag passes (" + holds + " holds, longest " + maxHold.ToString("0.0") + " s)");
            Check(Math.Abs(v - 123) < 2.5, "step-and-wait settles near the target (" + v.ToString("0.0") + ")");
        }
        // 4) emergencies still move fast; normal PI is 4%/s
        {
            var g = new ThrottleGate(); g.Step(0, .02); Check(Near(g.Step(1, .02, "emergency", "low-energy floor"), ThrottleGate.EmergencyRate * .02), "emergency throttle still fast (2/s)");
            var g2 = new ThrottleGate(); g2.Step(0, 1); Check(Near(g2.Step(1, 1), .04), "PI path 4% per second");
        }
        // 5) every low-energy activation gets a reason
        {
            string a = PilotPolicy.LowEnergyReason(80, 74, -1, 2000), b = PilotPolicy.LowEnergyReason(150, 74, -12, 200);
            Check(a.Contains("stall") && b.Contains("sinking") && !b.Contains("stall"), "low-energy reason names the cause (" + a + " | " + b + ")");
        }
    }

    static void Rates()
    {
        Check(Near(RateMeter.WrapRate(359, 1, 1), 2), "heading 359 -> 1 = +2 deg");
        Check(Near(RateMeter.WrapRate(1, 359, 1), -2), "heading 1 -> 359 = -2 deg");
        Check(Near(RateMeter.WrapRate(170, -170, .5), 40), "roll +170 -> -170 = +20 deg in 0.5 s = 40 deg/s");
        var m = new RateMeter(); double hdg = 355, maxAbs = 0;
        for (int k = 0; k < 100; k++) { double t = k * .1; m.Update(t, hdg % 360, (hdg + 1) % 360, 5 + .2 * k * .1, 10); maxAbs = Math.Max(maxAbs, Math.Abs(m.HeadingRate)); hdg += 1; }
        Check(Math.Abs(m.HeadingRate - 10) < .2 && maxAbs < 10.5, "smoothed heading rate through north: +10 deg/s, no wrap spike (max " + maxAbs.ToString("0.0") + ")");
        Check(Math.Abs(m.CourseRate - 10) < .2, "course rate from the velocity track: +10 deg/s");
        Check(Math.Abs(m.PitchRate - .2) < .05 && Math.Abs(m.RollRate) < 1e-6, "pitch rate and roll rate measured separately");
        double hrBefore = m.HeadingRate; m.Update(20, 0, double.NaN, 0, 0); Check(m.HeadingRate == hrBefore && hrBefore != 0, "audit #10: gap > 2 s holds the last rate (no bogus 0) and re-seeds");
    }

    static void PathAngle()
    {
        Check(Near(PilotPolicy.FlightPathAngle(10, 10), 45), "fpa atan2(10,10) = 45");
        Check(Near(PilotPolicy.FlightPathAngle(0, 100), 0) && Near(PilotPolicy.FlightPathAngle(0, 0), 0), "level path = 0");
        Check(Near(PilotPolicy.FlightPathAngle(-100 * Math.Tan(5 * Math.PI / 180), 100), -5, 1e-9), "-5 deg path from vs/horizontal speed");
        var li = new LearnIn { Vs = -10, GroundSpeed = 0 }; Check(double.IsNaN(li.Fpa), "no horizontal speed -> no path angle");
        li.GroundSpeed = 100; Check(Near(li.Fpa, Math.Atan2(-10, 100) * 180 / Math.PI), "LearnIn.Fpa");
        // the dive test targets the PATH: nose 0 deg while the path is -5 counts as reached; command = target path + angle of attack
        var lf = new LearnFlight(null, null, true);
        var q = In(0, 5000); lf.Step(q); q.T = 1; lf.Step(q);   // start -> climb -> (at 5 km) wait
        Check(lf.Phase == "wait", "manual learn reaches wait (" + lf.Phase + ")");
        lf.Request(8); q.T = 2; lf.Step(q); Check(lf.Phase == "dive" && lf.ActiveTest == 8, "checking box 8 starts the dives (" + lf.Phase + ")");
        q.T = 3; q.Pitch = 0; q.GroundSpeed = 150; q.Vs = -150 * Math.Tan(5 * Math.PI / 180); lf.Step(q);
        Check(Math.Abs(lf.Pitch - 0) < .5, "dive -5 deg path with the nose at 0 (5 deg AoA): pitch command ~0, got " + lf.Pitch.ToString("0.00"));
        double t = 4; for (; t < 20 && lf.Phase == "dive"; t += .5) { q.T = t; lf.Step(q); }
        Check(lf.Phase == "recover" && lf.P.Dives.Count == 1 && lf.P.Dives[0][0] == 5, "dive measured on the flight-path angle (nose pitch alone never reached -5)");
    }

    static LearnIn In(double t, double alt) { return new LearnIn { T = t, Speed = 150, Alt = alt, Agl = alt, Vs = 0, Pitch = 3, G = 1, GLimit = 8, FuelFrac = .9, Mass = 20, StallGuess = 60, Throttle = .6, Heading = 90, GroundSpeed = 150 }; }

    static void Manual()
    {
        var lf = new LearnFlight(null, null, true);
        var q = In(0, 70); q.Landed = true; q.Speed = 0; lf.Step(q); Check(lf.WantTakeoff && lf.Phase == "takeoff", "LEARN takes off");
        q.Landed = false; q.Agl = 30; q.Alt = 100; q.Speed = 75; q.T = 1; lf.Step(q); Check(lf.Phase == "pullback", "liftoff -> power back");
        q.T = 5; lf.Step(q); Check(lf.Phase == "climb", "climb");
        q.T = 6; q.Alt = 5000; lf.Step(q); Check(lf.Phase == "wait" && lf.Waiting && lf.HoldAlt == 5000 && lf.HoldHdg == 90, "after the climb: WAIT holding 5000 m hdg 090 (" + lf.Phase + ")");
        q.T = 7; lf.Step(q); Check(double.IsNaN(lf.Pitch) && double.IsNaN(lf.Bank) && double.IsNaN(lf.Throttle) && !double.IsNaN(lf.SpeedTarget), "waiting: normal altitude/heading hold, speed PI");
        var cl = lf.ManualChecklist(0); bool allOpen = true; foreach (var l in cl) if (l.Contains("[x]") || l.Contains("[>]")) allOpen = false;
        Check(allOpen && cl.Count >= LearnFlight.Codes.Length, "every learn box starts unchecked");
        // any order: 6 first, then 3
        lf.Request(6); q.T = 8; lf.Step(q); Check(lf.Phase == "rates" && lf.ActiveTest == 6, "box 6 runs the rate checks at current speed (" + lf.Phase + ")");
        Check(lf.ManualChecklist(-1).Exists(l => l.Contains("[>] 6")), "running test shows [>]");
        Check(lf.Request(3).Contains("queued"), "box 3 while 6 runs: queued");
        Check(lf.Skip().StartsWith("Skipped step 6"), "SKIP ends the running test");
        Check(lf.Phase == "wait" && lf.Ran.Contains(6) && lf.TestsDone == 1, "after the test: back to waiting (" + lf.Phase + ")");
        q.T = 9; lf.Step(q); Check(lf.Phase == "decel" && lf.ActiveTest == 3, "queued test 3 starts from waiting");
        for (double t = 10; t < 80 && lf.ActiveTest == 3; t += 1) { q.T = t; lf.Step(q); }
        Check(lf.Phase == "wait" && lf.Ran.Contains(3) && !double.IsNaN(lf.P.Decel) && !double.IsNaN(lf.P.Accel), "test 3 (decel + accel) runs alone, then waits (" + lf.Phase + ")");
        Check(lf.ManualChecklist(-1).Exists(l => l.Contains("[x] 3") && l.Contains("done")), "done test shows [x]");
        // low after a test: climb back first (30 deg, full power)
        q.T = 100; q.Alt = 3000; lf.Request(8); lf.Step(q); lf.Skip();
        Check(lf.Phase == "climbback", "below 4.5 km after a test: climb back (" + lf.Phase + ")");
        q.T = 101; q.Speed = 150; lf.Step(q); Check(Near(lf.Throttle, 1) && lf.Pitch > 25 && lf.Pitch <= 30, "climb-back ~30 deg at full power (" + lf.Pitch.ToString("0.0") + ")");
        q.T = 102; q.Speed = 80; lf.Step(q); Check(lf.Pitch < 10, "climb-back pitch lowered near 1.3x stall (" + lf.Pitch.ToString("0.0") + " at 80 m/s, stall 60)");
        q.T = 103; q.Alt = 4950; lf.Step(q); Check(lf.Phase == "wait", "back at 5 km: waiting");
        Check(lf.Request(1).Contains("unchecked") && lf.Request(12).Contains("landing") && lf.Done && !lf.Aborted, "box 12 ends the flight (land)");
        Check(Near(PilotPolicy.ClimbBackPitch(120, 60), 30) && PilotPolicy.ClimbBackPitch(78, 60) == 0 && PilotPolicy.ClimbBackPitch(85, 60) < PilotPolicy.ClimbBackPitch(90, 60), "ClimbBackPitch: 30 deg with margin, 0 at 1.3x stall");
        // queue from the pre-flight checklist (boxes checked before LEARN)
        var pf = new PlaneProfile(); LearnFlight.Toggle(pf, 5); var lq = new LearnFlight(pf, pf.Rerun, true);
        Check(lq.Queue.Count == 1 && lq.Queue[0] == 5 && lq.P.Rerun.Count == 0, "boxes checked before LEARN queue those tests");
        Check(LearnFlight.ProfileChecklist(new PlaneProfile(), 0).TrueForAll(l => !l.Contains("[x]")), "pre-flight checklist: all unchecked");
    }

    static void Live()
    {
        var ls = new LiveStats(); double fuel = 1000;
        for (int k = 0; k <= 80; k++) { ls.Update(k * .25, fuel, 100 + k * .25 * 2, -k * .25 * .5); fuel -= .5; }   // 2 u/s, +2 m/s2 horizontal, -0.5 m/s2 vertical
        Check(Math.Abs(ls.Burn - 2) < .05 && Math.Abs(ls.TimeToEmpty - fuel / 2) < 20, "live burn 2 u/s, time to empty ~" + ls.TimeToEmpty.ToString("0") + " s");
        Check(Math.Abs(ls.HAccel - 2) < .05 && Math.Abs(ls.VAccel + .5) < .05, "live horizontal / vertical acceleration");
        Check(LiveStats.Duration(3725) == "1h 02m" && LiveStats.Duration(725) == "12m 05s" && LiveStats.Duration(double.NaN) == "-", "duration format");
        Check(MfdNav.AboutLabel("Aeris 3A") == "ABOUT AERIS 3A" && MfdNav.AboutLabel(null) == "ABOUT PLANE" && MfdNav.AboutLabel("A Very Long Vessel Name Indeed").Length <= 6 + MfdNav.AboutNameMax, "ABOUT <vessel name> label");
        var it = new MfdNav.Item("about", "ABOUT PLANE"); var old = MfdNav.VesselName; MfdNav.VesselName = () => "Learjet"; Check(it.Label == "ABOUT LEARJET", "menu button uses the vessel name"); MfdNav.VesselName = old;
    }

    // REPORT.7.9 audit fixes (items 1-4, 6, 10-12, 14, 16, 19)
    static LearnIn T7(double t, double bank, double g, double spd = 120) { return new LearnIn { T = t, Speed = spd, Alt = 5000, Agl = 5000, Vs = 0, Pitch = 3, G = g, GLimit = 2.5, FuelFrac = .9, Mass = 20, StallGuess = 60, Throttle = .6, Bank = bank, Stress = .2, TurnRate = 3 }; }
    static LearnFlight InTurn() { var lf = new LearnFlight(); lf.Step(T7(0, 0, 1)); lf.Phase = "turn"; return lf; }
    // live 17:53 (Aeris 3A, 8.6 km): every Learn slowdown sat at 100% - the stall emergency read the TAS speed targets on IAS
    static double PipeThrottle(string phase, bool fixedPath, out LearnFlight lf, double seconds = 40)
    {
        const double rho = .26; double es = fixedPath ? 60 : 120;   // fixed: stall is sea-level EAS, Learn gets it as TAS at this density
        lf = new LearnFlight(); var q = In(0, 8600); q.Speed = 336; q.StallGuess = fixedPath ? PilotPolicy.TasStall(es, rho) : es; q.Throttle = 1; lf.Step(q); lf.Phase = phase;
        var sh = new SpeedHold(); var gt = new ThrottleGate(); gt.Step(1, .1); double v = 336, thr = 1;
        for (double t = .1; t <= seconds; t += .1)
        {
            q.T = t; q.Speed = v; q.Throttle = thr; lf.Step(q);
            double want = !double.IsNaN(lf.SpeedTarget) ? sh.Step(lf.SpeedTarget, v, .1, thr) : double.IsNaN(lf.Throttle) ? thr : lf.Throttle; string kind = "pi";
            double ias = .3 * v, eas = v * Math.Sqrt(rho / PilotPolicy.Rho0);
            if (fixedPath ? PilotPolicy.StallEmergency(eas, v, rho, es, es) : ias < 1.15 * es && ias <= 1.5 * es) { want = 1; kind = "emergency"; }   // unfixed = the old IAS-only rule
            thr = gt.Step(want, .1, kind); v += (6 * thr - .00004 * v * v) * .1;
        }
        return thr;
    }
    // Replay of live 18:16 turnprep porpoise: nose lags the command (tau 1 s, 4 deg/s), path lags the nose (tau 1.5 s, 3 deg AoA), 250 m/s.
    static double[] PorpoiseSim(Func<LearnIn, double> law, double alt0)
    {
        double alt = alt0, nose = 3, gam = 0, v = 250, dt = .05, lo = 99, hi = -99; var q = new Queue<double>();
        for (double t = 0; t < 180; t += dt)
        {
            double vs = v * Math.Sin(gam * Math.PI / 180);
            var li = new LearnIn { T = t, Speed = v, Alt = alt, Agl = alt, Vs = vs, Pitch = nose, G = 1, GLimit = 8, FuelFrac = .9, Mass = 5.5, StallGuess = 60, Throttle = .3, GroundSpeed = v * Math.Cos(gam * Math.PI / 180) };
            double cmd = law(li); q.Enqueue(cmd); double late = q.Count > 50 ? q.Dequeue() : 3;   // 2.5 s controller/actuator delay
            nose += Math.Max(-4 * dt, Math.Min(4 * dt, (late - nose) * dt / 2));
            gam += ((nose - 3) - gam) * dt / 1.5; alt += vs * dt;
            if (t > 120) { lo = Math.Min(lo, cmd); hi = Math.Max(hi, cmd); }
        }
        return new[] { hi - lo, alt };
    }
    // live 20:20:17-20:20:53: stall test entered at 318 m/s, looped to 90 deg nose-up, apex 8.6 m/s saved as "stall"
    // live 20:27: after box 12 -> ferry, TOGGLE kept answering "Step 12 is setup (always flown)" with the cursor parked on 12
    // live 21:11-21:18 Stearwing A300 (49 t, EAS stall 74 -> TAS 107 at 5 km, rho .58)
    // Luke 9:27 PM: A300 top-of-wing spoilers were deployed as flaps in 9b
    static void Surfaces()
    {
        Check(SurfaceRoles.FromTag("Flap L") == SurfRole.Flap && SurfaceRoles.FromTag("spoiler") == SurfRole.Brake && SurfaceRoles.FromTag("speedbrake") == SurfRole.Brake
            && SurfaceRoles.FromTag("slat") == SurfRole.Slat && SurfaceRoles.FromTag("") == null && SurfaceRoles.FromTag("auto") == null, "tags: flap / brake|spoiler / slat; empty or auto = untagged");
        // Classify(pitch, yaw, roll, aero, brakeAg, span01, chord, vert, defl)
        Check(SurfaceRoles.Classify(false, false, false, false, false, .4, 0, 1, -1) == SurfRole.Brake, "A300 spoiler: dedicated surface on top of the wing -> brake, not flap");
        Check(SurfaceRoles.Classify(false, false, false, false, true, .4, -1, 0, 1) == SurfRole.Brake, "surface wired to the Brakes action group -> brake");
        Check(SurfaceRoles.Classify(false, false, false, true, false, .2, 0, 0, 0) == SurfRole.Brake, "stock airbrake -> brake");
        Check(SurfaceRoles.Classify(false, false, false, false, false, .3, -1, 0, 1) == SurfRole.Flap, "inboard trailing-edge dedicated surface -> flap");
        Check(SurfaceRoles.Classify(false, false, false, false, false, .3, 1, 0, 1) == SurfRole.Unknown && !SurfaceRoles.HighLift(SurfRole.Unknown), "untagged leading edge -> unknown, left alone");
        Check(SurfaceRoles.Classify(false, false, true, false, false, .8, -1, 0, 0) == SurfRole.Aileron && SurfaceRoles.Classify(false, false, true, false, false, .3, -1, 0, 1) == SurfRole.RollFlap, "outboard roll = aileron, inboard roll-only = flaperon");
        Check(SurfaceRoles.Classify(true, false, true, false, false, .9, -1, 0, 1) == SurfRole.Pitch, "pitch surfaces never deploy");
        Check(SurfaceRoles.HighLift(SurfRole.Slat) && SurfaceRoles.HighLift(SurfRole.Flap) && !SurfaceRoles.HighLift(SurfRole.Brake), "tagged slats deploy with flaps; brakes never do");
        // 9b / 10b flaps only on station; up for climb-backs and after the test
        var lf = new LearnFlight(null, new[] { 91 }, true); var q = In(0, 5000); q.HasFlaps = true; lf.Step(q);
        Check(lf.FlapsFor("sinkmap", 5000) == 0, "no flaps outside a flaps test");
        var nf = new LearnFlight(null, new[] { 91, 101 }, true); var r = In(0, 5000); r.HasFlaps = false; nf.Step(r); nf.Phase = "wait"; for (int k = 1; k < 5; k++) { r.T = k * .1; nf.Step(r); }
        Check(nf.Queue.Count == 0 && nf.Phase == "wait" && nf.Status.Contains("no flaps"), "9b/10b skipped with a notice when the craft has no flaps (" + nf.Status + ")");
    }
    static void A300Run()
    {
        double tas = PilotPolicy.TasStall(74, .58);
        var lf = new LearnFlight(); var q = In(0, 5000); q.Mass = 49; q.StallGuess = tas; q.Rho = .58; q.Speed = 1.25 * tas; q.Pitch = 2;
        lf.Step(q); lf.Phase = "sinkdown"; double t = 0;
        // throttle cut: one 2 s dip to 119 m/s (< 1.15x = 123) like 21:12:58, then pitch-for-speed recovers to 1.25x
        for (int k = 0; k < 400; k++) { t += .1; q.T = t; q.Speed = k > 20 && k < 40 ? 119 : 1.25 * tas; q.Vs = -Math.Min(12, k * .05); lf.Step(q); if (lf.Phase != "sinkdown" && lf.Phase != "sinkmap") break; }
        Check(lf.Phase == "sinkdown" || lf.Phase == "sinkmap", "A300 sink map: a 2 s dip under 1.15x stall does not stop the map (" + lf.Phase + ", " + lf.Status + ")");
        for (int k = 0; k < 60; k++) { t += .1; q.T = t; q.Speed = 115; lf.Step(q); }
        Check(lf.Status != null && lf.Status.Contains("under 1.15x"), "sustained slow (4 s) still stops it");
        // sinkprep does not hand over while still too slow / nose far down
        var sp = new LearnFlight(); var s = In(0, 5000); s.StallGuess = tas; s.Speed = 119; s.Pitch = -17; sp.Step(s); sp.Phase = "sinkprep";
        for (double tt = .1; tt < 10; tt += .1) { s.T = tt; sp.Step(s); }
        Check(sp.Phase == "sinkprep", "sinkprep waits for 1.18-1.28x stall and a settled nose (" + sp.Phase + ")");
        // 9b / 10b queue in checklist order (profile Rerun is sorted 9,10,11,91,101)
        var qf = new LearnFlight(null, new[] { 9, 10, 11, 91, 101 }, true);
        Check(string.Join(",", qf.Queue.ConvertAll(x => x.ToString()).ToArray()) == "9,91,10,101,11", "queue order 9,9b,10,10b,11 (" + string.Join(",", qf.Queue.ConvertAll(x => x.ToString()).ToArray()) + ")");
        Check(LearnFlight.AtStallAlt(11) && LearnFlight.AtStallAlt(101) && !LearnFlight.AtStallAlt(9), "10/10b/11 fly at 4 km");
    }
    static void LearnMenu()
    {
        int last = LearnFlight.Codes.Length - 1; int j = LearnFlight.NextToggleable(last);
        Check(LearnFlight.Codes[last] == 12 && LearnFlight.Toggle(new PlaneProfile(), LearnFlight.Codes[j]), "TOGGLE on box 12 moves the cursor to a toggleable box (" + LearnFlight.Codes[j] + ")");
        Check(!LearnFlight.Toggle(new PlaneProfile(), 12) && LearnFlight.Toggle(new PlaneProfile(), LearnFlight.Codes[LearnFlight.NextToggleable(0)]), "setup rows skipped from the top too");
        MfdNav.LearnCursor = last; MfdNav.AboutOffset = 7; MfdNav.ResetLearnMenu();
        Check(MfdNav.LearnCursor == 0 && MfdNav.AboutOffset == 0, "revert/reload resets the Learn menu cursor");
    }
    static void StallZoom()
    {
        var lf = new LearnFlight(); var q = In(0, 4000); q.Speed = 318; q.StallGuess = 70; q.Rho = .6; lf.Step(q); lf.Phase = "stall"; lf.Step(q);
        double[][] log = { new[] { 318, -2.6 }, new[] { 303, -.9 }, new[] { 282, 5.4 }, new[] { 254.0, 16 }, new[] { 217, 31.6 }, new[] { 168, 52.6 }, new[] { 112, 77.3 }, new[] { 85, 89.9 }, new[] { 37, 42.6 }, new[] { 11, 41.7 } };
        double t = .1;
        foreach (var r in log) for (int k = 0; k < 20; k++) { t += .1; q.T = t; q.Speed = r[0]; q.Pitch = r[1]; lf.Step(q); }
        Check(double.IsNaN(lf.P.Stall), "zoom replay: no stall recorded (got " + lf.P.Stall + ")");
        Check(lf.Status != null && lf.Status.Contains("zoom"), "zoom replay: aborted with a notice (" + lf.Status + ")");
        // stallprep does not hand over to the stall while still fast
        var sp = new LearnFlight(); var s = In(0, 4000); s.Speed = 318; s.StallGuess = 70; sp.Step(s); sp.Phase = "stallprep";
        for (double tt = .1; tt < 30; tt += .1) { s.T = tt; sp.Step(s); }
        Check(sp.Phase == "stallprep" && Math.Abs(sp.SpeedTarget - 98) < .5, "stallprep: holds 1.4x stall target and waits while fast (" + sp.Phase + ")");
        // real stall at 4 km is stored as sea-level EAS
        var ok = new LearnFlight(); var o = In(0, 4000); o.Speed = 100; o.StallGuess = 80; o.Rho = .6; ok.Step(o); ok.Phase = "stall"; ok.Step(o);
        double v = 100; for (double tt = .1; tt < 60 && ok.Phase == "stall"; tt += .1) { o.T = tt; v = Math.Max(85, v - .3); o.Speed = v; o.Pitch = 8; if (tt > 33) { o.Pitch = -15; o.Vs = -30; } ok.Step(o); }
        Check(Math.Abs(ok.P.Stall - 85 * Math.Sqrt(.6 / 1.225)) < .5, "measured stall stored as EAS (" + ok.P.Stall.ToString("0.0") + ")");
    }
    static void GraphSeries()
    {
        var c = new PerfCurve("climb", "alt_km", "m/s", "none");
        for (int k = 0; k < 100; k++) c.Add(100 - k * .5, 5, k * .05);   // 0..4.95 km, raw samples every 50 m
        var b = c.GraphSeries(); Check(b.Count == 1 && b[0].Value.Count == 10, "climb graph averaged into 0.5 km bins (" + b[0].Value.Count + ")");
        Check(Math.Abs(b[0].Value[0][1] - (100 - 4.5 * .5)) < 1e-6, "bin y = mean of its samples");
        var t = new PerfCurve("turn_g", "bank,spd", "g", "none");
        foreach (double v in new[] { 300.0, 250, 90 }) foreach (double bank in new[] { 10.0, 20, 30 }) t.Add(1 / Math.Cos(bank * Math.PI / 180), 5, bank, v + bank * .1);
        var p = t.GraphSeries();
        Check(p.Count == 3 && p[0].Value.Count == 3 && Math.Abs(p[0].Key - 302) < .5 && Math.Abs(p[2].Key - 92) < .5, "turn graph: one line per speed pass, labelled with measured speed");
    }
    static void HighLowTurns()
    {
        var lf = new LearnFlight(); lf.P.Perf.CruiseSpeed = 250; lf.P.Perf.MaxLevel = 400;
        var t = lf.BuildTurnTargets(385, 60);
        bool desc = true; for (int k = 1; k < t.Count; k++) desc &= t[k] < t[k - 1];
        Check(t.Count >= 3 && t.Count <= LearnFlight.MaxTurnPasses && desc && Math.Abs(t[t.Count - 1] - 90) < 1 && t[0] <= 385, "high->low: descending, finite, ends at 1.5x stall (" + string.Join("/", t.ConvertAll(x => x.ToString("0")).ToArray()) + ")");
        var none = new LearnFlight().BuildTurnTargets(200, double.NaN);
        Check(none.Count >= 1 && none.TrueForAll(x => !double.IsNaN(x) && x > 0), "no learned data -> fallback targets, no NaN");
        var capped = new LearnFlight(); capped.P.Perf.CruiseSpeed = 250; capped.P.Perf.MaxLevel = 400;
        Check(capped.BuildTurnTargets(420, 60)[0] <= 380.1, "first pass soft-capped at 0.95x learned Vmax");
    }
    static void RatesAtCurrentSpeed()
    {
        var lf = new LearnFlight(); var q = In(0, 5000); q.Speed = 310; lf.Step(q); lf.Phase = "vmax";
        for (double t = 1; t < 25 && lf.Phase == "vmax"; t += .5) { q.T = t; q.Speed = 310 + Math.Min(2, t * .05); lf.Step(q); }
        Check(lf.Phase == "rates" && Math.Abs(lf.SpeedTarget - q.Speed) < 5, "vmax -> rates at current speed, no ratesprep bleed (" + lf.Phase + " @" + lf.SpeedTarget.ToString("0") + ")");
        bool cut = false; Check(LearnFlight.RatesElevator(.2, .1, 5.5, 9, ref cut) == 0 && cut, "G-limited pitch pull still cuts above 0.6x limit");
    }
    static void TurnHoldDamp()
    {
        // Old: Level + TurnPitch double-counted AoA (climbs); new TurnPitchCmd uses TurnPitch as AoA floor only.
        var old = PorpoiseSim(i => FlightPolicy.Clamp(LearnFlight.Level(i, LearnFlight.ClimbAlt) + ApproachProfile.TurnPitch(45), -10, 20), 5300);
        var neu = PorpoiseSim(i => LearnFlight.TurnPitchCmd(i, 45, LearnFlight.ClimbAlt), 5300);
        Check(old[1] > neu[1] + 50, "replay: Level+TurnPitch add climbs more than TurnPitchCmd (old " + old[1].ToString("0") + " vs " + neu[1].ToString("0") + ")");
        foreach (double a0 in new[] { 5300.0, 5000.0, 5600.0 })
        {
            var r = PorpoiseSim(i => LearnFlight.TurnPitchCmd(i, 45, LearnFlight.ClimbAlt), a0);
            Check(r[0] < 2.5, "turn altitude hold damped from " + a0 + " m (cmd swing " + r[0].ToString("0.00") + ")");
            Check(Math.Abs(r[1] - LearnFlight.ClimbAlt) < 250, "turn hold stays near 5 km from " + a0 + " (settled " + r[1].ToString("0") + ")");
        }
        Check(ApproachProfile.TurnPitch(60) > 2, "TurnPitch back-pressure still applied at 60 deg bank");
        var lf = new LearnFlight(); var q = In(0, 5200); q.Speed = 250; q.GroundSpeed = 250; lf.Step(q); lf.Phase = "turn"; lf.P.Perf.CruiseSpeed = 250;
        q.T = 1; q.Bank = 45; q.Pitch = 5; q.Vs = 0; lf.Step(q);
        Check(!double.IsNaN(lf.Pitch) && lf.Pitch < 12, "turn commands pitch toward ClimbAlt hold, not a raw Level+TurnPitch stack (" + lf.Pitch.ToString("0.0") + ")");
    }
    static void StallPrepDescend()
    {
        var lf = new LearnFlight(); lf.P.Dives.Add(new[] { 15.0, 28, 10, 5.5 }); // learned ~28 m/s sink
        Check(Math.Abs(lf.DiveSinkAt(10000) - 28) < .1, "stallprep uses learned dive sink above 4.5 km");
        Check(Math.Abs(new LearnFlight().DiveSinkAt(10000) - LearnFlight.SinkMaxHigh) < .1, "no dives -> SinkMaxAt fallback");
        double altOld = 10000, altNew = 10000, v = 315, nose = 0, gam = 0, dt = .2;
        for (double t = 0; t < 120; t += dt)
        {
            var o = new LearnIn { T = t, Speed = v, Alt = altOld, Vs = v * Math.Sin(gam * Math.PI / 180), Pitch = nose, GroundSpeed = Math.Max(30, v * Math.Cos(gam * Math.PI / 180)), G = 1, GLimit = 8, FuelFrac = .8, Mass = 5.5, StallGuess = 60 };
            double cmdO = LearnFlight.Level(o, LearnFlight.StallAlt);
            nose += Math.Max(-4 * dt, Math.Min(4 * dt, (cmdO - nose) * dt / 2));
            gam += ((nose - 2) - gam) * dt / 1.5; altOld += v * Math.Sin(gam * Math.PI / 180) * dt;
        }
        nose = 0; gam = 0; var dive = new LearnFlight(); dive.P.Dives.Add(new[] { 15.0, 28, 10, 5.5 });
        for (double t = 0; t < 300; t += dt)
        {
            var n = new LearnIn { T = t, Speed = v, Alt = altNew, Vs = v * Math.Sin(gam * Math.PI / 180), Pitch = nose, GroundSpeed = Math.Max(30, v * Math.Cos(gam * Math.PI / 180)), G = 1, GLimit = 8, FuelFrac = .8, Mass = 5.5, StallGuess = 60 };
            double cmd = LearnFlight.DescentPitch(n, LearnFlight.StallAlt, dive.DiveSinkAt(altNew));
            nose += Math.Max(-4 * dt, Math.Min(4 * dt, (cmd - nose) * dt / 2));
            gam += ((nose - 2) - gam) * dt / 1.5; altNew += v * Math.Sin(gam * Math.PI / 180) * dt;
            if (Math.Abs(altNew - LearnFlight.StallAlt) < 200) break;
        }
        Check(altNew < altOld - 1500, "stallprep DescentPitch from 10 km beats Level creep (new " + altNew.ToString("0") + " vs old " + altOld.ToString("0") + ")");
        Check(altNew < LearnFlight.StallAlt + 500, "DescentPitch reaches near 4 km from 10 km (" + altNew.ToString("0") + ")");
        var high = new LearnIn { T = 1, Speed = 315, Alt = 10000, Vs = -20, Pitch = -5, GroundSpeed = 300, G = 1, GLimit = 8, FuelFrac = .8, Mass = 5.5, StallGuess = 60 };
        var highLf = new LearnFlight(); highLf.P.Dives.Add(new[] { 15.0, 28, 10, 5.5 }); highLf.Phase = "stallprep"; highLf.Step(high);
        Check(highLf.Throttle == 0 && highLf.Pitch < -2, "stallprep idles and noses down from 10 km (thr " + highLf.Throttle.ToString("0.00") + ", pitch " + highLf.Pitch.ToString("0.0") + ")");
        var near = new LearnIn { T = 1, Speed = 200, Alt = LearnFlight.StallAlt + 80, Vs = -2, Pitch = 2, GroundSpeed = 200, G = 1, GLimit = 8, FuelFrac = .8, Mass = 5.5, StallGuess = 60 };
        var hold = new LearnFlight(); hold.Phase = "stallprep"; hold.Step(near);
        Check(Math.Abs(hold.SpeedTarget - 1.4 * 60) < .5 && Math.Abs(hold.Pitch) < 8, "near 4 km: level settle on a 1.4x stall speed target, not still diving");
    }
    static void DescentToTestAlt()
    {
        var lf = new LearnFlight(); lf.P.Dives.Add(new[] { 10.0, 26, 5, 5.5 }); lf.P.Dives.Add(new[] { 20.0, 32, 8, 5.5 });
        Check(lf.DiveSinkAt(8000) >= 26 && lf.DiveSinkAt(8000) <= LearnFlight.SinkMaxHigh + .1, "descent sink from learned dives, capped by SinkMaxAt");
        var high = new LearnIn { T = 1, Speed = 280, Alt = 8000, Vs = -5, Pitch = 0, GroundSpeed = 280, G = 1, GLimit = 8, FuelFrac = .8, Mass = 5.5, StallGuess = 60 };
        lf.Phase = "turnprep"; lf.Step(high);
        Check(lf.Throttle == 0 && lf.Pitch < LearnFlight.Level(high, LearnFlight.ClimbAlt) - 1, "turnprep from 8 km: idle DescentPitch, not Level creep (pitch " + lf.Pitch.ToString("0.0") + ")");
        var sink = new LearnFlight(); sink.P.Dives.Add(new[] { 15.0, 27, 5, 5.5 }); sink.Phase = "sinkprep";
        var sIn = new LearnIn { T = 1, Speed = 200, Alt = 7500, Vs = -5, Pitch = 0, GroundSpeed = 200, G = 1, GLimit = 8, FuelFrac = .8, Mass = 5.5, StallGuess = 60 };
        sink.Step(sIn);
        Check(sink.Throttle == 0 && sink.Pitch < -1, "sinkprep from high: DescentPitch to 5 km");
        // LevelOff lead: near target, DescentPitch blends toward Level (not full dive)
        var near = new LearnIn { T = 1, Speed = 250, Alt = LearnFlight.ClimbAlt + 100, Vs = -8, Pitch = -2, GroundSpeed = 250, G = 1, GLimit = 8, FuelFrac = .8, Mass = 5.5, StallGuess = 60 };
        double diveFar = LearnFlight.DescentPitch(new LearnIn { Speed = 250, Alt = 9000, Vs = -25, Pitch = -8, GroundSpeed = 245, G = 1 }, LearnFlight.ClimbAlt, 28);
        double diveNear = LearnFlight.DescentPitch(near, LearnFlight.ClimbAlt, 28);
        Check(diveNear > diveFar, "DescentPitch LevelOff lead eases the dive near the test altitude (near " + diveNear.ToString("0.0") + " vs far " + diveFar.ToString("0.0") + ")");
        Check(Math.Abs(new LearnFlight().DiveSinkAt(9000) - LearnFlight.SinkMaxHigh) < .1, "no-profile descent fallback stays on SinkMaxAt");
    }
    static void PrepPorpoise()
    {
        var old = PorpoiseSim(i => FlightPolicy.Clamp(3 + (FlightPolicy.Clamp((5000 - i.Alt) * .05, -15, 15) - i.Vs) * .6, -10, 15), 5300);
        Check(old[0] > 4, "replay: old 0.6 deg/(m/s) VS law porpoises (cmd swing " + old[0].ToString("0.0") + ", alt " + old[1].ToString("0") + ")");
        foreach (double a0 in new[] { 5300.0, 4600.0, 5000.0 })
        {
            var r = PorpoiseSim(i => LearnFlight.Level(i, LearnFlight.ClimbAlt), a0);
            Check(r[0] < 1.5, "prep altitude hold is damped from " + a0 + " m (cmd swing " + r[0].ToString("0.00") + ")");
            Check(Math.Abs(r[1] - LearnFlight.ClimbAlt) < 60, "prep hold settles at 5 km from " + a0 + " m (" + r[1].ToString("0") + ")");
        }
        var w = PorpoiseSim(i => LearnFlight.Level(i), 5000);
        Check(w[0] < 1.5, "plain level hold (wait/ratesprep/sinkprep) is damped");
    }
    static void LevelOffCapture()
    {
        // 30 deg climb, nose follows the command at 2 deg/s: without a lead this zooms past 5 km (live: 8.6 km).
        foreach (double v in new[] { 200.0, 385.0 })
        {
            double alt = 1000, p = 30, maxAlt = 0, dt = .1;
            for (double t = 0; t < 400; t += dt)
            {
                double vs = v * Math.Sin(p * Math.PI / 180);
                var li = new LearnIn { T = t, Speed = v, Alt = alt, Agl = alt, Vs = vs, Pitch = p, G = 1, GLimit = 8, FuelFrac = .9, Mass = 5.5, StallGuess = 60, Throttle = 1, GroundSpeed = v };
                double cmd = LearnFlight.LevelOff(li, LearnFlight.ClimbAlt, PilotPolicy.ClimbBackPitch(v, 60));
                p += Math.Max(-2 * dt, Math.Min(2 * dt, cmd - p)); alt += vs * dt; maxAlt = Math.Max(maxAlt, alt);
            }
            Check(maxAlt <= LearnFlight.ClimbAlt + 150, "level-off lead caps the climb within 150 m at " + v + " m/s (peak " + maxAlt.ToString("0") + ")");
            Check(Math.Abs(alt - LearnFlight.ClimbAlt) < 150, "climb settles at 5 km at " + v + " m/s (" + alt.ToString("0") + ")");
        }
        var low = new LearnIn { Alt = 1000, Vs = 150, Speed = 300, Pitch = 30 };
        Check(LearnFlight.LevelOff(low, 5000, 30) == 30, "far below the target -> full climb pitch");
        Check(CraftClass.Classify(5.5, 503, 40) == "light", "5.5 t craft with a glitched 503 m span stays light");
        Check(CraftClass.Classify(5.5, 8, 40) == "light", "5.5 t, 8 m span -> light");
        Check(CraftClass.Classify(30, 40, 60) == "heavy", "real 40 m span -> heavy");
        Check(CraftClass.Classify(120, 503, 300) == "heavy", "120 t -> heavy regardless of span");
    }
    static void LearnThrottleStuck()
    {
        // live 20:27 (ferry after Learn, 8.3 km): 'stall emergency: ias=92 srf=368 stall=131 est=131' every frame
        Check(92 < 1.15 * 131 && 92 < 1.5 * 131, "repro 20:27: the old rule (KSP IAS 92 vs the sea-level stall, IAS-only guard) fires");
        double eas2027 = 368 * Math.Sqrt(.28 / PilotPolicy.Rho0);
        Check(!PilotPolicy.StallEmergency(eas2027, 368, .28, 131, 131), "20:27 fixed: EAS " + eas2027.ToString("0") + " vs 131 stall -> no emergency");
        Check(!PilotPolicy.StallEmergency(0, 368, .28, 131, 131) && !PilotPolicy.StallEmergency(double.NaN, 450, .28, 131, 131), "bad q -> srf/density guard, no emergency at 368-450 m/s");
        Check(!PilotPolicy.StallEmergency(80, 450, .28, 131, 131), "guard: srf > 1.5x TAS stall estimate never fires, whatever EAS says");
        Check(PilotPolicy.StallEmergency(140, 140 / Math.Sqrt(.28 / PilotPolicy.Rho0), .28, 131, 131), "genuinely slow at altitude (EAS 140 < 151) still fires");
        Check(Math.Abs(PilotPolicy.Eas(.5 * .28 * 368 * 368 / 1000, 368) - eas2027) < .5, "EAS from dynamic pressure");
        Check(Math.Abs(PilotPolicy.SafeBank(30, 195, 131, 0)) >= 10 && Math.Abs(PilotPolicy.SafeBank(30, 230, 131, 0)) == 30 && Math.Abs(PilotPolicy.SafeBank(30, 92, 131, 0)) == 5, "turns: EAS 195 turns (>= 10 deg), 230 free; old IAS 92 clamped the ferry turn to 5 deg");
        Check(!PilotPolicy.LowEnergy(false, eas2027, 131, 20, 7700), "low energy judged on EAS: not low at 368 m/s / 8.3 km");
        Check(!PilotPolicy.StallPlausible(8.6, 131, 163.6) && PilotPolicy.StallPlausible(120, 131, 163.6) && !PilotPolicy.StallPlausible(170, 131, 163.6) && PilotPolicy.StallPlausible(50, 45.7, double.NaN), "stall sanity: 8.6 junk and > liftoff rejected; 120 kept");
        LearnFlight lf; double before = PipeThrottle("wait", false, out lf);
        Check(before > .99, "repro: wait at 8.6 km with IAS-judged emergency stays at 100% (" + before.ToString("0.00") + " ph " + lf.Phase + " tgt " + lf.SpeedTarget + " thr " + lf.Throttle + ")");
        foreach (var ph in new[] { "wait" })
        {
            double thr = PipeThrottle(ph, true, out lf);
            Check(thr < .6, "Learn " + ph + " (speed target / learn throttle) can reduce throttle at altitude: " + thr.ToString("0.00") + " after 40 s ph " + lf.Phase + " tgt " + lf.SpeedTarget + " thr " + lf.Throttle);
        }
        // turnprep/sinkprep high above the test altitude now idle for DescentPitch (also cuts throttle)
        Check(PipeThrottle("turnprep", true, out lf) < .6, "turnprep high: idle descent cuts throttle");
        // ratesprep (legacy) immediately skips bleed-down and runs rates at current speed
        var lr = new LearnFlight(); var r = In(0, 5000); r.Speed = 336; r.StallGuess = 120; lr.Step(r); lr.Phase = "ratesprep";
        r.T = .5; lr.Step(r);
        Check(lr.Phase == "rates" && !double.IsNaN(lr.SpeedTarget) && Math.Abs(lr.SpeedTarget - 336) < .1, "ratesprep skips bleed-down; rates at current speed (" + lr.Phase + " @" + lr.SpeedTarget.ToString("0") + ")");
    }
    // profile fingerprint: name stays the key, print is metadata
    static CraftFingerprint Fp(double dry, double lift, int parts, string root = "mk1pod") { return new CraftFingerprint { DryMass = dry, Lift = lift, Parts = parts, Engines = 1, Root = root, Intake = true }; }
    static void Fingerprint()
    {
        var a = Fp(5, 10, 40);
        Check(CraftFingerprint.Compare(a, Fp(5.2, 10.3, 42)) == CraftFingerprint.Match.Ok, "fingerprint: small SPH edit (+4% mass, +2 parts) stays OK");
        Check(CraftFingerprint.Compare(a, Fp(5.7, 10, 40)) == CraftFingerprint.Match.Soft && CraftFingerprint.Compare(a, Fp(7, 10, 40)) == CraftFingerprint.Match.Hard && CraftFingerprint.Compare(a, Fp(5, 10, 40, "cockpitB")) == CraftFingerprint.Match.Hard, "fingerprint: 12% mass soft, 40% mass / new root hard");
        Check(CraftFingerprint.Compare(null, a) == CraftFingerprint.Match.Missing, "legacy profile without a print: Missing (still used)");
        Check(CraftFingerprint.RenameMatch(a, Fp(5.1, 10, 41)) && !CraftFingerprint.RenameMatch(a, Fp(8, 10, 40)), "rename carry offered only for a matching airframe");
        var p = new PlaneProfile(); p.Stall = 60; p.Fingerprint = a; var rt = PlaneProfile.FromDict(MiniJson.Deserialize(MiniJson.Serialize(p.ToDict())) as Dictionary<string, object>);
        Check(rt.Fingerprint != null && rt.Fingerprint.Parts == 40 && Math.Abs(rt.Fingerprint.DryMass - 5) < 1e-9 && rt.Fingerprint.Root == "mk1pod" && rt.Fingerprint.Intake, "fingerprint round-trips in the profile JSON");
        var legacy = new PlaneProfile(); legacy.Stall = 70; var d = legacy.ToDict(); var lg = PlaneProfile.FromDict(MiniJson.Deserialize(MiniJson.Serialize(d)) as Dictionary<string, object>);
        Check(!d.ContainsKey("fingerprint") && lg != null && lg.Fingerprint == null && lg.Stall == 70, "legacy profile (no fingerprint) still loads under its name");
    }
    // AUTOPILOT_REVIEW.md policy pieces (#4/#11 stall schedule, #7/#10 roll step)
    static void ApReview()
    {
        bool was = ApproachProfile.TrustedStall;
        ApproachProfile.TrustedStall = false;
        Check(ApproachProfile.SaneStall(25) == 40 && ApproachProfile.SaneStall(150) == 120 && ApproachProfile.Speed(25) == ApproachProfile.MinSpeed && ApproachProfile.HiSpeed(60) == 138, "no profile: approach schedule fallbacks unchanged (40-120 stall, 80 join floor, 150 cap)");
        ApproachProfile.TrustedStall = true;
        Check(ApproachProfile.SaneStall(25) == 25 && ApproachProfile.SaneStall(150) == 150 && Math.Abs(ApproachProfile.Speed(30) - 45) < 1e-9 && Math.Abs(ApproachProfile.AppSpeed(30) - 40.5) < 1e-9, "LEARN stall: light craft keep real Vs (join 45, final 40.5)");
        ApproachProfile.TrustedStall = was;
        Check(CraftClass.RollStep("light", 0, 40, .1) == 40 && Math.Abs(CraftClass.RollStep("heavy", 0, 40, .1) - CraftClass.HeavyRollRate * .1) < 1e-9, "no learned roll rate: light snaps, heavy uses HeavyRollRate");
        Check(Math.Abs(CraftClass.RollStep("heavy", 0, 40, .1, 20) - 2) < 1e-9 && Math.Abs(CraftClass.RollStep("light", 0, 40, .1, 30) - 3) < 1e-9, "learned roll rate drives the bank step for every class");
    }

    // live 16:59 (Aeris 3A): climb pitch 6 at 385 m/s; boxes would not uncheck
    static void Oct11Live()
    {
        {   // Learn climb: fast plane noses up to ~30 deg (was BestClimbPitch floor 6, inverted: faster -> lower)
            var lf = new LearnFlight(); var q = In(0, 1000); q.Speed = 385; q.StallGuess = 98; lf.Step(q); lf.Phase = "climb"; q.T = 1; lf.Step(q);
            Check(Math.Abs(lf.Pitch - PilotPolicy.ClimbBackPitch(385, 98)) < 1e-9 && lf.Pitch >= 25, "learn climb at 385 m/s uses ClimbBackPitch (" + lf.Pitch.ToString("0.0") + " deg, was 6)");
            q.Speed = 1.35 * 98; q.T = 2; lf.Step(q); Check(lf.Pitch < 10, "learn climb eases pitch near 1.3x stall (" + lf.Pitch.ToString("0.0") + ")");
        }
        {   // every box toggles in the manual picker
            var lf = new LearnFlight(null, null, true); var q = In(0, 1000); lf.Step(q); q.T = 1; lf.Step(q);
            Check(lf.Phase == "climb", "manual learn climbing (" + lf.Phase + ")");
            string r = lf.Request(2); q.T = 2; lf.Step(q);
            Check(lf.Phase == "wait" && Math.Abs(lf.HoldAlt - 1000) < 1 && r.Contains("Skipped"), "unchecking the running climb = skip, waits at the current altitude (" + lf.Phase + ", " + r + ")");
            lf.Request(5); lf.Request(6); Check(lf.Queue.Count == 2, "two tests queued");
            q.T = 3; lf.Step(q); Check(lf.ActiveTest == 5 && lf.Queue.Count == 1, "test 5 running, 6 queued");
            string u6 = lf.Request(6); Check(lf.Queue.Count == 0 && u6.Contains("removed"), "unchecking a queued test removes it");
            string u5 = lf.Request(5); q.T = 4; lf.Step(q); Check(lf.ActiveTest == 0 && (lf.Phase == "wait" || lf.Phase == "climbback") && u5.Contains("Skipped"), "unchecking the running test acts like SKIP (" + lf.Phase + ", " + u5 + ")");
            string d5 = lf.Request(5); Check(!lf.Ran.Contains(5) && lf.Queue.Count == 0 && d5.Contains("unchecked"), "unchecking a done test clears its mark (" + d5 + ")");
            string c1 = lf.Request(1); var cl = lf.ManualChecklist(-1); Check(c1.Contains("unchecked") && cl[0].Contains("[ ] 1"), "takeoff box toggles too (" + cl[0] + ")");
        }
    }

    // REPORT.7.9 (rewrite) items 1-17
    static LearnIn S9(double t, double alt, double vs, double spd = 76.8) { return new LearnIn { T = t, Speed = spd, Alt = alt, Agl = alt, FuelFrac = .9, GLimit = 8, G = 1, StallGuess = 64, Mass = 30, Pitch = 5, Vs = vs, Throttle = .3 }; }
    static void Audit2()
    {
        {   // #1 / #15: phase flags NFC uses to skip the LE bank clamp (turns) and LE + MinSafeSpeed (sink map)
            var lf = InTurn(); bool t1 = lf.TurnPhase; lf.Phase = "turnprep"; bool t2 = lf.TurnPhase; lf.Phase = "recover"; bool t3 = lf.TurnPhase; lf.Phase = "dive"; bool t4 = lf.TurnPhase;
            Check(t1 && t2 && t3 && !t4 && !lf.SinkPhase, "#1: TurnPhase true for turn/turnprep/turn-recover only (turn-only flag, not learnPull)");
            lf.Phase = "sinkmap"; bool s1 = lf.SinkPhase; lf.Phase = "sinkclimb"; bool s2 = lf.SinkPhase; lf.Phase = "stall"; Check(s1 && s2 && !lf.SinkPhase && !lf.TurnPhase, "#15: SinkPhase true for sink* only");
        }
        {   // #5 CapHot: G term ignored in the Learn turn step, heat still counts
            Check(!LearnGuards.CapHot(.5, 2.3, 2.5, true) && LearnGuards.CapHot(.5, 2.3, 2.5, false) && LearnGuards.CapHot(.9, 1, 2.5, true), "#5: CapHot ignores G during Learn turns, keeps heat");
        }
        {   // #17 rails warp notice once per learn
            bool said = false; bool a = LearnGuards.RailsWarpNotice(true, true, 2, ref said), b = LearnGuards.RailsWarpNotice(true, true, 3, ref said), c = LearnGuards.RailsWarpNotice(true, false, 2, ref said);
            bool d = LearnGuards.RailsWarpNotice(false, true, 2, ref said), e = LearnGuards.RailsWarpNotice(true, true, 2, ref said);
            Check(a && !b && !c && !d && e && !LearnGuards.RailsWarpNotice(true, true, 0, ref said), "#17: one notice per Learn while on rails warp; physics warp silent");
        }
        {   // #3: step 7 starts fresh (manual StartTest(7) and the rates -> turnprep entry)
            var lf = new LearnFlight(null, null, true); var q = In(0, 5000); lf.Step(q); q.T = 1; lf.Step(q);
            lf.P.Turns.Add(new double[] { 10, 1, 1, 0, .1, 20, .5, 100, 1000, 2, 1, 100 }); lf.P.Envelope.Add(new double[] { 100, 10, 1, 1, 1000, 2, 20 });
            lf.Request(7); q.T = 2; lf.Step(q);
            Check(lf.Phase == "turnprep" && lf.P.Turns.Count == 0 && lf.P.Envelope.Count == 0, "#3: StartTest(7) clears Turns / Envelope (" + lf.Phase + ")");
            var la = new LearnFlight(); la.Step(T7(0, 0, 1)); la.P.Turns.Add(new double[] { 10, 1, 1, 0, .1, 20, .5, 100, 1000, 2, 1, 100 }); la.P.Envelope.Add(new double[] { 100, 10, 1, 1, 1000, 2, 20 });
            la.Phase = "rates"; la.Step(T7(11, 0, 1)); la.Step(T7(11.1, 0, 1));
            // high→low: already at entry speed → turnprep can complete in one tick
            Check((la.Phase == "turnprep" || la.Phase == "turn") && la.P.Turns.Count == 0 && la.P.Envelope.Count == 0, "#3: rates -> turnprep entry clears Turns / Envelope (" + la.Phase + ")");
        }
        {   // #2: nose error at the FIRST bank (no earlier success) does not invalidate the step; bank completes on altitude/time
            var lf = InTurn(); double t = 0;
            for (int k = 0; k < 400 && lf.Phase == "turn" && lf.P.Turns.Count == 0; k++) { t += .1; double b = double.IsNaN(lf.Bank) ? 0 : lf.Bank; var q = T7(t, b, 1.02); q.Pitch = -12; lf.Step(q); }
            Check(!lf.Redo.Contains(7) && lf.P.Turns.Count == 1 && !lf.Status.Contains("invalid"), "#2: first-bank nose error alone does not wipe step 7 (" + lf.Status + ")");
            // nose error during roll-in (bank not reached) is ignored
            var lr = InTurn(); t = 0; for (int k = 0; k < 150; k++) { t += .1; var q = T7(t, 0, 1); q.Pitch = -12; lr.Step(q); }
            Check(!lr.Redo.Contains(7) && lr.P.Envelope.Count == 0 && lr.Phase == "turn", "#2: nose error before reachAt is not counted");
        }
        {   // #4: bank never reached in 20 s -> pass ends, no half-rolled turn row averaged
            var lf = InTurn(); double t = 0; for (int k = 0; k < 260 && lf.Phase == "turn"; k++) { t += .1; lf.Step(T7(t, 3, 1)); }
            Check(lf.Phase == "turnprep" && lf.P.Turns.Count == 0 && lf.Status.Contains("not reached"), "#4: bank not reached in 20 s ends the pass without sampling (" + lf.Phase + ", " + lf.Status + ")");
        }
        {   // #6: envelope speed = measured mean, not the commanded pass speed
            var lf = InTurn(); lf.Step(T7(.05, 0, 1, 150)); double t = .05;   // lock pass target from 150, then fly banks at 110
            for (int k = 0; k < 2000 && lf.Phase == "turn"; k++) { t += .1; double b = double.IsNaN(lf.Bank) ? 0 : lf.Bank; lf.Step(T7(t, b, b >= 20 ? 2.2 : 1.02, 110)); }
            Check(lf.P.Envelope.Count >= 1 && Math.Abs(lf.P.Envelope[0][0] - 110) < .5 && lf.P.Turns[0][7] != 110, "#6: envelope tagged at measured 110 m/s, not pass target " + (lf.P.Turns.Count > 0 ? lf.P.Turns[0][7].ToString("0") : "-"));
        }
        {   // #7 / #8 labels
            var lf = InTurn(); lf.Phase = "turnprep"; lf.Step(T7(1, 0, 1)); string cl = string.Join("\n", lf.Checklist());
            Check(LearnFlight.StepNames[6] == "Max safe turn / envelope", "#7: step 7 label");
            Check(!cl.Contains("pass 2: slowing") && (cl.Contains("pass ") || cl.Contains("settle")), "#8: turnprep progress names the pass (" + cl.Replace("\n", " | ") + ")");
        }
        {   // #11: limThr cleared when the map resumes after sinkclimb
            var lf = new LearnFlight(); var q = S9(0, 5000, -12); lf.Step(q); lf.Phase = "sinkprep"; q.T = .1; lf.Step(q); q.T = .2; lf.Step(q);
            double t = .2; for (int s = 0; s < 40 && lf.Phase == "sinkdown"; s++) { t += .1; q.T = t; lf.Step(q); }
            q.Alt = 4050; q.Vs = -25; for (int s = 0; s < 100; s++) { t += .1; q.T = t; lf.Step(q); }   // deep sink near 4 km winds limThr up
            double wound = lf.Throttle; q.Alt = 3990; t += .1; q.T = t; lf.Step(q); bool climb = lf.Phase == "sinkclimb";
            q.Alt = 4950; q.Vs = -3; t += .1; q.T = t; lf.Step(q); t += .1; q.T = t; lf.Step(q);
            Check(climb && lf.Phase == "sinkmap" && wound > .2 && lf.Throttle < .1, "#11: post-climb map starts without the old limThr floor (" + wound.ToString("0.00") + " -> " + lf.Throttle.ToString("0.00") + ")");
        }
        {   // #12: one speed factor (1.25) for prep exit and the map
            Check(LearnFlight.SinkSpeedFactor == 1.25, "#12: SinkSpeedFactor 1.25");
            var a = new LearnFlight(); var q = S9(0, 5000, 0, 83); a.Step(q); a.Phase = "sinkprep"; q.T = .1; a.Step(q); bool stay = a.Phase == "sinkprep";
            var b = new LearnFlight(); var r = S9(0, 5000, 0, 81); b.Step(r); b.Phase = "sinkprep"; r.T = .1; b.Step(r);
            Check(stay && b.Phase == "sinkdown", "#12: prep exits at ~1.25x stall (81 yes, 83 = 1.3x no)");
        }
        {   // #13: lag recorded only when measured in THAT step
            var lf = new LearnFlight(); var q = S9(0, 5000, -12); lf.Step(q); lf.Phase = "sinkprep"; q.T = .1; lf.Step(q); q.T = .2; lf.Step(q);
            double t = .2; for (int s = 0; s < 40 && lf.Phase == "sinkdown"; s++) { t += .1; q.T = t; lf.Step(q); }
            q.Vs = -8.1; for (int s = 0; s < 150; s++) { t += .1; q.T = t; lf.Step(q); }
            int lag1 = lf.P.Curves.ThrottleLag.Points.Count, rows1 = lf.P.Sink.Count;
            q.Vs = -7.95; for (int s = 0; s < 80 && lf.P.Sink.Count == rows1; s++) { t += .1; q.T = t; lf.Step(q); }
            Check(rows1 == 1 && lf.P.Sink.Count == 2 && lf.P.Sink[1][2] == LearnFlight.RampWait && lf.P.Curves.ThrottleLag.Points.Count == lag1,
                "#13: unmeasured step stores RampWait in the table and no ThrottleLag point (rows " + lf.P.Sink.Count + ", lag pts " + lag1 + "->" + lf.P.Curves.ThrottleLag.Points.Count + ")");
        }
        {   // #14 B: climb-back G uses the spike threshold; mapping keeps 0.9x
            var a = new LearnFlight(); var q = S9(0, 4500, 5); q.GLimit = 2.5; a.Step(q); a.Phase = "sinkclimb"; q.T = .1; q.G = 2.3; a.Step(q);
            var b = new LearnFlight(); var r = S9(0, 4000, 5); r.GLimit = 2.5; b.Step(r); b.Phase = "sinkprep"; r.T = .1; r.G = 2.3; b.Step(r);
            var c = new LearnFlight(); var u = S9(0, 4500, -5); u.GLimit = 2.5; c.Step(u); c.Phase = "sinkmap"; u.T = .1; u.G = 2.3; c.Step(u);
            var d = new LearnFlight(); var w = S9(0, 4500, 5); w.GLimit = 2.5; d.Step(w); d.Phase = "sinkclimb"; w.T = .1; w.G = 2.6; d.Step(w);
            Check(!a.Aborted && !b.Aborted && c.Aborted && d.Aborted, "#14: 0.92x g on sink climb-back no abort; map still aborts at 0.9x; real spike aborts");
        }
    }

    static void Audit()
    {
        // 1) G between 0.8x and 0.9x+ in a turn: the turn stop saves passBest, no global abort
        {
            var lf = InTurn(); double t = 0; bool spiked = false;
            for (int k = 0; k < 2000 && lf.Phase == "turn"; k++) { t += .1; double b = double.IsNaN(lf.Bank) ? 0 : lf.Bank; double g = b >= 20 ? 2.375 : 1.02; spiked |= g > .9 * 2.5; lf.Step(T7(t, b, g)); }
            Check(spiked && !lf.Aborted && lf.P.Envelope.Count == 1 && lf.P.Envelope[0][1] == 10, "item 1: 0.95x g at 20 deg stops the ladder and saves 10 deg, no abort (" + lf.Phase + ", aborted " + lf.Aborted + ", env " + lf.P.Envelope.Count + ")");
            var lf2 = InTurn(); lf2.Step(T7(1, 10, 2.6)); Check(lf2.Aborted, "item 1: a real spike above the structural limit still aborts in a turn");
            var lf3 = new LearnFlight(); lf3.Step(T7(0, 0, 1)); lf3.Phase = "cruise"; lf3.Step(T7(1, 0, 2.375)); Check(lf3.Aborted, "item 1: outside the turn steps the 0.9x abort is unchanged");
        }
        // 2) turn pitch has load-factor back-pressure; throttle authority = SpeedTarget only; badThr ignores a lever still ramping up
        {
            var q = T7(0, 60, 2); double lvl = LearnFlight.TurnPitchCmd(q, 0, 5000), p60 = LearnFlight.TurnPitchCmd(q, 60, 5000);
            Check(Math.Abs(p60 - lvl - ApproachProfile.TurnPitch(60)) < 1e-9 && p60 > lvl + 3, "item 2: 60 deg bank adds TurnPitch back-pressure (" + lvl.ToString("0.0") + " -> " + p60.ToString("0.0") + ")");
            var lf = InTurn(); lf.Step(T7(1, 0, 1)); Check(double.IsNaN(lf.Throttle) && !double.IsNaN(lf.SpeedTarget), "items 3/19: turn sets SpeedTarget only (no dead learn throttle)");
            lf.Phase = "turnprep"; lf.Step(T7(2, 0, 1, 150)); Check(double.IsNaN(lf.Throttle) && !double.IsNaN(lf.SpeedTarget), "items 3/19: turnprep sets SpeedTarget only (NativeLearn ignores learn Throttle when SpeedTarget is set)");
            // lever ramping 10% -> 30% at the 4%/s gate while 30 m/s slow and still decelerating: the PI is working, not a fault
            var lr = InTurn(); double t = 1, thr = .1, v = 150; lr.Step(T7(t, 0, 1, v)); double vT = lr.SpeedTarget;
            for (int k = 0; k < 60; k++) { t += .1; thr = Math.Min(.29, thr + .004); v -= .05; var qi = T7(t, 0, 1, vT - 30 + (v - 150)); qi.Throttle = thr; lr.Step(qi); }
            Check(!lr.Redo.Contains(7) && !lr.Status.Contains("not tracking"), "item 3: spool / gate ramp-up does not invalidate the turn (" + lr.Status + ")");
            var ls = InTurn(); t = 1; ls.Step(T7(t, 0, 1)); vT = ls.SpeedTarget; double sp = vT - 30;
            for (int k = 0; k < 80; k++) { t += .1; sp += .05; var qi = T7(t, 0, 1, sp); qi.Throttle = .2; ls.Step(qi); }
            Check(!ls.Redo.Contains(7), "item 3: speed already recovering -> no throttle fault");
        }
        // 3) one-frame G spike does not count as the limit (filtered)
        {
            var lf = InTurn(); double t = 0;
            for (int k = 0; k < 40; k++) { t += .1; double b = double.IsNaN(lf.Bank) ? 0 : lf.Bank; lf.Step(T7(t, b, k == 30 ? 2.2 : 1.02)); }
            Check(lf.Phase == "turn" && lf.P.Turns.Count == 0 && lf.LoadG < 1.5, "item 4: single-frame 2.2 g (0.88x) does not stop the bank ladder (filtered g " + lf.LoadG.ToString("0.00") + ")");
        }
        // 4) envelope NaN -> null, read back as NaN
        {
            var p = new PlaneProfile(); p.Envelope.Add(new[] { 120, 40, 1.3, 3.5, double.NaN, double.NaN, 20 });
            string js = MiniJson.Serialize(p.ToDict()); var rt = PlaneProfile.FromDict(MiniJson.Deserialize(js) as Dictionary<string, object>);
            Check(js.Contains("null") && double.IsNaN(rt.Envelope[0][4]) && double.IsNaN(rt.Envelope[0][5]) && rt.Envelope[0][1] == 40, "item 6: missing radius / roll-in saved as null, not 0");
        }
        // 5) sink map: one target per settled throttle step; vsF reset on entry; SinkLimit on vsF
        {
            var lf = new LearnFlight(); var q = new LearnIn { T = 0, Speed = 76.8, Alt = 5000, FuelFrac = .9, GLimit = 8, G = 1, StallGuess = 64, Mass = 30, Pitch = 5, Vs = -12 };
            lf.Step(q); lf.Phase = "sinkprep"; q.T = .1; lf.Step(q); q.T = .2; lf.Step(q);
            Check(lf.Phase == "sinkdown" && Math.Abs(lf.SinkFiltered + 12) < .5, "item 12: vsF reset to the current VS on sinkdown entry (" + lf.SinkFiltered.ToString("0.0") + ")");
            for (int s = 3; s < 80 && lf.Phase == "sinkdown"; s++) { q.T = s * .1; lf.Step(q); }
            Check(lf.Phase == "sinkmap" && Math.Abs(lf.SinkFiltered + 12) < .5, "item 12: sinkmap entry with a fresh filter");
            double t = q.T; for (int s = 0; s < 40; s++) { t += .1; q.T = t; q.Vs = -1; lf.Step(q); }   // sink collapses within one throttle step
            Check(lf.P.Sink.Count == 1, "item 10: VS jump through several targets records ONE point per throttle step (" + lf.P.Sink.Count + ")");
            for (int s = 0; s < 60; s++) { t += .1; q.T = t; lf.Step(q); }
            Check(lf.P.Sink.Count == 2 && lf.P.Sink[0][1] != lf.P.Sink[1][1], "item 10: next target only on the next step, different throttle");
            // filtered floor: a one-frame -40 m/s spike at 4.1 km doesn't kick the throttle
            var lm = new LearnFlight(); var r = q; r.T = 0; r.Vs = -12; r.Alt = 4100; lm.Step(r); lm.Phase = "sinkmap"; r.T = .1; lm.Step(r); double th0 = lm.Throttle;
            r.T = .2; r.Vs = -40; lm.Step(r); r.T = .3; r.Vs = -12; lm.Step(r);
            Check(Math.Abs(lm.Throttle - th0) < .01, "item 11: SinkLimit uses the filtered VS (one-frame spike ignored, thr " + th0.ToString("0.000") + " -> " + lm.Throttle.ToString("0.000") + ")");
        }
        // 6) altitude-scheduled sink floor; no floor throttle in sinkdown
        {
            Check(LearnFlight.SinkMaxAt(5000) == 28 && LearnFlight.SinkMaxAt(4000) == 15 && Math.Abs(LearnFlight.SinkMaxAt(4250) - 21.5) < 1e-9, "item 16: 28 m/s allowed above 4.5 km, 15 at 4 km");
            var ld = new LearnFlight(); var q = new LearnIn { T = 0, Speed = 76.8, Alt = 4600, FuelFrac = .9, GLimit = 8, G = 1, StallGuess = 64, Mass = 30, Pitch = 5, Vs = -25 };
            ld.Step(q); ld.Phase = "sinkdown"; double mx = 0; for (int s = 1; s < 25; s++) { q.T = s * .1; ld.Step(q); if (ld.Phase == "sinkdown") mx = Math.Max(mx, ld.Throttle); }
            Check(mx == 0, "item 14: sinkdown stays at idle even at 25 m/s sink (no soft floor)");
            var hi = new LearnFlight(); var h = q; h.T = 0; h.Alt = 5000; hi.Step(h); hi.Phase = "sinkmap"; for (int s = 1; s < 40; s++) { h.T = s * .1; hi.Step(h); }
            var lo = new LearnFlight(); var l = q; l.T = 0; l.Alt = 4050; lo.Step(l); lo.Phase = "sinkmap"; for (int s = 1; s < 40; s++) { l.T = s * .1; lo.Step(l); }
            Check(hi.Throttle < .01 && lo.Throttle > .05, "item 16: 25 m/s sink tolerated at 5 km, floor adds power near 4 km (" + hi.Throttle.ToString("0.00") + " / " + lo.Throttle.ToString("0.00") + ")");
        }
    }
}
