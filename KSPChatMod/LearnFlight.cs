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
        internal readonly List<double[]> Turns = new List<double[]>();   // bank, g, turn rate deg/s, alt loss m, max stress/heat 0..1, mass t, throttle to hold speed
        internal readonly List<double[]> Dives = new List<double[]>();   // pitch, sink m/s, speed gain m/s
        /// <summary>Every recorded data point with the mass at that moment (Luke 7:21 PM): key, value, mass t.</summary>
        internal readonly List<object[]> Samples = new List<object[]>();
        internal void Note(string key, double value, double massT) { if (!double.IsNaN(value)) Samples.Add(new object[] { key, Math.Round(value, 3), Math.Round(massT, 3) }); }
        internal readonly PerfData Perf = new PerfData();
        internal readonly List<int> Rerun = new List<int>();   // steps unchecked for the next LEARN (persisted)
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
            var t = new List<object>(); foreach (var x in Turns) t.Add(new List<object> { x[0], Math.Round(x[1], 2), Math.Round(x[2], 2), Math.Round(x[3], 1), Math.Round(x[4], 2), x.Length > 5 ? Math.Round(x[5], 2) : double.NaN, x.Length > 6 ? Math.Round(x[6], 3) : double.NaN });
            var dv = new List<object>(); foreach (var x in Dives) dv.Add(new List<object> { x[0], Math.Round(x[1], 1), Math.Round(x[2], 1), x.Length > 3 ? Math.Round(x[3], 2) : double.NaN });
            var sm = new List<object>(); foreach (var x in Samples) sm.Add(new List<object> { x[0], x[1], x[2] });
            var sk = new List<object>(); foreach (var x in Sink) sk.Add(new List<object> { Math.Round(x[0], 1), Math.Round(x[1], 3), Math.Round(x[2], 1), x.Length > 3 ? Math.Round(x[3], 2) : double.NaN, x.Length > 4 ? Math.Round(x[4], 1) : double.NaN });
            return new Dictionary<string, object> { { "m0", N(M0) }, { "liftoff", N(Liftoff) }, { "stall", N(Stall) }, { "stall_alt_loss", N(StallAltLoss) }, { "climb_accel", N(ClimbAccel) }, { "climb_vs", N(ClimbVs) },
                { "decel", N(Decel) }, { "accel", N(Accel) }, { "lag", N(Lag) }, { "econ_speed", N(EconSpeed) }, { "econ_throttle", N(EconThrottle) }, { "fuel_used", N(FuelUsed) },
                { "complete", Complete }, { "abort", Abort }, { "date", Date }, { "turns", t }, { "dives", dv }, { "sink_table", sk }, { "samples", sm }, { "perf", Perf.ToDict() }, { "rerun", Rerun.ConvertAll(x => (object)x) } };
        }
        internal static PlaneProfile FromDict(Dictionary<string, object> d)
        {
            if (d == null) return null; var p = new PlaneProfile();
            p.M0 = D(d, "m0"); p.Liftoff = D(d, "liftoff"); p.Stall = D(d, "stall"); p.StallAltLoss = D(d, "stall_alt_loss"); p.ClimbAccel = D(d, "climb_accel"); p.ClimbVs = D(d, "climb_vs");
            p.Decel = D(d, "decel"); p.Accel = D(d, "accel"); p.Lag = D(d, "lag"); p.EconSpeed = D(d, "econ_speed"); p.EconThrottle = D(d, "econ_throttle"); p.FuelUsed = D(d, "fuel_used");
            object o; p.Complete = d.TryGetValue("complete", out o) && o is bool && (bool)o; p.Abort = d.TryGetValue("abort", out o) && o != null ? o.ToString() : "";             p.Date = d.TryGetValue("date", out o) && o != null ? o.ToString() : "";
            var rr = d.TryGetValue("rerun", out o) ? o as System.Collections.IList : null; if (rr != null) foreach (var x in rr) { try { p.Rerun.Add(Convert.ToInt32(x)); } catch (Exception) { } }
            foreach (var key in new[] { "turns", "dives" })
                if (d.TryGetValue(key, out o) && o is System.Collections.IList) foreach (var r in (System.Collections.IList)o)
                {
                    var l = r as System.Collections.IList; if (l == null || l.Count < 3) continue; var a = new double[l.Count];
                    for (int j = 0; j < l.Count; j++) { try { a[j] = l[j] == null ? double.NaN : Convert.ToDouble(l[j], System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { a[j] = double.NaN; } }
                    (key == "turns" ? p.Turns : p.Dives).Add(a);
                }
            if (d.TryGetValue("perf", out o)) p.Perf.FromDict(o as Dictionary<string, object>);
            if (d.TryGetValue("sink_table", out o) && o is System.Collections.IList) foreach (var r in (System.Collections.IList)o) { var l = r as System.Collections.IList; if (l == null || l.Count < 3) continue; try { var ci = System.Globalization.CultureInfo.InvariantCulture; p.Sink.Add(new[] { Convert.ToDouble(l[0], ci), Convert.ToDouble(l[1], ci), Convert.ToDouble(l[2], ci), l.Count > 3 && l[3] != null ? Convert.ToDouble(l[3], ci) : double.NaN, l.Count > 4 && l[4] != null ? Convert.ToDouble(l[4], ci) : double.NaN }); } catch (Exception) { } }
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
        internal double T, Speed, Alt, Agl, Vs, Pitch, G, GLimit, FuelFrac, FuelRate, Stress, TurnRate, Throttle, Liftoff, StallGuess, Mass, GroundSpeed, RollRate, PitchRate, Trim, ThrustFrac;
        internal string Res;   // resources that went down since the last frame (comma list)
        internal bool Landed, PartLost;
    }

    /// <summary>Test-flight state machine. Pure: the controller feeds LearnIn each frame and applies the outputs
    /// (NaN = leave that axis to the normal controller).</summary>
    internal sealed class LearnFlight
    {
        internal string Phase = "start", Status = "", Reason = "";
        internal double Throttle = double.NaN, Pitch = double.NaN, Bank = double.NaN, Elevator = double.NaN, Aileron = double.NaN;
        internal bool LandingDone; double rollM, rollPrevT = double.NaN, rollStartT = double.NaN;
        internal bool WantTakeoff, Done, Aborted;
        internal readonly PlaneProfile P = new PlaneProfile();
        /// <summary>Selective re-run (Luke 11:26 PM): only these steps are flown; takeoff, climb and landing always run as setup. Null = full test.</summary>
        internal readonly HashSet<int> Only; readonly PlaneProfile prior;
        internal LearnFlight() { }
        internal LearnFlight(PlaneProfile prev, IEnumerable<int> only)
        {
            if (prev == null || only == null) return;
            prior = PlaneProfile.FromDict(MiniJson.Deserialize(MiniJson.Serialize(prev.ToDict())) as Dictionary<string, object>);
            P = PlaneProfile.FromDict(MiniJson.Deserialize(MiniJson.Serialize(prev.ToDict())) as Dictionary<string, object>);
            Only = new HashSet<int>(only); if (Only.Contains(11)) Only.Add(10); if (Only.Contains(10)) Only.Add(11);
        }
        static bool Setup(int k) { return k <= 2 || k >= 12; }
        internal bool Selected(int k) { return Only == null || Only.Contains(k); }
        /// <summary>Phase after skipping unselected test steps ("finish" = end the test).</summary>
        string SkipTo(string ph)
        {
            for (int guard = 0; guard < 20 && Only != null && ph != "finish"; guard++)
            {
                int k = StepOf(ph, idx); if (Setup(k) || Only.Contains(k)) break;
                switch (k)
                {
                    case 3: ph = "cruise"; break; case 4: ph = "vmax"; break; case 5: ph = "rates"; break;
                    case 6: ph = "turn"; idx = 0; break; case 7: ph = "dive"; idx = 100; break; case 8: ph = "sinkprep"; break; case 9: ph = "stallprep"; break;
                    default: ph = "finish"; break;
                }
            }
            return ph;
        }
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
        double climbFuel0 = double.NaN, cruiseFuel0 = double.NaN, spoolAt = double.NaN, vmaxV, vmaxT, prevStepT = double.NaN;
        double turnThr, thrSum, overAt = double.NaN, spCmd = double.NaN, spT = double.NaN;
        /// <summary>Sink map (Luke 11:32 PM): elevator holds 1.2x stall (pitch-for-speed); throttle alone sets the sink.
        /// Slow -> nose down, fast -> nose up; clamped -10..15 deg, no extra nose-up above 1.5 g, hard nose-down near 1.15x stall.</summary>
        double SpeedPitch(LearnIn i)
        {
            double vT = 1.2 * i.StallGuess, e = i.Speed - vT, dt = double.IsNaN(spT) ? 0 : FlightPolicy.Clamp(i.T - spT, 0, 1); spT = i.T;
            if (double.IsNaN(spCmd)) spCmd = double.IsNaN(i.Pitch) ? 5 : i.Pitch;
            double rate = FlightPolicy.Clamp(e * .4, -3, 3); if (rate > 0 && i.G > 1.5) rate = 0;
            spCmd = FlightPolicy.Clamp(spCmd + rate * dt, -10, 15);
            double cmd = FlightPolicy.Clamp(spCmd + e * .5, -10, 15);
            if (i.Speed < 1.18 * i.StallGuess) cmd = Math.Min(cmd, -5);
            return cmd;
        }
        double CruiseThr() { if (econ.Count == 0) return .6; double best = -1, th = .6; foreach (var e in econ) { double k = e[0] / Math.Max(1e-6, e[2]); if (k > best) { best = k; th = e[1]; } } return th; }
        static string Cat(string ph)
        {
            switch (ph) { case "takeoff": case "pullback": case "start": return "takeoff"; case "climb": case "sinkclimb": return "climb"; case "decel": return "idle"; case "turn": return "turns"; case "dive": case "stall": case "stallrec": case "sinkdown": case "sinkmap": return "descent"; default: return "level"; }
        }
        /// <summary>Landing rollout (step 12): call after Done while the controller lands; true once stopped.</summary>
        internal bool TrackLanding(LearnIn i)
        {
            if (LandingDone) return false;
            if (i.Landed && i.Speed > 1) { if (double.IsNaN(rollStartT)) { rollStartT = i.T; rollM = 0; } else rollM += i.GroundSpeed * (i.T - rollPrevT); rollPrevT = i.T; return false; }
            if (i.Landed && !double.IsNaN(rollStartT) && i.Speed <= 1) { LandingDone = true; if (Selected(12) || prior == null || double.IsNaN(prior.Perf.Rollout)) P.Perf.Rollout = rollM; else rollM = P.Perf.Rollout; P.Note("rollout", rollM, i.Mass); return true; }
            rollPrevT = i.T; return false;
        }
        double t0, v0, a0, minV, maxPitch, minAlt, fuel0 = double.NaN, lagAt = double.NaN, gSum, trSum, stMax; int n, idx;
        readonly List<double[]> econ = new List<double[]>();
        internal readonly List<string> Visited = new List<string>();

        void Go(string ph, LearnIn i, string status)
        {
            ph = SkipTo(ph); if (ph == "finish") { Finish(false, ""); return; }
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
            if (prior != null)
            {   // keep the old setup data unless that step was selected
                if (!Only.Contains(1)) { P.Liftoff = prior.Liftoff; P.Perf.GroundRoll = prior.Perf.GroundRoll; }
                if (!Only.Contains(2)) { P.ClimbAccel = prior.ClimbAccel; P.ClimbVs = prior.ClimbVs; P.Perf.ClimbTime = prior.Perf.ClimbTime; P.Perf.ClimbFuel = prior.Perf.ClimbFuel; }
            }
            stepAtDone = StepNo; Done = true; Aborted = aborted; P.Abort = aborted ? why : ""; P.Complete = !aborted; Reason = why; Phase = "done";
            if (econ.Count > 0) { double best = -1; foreach (var e in econ) { double k = e[0] / Math.Max(1e-6, e[2]); if (k > best) { best = k; P.EconSpeed = e[0]; P.EconThrottle = e[1]; } } P.Note("econ_speed", P.EconSpeed, P.M0); }
            Status = aborted ? "Test flight aborted (" + why + "): saving what was learned, landing at KSC 27." : "Test flight complete: profile saved, landing at KSC 27.";
            Throttle = Pitch = Bank = Elevator = Aileron = double.NaN;
        }

        internal static readonly string[] StepNames = { "Takeoff + ground roll", "Climb to 5 km", "Idle decel / full-power accel", "Econ cruise 60 s", "Max level speed", "Roll / pitch rates", "Turns 5-30 deg", "Idle dives 5-20 deg", "Sink map 0-10 m/s", "Stall at 4 km", "Stall recovery", "Land KSC 27 + rollout" };
        internal static int StepCount { get { return StepNames.Length; } }
        LearnIn last; int stepAtDone = -1;
        /// <summary>1..9 for the current phase.</summary>
        internal int StepNo { get { return StepOf(Phase, idx); } }
        static int StepOf(string ph, int idx)
        {
            {
                switch (ph)
                {
                    case "start": case "takeoff": case "pullback": return 1;
                    case "climb": return 2; case "decel": case "accel": return 3; case "cruise": return 4; case "vmax": return 5; case "rates": return 6; case "turn": return 7;
                    case "recover": return idx < 100 ? 7 : 8; case "dive": return 8;
                    case "sinkprep": case "sinkdown": case "sinkmap": case "sinkclimb": return 9;
                    case "stallprep": case "stall": return 10; case "stallrec": return 11;
                    default: return 12;
                }
            }
        }
        /// <summary>Compact checklist (Luke 8:25 PM): [x] done, [>] current + sub-progress, [ ] pending, [-] skipped/aborted.</summary>
        internal List<string> Checklist()
        {
            var raw = ChecklistRaw(); if (Only == null) return raw;
            for (int j = 0; j < raw.Count; j++)
            {
                var l = raw[j]; if (l.Length < 6 || l[0] != '[') continue; int sp = l.IndexOf(' ', 4); int k; if (sp < 0 || !int.TryParse(l.Substring(4, sp - 4), out k)) continue;
                if (!Setup(k) && !Only.Contains(k)) raw[j] = "[x] " + k + " " + StepNames[k - 1] + " (kept)";
            }
            return raw;
        }
        /// <summary>Toggle view of a saved profile: [x] keep, [ ] re-run next LEARN; '>' marks the cursor.</summary>
        internal static List<string> ProfileChecklist(PlaneProfile p, int cursor)
        {
            var o = new List<string>();
            for (int k = 1; k <= StepCount; k++) o.Add((k - 1 == cursor ? ">" : " ") + (Setup(k) ? "[*] " : p.Rerun.Contains(k) ? "[ ] " : "[x] ") + k + " " + StepNames[k - 1]);
            o.Add(p.Rerun.Count == 0 ? "All kept. TOGGLE unchecks a step." : "LEARN re-runs " + p.Rerun.Count + " step(s) + takeoff/climb/landing.");
            return o;
        }
        internal static bool Toggle(PlaneProfile p, int step)
        {
            if (p == null || step < 1 || step > StepCount || Setup(step)) return false;
            if (!p.Rerun.Remove(step)) p.Rerun.Add(step); p.Rerun.Sort(); return true;
        }
        List<string> ChecklistRaw()
        {
            var o = new List<string>(); int cur = Done ? (LandingDone ? 13 : 12) : StepNo, abortAt = Aborted ? stepAtDone : -1;
            for (int k = 1; k <= StepCount; k++)
            {
                string mark, extra = "";
                if (Aborted && k >= abortAt && k < StepCount) mark = "[-]";
                else if (k < cur) mark = "[x]";
                else if (k == cur) mark = "[>]";
                else mark = "[ ]";
                if (mark == "[x]" && k == 8 && P.Dives.Count == 0) mark = "[-]";
                if (mark == "[x]" && k == 9 && P.Sink.Count == 0) mark = "[-]";
                if (k == 8 && P.Dives.Count > 0 && mark != "[>]") extra = " (" + P.Dives.Count + "/4)";
                if (k == 9 && P.Sink.Count > 0 && mark != "[>]") extra = " (" + P.Sink.Count + "/6)";
                if (k == 7 && P.Turns.Count > 0 && mark != "[>]") extra = " (to " + P.Turns[P.Turns.Count - 1][0] + " deg)";
                if (k == 1 && !double.IsNaN(P.Perf.GroundRoll) && mark == "[x]") extra = " (" + P.Perf.GroundRoll.ToString("0") + " m)";
                if (k == 12 && LandingDone) extra = " (rollout " + P.Perf.Rollout.ToString("0") + " m)";
                o.Add(mark + " " + k + " " + StepNames[k - 1] + extra);
                if (mark == "[>]") { string sub = SubProgress(); if (sub.Length > 0) o.Add("      " + sub); }
            }
            if (Aborted) o.Add("Aborted: " + Reason);
            return o;
        }
        string SubProgress()
        {
            var i = last; double dt = i.T - t0; Func<double, string> pc = x => double.IsNaN(x) ? "-" : Math.Round(x * 100) + "%";
            switch (Phase)
            {
                case "start": case "takeoff": return "speed " + i.Speed.ToString("0") + " m/s";
                case "pullback": return "liftoff " + P.Liftoff.ToString("0") + " m/s, power 50%";
                case "climb": return "alt " + (i.Alt / 1000).ToString("0.0") + "/5.0 km, " + i.Speed.ToString("0") + " m/s";
                case "decel": return "idle " + dt.ToString("0") + "/20 s, " + i.Speed.ToString("0") + " m/s";
                case "accel": return "full power " + dt.ToString("0") + "/20 s, " + i.Speed.ToString("0") + " m/s";
                case "cruise": return "econ " + Math.Round(CruiseThr() * 100) + "% " + dt.ToString("0") + "/60 s, " + i.Speed.ToString("0") + " m/s";
                case "vmax": return "full power level: " + i.Speed.ToString("0") + " m/s";
                case "rates": return (dt < 4 ? "roll " : dt < 7 ? "level " : "pitch ") + dt.ToString("0") + "/10 s";
                case "turn": return "bank " + Banks[Math.Min(idx, Banks.Length - 1)] + " deg, " + dt.ToString("0") + "/20 s, " + i.G.ToString("0.0") + " g";
                case "recover": return "level off " + dt.ToString("0") + "/12 s";
                case "dive": return "pitch -" + DivePitch[Math.Min(idx - 100, DivePitch.Length - 1)] + " deg, sink " + (-i.Vs).ToString("0") + " m/s";
                case "sinkprep": return "to 5 km / " + (1.3 * i.StallGuess).ToString("0") + " m/s: " + (i.Alt / 1000).ToString("0.0") + " km, " + i.Speed.ToString("0") + " m/s";
                case "sinkclimb": return "paused below 4 km, climbing: " + (i.Alt / 1000).ToString("0.0") + " km";
                case "sinkdown": return "ramp down: sink " + (-vsF).ToString("0.0") + " m/s, throttle " + pc(sinkThr);
                case "sinkmap": return "target " + SinkTargets[Math.Max(0, idx)] + " m/s: sink " + (-vsF).ToString("0.0") + ", throttle " + pc(stepThr);
                case "stallprep": return "to 4 km: " + (i.Alt / 1000).ToString("0.0") + " km";
                case "stall": return "back-stick " + pc(Elevator) + ", " + i.Speed.ToString("0") + " m/s, pitch " + i.Pitch.ToString("0");
                case "stallrec": return "recovering: " + i.Speed.ToString("0") + " m/s, lost " + (a0 - minAlt).ToString("0") + " m";
                default: return Done ? (LandingDone ? "" : rollM > 0 ? "rollout " + rollM.ToString("0") + " m" : "approach to KSC 27") : "";
            }
        }

        internal void Step(LearnIn i)
        {
            if (Done) return;
            last = i;
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
            P.Perf.Feed(Cat(Phase), i, prevStepT); prevStepT = i.T;
            double dt = i.T - t0; Throttle = Pitch = Bank = Elevator = Aileron = double.NaN;
            switch (Phase)
            {
                case "start":
                    if (i.Landed) { WantTakeoff = true; Go("takeoff", i, "takeoff at full power, recording rotate/liftoff speed."); }
                    else Go("climb", i, "already airborne - climb to 5 km at full power, 20 deg pitch.");
                    return;
                case "takeoff":
                    if (i.Landed && i.Speed > 1) { if (!double.IsNaN(rollPrevT)) rollM += i.GroundSpeed * (i.T - rollPrevT); rollPrevT = i.T; }
                    if (!i.Landed && i.Agl > 20) { P.Liftoff = double.IsNaN(i.Liftoff) ? i.Speed : i.Liftoff; P.Note("liftoff", P.Liftoff, i.Mass); P.Perf.GroundRoll = rollM; P.Note("ground_roll", rollM, i.Mass); Go("pullback", i, "Liftoff at " + P.Liftoff.ToString("0") + " m/s. Power back."); }
                    return;
                case "pullback":
                    Throttle = .5; Pitch = 8; Bank = 0;
                    if (dt > 3) Go("climb", i, "climb to 5 km at full power, 20 deg pitch, recording acceleration.");
                    return;
                case "climb":
                    if (n == 0) climbFuel0 = i.FuelFrac; P.Perf.Ceiling(i.Alt, i.Vs, i.Mass);
                    Throttle = 1; Pitch = i.Speed < 1.3 * i.StallGuess ? 8 : 20; Bank = 0; gSum += i.Vs; n++;
                    if (i.Alt >= ClimbAlt || dt > 400) { P.ClimbAccel = (i.Speed - v0) / Math.Max(1, dt); P.Perf.ClimbTime = dt; P.Perf.ClimbFuel = climbFuel0 - i.FuelFrac; P.Note("climb_time", dt, i.Mass); P.Note("climb_fuel", P.Perf.ClimbFuel, i.Mass); P.ClimbVs = gSum / Math.Max(1, n); P.Note("climb_accel", P.ClimbAccel, i.Mass); P.Note("climb_vs", P.ClimbVs, i.Mass); Go("decel", i, "throttle idle, recording deceleration for 20 s."); }
                    return;
                case "decel":
                    Throttle = 0; Pitch = Level(i); Bank = 0;
                    if (dt >= Seg || i.Speed < 1.35 * i.StallGuess) { P.Decel = (v0 - i.Speed) / Math.Max(1, dt); P.Note("decel", P.Decel, i.Mass); Go("accel", i, "full power, recording acceleration and throttle lag."); }
                    return;
                case "accel":
                    Throttle = 1; Pitch = Level(i); Bank = 0; minV = Math.Min(minV, i.Speed);
                    if (double.IsNaN(lagAt) && i.Speed > minV + .5) lagAt = dt;
                    if (double.IsNaN(spoolAt) && i.ThrustFrac >= .9) spoolAt = dt;
                    if (dt >= Seg) { P.Accel = (i.Speed - minV) / Math.Max(1, dt - (double.IsNaN(lagAt) ? 0 : lagAt)); P.Lag = lagAt; P.Note("accel", P.Accel, i.Mass); P.Note("lag", P.Lag, i.Mass); P.Perf.Spool = spoolAt; P.Note("spool", spoolAt, i.Mass); Go("cruise", i, "Level cruise 60 s at the economy setting: measuring cruise burn and trim."); }
                    return;
                case "cruise":
                    Throttle = CruiseThr(); Pitch = Level(i); Bank = 0; if (dt > 15) { gSum += i.Trim; n++; }
                    if (dt >= 60) { P.Perf.CruiseTrim = gSum / Math.Max(1, n); P.Perf.CruiseSpeed = i.Speed; P.Perf.CruiseThrottle = Throttle; P.Perf.CruiseFracPerS = (cruiseFuel0 - i.FuelFrac) / Math.Max(1, dt - 15);
                        P.Note("cruise_speed", i.Speed, i.Mass); P.Note("cruise_burn_pct_min", P.Perf.CruiseFracPerS * 6000, i.Mass); P.Note("cruise_trim", P.Perf.CruiseTrim, i.Mass);
                        Go("vmax", i, "Max level speed run: full power, level."); }
                    else if (dt <= 15) cruiseFuel0 = i.FuelFrac;
                    return;
                case "vmax":
                    Throttle = 1; Pitch = Level(i); Bank = 0;
                    if (dt > 10 && i.T - vmaxT >= 10) { if (i.Speed - vmaxV < 1 || dt > 600) { P.Perf.MaxLevel = i.Speed; P.Note("max_level_speed", i.Speed, i.Mass); Go("rates", i, "Roll and pitch rate checks: quick full inputs inside the g limit."); return; } vmaxV = i.Speed; vmaxT = i.T; }
                    if (dt <= 10) { vmaxV = i.Speed; vmaxT = i.T; }
                    return;
                case "rates":
                    {   // 0-2 s full right, 2-4 s full left, 4-7 s neutral (level), 7-8.5 s pull, 8.5-10 s relax; g-limited
                        Throttle = .8; P.Perf.RollRate = Math.Max(double.IsNaN(P.Perf.RollRate) ? 0 : P.Perf.RollRate, dt < 4 ? Math.Abs(i.RollRate) : 0);
                        if (dt < 2) Aileron = 1; else if (dt < 4) Aileron = -1; else if (dt < 7) { Bank = 0; Pitch = Level(i); }
                        else if (dt < 8.5) { Bank = 0; Elevator = i.GLimit > 0 && i.G > .7 * i.GLimit ? 0 : .6; P.Perf.PitchRate = Math.Max(double.IsNaN(P.Perf.PitchRate) ? 0 : P.Perf.PitchRate, Math.Abs(i.PitchRate)); }
                        else { Bank = 0; Pitch = Level(i); }
                        if (dt >= 10) { P.Note("roll_rate", P.Perf.RollRate, i.Mass); P.Note("pitch_rate", P.Perf.PitchRate, i.Mass); idx = 0; Go("turn", i, "Level turns 5 to 30 deg bank, holding altitude and speed."); }
                        return;
                    }
                case "turn":
                    {
                        int b = Banks[idx]; Bank = b;
                        // Level turn (Luke 11:25 PM): hold the entry altitude (VS ~0); back-pressure sets the g. PI throttle holds the entry speed.
                        if (n == 0) { turnThr = double.IsNaN(i.Throttle) ? .7 : FlightPolicy.Clamp(i.Throttle, .2, 1); thrSum = 0; }
                        turnThr = FlightPolicy.Clamp(turnThr + (v0 - i.Speed) * .0008, 0, 1);
                        Throttle = FlightPolicy.Clamp(turnThr + (v0 - i.Speed) * .04, 0, 1); thrSum += Throttle;
                        Pitch = Level(i, a0); gSum += i.G; trSum += Math.Abs(i.TurnRate); stMax = Math.Max(stMax, i.Stress); n++;
                        bool nearLimit = (i.GLimit > 0 && i.G > TurnStop * i.GLimit) || i.Stress > TurnStop;
                        if (dt >= Seg || nearLimit || i.Speed < 1.3 * i.StallGuess)
                        {
                            P.Turns.Add(new double[] { b, gSum / Math.Max(1, n), trSum / Math.Max(1, n), a0 - i.Alt, stMax, i.Mass, thrSum / Math.Max(1, n) });
                            P.Note("turn_throttle_" + b, thrSum / Math.Max(1, n), i.Mass);
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
                        if (idx < 100) { idx = 100; Go("dive", i, "dives at idle, 5 to 20 deg nose down."); }
                        else if (idx - 100 < DivePitch.Length && i.Alt > 2500) Go("dive", i, "Dive " + DivePitch[idx - 100] + " deg nose down.");
                        else Go("sinkprep", i, "sink map - back to 5 km, slow to 1.25x stall, nose 12 deg up.");
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
                    if ((i.Alt >= SinkAlt - 200 && i.Speed <= 1.3 * i.StallGuess) || dt > 300) { sinkThr = 0; overAt = double.NaN; Go("sinkdown", i, "Sink map: throttle cut until the sink passes 10 m/s."); }
                    return;
                case "sinkclimb":
                    Throttle = 1; Pitch = Level(i, SinkAlt); Bank = 0;
                    if (i.Alt >= SinkAlt - 100) { string r = resume; double keepT = stepThr; Go(r, i, "Sink map resumed."); stepThr = keepT; stepAt = i.T; vs0 = vsF; lagDone = false; prevT = i.T; }
                    return;
                case "sinkdown":
                case "sinkmap":
                    {
                        Pitch = SpeedPitch(i); Bank = 0;
                        if (i.Speed < 1.15 * i.StallGuess) { Go("stallprep", i, "Sink map stopped (speed under 1.15x stall); " + P.Sink.Count + " points. level at 4 km for the stall test."); return; }
                        if (i.Alt < 4000) { resume = Phase; Go("sinkclimb", i, "Sink map paused below 4 km: climbing back to 5 km, then resuming at " + Math.Round((Phase == "sinkdown" ? sinkThr : stepThr) * 100) + "% throttle."); return; }
                        vsF += (i.Vs - vsF) * Math.Min(1, (i.T - lastT) / 2); lastT = i.T;
                        if (Phase == "sinkdown")
                        {   // Luke 11:28 PM: cut the throttle until the sink is MORE than 10 m/s (held 3 s)
                            Throttle = sinkThr = 0;
                            if (-vsF > 10.5) { if (double.IsNaN(overAt)) overAt = i.T; } else overAt = double.NaN;
                            if ((!double.IsNaN(overAt) && i.T - overAt >= 3) || dt > 300)
                            { idx = SinkTargets.Length - 1; stepThr = 0; Go("sinkmap", i, "Sink map: throttle up 1% every 5 s, recording 10..0 m/s sink."); stepThr = 0; prevT = stepAt = i.T; vs0 = vsF; lagDone = false; lagS = double.NaN; }
                            return;
                        }
                        // +1 %, wait 5 s (throttle lag), repeat; record each target as the sink comes down through it
                        if (double.IsNaN(stepThr)) stepThr = 0;
                        if (i.T - prevT >= RampWait) { stepThr = Math.Min(1, stepThr + .01); prevT = stepAt = i.T; vs0 = vsF; lagDone = false; }
                        Throttle = stepThr;
                        if (!lagDone && Math.Abs(vsF - vs0) > .2) { lagDone = true; lagS = i.T - stepAt; }
                        while (idx >= 0 && -vsF <= SinkTargets[idx])
                        {
                            P.Sink.Add(new[] { SinkTargets[idx], stepThr, double.IsNaN(lagS) ? RampWait : lagS, i.Mass, i.Pitch }); P.Note("sink_thr@" + SinkTargets[idx], stepThr, i.Mass); idx--;
                        }
                        lastVsF = vsF;
                        if (idx < 0) Go("stallprep", i, "Sink map done (" + P.Sink.Count + " points). Level at 4 km for the stall test.");
                        else if (stepThr >= 1 && i.T - prevT >= RampWait) Go("stallprep", i, "Sink map: full power reached at " + (-vsF).ToString("0.0") + " m/s sink; " + P.Sink.Count + " points.");
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

namespace KSPChatBridge
{
    /// <summary>LEARN fuel/performance data (Luke 8:28 PM), mass-tagged per phase category.</summary>
    internal sealed class PerfData
    {
        internal double GroundRoll = double.NaN, Rollout = double.NaN, ClimbTime = double.NaN, ClimbFuel = double.NaN, MaxLevel = double.NaN, RollRate = double.NaN, PitchRate = double.NaN,
            CruiseTrim = double.NaN, CruiseSpeed = double.NaN, CruiseThrottle = double.NaN, CruiseFracPerS = double.NaN, Spool = double.NaN;
        /// <summary>cat -> [fuel frac used, seconds, metres, mass*seconds]</summary>
        internal readonly Dictionary<string, double[]> Phase = new Dictionary<string, double[]>();
        internal readonly Dictionary<string, HashSet<string>> Res = new Dictionary<string, HashSet<string>>();
        internal readonly List<double[]> Climb = new List<double[]>();   // alt km bin, mean climb m/s, mass
        double prevFuel = double.NaN; double binSum, binN; int bin = -1; double binMass;
        internal void Feed(string cat, LearnIn i, double prevT)
        {
            if (!double.IsNaN(prevT) && i.T > prevT && !double.IsNaN(prevFuel))
            {
                double dt = i.T - prevT; double[] a; if (!Phase.TryGetValue(cat, out a)) Phase[cat] = a = new double[4];
                a[0] += Math.Max(0, prevFuel - i.FuelFrac); a[1] += dt; a[2] += i.GroundSpeed * dt; a[3] += i.Mass * dt;
            }
            prevFuel = i.FuelFrac;
            if (!string.IsNullOrEmpty(i.Res)) { HashSet<string> h; if (!Res.TryGetValue(cat, out h)) Res[cat] = h = new HashSet<string>(); foreach (var r in i.Res.Split(',')) if (r.Length > 0) h.Add(r); }
        }
        internal void Ceiling(double alt, double vs, double m)
        {
            int b = (int)(alt / 1000); if (b != bin) { if (bin >= 0 && binN > 0) Climb.Add(new[] { bin + .5, binSum / binN, binMass }); bin = b; binSum = binN = 0; }
            binSum += vs; binN++; binMass = m;
        }
        internal double PctPerMin(string cat) { double[] a; return Phase.TryGetValue(cat, out a) && a[1] > 5 ? a[0] / a[1] * 6000 : double.NaN; }
        internal double PctPerKm(string cat) { double[] a; return Phase.TryGetValue(cat, out a) && a[2] > 500 ? a[0] / a[2] * 100000 : double.NaN; }
        /// <summary>Altitude (km) where climb at full power / 20 deg falls to 0.5 m/s, extrapolated from the last two 1 km bins.</summary>
        internal double CeilingKm()
        {
            if (Climb.Count < 2) return double.NaN; var a = Climb[Climb.Count - 2]; var b = Climb[Climb.Count - 1];
            double slope = (b[1] - a[1]) / Math.Max(1e-6, b[0] - a[0]); if (slope >= -1e-3) return double.NaN; return b[0] + (.5 - b[1]) / slope;
        }
        internal bool HasCruise { get { return !double.IsNaN(CruiseFracPerS) && CruiseFracPerS > 0 && !double.IsNaN(CruiseSpeed); } }
        internal double EnduranceMin(double fuelFrac) { return HasCruise ? fuelFrac / CruiseFracPerS / 60 : double.NaN; }
        internal double RangeKm(double fuelFrac) { return HasCruise ? fuelFrac / CruiseFracPerS * CruiseSpeed / 1000 : double.NaN; }
        /// <summary>Fuel fraction to fly home distKm at cruise plus one go-around (90 s at the climb burn, else 2x cruise).</summary>
        internal double ReserveFrac(double distKm)
        {
            if (!HasCruise) return double.NaN; double climb = PctPerMin("climb") / 6000; if (double.IsNaN(climb)) climb = 2 * CruiseFracPerS;
            return distKm * 1000 / CruiseSpeed * CruiseFracPerS + 90 * climb;
        }
        static object N(double v) { return double.IsNaN(v) ? null : (object)Math.Round(v, 5); }
        static double D(Dictionary<string, object> d, string k) { object o; if (d == null || !d.TryGetValue(k, out o) || o == null) return double.NaN; try { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return double.NaN; } }
        internal Dictionary<string, object> ToDict()
        {
            var ph = new Dictionary<string, object>(); foreach (var kv in Phase) ph[kv.Key] = new List<object> { Math.Round(kv.Value[0], 5), Math.Round(kv.Value[1], 1), Math.Round(kv.Value[2], 0), Math.Round(kv.Value[1] > 0 ? kv.Value[3] / kv.Value[1] : 0, 2) };
            var rs = new Dictionary<string, object>(); foreach (var kv in Res) rs[kv.Key] = string.Join(",", new List<string>(kv.Value).ToArray());
            var cl = new List<object>(); foreach (var c in Climb) cl.Add(new List<object> { c[0], Math.Round(c[1], 1), Math.Round(c[2], 2) });
            return new Dictionary<string, object> { { "ground_roll", N(GroundRoll) }, { "rollout", N(Rollout) }, { "climb_time", N(ClimbTime) }, { "climb_fuel", N(ClimbFuel) }, { "max_level", N(MaxLevel) },
                { "roll_rate", N(RollRate) }, { "pitch_rate", N(PitchRate) }, { "cruise_trim", N(CruiseTrim) }, { "cruise_speed", N(CruiseSpeed) }, { "cruise_throttle", N(CruiseThrottle) },
                { "cruise_frac_s", N(CruiseFracPerS) }, { "spool", N(Spool) }, { "phases", ph }, { "resources", rs }, { "climb_bins", cl } };
        }
        internal void FromDict(Dictionary<string, object> d)
        {
            if (d == null) return;
            GroundRoll = D(d, "ground_roll"); Rollout = D(d, "rollout"); ClimbTime = D(d, "climb_time"); ClimbFuel = D(d, "climb_fuel"); MaxLevel = D(d, "max_level"); RollRate = D(d, "roll_rate"); PitchRate = D(d, "pitch_rate");
            CruiseTrim = D(d, "cruise_trim"); CruiseSpeed = D(d, "cruise_speed"); CruiseThrottle = D(d, "cruise_throttle"); CruiseFracPerS = D(d, "cruise_frac_s"); Spool = D(d, "spool");
            object o; var ci = System.Globalization.CultureInfo.InvariantCulture;
            var ph = d.TryGetValue("phases", out o) ? o as Dictionary<string, object> : null;
            if (ph != null) foreach (var kv in ph) { var l = kv.Value as System.Collections.IList; if (l == null || l.Count < 4) continue; try { double t = Convert.ToDouble(l[1], ci); Phase[kv.Key] = new[] { Convert.ToDouble(l[0], ci), t, Convert.ToDouble(l[2], ci), Convert.ToDouble(l[3], ci) * t }; } catch (Exception) { } }
            var rs = d.TryGetValue("resources", out o) ? o as Dictionary<string, object> : null;
            if (rs != null) foreach (var kv in rs) Res[kv.Key] = new HashSet<string>((kv.Value ?? "").ToString().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            var cl = d.TryGetValue("climb_bins", out o) ? o as System.Collections.IList : null;
            if (cl != null) foreach (var c in cl) { var l = c as System.Collections.IList; if (l == null || l.Count < 3) continue; try { Climb.Add(new[] { Convert.ToDouble(l[0], ci), Convert.ToDouble(l[1], ci), Convert.ToDouble(l[2], ci) }); } catch (Exception) { } }
        }
    }
}
