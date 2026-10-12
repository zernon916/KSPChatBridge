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
        /// <summary>False when fingerprint hard-mismatch: keep profile for ABOUT but do not trust stall schedule / turn envelope.</summary>
        bool profileTrusted = true;
        CraftFingerprint.Match profileMatch = CraftFingerprint.Match.Missing;
        string pendingCarryFrom;
        internal static string LearnStatus { get { return instance == null || instance.learn == null ? "" : "LEARN " + instance.learn.Phase.ToUpperInvariant() + ": " + instance.learn.Status; } }

        string LearnPlaneCmd(Dictionary<string, object> a)
        {
            if (vessel == null) return "No active vessel.";
            if (liftArea <= 0) return "LEARN THIS PLANE is for winged planes only.";
            if (learn != null && !learn.Done) return "Test flight already running: " + learn.Status;
            // Oct 11 manual test picker: take off, climb, wait (holding altitude + heading); checked boxes run one test each, then back to waiting
            var queue = profile != null ? new List<int>(profile.Rerun) : new List<int>(); if (profile != null) profile.Rerun.Clear();
            learn = new LearnFlight(profile, queue, true); lastLearn = null; learnRates.Reset(); learnTestsSaved = 0; learnPilotUntil = 0; learnPilotHad = false; MfdNav.AboutPage = AboutTitles.Length - 1; learnParts = vessel.parts.Count; learnPrevFuel = learnPrevHdg = learnPrevT = double.NaN; learnLast = "";
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
            Vector3d sv = vessel.srf_velocity; double hs = vessel.horizontalSrfSpeed;
            double course = hs > 1 ? (Math.Atan2(Vector3d.Dot(sv, vessel.east), Vector3d.Dot(sv, vessel.north)) * 180 / Math.PI + 360) % 360 : double.NaN;
            learnRates.Update(t, hdg, course, pitch, Roll());   // heading rate = wrapTo180(dHdg)/dt, course rate from the velocity vector, roll/pitch rates
            double tr = learnRates.HeadingRate;
            learnPrevFuel = total; learnPrevHdg = hdg; learnPrevT = t;
            double stress = 0; foreach (Part p in vessel.parts) { if (p.maxTemp > 0) stress = Math.Max(stress, p.temperature / p.maxTemp); if (p.skinMaxTemp > 0) stress = Math.Max(stress, p.skinTemperature / p.skinMaxTemp); }
            var i = BuildIn(t, frac, rate, tr, stress, pitch);
            learn.Step(i);
            if (learn.TestsDone != learnTestsSaved) { learnTestsSaved = learn.TestsDone; profile = learn.P; SaveProfile(learn.P); ChatLog.Write("learn", "test done, profile saved: " + learn.P.Summary()); }
            if (learn.Status != learnLast) { learnLast = learn.Status; ChatLog.Write("learn", learn.Phase + ": " + learn.Status); ChatWindow.Notice("[LEARN] " + learn.Status); }
            DoneCheck();
            if (learn == null || learn.Phase == "takeoff" || mode == "takeoff") return;
            if (learn.Phase != learnPhaseSeen)
            {   // Luke 10:38: every control override is reset between learn steps (no elevator/throttle/integrator carry-over)
                learnPhaseSeen = learn.Phase; pitchIntegral = vsIntegral = 0; pitchShape.Reset(pitch); elevator = vessel.ctrlState.pitch; bankOverride = false;
                if (!double.IsNaN(gate.Out)) FlightInputHandler.state.mainThrottle = (float)gate.Out;
                ChatLog.Write("learn", "step " + learn.Phase + ": control overrides reset (thr " + Math.Round((double.IsNaN(gate.Out) ? 0 : gate.Out) * 100) + "%)");
                if (learn.Phase == "wait" && !double.IsNaN(learn.HoldAlt))
                {   // waiting: the normal hold flies altitude + heading (+ speed via the PI)
                    altitude = learn.HoldAlt; heading = double.IsNaN(learn.HoldHdg) ? hdg : learn.HoldHdg; holdAltitude = holdHeading = true; capture = null;
                    ChatLog.Write("learn", "waiting: hold " + altitude.ToString("0") + " m hdg " + heading.ToString("000"));
                }
            }
            directVs = null;
            if (!double.IsNaN(learn.SpeedTarget)) { holdSpeed = true; speed = learn.SpeedTarget; } else holdSpeed = false;   // speed steps fly the ONE speed PI
            directPitch = double.IsNaN(learn.Pitch) ? (double?)null : learn.Pitch;
            directBank = double.IsNaN(learn.Bank) || !double.IsNaN(learn.Aileron) ? (double?)null : learn.Bank;
            bankOverride = directBank.HasValue;   // learn banks are the test's own (G/stall-guarded): no slow/decel bank clamp (that held every turn at 5 deg)
            if (learn.Phase != "wait" && t >= learnTraceNext)
            {   // commanded vs actual every 2 s: nose pitch (attitude) AND flight-path angle, heading/course/roll/pitch rates
                learnTraceNext = t + 2; double fpa = PilotPolicy.FlightPathAngle(vessel.verticalSpeed, hs);
                ChatLog.Write("learn", learn.Phase + " cmd bank " + (double.IsNaN(learn.Bank) ? "-" : learn.Bank.ToString("0")) + " act " + Roll().ToString("0") + ", cmd pitch " + (double.IsNaN(learn.Pitch) ? "-" : learn.Pitch.ToString("0")) + " nose " + pitch.ToString("0.0") + " fpa " + fpa.ToString("0.0")
                    + ", hdg rate " + learnRates.HeadingRate.ToString("0.0") + " course rate " + learnRates.CourseRate.ToString("0.0") + " roll rate " + learnRates.RollRate.ToString("0.0") + " pitch rate " + learnRates.PitchRate.ToString("0.0")
                    + ", thr " + ((double.IsNaN(gate.Out) ? vessel.ctrlState.mainThrottle : gate.Out) * 100).ToString("0.00") + "%, spd " + vessel.srfSpeed.ToString("0"));
            }
        }
        double learnTraceNext; string learnPhaseSeen = "";
        readonly RateMeter learnRates = new RateMeter(); int learnTestsSaved; float learnPilotUntil; bool learnPilotHad;
        /// <summary>Manual learn wait: the pilot is flying (stick keys within the last 2 s); the controller does not touch the controls.</summary>
        bool LearnPilotFlying { get { return learn != null && learn.Waiting && Time.realtimeSinceStartup < learnPilotUntil; } }
        /// <summary>Called from Update with the manual-input flag. Returns true when the input was absorbed by the learn wait (no Stop()).</summary>
        bool LearnPilotInput(bool manual)
        {
            if (learn == null || !learn.Waiting) { learnPilotHad = false; return false; }
            if (manual)
            {
                if (!learnPilotHad) { ChatWindow.Notice("[LEARN] You have the controls. The hold resumes (here) when you let go; check a box to run a test."); ChatLog.Write("learn", "pilot took the controls while waiting"); }
                learnPilotHad = true; learnPilotUntil = Time.realtimeSinceStartup + 2; return true;
            }
            if (learnPilotHad && Time.realtimeSinceStartup >= learnPilotUntil)
            {   // pilot let go: hold where they left it
                learnPilotHad = false; altitude = vessel.altitude; heading = FlightGlobals.ship_heading; learn.HoldAlt = altitude; learn.HoldHdg = heading; capture = null;
                pitchIntegral = vsIntegral = 0; elevator = vessel.ctrlState.pitch; gate.Seed(vessel.ctrlState.mainThrottle); shold.Reset();
                ChatLog.Write("learn", "pilot released: hold " + altitude.ToString("0") + " m hdg " + heading.ToString("000"));
            }
            return false;
        }
        Dictionary<string, double> resPrev = new Dictionary<string, double>();
        LearnIn BuildIn(double t, double frac, double rate, double tr, double stress, double pitch)
        {
            double thr = 0, thrMax = 0; var res = new Dictionary<string, double>();
            foreach (Part p in vessel.parts) { foreach (PartModule pm in p.Modules) { var en = pm as ModuleEngines; if (en != null && en.EngineIgnited) { thr += en.finalThrust; thrMax += en.maxThrust; } } foreach (PartResource r in p.Resources) { double v; res.TryGetValue(r.resourceName, out v); res[r.resourceName] = v + r.amount; } }
            var used = new List<string>(); foreach (var kv in res) { double pv; if (resPrev.TryGetValue(kv.Key, out pv) && kv.Value < pv - 1e-6 && kv.Key != "ElectricCharge" && kv.Key != "IntakeAir") used.Add(kv.Key); } resPrev = res;
            return new LearnIn { T = t, Speed = vessel.srfSpeed, Alt = vessel.altitude, Agl = vessel.radarAltitude, Vs = vessel.verticalSpeed, Pitch = pitch, G = vessel.geeForce, GLimit = ApproachProfile.StructG,
                FuelFrac = frac, FuelRate = rate, Stress = stress, TurnRate = tr, CourseRate = learnRates.CourseRate, Heading = FlightGlobals.ship_heading, Throttle = double.IsNaN(gate.Out) ? vessel.ctrlState.mainThrottle : gate.Out, Liftoff = takeoff != null ? takeoff.LiftoffSpeed : double.NaN, StallGuess = PilotPolicy.TasStall(stall, vessel.atmDensity), Rho = vessel.atmDensity, Mass = vessel.totalMass,
                Landed = vessel.LandedOrSplashed, PartLost = vessel.parts.Count < learnParts,
                HasFlaps = flaps.Count > 0, Bank = Roll(), GroundSpeed = vessel.horizontalSrfSpeed, RollRate = learnRates.RollRate, PitchRate = learnRates.PitchRate, Trim = vessel.ctrlState.pitchTrim, ThrustFrac = thrMax > 0 ? thr / thrMax : 0, Res = string.Join(",", used.ToArray()) };
        }
        void DoneCheck()
        {
            if (learn.Done)
            {
                if (!learn.Aborted) learn.P.Rerun.Clear(); foreach (int k in learn.Redo) if (!learn.P.Rerun.Contains(k)) learn.P.Rerun.Add(k); profile = learn.P;
                SaveProfile(learn.P); var done = learn; lastLearn = learn; learn = null; holdSpeed = true; directPitch = null; directBank = null; directVs = null;
                ChatLog.Write("learn", "profile " + vessel.vesselName + ": " + done.P.Summary());
                bankOverride = false; pendingCircle = null; learnPhaseSeen = "";   // no learn bank override survives into the landing (held -5 deg in hold)
                StartLearnLanding();
            }
        }

        /// <summary>After the test (complete or aborted): ferry + approach to KSC 27, verified; one retry straight into land_plane if it did not engage.</summary>
        void StartLearnLanding()
        {
            var args = new Dictionary<string, object> { { "where", "ksc 27" } };
            string r; try { r = Command("land", args); } catch (Exception ex) { r = "land failed: " + ex.Message; }
            ChatWindow.Notice(r);
            if (!PilotPolicy.LandingEngaged(mode, pendingLand != null))
            {
                ChatLog.Write("learn", "landing did not engage (mode " + mode + "): " + r + "; retrying land_plane KSC 27");
                try { r = Command("land_plane", new Dictionary<string, object>(args)); } catch (Exception ex) { r = "land_plane failed: " + ex.Message; }
                ChatWindow.Notice(r);
            }
            ChatLog.Write("learn", "after test: " + (pendingLand != null ? "ferry" : mode) + " to KSC 27, stall " + stall.ToString("0") + " (est " + StallEstimate().ToString("0") + ")");
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
            if (!double.IsNaN(learn.Throttle) && double.IsNaN(learn.SpeedTarget)) { c.mainThrottle = (float)learn.Throttle; throttleKind = learn.TestThrottle ? "test" : "pi"; throttleWhy = "learn " + learn.Phase; }
            if (!double.IsNaN(learn.Aileron)) c.roll = (float)learn.Aileron;
            if (!double.IsNaN(learn.Elevator)) c.pitch = (float)learn.Elevator;
        }

        Dictionary<string, object> Bucket(string key) { object o; var d = settingsData.TryGetValue(key, out o) ? o as Dictionary<string, object> : null; if (d == null) settingsData[key] = d = new Dictionary<string, object>(); return d; }

        /// <summary>Dry mass / lift / parts / root / engines — fuel-independent shape for profile safety checks.</summary>
        static CraftFingerprint CaptureFingerprint(Vessel v)
        {
            if (v == null) return null;
            double res = 0, lift = 0; int eng = 0; bool intake = false;
            foreach (Part p in v.parts)
            {
                foreach (PartResource r in p.Resources) if (r != null && r.info != null) res += r.amount * r.info.density;
                foreach (PartModule pm in p.Modules)
                {
                    var ls = pm as ModuleLiftingSurface; if (ls != null) lift += ls.deflectionLiftCoeff;
                    var en = pm as ModuleEngines; if (en == null) continue; eng++;
                    foreach (var pr in en.propellants) if (pr.name == "IntakeAir") intake = true;
                }
            }
            return new CraftFingerprint
            {
                DryMass = Math.Max(0, v.totalMass - res), Lift = lift, Parts = v.parts.Count, Engines = eng, Intake = intake,
                Root = v.rootPart != null ? (v.rootPart.partInfo != null ? v.rootPart.partInfo.name : v.rootPart.name) : "",
                LearnedName = v.vesselName ?? ""
            };
        }

        /// <summary>learned=false (checkbox toggles): keep the stored fingerprint and the current trust; only a LEARN write re-stamps.</summary>
        /// <summary>Reject junk stalls (below max(0.4x estimate, liftoff/2) or above liftoff): keep the estimate, log it (live 20:20: 8.6 m/s).</summary>
        void CheckStall(PlaneProfile p)
        {
            if (p == null || double.IsNaN(p.Stall)) return;
            double est = StallEstimate(), lo = double.IsNaN(p.Liftoff) ? liftoffSpeed : p.Liftoff;
            if (PilotPolicy.StallPlausible(p.Stall, est, lo)) return;
            ChatLog.Write("learn", "stall " + p.Stall.ToString("0.0") + " m/s rejected (estimate " + est.ToString("0") + ", liftoff " + (double.IsNaN(lo) ? "-" : lo.ToString("0")) + ") - keeping the estimate");
            p.Stall = double.NaN; p.StallAltLoss = double.NaN;
        }        void SaveProfile(PlaneProfile p, bool learned = true)
        {
            CheckStall(p); p.Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            // Fingerprint only at LEARN write (and step toggles on the same craft) — never refreshed from Bind alone.
            if (learned && vessel != null) { p.Fingerprint = CaptureFingerprint(vessel); if (p.Fingerprint != null) p.Fingerprint.LearnedName = vessel.vesselName; }
            string n = vessel.vesselName; Bucket("plane_profiles")[n] = p.ToDict();
            if (!double.IsNaN(p.Stall)) Bucket("stall_speeds")[n] = Math.Round(p.Stall, 1);
            if (!double.IsNaN(p.Liftoff)) { Bucket("liftoff_speeds")[n] = Math.Round(p.Liftoff, 1); liftoffSpeed = p.Liftoff; }
            if (!double.IsNaN(p.Lag)) { Bucket("throttle_lag")[n] = Math.Round(p.Lag, 2); shold.SetLag(p.Lag); lagMeasured = true; }
            if (!double.IsNaN(p.Decel) && p.Decel > 0) Decel.Learn(n, 1.6 * (double.IsNaN(p.Stall) ? stall : p.Stall), p.Decel, false);
            profile = p; if (learned) { profileTrusted = true; profileMatch = CraftFingerprint.Match.Ok; pendingCarryFrom = null; }
            CheckStall(p); if (!double.IsNaN(p.Stall)) stallGuess = p.Stall; stall = LearnedStall();
            ApplyProfileTrust();
            try { Save(); } catch (Exception) { }
        }

        void LoadProfile()
        {
            pendingCarryFrom = null; profileTrusted = true; profileMatch = CraftFingerprint.Match.Missing;
            object o; var all = settingsData.TryGetValue("plane_profiles", out o) ? o as Dictionary<string, object> : null;
            object po = null; if (all != null) all.TryGetValue(vessel.vesselName, out po);
            profile = PlaneProfile.FromDict(po as Dictionary<string, object>);
            var now = CaptureFingerprint(vessel);
            if (profile == null)
            {   // renamed plane: same fingerprint under another name → offer carry (do not auto-copy)
                if (all != null && now != null)
                    foreach (var kv in all)
                    {
                        if (string.Equals(kv.Key, vessel.vesselName, StringComparison.OrdinalIgnoreCase)) continue;
                        var cand = PlaneProfile.FromDict(kv.Value as Dictionary<string, object>);
                        if (cand == null || cand.Fingerprint == null || !CraftFingerprint.RenameMatch(cand.Fingerprint, now)) continue;
                        pendingCarryFrom = kv.Key;
                        ChatWindow.Notice("[LEARN] Renamed craft? Profile \"" + kv.Key + "\" matches this plane. Say \"carry profile\" (or carry_plane_profile) to use it under \"" + vessel.vesselName + "\".");
                        ChatLog.Write("vessel", "profile carry offer from \"" + kv.Key + "\" -> \"" + vessel.vesselName + "\"");
                        break;
                    }
                ApplyProfileTrust();
                return;
            }
            profileMatch = CraftFingerprint.Compare(profile.Fingerprint, now);
            if (profileMatch == CraftFingerprint.Match.Hard)
            {
                profileTrusted = false;
                ChatWindow.Notice("[LEARN] Craft looks different from the saved profile (mass/wings/parts). Re-run LEARN, or keep flying with conservative defaults.");
                ChatLog.Write("vessel", "profile HARD mismatch for " + vessel.vesselName + ": " + CraftFingerprint.Label(profileMatch));
            }
            else if (profileMatch == CraftFingerprint.Match.Soft)
            {
                ChatWindow.Notice("[LEARN] Craft looks a bit different from the saved profile — consider re-LEARN if handling feels off.");
                ChatLog.Write("vessel", "profile soft mismatch for " + vessel.vesselName);
            }
            else if (profileMatch == CraftFingerprint.Match.Missing)
                ChatLog.Write("vessel", "profile has no fingerprint (legacy) for " + vessel.vesselName + " — using it; next LEARN will stamp one");
            double m = vessel.totalMass, s = profile.StallAt(m);
            if (!double.IsNaN(s)) stallGuess = s;
            if (!double.IsNaN(profile.Lag)) shold.SetLag(profile.Lag);
            if (!double.IsNaN(profile.Accel)) shold.SetTargetSlew(profile.AccelAt(m));
            ApplyProfileTrust();
            ChatLog.Write("vessel", "plane profile (" + profile.Date + ", test " + profile.M0.ToString("0.0") + " t, now " + m.ToString("0.0") + " t, match " + CraftFingerprint.Label(profileMatch) + "): stall " + (double.IsNaN(s) ? "-" : s.ToString("0")) + " m/s scaled; " + profile.Summary());
        }

        /// <summary>TrustedStall + envelope only when the craft still matches the LEARN fingerprint.</summary>
        void ApplyProfileTrust()
        {
            bool trust = profile != null && profileTrusted;
            ApproachProfile.TrustedStall = trust && !double.IsNaN(profile.Stall) && profile.Stall > 0;
            if (trust && profile.Envelope.Count > 0) { var pr = profile; ApproachProfile.SafeBank = v => pr.SafeBankAt(v); ApproachProfile.RollIn = pr.RollIn; }
            else { ApproachProfile.SafeBank = null; ApproachProfile.RollIn = 0; }
        }

        /// <summary>Copy a fingerprint-matched profile onto this vessel name (rename carry-over).</summary>
        string CarryPlaneProfileCmd(Dictionary<string, object> a)
        {
            if (vessel == null) return "No active vessel.";
            string from = Str(a, "from", pendingCarryFrom ?? "");
            if (from.Length == 0) return "No renamed profile to carry. Switch to a renamed craft that matches a saved fingerprint first.";
            object o; var all = settingsData.TryGetValue("plane_profiles", out o) ? o as Dictionary<string, object> : null;
            object po = null; if (all == null || !all.TryGetValue(from, out po)) return "No saved profile named \"" + from + "\".";
            var src = PlaneProfile.FromDict(po as Dictionary<string, object>);
            if (src == null) return "Saved profile \"" + from + "\" is unreadable.";
            var now = CaptureFingerprint(vessel);
            if (src.Fingerprint != null && now != null && !CraftFingerprint.RenameMatch(src.Fingerprint, now) && !Bool(a, "force", false))
                return "Profile \"" + from + "\" does not match this craft closely enough. Re-run LEARN, or pass force=true to copy anyway.";
            if (src.Fingerprint != null) src.Fingerprint.LearnedName = vessel.vesselName;
            // Copy under the new name without re-stamping fingerprint from a non-LEARN path: write dict then LoadProfile.
            Bucket("plane_profiles")[vessel.vesselName] = src.ToDict();
            if (!double.IsNaN(src.Stall)) Bucket("stall_speeds")[vessel.vesselName] = Math.Round(src.Stall, 1);
            if (!double.IsNaN(src.Liftoff)) Bucket("liftoff_speeds")[vessel.vesselName] = Math.Round(src.Liftoff, 1);
            if (!double.IsNaN(src.Lag)) Bucket("throttle_lag")[vessel.vesselName] = Math.Round(src.Lag, 2);
            pendingCarryFrom = null;
            try { Save(); } catch (Exception) { }
            LoadProfile(); ConfigureInertia(); stall = LearnedStall();
            ChatLog.Write("learn", "carried profile \"" + from + "\" -> \"" + vessel.vesselName + "\"");
            return "Carried LEARN profile from \"" + from + "\" to \"" + vessel.vesselName + "\".";
        }

        /// <summary>Approach/flare throttle from the learned sink map (NaN without one).</summary>
        double SinkThrottle(double targetVs) { return profile == null || vessel == null ? double.NaN : profile.ThrottleForSink(-targetVs, vessel.totalMass); }
        /// <summary>LEARN page cursor/toggle (Luke 11:26 PM). True when the cursor keys are in toggle mode (no flight running).</summary>
        internal static bool LearnToggleMode { get { var me = instance; return me != null && MfdNav.AboutPage == AboutTitles.Length - 1 && ((me.learn != null && me.learn.ManualMode && !me.learn.Done) || (me.profile != null && (me.learn == null || me.learn.Done) && (me.lastLearn == null || me.lastLearn.LandingDone || me.learn == null))); } }
        internal static string ToggleLearnStep(int step)
        {
            return ToggleLearnCode(step);
        }
        /// <summary>Clean Learn slate for a new vessel object (revert, scene reload, vessel switch): the old state machine, its landing
        /// hand-off, direct overrides and the MFD checklist cursor all reset so the menu can never be left pointing at a dead Learn.</summary>
        void ResetLearnState()
        {
            if (learn != null || lastLearn != null) ChatLog.Write("learn", "Learn state reset (new vessel object: revert / reload / switch)");
            learn = null; lastLearn = null; learnTestsSaved = 0; learnPilotUntil = 0; learnPilotHad = false; learnRates.Reset();
            directPitch = null; directBank = null; directVs = null; bankOverride = false; pendingCircle = null; learnPhaseSeen = "";
            MfdNav.ResetLearnMenu();
        }
        internal static string ToggleLearnCode(int step)
        {
            var me = instance; if (me == null) return "No active vessel.";
            if (me.learn != null && !me.learn.Done)
            {   // manual test picker: checking a box runs that one test (then back to waiting)
                if (!me.learn.ManualMode) return "Test flight running.";
                string r = me.learn.Request(step); ChatLog.Write("learn", "box " + LearnFlight.CodeName(step) + ": " + r); return r;
            }
            if (me.profile == null) return "Press LEARN: it takes off, climbs and waits; then check a box to run that test.";
            if (!LearnFlight.Toggle(me.profile, step)) { MfdNav.LearnCursor = LearnFlight.NextToggleable(MfdNav.LearnCursor); return LearnFlight.CodeName(step) + " is always flown (setup/landing) - cursor moved to " + LearnFlight.CodeName(LearnFlight.Codes[MfdNav.LearnCursor]) + "."; }
            me.SaveProfile(me.profile, false);   // a toggle is not a LEARN: no fingerprint re-stamp (would hide a hard mismatch)
            return LearnFlight.CodeName(step) + (me.profile.Rerun.Contains(step) ? " checked: runs after the LEARN climb." : " unchecked.");
        }
        internal static string LearnSkip()
        {
            var me = instance; if (me == null || me.learn == null) return "No test flight running.";
            if (me.learn.Done) return "Can't skip the landing.";
            string r = me.learn.Skip(); ChatLog.Write("learn", r); return r;
        }
        /// <summary>Base ABOUT pages; curve graph pages are inserted before LEARN when the profile has data.</summary>
        internal static readonly string[] AboutBase = { "SUMMARY", "SPEEDS/STALL", "TURNS/DIVES/SINK", "FUEL/PERF", "LIVE" };
        readonly LiveStats live = new LiveStats(); float liveNext; Vessel liveVessel;
        /// <summary>ABOUT LIVE page samples (4 Hz, from Update): fuel burn, time to empty, horizontal / vertical acceleration.</summary>
        void LiveTick()
        {
            if (MfdNav.VesselName == null) MfdNav.VesselName = () => FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.vesselName : null;
            if (vessel == null || Time.realtimeSinceStartup < liveNext) return; liveNext = Time.realtimeSinceStartup + .25f;
            if (liveVessel != vessel) { liveVessel = vessel; live.Reset(); }
            double fr, tot; FuelState(out fr, out tot);
            live.Update(Planetarium.GetUniversalTime(), tot, vessel.horizontalSrfSpeed, vessel.verticalSpeed);
        }
        internal static string[] AboutTitles
        {
            get
            {
                var o = new List<string>(AboutBase);
                var me = instance; var pr = me != null ? me.profile : null;
                if (pr != null) foreach (var c in pr.Curves.Graphable()) o.Add("CURVE:" + c.Name);
                o.Add("LEARN");
                return o.ToArray();
            }
        }
        static string F0(double v) { return double.IsNaN(v) ? "-" : v.ToString("0"); }
        static string F1(double v) { return double.IsNaN(v) ? "-" : v.ToString("0.0"); }
        static string F2(double v) { return double.IsNaN(v) ? "-" : v.ToString("0.00"); }
        /// <summary>ABOUT THIS PLANE, paged (Luke 8:30 PM): 1 SUMMARY, 2 SPEEDS/STALL, 3 TURNS/DIVES/SINK, 4 FUEL/PERF, 5 LIVE (burn, time to empty, attitude, throttle, accels, mass), GRAPH pages, LEARN last.
        /// Source tags: TEST = learn flight (scaled to this mass), LIVE = measured in normal flight, EST = estimate.</summary>
        internal static List<string> AboutLines()
        {
            var titles = AboutTitles;
            var o = new List<string>(); var me = instance; int pg = Math.Max(0, Math.Min(titles.Length - 1, MfdNav.AboutPage));
            string title = titles[pg];
            string vname = me != null && me.vessel != null ? me.vessel.vesselName : "";
            o.Add(MfdNav.AboutLabel(vname) + "  " + (pg + 1) + "/" + titles.Length + " " + title.Replace("CURVE:", "GRAPH ") + "   < >");
            if (me == null || me.vessel == null) { o.Add("No active vessel."); return o; }
            var p = me.profile; double m = me.vessel.totalMass; var lf = me.learn ?? me.lastLearn;
            string tag = p != null ? "TEST" : "EST";
            if (title.StartsWith("CURVE:") && p != null)
            {
                string cname = title.Substring(6);
                PerfCurve curve = null; foreach (var c in p.Curves.All) if (c.Name == cname) { curve = c; break; }
                if (curve == null || curve.Empty) { o.Add("No data."); return o; }
                o.Add(curve.YLabel + " vs " + curve.XLabel + " (" + curve.Points.Count + " pts, scale " + curve.Scale + ")");
                var series = curve.Series1D();
                if (series.Count == 0) { o.Add("(multi-var samples - see table)"); }
                foreach (var s in series) o.Add("  " + s[0].ToString("0.##") + " -> " + s[1].ToString("0.##"));
                o.Add("[outside MFD draws the graph; IVA shows this table]");
                return o;
            }
            int basePg = title == "LEARN" ? 99 : Array.IndexOf(AboutBase, title);
            switch (basePg)
            {
                case 0:
                    o.Add(me.vessel.vesselName + "  " + m.ToString("0.0") + " t  " + CraftClass.Label(me.CraftCls, me.craftOverride));
                    o.Add(p == null ? "No test flight yet: press LEARN." : "Tested " + p.Date + " at " + p.M0.ToString("0.0") + " t" + (p.Complete ? "" : " (partial: " + p.Abort + ")"));
                    if (p != null) o.Add("Profile " + CraftFingerprint.Label(me.profileMatch) + (me.profileTrusted ? "" : " (curves off)") + (me.pendingCarryFrom != null ? "  carry from \"" + me.pendingCarryFrom + "\"?" : ""));
                    else if (me.pendingCarryFrom != null) o.Add("No profile under this name — match \"" + me.pendingCarryFrom + "\" (say carry profile)");
                    o.Add("Stall " + F0(me.stall) + "  Approach " + F0(ApproachProfile.AppSpeed(me.stall)) + "  Liftoff " + F0(p != null && !double.IsNaN(p.Liftoff) ? p.LiftoffAt(m) : me.liftoffSpeed) + " m/s");
                    if (p != null) { double fr, tot; me.FuelState(out fr, out tot); o.Add("Range " + F0(p.Perf.RangeKm(fr)) + " km  Endurance " + F0(p.Perf.EnduranceMin(fr)) + " min (now " + Math.Round(fr * 100) + "% fuel)"); }
                    if (lf != null) o.Add("Learn: step " + Math.Min(lf.StepNo, LearnFlight.StepCount) + "/" + LearnFlight.StepCount + (lf.Done ? (lf.Aborted ? " aborted" : " done") : "") + " (page 5)");
                    break;
                case 1:
                    string ts = p != null && !double.IsNaN(p.Stall) ? "TEST" : me.learner != null && me.learner.Confidence > .5 ? "LIVE " + Math.Round(me.learner.Confidence * 100) + "%" : "EST";
                    o.Add("Stall    " + F0(me.stall) + " m/s " + ts + (p != null && !double.IsNaN(p.StallAltLoss) ? ", recovery -" + F0(p.StallAltLoss) + " m" : ""));
                    o.Add("Liftoff  " + F0(p != null && !double.IsNaN(p.Liftoff) ? p.LiftoffAt(m) : me.liftoffSpeed) + " m/s " + (p != null && !double.IsNaN(p.Liftoff) ? "TEST" : double.IsNaN(me.liftoffSpeed) ? "EST" : "LIVE"));
                    o.Add("Approach " + F0(ApproachProfile.AppSpeed(me.stall)) + " m/s (1.35 Vs)");
                    if (me.flaps.Count > 0 || me.farFlaps) o.Add(FlapLine + (p != null && !double.IsNaN(p.FlapStall) ? ", flaps-full stall " + F0(PlaneProfile.ScaleSpeed(p.FlapStall, p.M0, m)) + " TEST (" + (p.FlapStall - p.Stall).ToString("+0;-0") + ")" : ""));
                    o.Add("Speed cap " + F0(PilotPolicy.CapSpeed) + " m/s below 6 km (plane " + F0(CapPlane) + " " + CapSource + (CapHot ? ", HOT -10%" : "") + ")");
                    o.Add("Econ     " + (p == null || double.IsNaN(p.EconSpeed) ? "-" : PlaneProfile.ScaleSpeed(p.EconSpeed, p.M0, m).ToString("0") + " m/s @ " + Math.Round(p.EconThrottle * 100) + "%") + "  Vmax lvl " + F0(p == null ? double.NaN : p.Perf.MaxLevel));
                    double dl = Decel.Estimate(me.vessel.vesselName, 150, 100, true);
                    o.Add("Accel " + F2(p == null ? double.NaN : p.AccelAt(m)) + "  Decel " + F2(p != null && !double.IsNaN(p.Decel) ? p.DecelAt(m) : dl) + " m/s2 " + (p != null ? "TEST" : double.IsNaN(dl) ? "EST" : "LIVE"));
                    o.Add("Throttle lag " + me.shold.Lag.ToString("0.0") + " s " + (p != null && !double.IsNaN(p.Lag) ? "TEST" : me.lagMeasured ? "LIVE" : "EST") + "  spool " + F1(p == null ? double.NaN : p.Perf.Spool) + " s  inertia x" + me.shold.K.ToString("0.0"));
                    break;
                case 2:
                    if (p == null) { o.Add("No test data."); break; }
                    if (p.Turns.Count > 0) { double mg = 0; foreach (var x in p.Turns) mg = Math.Max(mg, x[1]); var lt = p.Turns[p.Turns.Count - 1]; o.Add("Turns to " + lt[0] + " deg, max " + mg.ToString("0.0") + " g, " + lt[2].ToString("0.0") + " deg/s, alt " + (-lt[3]).ToString("+0;-0") + " m" + (lt.Length > 6 && !double.IsNaN(lt[6]) ? ", thr " + Math.Round(lt[6] * 100) + "%" : "")); }
                    foreach (var e in p.Envelope) o.Add("Safe turn @" + F0(e[0]) + " m/s: bank " + F0(e[1]) + ", " + F1(e[2]) + " g, " + F1(e[3]) + " deg/s, r " + F1(e[4] / 1000) + " km, roll-in " + F1(e[5]) + " s");
                    o.Add("Roll rate " + F0(p.Perf.RollRate) + " deg/s  Pitch rate " + F0(p.Perf.PitchRate) + " deg/s");
                    if (p.Dives.Count > 0) { var s = ""; foreach (var x in p.Dives) s += x[0] + ":" + x[1].ToString("0") + " "; o.Add("Dive sink (deg:m/s) " + s.Trim()); }
                    if (p.SinkFlaps.Count > 0) { var s3 = ""; foreach (var x in p.SinkFlaps) { double clean = p.ThrottleForSink(x[0], p.M0); s3 += x[0].ToString("0") + ":" + Math.Round(x[1] * 100) + "%" + (double.IsNaN(clean) ? "" : "(" + Math.Round((x[1] - clean) * 100).ToString("+0;-0") + ")") + " "; } o.Add("Flaps sink map (m/s:thr, vs clean) " + s3.Trim()); }
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
                case 4:
                    {   // LIVE (Oct 11)
                        var v = me.vessel; var L = me.live; double fr4, tot4; me.FuelState(out fr4, out tot4);
                        double pctMin = tot4 > 0 && fr4 > 0 ? L.Burn / (tot4 / fr4) * 6000 : double.NaN;
                        o.Add("LIVE  " + v.vesselName);
                        o.Add("Fuel burn " + F2(L.Burn) + " u/s (" + F2(pctMin) + " %/min), " + F0(tot4) + " u left");
                        o.Add("Empty in  " + LiveStats.Duration(L.TimeToEmpty) + " at this burn");
                        o.Add("Pitch " + F1(me.Pitch()) + "  Roll " + F1(me.Roll()) + "  Hdg " + FlightGlobals.ship_heading.ToString("000.0"));
                        o.Add("Throttle " + (v.ctrlState.mainThrottle * 100.0).ToString("0.00") + "%  Speed " + F1(v.srfSpeed) + " m/s");
                        o.Add("Horiz " + (L.HAccel >= 0 ? "accel " : "decel ") + Math.Abs(L.HAccel).ToString("0.00") + " m/s2  Vert accel " + L.VAccel.ToString("+0.00;-0.00") + " m/s2");
                        o.Add("Mass " + v.totalMass.ToString("0.000") + " t");
                        break;
                    }
                default:
                    if (lf != null && lf.ManualMode && !lf.Done) { o.AddRange(lf.ManualChecklist(MfdNav.LearnCursor)); break; }
                    if (lf != null && (!lf.Done || !lf.LandingDone)) { o.AddRange(lf.Checklist()); break; }
                    if (p == null) { o.Add("No test flight yet. LEARN starts one."); break; }
                    o.AddRange(LearnFlight.ProfileChecklist(p, MfdNav.LearnCursor));
                    break;
            }
                        return o;
        }
        /// <summary>Current ABOUT curve for outside-MFD graph drawing; null when not on a CURVE page.</summary>
        internal static PerfCurve AboutCurve()
        {
            var titles = AboutTitles; int pg = Math.Max(0, Math.Min(titles.Length - 1, MfdNav.AboutPage));
            string title = titles[pg]; if (!title.StartsWith("CURVE:")) return null;
            var me = instance; if (me == null || me.profile == null) return null;
            string cname = title.Substring(6);
            foreach (var c in me.profile.Curves.All) if (c.Name == cname) return c.Empty ? null : c;
            return null;
        }
    }
}
