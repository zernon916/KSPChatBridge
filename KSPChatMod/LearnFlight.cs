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
            var t = new List<object>(); foreach (var x in Turns) t.Add(new List<object> { x[0], Math.Round(x[1], 2), Math.Round(x[2], 2), Math.Round(x[3], 1), Math.Round(x[4], 2) });
            var dv = new List<object>(); foreach (var x in Dives) dv.Add(new List<object> { x[0], Math.Round(x[1], 1), Math.Round(x[2], 1) });
            return new Dictionary<string, object> { { "m0", N(M0) }, { "liftoff", N(Liftoff) }, { "stall", N(Stall) }, { "stall_alt_loss", N(StallAltLoss) }, { "climb_accel", N(ClimbAccel) }, { "climb_vs", N(ClimbVs) },
                { "decel", N(Decel) }, { "accel", N(Accel) }, { "lag", N(Lag) }, { "econ_speed", N(EconSpeed) }, { "econ_throttle", N(EconThrottle) }, { "fuel_used", N(FuelUsed) },
                { "complete", Complete }, { "abort", Abort }, { "date", Date }, { "turns", t }, { "dives", dv } };
        }
        internal static PlaneProfile FromDict(Dictionary<string, object> d)
        {
            if (d == null) return null; var p = new PlaneProfile();
            p.M0 = D(d, "m0"); p.Liftoff = D(d, "liftoff"); p.Stall = D(d, "stall"); p.StallAltLoss = D(d, "stall_alt_loss"); p.ClimbAccel = D(d, "climb_accel"); p.ClimbVs = D(d, "climb_vs");
            p.Decel = D(d, "decel"); p.Accel = D(d, "accel"); p.Lag = D(d, "lag"); p.EconSpeed = D(d, "econ_speed"); p.EconThrottle = D(d, "econ_throttle"); p.FuelUsed = D(d, "fuel_used");
            object o; p.Complete = d.TryGetValue("complete", out o) && o is bool && (bool)o; p.Abort = d.TryGetValue("abort", out o) && o != null ? o.ToString() : ""; p.Date = d.TryGetValue("date", out o) && o != null ? o.ToString() : "";
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
            if (econ.Count > 0) { double best = -1; foreach (var e in econ) { double k = e[0] / Math.Max(1e-6, e[2]); if (k > best) { best = k; P.EconSpeed = e[0]; P.EconThrottle = e[1]; } } }
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
                    if (!i.Landed && i.Agl > 20) { P.Liftoff = double.IsNaN(i.Liftoff) ? i.Speed : i.Liftoff; Go("pullback", i, "Liftoff at " + P.Liftoff.ToString("0") + " m/s. Power back."); }
                    return;
                case "pullback":
                    Throttle = .5; Pitch = 8; Bank = 0;
                    if (dt > 3) Go("climb", i, "Step 2/8: climb to 5 km at full power, 20 deg pitch, recording acceleration.");
                    return;
                case "climb":
                    Throttle = 1; Pitch = i.Speed < 1.3 * i.StallGuess ? 8 : 20; Bank = 0; gSum += i.Vs; n++;
                    if (i.Alt >= ClimbAlt || dt > 400) { P.ClimbAccel = (i.Speed - v0) / Math.Max(1, dt); P.ClimbVs = gSum / Math.Max(1, n); Go("decel", i, "Step 3/8: throttle idle, recording deceleration for 20 s."); }
                    return;
                case "decel":
                    Throttle = 0; Pitch = Level(i); Bank = 0;
                    if (dt >= Seg || i.Speed < 1.35 * i.StallGuess) { P.Decel = (v0 - i.Speed) / Math.Max(1, dt); Go("accel", i, "Step 3/8: full power, recording acceleration and throttle lag."); }
                    return;
                case "accel":
                    Throttle = 1; Pitch = Level(i); Bank = 0; minV = Math.Min(minV, i.Speed);
                    if (double.IsNaN(lagAt) && i.Speed > minV + .5) lagAt = dt;
                    if (dt >= Seg) { P.Accel = (i.Speed - minV) / Math.Max(1, dt - (double.IsNaN(lagAt) ? 0 : lagAt)); P.Lag = lagAt; idx = 0; Go("turn", i, "Step 4/8: banked turns 5 to 30 deg with 20 deg nose-up pull."); }
                    return;
                case "turn":
                    {
                        int b = Banks[idx]; Throttle = 1; Bank = b; Pitch = i.Speed < 1.4 * i.StallGuess ? Level(i) : 20; gSum += i.G; trSum += Math.Abs(i.TurnRate); stMax = Math.Max(stMax, i.Stress); n++;
                        bool nearLimit = (i.GLimit > 0 && i.G > TurnStop * i.GLimit) || i.Stress > TurnStop;
                        if (dt >= Seg || nearLimit || i.Speed < 1.3 * i.StallGuess)
                        {
                            P.Turns.Add(new double[] { b, gSum / Math.Max(1, n), trSum / Math.Max(1, n), a0 - i.Alt, stMax });
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
                        else Go("stallprep", i, "Step 6/8: level at 4 km for the stall test.");
                    }
                    return;
                case "dive":
                    {
                        int p = DivePitch[idx - 100]; Throttle = 0; Pitch = -p; Bank = 0; gSum += i.Vs; n++;
                        if (dt >= DiveSeg || i.Alt < 2000 || i.Speed > 320) { P.Dives.Add(new double[] { p, -gSum / Math.Max(1, n), i.Speed - v0 }); idx++; Go("recover", i, null); }
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
                        if (broke || dt > 90) { P.Stall = minV; Go("stallrec", i, "Stall at " + minV.ToString("0") + " m/s. Recovering: nose down, full power, wings level."); }
                        return;
                    }
                case "stallrec":
                    Throttle = 1; Bank = 0; minAlt = Math.Min(minAlt, i.Alt);
                    Pitch = i.Speed < 1.3 * P.Stall ? -10 : Level(i);
                    if ((i.Vs > 0 && i.Speed > 1.3 * P.Stall && dt > 3) || dt > 60) { P.StallAltLoss = a0 - minAlt; Finish(false, ""); }
                    return;
            }
        }
    }
}
