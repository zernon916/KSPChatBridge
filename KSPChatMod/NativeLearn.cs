using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>LEARN THIS PLANE (Luke 7:06 PM): drives LearnFlight, saves one unified profile per craft name, lands at KSC 27.</summary>
    public partial class NativeFlightController
    {
        LearnFlight learn, lastLearn; int learnParts; double learnPrevFuel = double.NaN, learnPrevHdg = double.NaN, learnPrevT = double.NaN; string learnLast = "";
        PlaneProfile profile;
        internal static string LearnStatus { get { return instance == null || instance.learn == null ? "" : "LEARN " + instance.learn.Phase.ToUpperInvariant() + ": " + instance.learn.Status; } }

        string LearnPlaneCmd(Dictionary<string, object> a)
        {
            if (vessel == null) return "No active vessel.";
            if (liftArea <= 0) return "LEARN THIS PLANE is for winged planes only.";
            if (learn != null && !learn.Done) return "Test flight already running: " + learn.Status;
            learn = new LearnFlight(); lastLearn = null; MfdNav.AboutPage = AboutTitles.Length - 1; learnParts = vessel.parts.Count; learnPrevFuel = learnPrevHdg = learnPrevT = double.NaN; learnLast = "";
            ChatLog.Write("learn", "test flight start: " + vessel.vesselName + " " + vessel.totalMass.ToString("0.0") + " t");
            if (vessel.LandedOrSplashed) { altitude = vessel.altitude + 300; heading = FlightGlobals.ship_heading; BeginHold(); return "Test flight: " + StartTakeoff(); }
            BeginHold(); return "Test flight started from the air.";
        }

        void FuelState(out double frac, out double total)
        {
            double amt = 0, max = 0;
            foreach (Part p in vessel.parts) foreach (PartResource r in p.Resources) if (r.resourceName == "LiquidFuel" || r.resourceName == "Oxidizer") { amt += r.amount; max += r.maxAmount; }
            frac = max > 0 ? amt / max : 1; total = amt;
        }

        void LearnTick(FlightCtrlState c, double dt, double pitch)
        {
            double frac, total; FuelState(out frac, out total);
            double t = Planetarium.GetUniversalTime(), hdg = FlightGlobals.ship_heading;
            double rate = double.IsNaN(learnPrevFuel) || t <= learnPrevT ? 0 : Math.Max(0, (learnPrevFuel - total) / (t - learnPrevT));
            double tr = double.IsNaN(learnPrevHdg) || t <= learnPrevT ? 0 : FlightPolicy.Wrap(hdg - learnPrevHdg) / (t - learnPrevT);
            learnPrevFuel = total; learnPrevHdg = hdg; learnPrevT = t;
            double stress = 0; foreach (Part p in vessel.parts) { if (p.maxTemp > 0) stress = Math.Max(stress, p.temperature / p.maxTemp); if (p.skinMaxTemp > 0) stress = Math.Max(stress, p.skinTemperature / p.skinMaxTemp); }
            var i = BuildIn(t, frac, rate, tr, stress, pitch);
            learn.Step(i);
            if (learn.Status != learnLast) { learnLast = learn.Status; ChatLog.Write("learn", learn.Phase + ": " + learn.Status); ChatWindow.Notice("[LEARN] " + learn.Status); }
            DoneCheck();
            if (learn == null || learn.Phase == "takeoff" || mode == "takeoff") return;
            holdSpeed = false; directVs = null;
            directPitch = double.IsNaN(learn.Pitch) ? (double?)null : learn.Pitch;
            directBank = double.IsNaN(learn.Bank) || !double.IsNaN(learn.Aileron) ? (double?)null : learn.Bank;
        }
        Dictionary<string, double> resPrev = new Dictionary<string, double>();
        LearnIn BuildIn(double t, double frac, double rate, double tr, double stress, double pitch)
        {
            double thr = 0, thrMax = 0; var res = new Dictionary<string, double>();
            foreach (Part p in vessel.parts) { foreach (PartModule pm in p.Modules) { var en = pm as ModuleEngines; if (en != null && en.EngineIgnited) { thr += en.finalThrust; thrMax += en.maxThrust; } } foreach (PartResource r in p.Resources) { double v; res.TryGetValue(r.resourceName, out v); res[r.resourceName] = v + r.amount; } }
            var used = new List<string>(); foreach (var kv in res) { double pv; if (resPrev.TryGetValue(kv.Key, out pv) && kv.Value < pv - 1e-6 && kv.Key != "ElectricCharge" && kv.Key != "IntakeAir") used.Add(kv.Key); } resPrev = res;
            var av = vessel.angularVelocity;
            return new LearnIn { T = t, Speed = vessel.srfSpeed, Alt = vessel.altitude, Agl = vessel.radarAltitude, Vs = vessel.verticalSpeed, Pitch = pitch, G = vessel.geeForce, GLimit = ApproachProfile.StructG,
                FuelFrac = frac, FuelRate = rate, Stress = stress, TurnRate = tr, Throttle = vessel.ctrlState.mainThrottle, Liftoff = takeoff != null ? takeoff.LiftoffSpeed : double.NaN, StallGuess = stall, Mass = vessel.totalMass,
                Landed = vessel.LandedOrSplashed, PartLost = vessel.parts.Count < learnParts,
                GroundSpeed = vessel.horizontalSrfSpeed, RollRate = av.y * 180 / Math.PI, PitchRate = av.x * 180 / Math.PI, Trim = vessel.ctrlState.pitchTrim, ThrustFrac = thrMax > 0 ? thr / thrMax : 0, Res = string.Join(",", used.ToArray()) };
        }
        void DoneCheck()
        {
            if (learn.Done)
            {
                SaveProfile(learn.P); var done = learn; lastLearn = learn; learn = null; holdSpeed = true; directPitch = null; directBank = null; directVs = null;
                ChatLog.Write("learn", "profile " + vessel.vesselName + ": " + done.P.Summary());
                ChatWindow.Notice(Command("land", new Dictionary<string, object> { { "where", "ksc 27" } }));
            }
        }

        void LearnApply(FlightCtrlState c)
        {
            if (learn == null && lastLearn != null && !lastLearn.LandingDone && vessel != null)
            {   // step 12: landing rollout distance
                double fr, tot; FuelState(out fr, out tot);
                if (lastLearn.TrackLanding(BuildIn(Planetarium.GetUniversalTime(), fr, 0, 0, 0, 0))) { SaveProfile(lastLearn.P); ChatLog.Write("learn", "rollout " + lastLearn.P.Perf.Rollout.ToString("0") + " m"); ChatWindow.Notice("[LEARN] Landing rollout " + lastLearn.P.Perf.Rollout.ToString("0") + " m. Profile complete."); }
                return;
            }
            if (learn == null || learn.Phase == "takeoff" || mode == "takeoff") return;
            if (!double.IsNaN(learn.Throttle)) c.mainThrottle = (float)learn.Throttle;
            if (!double.IsNaN(learn.Aileron)) c.roll = (float)learn.Aileron;
            if (!double.IsNaN(learn.Elevator)) c.pitch = (float)learn.Elevator;
        }

        Dictionary<string, object> Bucket(string key) { object o; var d = settingsData.TryGetValue(key, out o) ? o as Dictionary<string, object> : null; if (d == null) settingsData[key] = d = new Dictionary<string, object>(); return d; }

        void SaveProfile(PlaneProfile p)
        {
            p.Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            string n = vessel.vesselName; Bucket("plane_profiles")[n] = p.ToDict();
            if (!double.IsNaN(p.Stall)) Bucket("stall_speeds")[n] = Math.Round(p.Stall, 1);
            if (!double.IsNaN(p.Liftoff)) { Bucket("liftoff_speeds")[n] = Math.Round(p.Liftoff, 1); liftoffSpeed = p.Liftoff; }
            if (!double.IsNaN(p.Lag)) { Bucket("throttle_lag")[n] = Math.Round(p.Lag, 2); shold.SetLag(p.Lag); lagMeasured = true; }
            if (!double.IsNaN(p.Decel) && p.Decel > 0) Decel.Learn(n, 1.6 * (double.IsNaN(p.Stall) ? stall : p.Stall), p.Decel, false);
            profile = p; if (!double.IsNaN(p.Stall)) stallGuess = p.Stall; stall = LearnedStall();
            try { Save(); } catch (Exception) { }
        }

        void LoadProfile()
        {
            object o; var all = settingsData.TryGetValue("plane_profiles", out o) ? o as Dictionary<string, object> : null; object po = null; if (all != null) all.TryGetValue(vessel.vesselName, out po);
            profile = PlaneProfile.FromDict(po as Dictionary<string, object>);
            if (profile == null) return;
            double m = vessel.totalMass, s = profile.StallAt(m);
            if (!double.IsNaN(s)) stallGuess = s;
            if (!double.IsNaN(profile.Lag)) shold.SetLag(profile.Lag);
            ChatLog.Write("vessel", "plane profile (" + profile.Date + ", test " + profile.M0.ToString("0.0") + " t, now " + m.ToString("0.0") + " t): stall " + (double.IsNaN(s) ? "-" : s.ToString("0")) + " m/s scaled; " + profile.Summary());
        }

        /// <summary>Approach/flare throttle from the learned sink map (NaN without one).</summary>
        double SinkThrottle(double targetVs) { return profile == null || vessel == null ? double.NaN : profile.ThrottleForSink(-targetVs, vessel.totalMass); }
        internal static readonly string[] AboutTitles = { "SUMMARY", "SPEEDS/STALL", "TURNS/DIVES/SINK", "FUEL/PERF", "LEARN" };
        static string F0(double v) { return double.IsNaN(v) ? "-" : v.ToString("0"); }
        static string F1(double v) { return double.IsNaN(v) ? "-" : v.ToString("0.0"); }
        static string F2(double v) { return double.IsNaN(v) ? "-" : v.ToString("0.00"); }
        /// <summary>ABOUT THIS PLANE, paged (Luke 8:30 PM): 1 SUMMARY, 2 SPEEDS/STALL, 3 TURNS/DIVES/SINK, 4 FUEL/PERF, 5 LEARN.
        /// Source tags: TEST = learn flight (scaled to this mass), LIVE = measured in normal flight, EST = estimate.</summary>
        internal static List<string> AboutLines()
        {
            var o = new List<string>(); var me = instance; int pg = Math.Max(0, Math.Min(AboutTitles.Length - 1, MfdNav.AboutPage));
            o.Add("ABOUT " + (pg + 1) + "/" + AboutTitles.Length + " " + AboutTitles[pg] + "   < >");
            if (me == null || me.vessel == null) { o.Add("No active vessel."); return o; }
            var p = me.profile; double m = me.vessel.totalMass; var lf = me.learn ?? me.lastLearn;
            string tag = p != null ? "TEST" : "EST";
            switch (pg)
            {
                case 0:
                    o.Add(me.vessel.vesselName + "  " + m.ToString("0.0") + " t  " + CraftClass.Label(me.CraftCls, me.craftOverride));
                    o.Add(p == null ? "No test flight yet: press LEARN." : "Tested " + p.Date + " at " + p.M0.ToString("0.0") + " t" + (p.Complete ? "" : " (partial: " + p.Abort + ")"));
                    o.Add("Stall " + F0(me.stall) + "  Approach " + F0(ApproachProfile.AppSpeed(me.stall)) + "  Liftoff " + F0(p != null && !double.IsNaN(p.Liftoff) ? p.LiftoffAt(m) : me.liftoffSpeed) + " m/s");
                    if (p != null) { double fr, tot; me.FuelState(out fr, out tot); o.Add("Range " + F0(p.Perf.RangeKm(fr)) + " km  Endurance " + F0(p.Perf.EnduranceMin(fr)) + " min (now " + Math.Round(fr * 100) + "% fuel)"); }
                    if (lf != null) o.Add("Learn: step " + Math.Min(lf.StepNo, LearnFlight.StepCount) + "/" + LearnFlight.StepCount + (lf.Done ? (lf.Aborted ? " aborted" : " done") : "") + " (page 5)");
                    break;
                case 1:
                    string ts = p != null && !double.IsNaN(p.Stall) ? "TEST" : me.learner != null && me.learner.Confidence > .5 ? "LIVE " + Math.Round(me.learner.Confidence * 100) + "%" : "EST";
                    o.Add("Stall    " + F0(me.stall) + " m/s " + ts + (p != null && !double.IsNaN(p.StallAltLoss) ? ", recovery -" + F0(p.StallAltLoss) + " m" : ""));
                    o.Add("Liftoff  " + F0(p != null && !double.IsNaN(p.Liftoff) ? p.LiftoffAt(m) : me.liftoffSpeed) + " m/s " + (p != null && !double.IsNaN(p.Liftoff) ? "TEST" : double.IsNaN(me.liftoffSpeed) ? "EST" : "LIVE"));
                    o.Add("Approach " + F0(ApproachProfile.AppSpeed(me.stall)) + " m/s (1.35 Vs)");
                    o.Add("Econ     " + (p == null || double.IsNaN(p.EconSpeed) ? "-" : PlaneProfile.ScaleSpeed(p.EconSpeed, p.M0, m).ToString("0") + " m/s @ " + Math.Round(p.EconThrottle * 100) + "%") + "  Vmax lvl " + F0(p == null ? double.NaN : p.Perf.MaxLevel));
                    double dl = Decel.Estimate(me.vessel.vesselName, 150, 100, true);
                    o.Add("Accel " + F2(p == null ? double.NaN : p.AccelAt(m)) + "  Decel " + F2(p != null && !double.IsNaN(p.Decel) ? p.DecelAt(m) : dl) + " m/s2 " + (p != null ? "TEST" : double.IsNaN(dl) ? "EST" : "LIVE"));
                    o.Add("Throttle lag " + me.shold.Lag.ToString("0.0") + " s " + (p != null && !double.IsNaN(p.Lag) ? "TEST" : me.lagMeasured ? "LIVE" : "EST") + "  spool " + F1(p == null ? double.NaN : p.Perf.Spool) + " s  inertia x" + me.shold.K.ToString("0.0"));
                    break;
                case 2:
                    if (p == null) { o.Add("No test data."); break; }
                    if (p.Turns.Count > 0) { double mg = 0; foreach (var x in p.Turns) mg = Math.Max(mg, x[1]); var lt = p.Turns[p.Turns.Count - 1]; o.Add("Turns to " + lt[0] + " deg, max " + mg.ToString("0.0") + " g, " + lt[2].ToString("0.0") + " deg/s, alt " + (-lt[3]).ToString("+0;-0") + " m" + (lt.Length > 6 && !double.IsNaN(lt[6]) ? ", thr " + Math.Round(lt[6] * 100) + "%" : "")); }
                    o.Add("Roll rate " + F0(p.Perf.RollRate) + " deg/s  Pitch rate " + F0(p.Perf.PitchRate) + " deg/s");
                    if (p.Dives.Count > 0) { var s = ""; foreach (var x in p.Dives) s += x[0] + ":" + x[1].ToString("0") + " "; o.Add("Dive sink (deg:m/s) " + s.Trim()); }
                    if (p.Sink.Count > 0) { var ss = new List<double[]>(p.Sink); ss.Sort((x, y) => x[0].CompareTo(y[0])); var s2 = ""; double lg = 0; foreach (var x in ss) { s2 += x[0].ToString("0") + ":" + Math.Round(p.ThrottleForSink(x[0], m) * 100) + "% "; lg = Math.Max(lg, x[2]); } o.Add("Sink map (m/s:thr) " + s2.Trim()); o.Add("  sink lag <= " + lg.ToString("0.0") + " s"); }
                    break;
                case 3:
                    if (p == null) { o.Add("No test data."); break; }
                    var pf = p.Perf; double fr2, tot2; me.FuelState(out fr2, out tot2);
                    foreach (var c in new[] { "takeoff", "climb", "level", "descent", "idle", "turns" }) { double pm = pf.PctPerMin(c), pk = pf.PctPerKm(c); if (!double.IsNaN(pm)) o.Add(c.PadRight(8) + F1(pm) + " %/min " + F2(pk) + " %/km"); }
                    o.Add("Climb 5 km: " + F0(pf.ClimbTime) + " s, " + F1(pf.ClimbFuel * 100) + "% fuel; ceiling ~" + F1(pf.CeilingKm()) + " km");
                    o.Add("Cruise " + F0(pf.CruiseSpeed) + " m/s @ " + (double.IsNaN(pf.CruiseThrottle) ? "-" : Math.Round(pf.CruiseThrottle * 100) + "%") + ", trim " + F2(pf.CruiseTrim));
                    o.Add("Range " + F0(pf.RangeKm(fr2)) + " km  Endur " + F0(pf.EnduranceMin(fr2)) + " min  Reserve 100 km " + F1(pf.ReserveFrac(100) * 100) + "%");
                    o.Add("Ground roll " + F0(pf.GroundRoll) + " m  Rollout " + F0(pf.Rollout) + " m");
                    foreach (var kv in pf.Res) o.Add("  " + kv.Key + ": " + string.Join(",", new List<string>(kv.Value).ToArray()));
                    break;
                default:
                    if (lf == null) { o.Add("No test flight running. LEARN starts one."); break; }
                    o.AddRange(lf.Checklist());
                    break;
            }
            return o;
        }
    }
}
