using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>One learned profile per named craft (Luke 7:06 PM "LEARN THIS PLANE"). Values are at the test mass M0 (t);
    /// at another mass speeds scale by sqrt(m/M0) and accelerations by M0/m (thrust/drag-limited).</summary>
    internal sealed class PlaneProfile
    {
        internal double M0 = double.NaN, Liftoff = double.NaN, Stall = double.NaN, StallAltLoss = double.NaN, ClimbAccel = double.NaN, ClimbVs = double.NaN,
            Decel = double.NaN, Accel = double.NaN, Lag = double.NaN, EconSpeed = double.NaN, EconThrottle = double.NaN, FuelUsed = double.NaN;
        internal bool Complete; internal string Abort = "", Date = "";
        internal readonly List<double[]> Turns = new List<double[]>();   // bank, g, turn rate deg/s, alt loss m, max stress 0..1
        internal readonly List<double[]> Dives = new List<double[]>();   // pitch, sink m/s, speed gain m/s
        /// <summary>Every recorded data point with the mass at that moment (Luke 7:21 PM): key, value, mass t.</summary>
        internal readonly List<object[]> Samples = new List<object[]>();
        internal void Note(string key, double value, double massT) { if (!double.IsNaN(value)) Samples.Add(new object[] { key, Math.Round(value, 3), Math.Round(massT, 3) }); }
        internal readonly List<double[]> Sink = new List<double[]>();    // sink m/s (+ down), throttle 0..1, lag s (Luke 7:19 PM)
        /// <summary>Throttle for a steady sink rate (+ = down) near 1.25 Vs, nose 12 deg up; scaled by m/M0 (thrust needed ~ weight).
        /// NaN without a table.</summary>
        internal double ThrottleForSink(double sink, double m)
        {
            if (Sink.Count == 0) return double.NaN;
            var s = new List<double[]>(Sink); s.Sort((a, b) => a[0].CompareTo(b[0]));
            double th;
            if (sink <= s[0][0]) th = s[0][1]; else if (sink >= s[s.Count - 1][0]) th = s[s.Count - 1][1];
            else { th = s[0][1]; for (int k = 1; k < s.Count; k++) if (sink <= s[k][0]) { double f = (sink - s[k - 1][0]) / Math.Max(1e-6, s[k][0] - s[k - 1][0]); th = s[k - 1][1] + f * (s[k][1] - s[k - 1][1]); break; } }
            double m0 = s[0].Length > 3 && !double.IsNaN(s[0][3]) ? s[0][3] : M0; double sc = double.IsNaN(m0) || m0 <= 0 || m <= 0 ? 1 : m / m0;
            return FlightPolicy.Clamp(th * sc, 0, 1);
        }
        internal static double ScaleSpeed(double v, double m0, double m) { return double.IsNaN(v) || double.IsNaN(m0) || m0 <= 0 || m <= 0 ? v : v * Math.Sqrt(m / m0); }
        internal static double ScaleAccel(double a, double m0, double m) { return double.IsNaN(a) || double.IsNaN(m0) || m0 <= 0 || m <= 0 ? a : a * m0 / m; }
        internal double StallAt(double m) { return ScaleSpeed(Stall, M0, m); }
        internal double LiftoffAt(double m) { return ScaleSpeed(Liftoff, M0, m); }
        internal double DecelAt(double m) { return ScaleAccel(Decel, M0, m); }
        internal double AccelAt(double m) { return ScaleAccel(Accel, M0, m); }
        static object N(double v) { return double.IsNaN(v) ? null : (object)Math.Round(v, 3); }
        static double D(Dictionary<string, object> d, string k) { object o; if (d == null || !d.TryGetValue(k, out o) || o == null) return double.NaN; try { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return double.NaN; } }
        internal Dictionary<string, object> ToDict()
        {
            var t = new List<object>(); foreach (var x in Turns) t.Add(new List<object> { x[0], Math.Round(x[1], 2), Math.Round(x[2], 2), Math.Round(x[3], 1), Math.Round(x[4], 2), x.Length > 5 ? Math.Round(x[5], 2) : double.NaN });
            var dv = new List<object>(); foreach (var x in Dives) dv.Add(new List<object> { x[0], Math.Round(x[1], 1), Math.Round(x[2], 1), x.Length > 3 ? Math.Round(x[3], 2) : double.NaN });
            var sm = new List<object>(); foreach (var x in Samples) sm.Add(new List<object> { x[0], x[1], x[2] });
            var sk = new List<object>(); foreach (var x in Sink) sk.Add(new List<object> { Math.Round(x[0], 1), Math.Round(x[1], 3), Math.Round(x[2], 1), x.Length > 3 ? Math.Round(x[3], 2) : double.NaN });
            return new Dictionary<string, object> { { "m0", N(M0) }, { "liftoff", N(Liftoff) }, { "stall", N(Stall) }, { "stall_alt_loss", N(StallAltLoss) }, { "climb_accel", N(ClimbAccel) }, { "climb_vs", N(ClimbVs) },
                { "decel", N(Decel) }, { "accel", N(Accel) }, { "lag", N(Lag) }, { "econ_speed", N(EconSpeed) }, { "econ_throttle", N(EconThrottle) }, { "fuel_used", N(FuelUsed) },
                { "complete", Complete }, { "abort", Abort }, { "date", Date }, { "turns", t }, { "dives", dv }, { "sink_table", sk }, { "samples", sm } };
        }
        internal static PlaneProfile FromDict(Dictionary<string, object> d)
        {
            if (d == null) return null; var p = new PlaneProfile();
            p.M0 = D(d, "m0"); p.Liftoff = D(d, "liftoff"); p.Stall = D(d, "stall"); p.StallAltLoss = D(d, "stall_alt_loss"); p.ClimbAccel = D(d, "climb_accel"); p.ClimbVs = D(d, "climb_vs");
            p.Decel = D(d, "decel"); p.Accel = D(d, "accel"); p.Lag = D(d, "lag"); p.EconSpeed = D(d, "econ_speed"); p.EconThrottle = D(d, "econ_throttle"); p.FuelUsed = D(d, "fuel_used");
            object o; p.Complete = d.TryGetValue("complete", out o) && o is bool && (bool)o; p.Abort = d.TryGetValue("abort", out o) && o != null ? o.ToString() : "";             p.Date = d.TryGetValue("date", out o) && o != null ? o.ToString() : "";
            if (d.TryGetValue("sink_table", out o) && o is System.Collections.IList) foreach (var r in (System.Collections.IList)o) { var l = r as System.Collections.IList; if (l == null || l.Count < 3) continue; try { var ci = System.Globalization.CultureInfo.InvariantCulture; p.Sink.Add(new[] { Convert.ToDouble(l[0], ci), Convert.ToDouble(l[1], ci), Convert.ToDouble(l[2], ci), l.Count > 3 && l[3] != null ? Convert.ToDouble(l[3], ci) : double.NaN }); } catch (Exception) { } }
            return p;
        }
        internal string Summary()
        {
            Func<double, string, string> f = (v, u) => double.IsNaN(v) ? "-" : v.ToString("0.0") + u;
            return "m0 " + f(M0, " t") + ", liftoff " + f(Liftoff, " m/s") + ", stall " + f(Stall, " m/s") + " (alt loss " + f(StallAltLoss, " m") + "), decel " + f(Decel, " m/s2") + ", accel " + f(Accel, " m/s2")
                + ", lag " + f(Lag, " s") + ", turns " + Turns.Count + ", dives " + Dives.Count + ", econ " + f(EconSpeed, " m/s") + " @ " + (double.IsNaN(EconThrottle) ? "-" : Math.Round(EconThrottle * 100) + "%") + (Abort.Length > 0 ? ", aborted: " + Abort : "");
        }
    }

    internal struct LearnIn
    {
        internal double T, Speed, Alt, Agl, Vs, Pitch, G, GLimit, FuelFrac, FuelRate, Stress, TurnRate, Throttle, Liftoff, StallGuess, Mass;
        internal bool Landed, PartLost;
    }

    /// <summary>Test-flight state machine. Pure: the controller feeds LearnIn each frame and applies the outputs
    /// (NaN = leave that axis to the normal controller).</summary>
    internal sealed class LearnFlight
    {
        internal string Phase = "start", Status = "", Reason = "";
        internal double Throttle = double.NaN, Pitch = double.NaN, Bank = double.NaN, Elevator = double.NaN;
        internal bool WantTakeoff, Done, Aborted;
        internal readonly PlaneProfile P = new PlaneProfile();
        internal const double ClimbAlt = 5000, StallAlt = 4000, FuelAbort = .4, StressAbort = .9, GAbort = .9, TurnStop = .8, Seg = 20, DiveSeg = 10;
        internal static readonly int[] Banks = { 5, 10, 15, 20, 25, 30 }; internal static readonly int[] DivePitch = { 5, 10, 15, 20 };
        internal const double SinkAlt = 5000, SinkPitch = 12, RampWait = 5; internal static readonly double[] SinkTargets = { 0, 2, 4, 6, 8, 10 };
        double sinkThr, sweepThr, vsF, lastVsF, lastT, prevT, stepAt, vs0, lagS = double.NaN, stepThr = double.NaN; bool lagDone; int tries; string resume = "sinkdown";
        readonly List<double[]> down = new List<double[]>();
        double GuessThr(double sink)
        {   // from the slow down-sweep (sink -> throttle), else linear between the sweep end and 0.6
            if (down.Count >= 2) { double best = double.MaxValue, th = .5; foreach (var d in down) { double e = Math.Abs(d[0] - sink); if (e < best) { best = e; th = d[1]; } } return th; }
            return FlightPolicy.Clamp(sweepThr + (10 - sink) * .04, 0, 1);
        }
        double t0, v0, a0, minV, maxPitch, minAlt, fuel0 = double.NaN, lagAt = double.NaN, gSum, trSum, stMax; int n, idx;
        readonly List<double[]> econ = new List<double[]>();
        internal readonly List<string> Visited = new List<string>();

        void Go(string ph, LearnIn i, string status)
        {
            Phase = ph; t0 = i.T; v0 = i.Speed; a0 = i.Alt; minV = i.Speed; maxPitch = i.Pitch; minAlt = i.Alt; gSum = trSum = stMax = 0; n = 0; lagAt = double.NaN;
            if (!Visited.Contains(ph)) Visited.Add(ph); if (status != null) Status = status;
        }
        static double Level(LearnIn i, double target = double.NaN)
        {
            double vsT = double.IsNaN(target) ? 0 : FlightPolicy.Clamp((target - i.Alt) * .05, -15, 15);
            return FlightPolicy.Clamp(3 + (vsT - i.Vs) * .6, -10, 15);
        }
        void Finish(bool aborted, string why)
        {
            Done = true; Aborted = aborted; P.Abort = aborted ? why : ""; P.Complete = !aborted; Reason = why; Phase = "done";
            if (econ.Count > 0) { double best = -1; foreach (var e in econ) { double k = e[0] / Math.Max(1e-6, e[2]); if (k > best) { best = k; P.EconSpeed = e[0]; P.EconThrottle = e[1]; } } P.Note("econ_speed", P.EconSpeed, P.M0); }
            Status = aborted ? "Test flight aborted (" + why + "): saving what was learned, landing at KSC 27." : "Test flight complete: profile saved, landing at KSC 27.";
            Throttle = Pitch = Bank = Elevator = double.NaN;
        }

        internal void Step(LearnIn i)
        {
            if (Done) return;
            if (double.IsNaN(P.M0)) P.M0 = i.Mass;
            if (double.IsNaN(fuel0)) fuel0 = i.FuelFrac; P.FuelUsed = fuel0 - i.FuelFrac;
            if (!i.Landed && Phase != "start" && Phase != "takeoff")
            {   // abort rules
                if (i.FuelFrac < FuelAbort) { Finish(true, "fuel " + Math.Round(i.FuelFrac * 100) + "%"); return; }
                if (i.PartLost) { Finish(true, "part lost"); return; }
                if (i.Stress > StressAbort) { Finish(true, "structural stress " + Math.Round(i.Stress * 100) + "%"); return; }
                if (i.GLimit > 0 && i.G > GAbort * i.GLimit) { Finish(true, "g " + i.G.ToString("0.0") + " near the " + i.GLimit.ToString("0") + " g limit"); return; }
            }
            if (Math.Abs(i.Vs) < 3 && i.FuelRate > 0 && !double.IsNaN(i.Throttle) && i.Throttle > .1 && (Phase == "recover" || Phase == "stallprep" || Phase == "accel")) econ.Add(new[] { i.Speed, i.Throttle, i.FuelRate });
            double dt = i.T - t0; Throttle = Pitch = Bank = Elevator = double.NaN;
            switch (Phase)
            {
                case "start":
                    if (i.Landed) { WantTakeoff = true; Go("takeoff", i, "Step 1/8: takeoff at full power, recording rotate/liftoff speed."); }
                    else Go("climb", i, "Step 2/8: already airborne - climb to 5 km at full power, 20 deg pitch.");
                    return;
                case "takeoff":
                    if (!i.Landed && i.Agl > 20) { P.Liftoff = double.IsNaN(i.Liftoff) ? i.Speed : i.Liftoff; P.Note("liftoff", P.Liftoff, i.Mass); Go("pullback", i, "Liftoff at " + P.Liftoff.ToString("0") + " m/s. Power back."); }
                    return;
                case "pullback":
                    Throttle = .5; Pitch = 8; Bank = 0;
                    if (dt > 3) Go("climb", i, "Step 2/8: climb to 5 km at full power, 20 deg pitch, recording acceleration.");
                    return;
                case "climb":
                    Throttle = 1; Pitch = i.Speed < 1.3 * i.StallGuess ? 8 : 20; Bank = 0; gSum += i.Vs; n++;
                    if (i.Alt >= ClimbAlt || dt > 400) { P.ClimbAccel = (i.Speed - v0) / Math.Max(1, dt); P.ClimbVs = gSum / Math.Max(1, n); P.Note("climb_accel", P.ClimbAccel, i.Mass); P.Note("climb_vs", P.ClimbVs, i.Mass); Go("decel", i, "Step 3/8: throttle idle, recording deceleration for 20 s."); }
                    return;
                case "decel":
                    Throttle = 0; Pitch = Level(i); Bank = 0;
                    if (dt >= Seg || i.Speed < 1.35 * i.StallGuess) { P.Decel = (v0 - i.Speed) / Math.Max(1, dt); P.Note("decel", P.Decel, i.Mass); Go("accel", i, "Step 3/8: full power, recording acceleration and throttle lag."); }
                    return;
                case "accel":
                    Throttle = 1; Pitch = Level(i); Bank = 0; minV = Math.Min(minV, i.Speed);
                    if (double.IsNaN(lagAt) && i.Speed > minV + .5) lagAt = dt;
                    if (dt >= Seg) { P.Accel = (i.Speed - minV) / Math.Max(1, dt - (double.IsNaN(lagAt) ? 0 : lagAt)); P.Lag = lagAt; P.Note("accel", P.Accel, i.Mass); P.Note("lag", P.Lag, i.Mass); idx = 0; Go("turn", i, "Step 4/8: banked turns 5 to 30 deg with 20 deg nose-up pull."); }
                    return;
                case "turn":
                    {
                        int b = Banks[idx]; Throttle = 1; Bank = b; Pitch = i.Speed < 1.4 * i.StallGuess ? Level(i) : 20; gSum += i.G; trSum += Math.Abs(i.TurnRate); stMax = Math.Max(stMax, i.Stress); n++;
                        bool nearLimit = (i.GLimit > 0 && i.G > TurnStop * i.GLimit) || i.Stress > TurnStop;
                        if (dt >= Seg || nearLimit || i.Speed < 1.3 * i.StallGuess)
                        {
                            P.Turns.Add(new double[] { b, gSum / Math.Max(1, n), trSum / Math.Max(1, n), a0 - i.Alt, stMax, i.Mass });
                            idx++;
                            if (nearLimit || idx >= Banks.Length) Go("recover", i, nearLimit ? "Turns stopped at " + b + " deg: near the g/stress limit." : "Turns done. Leveling off.");
                            else Go("turn", i, "Turn " + Banks[idx] + " deg bank.");
                        }
                        return;
                    }
                case "recover":
                    Throttle = 1; Pitch = Level(i); Bank = 0;
                    if (dt >= 12)
                    {
                        if (idx < 100) { idx = 100; Go("dive", i, "Step 5/8: dives at idle, 5 to 20 deg nose down."); }
                        else if (idx - 100 < DivePitch.Length && i.Alt > 2500) Go("dive", i, "Dive " + DivePitch[idx - 100] + " deg nose down.");
                        else Go("sinkprep", i, "Step 6/9: sink map - back to 5 km, slow to 1.25x stall, nose 12 deg up.");
                    }
                    return;
                case "dive":
                    {
                        int p = DivePitch[idx - 100]; Throttle = 0; Pitch = -p; Bank = 0; gSum += i.Vs; n++;
                        if (dt >= DiveSeg || i.Alt < 2000 || i.Speed > 320) { P.Dives.Add(new double[] { p, -gSum / Math.Max(1, n), i.Speed - v0, i.Mass }); idx++; Go("recover", i, null); }
                        return;
                    }
                case "sinkprep":
                    Throttle = i.Alt < SinkAlt - 200 ? 1 : .5; Pitch = i.Alt < SinkAlt - 200 ? Level(i, SinkAlt) : SinkPitch; Bank = 0;
                    if ((i.Alt >= SinkAlt - 200 && i.Speed <= 1.3 * i.StallGuess) || dt > 300) { sinkThr = .5; Go("sinkdown", i, "Sink map: reducing throttle slowly until 10 m/s sink."); }
                    return;
                case "sinkclimb":
                    Throttle = 1; Pitch = Level(i, SinkAlt); Bank = 0;
                    if (i.Alt >= SinkAlt - 100) { string r = resume; double keepT = stepThr; Go(r, i, "Sink map resumed."); stepThr = keepT; stepAt = i.T; vs0 = vsF; lagDone = false; prevT = i.T; }
                    return;
                case "sinkdown":
                case "sinkmap":
                    {
                        Pitch = SinkPitch; Bank = 0;
                        if (i.Speed < 1.15 * i.StallGuess) { Go("stallprep", i, "Sink map stopped (speed under 1.15x stall); " + P.Sink.Count + " points. Step 7/9: level at 4 km for the stall test."); return; }
                        if (i.Alt < 4000) { resume = Phase; Go("sinkclimb", i, "Sink map paused below 4 km: climbing back to 5 km, then resuming at " + Math.Round((Phase == "sinkdown" ? sinkThr : stepThr) * 100) + "% throttle."); return; }
                        vsF += (i.Vs - vsF) * Math.Min(1, (i.T - lastT) / 2); lastT = i.T;
                        if (Phase == "sinkdown")
                        {
                            if (i.T - prevT >= RampWait) { sinkThr = Math.Max(0, sinkThr - .01); prevT = i.T; } Throttle = sinkThr;   // 1 % then wait 5 s (throttle lag)
                            if (-vsF >= 10 || sinkThr <= 0 || dt > 900) { sweepThr = sinkThr; idx = SinkTargets.Length - 1; stepAt = i.T; Go("sinkmap", i, "Sink map: stepping throttle up for 10..0 m/s sink."); stepAt = i.T; vs0 = vsF; lagDone = false; }
                            else if (Math.Abs(vsF - lastVsF) < .2) { down.Add(new[] { -vsF, sinkThr }); P.Note("sweep_sink@" + Math.Round(sinkThr * 100), -vsF, i.Mass); }
                            lastVsF = vsF;
                            return;
                        }
                        double target = SinkTargets[idx];
                        if (double.IsNaN(stepThr)) { stepThr = GuessThr(target); stepAt = i.T; vs0 = vsF; lagDone = false; lagS = double.NaN; }
                        if (-vsF > 10 && i.T - prevT >= RampWait) { stepThr = Math.Min(1, stepThr + .01); prevT = i.T; }   // past 10 m/s sink: +1 %, then wait 5 s
                        Throttle = stepThr;
                        if (!lagDone && Math.Abs(vsF - vs0) > .5) { lagDone = true; lagS = i.T - stepAt; }
                        bool steady = i.T - stepAt > 8 && Math.Abs(vsF - lastVsF) < .05; lastVsF = vsF;
                        if (steady || i.T - stepAt > 30)
                        {
                            double sinkNow = -vsF;
                            if (Math.Abs(sinkNow - target) > 1.2 && i.T - stepAt < 30 && tries < 3) { stepThr = FlightPolicy.Clamp(stepThr + (sinkNow - target) * .02, 0, 1); stepAt = i.T; vs0 = vsF; lagDone = false; tries++; return; }
                            P.Sink.Add(new[] { sinkNow, stepThr, double.IsNaN(lagS) ? i.T - stepAt : lagS, i.Mass }); tries = 0; idx--; stepThr = double.NaN;
                            if (idx < 0) Go("stallprep", i, "Sink map done (" + P.Sink.Count + " points). Step 7/9: level at 4 km for the stall test.");
                        }
                        return;
                    }
                case "stallprep":
                    Throttle = .8; Pitch = Level(i, StallAlt); Bank = 0;
                    if ((Math.Abs(i.Alt - StallAlt) < 150 && Math.Abs(i.Vs) < 3 && dt > 5) || dt > 180) Go("stall", i, "Stall test: power idle, back-stick until the nose drops.");
                    return;
                case "stall":
                    {
                        Throttle = 0; Bank = 0; Elevator = FlightPolicy.Clamp(dt * .03, 0, 1); minV = Math.Min(minV, i.Speed);
                        if (i.Pitch > maxPitch) maxPitch = i.Pitch;
                        bool broke = Elevator >= .95 && (i.Pitch < -10 || i.Vs < -25);
                        if (broke || dt > 90) { P.Stall = minV; P.Note("stall", P.Stall, i.Mass); Go("stallrec", i, "Stall at " + minV.ToString("0") + " m/s. Recovering: nose down, full power, wings level."); }
                        return;
                    }
                case "stallrec":
                    Throttle = 1; Bank = 0; minAlt = Math.Min(minAlt, i.Alt);
                    Pitch = i.Speed < 1.3 * P.Stall ? -10 : Level(i);
                    if ((i.Vs > 0 && i.Speed > 1.3 * P.Stall && dt > 3) || dt > 60) { P.StallAltLoss = a0 - minAlt; P.Note("stall_alt_loss", P.StallAltLoss, i.Mass); Finish(false, ""); }
                    return;
            }
        }
    }
}
