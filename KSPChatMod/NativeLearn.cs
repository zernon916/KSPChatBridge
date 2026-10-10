using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>LEARN THIS PLANE (Luke 7:06 PM): drives LearnFlight, saves one unified profile per craft name, lands at KSC 27.</summary>
    public partial class NativeFlightController
    {
        LearnFlight learn; int learnParts; double learnPrevFuel = double.NaN, learnPrevHdg = double.NaN, learnPrevT = double.NaN; string learnLast = "";
        PlaneProfile profile;
        internal static string LearnStatus { get { return instance == null || instance.learn == null ? "" : "LEARN " + instance.learn.Phase.ToUpperInvariant() + ": " + instance.learn.Status; } }

        string LearnPlaneCmd(Dictionary<string, object> a)
        {
            if (vessel == null) return "No active vessel.";
            if (liftArea <= 0) return "LEARN THIS PLANE is for winged planes only.";
            if (learn != null && !learn.Done) return "Test flight already running: " + learn.Status;
            learn = new LearnFlight(); learnParts = vessel.parts.Count; learnPrevFuel = learnPrevHdg = learnPrevT = double.NaN; learnLast = "";
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
            var i = new LearnIn { T = t, Speed = vessel.srfSpeed, Alt = vessel.altitude, Agl = vessel.radarAltitude, Vs = vessel.verticalSpeed, Pitch = pitch, G = vessel.geeForce, GLimit = ApproachProfile.StructG,
                FuelFrac = frac, FuelRate = rate, Stress = stress, TurnRate = tr, Throttle = vessel.ctrlState.mainThrottle, Liftoff = takeoff != null ? takeoff.LiftoffSpeed : double.NaN, StallGuess = stall, Mass = vessel.totalMass,
                Landed = vessel.LandedOrSplashed, PartLost = vessel.parts.Count < learnParts };
            learn.Step(i);
            if (learn.Status != learnLast) { learnLast = learn.Status; ChatLog.Write("learn", learn.Phase + ": " + learn.Status); ChatWindow.Notice("[LEARN] " + learn.Status); }
            if (learn.Done)
            {
                SaveProfile(learn.P); var done = learn; learn = null; holdSpeed = true; directPitch = null; directBank = null; directVs = null;
                ChatLog.Write("learn", "profile " + vessel.vesselName + ": " + done.P.Summary());
                ChatWindow.Notice(Command("land", new Dictionary<string, object> { { "where", "ksc 27" } }));
                return;
            }
            if (learn.Phase == "takeoff" || mode == "takeoff") return;
            holdSpeed = false; directVs = null;
            directPitch = double.IsNaN(learn.Pitch) ? (double?)null : learn.Pitch;
            directBank = double.IsNaN(learn.Bank) ? (double?)null : learn.Bank;
        }

        void LearnApply(FlightCtrlState c)
        {
            if (learn == null || learn.Phase == "takeoff" || mode == "takeoff") return;
            if (!double.IsNaN(learn.Throttle)) c.mainThrottle = (float)learn.Throttle;
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

        /// <summary>ABOUT THIS PLANE page (Luke 7:10 PM): value + source/confidence (TEST = learn flight scaled to this mass, LIVE = measured in normal flight, EST = estimate).</summary>
        internal static List<string> AboutLines()
        {
            var o = new List<string>(); var me = instance;
            if (me == null || me.vessel == null) { o.Add("No active vessel."); return o; }
            var p = me.profile; double m = me.vessel.totalMass;
            Func<double, string> f0 = v => double.IsNaN(v) ? "  -  " : v.ToString("0"); Func<double, string> f2 = v => double.IsNaN(v) ? " - " : v.ToString("0.00");
            o.Add(me.vessel.vesselName + "  " + m.ToString("0.0") + " t");
            o.Add(p == null ? "No test flight yet: LEARN on AIRCRAFT." : "Test " + p.Date + " at " + p.M0.ToString("0.0") + " t" + (p.Complete ? "" : " (partial: " + p.Abort + ")"));
            string ts = p != null && !double.IsNaN(p.Stall) ? "TEST" : me.learner != null && me.learner.Confidence > .5 ? "LIVE " + Math.Round(me.learner.Confidence * 100) + "%" : "EST";
            o.Add("Liftoff " + f0(p != null && !double.IsNaN(p.Liftoff) ? p.LiftoffAt(m) : me.liftoffSpeed) + " m/s  " + (p != null && !double.IsNaN(p.Liftoff) ? "TEST" : double.IsNaN(me.liftoffSpeed) ? "EST" : "LIVE"));
            o.Add("Stall   " + f0(me.stall) + " m/s  " + ts);
            o.Add("Approach " + f0(ApproachProfile.AppSpeed(me.stall)) + " m/s  (1.35 Vs)");
            o.Add("Econ    " + (p == null || double.IsNaN(p.EconSpeed) ? "  -  " : PlaneProfile.ScaleSpeed(p.EconSpeed, p.M0, m).ToString("0") + " m/s @ " + Math.Round(p.EconThrottle * 100) + "%") + (p == null ? "" : "  TEST"));
            double dl = Decel.Estimate(me.vessel.vesselName, 150, 100, true);
            o.Add("Accel " + f2(p == null ? double.NaN : p.AccelAt(m)) + "  Decel " + f2(p != null && !double.IsNaN(p.Decel) ? p.DecelAt(m) : dl) + " m/s2  " + (p != null ? "TEST" : double.IsNaN(dl) ? "EST" : "LIVE"));
            o.Add("Throttle lag " + me.shold.Lag.ToString("0.0") + " s  " + (p != null && !double.IsNaN(p.Lag) ? "TEST" : me.lagMeasured ? "LIVE" : "EST") + "  inertia x" + me.shold.K.ToString("0.0"));
            if (p != null && p.Turns.Count > 0) { var last = p.Turns[p.Turns.Count - 1]; double mg = 0; foreach (var x in p.Turns) mg = Math.Max(mg, x[1]); o.Add("Turns to " + last[0] + " deg, max " + mg.ToString("0.0") + " g, " + last[2].ToString("0.0") + " deg/s"); }
            if (p != null && p.Dives.Count > 0) { var s = ""; foreach (var x in p.Dives) s += x[0] + ":" + x[1].ToString("0") + " "; o.Add("Sink (deg:m/s) " + s.Trim()); }
            if (p != null && !double.IsNaN(p.StallAltLoss)) o.Add("Stall recovery loses " + p.StallAltLoss.ToString("0") + " m");
            return o;
        }
    }
}
