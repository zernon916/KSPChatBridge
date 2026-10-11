using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Craft shape snapshot saved with a LEARN profile (name stays the dictionary key). Used to catch
    /// "same name, different plane" without orphaning data on every SPH edit.</summary>
    internal sealed class CraftFingerprint
    {
        internal double DryMass = double.NaN, Lift = double.NaN;
        internal int Parts, Engines;
        internal string Root = "", LearnedName = "";
        internal bool Intake;
        /// <summary>True when at least one identifying field was stored (legacy profiles have none).</summary>
        internal bool Present { get { return Parts > 0 || (!double.IsNaN(Lift) && Lift > 0) || Root.Length > 0; } }
        internal enum Match { Missing, Ok, Soft, Hard }
        /// <summary>Hard: mass/lift &gt;20%, parts &gt;25%, or root part changed. Soft: mass/lift/parts &gt;10%. Missing: no saved print.</summary>
        internal static Match Compare(CraftFingerprint saved, CraftFingerprint now)
        {
            if (saved == null || !saved.Present || now == null || !now.Present) return Match.Missing;
            bool rootDiff = saved.Root.Length > 0 && now.Root.Length > 0 && !string.Equals(saved.Root, now.Root, StringComparison.OrdinalIgnoreCase);
            double massR = RelDiff(saved.DryMass, now.DryMass), liftR = RelDiff(saved.Lift, now.Lift);
            double partsR = saved.Parts <= 0 && now.Parts <= 0 ? 0 : Math.Abs(saved.Parts - now.Parts) / (double)Math.Max(1, Math.Max(saved.Parts, now.Parts));
            if (rootDiff || massR > .20 || liftR > .20 || partsR > .25) return Match.Hard;
            if (massR > .10 || liftR > .10 || partsR > .10 || saved.Intake != now.Intake) return Match.Soft;
            return Match.Ok;
        }
        /// <summary>Close enough to offer rename carry-over (same airframe under a new vessel name).</summary>
        internal static bool RenameMatch(CraftFingerprint saved, CraftFingerprint now)
        {
            var m = Compare(saved, now);
            return m == Match.Ok || (m == Match.Soft && saved.Root.Length > 0 && string.Equals(saved.Root, now.Root, StringComparison.OrdinalIgnoreCase) && RelDiff(saved.Lift, now.Lift) <= .10);
        }
        static double RelDiff(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b) || a <= 0 || b <= 0) return 0;
            return Math.Abs(a - b) / Math.Max(a, b);
        }
        internal static string Label(Match m)
        {
            return m == Match.Ok ? "OK" : m == Match.Soft ? "craft changed?" : m == Match.Hard ? "craft changed - re-run LEARN" : "unknown craft match";
        }
        static object N(double v) { return double.IsNaN(v) ? null : (object)Math.Round(v, 3); }
        static double D(Dictionary<string, object> d, string k) { object o; if (d == null || !d.TryGetValue(k, out o) || o == null) return double.NaN; try { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return double.NaN; } }
        internal Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> { { "dry_mass", N(DryMass) }, { "lift", N(Lift) }, { "parts", Parts }, { "engines", Engines },
                { "root", Root }, { "learned_name", LearnedName }, { "intake", Intake } };
        }
        internal static CraftFingerprint FromDict(Dictionary<string, object> d)
        {
            if (d == null) return null; var f = new CraftFingerprint();
            f.DryMass = D(d, "dry_mass"); f.Lift = D(d, "lift");
            object o; if (d.TryGetValue("parts", out o) && o != null) { try { f.Parts = Convert.ToInt32(o); } catch (Exception) { } }
            if (d.TryGetValue("engines", out o) && o != null) { try { f.Engines = Convert.ToInt32(o); } catch (Exception) { } }
            if (d.TryGetValue("root", out o) && o != null) f.Root = o.ToString();
            if (d.TryGetValue("learned_name", out o) && o != null) f.LearnedName = o.ToString();
            if (d.TryGetValue("intake", out o) && o is bool) f.Intake = (bool)o;
            return f.Present ? f : null;
        }
    }

    /// <summary>One learned profile per named craft (Luke 7:06 PM "LEARN THIS PLANE"). Values are at the test mass M0 (t);
    /// at another mass speeds scale by sqrt(m/M0) and accelerations by M0/m (thrust/drag-limited).</summary>
    internal sealed class PlaneProfile
    {
        internal double M0 = double.NaN, Liftoff = double.NaN, Stall = double.NaN, StallAltLoss = double.NaN, ClimbAccel = double.NaN, ClimbVs = double.NaN,
            Decel = double.NaN, Accel = double.NaN, Lag = double.NaN, EconSpeed = double.NaN, EconThrottle = double.NaN, FuelUsed = double.NaN;
        internal bool Complete; internal string Abort = "", Date = "";
        /// <summary>Shape at last LEARN write; null on legacy profiles.</summary>
        internal CraftFingerprint Fingerprint;
        internal readonly List<double[]> Turns = new List<double[]>();   // bank, g, turn rate deg/s, alt loss m, max stress/heat 0..1, mass t, throttle to hold speed
        internal readonly List<double[]> Dives = new List<double[]>();   // pitch, sink m/s, speed gain m/s
        /// <summary>Every recorded data point with the mass at that moment (Luke 7:21 PM): key, value, mass t.</summary>
        internal readonly List<object[]> Samples = new List<object[]>();
        internal void Note(string key, double value, double massT) { if (!double.IsNaN(value)) Samples.Add(new object[] { key, Math.Round(value, 3), Math.Round(massT, 3) }); }
        internal readonly PerfData Perf = new PerfData();
        internal readonly List<int> Rerun = new List<int>();
        internal readonly List<double[]> SinkFlaps = new List<double[]>();
        /// <summary>Safe turn envelope: [speed, max sustained bank, g, turn rate deg/s, radius m, roll-in s, mass].</summary>
        internal readonly List<double[]> Envelope = new List<double[]>();
        /// <summary>Sampled performance curves (mass-tagged). Empty curves fall back to the single-value fields above.</summary>
        internal readonly CurveBook Curves = new CurveBook();
        /// <summary>Max safe bank at a speed (interpolated by speed between the learned passes); NaN without data.</summary>
        internal double SafeBankAt(double speed)
        {
            if (Envelope.Count == 0) return double.NaN; var e = new List<double[]>(Envelope); e.Sort((a, b) => a[0].CompareTo(b[0]));
            if (speed <= e[0][0]) return e[0][1]; if (speed >= e[e.Count - 1][0]) return e[e.Count - 1][1];
            for (int k = 1; k < e.Count; k++) if (speed <= e[k][0]) { double t = (speed - e[k - 1][0]) / Math.Max(1e-6, e[k][0] - e[k - 1][0]); return e[k - 1][1] + t * (e[k][1] - e[k - 1][1]); }
            return e[e.Count - 1][1];
        }
        /// <summary>Mean roll-in seconds from the turn envelope rows (index 5).</summary>
        internal double RollIn { get { if (Envelope.Count == 0) return 0; double s = 0; foreach (var x in Envelope) s += double.IsNaN(x[5]) ? 0 : x[5]; return s / Envelope.Count; } }
        /// <summary>Climb rate (m/s) at altitude from the curve, else ClimbVs fallback.</summary>
        internal double ClimbRateAt(double altM, double mass)
        {
            double v = Curves.Climb.Lookup(mass, altM / 1000.0);
            return double.IsNaN(v) ? ClimbVs : v;
        }
        /// <summary>Best climb speed (Vy): ~1.4 Vs, or 85% of learned cruise when known.</summary>
        internal double BestClimbSpeed(double stall, double mass)
        {
            double vy = !double.IsNaN(Perf.CruiseSpeed) && Perf.CruiseSpeed > 0 ? Math.Min(ScaleSpeed(Perf.CruiseSpeed, M0, mass) * .85, 1.55 * stall) : 1.4 * stall;
            return Math.Max(1.3 * stall, vy);
        }
        internal double FlapStall = double.NaN, FlapStallAltLoss = double.NaN, FlapLimit = double.NaN;
        internal bool FlapSub = true, FlapStallSub = true;   // 9b / 10b sub-steps (toggle-able)   // steps unchecked for the next LEARN (persisted)
        internal readonly List<double[]> Sink = new List<double[]>();    // sink m/s (+ down), throttle 0..1, lag s (Luke 7:19 PM)
        /// <summary>Throttle for a steady sink rate (+ = down) near 1.25 Vs, nose 12 deg up; scaled by m/M0 (thrust needed ~ weight).
        /// NaN without a table.</summary>
        internal double ThrottleForSink(double sink, double m)
        {
            // Prefer the legacy sink table (stable invert). Curve used when the table is empty: invert by nearest sink samples.
            if (Sink.Count == 0 && !Curves.Sink.Empty)
            {
                double bestTh = double.NaN, bestErr = double.MaxValue, m0 = m;
                foreach (var pt in Curves.Sink.Points)
                {   // thr, spd, flaps, sink, mass
                    if (pt.Length < 5 || double.IsNaN(pt[3])) continue;
                    double err = Math.Abs(pt[3] - sink); if (err < bestErr) { bestErr = err; bestTh = pt[0]; m0 = pt[4]; }
                }
                if (!double.IsNaN(bestTh)) { double sc = double.IsNaN(m0) || m0 <= 0 || m <= 0 ? 1 : m / m0; return FlightPolicy.Clamp(bestTh * sc, 0, 1); }
            }
            if (Sink.Count == 0) return double.NaN;
            var s = new List<double[]>(Sink); s.Sort((a, b) => a[0].CompareTo(b[0]));
            double th0;
            if (sink <= s[0][0]) th0 = s[0][1]; else if (sink >= s[s.Count - 1][0]) th0 = s[s.Count - 1][1];
            else { th0 = s[0][1]; for (int k = 1; k < s.Count; k++) if (sink <= s[k][0]) { double f = (sink - s[k - 1][0]) / Math.Max(1e-6, s[k][0] - s[k - 1][0]); th0 = s[k - 1][1] + f * (s[k][1] - s[k - 1][1]); break; } }
            double m0b = s[0].Length > 3 && !double.IsNaN(s[0][3]) ? s[0][3] : M0; double scb = double.IsNaN(m0b) || m0b <= 0 || m <= 0 ? 1 : m / m0b;
            return FlightPolicy.Clamp(th0 * scb, 0, 1);
        }
        internal static double ScaleSpeed(double v, double m0, double m) { return double.IsNaN(v) || double.IsNaN(m0) || m0 <= 0 || m <= 0 ? v : v * Math.Sqrt(m / m0); }
        internal static double ScaleAccel(double a, double m0, double m) { return double.IsNaN(a) || double.IsNaN(m0) || m0 <= 0 || m <= 0 ? a : a * m0 / m; }
        internal double StallAt(double m) { double v = Curves.Stall.Lookup(m, 0); return double.IsNaN(v) ? ScaleSpeed(Stall, M0, m) : v; }
        internal double LiftoffAt(double m) { return ScaleSpeed(Liftoff, M0, m); }
        internal double DecelAt(double m) { double v = Curves.Decel.Lookup(m, double.IsNaN(EconSpeed) ? 100 : EconSpeed); return double.IsNaN(v) ? ScaleAccel(Decel, M0, m) : v; }
        internal double AccelAt(double m) { double v = Curves.Accel.Lookup(m, double.IsNaN(EconSpeed) ? 100 : EconSpeed); return double.IsNaN(v) ? ScaleAccel(Accel, M0, m) : v; }
        static object N(double v) { return double.IsNaN(v) ? null : (object)Math.Round(v, 3); }
        static double D(Dictionary<string, object> d, string k) { object o; if (d == null || !d.TryGetValue(k, out o) || o == null) return double.NaN; try { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return double.NaN; } }
        internal Dictionary<string, object> ToDict()
        {
            var t = new List<object>(); foreach (var x in Turns) t.Add(new List<object>(Array.ConvertAll(x, v => (object)(double.IsNaN(v) ? double.NaN : Math.Round(v, 3)))));
            var dv = new List<object>(); foreach (var x in Dives) dv.Add(new List<object> { x[0], Math.Round(x[1], 1), Math.Round(x[2], 1), x.Length > 3 ? Math.Round(x[3], 2) : double.NaN });
            var sm = new List<object>(); foreach (var x in Samples) sm.Add(new List<object> { x[0], x[1], x[2] });
            var sk = new List<object>(); foreach (var x in Sink) sk.Add(new List<object> { Math.Round(x[0], 1), Math.Round(x[1], 3), Math.Round(x[2], 1), x.Length > 3 ? Math.Round(x[3], 2) : double.NaN, x.Length > 4 ? Math.Round(x[4], 1) : double.NaN });
            var dict = new Dictionary<string, object> { { "m0", N(M0) }, { "liftoff", N(Liftoff) }, { "stall", N(Stall) }, { "stall_alt_loss", N(StallAltLoss) }, { "climb_accel", N(ClimbAccel) }, { "climb_vs", N(ClimbVs) },
                { "decel", N(Decel) }, { "accel", N(Accel) }, { "lag", N(Lag) }, { "econ_speed", N(EconSpeed) }, { "econ_throttle", N(EconThrottle) }, { "fuel_used", N(FuelUsed) },
                { "complete", Complete }, { "abort", Abort }, { "date", Date }, { "turns", t }, { "dives", dv }, { "sink_table", sk }, { "samples", sm }, { "perf", Perf.ToDict() }, { "rerun", Rerun.ConvertAll(x => (object)x) }, { "sink_flaps", SinkFlaps.ConvertAll(x => (object)new List<object> { Math.Round(x[0], 1), Math.Round(x[1], 3), Math.Round(x[2], 1), x.Length > 3 ? Math.Round(x[3], 2) : double.NaN, x.Length > 4 ? Math.Round(x[4], 1) : double.NaN }) },
                { "turn_envelope", Envelope.ConvertAll(x => (object)new List<object>(Array.ConvertAll(x, v => double.IsNaN(v) ? null : (object)Math.Round(v, 2)))) }, { "flap_stall", N(FlapStall) }, { "flap_stall_alt_loss", N(FlapStallAltLoss) }, { "flap_limit", N(FlapLimit) }, { "flap_sub", FlapSub }, { "flap_stall_sub", FlapStallSub }, { "curves", Curves.ToDict() } };
            if (Fingerprint != null && Fingerprint.Present) dict["fingerprint"] = Fingerprint.ToDict();
            return dict;
        }
        internal static PlaneProfile FromDict(Dictionary<string, object> d)
        {
            if (d == null) return null; var p = new PlaneProfile();
            p.M0 = D(d, "m0"); p.Liftoff = D(d, "liftoff"); p.Stall = D(d, "stall"); p.StallAltLoss = D(d, "stall_alt_loss"); p.ClimbAccel = D(d, "climb_accel"); p.ClimbVs = D(d, "climb_vs");
            p.Decel = D(d, "decel"); p.Accel = D(d, "accel"); p.Lag = D(d, "lag"); p.EconSpeed = D(d, "econ_speed"); p.EconThrottle = D(d, "econ_throttle"); p.FuelUsed = D(d, "fuel_used");
            object o; p.Complete = d.TryGetValue("complete", out o) && o is bool && (bool)o; p.Abort = d.TryGetValue("abort", out o) && o != null ? o.ToString() : "";             p.Date = d.TryGetValue("date", out o) && o != null ? o.ToString() : "";
            if (d.TryGetValue("fingerprint", out o)) p.Fingerprint = CraftFingerprint.FromDict(o as Dictionary<string, object>);
            var rr = d.TryGetValue("rerun", out o) ? o as System.Collections.IList : null; if (rr != null) foreach (var x in rr) { try { p.Rerun.Add(Convert.ToInt32(x)); } catch (Exception) { } }
            foreach (var key in new[] { "turns", "dives" })
                if (d.TryGetValue(key, out o) && o is System.Collections.IList) foreach (var r in (System.Collections.IList)o)
                {
                    var l = r as System.Collections.IList; if (l == null || l.Count < 3) continue; var a = new double[l.Count];
                    for (int j = 0; j < l.Count; j++) { try { a[j] = l[j] == null ? double.NaN : Convert.ToDouble(l[j], System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { a[j] = double.NaN; } }
                    (key == "turns" ? p.Turns : p.Dives).Add(a);
                }
            if (d.TryGetValue("turn_envelope", out o) && o is System.Collections.IList) foreach (var r in (System.Collections.IList)o) { var l = r as System.Collections.IList; if (l == null || l.Count < 6) continue; var ar = new double[l.Count]; for (int j = 0; j < l.Count; j++) { try { ar[j] = l[j] == null ? double.NaN : Convert.ToDouble(l[j], System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { ar[j] = double.NaN; } } p.Envelope.Add(ar); }
            p.FlapStall = D(d, "flap_stall"); p.FlapStallAltLoss = D(d, "flap_stall_alt_loss"); p.FlapLimit = D(d, "flap_limit");
            if (d.TryGetValue("flap_sub", out o) && o is bool) p.FlapSub = (bool)o; if (d.TryGetValue("flap_stall_sub", out o) && o is bool) p.FlapStallSub = (bool)o;
            if (d.TryGetValue("sink_flaps", out o) && o is System.Collections.IList) foreach (var r in (System.Collections.IList)o) { var l = r as System.Collections.IList; if (l == null || l.Count < 3) continue; var ar = new double[l.Count]; for (int j = 0; j < l.Count; j++) { try { ar[j] = l[j] == null ? double.NaN : Convert.ToDouble(l[j], System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { ar[j] = double.NaN; } } p.SinkFlaps.Add(ar); }
            if (d.TryGetValue("perf", out o)) p.Perf.FromDict(o as Dictionary<string, object>);
            if (d.TryGetValue("sink_table", out o) && o is System.Collections.IList) foreach (var r in (System.Collections.IList)o) { var l = r as System.Collections.IList; if (l == null || l.Count < 3) continue; try { var ci = System.Globalization.CultureInfo.InvariantCulture; p.Sink.Add(new[] { Convert.ToDouble(l[0], ci), Convert.ToDouble(l[1], ci), Convert.ToDouble(l[2], ci), l.Count > 3 && l[3] != null ? Convert.ToDouble(l[3], ci) : double.NaN, l.Count > 4 && l[4] != null ? Convert.ToDouble(l[4], ci) : double.NaN }); } catch (Exception) { } }
            if (d.TryGetValue("curves", out o)) p.Curves.FromDict(o as Dictionary<string, object>);
            p.Curves.SeedFromLegacy(p);
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
        internal double T, Speed, Alt, Agl, Vs, Pitch, G, GLimit, FuelFrac, FuelRate, Stress, TurnRate, Throttle, Liftoff, StallGuess, Mass, GroundSpeed, RollRate, PitchRate, Trim, ThrustFrac, Bank;
        /// <summary>TurnRate = heading rate (wrapTo180(dHdg)/dt, smoothed); CourseRate = velocity-vector track rate; Heading deg.</summary>
        internal double CourseRate, Heading;
        /// <summary>Flight-path angle (deg) from Vs and the horizontal surface speed; NaN without a horizontal speed (then nose pitch is used).</summary>
        internal double Fpa { get { return GroundSpeed > 1 ? PilotPolicy.FlightPathAngle(Vs, GroundSpeed) : double.NaN; } }
        internal bool HasFlaps;
        internal string Res;   // resources that went down since the last frame (comma list)
        internal bool Landed, PartLost;
    }

    /// <summary>Test-flight state machine. Pure: the controller feeds LearnIn each frame and applies the outputs
    /// (NaN = leave that axis to the normal controller).</summary>
    internal sealed class LearnFlight
    {
        internal string Phase = "start", Status = "", Reason = "";
        internal double Throttle = double.NaN, Pitch = double.NaN, Bank = double.NaN, Elevator = double.NaN, Aileron = double.NaN, SpeedTarget = double.NaN;
        /// <summary>Steps that explicitly test idle/full power (fast but finite throttle ramp); every other step goes through the speed PI.</summary>
        internal bool TestThrottle { get { return Phase == "decel" || Phase == "accel" || Phase == "vmax" || Phase == "dive" || Phase.StartsWith("stall") || Phase.StartsWith("sink") || Phase == "climb" || Phase == "rates" || Phase == "recover" || Phase == "cruise" || Phase == "climbback"; } }
        /// <summary>Steps found with throttle/pitch not tracking: data discarded, step left unchecked for a redo.</summary>
        internal readonly List<int> Redo = new List<int>();
        internal int Flaps;   // flap setting the test wants (0..3)
        bool flapPass, stallFlapPass;
        List<double[]> SinkTbl { get { return flapPass ? P.SinkFlaps : P.Sink; } }
        /// <summary>After the clean sink map: repeat with flaps full (toggle-able 9b), then the stall step.</summary>
        void SinkDone(LearnIn i, string msg)
        {
            if (!flapPass && i.HasFlaps && P.FlapSub && !ManualMode) { flapPass = true; P.SinkFlaps.Clear(); Go("sinkprep", i, msg + " Repeating the sink map with flaps FULL."); return; }
            flapPass = false; Go("stallprep", i, msg);
        }
        internal bool LandingDone; double rollM, rollPrevT = double.NaN, rollStartT = double.NaN;
        internal bool WantTakeoff, Done, Aborted;
        internal readonly PlaneProfile P = new PlaneProfile();
        /// <summary>Selective re-run (Luke 11:26 PM): only these steps are flown; takeoff, climb and landing always run as setup. Null = full test.</summary>
        internal readonly HashSet<int> Skipped = new HashSet<int>();
        /// <summary>SKIP (Luke 11:36 PM): end the current step, keep its partial data, mark [-], move on. Not during the takeoff roll or the landing.</summary>
        internal string Skip()
        {
            if (Done) return "Can't skip the landing.";
            if (ManualMode && activeTest == 0 && setupDone) return "No test running: check a test box to run one.";
            int k = StepNo;
            if (k == 1 && (Phase != "pullback")) return "Can't skip the takeoff roll.";
            if (k >= 12) return "Can't skip the landing.";
            Skipped.Add(k); if (k == 10) Skipped.Add(11);
            string next;
            switch (k)
            {
                case 1: next = "climb"; break; case 2: next = "decel"; break; case 3: next = "cruise"; break; case 4: next = "vmax"; break; case 5: next = "ratesprep"; break;
                case 6: next = "turn"; idx = 0; break; case 7: next = "recover"; idx = 0; break; case 8: next = "sinkprep"; break; case 9: next = "stallprep"; break;
                default: next = "finish"; break;
            }
            if (k == 7) { idx = 0; Go("recover", last, "Turns skipped. Leveling off."); idx = 100; Phase = "recover"; }   // level off, then dives
            else if (k == 8) Go("sinkprep", last, "Dives skipped.");
            else if (next == "finish") { if (ManualMode) Go("wait", last, "Step " + k + " skipped."); else Finish(false, ""); }
            else Go(next, last, "Step " + k + " skipped.");
            return "Skipped step " + k + " (" + StepNames[k - 1] + "); partial data kept.";
        }
        internal readonly HashSet<int> Only; readonly PlaneProfile prior;
        internal LearnFlight() { }
        internal LearnFlight(PlaneProfile prev, IEnumerable<int> only)
        {
            if (prev == null || only == null) return;
            prior = PlaneProfile.FromDict(MiniJson.Deserialize(MiniJson.Serialize(prev.ToDict())) as Dictionary<string, object>);
            P = PlaneProfile.FromDict(MiniJson.Deserialize(MiniJson.Serialize(prev.ToDict())) as Dictionary<string, object>);
            Only = new HashSet<int>(only); if (Only.Contains(11)) Only.Add(10); if (Only.Contains(10)) Only.Add(11);
        }
        // ---- Manual test picker (Oct 11): LEARN takes off, climbs and WAITS (holding altitude + heading, pilot may fly); checking a box
        // runs that one test, then the flight returns to waiting. Tests in any order; 12 = land and end. ----
        internal readonly bool ManualMode;
        internal readonly List<int> Queue = new List<int>();
        internal readonly List<int> Ran = new List<int>();
        int activeTest; bool setupDone, waitHere; readonly HashSet<int> setupUnchecked = new HashSet<int>(); double waitSpeed = double.NaN;
        internal int ActiveTest { get { return activeTest; } }
        internal int TestsDone { get; private set; }
        internal double HoldAlt = double.NaN, HoldHdg = double.NaN;
        internal bool Waiting { get { return ManualMode && !Done && (Phase == "wait" || Phase == "climbback"); } }
        internal LearnFlight(PlaneProfile prev, IEnumerable<int> queue, bool manual)
        {
            ManualMode = manual;
            if (prev != null) { prior = PlaneProfile.FromDict(MiniJson.Deserialize(MiniJson.Serialize(prev.ToDict())) as Dictionary<string, object>); P = PlaneProfile.FromDict(MiniJson.Deserialize(MiniJson.Serialize(prev.ToDict())) as Dictionary<string, object>); P.Rerun.Clear(); }
            if (queue != null) foreach (int k in queue) if (Pickable(k) && !Queue.Contains(k)) Queue.Add(k);
        }
        static bool Pickable(int k) { return (k >= 3 && k <= 11) || k == 91 || k == 101; }
        static int[] ActiveSteps(int code) { return code == 10 || code == 11 || code == 101 ? new[] { 10, 11 } : code == 91 ? new[] { 9 } : new[] { code }; }
        internal static string CodeName(int code) { return code == 91 ? "9b Flaps-full sink map" : code == 101 ? "10b Flaps-full stall" : code >= 1 && code <= StepCount ? code + " " + StepNames[code - 1] : code.ToString(); }
        /// <summary>Box checked (or unchecked) on the LEARN page while the manual test flight runs.</summary>
        internal string Request(int code)
        {
            if (!ManualMode || Done) return "No test flight running: press LEARN.";
            if (code == 1 || code == 2)
            {   // setup boxes toggle too: unchecking the running takeoff/climb = SKIP (climb skip waits right here); otherwise flip the mark
                if (!setupDone && activeTest == 0 && StepNo == code)
                {
                    if (code == 2) { waitHere = true; string r = Skip(); waitHere = false; return r; }
                    return Skip();
                }
                if (!setupUnchecked.Remove(code)) { setupUnchecked.Add(code); return CodeName(code) + " unchecked (" + (setupDone || StepNo > code ? "already flown; data kept" : "runs as setup") + ")."; }
                return CodeName(code) + " checked.";
            }
            if (code == 12) { Finish(false, ""); return "Ending the test flight: landing at KSC 27."; }
            if (!Pickable(code)) return "Unknown test.";
            if (activeTest == code)
                return "Unchecked the running test: " + Skip();
            if (Queue.Remove(code)) return "Test " + CodeName(code) + " unchecked (removed from the queue).";
            if (Ran.Remove(code)) return "Test " + CodeName(code) + " unchecked (data kept; check again to re-run).";
            Queue.Add(code);
            return Phase == "wait" ? "Starting test " + CodeName(code) + "." : !setupDone ? "Test " + CodeName(code) + " queued: runs once the climb is done." : "Test " + CodeName(code) + " queued: runs when the current test is done.";
        }
        void StartTest(int code, LearnIn i)
        {
            activeTest = code; flapPass = code == 91; stallFlapPass = code == 101; string n = "Test " + CodeName(code) + ": ";
            switch (code)
            {
                case 3: Go("decel", i, n + "throttle idle, recording deceleration for 20 s."); break;
                case 4: cruiseFuel0 = i.FuelFrac; Go("cruise", i, n + "level cruise 60 s at the economy setting."); break;
                case 5: vmaxV = i.Speed; vmaxT = i.T; Go("vmax", i, n + "full power, level."); break;
                case 6: ratesSpeed = RatesSpeed(i.StallGuess); ratesSteady = double.NaN; Go("ratesprep", i, n + "slowing to " + ratesSpeed.ToString("0") + " m/s first."); break;
                case 7: idx = 0; turnPass = 0; passBest = null; P.Turns.Clear(); P.Envelope.Clear(); passSpeed = TurnSpeed(i.Speed, i.StallGuess); Go("turnprep", i, n + "settling level at " + passSpeed.ToString("0") + " m/s before banking."); break;
                case 8: idx = 100; Go("dive", i, n + "idle, flight path " + DivePitch[0] + " deg down."); break;
                case 9: case 91: if (code == 91) P.SinkFlaps.Clear(); else P.Sink.Clear(); sinkStart = double.NaN; limThr = 0; Go("sinkprep", i, n + "back to 5 km, slow to " + SinkSpeedFactor + "x stall."); break;
                default: Go("stallprep", i, n + "level at 4 km first."); break;
            }
        }
        /// <summary>Manual mode: setup (takeoff/climb) flows on; inside a test its own phases flow on; anything else ends the test -> wait
        /// (climbing back to 5 km first when low).</summary>
        string ManualRoute(string ph, LearnIn i, ref string status)
        {
            if (ph == "climbback" || ph == "wait") { if (ph == "wait" && i.Alt < ClimbAlt - 500 && !waitHere) ph = "climbback"; }
            else
            {
                int k = StepOf(ph, idx);
                if (!setupDone && activeTest == 0 && k <= 2) return ph;
                if (activeTest != 0 && Array.IndexOf(ActiveSteps(activeTest), k) >= 0) return ph;
                ph = i.Alt < ClimbAlt - 500 && !waitHere ? "climbback" : "wait";
            }
            string pre = status ?? "";
            if (activeTest != 0) { if (!Ran.Contains(activeTest)) Ran.Add(activeTest); TestsDone++; pre = "Test " + CodeName(activeTest) + " done. " + (status ?? ""); activeTest = 0; }
            setupDone = true; flapPass = stallFlapPass = false;
            status = (pre.Length > 0 ? pre.Trim() + " " : "") + (ph == "climbback" ? "Climbing back to 5 km: full power, up to 30 deg." : "Waiting: holding altitude and heading. Check a test box to run it; you can fly manually any time.");
            return ph;
        }
        double WaitSpeed(LearnIn i)
        {
            double st = double.IsNaN(i.StallGuess) || i.StallGuess <= 0 ? 60 : i.StallGuess, c = P.Perf.CruiseSpeed;
            return Math.Max(1.5 * st, !double.IsNaN(c) && c > 0 ? c : 1.7 * st);
        }
        /// <summary>LEARN page while the manual flight runs: every box starts unchecked; [>] running, [x] done / queued.</summary>
        internal List<string> ManualChecklist(int cursor)
        {
            var o = new List<string>();
            for (int j = 0; j < Codes.Length; j++)
            {
                int k = Codes[j]; string cur = j == cursor ? ">" : " ", ind = k == 91 || k == 101 ? "  " : "";
                if (k == 1 || k == 2) { o.Add(cur + (setupUnchecked.Contains(k) ? "[ ] " : setupDone || StepNo > k ? "[*] " : StepNo == k ? "[>] " : "[ ] ") + CodeName(k) + " (auto)"); continue; }
                if (k == 12) { o.Add(cur + "[ ] 12 Land KSC 27 + rollout (check to end)"); continue; }
                string mark = activeTest == k ? "[>] " : Queue.Contains(k) || Ran.Contains(k) ? "[x] " : "[ ] ";
                string extra = activeTest == k ? "" : Queue.Contains(k) ? " (queued)" : Ran.Contains(k) ? " (done)" : "";
                o.Add(cur + ind + mark + CodeName(k) + extra);
                if (activeTest == k) { string sub = SubProgress(); if (sub.Length > 0) o.Add("      " + sub); }
            }
            o.Add(Phase == "wait" ? "WAITING at " + (double.IsNaN(HoldAlt) ? "-" : HoldAlt.ToString("0")) + " m hdg " + (double.IsNaN(HoldHdg) ? "-" : HoldHdg.ToString("000")) + ": TOGGLE a box to run that test." : Phase == "climbback" ? "Climbing back to 5 km." : !setupDone ? "Takeoff / climb, then waiting." : "Running " + CodeName(activeTest) + ".");
            return o;
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
                    case 3: ph = "cruise"; break; case 4: ph = "vmax"; break; case 5: ph = "ratesprep"; break;
                    case 6: ph = "turn"; idx = 0; break; case 7: ph = "dive"; idx = 100; break; case 8: ph = "sinkprep"; break; case 9: ph = "stallprep"; break;
                    default: ph = "finish"; break;
                }
            }
            return ph;
        }
        internal const double ClimbAlt = 5000, StallAlt = 4000, FuelAbort = .4, StressAbort = .9, GAbort = .9, TurnStop = .8, TurnSpikeG = 1.0, TurnSpikeStress = .97, LoadTau = .5, Seg = 20, DiveSeg = 10;
        internal static readonly int[] Banks = { 10, 20, 30, 40, 45, 50, 55, 60, 65, 70, 75, 80 }; /* stress test: short dwell, ladder runs to the real limit */ internal const double TurnSeg = 10; internal static readonly int[] DivePitch = { 5, 10, 15, 20 };   // max-safe-turn stress test (Luke 10:06 AM) internal static readonly int[] DivePitch = { 5, 10, 15, 20 };
        internal const double TurnPrepTimeout = 120; internal const double SinkAlt = 5000, RampWait = 5, SinkSpeedFactor = 1.25; internal static readonly double[] SinkTargets = { 0, 2, 4, 6, 8, 10 };
        double sinkThr, vsF, lastVsF, lastT, prevT, stepAt, vs0, lagS = double.NaN, stepThr = double.NaN; bool lagDone; int tries; string resume = "sinkdown";
        double climbFuel0 = double.NaN, cruiseFuel0 = double.NaN, spoolAt = double.NaN, vmaxV, vmaxT, prevStepT = double.NaN;
        double reachAt = double.NaN; internal const double ReachTimeout = 20;
        double ratesSpeed = double.NaN, ratesSteady = double.NaN, ratesElev; bool ratesCut;
        internal const double RatesG = .6, RatesElevMax = .6, RatesRamp = .4, RatesPrepTimeout = 90;
        /// <summary>Step 6 test speed: learned cruise or 2x stall (the lower), never under 1.6x stall.</summary>
        internal double RatesSpeed(double stall)
        {
            double s = double.IsNaN(stall) || double.IsInfinity(stall) || stall <= 0 ? 60 : stall, c = P.Perf.CruiseSpeed, v = 2 * s;
            if (!double.IsNaN(c) && c > 0) v = Math.Min(v, c);
            return Math.Max(v, 1.6 * s);
        }
        /// <summary>Pitch-rate pull: elevator ramps at RatesRamp/s up to RatesElevMax; once G passes RatesG x the limit it is cut to 0 for the rest of the pull
        /// (so neither the 80% learn limit nor the 90% abort is reached). No known limit: 9 g assumed.</summary>
        internal static double RatesElevator(double prev, double dt, double g, double gLimit, ref bool cut)
        {
            double lim = gLimit > 0 ? gLimit : 9; if (g > RatesG * lim) cut = true; if (cut) return 0;
            return Math.Min(RatesElevMax, Math.Max(0, double.IsNaN(prev) ? 0 : prev) + RatesRamp * Math.Max(0, dt));
        }
        /// <summary>Turns/dives measure only once the commanded bank/pitch is reached (heavy craft roll/pitch slowly; Luke 9:37 AM log).</summary>
        bool Reached(LearnIn i, bool ok) { if (double.IsNaN(reachAt) && (ok || i.T - t0 >= ReachTimeout)) { reachAt = i.T; gSum = trSum = crSum = stMax = 0; n = 0; thrSum = 0; spdSum = 0; a0 = i.Alt; } return !double.IsNaN(reachAt); }
        double TurnPrepSpeed(LearnIn i) { return turnPass == 0 && !double.IsNaN(passSpeed) ? passSpeed : 1.5 * i.StallGuess; }
        /// <summary>End of a turn pass: save the last good bank to the envelope at the MEASURED mean speed (row[11]), then next pass / recover.</summary>
        void EndPass(LearnIn i, string why, string tail)
        {
            if (passBest != null) { P.Envelope.Add(new[] { passBest[11], passBest[0], passBest[1], passBest[2], passBest[8], passBest[9], i.Mass }); P.Note("safe_bank@" + Math.Round(passBest[11]), passBest[0], i.Mass); }
            string res = (passBest != null ? "max safe bank " + passBest[0] + " deg at " + passBest[11].ToString("0") + " m/s (" + passBest[1].ToString("0.0") + " g, " + passBest[2].ToString("0.0") + " deg/s, r " + (passBest[8] / 1000).ToString("0.0") + " km)" : "no safe turn found") + tail;
            passBest = null; passSpeed = double.NaN;
            if (turnPass == 0) { turnPass = 1; Go("turnprep", i, "Turns at cruise: " + why + "; " + res + ". Slowing to ~approach speed for the second pass."); }
            else { turnPass = 0; Go("recover", i, "Turns at approach speed: " + why + "; " + res + ". Leveling off."); }
        }
        /// <summary>Turn step phases (turnprep / turn / turn recover): NFC skips the low-energy bank clamp and the G term of CapHot.</summary>
        internal bool TurnPhase { get { return Phase == "turn" || Phase == "turnprep" || (Phase == "recover" && idx < 100); } }
        /// <summary>Sink map phases: NFC skips the MinSafeSpeed hold floor and the low-energy override (the map flies 1.25x by pitch).</summary>
        internal bool SinkPhase { get { return Phase != null && Phase.StartsWith("sink"); } }
        internal double TurnSpeed(double v0, double stall) { double c = P.Perf.CruiseSpeed; double t = !double.IsNaN(c) && c > 0 ? c : 1.7 * stall; return Math.Min(v0, Math.Max(t, 1.5 * stall)); }
        double tickPrevT = double.NaN, badPitch, badThr, gF = double.NaN, stF = double.NaN, spdF = double.NaN, spdTrend, prevThr = double.NaN; internal const double BadPitchDeg = 8, BadPitchS = 8, BadThrS = 5;
        int turnPass; double passSpeed = double.NaN; double[] passBest; internal const double TurnAltLoss = 150;
        double thrSum, spdSum, overAt = double.NaN, spCmd = double.NaN, spT = double.NaN;
        /// <summary>Sink map (Luke 11:32 PM): elevator holds SinkSpeedFactor (1.25x) stall (pitch-for-speed); throttle alone sets the sink.
        /// Slow -> nose down, fast -> nose up; clamped -10..15 deg, no extra nose-up above 1.5 g, hard nose-down near 1.15x stall.</summary>
        double sinkStart = double.NaN, limThr, limT = double.NaN;
        internal const double SinkCap = 600, SinkMax = 15, SinkSettle = 2; double recThr = double.NaN;
        /// <summary>Extra throttle when the sink passes ~15 m/s (Luke 11:36 PM: the fixed-pitch build fell ~1000 ft); bleeds off once back under.</summary>
        double SinkLimit(LearnIn i)
        {   // driven by the same filtered VS as the map decisions; floor scheduled by altitude
            double dt = double.IsNaN(limT) ? 0 : FlightPolicy.Clamp(i.T - limT, 0, 1); limT = i.T; double sink = -vsF, max = SinkMaxAt(i.Alt);
            limThr = FlightPolicy.Clamp(limThr + (sink > max ? .1 * dt * (sink - max + 1) : -.05 * dt), 0, 1);
            return limThr;
        }
        /// <summary>Learn soft sink floor by altitude: ~28 m/s allowed above 4.5 km, tightening linearly to SinkMax (15) at 4 km.</summary>
        internal const double SinkMaxHigh = 28, SinkHighAlt = 4500, SinkLowAlt = 4000;
        internal static double SinkMaxAt(double alt)
        {
            if (double.IsNaN(alt) || alt <= SinkLowAlt) return SinkMax; if (alt >= SinkHighAlt) return SinkMaxHigh;
            return SinkMax + (SinkMaxHigh - SinkMax) * (alt - SinkLowAlt) / (SinkHighAlt - SinkLowAlt);
        }
        internal double SinkFiltered { get { return vsF; } }
        void FilterVs(LearnIn i) { if (i.T < lastT) lastT = i.T; vsF += (i.Vs - vsF) * Math.Min(1, (i.T - lastT) / 2); lastT = i.T; }
        double SpeedPitch(LearnIn i, double factor = 1.2)
        {
            double vT = factor * i.StallGuess, e = i.Speed - vT, dt = double.IsNaN(spT) ? 0 : FlightPolicy.Clamp(i.T - spT, 0, 1); spT = i.T;
            double gam = i.Fpa; bool path = !double.IsNaN(gam);   // sink tests fly the flight-path angle; nose = path + angle of attack
            if (double.IsNaN(spCmd)) spCmd = path ? gam : double.IsNaN(i.Pitch) ? 5 : i.Pitch;
            double rate = FlightPolicy.Clamp(e * .4, -3, 3); if (rate > 0 && i.G > 1.5) rate = 0;
            spCmd = FlightPolicy.Clamp(spCmd + rate * dt, -10, 15);
            double cmd = FlightPolicy.Clamp(spCmd + e * .5, -10, 15);
            if (i.Speed < 1.18 * i.StallGuess) cmd = Math.Min(cmd, -5);
            return path ? FpaPitch(i, cmd, dt) : cmd;
        }
        /// <summary>Nose pitch that flies a flight-path angle: target path + (smoothed) angle of attack (pitch - path). No path angle
        /// (no horizontal speed) -> the target is used as nose pitch.</summary>
        double aoaF = double.NaN;
        double FpaPitch(LearnIn i, double fpaTarget, double dt)
        {
            double g = i.Fpa; if (double.IsNaN(g) || double.IsNaN(i.Pitch)) return fpaTarget;
            double aoa = FlightPolicy.Clamp(i.Pitch - g, -15, 25);
            aoaF = double.IsNaN(aoaF) ? aoa : aoaF + (aoa - aoaF) * Math.Min(1, Math.Max(0, dt) / 1.0);
            return FlightPolicy.Clamp(fpaTarget + aoaF, -45, 30);
        }
        /// <summary>G / stress EMA (LoadTau) for the turn limit stop: one-frame geeForce / temperature spikes don't end the bank ladder.</summary>
        void FilterLoad(LearnIn i, double dt)
        {
            double a = Math.Min(1, dt / LoadTau);
            gF = double.IsNaN(gF) ? i.G : gF + (i.G - gF) * a; stF = double.IsNaN(stF) ? i.Stress : stF + (i.Stress - stF) * a;
            if (double.IsNaN(spdF)) { spdF = i.Speed; spdTrend = 0; } else if (dt > 0) { spdTrend += ((i.Speed - spdF) / dt - spdTrend) * Math.Min(1, dt / 1.0); spdF = i.Speed; }   // speed trend (m/s2, 1 s EMA)
        }
        internal double LoadG { get { return gF; } }
        /// <summary>Turn pitch: altitude hold (Level) plus load-factor back-pressure for the bank (ApproachProfile.TurnPitch).</summary>
        internal static double TurnPitchCmd(LearnIn i, double bank, double alt) { return FlightPolicy.Clamp(Level(i, alt) + ApproachProfile.TurnPitch(bank), -10, 20); }
        /// <summary>Throttle really not tracking: well slow, speed not recovering, lever low AND not moving up. A lever still ramping up
        /// (gate rate / spool lag) or speed already coming back is the PI working, not a fault.</summary>
        bool ThrottleNotTracking(LearnIn i, double vT, double dt)
        {
            bool rising = !double.IsNaN(prevThr) && !double.IsNaN(i.Throttle) && i.Throttle > prevThr + 1e-4; prevThr = i.Throttle;
            return i.Speed < vT - 10 && !double.IsNaN(i.Throttle) && i.Throttle < .3 && !rising && spdTrend <= 0;
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
        double t0, v0, a0, minV, maxPitch, minAlt, fuel0 = double.NaN, lagAt = double.NaN, gSum, trSum, crSum, stMax; int n, idx;
        readonly List<double[]> econ = new List<double[]>();
        internal readonly List<string> Visited = new List<string>();

        void Go(string ph, LearnIn i, string status)
        {
            if (ManualMode) ph = ManualRoute(ph, i, ref status);
            else { ph = SkipTo(ph); if (ph == "finish") { Finish(false, ""); return; } }
            Phase = ph; aoaF = double.NaN;
            spdF = double.NaN; prevThr = double.NaN; badThr = 0;
            if (ph == "sinkdown" || ph == "sinkmap") { vsF = i.Vs; lastT = i.T; overAt = double.NaN; recThr = double.NaN; limThr = 0; limT = double.NaN; }
            if (ph == "wait") { HoldAlt = waitHere ? Math.Round(i.Alt / 100) * 100 : ClimbAlt; HoldHdg = i.Heading; waitSpeed = WaitSpeed(i); } t0 = i.T; v0 = i.Speed; a0 = i.Alt; minV = i.Speed; maxPitch = i.Pitch; minAlt = i.Alt; gSum = trSum = crSum = stMax = 0; n = 0; lagAt = double.NaN; reachAt = double.NaN;
            if (!Visited.Contains(ph)) Visited.Add(ph); if (status != null) Status = status;
        }
        /// <summary>Climb level-off lead: full climb pitch until vs x LevelLead s (150..3000 m) below the target, then blend linearly
        /// into the altitude hold so the climb captures the target instead of zooming past it (live 17:53: 30 deg climb overshot 5 km to 8.6 km).</summary>
        internal const double LevelLead = 13;
        internal static double LevelOff(LearnIn i, double target, double climbPitch)
        {
            double lead = FlightPolicy.Clamp(Math.Max(0, i.Vs) * LevelLead, 150, 3000), below = target - i.Alt;
            if (below >= lead) return climbPitch;
            double lvl = Level(i, target), f = FlightPolicy.Clamp(below / lead, 0, 1);
            return lvl + (Math.Max(climbPitch, lvl) - lvl) * f;
        }
        /// <summary>Level / altitude hold for Learn prep and wait phases. Commands a flight-path angle, not raw nose from VS error: the old
        /// 3 + 0.6 deg per m/s VS law had a loop gain of ~2.6 at 250 m/s (1 deg = 4.4 m/s) and, with nose/path lag, porpoised -7..+11 deg on a
        /// ~16 s cycle (live 18:16, turnprep). Now: nose = current AoA + target path angle + 0.5 x path error; altitude -> VS at 0.03/s, max 10 m/s.</summary>
        internal static double Level(LearnIn i, double target = double.NaN)
        {
            double vsT = double.IsNaN(target) ? 0 : FlightPolicy.Clamp((target - i.Alt) * .03, -10, 10);
            double gam = i.Fpa, v = Math.Max(30, i.GroundSpeed);
            if (double.IsNaN(gam)) return FlightPolicy.Clamp(3 + (vsT - i.Vs) * .15, -10, 15);
            double aoa = FlightPolicy.Clamp(i.Pitch - gam, -5, 15), gT = Math.Asin(FlightPolicy.Clamp(vsT / v, -1, 1)) * 180 / Math.PI;
            return FlightPolicy.Clamp(aoa + gT + .5 * (gT - gam), -10, 15);
        }
        void SampleClimb(LearnIn i)
        {
            P.Perf.Ceiling(i.Alt, i.Vs, i.Mass);
            if (i.Vs > 1) P.Curves.Climb.Add(i.Vs, i.Mass, i.Alt / 1000.0);
        }
        void Finish(bool aborted, string why)
        {
            if (prior != null && Only != null)
            {   // keep the old setup data unless that step was selected
                if (!Only.Contains(1)) { P.Liftoff = prior.Liftoff; P.Perf.GroundRoll = prior.Perf.GroundRoll; }
                if (!Only.Contains(2)) { P.ClimbAccel = prior.ClimbAccel; P.ClimbVs = prior.ClimbVs; P.Perf.ClimbTime = prior.Perf.ClimbTime; P.Perf.ClimbFuel = prior.Perf.ClimbFuel; }
            }
            stepAtDone = StepNo; Done = true; Aborted = aborted; P.Abort = aborted ? why : ""; P.Complete = !aborted; Reason = why; Phase = "done";
            if (econ.Count > 0) { double best = -1; foreach (var e in econ) { double k = e[0] / Math.Max(1e-6, e[2]); if (k > best) { best = k; P.EconSpeed = e[0]; P.EconThrottle = e[1]; } } P.Note("econ_speed", P.EconSpeed, P.M0); }
            Status = aborted ? "Test flight aborted (" + why + "): saving what was learned, landing at KSC 27." : "Test flight complete: profile saved, landing at KSC 27.";
            Throttle = Pitch = Bank = Elevator = Aileron = SpeedTarget = double.NaN;
        }

        void EndTest(LearnIn i) { if (ManualMode) Go("wait", i, null); else Finish(false, ""); }
        internal static readonly string[] StepNames = { "Takeoff + ground roll", "Climb to 5 km", "Idle decel / full-power accel", "Econ cruise 60 s", "Max level speed", "Roll / pitch rates", "Max safe turn / envelope", "Idle dives 5-20 deg", "Sink map 0-10 m/s", "Stall at 4 km", "Stall recovery", "Land KSC 27 + rollout" };
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
                    case "climb": return 2; case "decel": case "accel": return 3; case "cruise": return 4; case "vmax": return 5; case "ratesprep": case "rates": return 6; case "turn": case "turnprep": return 7;
                    case "recover": return idx < 100 ? 7 : 8; case "dive": return 8;
                    case "sinkprep": case "sinkdown": case "sinkmap": case "sinkclimb": return 9;
                    case "stallprep": case "stall": return 10; case "stallrec": return 11;
                    case "wait": case "climbback": return 0;
                    default: return 12;
                }
            }
        }
        /// <summary>Compact checklist (Luke 8:25 PM): [x] done, [>] current + sub-progress, [ ] pending, [-] skipped/aborted.</summary>
        internal List<string> Checklist()
        {
            var raw = ChecklistRaw(); if (Only == null && Skipped.Count == 0) return raw;
            for (int j = 0; j < raw.Count; j++)
            {
                var l = raw[j]; if (l.Length < 6 || l[0] != '[') continue; int sp = l.IndexOf(' ', 4); int k; if (sp < 0 || !int.TryParse(l.Substring(4, sp - 4), out k)) continue;
                if (Skipped.Contains(k)) raw[j] = "[-] " + k + " " + StepNames[k - 1] + " (skipped)";
                else if (Only != null && !Setup(k) && !Only.Contains(k)) raw[j] = "[x] " + k + " " + StepNames[k - 1] + " (kept)";
            }
            return raw;
        }
        /// <summary>LEARN page before a flight (Oct 11): every box starts unchecked; [x] = run that test after the climb. '>' marks the cursor.</summary>
        internal static List<string> ProfileChecklist(PlaneProfile p, int cursor)
        {
            var o = new List<string>();
            var codes = Codes;
            for (int j = 0; j < codes.Length; j++)
            {
                int k = codes[j]; string cur = j == cursor ? ">" : " ";
                if (k == 91) o.Add(cur + (p.Rerun.Contains(91) ? "  [x] " : "  [ ] ") + "9b Flaps-full sink map");
                else if (k == 101) o.Add(cur + (p.Rerun.Contains(101) ? "  [x] " : "  [ ] ") + "10b Flaps-full stall (optional)");
                else o.Add(cur + (Setup(k) ? "[*] " : p.Rerun.Contains(k) ? "[x] " : "[ ] ") + k + " " + StepNames[k - 1]);
            }
            o.Add(p.Rerun.Count == 0 ? "All unchecked. LEARN takes off, climbs and waits; TOGGLE a box to run that test." : "LEARN runs " + p.Rerun.Count + " checked test(s) after the climb, then waits.");
            return o;
        }
        /// <summary>Profile checklist rows: steps 1..12 with the flap sub-steps 9b (91) and 10b (101).</summary>
        internal static readonly int[] Codes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 91, 10, 101, 11, 12 };
        internal static bool Toggle(PlaneProfile p, int step)
        {
            if (p == null || ((step < 1 || step > StepCount || Setup(step)) && step != 91 && step != 101)) return false;
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
                case "ratesprep": return "slowing to " + (double.IsNaN(ratesSpeed) ? "-" : ratesSpeed.ToString("0")) + " m/s (" + i.Speed.ToString("0") + ")";
                case "rates": return (dt < 4 ? "roll " : dt < 7 ? "level " : "pitch ") + dt.ToString("0") + "/10 s";
                case "turnprep": return (turnPass == 0 ? "cruise" : "approach") + " settle " + TurnPrepSpeed(i).ToString("0") + " m/s (" + i.Speed.ToString("0") + ")";
                case "turn": return (turnPass == 0 ? "cruise " : "approach ") + "bank " + Banks[Math.Min(idx, Banks.Length - 1)] + " deg, " + dt.ToString("0") + "/20 s, " + i.G.ToString("0.0") + " g";
                case "recover": return "level off " + dt.ToString("0") + "/12 s";
                case "dive": return "pitch -" + DivePitch[Math.Min(idx - 100, DivePitch.Length - 1)] + " deg, sink " + (-i.Vs).ToString("0") + " m/s";
                case "sinkprep": return "to 5 km / " + (SinkSpeedFactor * i.StallGuess).ToString("0") + " m/s: " + (i.Alt / 1000).ToString("0.0") + " km, " + i.Speed.ToString("0") + " m/s";
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
                // turn steps: the turn state machine stops at TurnStop (0.8x, filtered) and saves passBest; only a real spike aborts there
                bool turning = Phase == "turn" || Phase == "turnprep" || (Phase == "recover" && idx < 100);
                // sink climb-back (sinkclimb / climbing sinkprep, ~30 deg nose up at full power): only a real spike aborts (REPORT 7.9 #14 B)
                if (Phase == "sinkclimb" || (Phase == "sinkprep" && i.Alt < SinkAlt - 200)) turning = true;
                if (i.Stress > (turning ? TurnSpikeStress : StressAbort)) { Finish(true, "structural stress " + Math.Round(i.Stress * 100) + "%"); return; }
                if (i.GLimit > 0 && i.G > (turning ? TurnSpikeG : GAbort) * i.GLimit) { Finish(true, "g " + i.G.ToString("0.0") + " near the " + i.GLimit.ToString("0") + " g limit"); return; }
            }
            if (Math.Abs(i.Vs) < 3 && i.FuelRate > 0 && !double.IsNaN(i.Throttle) && i.Throttle > .1 && (Phase == "recover" || Phase == "stallprep" || Phase == "accel")) econ.Add(new[] { i.Speed, i.Throttle, i.FuelRate });
            Flaps = flapPass || stallFlapPass ? 3 : 0;
            P.Perf.Feed(Cat(Phase), i, prevStepT); prevStepT = i.T;
            double dt = i.T - t0; Throttle = Pitch = Bank = Elevator = Aileron = SpeedTarget = double.NaN; double tick = double.IsNaN(tickPrevT) ? 0 : Math.Max(0, i.T - tickPrevT); tickPrevT = i.T; FilterLoad(i, tick);
            switch (Phase)
            {
                case "start":
                    if (i.Landed) { WantTakeoff = true; Go("takeoff", i, "takeoff at full power, recording rotate/liftoff speed."); }
                    else Go("climb", i, "already airborne - climb to 5 km at full power, up to 30 deg pitch.");
                    return;
                case "takeoff":
                    if (i.Landed && i.Speed > 1) { if (!double.IsNaN(rollPrevT)) rollM += i.GroundSpeed * (i.T - rollPrevT); rollPrevT = i.T; }
                    if (!i.Landed && i.Agl > 20) { P.Liftoff = double.IsNaN(i.Liftoff) ? i.Speed : i.Liftoff; P.Note("liftoff", P.Liftoff, i.Mass); P.Perf.GroundRoll = rollM; P.Note("ground_roll", rollM, i.Mass); Go("pullback", i, "Liftoff at " + P.Liftoff.ToString("0") + " m/s. Power back."); }
                    return;
                case "pullback":
                    Throttle = .5; Pitch = 8; Bank = 0;
                    if (dt > 3) Go("climb", i, "climb to 5 km at full power, up to 30 deg pitch, recording acceleration.");
                    return;
                case "climb":
                    if (n == 0) climbFuel0 = i.FuelFrac; SampleClimb(i);
                    Throttle = 1; Pitch = LevelOff(i, ClimbAlt, i.Speed < 1.3 * i.StallGuess ? 8 : PilotPolicy.ClimbBackPitch(i.Speed, i.StallGuess)); Bank = 0; gSum += i.Vs; n++;   // up to ~30 deg, easing toward 1.3x stall
                    if (i.Alt >= ClimbAlt - 60 || dt > 400) { P.ClimbAccel = (i.Speed - v0) / Math.Max(1, dt); P.Perf.ClimbTime = dt; P.Perf.ClimbFuel = climbFuel0 - i.FuelFrac; P.Note("climb_time", dt, i.Mass); P.Note("climb_fuel", P.Perf.ClimbFuel, i.Mass); P.ClimbVs = gSum / Math.Max(1, n); P.Note("climb_accel", P.ClimbAccel, i.Mass); P.Note("climb_vs", P.ClimbVs, i.Mass); P.Curves.Accel.Add(P.ClimbAccel, i.Mass, (i.Speed + v0) / 2); Go("decel", i, "throttle idle, recording deceleration for 20 s."); }
                    return;
                case "decel":
                    Throttle = 0; Pitch = Level(i); Bank = 0;
                    if (dt >= Seg || i.Speed < 1.35 * i.StallGuess) { P.Decel = (v0 - i.Speed) / Math.Max(1, dt); P.Note("decel", P.Decel, i.Mass); P.Curves.Decel.Add(P.Decel, i.Mass, (v0 + i.Speed) / 2); Go("accel", i, "full power, recording acceleration and throttle lag."); }
                    return;
                case "accel":
                    Throttle = 1; Pitch = Level(i); Bank = 0; minV = Math.Min(minV, i.Speed);
                    if (double.IsNaN(lagAt) && i.Speed > minV + .5) lagAt = dt;
                    if (double.IsNaN(spoolAt) && i.ThrustFrac >= .9) spoolAt = dt;
                    if (dt >= Seg) { P.Accel = (i.Speed - minV) / Math.Max(1, dt - (double.IsNaN(lagAt) ? 0 : lagAt)); P.Lag = lagAt; P.Note("accel", P.Accel, i.Mass); P.Note("lag", P.Lag, i.Mass); P.Perf.Spool = spoolAt; P.Note("spool", spoolAt, i.Mass); P.Curves.Accel.Add(P.Accel, i.Mass, (minV + i.Speed) / 2); if (!double.IsNaN(P.Lag)) P.Curves.ThrottleLag.Add(P.Lag, i.Mass, 1); Go("cruise", i, "Level cruise 60 s at the economy setting: measuring cruise burn and trim."); }
                    return;
                case "cruise":
                    Throttle = CruiseThr(); Pitch = Level(i); Bank = 0; if (dt > 15) { gSum += i.Trim; n++; }
                    if (dt >= 60) { P.Perf.CruiseTrim = gSum / Math.Max(1, n); P.Perf.CruiseSpeed = i.Speed; P.Perf.CruiseThrottle = Throttle; P.Perf.CruiseFracPerS = (cruiseFuel0 - i.FuelFrac) / Math.Max(1, dt - 15);
                        P.Note("cruise_speed", i.Speed, i.Mass); P.Note("cruise_burn_pct_min", P.Perf.CruiseFracPerS * 6000, i.Mass); P.Note("cruise_trim", P.Perf.CruiseTrim, i.Mass);
                        if (!double.IsNaN(P.Perf.CruiseFracPerS)) P.Curves.FuelBurn.Add(P.Perf.CruiseFracPerS * 100, i.Mass, i.Speed, i.Alt, Throttle);
                        Go("vmax", i, "Max level speed run: full power, level."); }
                    else if (dt <= 15) cruiseFuel0 = i.FuelFrac;
                    return;
                case "vmax":
                    Throttle = 1; Pitch = Level(i); Bank = 0;
                    if (dt > 10 && i.T - vmaxT >= 10) { if (i.Speed - vmaxV < 1 || dt > 600) { P.Perf.MaxLevel = i.Speed; P.Note("max_level_speed", i.Speed, i.Mass); ratesSpeed = RatesSpeed(i.StallGuess); ratesSteady = double.NaN; Go("ratesprep", i, "Slowing to " + ratesSpeed.ToString("0") + " m/s for the roll/pitch rate checks (not at max speed)."); return; } vmaxV = i.Speed; vmaxT = i.T; }
                    if (dt <= 10) { vmaxV = i.Speed; vmaxT = i.T; }
                    return;
                case "ratesprep":
                    {   // step 6 setup: never pull at vmax (Luke 14:18 log: 1.5 s at 60% elevator from 300 m/s = 8.6 g, test aborted)
                        if (double.IsNaN(ratesSpeed)) ratesSpeed = RatesSpeed(i.StallGuess);
                        SpeedTarget = ratesSpeed; Throttle = i.Speed > ratesSpeed + 10 ? 0 : .6; Pitch = Level(i, ClimbAlt); Bank = 0;   // back to the 5 km test altitude
                        if (Math.Abs(i.Speed - ratesSpeed) < 10) { if (double.IsNaN(ratesSteady)) ratesSteady = i.T; } else ratesSteady = double.NaN;
                        if ((!double.IsNaN(ratesSteady) && i.T - ratesSteady >= 3) || dt > RatesPrepTimeout) { bool late = dt > RatesPrepTimeout; ratesElev = 0; ratesCut = false; Go("rates", i, (late ? "Could not slow to " + ratesSpeed.ToString("0") + " m/s in " + RatesPrepTimeout.ToString("0") + " s - running the rate checks at " + i.Speed.ToString("0") + " m/s instead. " : "") + "Roll and pitch rate checks at " + i.Speed.ToString("0") + " m/s: full roll, G-limited pull (cut at " + Math.Round(RatesG * 100) + "% of the g limit)."); }
                        return;
                    }
                case "rates":
                    {   // 0-2 s full right, 2-4 s full left, 4-7 s neutral (level), 7-8.5 s pull, 8.5-10 s relax; g-limited
                        Throttle = .8; if (!double.IsNaN(ratesSpeed)) SpeedTarget = ratesSpeed; P.Perf.RollRate = Math.Max(double.IsNaN(P.Perf.RollRate) ? 0 : P.Perf.RollRate, dt < 4 ? Math.Abs(i.RollRate) : 0);
                        if (dt < 2) Aileron = 1; else if (dt < 4) Aileron = -1; else if (dt < 7) { Bank = 0; Pitch = Level(i); }
                        else if (dt < 8.5) { Bank = 0; Elevator = RatesElevator(ratesElev, tick, i.G, i.GLimit, ref ratesCut); ratesElev = Elevator; P.Perf.PitchRate = Math.Max(double.IsNaN(P.Perf.PitchRate) ? 0 : P.Perf.PitchRate, Math.Abs(i.PitchRate)); }
                        else { Bank = 0; Pitch = Level(i); }
                        if (dt >= 10) { P.Note("roll_rate", P.Perf.RollRate, i.Mass); P.Note("pitch_rate", P.Perf.PitchRate, i.Mass);
                            if (!double.IsNaN(P.Perf.RollRate)) P.Curves.RollRate.Add(P.Perf.RollRate, i.Mass, i.Speed); if (!double.IsNaN(P.Perf.PitchRate)) P.Curves.PitchRate.Add(P.Perf.PitchRate, i.Mass, i.Speed); idx = 0; turnPass = 0; passSpeed = TurnSpeed(i.Speed, i.StallGuess); passBest = null; P.Turns.Clear(); P.Envelope.Clear(); Go("turnprep", i, "Max safe turn: settling level at " + passSpeed.ToString("0") + " m/s (cruise) with power before banking."); }
                        return;
                    }
                case "turn":
                    {
                        int b = Banks[idx]; Bank = b;
                        // MAX SAFE TURN (Luke 10:06 AM): level turns (VS ~0), back-pressure sets the g, full power as needed to hold the pass speed;
                        // pass 0 at cruise, pass 1 at ~approach speed (1.5 Vs). Stops at 80% structure g, stress/heat, 1.3 Vs, or altitude not held.
                        if (double.IsNaN(passSpeed)) passSpeed = turnPass == 0 ? TurnSpeed(v0, i.StallGuess) : 1.5 * i.StallGuess;
                        double vT = passSpeed;
                        SpeedTarget = vT;   // ONE throttle authority: the controller's speed PI (full power as needed); no learn Throttle
                        Pitch = TurnPitchCmd(i, b, a0);   // altitude hold + load-factor back-pressure for the bank
                        // controls must track (Luke 10:33 / 11:06). Throttle fault -> step invalid. Nose error only counts once the bank is
                        // reached, and only ends the bank after an earlier bank succeeded; otherwise TurnAltLoss (150 m) judges altitude.
                        if (!double.IsNaN(reachAt) && Math.Abs(Pitch - i.Pitch) > BadPitchDeg) badPitch += tick; else badPitch = Math.Max(0, badPitch - tick);
                        if (ThrottleNotTracking(i, vT, tick)) badThr += tick; else badThr = Math.Max(0, badThr - tick);
                        if (badThr > BadThrS)
                        {
                            string what = "throttle " + Math.Round(i.Throttle * 100) + "% while " + Math.Round(vT - i.Speed) + " m/s slow";
                            passBest = null; passSpeed = double.NaN; turnPass = 0; badPitch = badThr = 0; if (!Redo.Contains(7)) Redo.Add(7);
                            Go("recover", i, "Turn data invalid (" + what + " - controls not tracking): envelope NOT saved, step 7 marked for redo. Leveling off with power."); return;
                        }
                        if (badPitch > BadPitchS)
                        {
                            string what = "pitch cmd " + Pitch.ToString("0") + " act " + i.Pitch.ToString("0"); badPitch = 0;
                            if (passBest != null) { EndPass(i, "can't hold altitude", " - limit at " + b + " deg (" + what + ")"); return; }
                        }
                        bool nearLimit = (i.GLimit > 0 && gF > TurnStop * i.GLimit) || stF > TurnStop;   // filtered G / stress (no single-frame spikes); checked during the roll-in too
                        bool bankOk = Math.Abs(Math.Abs(i.Bank) - b) < 2.5;
                        if (!nearLimit && double.IsNaN(reachAt) && !bankOk && i.T - t0 >= ReachTimeout)
                        {   // bank never arrived: end the pass here, no average of a half-rolled turn
                            EndPass(i, "bank " + b + " deg not reached in " + ReachTimeout.ToString("0") + " s", ""); return;
                        }
                        if (!nearLimit && !Reached(i, bankOk)) return;
                        if (double.IsNaN(reachAt)) reachAt = i.T;
                        thrSum += double.IsNaN(i.Throttle) ? 0 : i.Throttle; spdSum += i.Speed; gSum += i.G; trSum += Math.Abs(i.TurnRate); crSum += Math.Abs(i.CourseRate); stMax = Math.Max(stMax, i.Stress); n++;
                        bool slow = i.Speed < 1.3 * i.StallGuess, sinking = a0 - i.Alt > TurnAltLoss || (i.T - reachAt >= TurnSeg && badPitch > 2);   // short dwell: a bank whose nose is already not tracking at the end is not "good"
                        if (i.T - reachAt >= TurnSeg || nearLimit || slow || sinking)
                        {
                            double rate = trSum / Math.Max(1, n), radius = rate > .01 ? i.Speed / (rate * Math.PI / 180) : double.NaN, rollIn = reachAt - t0;
                            double crate = crSum / Math.Max(1, n), vMean = n > 0 ? spdSum / n : i.Speed;
                            var row = new double[] { b, gSum / Math.Max(1, n), rate, a0 - i.Alt, stMax, i.Mass, thrSum / Math.Max(1, n), vT, radius, rollIn, crate, vMean };
                            P.Turns.Add(row); P.Note("turn_throttle_" + b, row[6], i.Mass);
                            P.Curves.TurnG.Add(row[1], i.Mass, b, vT); P.Curves.TurnRate.Add(rate, i.Mass, b, vT); P.Curves.CourseRate.Add(crate, i.Mass, b, vT);
                            if (!double.IsNaN(radius)) P.Curves.TurnRadius.Add(radius, i.Mass, b, vT);
                            bool fail = nearLimit || slow || sinking;
                            if (!fail) passBest = row;
                            idx++;
                            if (fail || idx >= Banks.Length) EndPass(i, nearLimit ? "near the g/stress limit" : slow ? "speed down to 1.3x stall" : sinking ? "can't hold altitude" : "all banks done", "");
                            else Go("turn", i, "Turn " + Banks[idx] + " deg bank.");
                        }
                        return;
                    }
                case "turnprep":
                    {
                        double vT = TurnPrepSpeed(i); Bank = 0; Pitch = Level(i, ClimbAlt); SpeedTarget = vT;   // turns fly at the 5 km test altitude   // speed PI only
                        if ((Math.Abs(i.Speed - vT) < Math.Max(5, .03 * vT) && Math.Abs(i.Alt - ClimbAlt) < 400) || dt > TurnPrepTimeout) { idx = 0; Go("turn", i, "Turn 10 deg bank at " + vT.ToString("0") + " m/s."); }
                        return;
                    }
                case "recover":
                    Throttle = 1; Pitch = Level(i); Bank = 0;
                    if (dt >= 12)
                    {
                        if (idx < 100) { idx = 100; Go("dive", i, "dives at idle, 5 to 20 deg nose down."); }
                        else if (idx - 100 < DivePitch.Length && i.Alt > 2500) Go("dive", i, "Dive " + DivePitch[idx - 100] + " deg nose down.");
                        else Go("sinkprep", i, "sink map - back to 5 km, slow to " + SinkSpeedFactor + "x stall.");
                    }
                    return;
                case "dive":
                    {
                        int p = DivePitch[idx - 100]; Throttle = 0; Bank = 0; double gam = i.Fpa;   // dives target the FLIGHT-PATH angle (not nose pitch)
                        Pitch = FpaPitch(i, -p, tick);
                        if (!Reached(i, Math.Abs((double.IsNaN(gam) ? i.Pitch : gam) + p) < 2) && i.Alt >= 2000 && i.Speed <= 320) { v0 = i.Speed; return; }
                        if (n == 0) v0 = i.Speed; gSum += i.Vs; n++;
                        if (i.T - reachAt >= DiveSeg || i.Alt < 2000 || i.Speed > 320) { P.Dives.Add(new double[] { p, -gSum / Math.Max(1, n), i.Speed - v0, i.Mass }); idx++; Go("recover", i, null); }
                        return;
                    }
                case "sinkprep":
                    if (i.Alt < SinkAlt - 200) { Throttle = 1; Pitch = LevelOff(i, SinkAlt, PilotPolicy.ClimbBackPitch(i.Speed, i.StallGuess)); SampleClimb(i); }   // climb-back: ~30 deg, less near 1.3x stall
                    else { Throttle = .5; Pitch = SpeedPitch(i, SinkSpeedFactor); FilterVs(i); Throttle = Math.Max(Throttle, SinkLimit(i)); }
                    Bank = 0; if (double.IsNaN(sinkStart)) sinkStart = i.T;
                    if ((i.Alt >= SinkAlt - 200 && i.Speed <= (SinkSpeedFactor + .03) * i.StallGuess) || dt > 300) { sinkThr = 0; overAt = double.NaN; sinkStart = i.T; Go("sinkdown", i, "Sink map: throttle cut until the sink passes 10 m/s."); }
                    return;
                case "climbback":
                    Throttle = 1; Pitch = LevelOff(i, ClimbAlt, PilotPolicy.ClimbBackPitch(i.Speed, i.StallGuess)); Bank = 0; SampleClimb(i);   // climb-back / sinkclimb: 5 km (ClimbAlt == SinkAlt)
                    if (i.Alt >= ClimbAlt - 100 || dt > 600) Go("wait", i, "Back at 5 km. Waiting: check a test box to run it; you can fly manually any time.");
                    return;
                case "wait":
                    SpeedTarget = double.IsNaN(waitSpeed) ? WaitSpeed(i) : waitSpeed;   // pitch/bank/throttle: the normal altitude + heading + speed hold
                    if (Queue.Count > 0) { int code = Queue[0]; Queue.RemoveAt(0); StartTest(code, i); }
                    return;
                case "sinkclimb":
                    Throttle = 1; Pitch = LevelOff(i, ClimbAlt, PilotPolicy.ClimbBackPitch(i.Speed, i.StallGuess)); Bank = 0; SampleClimb(i);   // climb-back / sinkclimb: 5 km (ClimbAlt == SinkAlt)
                    if (i.Alt >= SinkAlt - 100) { string r = resume; double keepT = stepThr; Go(r, i, "Sink map resumed."); stepThr = keepT; stepAt = i.T; vs0 = vsF; lagDone = false; lagS = double.NaN; prevT = i.T; }
                    return;
                case "sinkdown":
                case "sinkmap":
                    {
                        Pitch = SpeedPitch(i, SinkSpeedFactor); Bank = 0;
                        if (!double.IsNaN(sinkStart) && i.T - sinkStart > SinkCap) { SinkDone(i, "Sink map: 10 min cap reached, keeping " + SinkTbl.Count + " points. Level at 4 km for the stall test."); return; }
                        if (i.Speed < 1.15 * i.StallGuess) { SinkDone(i, "Sink map stopped (speed under 1.15x stall); " + SinkTbl.Count + " points. level at 4 km for the stall test."); return; }
                        if (i.Alt < 4000) { resume = Phase; Go("sinkclimb", i, "Sink map paused below 4 km: climbing back to 5 km, then resuming at " + Math.Round((Phase == "sinkdown" ? sinkThr : stepThr) * 100) + "% throttle."); return; }
                        FilterVs(i);
                        if (Phase == "sinkdown")
                        {   // Luke 11:28 PM: cut the throttle until the sink is MORE than 10 m/s (held 3 s). No soft floor here (true idle
                            // reference); below 4 km the climb-back takes over.
                            sinkThr = 0; Throttle = 0; limThr = 0;
                            if (-vsF > 10.5) { if (double.IsNaN(overAt)) overAt = i.T; } else overAt = double.NaN;
                            if ((!double.IsNaN(overAt) && i.T - overAt >= 3) || dt > 300)
                            { idx = SinkTargets.Length - 1; stepThr = 0; Go("sinkmap", i, "Sink map: throttle up 1% every 5 s, recording 10..0 m/s sink."); stepThr = 0; prevT = stepAt = i.T; vs0 = vsF; lagDone = false; lagS = double.NaN; }
                            return;
                        }
                        // +1 %, wait 5 s (throttle lag), repeat; record each target as the sink comes down through it
                        if (double.IsNaN(stepThr)) stepThr = 0;
                        if (idx >= SinkTargets.Length) idx = SinkTargets.Length - 1;
                        if (i.T - prevT >= RampWait) { stepThr = Math.Min(1, stepThr + .01); prevT = stepAt = i.T; vs0 = vsF; lagDone = false; lagS = double.NaN; }
                        bool settled = i.T - stepAt >= SinkSettle && stepThr != recThr;   // one target per throttle step, after the step settles
                        Throttle = Math.Max(stepThr, SinkLimit(i));
                        if (!lagDone && Math.Abs(vsF - vs0) > .2) { lagDone = true; lagS = i.T - stepAt; }
                        if (settled && idx >= 0 && -vsF <= SinkTargets[idx])
                        {
                            recThr = stepThr;
                            SinkTbl.Add(new[] { SinkTargets[idx], stepThr, lagDone ? lagS : RampWait, i.Mass, i.Pitch }); P.Note("sink_thr@" + SinkTargets[idx], stepThr, i.Mass);
                            P.Curves.Sink.Add(SinkTargets[idx], i.Mass, stepThr, i.Speed, flapPass ? 3 : 0); if (lagDone) P.Curves.ThrottleLag.Add(lagS, i.Mass, .01); idx--;
                        }
                        lastVsF = vsF;
                        if (idx < 0) SinkDone(i, "Sink map done (" + SinkTbl.Count + " points). Level at 4 km for the stall test.");
                        else if (stepThr >= 1 && i.T - prevT >= RampWait) SinkDone(i, "Sink map: full power reached at " + (-vsF).ToString("0.0") + " m/s sink; " + SinkTbl.Count + " points.");
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
                        if (broke || dt > 90) { if (stallFlapPass) { P.FlapStall = minV; P.Note("flap_stall", minV, i.Mass); P.Curves.Stall.Add(minV, i.Mass, 3); } else { P.Stall = minV; P.Note("stall", P.Stall, i.Mass); P.Curves.Stall.Add(minV, i.Mass, 0); } Go("stallrec", i, "Stall at " + minV.ToString("0") + " m/s. Recovering: nose down, full power, wings level."); }
                        return;
                    }
                case "stallrec":
                    Throttle = 1; Bank = 0; minAlt = Math.Min(minAlt, i.Alt);
                    Pitch = i.Speed < 1.3 * P.Stall ? -10 : Level(i);
                    if ((i.Vs > 0 && i.Speed > 1.3 * P.Stall && dt > 3) || dt > 60) {
                        if (stallFlapPass) { P.FlapStallAltLoss = a0 - minAlt; P.Note("flap_stall_alt_loss", P.FlapStallAltLoss, i.Mass); stallFlapPass = false; EndTest(i); }
                        else { P.StallAltLoss = a0 - minAlt; P.Note("stall_alt_loss", P.StallAltLoss, i.Mass);
                            if (!ManualMode && i.HasFlaps && P.FlapStallSub && i.FuelFrac > .55 && i.Alt > 2500) { stallFlapPass = true; Go("stallprep", i, "Optional: stall again with flaps FULL."); } else EndTest(i); }
                    }
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

namespace KSPChatBridge
{
    internal enum FlapKind { Pitch, Flap, RollFlap, Other }
    /// <summary>Flap rules (Luke 9:45 AM).</summary>
    internal static class FlapsPolicy
    {
        /// <summary>Dedicated flap: pitch/yaw/roll all disabled. Roll-only: may deploy as a flap and keep rolling. Anything with pitch: never.</summary>
        internal static FlapKind Classify(bool pitch, bool yaw, bool roll)
        {
            if (pitch) return FlapKind.Pitch;
            if (!yaw && !roll) return FlapKind.Flap;
            if (roll && !yaw) return FlapKind.RollFlap;
            return FlapKind.Other;
        }
        internal static double Fraction(int setting) { return setting <= 0 ? 0 : setting >= 3 ? 1 : setting / 3.0; }
        internal static string Name(int s) { return s <= 0 ? "UP" : s >= 3 ? "FULL" : s.ToString(); }
        /// <summary>Flap speed limit: learned, else 1.6x clean stall.</summary>
        internal static double SpeedLimit(double cleanStall, double learned) { return !double.IsNaN(learned) && learned > 0 ? learned : 1.6 * (double.IsNaN(cleanStall) || cleanStall <= 0 ? 60 : cleanStall); }
        /// <summary>Highest setting allowed at this speed: 0 above the limit (5 m/s hysteresis to keep what is out), else full.</summary>
        internal static int MaxAllowed(double ias, double limit, int current) { return ias <= limit || (current > 0 && ias <= limit + 5) ? 3 : 0; }
        /// <summary>Approach schedule: up until short final; 1 inside 2x the short-final fix, 2 inside it, full inside half of it and in the flare/rollout.</summary>
        internal static int ApproachSetting(string phase, double distM, double shortFix)
        {
            if (phase == "flare" || phase == "rollout" || phase == "stopped") return 3;
            if (phase != "final" && phase != "intercept") return 0;
            if (distM < .5 * shortFix) return 3; if (distM < shortFix) return 2; if (distM < 2 * shortFix) return 1; return 0;
        }
    }
}
