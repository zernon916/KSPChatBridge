using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Expansions.Serenity;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public partial class NativeFlightController : MonoBehaviour
    {
        static NativeFlightController instance;
        internal static bool Active { get { return instance != null && instance.mode != "idle"; } }
        internal static string ApLine { get { if (instance == null) return MfdNav.ApStatus("idle", false, false); var r = instance.runway; bool fin = instance.mode == "landing" && r != null && r.Phase == "final"; return MfdNav.ApStatus(instance.pendingLand != null && instance.mode == "hold" ? "ferry" : instance.mode == "landing" ? "approach" : instance.mode, fin && r.LocCoupled, fin && r.GsCoupled) + (instance.flapSet > 0 ? " FLAPS " + FlapsPolicy.Name(instance.flapSet) : ""); } }
        internal static bool Busy { get { return Active || (instance != null && instance.plan != null && instance.plan.Running); } }
        internal static bool PlanRunning { get { return instance != null && instance.plan != null && instance.plan.Running; } }
        internal static string Phase { get { return instance == null ? "idle" : instance.mode; } }
        internal static bool? ApGearCmd;
        internal static void SetGroup(Vessel target, KSPActionGroup group, bool value)
        {
            target.ActionGroups.SetGroup(group, value);
            if (group == KSPActionGroup.Gear) ApGearCmd = value;   // GROK LAND5g
            if (instance != null && instance.vessel == target && instance.recovery != null)
                instance.recovery.AcceptGroup(target, group, value);
        }
        readonly ControlLease lease = new ControlLease();
        readonly Dictionary<ModuleControlSurface, SurfaceState> originals = new Dictionary<ModuleControlSurface, SurfaceState>();
        readonly Dictionary<string, bool> auto = new Dictionary<string, bool> { { "master", true }, { "pitch", true }, { "roll", true }, { "yaw", true }, { "rotor", true } };
        Vessel vessel;
        string mode = "idle";
        double altitude, heading, speed = 150, band = 150, throttle, lastThrottle = -10, prevPitch, prevRoll, pitchIntegral, vsIntegral;
        double takeoverT = 99, takePitch, takeRoll, takeYaw;   // T5: smooth takeover - seconds since the hold engaged + the pilot's stick at that moment (99 = ramp done)
        double? capture;
        double terrainFloor = double.NaN, terrainAheadM = double.NaN, terrainMargin = 300;
        float nextTerrain;
        float nextTrim, watchTrimUntil;
        double trimBaselineVs, trimBaselinePitch;
        bool trimSuspended, parkingSet, parkingReleased, wasAirborne, prevBrakes;
        int partCount;
        Guid roverDetectedId;
        string settingsPath;
        Dictionary<string, object> settingsData = new Dictionary<string, object>();
        bool settingsWritable = true;
        NativeSpots spots;
        NativeCraftNotes craftNotes;
        float lastCapCut = -10; bool landingGearDown; int goArounds, lastAlarmSeq = -1; double bankCmd, bankPrevWant; float gTraceUntil, gTraceNext; readonly PitchShaper pitchShape = new PitchShaper(); string shapedMode = ""; double landMass0, landStall0; string loggedSmooth = "";
        readonly ApproachThrottle appThr = new ApproachThrottle(); double appThrLogT, prevApOut = double.NaN;   /* THR10 */ bool flareThrCmd, flareThrLogged; double flareThrAuto = double.NaN;   /* THR9 */ double rollHdgPrev = double.NaN, rollHdgRate = double.NaN, rollSteer = double.NaN; /* ROLL1 */ string brakeLogKey; bool flareFastEndLogged, liftDump;   /* LAND5q */ bool fastBrakeLogged, rolloutBrakeLogged; double rolloutLandedS; double ferrySecsClear = double.NaN; bool stallEmLatch; double rolloutAirS, bouncePitch = double.NaN; readonly AutoWarpState autoWarp = new AutoWarpState(); float autoWarpNext; double overrideSince = double.NaN, overrideUntil; bool overridePending; readonly ConfigTrim cfgTrim = new ConfigTrim(); int cfgKey = -1; readonly StepThrottle stepThr = new StepThrottle(); double prevHdgL = double.NaN, prevThrCfg = double.NaN; string loggedLock = "", loggedFollow = "", stepMode = "", loggedPattern = ""; readonly AltCallouts callouts = new AltCallouts(); double lastAirVs, flareEntryPitch = double.NaN; double aoaTrim = double.NaN, vsFilt = double.NaN, vsSlow = double.NaN, loadFf, attCmd = double.NaN; bool landLawOn; float turnPitchTraceNext;   // GROK LAND3: turn pitch-attitude hold state
        bool rudderOn; double rudderHdg0 = double.NaN, rudderSince, rudderOffUntil, lastRudderHdg = double.NaN;   // T6: heading errors under 3 deg go to the rudder, not the wings (+ the heading rate for the damping term and the give-up guard)
        readonly AlertGate alertGate = new AlertGate();
        string loggedDubins = ""; string loggedRoute = ""; double lastDesiredPitch; double lastAskedSpeed = double.NaN; readonly RecoveryGate gearGate = new RecoveryGate(); bool? gearSaid;
        double planBank, prevSpd = -1; bool capLifted;
        double climbPitchMax = 15, descentPitchMin = -5;
        double? directVs, directPitch, directBank, pendingCircle; bool bankOverride; string loggedMode = ""; float nextTelemetry;
        float elevator;
        NativePropulsion props;
        NativePower power;
        NativeRecovery recovery;
        NativeReversers reversers;
        StallStudy stallStudy;
        bool needStallStudy;
        ReverseRollout reverseRollout;
        bool wasNative;
        readonly NativeEngines engines = new NativeEngines();
        RotorSpool spool;
        CollectiveController collective = new CollectiveController();
        string afterSpool;
        float nextSpool;
        long spoolSequence;
        bool helicopterLanding;
        bool helicopterPositionHold, helicopterVerticalOnly;
        double hoverLat, hoverLon, lastHeliHeading, rotorYawBias, sideIntegral;
        HelicopterYaw helicopterYaw = new HelicopterYaw();
        RotorEmergency rotorEmergency = new RotorEmergency();
        TouchdownGate helicopterTouchdown = new TouchdownGate();
        double landingAoa = double.NaN;
        RotorPark rotorPark;
        float nextRotorPark;
        float nextRotorTrim;
        bool holdAltitude = true, holdHeading = true, holdSpeed = true; double vsSlew = double.NaN;
        TakeoffMission takeoff; TakeoffGround tground; readonly SpeedHold shold = new SpeedHold(); readonly ThrottleGate gate = new ThrottleGate { Log = m => ChatLog.Write("throttle", m) }; string throttleKind = "pi", throttleWhy = ""; string shapedSpeedMode = ""; bool landFloor, lowEnergy; float lowTraceNext, attTraceNext;
        StallLearner learner; double stallGuess = 45, liftArea; float nextStallSample, nextStallSave;
        void ConfigureInertia()
        {
            double thrust = 0, spool = 0; int n = 0;
            foreach (Part ep in vessel.parts) foreach (PartModule pm in ep.Modules) { var en = pm as ModuleEngines; if (en == null) continue; thrust += en.maxThrust; n++; if (en.useEngineResponseTime && en.engineAccelerationSpeed > 0) spool = Math.Max(spool, 1 / en.engineAccelerationSpeed); }
            double twr = n == 0 ? double.NaN : thrust / (vessel.totalMass * 9.81);
            shold.Configure(vessel.totalMass, twr, spool); if (spool > 0) stepThr.SpoolS = FlightPolicy.Clamp(spool, 1, 15);
            if (!double.IsNaN(twr)) stepThr.AccelPerThr = FlightPolicy.Clamp(.5 * twr * 9.81, 2, 15);   // GROK LAND5k: deficit steps sized from the thrust (half the static TWR, drag/spool margin)   // GROK LAND5j: measured spool = the prediction horizon
            { object lo; var lc = settingsData.TryGetValue("throttle_lag", out lo) ? lo as Dictionary<string, object> : null; double ml = lc == null ? double.NaN : Num(lc, vessel.vesselName, double.NaN); if (!double.IsNaN(ml)) shold.SetLag(ml); lagMeasured = !double.IsNaN(ml); }
            if (profile != null)
            {
                if (!double.IsNaN(profile.Lag)) shold.SetLag(profile.Lag);
                if (!double.IsNaN(profile.Accel)) shold.SetTargetSlew(profile.AccelAt(vessel.totalMass));   // AUTOPILOT_REVIEW #14
            }
            // T3: the speed hold asks for an ACCELERATION (speed error / 4 s) and clamps it to what this craft measured - LEARN accel,
            // LEARN idle decel, else the live idle/airbrake decel learner. Unmeasured sides stay unclamped (accel falls back to Sens).
            double accA = profile != null && !double.IsNaN(profile.Accel) ? profile.AccelAt(vessel.totalMass) : double.NaN;
            double decA = profile != null && !double.IsNaN(profile.Decel) ? profile.DecelAt(vessel.totalMass) : Decel.Estimate(vessel.vesselName, 150, 100, true);
            shold.SetAccelLimits(accA, decA);
            lagMeter = new SpeedHold.LagMeter();
            string accTxt = double.IsNaN(shold.AccelLimit) ? "n/a (Sens " + shold.Sens.ToString("0.0") + " from TWR)" : shold.AccelLimit.ToString("0.00") + " learned";
            string decTxt = double.IsNaN(shold.DecelLimit) ? "n/a (slowdown unclamped)" : shold.DecelLimit.ToString("0.00") + " learned";
            ChatLog.Write("vessel", string.Format(System.Globalization.CultureInfo.InvariantCulture, "inertia modifier {0:0.00} (m={1:0.0} t, TWR={2:0.00}, spool={3:0.0} s): throttle gains /{0:0.00}, lag {6:0.0} s {7}, decel {4:0.00} m/s2 {5}, speed hold asks accel = speed error / {10:0} s clamped +{8} / -{9} m/s2",
                shold.K, vessel.totalMass, twr, spool, double.IsNaN(Decel.Estimate(vessel.vesselName, 150, 100, true)) ? ApproachProfile.MassDecel(vessel.totalMass) : Decel.Estimate(vessel.vesselName, 150, 100, true), double.IsNaN(Decel.Estimate(vessel.vesselName, 150, 100, true)) ? "(mass estimate)" : "(measured)", shold.Lag, lagMeasured ? "(measured)" : "(estimate)", accTxt, decTxt, SpeedHold.AccelTau));
        }
        SpeedHold.LagMeter lagMeter; bool lagMeasured; float lagAccPrevT; double lagPrevV = double.NaN, lagAcc;
        void MeasureLag(double dt)
        {
            if (lagMeter == null || vessel.LandedOrSplashed || dt <= 0) return;
            double v = vessel.srfSpeed; if (!double.IsNaN(lagPrevV)) lagAcc += ((v - lagPrevV) / dt - lagAcc) * Math.Min(1, dt / .5); lagPrevV = v;
            if (Math.Abs(vessel.verticalSpeed) > 8) return;   // level-ish only: pitch changes also move speed
            double lag = lagMeter.Observe(Planetarium.GetUniversalTime(), vessel.ctrlState.mainThrottle, lagAcc, shold.Sens);
            if (double.IsNaN(lag)) return;
            double blended = lagMeasured ? .7 * shold.Lag + .3 * lag : lag; shold.SetLag(blended); lagMeasured = true;
            object o; var cache = settingsData.TryGetValue("throttle_lag", out o) ? o as Dictionary<string, object> : null;
            if (cache == null) settingsData["throttle_lag"] = cache = new Dictionary<string, object>();
            cache[vessel.vesselName] = Math.Round(shold.Lag, 2); try { Save(); } catch (Exception) { }
            ChatLog.Write("speed", "throttle->speed lag measured " + lag.ToString("0.0") + " s -> " + shold.Lag.ToString("0.0") + " s saved for " + vessel.vesselName);
        }
        int errorHolds;
        /// <summary>Luke 6:10 PM: an error must never leave nose-down / idle applied. Log the full trace; airborne the first few
        /// times fall back to a plain altitude/heading hold; otherwise release with centred controls, power kept up, SAS on.</summary>
        void FailSoft(FlightCtrlState c, Exception ex)
        {
            string where = mode; ChatLog.Write("error", where + ": " + ex); Debug.LogError("[KSPChatBridge] controller error in " + where + ": " + ex);
            c.pitch = c.yaw = c.roll = c.wheelSteer = 0; elevator = 0; pitchIntegral = vsIntegral = 0; takeoverT = 99;   // T5: an error release ends the takeover ramp - the pilot's stick seed is stale
            bool air = vessel != null && !vessel.LandedOrSplashed;
            if (air && where != "hold" && errorHolds < 3)
            {
                errorHolds++; mode = "hold"; directPitch = directVs = directBank = null; holdAltitude = holdHeading = holdSpeed = true;
                altitude = Math.Max(vessel.altitude, vessel.altitude - vessel.radarAltitude + 300); heading = FlightGlobals.ship_heading;
                speed = FlightPolicy.Clamp(1.6 * (double.IsNaN(stall) || stall <= 0 ? 60 : stall), 80, 200); throttle = Math.Max(throttle, .6);
                SetGroup(vessel, KSPActionGroup.Brakes, false); AirbrakesByAutopilot = false;
                ChatWindow.Notice("Local autopilot error during " + where + " - holding " + Math.Round(altitude) + " m, heading " + Math.Round(heading) + " (details in the log).");
                return;
            }
            if (air) { c.mainThrottle = vessel.ctrlState.mainThrottle = Math.Max(c.mainThrottle, .6f); gate.Seed(c.mainThrottle, "controller error release: power 60%+"); SetGroup(vessel, KSPActionGroup.SAS, true); }
            Stop(); ChatWindow.Notice("Local controller released after error (" + ex.GetType().Name + " in " + where + "): controls centred" + (air ? ", power 60%+, SAS on." : "."));
        }
        /// <summary>Equivalent airspeed (from dynamic pressure): the ONE speed every stall comparison uses.</summary>
        double Eas() { return vessel == null ? double.NaN : PilotPolicy.Eas(vessel.dynamicPressurekPa, vessel.srfSpeed); }
        bool stallCached, stallConservative; double stallOverride = double.NaN;
        /// <summary>GROK LAND5k / TAB-CRAFT1: per-craft overrides (craft_overrides.<name>: stall_override, approach_speed, threshold_speed) beat the
        /// learned/estimated stall. live = changed from the tablet mid-flight: the speed schedule is replanned, the locked path is kept.</summary>
        void ApplyCraftOverrides(bool live)
        {
            if (vessel == null) return; double ap, th;
            stallOverride = CraftOverrides.Apply(settingsData, vessel.vesselName, out ap, out th);
            RunwayMission.AutoGoAround = CraftOverrides.On(CraftOverrides.AutoGa, Bool(settingsData, "autoland_auto_go_around", false));   // GROK LAND5k: default OFF
            string txt = "stall " + (double.IsNaN(stallOverride) ? "learned" : Math.Round(stallOverride).ToString()) + ", approach " + (double.IsNaN(ap) ? "-" : Math.Round(ap).ToString()) + ", threshold " + (double.IsNaN(th) ? "-" : Math.Round(th).ToString()) + " m/s, auto go-around " + (RunwayMission.AutoGoAround ? "ON" : "OFF");
            if (!live) { ChatLog.Write("vessel", "craft override: " + txt + (double.IsNaN(stallOverride) ? "" : " (learned stall ignored)")); return; }
            stall = LearnedStall(); if (landMass0 > 0) { landMass0 = vessel.totalMass; landStall0 = stall; }
            ChatLog.Write("vessel", "craft override changed (tablet): " + txt + " -> stall used " + Math.Round(stall) + "; speed schedule replanned, path kept");
        }
        /// <summary>GROK LAND5k: global auto go-around (autoland_auto_go_around, default OFF); a craft's auto_go_around beats it.</summary>
        internal static bool GlobalAutoGoAround
        {
            get { return instance != null && Bool(instance.settingsData, "autoland_auto_go_around", false); }
            set { var i = instance; if (i == null) return; i.settingsData["autoland_auto_go_around"] = value; try { i.Save(); } catch (Exception) { } if (i.vessel != null) i.ApplyCraftOverrides(true); else RunwayMission.AutoGoAround = value; ChatLog.Write("craft", "auto go-around (all craft) " + (value ? "ON" : "OFF")); }
        }
        internal static bool CraftReady { get { return instance != null && instance.vessel != null; } }
        internal static string CraftName { get { return CraftReady ? instance.vessel.vesselName : ""; } }
        internal static double CraftOverrideGet(string field) { return CraftReady ? CraftOverrides.Get(instance.settingsData, CraftName, field) : double.NaN; }
        /// <summary>Tablet edit: saved to native_settings.json at once and applied live.</summary>
        internal static void CraftOverrideSet(string field, double v)
        {
            var i = instance; if (!CraftReady) return;
            if (field == null) CraftOverrides.Clear(i.settingsData, CraftName); else CraftOverrides.Set(i.settingsData, CraftName, field, v);
            try { i.Save(); } catch (Exception) { ChatWindow.Notice("Craft override could not be saved."); }
            i.ApplyCraftOverrides(true);
            if (field == CraftOverrides.Vr || field == CraftOverrides.RotatePitch || field == null) ChatLog.Write("craft", CraftOverrides.TakeoffLogLine(CraftName, CraftOverrides.Get(i.settingsData, CraftName, CraftOverrides.Vr), CraftOverrides.Get(i.settingsData, CraftName, CraftOverrides.RotatePitch)));
            if (field != CraftOverrides.Vr && field != CraftOverrides.RotatePitch) ChatLog.Write("craft", CraftOverrides.LogLine(CraftName, CraftOverrides.Get(i.settingsData, CraftName, CraftOverrides.Approach), CraftOverrides.Get(i.settingsData, CraftName, CraftOverrides.Threshold), CraftOverrides.Get(i.settingsData, CraftName, CraftOverrides.Stall)) + (field != null && field != CraftOverrides.Approach && field != CraftOverrides.Threshold && field != CraftOverrides.Stall ? " " + field + "=" + (double.IsNaN(CraftOverrides.Get(i.settingsData, CraftName, field)) ? "-" : CraftOverrides.Text(CraftOverrides.Get(i.settingsData, CraftName, field))) : ""));
        }
        /// <summary>Value in use for a field (the -/+ start point when no override is set).</summary>
        internal static double CraftInUse(string field)
        {
            var i = instance; if (!CraftReady) return double.NaN;
            if (field == CraftOverrides.Stall) return i.stall;
            if (field == CraftOverrides.AutoGa) return RunwayMission.AutoGoAround ? 1 : 0;
            if (field == "airbrakes" || field == "reverser") return 1;
            if (field == "decel_plan") return i.runway != null ? i.runway.EffDecelPublic : double.NaN;
            if (field == "flap_limit_speed") return ApproachProfile.FlapLimitSpeed;
            if (field == "max_approach_bank") return i.runway != null && i.runway.TurnBank > 0 ? i.runway.TurnBank : 30;
            if (field == "flare_start_m" || field == "touchdown_sink" || field == "touchdown_aim_m" || field == "brake_start_speed") { var df = CraftOverrides.Find(field); return field == "touchdown_aim_m" && i.runway != null ? i.runway.TouchdownM : df.Default; }
            if (field == "vr") return i.takeoff != null && i.takeoff.Vr > 0 ? i.takeoff.Vr : TakeoffMission.RotateSpeed(i.stall, i.liftoffSpeed);
            if (field == CraftOverrides.RotatePitch) return TakeoffMission.RotateTarget(double.NaN);
            if (field == CraftOverrides.FlareThrottle) return 0;
            if (field == CraftOverrides.FlareSpoolLead) return 2;
            if (field == CraftOverrides.FlareSink) return -FlarePolicy.SinkTarget(double.NaN, CraftOverrides.L("touchdown_sink"), i.runway != null && i.runway.Override != null ? i.runway.Override.FlareSinkMs : double.NaN);
            if (field == CraftOverrides.FlareMaxPitch) return i.CpMax;
            if (field == CraftOverrides.RolloutPitch) return Derotation.DefaultNoseDown;
            if (field == "climb_speed") return i.vessel != null ? i.ClimbSpeedTarget() : double.NaN;
            if (field == CraftOverrides.Threshold) return ApproachProfile.Vref(i.stall);
            return ApproachProfile.HiSpeed(i.stall);
        }
        internal static List<string> CraftInfoLines()
        {
            var i = instance; var r = new List<string>(); if (!CraftReady) { r.Add("No active vessel."); return r; }
            var v = i.vessel; double m = v.GetTotalMass(), ms = i.learner == null ? double.NaN : i.learner.MeasuredStall(m * 1000, i.liftArea), cf = i.learner == null ? 0 : i.learner.Confidence;
            double de = Decel.Estimate(v.vesselName, 150, 100, true), s = i.stall;
            r.Add("Mass " + m.ToString("0.0", CultureInfo.InvariantCulture) + " t");
            r.Add("Stall in use " + Math.Round(s) + " m/s (" + (double.IsNaN(i.stallOverride) ? (i.stallConservative ? "conservative estimate" : "learned/estimated") : "override") + ")");
            r.Add("Learned stall " + (double.IsNaN(ms) ? "-" : Math.Round(ms).ToString()) + " (conf " + Math.Round(100 * cf) + "%), aero est " + (double.IsNaN(i.stallEstimate) ? Math.Round(i.stallGuess) : Math.Round(i.stallEstimate)));
            r.Add("Idle decel " + (double.IsNaN(de) ? ApproachProfile.MassDecel(v.totalMass).ToString("0.00", CultureInfo.InvariantCulture) + " m/s2 (mass est)" : de.ToString("0.00", CultureInfo.InvariantCulture) + " m/s2 (measured)"));
            r.Add("Engine lag " + i.shold.Lag.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            r.Add("Flare " + Math.Round(ApproachProfile.Vref(s)) + " m/s, floor " + Math.Round(ApproachProfile.FloorAtThr * s) + " (thr) - " + Math.Round(ApproachProfile.FloorAtFaf * s) + " (FAF)");
            r.Add("Approach " + Math.Round(ApproachProfile.HiSpeed(s)) + " m/s" + (double.IsNaN(ApproachProfile.OverrideThreshold) ? "" : ", threshold " + Math.Round(ApproachProfile.OverrideThreshold) + " m/s"));
            return r;
        }
        double LearnedStall() { if (learner == null || vessel == null) return stallGuess; if (!double.IsNaN(stallOverride)) return stallOverride; double ms0 = StallLearner.Accept(learner.MeasuredStall(vessel.GetTotalMass() * 1000, liftArea), stallEstimate, profile != null ? profile.Stall : double.NaN), s0 = StallLearner.Effective(stallGuess, ms0, learner.Confidence); if (double.IsNaN(s0) || s0 <= 0) s0 = stallGuess;
            stallConservative = double.IsNaN(ms0) && !stallCached && (profile == null || double.IsNaN(profile.Stall)); if (stallConservative) s0 = StallLearner.Conservative(s0, UsableLiftoff);   /* GROK LAND5k */ s0 = CraftClass.ConservativeStall(s0, UsableLiftoff); s0 = PilotPolicy.SaneStall(s0, StallEstimate()); if (vessel != null && CraftClass.Gentle(CraftCls)) s0 = Math.Max(s0, ApproachProfile.HeavyStallFloor(vessel.totalMass)); s0 = FlightPolicy.Clamp(s0, 20, 200); DecisionLog.Use("stall", profile != null && !double.IsNaN(profile.Stall) ? "learned stall (sanity-checked)" : "default estimate", s0); return s0; }
        double stallEstimate = double.NaN; bool rotateStalled;   // GROK TO3
        /// <summary>Lift-based stall estimate (liftoff/1.25 floor); the fallback whenever the learned/measured stall is missing or implausible.</summary>
        double StallEstimate() { return PilotPolicy.StallEstimate(double.IsNaN(stallEstimate) ? stallGuess : stallEstimate, UsableLiftoff); }
        /// <summary>GROK TO3: a stored rotation-limited liftoff (Aeris 3A saved 190.1 at 03:45) never feeds the stall estimate.</summary>
        double UsableLiftoff { get { return TakeoffMission.LiftoffUsable(liftoffSpeed, TakeoffMission.MinRotateSpeed(stallEstimate)) ? liftoffSpeed : double.NaN; } }
        internal static string StallLabel { get { var i = instance; return i == null || i.learner == null || i.vessel == null ? "" : StallLearner.Label(i.stallGuess, i.learner.MeasuredStall(i.vessel.GetTotalMass() * 1000, i.liftArea), i.learner.Confidence); } }
        void SaveLearner()
        {
            object so; var sc = settingsData.TryGetValue("stall_learn", out so) ? so as Dictionary<string, object> : null;
            if (sc == null) settingsData["stall_learn"] = sc = new Dictionary<string, object>();
            sc[vessel.vesselName] = learner.Save(); try { Save(); } catch (Exception) { }
        }
        string craftAuto = "light", craftOverride = "auto"; string CraftCls { get { return CraftClass.Effective(craftAuto, craftOverride); } }
        internal static string CraftLabel { get { return instance == null ? "" : CraftClass.Label(instance.CraftCls, instance.craftOverride); } }
        internal string CraftClassCmd(string m)
        {
            m = (m ?? "auto").ToLowerInvariant(); if (m != "gentle" && m != "fighter") m = "auto";
            craftOverride = m; settingsData["craft_class"] = m; try { Save(); } catch (Exception) { }
            ApproachProfile.JoinG = CraftClass.JoinG(CraftCls, FlightPolicy.Clamp(Num(settingsData, "approach_join_g", 4.0), 1.1, ApproachProfile.GCap));
            ApproachProfile.FinalG = CraftClass.JoinG(CraftCls, FlightPolicy.Clamp(Num(settingsData, "approach_final_g", 1.5), 1.05, ApproachProfile.GCap));
            return "Craft class: " + CraftClass.Label(CraftCls, craftOverride) + (CraftClass.Gentle(CraftCls) ? " - max 25 deg bank (or LEARN envelope), 6 deg/s roll, gentle pitch." : " - full g rating / LEARN envelope.");
        }
        RunwayMission runway;
        bool landingAfterTakeoff;
        NativeVerticalLanding verticalLanding;
        TaxiMission taxi; readonly Derotation derot = new Derotation();
        bool poweredTaxi;
        double roverTargetSpeed, roverIntegral, roverHeading;
        double roverDestinationLat, roverDestinationLon;
        string roverDestination;
        bool roverNavigating;
        bool roverHeadingEnabled, roverSafetySlow, roverSafetyBrake;
        double tgHdgPrev = double.NaN; double stall = 45, liftoffSpeed = double.NaN; float trimHandoffAt = -1000;
        void RememberLiftoff(double v)
        {
            object o; var cache = settingsData.TryGetValue("liftoff_speeds", out o) ? o as Dictionary<string, object> : null;
            if (cache == null) settingsData["liftoff_speeds"] = cache = new Dictionary<string, object>();
            cache[vessel.vesselName] = Math.Round(v, 1); liftoffSpeed = v; stall = LearnedStall(); try { Save(); } catch (Exception) { }
        }
        NativePlan plan;
        double planTurn, planLastHeading, planLastTime;
        internal static string PlanStatus { get { return instance == null || instance.plan == null ? "No local plan" : instance.plan.Status; } }

        sealed class SurfaceState
        {
            internal float Angle, Authority;
            internal bool Deploy, Invert, PartInvert, Pitch, Roll, Yaw;
            internal SurfaceState(ModuleControlSurface s)
            { Angle = s.deployAngle; Authority = s.authorityLimiter; Deploy = s.deploy; Invert = s.deployInvert; PartInvert = s.partDeployInvert; Pitch = s.ignorePitch; Roll = s.ignoreRoll; Yaw = s.ignoreYaw; }
            internal void Restore(ModuleControlSurface s)
            { s.deployAngle = Angle; s.authorityLimiter = Authority; s.deploy = Deploy; /* FLAP2: Invert / PartInvert stay the player's */ s.ignorePitch = Pitch; s.ignoreRoll = Roll; s.ignoreYaw = Yaw; }
        }
        static double Num(Dictionary<string, object> a, string key, double fallback)
        {
            if (!a.ContainsKey(key)) return fallback;
            double v = Convert.ToDouble(a[key], CultureInfo.InvariantCulture);
            if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException("Non-finite " + key);
            return v;
        }
        static string Str(Dictionary<string, object> a, string key, string fallback)
        { return a.ContainsKey(key) ? Convert.ToString(a[key], CultureInfo.InvariantCulture) : fallback; }
        static bool Bool(Dictionary<string, object> a, string key, bool fallback)
        { return a.ContainsKey(key) ? Convert.ToBoolean(a[key], CultureInfo.InvariantCulture) : fallback; }
        void Awake()
        {
            instance = this;
            MfdNav.LearnRunning = () => NativeFlightController.LearnIsRunning;
            MfdNav.LearnRover = () => NativeFlightController.ActiveRover;
            GameEvents.onPartDie.Add(OnPartDie);
            NativeCommands.StatusPathProvider = () => Path.Combine(KSPUtil.ApplicationRootPath,
                "GameData/KSPChatBridge/PluginData/native_control.json");
            settingsPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/native_settings.json");
            try
            {
                if (File.Exists(settingsPath)) settingsData = MiniJson.Deserialize(File.ReadAllText(settingsPath));
                string legacyPath = Path.Combine(AicsCore.DataDirectory, "bridge_settings.json");
                var legacy = Num(settingsData, "migration_version", 0) < 1 && File.Exists(legacyPath) ? MiniJson.Deserialize(File.ReadAllText(legacyPath)) : new Dictionary<string, object>();
                settingsData = NativeSettings.Migrate(settingsData, legacy);
                AiSettings.MergeInto(settingsData);
                if (!settingsData.ContainsKey("craft_notes_imported"))
                {
                    string notesPath = Path.Combine(AicsCore.DataDirectory, "craft_notes.json");
                    if (!settingsData.ContainsKey("craft_notes") && File.Exists(notesPath)) settingsData["craft_notes"] = MiniJson.Deserialize(File.ReadAllText(notesPath));
                    settingsData["craft_notes_imported"] = true;
                }
                band = FlightPolicy.Clamp(Num(settingsData, "altitude_band_m", 150), 10, 500);
                foreach (string key in new List<string>(auto.Keys)) auto[key] = Bool(settingsData, "auto_" + key, true);
                NativeSettings.CleanProfiles(settingsData);   // GROK LAND5k
                Save();
            }
            catch (Exception) { settingsWritable = false; ChatWindow.Notice("Local settings could not be loaded. Originals preserved; saving disabled for this flight."); }
            if (settingsData == null) settingsData = new Dictionary<string, object>();
            spots = new NativeSpots(settingsData);
            craftNotes = new NativeCraftNotes(settingsData);
        }
        void Save()
        {
            if (!settingsWritable) throw new InvalidOperationException("Settings load failed; originals preserved.");
            settingsData["altitude_band_m"] = band;
            foreach (var kv in auto) settingsData["auto_" + kv.Key] = kv.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            string temporary = settingsPath + ".tmp";
            File.WriteAllText(temporary, MiniJson.Serialize(settingsData));
            if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, settingsPath + ".bak");
            else File.Move(temporary, settingsPath);
        }
        void Bind()
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (vessel == active) return;
            Stop();
            if (vessel != null) vessel.OnFlyByWire -= Fly;
            vessel = active; originals.Clear(); parkingSet = parkingReleased = false; ResetLearnState();   // revert / reload / switch: no Learn state or menu cursor survives onto another vessel object
            if (vessel == null) return;
            {   // GROK LAND5k: a launched .craft can carry an unresolved '#autoLOC_...' ship name - profiles are keyed by name, so resolve it
                string raw = vessel.vesselName, loc = raw; try { loc = KSP.Localization.Localizer.Format(raw); } catch (Exception) { }
                string key = StallLearner.ProfileKey(raw, loc, HighLogic.SaveFolder == LandingTestRun.SaveName ? LandingTestRun.CraftName : null);
                if (key != raw && key.Length > 0) { vessel.vesselName = key; ChatLog.Write("vessel", "renamed '" + raw + "' -> '" + key + "' (profile key)"); }
            }
            ChatLog.Write("vessel", "switched to " + vessel.vesselName + " (" + vessel.GetTotalMass().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " t, " + vessel.parts.Count + " parts)");
            vessel.OnFlyByWire += Fly;
            props = new NativePropulsion(vessel);
            power = new NativePower(vessel);
            recovery = new NativeRecovery(vessel); wasNative = false;
            terrainFloor = double.NaN; nextTerrain = 0;
            reversers = new NativeReversers(vessel);
            flapScanAt = -100;
            object cached; var stallCache = settingsData.TryGetValue("stall_speeds", out cached) ? cached as Dictionary<string, object> : null;
            double liftSum = 0; foreach (Part lp in vessel.parts) foreach (PartModule lm in lp.Modules) { var ls = lm as ModuleLiftingSurface; if (ls != null) liftSum += ls.deflectionLiftCoeff; }
            double estStall = ApproachProfile.EstimateStall(vessel.totalMass * 1000, liftSum);
            ApproachProfile.JoinG = FlightPolicy.Clamp(Num(settingsData, "approach_join_g", 4.0), 1.1, ApproachProfile.GCap); ApproachProfile.CrewG = FlightPolicy.Clamp(Num(settingsData, "crew_g_limit", 6.0), 1.5, ApproachProfile.GCap);
            { var tol = new List<double>(); foreach (Part tp in vessel.parts) tol.Add(tp.gTolerance); ApproachProfile.StructG = ApproachProfile.StructuralG(tol); ApproachProfile.Backoff = 1; } ApproachProfile.FinalG = FlightPolicy.Clamp(Num(settingsData, "approach_final_g", 1.5), 1.05, ApproachProfile.GCap);
            try { stall = stallCache == null ? estStall : Math.Max(estStall, FlightPolicy.Clamp(Num(stallCache, vessel.vesselName, estStall), 20, 200)); }   // a stale low cache never undercuts the estimate
            catch (Exception) { stall = 45; ChatWindow.Notice("Invalid saved stall speed ignored for this craft."); }
            { object lo; var lc = settingsData.TryGetValue("liftoff_speeds", out lo) ? lo as Dictionary<string, object> : null; liftoffSpeed = lc == null ? double.NaN : Num(lc, vessel.vesselName, double.NaN); }
            partCount = vessel.parts.Count;
            stallGuess = stall; liftArea = liftSum; stallEstimate = estStall;
            { object so; var sc = settingsData.TryGetValue("stall_learn", out so) ? so as Dictionary<string, object> : null; object lo2 = null; if (sc != null) sc.TryGetValue(vessel.vesselName, out lo2); learner = StallLearner.Load(lo2 as Dictionary<string, object>); }
            ChatLog.Write("vessel", string.Format(System.Globalization.CultureInfo.InvariantCulture, "stall inputs: m={0:0.0} t, liftCoeff={1:0.00} -> S={2:0.0} m2, rho={3}, CLmax={4}, Vs est={5:0.0} m/s, heavy floor={6:0}, liftoff={7:0}",
                vessel.totalMass, liftSum, liftSum * ApproachProfile.AreaPerCoeff, ApproachProfile.RhoSea, ApproachProfile.ClMaxConservative, estStall, ApproachProfile.HeavyStallFloor(vessel.totalMass), liftoffSpeed));
            ConfigureInertia(); LoadProfile();
            { object sc0; var stc = settingsData.TryGetValue("stall_speeds", out sc0) ? sc0 as Dictionary<string, object> : null; object v0 = null; stallCached = stc != null && stc.TryGetValue(vessel.vesselName, out v0) && v0 != null; }
            ApplyCraftOverrides(false);
            stall = LearnedStall(); if (!double.IsNaN(stallOverride)) ChatLog.Write("vessel", "stall in use " + Math.Round(stall) + " (craft override)"); if (stallConservative) ChatLog.Write("vessel", "stall: no measurement, no learned profile - conservative estimate " + Math.Round(stall) + " (aero " + Math.Round(stallGuess) + " x " + StallLearner.UnmeasuredK + (double.IsNaN(UsableLiftoff) ? "" : ", liftoff " + Math.Round(UsableLiftoff) + " x " + StallLearner.LiftoffK) + ")");
            ChatLog.Write("vessel", "stall " + StallLearner.Label(stallGuess, learner.MeasuredStall(vessel.GetTotalMass() * 1000, liftArea), learner.Confidence));
            { double lo = 0, hi = 0; Vector3 rt = vessel.ReferenceTransform.right; foreach (Part sp in vessel.parts) { double x = Vector3.Dot(sp.transform.position - vessel.rootPart.transform.position, rt); lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
              craftAuto = CraftClass.Classify(vessel.GetTotalMass(), hi - lo, vessel.parts.Count); craftOverride = Str(settingsData, "craft_class", "auto");
              ApproachProfile.JoinG = CraftClass.JoinG(CraftCls, ApproachProfile.JoinG); ApproachProfile.FinalG = CraftClass.JoinG(CraftCls, ApproachProfile.FinalG);
              ChatLog.Write("vessel", "class " + CraftClass.Label(CraftCls, craftOverride) + " span " + (hi - lo).ToString("0") + " m"); }
            foreach (Part p in vessel.parts) foreach (PartModule m in p.Modules)
            { var s = m as ModuleControlSurface; if (s != null) originals[s] = new SurfaceState(s); }
            { string bw; brakesMoveControls = BrakesMoveControls(vessel, out bw); if (brakesMoveControls) ChatLog.Write("brakes", "Brakes group moves a pitch/roll surface (" + bw + "): approach airbrakes disabled for this craft (idle + flaps only)"); }   // GROK FIX3
        }
        /// <summary>Brakes AG (stock airbrakes; wheel brakes in the air are harmless) set by the approach speed logic, not tampering.</summary>
        internal static bool AirbrakesByAutopilot;
        bool recoveryWasActive, powerFloor;
        readonly AirbrakeGate airGate = new AirbrakeGate();
        /// <summary>GROK FIX3: a Brakes-bound control surface that also moves pitch/roll (elevons, tail winglets): the approach never uses airbrakes.</summary>
        bool brakesMoveControls;
        static bool BrakesBound(ModuleControlSurface s)
        { BaseAction a = null; try { a = s.Actions["ActionToggle"]; } catch (Exception) { } return a != null && (a.actionGroup & KSPActionGroup.Brakes) != 0; }
        static bool BrakesMoveControls(Vessel v, out string which)
        {
            which = "";
            foreach (Part p in v.parts) foreach (PartModule m in p.Modules)
            {
                var s = m as ModuleControlSurface; if (s == null || (s.ignorePitch && s.ignoreRoll)) continue;
                if (BrakesBound(s)) { which = p.partInfo != null ? p.partInfo.title : p.name; return true; }
            }
            return false;
        }
        /// <summary>GROK FIX1: real hold state for the tablet / control-box HOLDS chip (was a UI-only flag).</summary>
        /// <summary>GROK LAND2: FERRY (cruise to the runway) target for the map's predicted path; false when not ferrying.</summary>
        internal static bool FerryTarget(out double lat, out double lon) { lat = lon = 0; if (instance == null || instance.pendingLand == null) return false; lat = instance.ferryLat; lon = instance.ferryLon; return true; }
        internal static bool HoldsEngaged { get { return instance != null && instance.mode == "hold" && instance.pendingLand == null; } }   // ferry = landing job, not holds
        internal static bool FerryPending { get { return instance != null && instance.mode == "hold" && instance.pendingLand != null; } }
        internal static bool RunwayMissed { get { return instance != null && instance.runway != null && instance.runway.MissedClimb; } }
        internal static string RunwayPhase { get { return instance == null || instance.runway == null ? null : instance.runway.Phase; } }
        internal static string TakeoffPhase { get { return instance == null || instance.takeoff == null ? null : instance.takeoff.Phase; } }
        /// <summary>GROK TAB2: plain-words job for the tablet ("LANDING - FINAL").</summary>
        internal static string JobText { get { return instance == null ? "IDLE" : TabletNav.JobText(instance.mode, instance.mode == "takeoff" ? TakeoffPhase : RunwayPhase, FerryPending); } }
        internal static bool LandingJob { get { return FerryPending || Phase == "landing"; } }
        float nextChartPoll;
        static DecelLearner decelStore; double decelPrevV = -1; float decelSaveAt;
        static string DecelPath { get { return System.IO.Path.Combine(AicsCore.PluginDataDirectory, "decel_profiles.json"); } }
        internal static DecelLearner Decel { get { if (decelStore == null) { try { decelStore = System.IO.File.Exists(DecelPath) ? DecelLearner.FromJson(System.IO.File.ReadAllText(DecelPath)) : new DecelLearner(); } catch (Exception) { decelStore = new DecelLearner(); } } return decelStore; } }
        /// <summary>Measure this craft's deceleration at idle (+airbrakes) in level-ish flight; saved per craft name in PluginData/decel_profiles.json.</summary>
        void LearnDecel(double dt)
        {
            if (vessel.LandedOrSplashed || dt <= 0) { decelPrevV = -1; return; }
            double v = vessel.srfSpeed; bool idle = vessel.ctrlState.mainThrottle <= .06f, level = Math.Abs(vessel.verticalSpeed) < 5;
            if (decelPrevV > 0 && idle && level) Decel.Learn(vessel.vesselName, v, (decelPrevV - v) / dt, vessel.ActionGroups[KSPActionGroup.Brakes]);
            decelPrevV = v;
            if (Decel.Dirty && Time.realtimeSinceStartup > decelSaveAt) { decelSaveAt = Time.realtimeSinceStartup + 30; Decel.Dirty = false; try { AtomicFile.Write(DecelPath, Decel.ToJson()); } catch (Exception) { } }
        }
        /// <summary>From the FAF/ILS inbound (intercept/final/flare) the approach speed is forced; chat speed targets are refused.</summary>
        bool ApproachSpeedLocked { get { return mode == "landing" && runway != null && (runway.Phase == "intercept" || runway.Phase == "final" || runway.Phase == "flare"); } }
        /// <summary>Climb / climbout IAS: LEARN best-climb when known, else 1.8x stall (AUTOPILOT_REVIEW #3/#12).</summary>
        double ClimbSpeedTarget()
        {
            double vy = !double.IsNaN(CraftOverrides.L("climb_speed")) ? CraftOverrides.L("climb_speed") : profile != null ? profile.BestClimbSpeed(stall, vessel.totalMass) : double.NaN;   // TAB-CRAFT1
            DecisionLog.Use("climb speed", double.IsNaN(vy) || vy <= 0 ? "default 1.8x stall" : "learned climb curve", double.IsNaN(vy) || vy <= 0 ? FlightPolicy.Clamp(1.8 * stall, 80, 200) : vy);
            return double.IsNaN(vy) || vy <= 0 ? FlightPolicy.Clamp(1.8 * stall, 80, 200) : FlightPolicy.Clamp(vy, 40, 250);   // no profile: the old 1.8x stall, 80-200
        }
        /// <summary>Hot-swap: charts/ mtimes every ~2 s; a changed chart reloads live (bad JSON keeps the old one) and an active approach replans from here.</summary>
        void PollCharts()
        {
            if (Time.realtimeSinceStartup < nextChartPoll) return; nextChartPoll = Time.realtimeSinceStartup + 2;
            try
            {
                EnsureCharts();
                foreach (var key in ChartStore.Changed())
                {
                    var rw = runway != null && runway.Key == key ? runway : null;
                    if (rw != null && rw.ChartPin.Length > 0) { ChatLog.Write("approach", "chart " + key + " changed on disk - keeping the picked " + rw.ChartPin); continue; }   // GROK LAND2
                    string why; object end = ChartStore.Read(key, out why); ApproachOverride ov = null;
                    if (end != null && rw != null && FlightGlobals.ActiveVessel != null) ov = ApproachOverride.Parse(end, rw.Lat, rw.Lon, rw.Elevation, FlightGlobals.ActiveVessel.mainBody.Radius, out why, rw.Terrain);
                    if (rw == null) { if (why.Length == 0) { ChatWindow.Notice("[SYSTEM] chart " + key + " reloaded"); ChatLog.Write("approach", "chart " + key + " reloaded (" + System.IO.Path.GetFileName(ChartStore.PathFor(key)) + ")"); } else ChatLog.Write("approach", "chart " + key + " reload rejected (" + why + "); keeping the old chart"); continue; }
                    if (ov == null) { ChatLog.Write("approach", "chart " + key + " reload rejected (" + (why.Length > 0 ? why : "unreadable") + "); keeping the old chart"); ChatWindow.Notice("[SYSTEM] chart " + key + " has an error - kept the old chart (see log)"); continue; }
                    rw.Override = ov; rw.ReloadChart();
                    ChatWindow.Notice("[SYSTEM] chart " + key + " reloaded"); ChatLog.Write("approach", "chart " + key + " reloaded (" + System.IO.Path.GetFileName(ChartStore.PathFor(key)) + ")" + (rw == runway && mode == "landing" ? "; active approach replanned, phase " + rw.Phase : ""));
                }
            }
            catch (Exception ex) { ChatLog.Write("approach", "chart poll failed: " + ex.Message); }
        }
        void Update()
        {
            PollCharts();
            Bind();
            if (vessel == null || vessel.packed) return;
            if (!wasNative)
            {
                recovery = new NativeRecovery(vessel); wasNative = true;
                foreach (var surface in new List<ModuleControlSurface>(originals.Keys)) if (surface != null) originals[surface] = new SurfaceState(surface);
            }
            if (vessel.parts.Count != partCount)
            {
                partCount = vessel.parts.Count;
                foreach (var surface in new List<ModuleControlSurface>(originals.Keys)) if (surface == null) originals.Remove(surface);
                ChatWindow.Notice("Vessel parts changed; local controller state refreshed.");
                if (mode != "spool") props = new NativePropulsion(vessel);
                power = new NativePower(vessel);
                reversers = new NativeReversers(vessel);
                flapScanAt = -100;
            }
            SafetyTick();
            if (rotorPark != null && Time.realtimeSinceStartup >= nextRotorPark)
            {
                nextRotorPark = Time.realtimeSinceStartup + .5f;
                string parking = rotorPark.Step(Time.realtimeSinceStartup, vessel.LandedOrSplashed, props.Sample());
                if (parking != "wait")
                {
                    rotorPark = null;
                    if (parking == "brake") { props.Set(0, 460, false, 100); ChatWindow.Notice("Rotors parked after measured RPM fell below 20."); }
                    else ChatWindow.Notice("Rotor parking " + parking + "; brakes left released.");
                }
            }
            if (mode == "hold" && Time.realtimeSinceStartup >= nextTerrain)
            {
                nextTerrain = Time.realtimeSinceStartup + 1;
                try
                {
                    var body = vessel.mainBody;
                    double highest = NavigationMath.TerrainAhead(vessel.latitude, vessel.longitude, FlightGlobals.ship_heading, vessel.srfSpeed, body.Radius,
                        (lat, lon) => body.pqsController == null ? double.NaN : Math.Max(0, body.pqsController.GetSurfaceHeight(body.GetRelSurfaceNVector(lat, lon)) - body.Radius));
                    terrainAheadM = highest; terrainFloor = double.IsNaN(highest) ? double.NaN : highest + terrainMargin;
                }
                catch (Exception) { terrainFloor = double.NaN; }
            }
            bool manual = (!InputLockManager.IsLocked(ControlTypes.PITCH) && (GameSettings.PITCH_UP.GetKey() || GameSettings.PITCH_DOWN.GetKey()))
                || (!InputLockManager.IsLocked(ControlTypes.ROLL) && (GameSettings.ROLL_LEFT.GetKey() || GameSettings.ROLL_RIGHT.GetKey()))
                || (!InputLockManager.IsLocked(ControlTypes.YAW) && (GameSettings.YAW_LEFT.GetKey() || GameSettings.YAW_RIGHT.GetKey()));
            NativeCommands.WriteStatus(Busy, mode, NativeIds.Vessel(vessel.id));
            if (LearnPilotInput(manual)) manual = false;   // learn waiting: the pilot may fly without ending the test flight
            LiveTick();
            if (manual && Busy && mode == "landing" && vessel != null && !vessel.LandedOrSplashed && PilotPolicy.OverrideHolds(overrideSince, Time.realtimeSinceStartup))
            {   // GROK LAND5d (15:55:32 ap=idle): a stick key during the autoland = a temporary pilot override, not a disengage
                if (double.IsNaN(overrideSince)) { overrideSince = Time.realtimeSinceStartup; ChatLog.Write("ap", "pilot override: manual input during the autoland - controls yours, autoland resumes " + PilotPolicy.OverrideResumeS + " s after hands off"); ChatWindow.Notice("Autoland: pilot override - resumes " + PilotPolicy.OverrideResumeS + " s after you let go."); }
                overrideUntil = Time.realtimeSinceStartup + PilotPolicy.OverrideResumeS; manual = false;
            }
            else if (!manual && !double.IsNaN(overrideSince) && Time.realtimeSinceStartup >= overrideUntil) overrideSince = double.NaN;
            if (manual && Busy) { overrideSince = double.NaN; Stop(); ChatWindow.Notice("Local autopilot released: manual input; plan paused."); }
            if (!manual)
            {
                try { TickPlan(); }
                catch (Exception ex) { Stop(); ChatWindow.Notice("Local plan paused: " + ex.Message); }
            }
            if (mode == "spool" && Time.realtimeSinceStartup >= nextSpool)
            {
                nextSpool = Time.realtimeSinceStartup + .5f;
                try
                {
                    props.Set((float)spool.Torque(Time.realtimeSinceStartup));
                    if (spool.Tick(Time.realtimeSinceStartup, ++spoolSequence, props.Sample()))
                    { mode = afterSpool; parkingReleased = true; SetGroup(vessel, KSPActionGroup.Brakes, false); ChatWindow.Notice("Rotor spool ready: " + mode); }
                    else if (spool.Result != null) { string failure = spool.Result; Stop(); ChatWindow.Notice(failure); }
                }
                catch (Exception ex) { Stop(); ChatWindow.Notice("Rotor spool stopped: " + ex.Message); }
            }
            ResidualTick();
            if (mode != loggedMode) { ChatLog.Write("ap", (loggedMode.Length == 0 ? "" : loggedMode + " -> ") + mode); loggedMode = mode; }
            if (!vessel.LandedOrSplashed && Time.realtimeSinceStartup >= nextTelemetry) { nextTelemetry = Time.realtimeSinceStartup + 30; ChatLog.Write("T", Telemetry()); }
            ExtrasTick();
            if (mode == "hold" && auto["master"] && !trimSuspended && Time.realtimeSinceStartup >= nextTrim)
            {
                nextTrim = Time.realtimeSinceStartup + 8;
                if (PilotPolicy.TrimAllowed(vessel.LandedOrSplashed, vessel.radarAltitude, Time.realtimeSinceStartup - trimHandoffAt, vessel.verticalSpeed, Roll()) && vessel.srfSpeed > 25 && !directPitch.HasValue) NudgeTrim(false);
            }
            if (watchTrimUntil > Time.realtimeSinceStartup && (Math.Abs(vessel.verticalSpeed) > Math.Abs(trimBaselineVs) + 2 || Math.Abs(Pitch() - trimBaselinePitch) > 3))
            { trimSuspended = true; watchTrimUntil = 0; ChatWindow.Notice("Auto-trim paused: flight drifted from level. Reset restores surfaces."); }
        }
        void SafetyTick()
        {
            float now = Time.realtimeSinceStartup;
            bool flying = !vessel.LandedOrSplashed;
            try { ChatterTick(); } catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] crew chatter: " + ex.Message); }
            try { power.Tick(vessel, now); }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] Local power recovery: " + ex.Message); }
            if (LocalVesselState.AlarmSequence != lastAlarmSeq)   // round 3: system alerts reach the chat again (bridge parity)
            {
                lastAlarmSeq = LocalVesselState.AlarmSequence;
                if (LocalVesselState.Alarm.Length > 0 && alertGate.Allow(LocalVesselState.Level, LocalVesselState.Alarm, Time.realtimeSinceStartup)) { string al = "[SYSTEM] " + LocalVesselState.Level.ToUpperInvariant() + ": " + LocalVesselState.Alarm; ChatWindow.Notice(al); ChatLog.Write("alert", al); CrewEmergency("alarm", LocalVesselState.Alarm); }
            }
            try { EngineWatchTick(now, flying); } catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] engine watch: " + ex.Message); }
            try { engines.Tick(vessel, now); }
            catch (Exception ex) { ChatWindow.Notice("Local engine restart failed: " + ex.Message); }
            try
            {
                if (NativeSafety.RebaselineOnEngage(Active, recoveryWasActive)) { recovery = new NativeRecovery(vessel); ChatLog.Write("ap", "recovery baseline re-snapshotted at engage (" + mode + ")"); }   // GROK FIX1
                recoveryWasActive = Active;
                bool revert = NativeSafety.ShouldRevert(Active, flying);
                int restored = recovery.Tick(vessel, now, revert);
                restored += reversers.Recover(vessel, now, revert);
                var noticed = new List<string>(recovery.Noticed); noticed.AddRange(reversers.Noticed);
                AutoWarpTick(Active && flying);   // GROK LAND5f/5g
                if (LearnGuards.RailsWarpNotice(learn != null && !learn.Done, TimeWarp.WarpMode == TimeWarp.Modes.HIGH, TimeWarp.CurrentRateIndex, ref learnWarpSaid))
                {   ChatWindow.Notice("LEARN: rails warp is on - test timers run on game time, so dwells and throttle steps are skipped. Use physics warp (or none) while Learn runs."); ChatLog.Write("learn", "rails warp during learn (notice shown)"); }
                if (Active && flying) GearWatch(now); else { gearGate.Reset(); if (!Active) gearSaid = null; }
                foreach (string what in noticed)
                {
                    string al = "[SYSTEM] WARNING: " + what + " configuration changed in flight - pilot reverting";
                    ChatWindow.Notice(al); ChatLog.Write("alert", al); PilotEvents.Add(what + " settings were changed in flight; pilot is restoring them", PilotEvents.Now);
                    CrewEmergency("tamper", what);
                }
                if (restored > 0) { ChatWindow.Notice("Local pilot: restored configuration on " + restored + " module(s)."); PilotEvents.Add("restored " + restored + " tampered module(s)", PilotEvents.Now); }
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] Local configuration recovery: " + ex.Message); }
            bool brakes = vessel.ActionGroups[KSPActionGroup.Brakes];
            int wheelCount = 0;
            if (!flying) foreach (Part p in vessel.parts) wheelCount += p.FindModulesImplementing<ModuleWheelBase>().Count;
            bool wheels = wheelCount > 0;
            if (vessel.Landed && wheels && roverDetectedId != vessel.id)
            {
                roverDetectedId = vessel.id;
                ChatLog.Write("rover", "rover detected: wheels " + wheelCount + ", mass " + vessel.GetTotalMass().ToString("0.00", CultureInfo.InvariantCulture) + " t");
            }
            string park = NativeSafety.ParkingAction(!flying, wasAirborne, parkingReleased, parkingSet, NativeSafety.ParkingIdle(mode, plan != null && plan.Running), wheels, brakes, prevBrakes && !brakes);
            if (park == "rearm") { parkingSet = false; parkingReleased = false; }
            else if (park == "set") { SetGroup(vessel, KSPActionGroup.Brakes, true); parkingSet = true; brakes = true; }
            else if (park == "released") parkingReleased = true;
            wasAirborne = flying;
            prevBrakes = brakes;
            try { AttitudeLockTick(); }
            catch (Exception ex) { attitudeLock = null; Debug.LogWarning("[KSPChatBridge] Attitude lock: " + ex.Message); }
            try { ScienceTick(); }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] Local science watcher: " + ex.Message); }
        }
        double Track() { Vector3d v = vessel.srf_velocity; return FlightPolicy.GroundTrack(Vector3d.Dot(v, vessel.north), Vector3d.Dot(v, vessel.east)); }   // ground track from surface velocity; NaN below 5 m/s so callers fall back to heading
        double Pitch() { return Math.Asin(FlightPolicy.Clamp(Vector3d.Dot(vessel.ReferenceTransform.up, vessel.upAxis), -1, 1)) * 180 / Math.PI; }
        double Roll() { return -Math.Atan2(Vector3d.Dot(vessel.ReferenceTransform.right, vessel.upAxis), Vector3d.Dot(-vessel.ReferenceTransform.forward, vessel.upAxis)) * 180 / Math.PI; }
        Dictionary<string, object> pendingLand; double ferryLat, ferryLon, ferryHandoff = 145000, ferryTod, ferrySink = 10; bool ferryDescending; float ferryNext, capNext;
        internal static double CapPlane = double.NaN; internal static string CapSource = ""; internal static bool CapHot; bool learnWarpSaid; float stallEmTraceNext;
        /// <summary>Approach stall: the learned flaps-full stall once flaps are 2+ (never below 1.0x of it), else the clean one.</summary>
        double ApproachStall0()
        {
            if (!double.IsNaN(stallOverride) || profile == null || double.IsNaN(profile.FlapStall) || flaps.Count == 0 || flapSet < 2) return landStall0;   // TAB-CRAFT1: an override is not scaled down by the flap stall
            double fs = PlaneProfile.ScaleSpeed(profile.FlapStall, profile.M0, landMass0);
            return Math.Min(landStall0, Math.Max(fs, .6 * landStall0));
        }
        /// <summary>GROK LAND5f AUTOWARP: physics warp on long straight wings-level legs (setting autoland_autowarp, default on); the player's own warp wins.</summary>
        /// <summary>GROK LAND5i: 1 = every running engine's actual throttle is within 5% of its request, 0 = spooling, -1 = no engine data.</summary>
        int EnginesSettled()
        {
            int n = 0; bool ok = true;
            try { foreach (var e in vessel.FindPartModulesImplementing<ModuleEngines>()) { if (!e.EngineIgnited || e.flameout) continue; n++; if (Math.Abs(e.currentThrottle - e.requestedThrottle) > .05f) ok = false; } }
            catch { return -1; }
            return n == 0 ? -1 : ok ? 1 : 0;
        }
        void AutoWarpTick(bool live)
        {
            if (TimeWarp.fetch == null || Time.realtimeSinceStartup < autoWarpNext) return;
            autoWarpNext = Time.realtimeSinceStartup + 1;
            bool ferry = live && mode == "hold" && pendingLand != null, land = live && mode == "landing" && runway != null, on = Bool(settingsData, "autoland_autowarp", true) && (ferry || land);
            if (!on && !autoWarp.Ours && autoWarp.Req <= 0) { if (!autoWarp.PlayerOwns) autoWarp.Reset(); return; }   // nothing of ours to undo
            if (TimeWarp.WarpMode == TimeWarp.Modes.HIGH) { if (!autoWarp.PlayerOwns) { autoWarp.PlayerOwns = true; ChatLog.Write("ap", "autowarp: rails warp set by the player - leaving warp alone"); } return; }
            int want = 0; double secs = double.NaN;
            if (on)
            {
                double xtrk = 0, dist = double.MaxValue; bool ga = false, cfg = false;
                if (land)
                {
                    var lp = runway.Pattern ?? runway.Locked; ga = runway.MissedClimb || runway.PatternActive || runway.Phase != "entry"; dist = runway.ToGo; cfg = flapSet < 3 && runway.ToGo < FlapsPolicy.EntryFlapsM + 3000;
                    if (lp != null && lp.Seg == 1) { secs = lp.SegLeft / Math.Max(30, vessel.srfSpeed); xtrk = lp.Xtrk; }
                }
                else secs = ferrySecsClear;
                want = AutoWarp.Index(AutoWarp.Want(Roll(), xtrk, secs, dist, ga, cfg));
            }
            bool owned = autoWarp.PlayerOwns;
            int req = autoWarp.Step(TimeWarp.CurrentRateIndex, want, on);
            if (!owned && autoWarp.PlayerOwns) { ChatLog.Write("ap", "autowarp: the player changed the warp - leaving it alone for this flight"); return; }
            if (req >= 0) { TimeWarp.fetch.Mode = TimeWarp.Modes.LOW; TimeWarp.SetRate(req, true); ChatLog.Write("ap", "autowarp: " + (req == 0 ? "1x" : (req + 1) + "x") + " (" + (!on ? "autowarp off/AP off" : double.IsNaN(secs) ? "turn/no straight" : Math.Round(secs) + " s straight") + ")"); }
        }
        void FerryTick()
        {
            if (pendingLand == null || mode != "hold" || vessel == null || Time.realtimeSinceStartup < ferryNext) return;
            ferryNext = Time.realtimeSinceStartup + 2;
            double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, ferryLat, ferryLon, vessel.mainBody.Radius);
            if (d < ferryHandoff) { var args = pendingLand; pendingLand = null; ChatLog.Write("ap", "phase APPROACH: " + (d / 1000).ToString("0") + " km out, slowing to the join speed"); ChatWindow.Notice(Command("land_plane", args)); return; }
            heading = NavigationMath.Bearing(vessel.latitude, vessel.longitude, ferryLat, ferryLon);   // great circle: re-aim every 2 s
            speed = Math.Min(speed, PilotPolicy.FerrySpeedCap);   // GROK LAND5f: 280 m/s in the ferry hold after takeoff
            ferrySecsClear = Math.Abs(FlightPolicy.Wrap(heading - FlightGlobals.ship_heading)) > 5 ? double.NaN : (d - ferryHandoff - (ferryDescending ? 0 : ferryTod)) / Math.Max(30, vessel.srfSpeed);
            if (!ferryDescending && d - ferryHandoff < ferryTod && altitude > PilotPolicy.HandoffAlt) { ferryDescending = true; altitude = Math.Max(PilotPolicy.HandoffAlt, vessel.altitude - vessel.radarAltitude + 600); ChatLog.Write("ap", "ferry: top of descent at " + (d / 1000).ToString("0") + " km, down to " + altitude.ToString("0") + " m at up to " + ferrySink.ToString("0") + " m/s"); }
            if (ferryDescending)
            {   // steep but controllable, easing near the hand-off altitude; the altitude hold takes over once there
                double above = vessel.altitude - altitude;
                directVs = above > 50 ? -(double?)PilotPolicy.DescentVs(above, ferrySink, vessel.indicatedAirSpeed, PilotPolicy.CapSpeed) : null;
                descentPitchMin = above > 50 ? -12 : -5;   // the normal -5 deg descent pitch floor would cap the sink near 19 m/s at cruise speed
            }
        }
        /// <summary>Per-plane low-altitude speed cap (Luke 12:00 AM): profile max level x 0.95, else mass/class; never below the commanded speed unless hot.</summary>
        void UpdateCap()
        {
            if (vessel == null || Time.realtimeSinceStartup < capNext) return; capNext = Time.realtimeSinceStartup + 1;
            double heat = 0; foreach (Part p in vessel.parts) if (p.maxTemp > 0) heat = Math.Max(heat, Math.Max(p.temperature / p.maxTemp, p.skinMaxTemp > 0 ? p.skinTemperature / p.skinMaxTemp : 0));
            bool known = profile != null && !double.IsNaN(profile.Perf.MaxLevel);
            CapPlane = PilotPolicy.PlaneCap(known ? profile.Perf.MaxLevel : double.NaN, vessel.totalMass, CraftClass.Gentle(CraftCls)); CapSource = known ? "TEST" : "EST";
            ApplyProfileTrust();   // TrustedStall + envelope only when fingerprint still matches (hard mismatch → conservative)
            CapHot = LearnGuards.CapHot(heat, vessel.geeForce, ApproachProfile.StructG, learn != null && !learn.Done && learn.TurnPhase);
            PilotPolicy.CapSpeed = PilotPolicy.EffectiveCap(CapPlane, holdSpeed ? speed : double.NaN, CapHot);
        }
        internal const double FastPitchUp = 3;   // LAND5p (was 1)
        double CpMax { get { return Math.Min(climbPitchMax, PilotPolicy.PitchEnvelope(CraftClass.Gentle(CraftCls))); } }   // heavy: 15 deg max climb pitch
        void BeginHold()
        {
            climbVsCap = double.NaN;   // GROK TO2: the takeoff climb-rate cap ends with any new hold
            var savedTrim = craftNotes.Load(vessel.vesselName);
            foreach (string key in new[] { "pitch_trim", "roll_trim", "yaw_trim", "pitch_deploy_bias" }) Num(savedTrim, key, 0);
            Stop(false);
            if (!lease.Acquire(NativeIds.Vessel(vessel.id), "native")) throw new InvalidOperationException("Another controller owns this vessel");
            mode = "hold"; throttle = vessel.ctrlState.mainThrottle; prevPitch = Pitch(); prevRoll = Roll();
            elevator = vessel.ctrlState.pitch; lastThrottle = Planetarium.GetUniversalTime() - 3;
            pitchIntegral = vsIntegral = 0; lastDesiredPitch = Pitch(); capLifted = false; prevSpd = -1; capture = null; trimSuspended = false;
            autoWarp.Reset(); rolloutBrakeLogged = false; rolloutLandedS = 0;   // GROK LAND5f
            takeoverT = 0; takePitch = vessel.ctrlState.pitch; takeRoll = vessel.ctrlState.roll; takeYaw = vessel.ctrlState.yaw; gate.Seed(vessel.ctrlState.mainThrottle);   // T5: smooth takeover - start from the pilot's real stick + throttle, never a stale controller state
            ChatLog.Write("ap", "smooth takeover: pitch/roll/yaw ramp from the pilot's stick (" + takePitch.ToString("0.00") + "/" + takeRoll.ToString("0.00") + "/" + takeYaw.ToString("0.00") + ") to the hold over " + PilotPolicy.TakeoverSeconds.ToString("0.#") + " s; throttle gate seeded at " + Math.Round(vessel.ctrlState.mainThrottle * 100) + "%");
            SetGroup(vessel, KSPActionGroup.SAS, false);
            ApplyCraftNotes();
            if (!props.HasLift(vessel)) engines.Request(vessel, Time.realtimeSinceStartup);
        }
        void Fly(FlightCtrlState c)
        {
            if (mode == "idle" || vessel == null || vessel != FlightGlobals.ActiveVessel || vessel.packed) return;
            if (LearnPilotFlying) return;   // learn wait: pilot has the controls
            if (mode == "landing" && !double.IsNaN(overrideSince))
            {   // GROK LAND5d: pilot override - hands off the controls until OverrideResumeS after the last key, then a smooth takeover
                if (Time.realtimeSinceStartup < overrideUntil) { overridePending = true; return; }
                if (overridePending) { overridePending = false; takeoverT = 0; takePitch = c.pitch; takeRoll = c.roll; takeYaw = c.yaw; gate.Seed(c.mainThrottle); ChatLog.Write("ap", "pilot override ended: autoland resumed (" + (runway != null ? runway.Phase : "") + ")"); }
            }
            if (RocketFly(c)) return;
            try
            {
                if (mode == "spool") return;
                if (mode == "rover")
                {
                    if (!vessel.Landed) { Stop(); ChatWindow.Notice("Rover speed hold stopped: rover is no longer landed."); return; }
                    double roverDt = Math.Max(.001, Math.Min(.1, Time.fixedDeltaTime));
                    double targetSpeed = roverTargetSpeed;
                    double destinationDistance = double.NaN;
                    if (roverNavigating)
                    {
                        double distance = NavigationMath.Distance(vessel.latitude, vessel.longitude, roverDestinationLat, roverDestinationLon, vessel.mainBody.Radius);
                        destinationDistance = distance;
                        if (KscRoverPolicy.ReachedDestination(distance))
                        {
                            string destination = roverDestination;
                            ChatLog.Write("rover", "arrived at " + destination + " (" + distance.ToString("0.0", CultureInfo.InvariantCulture) + " m)");
                            ChatWindow.Notice("Rover arrived at " + destination + ".");
                            Stop(); return;
                        }
                        targetSpeed = KscRoverPolicy.ApproachSpeed(targetSpeed, distance);
                        roverHeading = NavigationMath.Bearing(vessel.latitude, vessel.longitude, roverDestinationLat, roverDestinationLon);
                        roverHeadingEnabled = true;
                    }
                    RoverProfile learnedRover = CurrentRoverProfile();
                    if (learnedRover != null)
                    {
                        targetSpeed = KscRoverPolicy.LearnedTopSpeed(targetSpeed, learnedRover.TopSpeed);
                        double headingError = roverHeadingEnabled ? FlightPolicy.Wrap(roverHeading - FlightGlobals.ship_heading) : 0;
                        targetSpeed = KscRoverPolicy.LearnedTurnSpeed(targetSpeed, learnedRover.SafeTurnSpeed, headingError);
                        if (roverNavigating)
                        {
                            targetSpeed = KscRoverPolicy.LearnedBrakingSpeed(targetSpeed, destinationDistance, learnedRover.BrakingDistance, 10);
                            targetSpeed = Math.Min(targetSpeed, KscRoverPolicy.ApproachSpeed(roverTargetSpeed, destinationDistance));
                        }
                    }
                    double roverPitch = Pitch(), roverRoll = Roll(), sideSpeed = Vector3d.Dot(vessel.srf_velocity, vessel.ReferenceTransform.right);
                    if (RoverTopSpeedTick(vessel.srfSpeed, roverPitch, roverRoll)
                        || RoverBrakingTick(vessel.srfSpeed, roverPitch, roverRoll)
                        || RoverTurnTick(vessel.srfSpeed, roverPitch, roverRoll)) return;
                    bool slowForSafety = KscRoverPolicy.ReduceSpeed(roverPitch, roverRoll, sideSpeed, vessel.srfSpeed);
                    bool brakeForRollover = KscRoverPolicy.BrakeForRollover(roverRoll);
                    if (slowForSafety != roverSafetySlow || brakeForRollover != roverSafetyBrake)
                    {
                        string detail = "safety: pitch " + roverPitch.ToString("0.0", CultureInfo.InvariantCulture) + " deg, roll "
                            + roverRoll.ToString("0.0", CultureInfo.InvariantCulture) + " deg, sideways " + sideSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s";
                        ChatLog.Write("rover", slowForSafety || brakeForRollover
                            ? detail + (slowForSafety ? "; speed target halved" : "") + (brakeForRollover ? "; braking for rollover" : "")
                            : "safety cleared");
                        roverSafetySlow = slowForSafety; roverSafetyBrake = brakeForRollover;
                    }
                    double safeTargetSpeed = slowForSafety ? targetSpeed * .5 : targetSpeed;
                    double error = safeTargetSpeed - vessel.srfSpeed;
                    bool braking = brakeForRollover || vessel.srfSpeed > safeTargetSpeed + 2;
                    if (braking)
                    {
                        roverIntegral = 0;
                        c.wheelThrottle = 0;
                        SetGroup(vessel, KSPActionGroup.Brakes, true);
                    }
                    else
                    {
                        SetGroup(vessel, KSPActionGroup.Brakes, false);
                        roverIntegral = FlightPolicy.Clamp(roverIntegral + .05 * error * roverDt, 0, 1);
                        c.wheelThrottle = (float)FlightPolicy.Clamp(.25 * error + roverIntegral, 0, 1);
                    }
                    c.wheelSteer = roverTurnTrial != null ? .2f : roverHeadingEnabled
                        ? (float)KscRoverPolicy.Steering(FlightPolicy.Wrap(roverHeading - FlightGlobals.ship_heading), vessel.srfSpeed)
                        : 0;
                    if (roverBrakingTrial != null && roverBrakingTrial.Braking)
                    {
                        roverIntegral = 0; c.wheelThrottle = 0; SetGroup(vessel, KSPActionGroup.Brakes, true);
                    }
                    c.yaw = 0;
                    c.mainThrottle = vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0;
                    return;
                }
                if (mode == "vertical landing")
                {
                    string previous = verticalLanding.Phase;
                    verticalLanding.Fly(vessel, c, Planetarium.GetUniversalTime(), Math.Max(.001, Time.fixedDeltaTime));
                    if (verticalLanding.Phase != previous) ChatWindow.Notice("Local powered descent: " + verticalLanding.Phase);
                    if (verticalLanding.Phase == "abort")
                    {
                        c.mainThrottle = 0; vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0; gate.Seed(0, "powered descent abort");
                        Stop(); ChatWindow.Notice("Powered descent aborted: insufficient thrust."); return;
                    }
                    if (verticalLanding.Phase == "landed") { Stop(false); SetGroup(vessel, KSPActionGroup.SAS, true); }
                    return;
                }
                if (mode == "taxi")
                {
                    TaxiHotSwap(); taxi.Step(Planetarium.GetUniversalTime(), vessel.latitude, vessel.longitude, FlightGlobals.ship_heading, vessel.srfSpeed, vessel.mainBody.Radius, vessel.LandedOrSplashed, poweredTaxi, Twr());
                    c.wheelSteer = (float)taxi.Wheel; c.wheelThrottle = (float)taxi.Drive; c.yaw = (float)taxi.Yaw;
                    c.mainThrottle = (float)gate.Step(vessel.LandedOrSplashed ? taxi.Throttle : Math.Max(.05, c.mainThrottle), Time.fixedDeltaTime, "ground", "taxi");
                    SetGroup(vessel, KSPActionGroup.Brakes, taxi.Brakes);
                    if (taxi.Result != null) { string result = taxi.Result; Stop(!result.Contains("arrived")); ChatWindow.Notice(result); }
                    return;
                }
                double dt = Math.Max(.001, Math.Min(.1, Time.fixedDeltaTime));
                double pitch = Pitch(), roll = Roll(), kin = FlightPolicy.Clamp(Math.Pow(10 / Math.Max(1, vessel.GetTotalMass()), .4), .3, 1.3);
                double q = FlightPolicy.Wrap(pitch - prevPitch) / dt, p = FlightPolicy.Wrap(roll - prevRoll) / dt;
                prevPitch = pitch; prevRoll = roll;
                if (!vessel.LandedOrSplashed && mode != "takeoff" && Time.realtimeSinceStartup >= nextStallSample && learner != null)
                {   // in-flight stall learning: steady level-ish samples of AoA vs CL (no stalling needed)
                    nextStallSample = Time.realtimeSinceStartup + .5f; var tr = vessel.ReferenceTransform; Vector3d vel = vessel.srf_velocity;
                    if (tr != null && vel.magnitude > 20)
                    {
                        double aoa = Math.Atan2(Vector3d.Dot(vel, tr.forward), Vector3d.Dot(vel, tr.up)) * 180 / Math.PI;
                        learner.Add(aoa, vessel.dynamicPressurekPa * 1000, vessel.GetTotalMass() * 1000, vessel.geeForce, liftArea, roll, vessel.verticalSpeed);
                    }
                    if (Time.realtimeSinceStartup >= nextStallSave) { nextStallSave = Time.realtimeSinceStartup + 20; stall = LearnedStall(); SaveLearner(); }
                }
                if (mode == "takeoff")
                {
                    if (NativeSafety.ReleaseForTakeoff(mode, takeoff.Phase, vessel.LandedOrSplashed, vessel.ActionGroups[KSPActionGroup.Brakes])) { parkingReleased = true; SetGroup(vessel, KSPActionGroup.Brakes, false); }
                    double nowT = Planetarium.GetUniversalTime();
                    if (tground != null && !vessel.ActionGroups[KSPActionGroup.Brakes] && tground.Release(nowT))
                    { pitchIntegral = vsIntegral = 0; elevator = 0; bankCmd = 0; pitchShape.Reset(pitch); ChatLog.Write("g", "takeoff brake release: thr ramp 25%/s"); }
                    if (tground != null && tground.Released && takeoff.Phase != "climbout complete" && Time.realtimeSinceStartup >= gTraceNext)   // GROK TO1: trace through rotation + liftoff (4 Hz first 5 s, then 1 Hz)
                    { gTraceNext = Time.realtimeSinceStartup + (tground.Since(nowT) < 5 ? .25f : 1f); ChatLog.Write("g", "ph=" + takeoff.Phase + " vr=" + takeoff.Vr.ToString("0") + " agl=" + vessel.radarAltitude.ToString("0") + " release+" + tground.Since(nowT).ToString("0.00") + "s spd=" + vessel.srfSpeed.ToString("0.0") + " hdg=" + FlightGlobals.ship_heading.ToString("0.0") + " pit=" + pitch.ToString("0.0") + " q=" + q.ToString("0.0") + " thr=" + c.mainThrottle.ToString("0.00") + " wheel=" + tground.Wheel.ToString("0.00") + " yaw=" + tground.Yaw.ToString("0.00") + " elev=" + elevator.ToString("0.00") + " g=" + vessel.geeForce.ToString("0.0")); }
                    double rotPr = profile != null ? profile.Perf.PitchRate : double.NaN, rotA = profile != null ? profile.AccelAt(vessel.totalMass) : double.NaN;
                string phBefore = takeoff.Phase; bool airBefore = !double.IsNaN(takeoff.LiftoffSpeed);
                directPitch = takeoff.Step(Planetarium.GetUniversalTime(), vessel.srfSpeed, vessel.radarAltitude, vessel.LandedOrSplashed, stall, liftoffSpeed, rotPr, rotA, stallEstimate);
                bool rs = TakeoffMission.RotationStalled(vessel.LandedOrSplashed, takeoff.Phase, vessel.srfSpeed, takeoff.Vr, pitch);
                if (rs && !rotateStalled) ChatLog.Write("takeoff", "nose not coming up at " + vessel.srfSpeed.ToString("0") + " m/s (Vr " + takeoff.Vr.ToString("0") + ", pitch " + pitch.ToString("0.0") + ", elev " + elevator.ToString("0.00") + "): takeoff flaps UP, check pitch-surface roles");
                rotateStalled = rs || (rotateStalled && vessel.LandedOrSplashed);
                if (phBefore == "roll" && takeoff.Phase == "rotate") ChatLog.Write("takeoff", "Vr=" + takeoff.Vr.ToString("0") + " (stall est " + (double.IsNaN(stallEstimate) ? "-" : stallEstimate.ToString("0")) + ", " + takeoff.VrSource + ") rotating at " + vessel.srfSpeed.ToString("0") + " m/s");
                if (!airBefore && !double.IsNaN(takeoff.LiftoffSpeed)) ChatLog.Write("takeoff", "wheels up at " + takeoff.LiftoffSpeed.ToString("0") + " m/s (Vr " + takeoff.Vr.ToString("0") + "); climb speed=" + takeoff.V2.ToString("0") + " pitch=" + TakeoffMission.ShallowPitch.ToString("0") + "->" + takeoff.MaxPitch.ToString("0") + " (shallow until V2, max pitch setting)");
                DecisionLog.Use("rotate Vr", double.IsNaN(liftoffSpeed) ? "default 1.15x stall" : !double.IsNaN(rotPr) && !double.IsNaN(rotA) ? "learned liftoff - pitch-rate lead" : "learned liftoff", takeoff.Vr);
                    if (takeoff.Phase == "takeoff timeout") { c.mainThrottle = 0; gate.Seed(0, "takeoff timeout on the ground"); SetGroup(vessel, KSPActionGroup.Brakes, true); Stop(); ChatWindow.Notice("Takeoff timed out on the ground."); return; }
                    if (takeoff.Phase == "climbout complete")
                    {
                        mode = landingAfterTakeoff ? "landing" : "hold";
                        if (mode == "hold")
                        {   // GROK TO2: heading after liftoff + safe altitude; climb-rate cap until the target altitude
                            if (!double.IsNaN(toHeading)) { heading = toHeading; ApFieldSync.Heading(heading); }
                            climbVsCap = toClimbVs;
                            ChatLog.Write("takeoff", "climbout complete at " + vessel.radarAltitude.ToString("0") + " m AGL, " + vessel.srfSpeed.ToString("0") + " m/s: heading " + heading.ToString("000") + ", climb " + (double.IsNaN(climbVsCap) ? "auto" : climbVsCap.ToString("0") + " m/s") + " to " + altitude.ToString("0") + " m");
                        }
                        landingAfterTakeoff = false; directPitch = null; directBank = pendingCircle;
                        throttle = PilotPolicy.HandoffThrottle(throttle);
                        speed = ClimbSpeedTarget();   // LEARN Vy when known, else 1.8x stall (AUTOPILOT_REVIEW #3/#12)
                        c.pitchTrim = vessel.ctrlState.pitchTrim = 0; trimHandoffAt = Time.realtimeSinceStartup; trimSuspended = false;   // trim starts clean after takeoff
                        if (!double.IsNaN(takeoff.LiftoffSpeed)) { if (TakeoffMission.LiftoffUsable(takeoff.LiftoffSpeed, takeoff.Vr)) RememberLiftoff(takeoff.LiftoffSpeed); else ChatLog.Write("takeoff", "late liftoff (rotation-limited) - not used for the stall estimate"); ChatLog.Write("takeoff", "liftoff at " + takeoff.LiftoffSpeed.ToString("0") + " m/s (Vr " + takeoff.Vr.ToString("0") + ", stall est " + stall.ToString("0") + ")"); }
                        if (pendingCircle.HasValue) ChatWindow.Notice("Climbing out; circling now at " + Math.Abs(pendingCircle.Value).ToString("0") + " deg bank.");
                        pendingCircle = null;
                    }
                    else
                    {
                        directBank = 0; speed = profile != null ? Math.Max(ClimbSpeedTarget(), 1.5 * stall) : 200;   // no profile: unchanged roll target
                        parkingReleased = true;
                        if (vessel.ActionGroups[KSPActionGroup.Brakes]) SetGroup(vessel, KSPActionGroup.Brakes, false);   // live bug: brakes were held until the 5%-stepped throttle reached 60%
                        if (TakeoffMission.GearUp(vessel.radarAltitude, vessel.verticalSpeed)) SetGroup(vessel, KSPActionGroup.Gear, false);   // Oct 12: after a positive climb
                        if (tground != null && vessel.LandedOrSplashed)
                        {   // gentle ground roll: rate-limited, speed-scaled steering + rudder toward the takeoff line
                            double err = tground.HeadingError(FlightGlobals.ship_heading, tground.CrossTrack(vessel.latitude, vessel.longitude, vessel.mainBody.Radius));
                            double gRate = double.IsNaN(tgHdgPrev) ? 0 : FlightPolicy.Wrap(FlightGlobals.ship_heading - tgHdgPrev) / dt; tgHdgPrev = FlightGlobals.ship_heading;
                            tground.Steer(err, vessel.srfSpeed, dt, gRate); c.wheelSteer = (float)tground.Wheel; c.yaw = (float)tground.Yaw;
                        }
                        else c.wheelSteer = (float)FlightPolicy.WheelSteering(heading - FlightGlobals.ship_heading, .5);
                    }
                }
                if (mode == "landing" || mode == "takeoff")
                    // flaps: FlapsTick (NativeFlaps.cs)
                landFloor = false;
                if (mode == "landing" && needStallStudy && stallStudy == null && runway.Phase == "entry"
                    && vessel.radarAltitude > 350 && Math.Abs(roll) < 10 && vessel.srfSpeed < 110)
                    stallStudy = new StallStudy(Planetarium.GetUniversalTime(), vessel.altitude, FlightGlobals.ship_heading);
                if (mode == "landing" && stallStudy != null && !stallStudy.Finished)
                {
                    double pathAngle = Math.Atan2(vessel.verticalSpeed, Math.Sqrt(Math.Max(0, vessel.srfSpeed * vessel.srfSpeed - vessel.verticalSpeed * vessel.verticalSpeed))) * 180 / Math.PI;
                    stallStudy.Step(Planetarium.GetUniversalTime(), vessel.srfSpeed, pitch - pathAngle, vessel.verticalSpeed, vessel.radarAltitude, roll);
                    if (stallStudy.Finished)
                    {
                        needStallStudy = false;
                        if (stallStudy.Measured.HasValue)
                        {
                            stall = stallGuess = stallStudy.Measured.Value;
                            object cacheObject;
                            var cache = settingsData.TryGetValue("stall_speeds", out cacheObject) ? cacheObject as Dictionary<string, object> : null;
                            if (cache == null) settingsData["stall_speeds"] = cache = new Dictionary<string, object>();
                            cache[vessel.vesselName] = stall;
                            try { Save(); } catch (Exception) { ChatWindow.Notice("Stall speed measured for this flight; settings could not be saved."); }
                        }
                        pitchIntegral = vsIntegral = 0;
                        ChatWindow.Notice(stallStudy.Result);
                    }
                    else
                    {
                        heading = stallStudy.Heading; altitude = stallStudy.Altitude; speed = 0;
                        directVs = FlightPolicy.Clamp((altitude - vessel.altitude) * .15, -3, 3);
                    }
                }
                if (learn != null) LearnTick(c, dt, pitch);
                if (mode == "landing" && (stallStudy == null || stallStudy.Finished))
                {
                    if (landMass0 <= 0) { landMass0 = vessel.totalMass; landStall0 = stall; }
                    if (landMass0 > 0)
                    {   // LEARN idle decel first (same as ferry), then live DecelLearner, then mass estimate (AUTOPILOT_REVIEW #5)
                        double dA = profile != null && !double.IsNaN(profile.Decel) ? profile.DecelAt(vessel.totalMass) : double.NaN; string dSrc = "learned decel";
                        if (double.IsNaN(dA) || dA <= 0) { dSrc = "live decel learner"; dA = Decel.Estimate(vessel.vesselName, ApproachProfile.HiSpeed(stall), ApproachProfile.AppSpeed(stall), true); }
                        if (double.IsNaN(dA) || dA <= 0) { dSrc = "default mass estimate"; dA = ApproachProfile.MassDecel(vessel.totalMass); }
                        runway.DecelA = dA / Math.Sqrt(shold.K); DecisionLog.Use("decel", dSrc, runway.DecelA); runway.LearnedDecel = dSrc == "default mass estimate" ? double.NaN : dA;
                        runway.LearnedSink = profile != null && profile.Dives != null && profile.Dives.Count > 0 ? PilotPolicy.DescentSink(profile.Dives, vessel.srfSpeed, PilotPolicy.CapSpeed) : double.NaN;   // Oct 12: approach descent limit from the learned idle dives   // Oct 12: predictive speed gate needs learned data
                    }
                    string landingPhaseBefore = runway.Phase;
                    if (!vessel.LandedOrSplashed)
                    { landingAoa = pitch - FlarePolicy.PathAngle(vessel.srfSpeed, vessel.verticalSpeed); lastAirVs = vessel.verticalSpeed; }
                    runway.Step(vessel.latitude, vessel.longitude, vessel.altitude, vessel.radarAltitude, vessel.srfSpeed, vessel.LandedOrSplashed, vessel.mainBody.Radius, ApproachProfile.StallAt(ApproachStall0(), landMass0, vessel.totalMass), Track(), FlightGlobals.ship_heading);
                    if (runway.Phase == "flare" && landingPhaseBefore != "flare" && runway.FlareFast) { flareEntryPitch = pitch; ChatLog.Write("approach", "flare FAST (" + Math.Round(vessel.srfSpeed) + " m/s, > landing + 10): small pitch-up (entry + " + FastPitchUp + " deg), idle + airbrakes + full flaps, sink " + (-runway.DesiredVs).ToString("0.0") + " m/s (flare_sink), long landing"); }
            if (runway.Phase == "flare" && landingPhaseBefore != "flare")
                        ChatLog.Write("approach", profileTrusted && profile != null && FlarePolicy.ValidAoa(profile.TouchdownAoa) ? "flare: learned touchdown AoA " + profile.TouchdownAoa.ToString("0.0") + " deg" : "flare: no trusted touchdown AoA; using sink-rate fallback");
                    if (runway.Phase == "flare" && runway.FlareFastEnded && landingPhaseBefore == "flare" && !flareFastEndLogged) { flareFastEndLogged = true; if (AirbrakesByAutopilot && vessel.ActionGroups[KSPActionGroup.Brakes]) { SetGroup(vessel, KSPActionGroup.Brakes, false); AirbrakesByAutopilot = false; } ChatLog.Write("approach", "flare: fast flare over at " + Math.Round(vessel.srfSpeed) + " m/s (<= landing + 5) - airbrakes IN, normal flare (sink " + (-runway.DesiredVs).ToString("0.0") + ")"); }   // LAND5q
            if (runway.Phase != "flare") { flareFastEndLogged = false; }
            liftDump = runway.Phase == "rollout";   // LAND5q: dump lift from first wheel contact (brake surfaces + airbrakes)
            if (runway.Phase != "rollout") { rollHdgPrev = double.NaN; rollHdgRate = double.NaN; rollSteer = double.NaN; }   /* ROLL1 */ if (runway.Phase == "rollout" && landingPhaseBefore != "rollout") { foreach (var ab in vessel.FindPartModulesImplementing<ModuleAeroSurface>()) if (ab != null && !ab.deploy) { ab.deploy = true; dumpedAero.Add(ab); } ChatLog.Write("approach", "touchdown: lift dump - spoilers / airbrakes out"); }
            if (runway.Phase == "rollout" && landingPhaseBefore != "rollout")
                    {
                        ChatLog.Write("approach", "touchdown sink " + Math.Abs(lastAirVs).ToString("0.0") + " m/s: " + ApproachProfile.TouchdownClass(lastAirVs) + (Math.Abs(lastAirVs) > 5 ? " (target 3)" : ""));   // GROK LAND5c
                        if (vessel.LandedOrSplashed && FlarePolicy.ValidAoa(landingAoa) && (profile == null || profileTrusted))
                        {
                            var landedProfile = profile ?? new PlaneProfile(); landedProfile.TouchdownAoa = landingAoa;
                            SaveProfile(landedProfile, false);
                            ChatLog.Write("learn", "touchdown AoA saved: " + landingAoa.ToString("0.0") + " deg (last airborne sample)");
                        }
                        else ChatLog.Write("learn", "touchdown AoA not saved: invalid sample or untrusted profile");
                        ChatLog.Write("approach", "rollout: lower nose as speed drops, limited to 2.5 deg/s");
                    }
                    if (runway.Phase == "go around" && goArounds < 3) { goArounds++; CrewEmergency("landing", runway.Why.Length > 0 ? runway.Why : "missed touchdown"); runway.GoAroundReset(); callouts.Reset(); ChatWindow.Notice("Local autoland: going around - " + (runway.Why.Length > 0 ? runway.Why : "missed touchdown") + "; re-entering the approach (" + goArounds + "/3)."); ChatLog.Write("approach", "go around " + goArounds + ": " + runway.Why); runway.Why = ""; directPitch = null; directVs = null; }   // round 3: keep landing
                if (runway.Phase == "go around") { goAroundFlaps = flapSet > 0; altitude = vessel.altitude + 500; speed = 1.5 * stall; mode = "hold"; directPitch = null; directVs = null; SetGroup(vessel, KSPActionGroup.Brakes, false); AirbrakesByAutopilot = false; string gw = runway.Why.Length > 0 ? runway.Why : "missed touchdown"; CrewEmergency("landing", "3 go-arounds used: " + gw); ChatWindow.Notice("Local autoland: 3 go-arounds used (last: " + gw + "). Holding " + Math.Round(altitude) + " m, heading " + Math.Round(heading) + ". Say \"land\" to try again or take control."); ChatLog.Write("approach", "go-around limit reached, holding: " + gw); }
                    else if (runway.Phase == "stopped") { c.mainThrottle = 0; gate.Seed(0, "stopped on the runway"); Stop(false); return; }
                    else
                    {
                        heading = runway.DesiredHeading; altitude = runway.DesiredAltitude; speed = runway.DesiredSpeed; directVs = runway.DesiredVs;
                        descentPitchMin = ApproachProfile.LandingPitchMin(runway.DesiredVs);   // Oct 12: -5 deg floor capped recapture sink; allow -12 when a steep sink is asked
                        if (runway.EnergyLog.Length > 0) { ChatLog.Write("approach", runway.EnergyLog); runway.EnergyLog = ""; }
                        if (runway.RouteLog != loggedRoute) { loggedRoute = runway.RouteLog; ChatLog.Write("approach", "chart " + runway.Kind + " " + loggedRoute); }
                    if (runway.PatternLog != loggedPattern) { loggedPattern = runway.PatternLog; if (loggedPattern.Length > 0) ChatLog.Write("approach", loggedPattern); }   // GROK LAND5c
            if ((runway.Phase == "final" || runway.Phase == "flare") && runway.Distance < ApproachChart.ShortFix && !vessel.LandedOrSplashed) { string co = callouts.Step(vessel.radarAltitude); if (co != null) { ChatLog.Write("approach", "callout: " + co); ChatWindow.Notice(co); } }   // GROK LAND5c: radar-altitude callouts
            if (runway.LockLog != loggedLock) { loggedLock = runway.LockLog; if (loggedLock.Length > 0) ChatLog.Write("approach", loggedLock); }   // GROK LAND5
            if (runway.FollowLog != loggedFollow) { loggedFollow = runway.FollowLog; if (loggedFollow.Length > 0) ChatLog.Write("approach", loggedFollow); }
            if (runway.DubinsLog != loggedDubins) { loggedDubins = runway.DubinsLog; if (loggedDubins.Length > 0) ChatLog.Write("approach", loggedDubins); }   // GROK JOIN1
                    if (runway.SmoothLog != loggedSmooth) { if (runway.JoinLog.Length > 0) ChatLog.Write("approach", runway.JoinLog); loggedSmooth = runway.SmoothLog; if (loggedSmooth.Length > 0) ChatLog.Write("approach", "plane " + Math.Round(runway.PlanSpeed) + " m/s " + runway.PlanG.ToString("0.0") + " g bank " + Math.Round(runway.PlanBank) + " r=" + Math.Round(runway.PlanRadius) + " m: " + loggedSmooth); }
                        { bool airPhase = runway.Phase == "entry" || runway.Phase == "intercept" || runway.Phase == "final" || runway.Phase == "flare", on = vessel.ActionGroups[KSPActionGroup.Brakes]; bool allowed = AirbrakeGate.Allowed(vessel.LandedOrSplashed, vessel.altitude - runway.Elevation, vessel.srfSpeed, runway.DesiredSpeed, brakesMoveControls, powerFloor) && !(runway.Phase == "final" && vessel.srfSpeed < runway.DesiredSpeed - StepThrottle.HighSlackMs);   // SPD2: speedbrakes stowed below plan - 3   // GROK LAND5e
                          bool deployOk = CraftOverrides.On("airbrakes", true) && AirbrakeGate.DeployOk(!runway.EnergyLow && vessel.verticalSpeed > runway.DesiredVs - 5, double.IsNaN(stepThr.Thr) ? 0 : stepThr.Accel, runway.DesiredVs > vessel.verticalSpeed + 3);   // GROK LAND5j: not while decelerating hard / pulling up   // GROK LAND4: no new deploy while low or sinking 5 m/s faster than the path (pitch, not drag)   // GROK FIX6: never airbrakes against floor power
                          if (airPhase && runway.OverrideActive && runway.Phase != "flare" && allowed && CraftOverrides.On("airbrakes", true))
                          {   // GROK LAND5m: override craft - airbrakes only while idle would still arrive > threshold_speed + 5 (off below +2)
                              bool want = runway.OverrideBrakes && deployOk || runway.OverrideBrakes && on;
                              if (want != on) { SetGroup(vessel, KSPActionGroup.Brakes, want); AirbrakesByAutopilot = want; ChatLog.Write("brakes", "airbrakes " + (want ? "OUT" : "IN") + ": predicted " + Math.Round(runway.PredThr) + " m/s at the threshold vs " + Math.Round(ApproachProfile.OverrideThreshold) + ", " + Math.Round(runway.Distance) + " m out"); }
                          }
                          else if (airPhase && !runway.FastContinue && !(runway.FlareFast && runway.Phase == "flare") && airGate.Step(Time.time, on, double.IsNaN(stepThr.Thr) ? vessel.srfSpeed : stepThr.PredictOver(vessel.srfSpeed), runway.DesiredSpeed, allowed, deployOk)) { SetGroup(vessel, KSPActionGroup.Brakes, airGate.On); AirbrakesByAutopilot = airGate.On; if (AirbrakeGate.LogOnce(ref brakeLogKey, airGate.Key + "|" + runway.Phase)) ChatLog.Write("brakes", airGate.Why + ", phase " + runway.Phase); }   // LAND5p: fast flare owns the airbrakes; log once per state change
                            if ((runway.FastContinue || runway.FlareFast && runway.Phase == "flare") && !vessel.LandedOrSplashed && !on) { SetGroup(vessel, KSPActionGroup.Brakes, true); AirbrakesByAutopilot = true; if (!fastBrakeLogged) { fastBrakeLogged = true; ChatLog.Write("brakes", "airbrakes: fast at touchdown, continuing (LAND5c)"); } }
                            if (!runway.FastContinue && !runway.FlareFast) fastBrakeLogged = false; }   // GROK FIX3: +8/+2 m/s hysteresis, 3 s dwell
                        runway.Heavy = CraftClass.Gentle(CraftCls); runway.Warp = TimeWarp.CurrentRate;   // GROK LAND4f
                        double vsBefore = directVs.Value; directVs = PilotPolicy.ApproachFloorVs(runway.Phase, runway.Distance, vessel.radarAltitude, vessel.altitude, terrainFloor, directVs.Value, vessel.verticalSpeed, runway.Heavy);
                        landFloor = directVs.Value > vsBefore + .5 || (runway.Phase == "final" && RunwayMission.BelowGsM(runway.Ils) > RunwayMission.GsMargin && runway.Distance > ApproachChart.ShortFix && vessel.verticalSpeed < -3);
                        if (landFloor && Time.realtimeSinceStartup >= gTraceNext) { gTraceNext = Time.realtimeSinceStartup + 1; ChatLog.Write("g", "approach floor: agl=" + vessel.radarAltitude.ToString("0") + " vs=" + vessel.verticalSpeed.ToString("0") + " belowGS=" + (double.IsNaN(RunwayMission.BelowGsM(runway.Ils)) ? "n/a" : RunwayMission.BelowGsM(runway.Ils).ToString("0")) + " spd=" + vessel.srfSpeed.ToString("0") + " pit=" + pitch.ToString("0")); }   // AGL floor: never sink into a hill on approach
                        if (runway.Phase == "entry" || runway.Phase == "intercept") landingGearDown = false; else if (runway.Gear && !landingGearDown) { SetGroup(vessel, KSPActionGroup.Gear, true); landingGearDown = true; configChangedAt = Time.realtimeSinceStartup; ChatLog.Write("approach", "gear down on final (" + Math.Round(runway.Distance / 100) / 10 + " km)"); }   // never fight the player on the outbound leg
                        if (TouchAndGoNow(runway.Phase)) return;
                        if (runway.Phase == "rollout")
                        {
                            c.mainThrottle = (float)reverseRollout.Tick(vessel.LandedOrSplashed, vessel.srfSpeed, Planetarium.GetUniversalTime(), reverse => (!reverse || Bool(settingsData, "autoland_reversers", true)) && reversers.Set(vessel, reverse));
                            if (!vessel.LandedOrSplashed) c.mainThrottle = Math.Max(.05f, c.mainThrottle);
                            c.mainThrottle = (float)gate.Step(c.mainThrottle, dt, "ground", "rollout idle/reversers");
                    if (!vessel.LandedOrSplashed) rolloutAirS += dt; else rolloutAirS = 0;
                    derot.NoseDownPitch = Derotation.NoseDownFor(CraftOverrides.L(CraftOverrides.RolloutPitch));   // LAND5n
                            if (Derotation.Airborne(rolloutAirS))   /* LAND5n: a real bounce, not a wheel skip */
                            {   // GROK LAND5i: bounced - hold the touchdown attitude (no derotation in the air, which slammed the nose gear and porpoised)
                                if (double.IsNaN(bouncePitch)) { bouncePitch = Derotation.BounceStart(pitch, vessel.srfSpeed, stall); ChatLog.Write("approach", vessel.srfSpeed > stall + Derotation.BounceFastOver ? "rollout: bounced fast (" + Math.Round(vessel.srfSpeed) + " m/s > stall + 10) - no pitch-up, nose " + bouncePitch.ToString("0.0") + " lowering 1 deg/s, idle" : "rollout: bounced - holding " + bouncePitch.ToString("0.0") + " deg nose, idle"); }
                        else bouncePitch = Derotation.BounceStep(bouncePitch, vessel.srfSpeed, stall, dt);   // LAND5q
                                c.pitch = (float)FlightPolicy.Clamp(.05 * (bouncePitch - pitch) - .03 * q, -.3, .3);
                            }
                            else { if (!double.IsNaN(bouncePitch)) ChatLog.Write("approach", "rollout: wheels back down - nose to " + (-derot.NoseDownPitch).ToString("0.0") + " deg at " + Derotation.Rate + " deg/s"); bouncePitch = double.NaN; derot.Step(pitch, dt, NoseGearDown(), vessel.srfSpeed); c.pitch = (float)Math.Min(derot.Elevator(pitch, q), 0.02f); }   // speed-based derotation, limited to 2.5 deg/s; LAND5i: no pitch-up on the ground
                            reversers.ApWants = CraftOverrides.On("reverser", true);   // TAB-CRAFT1   // GROK LAND5i: the whole rollout (bounces included) is the AP's own reverser use
                            c.roll = (float)FlightPolicy.Clamp(-.06 * roll - .015 * p, -1, 1);   // GROK LAND5f: firm wings level on the rollout (rudder/wheel steer, no roll)
                            if (vessel.LandedOrSplashed) rolloutLandedS += dt; else rolloutLandedS = 0;
                            { bool wb = RolloutBrake.FromTouchdown(vessel.LandedOrSplashed, rolloutAirS, vessel.ActionGroups[KSPActionGroup.Brakes]);   /* ROLL2: from main-gear contact, released in a bounce; debounced, no pulsing */ if (wb != vessel.ActionGroups[KSPActionGroup.Brakes]) { SetGroup(vessel, KSPActionGroup.Brakes, wb); AirbrakesByAutopilot = false; if (wb && !rolloutBrakeLogged) { rolloutBrakeLogged = true; ChatLog.Write("approach", "rollout: wheel brakes on at touchdown (" + Math.Round(vessel.srfSpeed) + " m/s)"); } } }
                            { double hNow = FlightGlobals.ship_heading, hr = double.IsNaN(rollHdgPrev) || dt <= 0 ? 0 : FlightPolicy.Wrap(hNow - rollHdgPrev) / dt; rollHdgPrev = hNow; rollHdgRate = double.IsNaN(rollHdgRate) ? hr : rollHdgRate + .3 * (hr - rollHdgRate); rollSteer = RolloutSteer.Step(rollSteer, heading - hNow, rollHdgRate, vessel.srfSpeed, vessel.ActionGroups[KSPActionGroup.Brakes], dt); c.wheelSteer = (float)rollSteer; }   // ROLL1: damped, speed/brake-scheduled, slew-limited nosewheel steering
                            lateralBankAsk = 0;
                            LogLandingLateral(c);
                            /* ROLL2: no nose-down gate on the wheel brakes any more - they start at main-gear contact */
                            return;
                        }
                    }
                }
                if (mode == "helicopter")
                {
                    if (helicopterLanding && helicopterTouchdown.Step(Time.realtimeSinceStartup, vessel.LandedOrSplashed, vessel.verticalSpeed))
                    {
                        props.Collective(0); props.Set(0, 460, false); Stop();
                        if (Bool(settingsData, "rotor_brake_park", false)) rotorPark = new RotorPark(Time.realtimeSinceStartup);
                        return;
                    }
                    double vs = helicopterLanding ? (vessel.radarAltitude > 30 ? -3 : vessel.radarAltitude > 10 ? -2 : vessel.radarAltitude > 3 ? -1 : -.7)
                        : FlightPolicy.Clamp(.3 * (altitude - vessel.altitude), -3, 3);
                    vs = HeliLearnVerticalSpeed(vs);
                    double liftRpm, liftLimit; bool liftMotor, liftLocked;
                    bool liftKnown = props.LiftSnapshot(out liftRpm, out liftLimit, out liftMotor, out liftLocked);
                    string previousEmergency = rotorEmergency.Mode;
                    string emergency = rotorEmergency.Step(Time.realtimeSinceStartup, liftKnown, liftRpm, liftLimit, liftMotor, liftLocked, vessel.LandedOrSplashed);
                    if (emergency != previousEmergency) ChatWindow.Notice("Local helicopter: " + emergency);
                    double fwd = Vector3d.Dot(vessel.srf_velocity, vessel.ReferenceTransform.up);
                    double side = Vector3d.Dot(vessel.srf_velocity, vessel.ReferenceTransform.right);
                    double forwardTarget = speed, rightTarget = 0;
                    if (helicopterPositionHold) HelicopterPolicy.HoldVelocity(
                        NavigationMath.Distance(vessel.latitude, vessel.longitude, hoverLat, hoverLon, vessel.mainBody.Radius),
                        NavigationMath.Bearing(vessel.latitude, vessel.longitude, hoverLat, hoverLon), FlightGlobals.ship_heading, out forwardTarget, out rightTarget);
                    HeliWaypointVelocity(ref forwardTarget, ref rightTarget);
                    HeliLearnForward(ref forwardTarget, ref rightTarget);
                    if (!HeliLearning) forwardTarget = FlightPolicy.Clamp(forwardTarget, -HeliLearned("forward", 50), HeliLearned("forward", 50));
                    if (emergency != "normal") { vs = -3; forwardTarget = vessel.radarAltitude < 12 ? 0 : 30; rightTarget = 0; }
                    double heliTilt = HeliTiltLimit();
                    double pitchTarget = props.Compound ? 0 : FlightPolicy.Clamp(-1.5 * (forwardTarget - fwd), -heliTilt, heliTilt);
                    double rollTarget = FlightPolicy.Clamp(1.5 * (rightTarget - side), -heliTilt, heliTilt);
                    double yawRate = FlightPolicy.Wrap(FlightGlobals.ship_heading - lastHeliHeading) / dt; lastHeliHeading = FlightGlobals.ship_heading;
                    double yawDemand = vessel.LandedOrSplashed ? 0 : helicopterYaw.Step(FlightPolicy.Wrap(heading - FlightGlobals.ship_heading), yawRate, dt);
                    if (helicopterYaw.Observe(Time.realtimeSinceStartup, yawDemand, yawRate)) ChatWindow.Notice("Helicopter yaw actuator sign corrected from measured response.");
                    if (!helicopterVerticalOnly)
                    {
                        c.pitch = (float)FlightPolicy.Clamp(.022 * (pitchTarget - pitch) - .012 * q, -1, 1);
                        c.roll = (float)FlightPolicy.Clamp(.014 * (rollTarget - roll) - .01 * p, -1, 1);
                        c.yaw = (float)yawDemand;
                        if (takeoverT < PilotPolicy.TakeoverSeconds)
                        {   // T5: smooth takeover - heli too: cyclic/yaw blend from the pilot's stick at engage to the hover-hold asks over 1 s
                            c.pitch = (float)PilotPolicy.TakeoverBlend(takePitch, c.pitch, takeoverT);
                            c.roll = (float)PilotPolicy.TakeoverBlend(takeRoll, c.roll, takeoverT);
                            c.yaw = (float)PilotPolicy.TakeoverBlend(takeYaw, c.yaw, takeoverT);
                            takeoverT += dt;
                        }
                    }
                    double collectiveOutput = collective.Step(vs, vessel.verticalSpeed, dt, vessel.LandedOrSplashed);
                    if (emergency == "autorotation" && liftKnown) collectiveOutput = HelicopterPolicy.Autorotation(vessel.radarAltitude, vessel.verticalSpeed, liftRpm, liftLimit);
                    props.Collective((float)collectiveOutput, "lift");
                    HeliLearnSample(dt, collectiveOutput, emergency);
                    sideIntegral = FlightPolicy.Clamp(sideIntegral + .1 * (forwardTarget - fwd) * dt, -12, 20);
                    if (!helicopterVerticalOnly) props.Yaw((float)(helicopterYaw.Sign * yawDemand + rotorYawBias / 10), (float)FlightPolicy.Clamp(.8 * (forwardTarget - fwd) + sideIntegral, -12, 20));
                    if (emergency == "normal" && auto["master"] && auto["rotor"] && Time.realtimeSinceStartup >= nextRotorTrim && Math.Abs(vessel.verticalSpeed) < .8 && Math.Abs(roll) < 8 && vessel.srfSpeed < 25)
                    {
                        nextRotorTrim = Time.realtimeSinceStartup + 8;
                        if (props.CounterLift && Math.Abs(yawRate) > 3) rotorYawBias = FlightPolicy.Clamp(rotorYawBias - .5 * Math.Sign(yawRate), -8, 8);
                        if (vessel.verticalSpeed < -.25) collective.Bias(.4); else if (vessel.verticalSpeed > .35) collective.Bias(-.3);
                    }
                    return; // rotorcraft lift must never command main throttle
                }
                double targetVs = directVs ?? FlightPolicy.VerticalSpeed(vessel.altitude, mode == "hold" ? PilotPolicy.EffectiveAltitude(altitude, terrainFloor) : altitude, mode == "hold" && !double.IsNaN(climbVsCap) ? climbVsCap : 25, 15, kin, band, ref capture);   // round 3: floor as target, not a fighting VS override (porpoise)
                if (!directVs.HasValue) { targetVs *= CraftClass.VsScale(CraftCls); targetVs = CraftClass.SlewVs(ref vsSlew, targetVs, dt, CraftCls); } else vsSlew = targetVs;   // heavy: gentle altitude corrections, VS command slewed (porpoise, Oct 12)
                if (runway != null) { double lr = profile != null ? profile.Perf.RollRate : double.NaN; runway.RollS = !double.IsNaN(lr) && lr > 5 ? 25 / FlightPolicy.Clamp(lr, 6, 60) : double.NaN; } if (runway != null) { runway.FlapStepsLeft = flaps.Count > 0 ? Math.Max(0, 3 - flapSet) : 0; runway.HasAirbrakes = brakeSurfs.Count > 0; }
                ApproachProfile.Heavy = CraftClass.Gentle(CraftCls); ApproachProfile.CruiseSpeed = profile == null ? double.NaN : !double.IsNaN(profile.Perf.CruiseSpeed) && profile.Perf.CruiseSpeed > 0 ? profile.Perf.CruiseSpeed : profile.EconSpeed; ApproachProfile.HasFlaps = flaps.Count > 0; ApproachProfile.FlapRatio = profile != null && !double.IsNaN(profile.FlapStall) && profile.Stall > 0 ? profile.FlapStall / profile.Stall : double.NaN;
                if (mode == "hold" && !double.IsNaN(terrainFloor) && vessel.altitude < terrainFloor)
                    targetVs = Math.Max(targetVs, FlightPolicy.Clamp((terrainFloor - vessel.altitude) * .08, 3, 25));
                bool landBank = mode == "landing" && runway != null && runway.TurnBank > 0;   // G-rated approach turns (and explicit "bank 60")   // "bank 60, land nearest"
                double blim; bool envBank = false;
                if (landBank && ApproachProfile.SafeBank != null) blim = runway.TurnBank;   // chart/join bank already envelope-sized
                else if (ApproachProfile.SafeBank != null)
                {   // hold / ferry / fly_to: 85% of learned max safe bank (AUTOPILOT_REVIEW #2); class HeavyBank only when no envelope (#10)
                    double sb = ApproachProfile.SafeBank(vessel.srfSpeed);
                    envBank = !double.IsNaN(sb) && sb > 0;
                    blim = envBank ? FlightPolicy.Clamp(ApproachProfile.EnvelopeUse * sb, 10, 70) : CraftClass.MaxBank(CraftCls, FlightPolicy.BankLimit(vessel.srfSpeed));
                }
                else blim = CraftClass.MaxBank(CraftCls, landBank ? runway.TurnBank : FlightPolicy.BankLimit(vessel.srfSpeed));
                double arcFf = mode == "landing" && runway != null && runway.Phase == "entry" ? runway.ArcBank : 0;   // curvature feed-forward; heading error is the trim
                double? latBank = mode == "landing" && runway != null && (runway.Phase == "entry" || runway.Phase == "intercept") && !double.IsNaN(runway.LatBank) ? runway.LatBank : (double?)null;   // GROK LAND3: centreline capture commands the bank itself
                double hdgErr = FlightPolicy.Wrap(heading - FlightGlobals.ship_heading);   // T6: one signed heading error feeds both the bank and the rudder asks
                double hdgRate = dt > 0 && !double.IsNaN(lastRudderHdg) ? FlightPolicy.Wrap(FlightGlobals.ship_heading - lastRudderHdg) / dt : 0; lastRudderHdg = FlightGlobals.ship_heading;
                bool wantRudder = !directBank.HasValue && !latBank.HasValue && !vessel.LandedOrSplashed && mode != "takeoff" && !(Time.realtimeSinceStartup < rudderOffUntil) && PilotPolicy.RudderSteer(hdgErr, rudderOn, vessel.srfSpeed, mode == "landing");   // T6: an explicit bank ask (roll hold / plan circle) and the ground roll keep their own yaw
                if (wantRudder && rudderOn && PilotPolicy.RudderStuck(Time.realtimeSinceStartup - rudderSince, hdgErr, rudderHdg0)) { wantRudder = false; rudderOffUntil = Time.realtimeSinceStartup + PilotPolicy.RudderCoolOffS; }   // no progress on the rudder (no authority / a moving target): the bank takes the error back
                if (wantRudder != rudderOn)
                {   // T6 decision log: which axis is flying the heading, and why it handed back
                    rudderOn = wantRudder;
                    if (rudderOn) { rudderSince = Time.realtimeSinceStartup; rudderHdg0 = hdgErr; ChatLog.Write("att", "heading hold: rudder takes the last " + PilotPolicy.RudderErrDeg.ToString("0.#") + " deg (err " + hdgErr.ToString("0.0") + " deg at " + vessel.srfSpeed.ToString("0") + " m/s) - wings level, no bank ask"); }
                    else ChatLog.Write("att", "heading hold: bank flies the heading again (err " + hdgErr.ToString("0.0") + " deg) - rudder released" + (Time.realtimeSinceStartup < rudderOffUntil ? "; no progress on the rudder, off for " + (rudderOffUntil - Time.realtimeSinceStartup).ToString("0") + " s" : ""));
                }
                if (mode == "landing" && runway != null && !double.IsNaN(runway.BankCap)) blim = Math.Min(blim, runway.BankCap);
                if (mode == "landing" && !double.IsNaN(CraftOverrides.L("max_approach_bank"))) blim = Math.Min(blim, CraftOverrides.L("max_approach_bank"));   // TAB-CRAFT1
            blim = PilotPolicy.LandBankCap(blim, mode == "landing", vessel.radarAltitude, vessel.LandedOrSplashed);
            if (mode == "landing" && runway != null && runway.Phase == "flare") blim = Math.Min(blim, FinalApproachPolicy.FlareBankCap);   // LAT3: flare keeps a small heading correction, wings near level   // GROK LAND5f: wings level at touchdown (16:41 wingtip strike)
            if (mode == "hold" && pendingLand != null) blim = PilotPolicy.FerryBank(blim, TimeWarp.CurrentRate);   // GROK LAND5f: ferry 54 deg at 235 m/s under warp   // GROK LAND5b: <= 20 deg below 500 m after a go-around
            double hdgRateL = double.IsNaN(prevHdgL) || dt <= 0 ? 0 : FlightPolicy.Wrap(FlightGlobals.ship_heading - prevHdgL) / dt; prevHdgL = FlightGlobals.ship_heading;
            double locDamp = mode == "landing" && runway != null && runway.Phase == "final" ? FlightPolicy.Clamp(PilotPolicy.FinalLatDamp * hdgRateL, -5, 5) : 0;   // GROK LAND5d: track-rate damping sized for the stronger lineup gain (no S-turns)   // GROK LAND5b: track-rate damping on the final capture (16 km wobble)
            bool finLat = mode == "landing" && runway != null && runway.Phase == "final" && !latBank.HasValue;   // GROK LAND5d (16:01 go-around, 65 m off at 1 km): bank ask was 0.45/deg - it 'straightened out' 200 m off
            double bank = directBank ?? (latBank.HasValue ? FlightPolicy.Clamp(latBank.Value, -blim, blim) : finLat ? FlightPolicy.Clamp(arcFf + PilotPolicy.FinalLatBank(hdgErr, runway.Distance + runway.TouchdownM) - locDamp, -Math.Min(blim, PilotPolicy.FinalLatCap(runway.Distance + runway.TouchdownM)), Math.Min(blim, PilotPolicy.FinalLatCap(runway.Distance + runway.TouchdownM))) : FlightPolicy.Clamp(arcFf + PilotPolicy.HeadingBank(hdgErr, rudderOn) - locDamp, -blim, blim));
                bank = (landBank || envBank) && !directBank.HasValue ? FlightPolicy.Clamp(bank, -blim, blim) : PilotPolicy.ClampBank(bank, vessel.srfSpeed, bankOverride);
                double decel = prevSpd < 0 || dt <= 0 ? 0 : (prevSpd - vessel.srfSpeed) / dt; prevSpd = vessel.srfSpeed;
                double learnedRoll = learn != null && learn.P != null && !double.IsNaN(learn.P.Perf.RollRate) ? learn.P.Perf.RollRate : profile != null && !double.IsNaN(profile.Perf.RollRate) ? profile.Perf.RollRate : double.NaN;   // step 6 result drives step 7 roll-in in the same flight
                double learnedPitch = profile != null && !double.IsNaN(profile.Perf.PitchRate) ? profile.Perf.PitchRate : double.NaN;
                DecisionLog.Use("roll rate", double.IsNaN(learnedRoll) || learnedRoll <= 5 ? "default " + TurnOnset.RollRate + " deg/s" : "learned roll rate", learnedRoll);
            double rollGain = !double.IsNaN(learnedRoll) && learnedRoll > 5 ? FlightPolicy.Clamp(TurnOnset.RollRate / learnedRoll, .5, 1.5) : 1;   // fast-rolling airframe -> less gain   // AUTOPILOT_REVIEW #7
                double pitchGain = !double.IsNaN(learnedPitch) && learnedPitch > 5 ? FlightPolicy.Clamp(20 / learnedPitch, .5, 1.5) : 1;
                bank = FinalApproachPolicy.BankCommand(bank, mode == "landing");
                if (!vessel.LandedOrSplashed && !directBank.HasValue)
                {   // turn onset: roll-rate + g-onset ramp (no choppy g at turn entry); LEARN roll rate when known
                    double want = bank; double onsetRoll = !double.IsNaN(learnedRoll) && learnedRoll > 5 ? FlightPolicy.Clamp(learnedRoll, 10, 60) : TurnOnset.RollRate;
                    bank = TurnOnset.HoldBank(ref bankCmd, CraftCls, want, dt, onsetRoll, bankOverride && learn != null && learn.TurnPhase, learnedRoll);   // Learn stress turns: roll in at the learned roll rate, no gentle g-onset. HoldBank stores bankCmd (Oct 12: the inline 'bankCmd = bank;' had ended up inside this comment, so the heading hold never rolled past ~1 deg)
                    if (Math.Abs(want) > 30 && Math.Abs(bankPrevWant) < 10) gTraceUntil = Time.realtimeSinceStartup + 6;
                    bankPrevWant = want;
                    if (Time.realtimeSinceStartup < gTraceUntil && Time.realtimeSinceStartup >= gTraceNext) { gTraceNext = Time.realtimeSinceStartup + .5f; ChatLog.Write("g", "turn entry: want=" + want.ToString("0") + " cmd=" + bankCmd.ToString("0") + " bank=" + Roll().ToString("0") + " n=" + vessel.geeForce.ToString("0.00")); }
                }
                else bankCmd = vessel.LandedOrSplashed ? 0 : bank;
                bank = FinalApproachPolicy.BankCommand(bank, mode == "landing");
                bankCmd = FinalApproachPolicy.BankCommand(bankCmd, mode == "landing");
                // Approach banks are sized from the learned envelope / TurnBank; the slow/decel SafeBank clamp would crush them to 5-10 deg (AUTOPILOT_REVIEW #1).
                if (!bankOverride && !vessel.LandedOrSplashed && mode != "takeoff" && !(mode == "landing" && (landBank || ApproachProfile.SafeBank != null)))
                    bank = PilotPolicy.SafeBank(bank, Eas(), PilotPolicy.SaneStall(stall, StallEstimate()), decel);   // EAS vs EAS stall (KSP IAS at altitude clamped ferry turns to 5 deg, live 20:27)
                bool heavyFloor = !vessel.LandedOrSplashed && (mode == "hold" || (mode == "landing" && runway != null && runway.Phase == "entry")) && CraftClass.FloorRecover(CraftCls, vessel.radarAltitude, vessel.verticalSpeed);
                if (heavyFloor) { bank = 0; bankCmd = CraftClass.RollStep(CraftCls, bankCmd, 0, dt, learnedRoll); targetVs = Math.Max(targetVs, 8); if (Time.realtimeSinceStartup >= gTraceNext) { gTraceNext = Time.realtimeSinceStartup + 2; ChatLog.Write("g", "heavy floor recovery: agl=" + vessel.radarAltitude.ToString("0") + " vs=" + vessel.verticalSpeed.ToString("0")); } }   // heavy: wings level + climb early   // no tight turns while slow / bleeding speed
                bool wasLowEnergy = lowEnergy; bool learnTurnLe = learn != null && !learn.Done && learn.TurnPhase, learnSinkLe = learn != null && !learn.Done && learn.SinkPhase;
                bool approachLeOff = mode == "landing" && runway != null && (runway.Phase == "flare" || runway.Phase == "rollout" || runway.Distance < ApproachChart.LongFix || ApproachSpeedLocked);   // AUTOPILOT_REVIEW #9
                double leK = mode == "landing" && runway != null && (runway.Phase == "final" || runway.Phase == "intercept") ? ApproachProfile.FloorK(runway.Distance, runway.Chart != null ? runway.Chart.FafD : RunwayMission.FafDistance) : double.NaN;   // GROK LAND5c: final uses the shrinking floor
                lowEnergy = !vessel.LandedOrSplashed && mode != "takeoff" && !approachLeOff && (double.IsNaN(leK) ? PilotPolicy.LowEnergy(lowEnergy, Eas(), stall, vessel.verticalSpeed, vessel.radarAltitude) : PilotPolicy.LowEnergyK(lowEnergy, Eas(), stall, vessel.verticalSpeed, vessel.radarAltitude, leK))
                    && !learnSinkLe;   // sink map flies 1.25x stall by pitch and owns the throttle; its own 1.15x stop is the guard
                if (lowEnergy && !wasLowEnergy) ChatLog.Write("throttle", "low-energy override ACTIVATED (" + mode + "): " + PilotPolicy.LowEnergyReason(vessel.srfSpeed, stall, vessel.verticalSpeed, vessel.radarAltitude) + " -> power 90%+ at the emergency rate, wings within 15 deg, climb");
                else if (!lowEnergy && wasLowEnergy) ChatLog.Write("throttle", "low-energy override cleared: spd=" + vessel.srfSpeed.ToString("0") + " (1.3 Vs " + PilotPolicy.MinSafeSpeed(stall).ToString("0") + ") vs=" + vessel.verticalSpeed.ToString("0") + " agl=" + vessel.radarAltitude.ToString("0"));
                double kinH = kin * CraftClass.GainScale(CraftCls);   // T4: mass / craft-class gain on the vs-error pitch angle (above the attitude trace so the log shows what the hold asked)
                if (!vessel.LandedOrSplashed && Time.realtimeSinceStartup >= attTraceNext && !(mode == "landing" && runway != null && runway.Phase != "flare" && runway.Phase != "rollout"))
                {   // attitude vs path (GROK LAND4: not logged while the landing attitude law flies - it is not the law in use): nose pitch is where the nose points, flight-path angle is where the plane goes
                    attTraceNext = Time.realtimeSinceStartup + 5;
                    ChatLog.Write("att", mode + " nose pitch " + pitch.ToString("0.0") + " fpa " + PilotPolicy.FlightPathAngle(vessel.verticalSpeed, vessel.horizontalSrfSpeed).ToString("0.0") + " roll " + Roll().ToString("0.0") + " hdg " + FlightGlobals.ship_heading.ToString("0.0") + " spd " + vessel.srfSpeed.ToString("0.0") + " vs " + vessel.verticalSpeed.ToString("0.0") + " vs-pitch ask " + PilotPolicy.VsPitchDeg(targetVs - vessel.verticalSpeed, vessel.srfSpeed, kinH).ToString("0.0") + " deg for vs err " + (targetVs - vessel.verticalSpeed).ToString("0.0") + " m/s (atan(err/tas), was 0.8 deg per m/s)");
                }
                if (!vessel.LandedOrSplashed && mode != "takeoff" && !(mode == "landing" && runway != null && (runway.Phase == "flare" || runway.Phase == "rollout")) && !learnSinkLe) speed = Math.Max(speed, PilotPolicy.MinSafeSpeed(PilotPolicy.TasStall(stall, vessel.atmDensity)));   // speed hold is surface speed: floor 1.3 Vs as TAS at this density
                if (lowEnergy)
                {   // hard minimum power + climb; airbrakes in
                    if (!learnTurnLe) bank = FlightPolicy.Clamp(bank, -15, 15);   // Learn turn ladder commands 40-70 deg on purpose
                    if (vessel.radarAltitude < 300) targetVs = Math.Max(targetVs, 4);
                    if (vessel.ActionGroups[KSPActionGroup.Brakes] && AirbrakesByAutopilot) { SetGroup(vessel, KSPActionGroup.Brakes, false); AirbrakesByAutopilot = false; }
                    if (Time.realtimeSinceStartup >= lowTraceNext) { lowTraceNext = Time.realtimeSinceStartup + 2; ChatLog.Write("g", "low energy: spd=" + vessel.srfSpeed.ToString("0") + " min=" + PilotPolicy.MinSafeSpeed(stall).ToString("0") + " stall=" + stall.ToString("0") + " vs=" + vessel.verticalSpeed.ToString("0") + " agl=" + vessel.radarAltitude.ToString("0") + " -> power 90%+, climb"); }
                }
                vsIntegral = FlightPolicy.Clamp(vsIntegral + (targetVs - vessel.verticalSpeed) * dt * .25 * kin, -5, 5);
                double desiredPitch = directPitch ?? FlightPolicy.Clamp(1 + PilotPolicy.VsPitchDeg(targetVs - vessel.verticalSpeed, vessel.srfSpeed, kinH) + vsIntegral + CraftClass.PitchLead(CraftCls, roll), targetVs < -1 ? descentPitchMin : -2, CpMax);
                {   // GROK LAND4: ONE pitch law for every landing phase but flare/rollout (LAND3 blended two laws at 5-10 deg bank -> they fought: +27/-23 m/s):
                    // nose attitude = learned level trim + desired flight-path angle + 0.3x filtered vs correction, nose command slewed <= 2 deg/s;
                    // the bank's extra lift is the elevator feed-forward. Outside landing: the same law blended in by bank (turns only).
                    bool landLaw = mode == "landing" && runway != null && runway.Phase != "flare" && runway.Phase != "rollout" && !runway.MissedClimb;   // GROK LAND5b: the go-around flies its own climb law
                    double vsPrev = vsFilt; vsFilt = PitchAttitude.VsStep(vsFilt, vessel.verticalSpeed, dt); vsSlow = PitchAttitude.OuterStep(vsSlow, vessel.verticalSpeed, dt);
                    bool steady = PitchAttitude.Steady(vsPrev, vsFilt, dt);   // trim learned only in quasi-steady flight (vs not changing): no feedback from the pull-ups
                    aoaTrim = PitchAttitude.TrimStep(aoaTrim, pitch, PilotPolicy.FlightPathAngle(vessel.verticalSpeed, vessel.horizontalSrfSpeed), roll, steady ? dt : 0);
                    double aw = landLaw ? 1 : PitchAttitude.Weight(Math.Max(Math.Abs(bankCmd), Math.Abs(roll)));
                    bool attOk = !directPitch.HasValue && !vessel.LandedOrSplashed && mode != "takeoff" && !double.IsNaN(aoaTrim) && !learnTurnLe && !learnSinkLe && !(learn != null && !learn.Done) && !(mode == "landing" && runway != null && (runway.Phase == "flare" || runway.Phase == "rollout"));
                    if (attOk && aw > 0)
                    {
                        double att = landLaw   // GROK LAND4g: landing = pilot law (nose held, slow outer vs loop, 0.5 deg/s in banks)
                            ? FlightPolicy.Clamp(PitchAttitude.FinalTarget(aoaTrim, targetVs, vsSlow, vsFilt, vessel.srfSpeed, Math.Max(Math.Abs(bankCmd), Math.Abs(roll))) - PitchShaper.BackPressure(bankCmd), targetVs < -1 ? descentPitchMin : -2, CpMax)
                            : FlightPolicy.Clamp(PitchAttitude.Target(aoaTrim, targetVs, vsFilt, vessel.srfSpeed, PitchAttitude.LandGain) + .3 * vsIntegral - PitchShaper.BackPressure(bankCmd), targetVs < -1 ? descentPitchMin : -2, CpMax);
                        attCmd = landLaw ? PitchAttitude.BankSlew(double.IsNaN(attCmd) ? pitch : attCmd, att, dt, Math.Max(Math.Abs(bankCmd), Math.Abs(roll)), pitch - att) : PitchAttitude.Slew(double.IsNaN(attCmd) ? pitch : attCmd, att, dt);
                        if (landLaw && runway.Phase == "final" && (runway.Distance < 5000 || vessel.radarAltitude < 500)) attCmd = PitchAttitude.SinkGuard(attCmd, pitch, vessel.verticalSpeed, targetVs, vessel.srfSpeed);   // GROK LAND5b: sink guard, no slew
                        desiredPitch = (1 - aw) * desiredPitch + aw * attCmd;
                        if (Time.realtimeSinceStartup >= turnPitchTraceNext) { turnPitchTraceNext = Time.realtimeSinceStartup + 5; ChatLog.Write("att", "nose tgt " + attCmd.ToString("0.0") + " act " + pitch.ToString("0.0") + " ff " + loadFf.ToString("0.00") + " deg | pitch law (attitude" + (landLaw ? ", landing" : ", turn x" + aw.ToString("0.0")) + "): bank " + roll.ToString("0") + " trim " + aoaTrim.ToString("0.0") + " path " + (Math.Atan2(targetVs, Math.Max(30, vessel.srfSpeed)) * 180 / Math.PI).ToString("0.0") + " -> nose " + attCmd.ToString("0.0") + " deg (vs " + vsFilt.ToString("0.0") + " filtered, want " + targetVs.ToString("0.0") + ")"); }
                    }
                    else attCmd = double.NaN;
                    landLawOn = attOk && landLaw;
                }   // T4: the vs error becomes a pitch ANGLE atan(err / true airspeed), so the hold behaves the same at 40 m/s and at 250 m/s
                if (mode == "landing" && runway != null && runway.Phase == "flare" && !vessel.LandedOrSplashed && profileTrusted && profile != null)
                {
                    double flarePitch = FlarePolicy.Pitch(profile.TouchdownAoa, vessel.srfSpeed, vessel.verticalSpeed, runway.DesiredVs);   // LAND5n: track the target sink
                    if (!double.IsNaN(flarePitch)) desiredPitch = FlightPolicy.Clamp(flarePitch, descentPitchMin, CpMax);
                }
                if (mode == "landing" && runway != null && runway.Phase == "flare" && runway.FlareFast && !double.IsNaN(flareEntryPitch) && !vessel.LandedOrSplashed) desiredPitch = Math.Min(desiredPitch, flareEntryPitch + FastPitchUp);
                if (mode == "landing" && runway != null && runway.Phase == "flare" && !vessel.LandedOrSplashed) desiredPitch = FlarePolicy.CapPitch(desiredPitch, CraftOverrides.L(CraftOverrides.FlareMaxPitch));   // LAND5n: max flare pitch
                if ((landFloor || lowEnergy) && !directPitch.HasValue && vessel.radarAltitude < 300) { desiredPitch = Math.Max(desiredPitch, vessel.srfSpeed > 1.3 * stall ? 8 : 3); vsIntegral = Math.Max(0, vsIntegral); pitchIntegral = Math.Max(0, pitchIntegral); }   // sink arrest: nose up unless near the stall (then power does it)
                bool missedClimb = mode == "landing" && runway != null && runway.MissedClimb && !vessel.LandedOrSplashed;
                if (missedClimb && !directPitch.HasValue) desiredPitch = FlightPolicy.Clamp(PitchAttitude.GoAroundPitch(vessel.srfSpeed, stall, runway.DesiredSpeed), 0, CpMax);   // GROK LAND5b: tracked go-around nose (was 15-17 vs tgt 0-3)
                if (targetVs > 3 && !directPitch.HasValue && !missedClimb)
                {   // climb: hold at least a climb attitude; bias toward LEARN Vy when known (AUTOPILOT_REVIEW #12)
                    desiredPitch = FlightPolicy.Clamp(desiredPitch, Math.Min(5, CpMax), CpMax);
                    if (profile != null || !double.IsNaN(CraftOverrides.L("climb_speed"))) { double vy = !double.IsNaN(CraftOverrides.L("climb_speed")) ? CraftOverrides.L("climb_speed") : profile.BestClimbSpeed(stall, vessel.totalMass); if (!double.IsNaN(vy) && vy > 0) desiredPitch = FlightPolicy.Clamp(desiredPitch + .35 * (vessel.srfSpeed - vy), Math.Min(5, CpMax), CpMax); }
                }
                if (mode != shapedMode || vessel.LandedOrSplashed) { shapedMode = mode; pitchShape.Reset(pitch); pitchIntegral = 0; vsIntegral = 0; }   // every mode change starts from the current state (no stale filter jump)
            ApproachProfile.Backoff = ApproachProfile.StressStep(ApproachProfile.Backoff, vessel.geeForce, ApproachProfile.StructG, dt);
            double gLim = mode == "landing" && runway != null && runway.Phase == "entry" ? Math.Max(3, ApproachProfile.LoadFactor(ApproachProfile.JoinG, vessel.srfSpeed, stall) + .5) : mode == "landing" && runway != null && (runway.Phase == "intercept" || runway.Phase == "final" || runway.Phase == "flare") ? Math.Max(1.2, ApproachProfile.FinalG) : 3;   // gentler once established inbound   // joins maneuver to the dynamic limit; final/flare stay gentle
            if (!(mode == "landing" && runway != null && runway.Phase == "entry") && mode != "takeoff") gLim = Math.Min(gLim, 3);
            gLim = PitchShaper.Limit(gLim, bankCmd); bool learnPull = learn != null && (learn.Phase == "turn" || learn.Phase == "turnprep" || learn.Phase == "recover");
            if (CraftClass.Gentle(CraftCls) && mode != "takeoff" && !learnPull) gLim = Math.Min(gLim, CraftClass.HeavyPitchG); if (learnPull) gLim = Math.Max(gLim, Math.Min(.8 * Math.Max(2.5, ApproachProfile.StructG), 1 / Math.Cos(Math.Min(70, Math.Abs(bankCmd)) * Math.PI / 180) + .4));   // learn turn: pull enough to hold altitude in the bank if (landFloor || lowEnergy || heavyFloor) gLim = Math.Max(gLim, 2); if (landBank && ApproachProfile.SafeBank != null) gLim = Math.Max(gLim, 1 / Math.Cos(Math.Min(70, Math.Abs(bankCmd)) * Math.PI / 180) + .25);   // enough pull to hold altitude in the learned bank   // sink arrest needs real pitch authority   // airliner: gentle pitch   // high g only in commanded turns/joins; climbs/level 1.8 g pitch rate, 3 g cap
            if (vessel.LandedOrSplashed) lastDesiredPitch = desiredPitch; else { desiredPitch = pitchShape.Step(desiredPitch, pitch, vessel.srfSpeed, gLim, bankCmd, dt); lastDesiredPitch = desiredPitch; }
                if (desiredPitch < pitch - 5 && pitchIntegral > 0) pitchIntegral = 0;   // anti-windup: dump nose-up integral when pushing over
                if (targetVs < vessel.verticalSpeed - 5 && vsIntegral > 0) vsIntegral = 0;
                {   // GROK LAND5b: learned elevator trim per flap/gear configuration; pre-load the delta when the configuration changes
                    int ck = ConfigTrim.Key(flapSet, landingGearDown);
                    if (ck != cfgKey) { double dl = cfgKey < 0 ? 0 : cfgTrim.Delta(cfgKey, ck); if (dl != 0) { pitchIntegral += dl; ChatLog.Write("att", "config " + cfgKey + " -> " + ck + ": elevator trim pre-load " + dl.ToString("+0.000;-0.000")); } cfgKey = ck; }
                    cfgTrim.Learn(ck, elevator, dt, landLawOn && Math.Abs(roll) < 5 && Math.Abs(desiredPitch - pitch) < 1 && Math.Abs(q) < 1);
                }
                pitchIntegral = FlightPolicy.Clamp(pitchIntegral + (desiredPitch - pitch) * (landLawOn ? .08 : .04) * dt * kin, -.5, .5);   // GROK LAND4g: tighter inner attitude loop on the landing law
                double kp = .022 * pitchGain * kin * (landLawOn ? PitchAttitude.InnerGain(desiredPitch - pitch, roll) : 1); loadFf = landLawOn ? PitchAttitude.LoadFF(aoaTrim, roll) : 0;
                double pitchOut = FlightPolicy.Clamp(AuthorityScale(true) * (kp * (desiredPitch - pitch + loadFf) - .016 * q + pitchIntegral) + (landLawOn ? 0 : PitchShaper.ElevatorFF(bankCmd)), -1, 1);   // GROK LAND4g: load-factor FF (trim AoA x (n-1)) replaces the bank-step FF on the landing law   // g feed-forward + more pitch-rate damping; LEARN pitch-rate scale (#7)
                pitchOut *= PilotPolicy.GScale(vessel.geeForce, Math.Max(gLim, 3));
                float eCmd = (float)PilotPolicy.PitchCommand(pitchOut, roll);
                elevator = Mathf.MoveTowards(elevator, eCmd, (float)(PitchShaper.ElevatorRate(vessel.geeForce, Math.Max(gLim, 3), eCmd, elevator) * dt));
                bool pitchOwned = PilotPolicy.TakePitch(holdAltitude, directVs.HasValue || directPitch.HasValue, lowEnergy && !vessel.LandedOrSplashed, pitch, CraftClass.Gentle(CraftCls)); if (pitchOwned) c.pitch = elevator;   // T5: the owned flags mark the axes the hold drives this tick - only those get the takeover blend
                if (mode == "takeoff" && vessel.LandedOrSplashed && takeoff != null && !double.IsNaN(TakeoffGround.Elevator(vessel.srfSpeed, takeoff.RotStart > 0 ? takeoff.RotStart / .85 : TakeoffMission.RotateSpeed(stall, liftoffSpeed)))) { c.pitch = elevator = (float)TakeoffGround.Elevator(vessel.srfSpeed, takeoff.RotStart > 0 ? takeoff.RotStart / .85 : TakeoffMission.RotateSpeed(stall, liftoffSpeed)); pitchIntegral = vsIntegral = 0; pitchOwned = true; }   // below rotate: elevator slightly nose-down, no integrator wind-up
                if (mode == "hold" && holdAltitude) c.pitchTrim = vessel.ctrlState.pitchTrim = (float)PilotPolicy.TrimBleed(vessel.ctrlState.pitchTrim, elevator, dt);   // level-off beats trim
                lateralBankAsk = bank;
                bool rollOwned = holdHeading || directBank.HasValue; if (rollOwned) c.roll = (float)FlightPolicy.Clamp(AuthorityScale(false) * (.014 * rollGain * (bank - roll) - .01 * p), -1, 1);
                if (rudderOn) c.yaw = (float)PilotPolicy.RudderYaw(hdgErr, hdgRate);   // T6: the rudder flies the last few degrees of heading error; above them c.yaw stays the pilot's live input
                if (takeoverT < PilotPolicy.TakeoverSeconds)
                {   // T5: smooth takeover - for 1 s after engage the axes the hold owns blend from the pilot's stick at engage to the hold's ask (no snapped surfaces at the handover); axes the hold does not own pass the pilot's live input
                    if (pitchOwned) c.pitch = elevator = (float)PilotPolicy.TakeoverBlend(takePitch, c.pitch, takeoverT);
                    if (rollOwned) c.roll = (float)PilotPolicy.TakeoverBlend(takeRoll, c.roll, takeoverT);
                    if (rudderOn) c.yaw = (float)PilotPolicy.TakeoverBlend(takeYaw, c.yaw, takeoverT);   // T6: the rudder is an owned axis while it has the heading
                    takeoverT += dt;
                }
                double targetThrottle = throttle;
                if (mode == "hold" && holdAltitude && vessel.altitude > altitude + 30 && vessel.verticalSpeed > 3) targetThrottle -= .05;   // above the target and still climbing: power off
                else if (targetVs > 3) targetThrottle += .05 * Math.Sign(targetVs - vessel.verticalSpeed);
                else if (Math.Abs(speed - vessel.srfSpeed) > 3) targetThrottle += .05 * Math.Sign(speed - vessel.srfSpeed);
                if (vessel.altitude < 6000 && vessel.indicatedAirSpeed > PilotPolicy.CapSpeed) targetThrottle = throttle - .05;
                if (mode == "takeoff" && vessel.indicatedAirSpeed < 200) targetThrottle = 1;
                if (mode == "takeoff" || vessel.LandedOrSplashed || mode != shapedSpeedMode) { shold.Reset(); shapedSpeedMode = mode; }
                if (mode == "takeoff" || vessel.LandedOrSplashed) throttle = FlightPolicy.Throttle(throttle, FlightPolicy.Clamp(targetThrottle, .05, 1), !vessel.LandedOrSplashed, Planetarium.GetUniversalTime(), ref lastThrottle);
                else if (mode == "landing" && runway != null && (runway.Phase == "entry" || runway.Phase == "intercept" || runway.Phase == "final") && !runway.MissedClimb)
                {   // GROK LAND5b (Luke): step-and-wait approach throttle (hold -5/+15 m/s, 4% steps 3 s apart, lead step before flap/gear)
                    if (double.IsNaN(stepThr.Thr) || stepMode != runway.Phase && runway.Phase == "entry") { stepThr.Reset(throttle); appThr.Reset(throttle); stepMode = runway.Phase; }
                    double faf = runway.Chart != null ? runway.Chart.FafD : RunwayMission.FafDistance, nd = runway.Phase == "final" ? FlapsPolicy.NextConfigDistance(runway.Distance, faf, flapSet, landingGearDown) : double.NaN;
                    stepThr.HoldPlan = runway.OverridePowerOk && runway.Phase == "final"; stepThr.High = runway.EnergyHigh && runway.Phase == "final";   /* SPD2 */ stepThr.EngineSettled = EnginesSettled(); stepThr.Frozen = StepThrottle.Freeze(runway.Phase, vessel.radarAltitude, runway.Distance);
                    stepThr.Update(Time.realtimeSinceStartup, vessel.srfSpeed, runway.DesiredSpeed, dt, double.IsNaN(nd) ? double.NaN : (runway.Distance - nd) / Math.Max(30, vessel.srfSpeed) - 4, runway.SpeedFloor, runway.Phase == "final" ? runway.Distance : double.NaN);
                    /* THR10: the step throttle is only the trend / 5 s prediction estimator now - ONE smooth energy throttle commands */
                { double belowM = FlightPolicy.Clamp(runway.DesiredAltitude - vessel.altitude, -150, 150); if (double.IsNaN(appThr.Cmd)) appThr.Reset(throttle);
                  throttle = appThr.Update(dt, vessel.srfSpeed, runway.DesiredSpeed, stepThr.Predicted, belowM);
                  if (appThr.Changed || Time.realtimeSinceStartup >= appThrLogT) { appThrLogT = Time.realtimeSinceStartup + 1; ChatLog.Write("throttle", appThr.LogLine(belowM) + " (" + Math.Round(vessel.srfSpeed) + " vs plan " + Math.Round(runway.DesiredSpeed) + ", 5 s ahead " + Math.Round(stepThr.Predicted) + ")"); } }
                }
                else { throttle = shold.Step(ApproachSpeedLocked ? runway.DesiredSpeed : speed, vessel.srfSpeed, dt, throttle); stepThr.Thr = double.NaN; appThr.Cmd = double.NaN; }
                if (mode == "landing" && runway != null && runway.MissedClimb && !vessel.LandedOrSplashed && vessel.srfSpeed < runway.DesiredSpeed - 5)
                { throttle = 1; throttleWhy = "go-around climb: full power (" + Math.Round(vessel.srfSpeed) + " vs " + Math.Round(runway.DesiredSpeed) + ")"; }   // GROK LAND5k: test run 1 climbed out at 8% / 44 m/s
                if (mode == "landing" && runway != null && runway.PatternActive && !vessel.LandedOrSplashed && vessel.srfSpeed > runway.DesiredSpeed + 10)
                { throttle = vessel.srfSpeed > runway.DesiredSpeed + 25 ? 0 : Math.Min(throttle, .3); throttleWhy = "go-around pattern overspeed (" + Math.Round(vessel.srfSpeed) + " vs " + Math.Round(runway.DesiredSpeed) + ")"; }   // GROK LAND5d
                // GROK LAND5i: the LAND5c 'below the floor: gentle power-up' ramp (+5%/s, written back into the step throttle) is gone - it turned each
                // logged '+3%' into +18% over the 3 s wait. The step throttle owns below-floor power now.
                if (mode == "landing" && runway != null && directVs.HasValue && !vessel.LandedOrSplashed && (runway.Phase == "final" || runway.Phase == "flare"))
                {   // learned sink map: throttle for the target sink rate (flare: map only; final: blended with the speed hold)
                    double st = SinkThrottle(directVs.Value);
                    /* THR9: flare throttle moved below - it no longer depends on a sink-map target */   // GROK LAND5c: idle in the flare (bleed toward stall at touchdown); LAND5b: final = step-and-wait only
                }   // smooth PI on airspeed (pitch flies the vertical speed)
                flareThrCmd = false;
            if (mode == "landing" && runway != null && !runway.MissedClimb && !vessel.LandedOrSplashed && (runway.Phase == "flare" || runway.Phase == "final" && StepThrottle.FlareLeadDue(runway.Low, runway.FlareH, vessel.verticalSpeed, CraftOverrides.FlareSpoolLeadS())))
            {   // THR9: flare throttle (override, else the final-lock power latched at the lead point) from lead s before the flare to main-gear touchdown
                double ft = CraftOverrides.FlareThrottleFrac(); bool ov = !double.IsNaN(ft);
                if (double.IsNaN(flareThrAuto)) flareThrAuto = Math.Max(0, Math.Min(1, throttle));
                throttle = ov ? ft : flareThrAuto; flareThrCmd = true; throttleWhy = "flare: throttle " + Math.Round(100 * throttle) + "% (" + (ov ? "override" : "auto") + ")";
                if (!flareThrLogged) { flareThrLogged = true; ChatLog.Write("throttle", "flare: " + Math.Round(100 * throttle) + "% (" + (ov ? "override" : "auto") + "), lead " + CraftOverrides.FlareSpoolLeadS().ToString("0.#") + " s" + (runway.Phase == "final" ? ", spooling before the flare at " + Math.Round(runway.Low) + " m (flare " + Math.Round(runway.FlareH) + " m)" : "")); }
            }
            else if (runway == null || runway.Phase != "final" && runway.Phase != "flare") { flareThrLogged = false; flareThrAuto = double.NaN; }
            if (mode == "landing" && runway != null && runway.Phase == "flare" && vessel.LandedOrSplashed) throttle = 0;   // THR9: idle at main-gear touchdown
            if (mode == "landing" && runway != null && StepThrottle.Emergency(vessel.srfSpeed, runway.DesiredSpeed) && ApproachProfile.IdleWanted(runway.EnergyHigh, vessel.srfSpeed, runway.DesiredSpeed, ApproachProfile.NearShort(runway.Phase, runway.Distance)) && !vessel.LandedOrSplashed && (runway.Phase == "entry" || runway.Phase == "intercept" || runway.Phase == "final"))
                { throttle = 0; throttleWhy = "approach high/fast: idle"; }
                if (mode == "landing" && runway != null && runway.FastContinue && runway.Phase != "flare" && !vessel.LandedOrSplashed) { throttle = 0; throttleWhy = "+10..+20 predicted at touchdown: idle, continuing"; }   // GROK LAND5c   // Oct 12 (A300 03:29): sink-map blend held 46-69% while 800 m high and 20+ m/s fast
            bool apPhase = mode == "landing" && runway != null && !runway.MissedClimb && (runway.Phase == "entry" || runway.Phase == "intercept" || runway.Phase == "final");   // THR10
            if ((landFloor && mode == "landing" && !apPhase && (runway == null || vessel.srfSpeed < runway.DesiredSpeed + 3) || lowEnergy) && !vessel.LandedOrSplashed)   // THR10: on approach the sink floor feeds the energy throttle's path term, no 90% jump; low energy (near stall + sinking) stays the one big jump   // Oct 12 (log 02:17): no 90% sink-floor power on final while above the planned speed (110 -> 164 m/s)
                { throttle = Math.Max(throttle, .9); throttleKind = "emergency"; throttleWhy = lowEnergy ? "low-energy floor" : "landing sink floor"; powerFloor = true; }   // arrest the sink with power, not only pitch
            else powerFloor = false;
            if (mode == "takeoff" && vessel.LandedOrSplashed) throttle = tground == null ? 1 : tground.Throttle(Planetarium.GetUniversalTime());   // ramp 25%/s from brake release (no 0->100% jolt)
            if (mode == "landing" && throttleKind != "emergency" && !flareThrCmd) { throttle = StepThrottle.ConfigLimit(prevThrCfg, throttle, dt, Time.realtimeSinceStartup - configChangedAt); }   // GROK LAND5b: 10%/s during + 5 s after a config change
            if (apPhase && !vessel.LandedOrSplashed && throttleKind != "emergency" && !flareThrCmd) { throttle = ApproachThrottle.Limit(prevApOut, throttle, dt); appThr.Cmd = throttle; stepThr.Thr = throttle; }   // THR10: every approach override (idle when fast etc.) goes through the same 15%/s up, 10%/s down
            prevApOut = throttle;
            prevThrCfg = throttle;
            LearnDecel(dt); MeasureLag(dt);
            bool speedLock = ApproachSpeedLocked;
            if (speedLock && !vessel.LandedOrSplashed) { speed = runway.DesiredSpeed; }   // FORCED approach speed: no 3 s/5% stepping, idle when fast, never a climb-first throttle
            UpdateCap();
            { bool cut; double capT = PilotPolicy.SpeedCapThrottle(throttle, vessel.indicatedAirSpeed, vessel.altitude, Time.realtimeSinceStartup - lastCapCut, out cut);
              if (cut && mode != "takeoff" && !capLifted) { throttle = capT; lastCapCut = Time.realtimeSinceStartup; } }
            if (!speedLock && mode != "takeoff" && !vessel.LandedOrSplashed && (runway == null || (runway.Phase != "flare" && runway.Phase != "rollout" && runway.Phase != "stopped")))
                if (mode == "takeoff" || vessel.LandedOrSplashed) throttle = PilotPolicy.ThrottleFloor(throttle, Eas(), speed, stall); else if ((stallEmLatch = PilotPolicy.StallLatch(stallEmLatch, PilotPolicy.StallEmergency(Eas(), vessel.srfSpeed, vessel.atmDensity, stall, StallEstimate()) && !PilotPolicy.LandSlowOk(mode == "landing" && runway != null ? runway.Phase : "", Eas(), stall, vessel.radarAltitude), Eas(), stall, StallEstimate()))) { if (Time.realtimeSinceStartup >= stallEmTraceNext) { stallEmTraceNext = Time.realtimeSinceStartup + 5; ChatLog.Write("throttle", "stall emergency: eas=" + Eas().ToString("0") + " ias=" + vessel.indicatedAirSpeed.ToString("0") + " rho=" + vessel.atmDensity.ToString("0.000") + " srf=" + vessel.srfSpeed.ToString("0") + " stall=" + stall.ToString("0") + " est=" + StallEstimate().ToString("0") + (learn != null ? " learn=" + learn.Phase : "")); }  throttle = 1; throttleKind = "emergency"; throttleWhy = "below 1.15x stall"; StallCrew(); }   // airborne: the PI holds speed; only a real near-stall slams power   // never trade airspeed below the band / stall margin   // Luke: 200 target / 220 cap low down, cut fast when over
                { double ho = PilotPolicy.HoldThrottleOut(holdSpeed, lowEnergy && !vessel.LandedOrSplashed, throttle); if (!double.IsNaN(ho)) { c.mainThrottle = (float)ho; if (!holdSpeed) FlightInputHandler.state.mainThrottle = (float)ho; } }   // low energy overrides even with the speed hold off
            FerryTick(); FlapsTick();
            LearnApply(c);
            LogLandingLateral(c);
            if (mode == "takeoff" && vessel.LandedOrSplashed && throttleKind == "pi") { throttleKind = "test"; throttleWhy = "takeoff roll power ramp"; }
            throttleKind = ThrottleGate.LandingKind(throttleKind, mode == "landing" && runway != null && !vessel.LandedOrSplashed, !double.IsNaN(stepThr.Thr), vessel.srfSpeed, runway != null ? runway.DesiredSpeed : double.NaN, c.mainThrottle, double.IsNaN(gate.Out) ? c.mainThrottle : gate.Out);   // GROK LAND5j
            gate.K = shold.K; c.mainThrottle = (float)gate.Step(c.mainThrottle, dt, throttleKind, throttleWhy); if (holdSpeed || learn != null || throttleKind == "emergency") FlightInputHandler.state.mainThrottle = c.mainThrottle; throttleKind = "pi"; throttleWhy = "";   // ONE smooth output path; the pilot's throttle mirrors it
                if (props.Rotors.Count > 0 && !props.HasLift(vessel) && mode != "spool")
                {
                    var samples = props.Sample(); double rpm = double.PositiveInfinity;
                    foreach (var rotor in samples) rpm = Math.Min(rpm, rotor.Rpm);
                    if (!double.IsNaN(rpm) && !double.IsInfinity(rpm) && rpm > 1 && !double.IsNaN(props.Radius))
                        props.Collective((float)FlightPolicy.Clamp(Math.Atan2(vessel.srfSpeed, rpm * 2 * Math.PI / 60 * props.Radius) * 180 / Math.PI + 8, 5, 45));
                }
            }
            catch (Exception ex) { FailSoft(c, ex); }
        }
        void Stop(bool interruptPlan = true)
        {
            CancelRoverLearn();
            pendingLand = null; goAroundFlaps = false; try { RestoreFlaps(); } catch (Exception) { }
            bankOverride = false; pendingCircle = null;
            if ((mode == "taxi" || mode == "rover") && vessel != null)
            {
                vessel.ctrlState.wheelThrottle = vessel.ctrlState.wheelSteer = 0;
                FlightInputHandler.state.wheelThrottle = FlightInputHandler.state.wheelSteer = 0;
                if (vessel.LandedOrSplashed) { vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0; SetGroup(vessel, KSPActionGroup.Brakes, true); }
                roverSafetySlow = roverSafetyBrake = false;
                roverNavigating = false; roverDestination = null;
            }
            if (reverseRollout != null && reverseRollout.Reversed && vessel != null)
            {
                vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = vessel.LandedOrSplashed ? 0 : .05f;
                try { reverseRollout.Cancel(reverse => reversers.Set(vessel, reverse)); }
                catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] Release reversers: " + ex.Message); }
            }
            reverseRollout = null;
            if (interruptPlan && plan != null) plan.Interrupt();
            mode = "idle"; lease.Release(); capture = null; spool = null;
            rotorPark = null; landingAfterTakeoff = false; verticalLanding = null;
            stallStudy = null; needStallStudy = false;
            engines.Cancel();
            NativeCommands.ClearStatus();
        }
        void TickPlan()
        {
            if (plan == null || !plan.Running) return;
            double now = Planetarium.GetUniversalTime(), dt = Math.Max(0, Math.Min(1, now - planLastTime)); planLastTime = now;
            var step = plan.Steps[plan.Index];
            if (!plan.Entered)
            {
                var args = new Dictionary<string, object>(); string result = "";
                if (step.Op == "takeoff") result = Command("takeoff", args);
                else if (step.Op == "taxi") { args["name"] = step.Destination; result = Command("taxi_to", args); }
                else if (step.Op == "land") { args["name"] = FlightResidualPolicy.BuiltInRunway(step.Destination, "") != null ? FlightResidualPolicy.RunwayAlias(step.Destination) : step.Destination; args["short_final"] = step.Destination.ToLowerInvariant().Contains("short"); result = Command("land_plane", args); }
                else if (step.Op == "head")
                {
                    bool isl = step.Destination == "island";
                    step.Heading = NavigationMath.Bearing(vessel.latitude, vessel.longitude, isl ? -1.5154 : -.0494058, isl ? -71.9093 : -74.6073375);
                    args["heading"] = step.Heading; args["altitude_m"] = -1; args["speed"] = -1; result = Command("plane_hold", args);
                    if (step.Bank != 0) { directBank = step.Bank; bankOverride = Math.Abs(step.Bank) > FlightPolicy.BankLimit(vessel.srfSpeed); }
                }
                else if (step.Op == "turnaround") { step.Heading = FlightPolicy.Wrap(FlightGlobals.ship_heading + 180) + 180; step.Heading = (FlightGlobals.ship_heading + 180) % 360; args["heading"] = step.Heading; args["altitude_m"] = -1; args["speed"] = -1; result = Command("plane_hold", args);
                    if (step.Bank != 0) { directBank = step.Bank; bankOverride = Math.Abs(step.Bank) > FlightPolicy.BankLimit(vessel.srfSpeed); } }
                else if (step.Op == "pitch") { PilotPolicy.ApplyPitchStep(step.Pitch, ref climbPitchMax, ref descentPitchMin); result = "ok"; }
                else if (step.Op == "bank") { planBank = step.Bank; result = "ok"; }
                else if (step.Op != "wait" && step.Op != "turnaround" && step.Op != "head")
                {
                    args["altitude_m"] = step.Altitude; args["altitude_ref"] = step.Reference;
                    args["heading"] = step.Heading; args["speed"] = step.Speed;
                    args["vertical_speed"] = step.VerticalSpeed;
                    result = Command("plane_hold", args);
                    if (step.Op == "circle") { directBank = planBank != 0 ? Math.Sign(step.Bank) * Math.Abs(planBank) : step.Bank; bankOverride = Math.Abs(directBank.Value) > FlightPolicy.BankLimit(vessel.srfSpeed); planTurn = 0; planLastHeading = FlightGlobals.ship_heading; }
                }
                bool accepted = step.Op == "wait" || step.Op == "pitch" || step.Op == "bank" || result == "Local aircraft holds engaged."
                    || result == "Local taxi started: straight lines; no obstacle avoidance."
                    || result == "Local autoland: takeoff then approach entry."
                    || result == "Local takeoff engaged." || result == "Local rotor spool started; waiting for measured RPM."
                    || result == "Local autoland: proceeding to approach entry.";
                if (!accepted) { plan.Interrupt(); ChatWindow.Notice("Plan paused: " + result); return; }
                plan.Entered = true;
                ChatWindow.Notice("Local plan: " + step.Text);
            }
            else { plan.Elapsed += dt; plan.DistanceElapsed += Math.Max(0, vessel.srfSpeed) * dt; }
            bool complete = false;
            if (step.Op == "wait" || step.Op == "cruise") complete = step.Distance > 0 ? plan.DistanceElapsed >= step.Distance : plan.Elapsed >= step.Seconds;
            else if (step.Op == "taxi") complete = taxi != null && taxi.Result != null && taxi.Result.Contains("arrived");
            else if (step.Op == "takeoff") complete = mode == "hold" && vessel.radarAltitude >= 100;
            else if (step.Op == "head") { complete = Math.Abs(FlightPolicy.Wrap(step.Heading - FlightGlobals.ship_heading)) < 12; if (complete) { directBank = null; bankOverride = false; holdHeading = true; heading = step.Heading; } }
            else if (step.Op == "turnaround") { complete = Math.Abs(FlightPolicy.Wrap(step.Heading - FlightGlobals.ship_heading)) < 15; if (complete && step.Bank != 0) { directBank = null; bankOverride = false; holdHeading = true; heading = step.Heading; } }
            else if (step.Op == "pitch" || step.Op == "bank") complete = true;
            else if (step.Op == "climb") complete = Math.Abs(vessel.altitude - altitude) <= band;
            else if (step.Op == "land") complete = vessel.LandedOrSplashed && vessel.srfSpeed < 1;
            else if (step.Op == "circle")
            {
                double delta = FlightPolicy.Wrap(FlightGlobals.ship_heading - planLastHeading); planLastHeading = FlightGlobals.ship_heading;
                planTurn += delta * Math.Sign(step.Bank); complete = planTurn >= step.Laps * 360;
            }
            if (complete) { plan.Advance(); if (!plan.Running) { directBank = null; ChatWindow.Notice("Local flight plan complete."); } }
        }
        string StartRotorMode(string nextMode)
        {
            if (props.Rotors.Count == 0) { mode = nextMode; return "Local " + mode + " engaged."; }
            if (!vessel.LandedOrSplashed) { mode = nextMode; return "Local " + mode + " engaged."; }
            if (!FlightPolicy.RotorPowerReady(NativePropulsion.Charge(vessel))) { Stop(); return "No juice to the rotors! Need at least 25% electric charge before spool-up."; }
            props.Collective(0); props.Set(0);
            spool = new RotorSpool(props.Ids(), Time.realtimeSinceStartup); afterSpool = nextMode;
            mode = "spool"; nextSpool = 0; spoolSequence = 0;
            return "Local rotor spool started; waiting for measured RPM.";
        }
        void NudgeTrim(bool force)
        {
            if (!auto["master"] || trimSuspended) return;
            if (Math.Abs(vessel.verticalSpeed) >= 1 || Math.Abs(Roll()) >= 5) return;
            double input = vessel.ctrlState.pitch;
            if (auto["roll"]) vessel.ctrlState.rollTrim = (float)FlightPolicy.Trim(vessel.ctrlState.rollTrim, vessel.ctrlState.roll);
            if (auto["yaw"]) vessel.ctrlState.yawTrim = (float)FlightPolicy.Trim(vessel.ctrlState.yawTrim, vessel.ctrlState.yaw);
            SyncTrim();
            trimBaselineVs = vessel.verticalSpeed; trimBaselinePitch = Pitch(); watchTrimUntil = Time.realtimeSinceStartup + 8;
            if (!auto["pitch"] || (!force && Math.Abs(input) < .04)) return;
            foreach (var pair in originals)
            {
                var s = pair.Key;
                if (s == null || s.ignorePitch || s.part.partInfo.title.IndexOf("blade", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!recovery.CanTrim(s)) continue; // do not adopt a pending sabotage change as a trim baseline
                double forward = Vector3.Dot(s.part.transform.position - vessel.CoM, vessel.ReferenceTransform.up);
                double sign = FlightPolicy.SurfaceSign(forward, s.partDeployInvert, s.deployInvert);
                if (!PilotPolicy.SurfaceTrimAllowed(input, Pitch())) continue;   // anti-windup both ways (GROK FIX5)
                if (BrakesBound(s)) continue;   // GROK FIX5: deploy/deployAngle of a Brakes-bound surface IS the airbrake - trim never writes it
                s.deployAngle = (float)FlightPolicy.Clamp(s.deployAngle + Math.Sign(input) * sign * Math.Min(.2, Math.Abs(input) * 2), -PilotPolicy.TrimSurfaceMax, PilotPolicy.TrimSurfaceMax);
                s.deploy = Math.Abs(s.deployAngle) > .05;
                recovery.AcceptSurface(s);
            }
            vessel.ctrlState.pitchTrim = (float)PilotPolicy.TrimStep(vessel.ctrlState.pitchTrim, input, Pitch());
            SyncTrim();
            trimBaselineVs = vessel.verticalSpeed; trimBaselinePitch = Pitch(); watchTrimUntil = Time.realtimeSinceStartup + 8;
        }
        internal static string Execute(string name, string argsJson)
        {
            // AI-off autopilot, or in-mod chat tool dispatch (NativeChat) may call into the local command layer.
            bool allowed = true;
            if (!allowed) return "Local mode is not ready.";
            if (name == "tech_advisor") { try { return TechAdvisor.Run(MiniJson.Deserialize(argsJson ?? "{}")); } catch (Exception ex) { return "Tech advice failed: " + ex.Message; } }
            if (instance == null || FlightGlobals.ActiveVessel == null) return "No active flight vessel.";
            try
            {
                instance.Bind(); instance.ExtrasCancel(name);
                if (name == "set_engines" || name == "cut_engines" || name == "abort" || name == "engine_mode" || name == "afterburner") EnginesCommandedAt = Time.realtimeSinceStartup;
                if (instance.plan != null && instance.plan.Running && PilotPolicy.PreemptsPlan(name))   // new orders replace the mission
                { instance.plan.Pause(); ChatLog.Write("mission", "flight plan paused by " + name); }
                string r = instance.Command(name, MiniJson.Deserialize(argsJson ?? "{}"));
                if (PilotPolicy.LogsTelemetry(name)) ChatLog.Write("T", instance.Telemetry());
                return r;
            }
            catch (Exception ex) { return "Local command failed: " + ex.Message; }
        }
        void SyncTrim()
        {
            FlightInputHandler.state.pitchTrim = vessel.ctrlState.pitchTrim;
            FlightInputHandler.state.rollTrim = vessel.ctrlState.rollTrim;
            FlightInputHandler.state.yawTrim = vessel.ctrlState.yawTrim;
        }
        void ApplyCraftNotes()
        {
            var notes = craftNotes.Load(vessel.vesselName);
            vessel.ctrlState.pitchTrim = (float)FlightPolicy.Clamp(Num(notes, "pitch_trim", vessel.ctrlState.pitchTrim), -1, 1);
            vessel.ctrlState.rollTrim = (float)FlightPolicy.Clamp(Num(notes, "roll_trim", vessel.ctrlState.rollTrim), -1, 1);
            vessel.ctrlState.yawTrim = (float)FlightPolicy.Clamp(Num(notes, "yaw_trim", vessel.ctrlState.yawTrim), -1, 1);
            SyncTrim();
            if (!notes.ContainsKey("pitch_deploy_bias")) return;
            double bias = FlightPolicy.Clamp(Num(notes, "pitch_deploy_bias", 0), -12, 12);
            foreach (var pair in originals)
            {
                var surface = pair.Key;
                if (surface == null || surface.ignorePitch || surface.part.partInfo.title.IndexOf("blade", StringComparison.OrdinalIgnoreCase) >= 0 || !recovery.CanTrim(surface)) continue;
                double forward = Vector3.Dot(surface.part.transform.position - vessel.CoM, vessel.ReferenceTransform.up);
                surface.deployAngle = (float)(bias * FlightPolicy.SurfaceSign(forward, surface.partDeployInvert, surface.deployInvert));
                surface.deploy = Math.Abs(bias) > .05; recovery.AcceptSurface(surface);
            }
        }
        NativePlan ParsePlan(string text)
        {
            return NativePlan.Parse(text, name => spots.Runway(name, vessel.mainBody.bodyName, vessel.mainBody.Radius,
                (lat, lon) => vessel.mainBody.pqsController == null ? double.NaN : Math.Max(0, vessel.mainBody.pqsController.GetSurfaceHeight(vessel.mainBody.GetRelSurfaceNVector(lat, lon)) - vessel.mainBody.Radius)) != null,
                route =>
                {
                    try { new TaxiMission(route, vessel.mainBody.bodyName, 8, name => spots.Point(name, vessel.mainBody.bodyName)); return true; }
                    catch (ArgumentException) { return false; }
                });
        }
        string Command(string name, Dictionary<string, object> a)
        {
            if (name != null && name.StartsWith("rocket_", StringComparison.Ordinal))
            {
                string rocketResult = RocketCommand(name, a);
                if (rocketResult != null) return rocketResult;
            }
            if (!NativeCommands.IsPorted(name)) return "Local command not yet ported: " + name;
            if (name == "get_gps" && vessel == null) return "No active vessel.";
            string extra = ExtrasCommand(name, a); if (extra != null) return extra;
            switch (name)
            {
                case "save_craft_notes":
                    var note = new Dictionary<string, object> { { "pitch_trim", vessel.ctrlState.pitchTrim }, { "roll_trim", vessel.ctrlState.rollTrim }, { "yaw_trim", vessel.ctrlState.yawTrim }, { "cruise_speed", speed } };
                    double biasTotal = 0; int pitchSurfaces = 0;
                    foreach (var pair in originals)
                    {
                        var surface = pair.Key;
                        if (surface == null || surface.ignorePitch || surface.part.partInfo.title.IndexOf("blade", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        double forward = Vector3.Dot(surface.part.transform.position - vessel.CoM, vessel.ReferenceTransform.up);
                        biasTotal += surface.deployAngle * FlightPolicy.SurfaceSign(forward, surface.partDeployInvert, surface.deployInvert); pitchSurfaces++;
                    }
                    if (pitchSurfaces > 0) note["pitch_deploy_bias"] = biasTotal / pitchSurfaces;
                    note["rotor"] = new Dictionary<string, object> { { "collective", props.CollectiveValue }, { "yaw_torque_bias", rotorYawBias } };
                    craftNotes.Merge(vessel.vesselName, note); Save(); return "Local craft notes saved for " + vessel.vesselName + ".";
                case "taxi/list":
                case "list_taxi_points":
                    var points = new System.Text.StringBuilder();
                    var available = new List<TaxiMission.Point>(spots.Points(vessel.mainBody.bodyName));
                    if (vessel.mainBody.bodyName == "Kerbin") available.AddRange(TaxiMission.Builtin.Values);
                    foreach (var point in available) points.Append(point.Name).Append('\t').Append(point.Lat.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(point.Lon.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    return points.ToString();
                case "list_landing_spots":
                    string spotList = spots.List();
                    return spotList.Length > 0 ? spotList : "No saved landing spots yet.";
                case "set_ai_name":
                    string newName = PlaystyleNotes.Collapse(Str(a, "name", ""));
                    if (newName.Length == 0) return "Name can't be empty.";
                    settingsData["ai_name"] = newName.Length > 24 ? newName.Substring(0, 24).TrimEnd(' ', '.', '!', '"', '\'') : newName;
                    Save(); return "Got it - my name is now " + settingsData["ai_name"] + ".";
                case "remember_preference":
                    return PlaystyleNotes.Remember(Str(a, "note", ""));
                case "save_landing_spot":
                    double spotLat = Num(a, "latitude", 999), spotLon = Num(a, "longitude", 999);
                    bool spotHere = spotLat == 999 || spotLon == 999;
                    if (spotHere) { spotLat = vessel.latitude; spotLon = vessel.longitude; }
                    string spotMode = Str(a, "mode", "V").ToUpperInvariant();
                    if (spotMode == "") spotMode = vessel.LandedOrSplashed && !props.HasLift(vessel) ? "H" : "V";
                    double spotHeading = Num(a, "heading", -1); if (spotHeading < 0) spotHeading = FlightGlobals.ship_heading;
                    double spotElevation = spotHere ? vessel.altitude - vessel.radarAltitude : vessel.mainBody.pqsController == null ? double.NaN
                        : Math.Max(0, vessel.mainBody.pqsController.GetSurfaceHeight(vessel.mainBody.GetRelSurfaceNVector(spotLat, spotLon)) - vessel.mainBody.Radius);
                    spots.Save(Str(a, "name", ""), vessel.mainBody.bodyName, spotMode, spotLat, spotLon, spotHeading, spotElevation);
                    Save(); return "Local landing spot saved.";
                case "taxi_to":
                    if (!vessel.LandedOrSplashed) return "Taxi requires a grounded vessel.";
                    bool taxiWheels = false; poweredTaxi = false;
                    foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
                    { taxiWheels |= module is ModuleWheelBase; var motor = module as ModuleWheels.ModuleWheelMotor; poweredTaxi |= motor != null && motor.motorEnabled; }
                    if (!taxiWheels) return "No wheels for taxi.";
                    var route = new TaxiMission(Str(a, "name", ""), vessel.mainBody.bodyName, Num(a, "speed", 8), label => spots.Point(label, vessel.mainBody.bodyName));
                    BeginHold(); taxi = route; mode = "taxi"; parkingReleased = true;
                    SetGroup(vessel, KSPActionGroup.Brakes, false);
                    if (poweredTaxi) engines.Cancel(); else NativeEngines.Takeoff(vessel);
                    return "Local taxi started: straight lines; no obstacle avoidance.";
                case "drive_speed":
                    double roverSpeed = Num(a, "speed", double.NaN);
                    if (double.IsNaN(roverSpeed) || double.IsInfinity(roverSpeed) || roverSpeed < 0 || roverSpeed > 25)
                        return "Rover speed must be between 0 and 25 m/s.";
                    if (!vessel.Landed) return "Rover speed hold requires a landed vessel.";
                    double requestedRoverSpeed = roverSpeed;
                    RoverProfile speedProfile = CurrentRoverProfile();
                    if (speedProfile != null) roverSpeed = KscRoverPolicy.LearnedTopSpeed(roverSpeed, speedProfile.TopSpeed);
                    bool roverWheels = false, roverMotors = false;
                    foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
                    {
                        roverWheels |= module is ModuleWheelBase;
                        var motor = module as ModuleWheels.ModuleWheelMotor;
                        roverMotors |= motor != null && motor.motorEnabled;
                    }
                    if (!roverWheels || !roverMotors) return "Rover speed hold requires powered wheels.";
                    if (mode != "rover") BeginHold();
                    roverTargetSpeed = roverSpeed; roverIntegral = 0; mode = "rover";
                    vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0;
                    SetGroup(vessel, KSPActionGroup.Brakes, false);
                    ChatLog.Write("rover", "speed target " + roverSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s");
                    return "Rover speed hold: " + roverSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s."
                        + (roverSpeed < requestedRoverSpeed ? " Capped to learned top speed." : "");
                case "drive_heading":
                    double roverHeadingTarget = Num(a, "heading", double.NaN);
                    if (double.IsNaN(roverHeadingTarget) || double.IsInfinity(roverHeadingTarget))
                        return "Rover heading must be a number in degrees.";
                    if (mode != "rover") return "Start rover speed hold with \"drive speed N\" first.";
                    roverHeading = (roverHeadingTarget % 360 + 360) % 360;
                    roverNavigating = false; roverDestination = null;
                    roverHeadingEnabled = true;
                    ChatLog.Write("rover", "heading target " + roverHeading.ToString("0.0", CultureInfo.InvariantCulture) + " deg");
                    return "Rover heading hold: " + roverHeading.ToString("000", CultureInfo.InvariantCulture) + " deg.";
                case "drive_to":
                    string destinationName = Str(a, "name", "").Trim();
                    if (destinationName.Length == 0) return "Rover destination can't be empty.";
                    if (!vessel.Landed) return "Rover navigation requires a landed vessel.";
                    bool driveWheels = false, driveMotors = false;
                    foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
                    {
                        driveWheels |= module is ModuleWheelBase;
                        var motor = module as ModuleWheels.ModuleWheelMotor;
                        driveMotors |= motor != null && motor.motorEnabled;
                    }
                    if (!driveWheels || !driveMotors) return "Rover navigation requires powered wheels.";
                    TaxiMission destinationRoute;
                    try { destinationRoute = new TaxiMission(destinationName, vessel.mainBody.bodyName, 8, label => spots.Point(label, vessel.mainBody.bodyName)); }
                    catch (ArgumentException ex) { return ex.Message; }
                    if (destinationRoute.Points.Count != 1) return "Enter one rover waypoint or latitude,longitude coordinate.";
                    TaxiMission.Point destinationPoint = destinationRoute.Points[0];
                    if (mode != "rover") BeginHold();
                    if (roverTargetSpeed <= 0) roverTargetSpeed = 8;
                    roverDestination = destinationPoint.Name;
                    roverDestinationLat = destinationPoint.Lat; roverDestinationLon = destinationPoint.Lon;
                    roverNavigating = true; roverHeadingEnabled = true; roverIntegral = 0; mode = "rover";
                    vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0;
                    SetGroup(vessel, KSPActionGroup.Brakes, false);
                    ChatLog.Write("rover", "drive to " + roverDestination + " at " + roverTargetSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s");
                    return "Rover driving to " + roverDestination + " at up to " + roverTargetSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s.";
                case "abort":
                    if (plan != null) plan.Pause();
                    bool descending = mode == "vertical landing";
                    Stop(); vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0; gate.Seed(0, "hard abort");
                    return descending ? "Hard abort: powered descent stopped, throttle zero." : "Hard abort: local control stopped, throttle zero.";
                case "stop_current": if (plan != null) plan.Pause(); Stop(); return "Local controller stopped; throttle preserved.";
                case "flightplan/check": ParsePlan(Str(a, "plan", "")); return "Local plan valid.";
                case "flightplan/stop": if (plan != null) plan.Pause(); return "Plan paused; active controller continues.";
                case "flightplan/resume":
                    if (plan == null) return "No local plan to resume.";
                    if (!plan.Paused) return "Plan is not paused.";
                    plan.Start(); planLastTime = Planetarium.GetUniversalTime();
                    return "Local plan " + plan.Status;
                case "flightplan/status": return PlanStatus;
                case "flightplan/follow":   // Luke: "follow/fly/run/resume the flight plan" runs the EXISTING plan from its current step
                    if (plan == null) { string ed = AicsMenu.PlanText; if (string.IsNullOrEmpty(ed) || ed.Trim().Length == 0) return "No flight plan yet - say \"make a plan: ...\" first."; plan = ParsePlan(ed); planBank = 0; climbPitchMax = 15; descentPitchMin = -5; }
                    if (plan.Running && !plan.Paused) return "Local plan already running: " + plan.Status;
                    plan.Start(); planLastTime = Planetarium.GetUniversalTime(); return "Local plan resumed: " + plan.Status;
                case "flightplan/fly":
                    string text = Str(a, "plan", "");
                    if (plan != null && plan.Text.Trim() == text.Trim() && plan.Running && !plan.Paused) return "Local plan already running: " + plan.Status;   // never reset a running plan
                    if (plan == null || plan.Text.Trim() != text.Trim()) { plan = ParsePlan(text); planBank = 0; climbPitchMax = 15; descentPitchMin = -5; }   // same text = resume from the current step
                    plan.Start(); planLastTime = Planetarium.GetUniversalTime(); return "Local plan " + plan.Status;
                case "get_status": case "autopilot_status": return PilotPolicy.StatusLine(mode, vessel.vesselName, mode == "landing" && runway != null ? runway.Phase : null, mode == "landing" && runway != null ? runway.ToGo : double.NaN, vessel.altitude, vessel.srfSpeed, FlightGlobals.ship_heading, plan != null && plan.Running ? plan.Index + 1 : 0);
                case "get_gps":
                    if (vessel == null) return "No active vessel.";
                    Vector3d v = vessel.srf_velocity;
                    double track = FlightPolicy.GroundTrack(Vector3d.Dot(v, vessel.north), Vector3d.Dot(v, vessel.east));
                    return GpsInfo.Format(vessel.vesselName, vessel.mainBody.bodyName, vessel.mainBody.Radius, vessel.latitude, vessel.longitude, vessel.altitude, vessel.radarAltitude, FlightGlobals.ship_heading, track, vessel.horizontalSrfSpeed, vessel.Landed || vessel.Splashed, vessel.situation.ToString(), Planetarium.GetUniversalTime());
                case "set_gear": gearSaid = Bool(a, "down", true); SetGroup(vessel, KSPActionGroup.Gear, gearSaid.Value); return "Gear " + (gearSaid.Value ? "down." : "up.");
                case "set_brakes": parkingReleased = !Bool(a, "on", true); SetGroup(vessel, KSPActionGroup.Brakes, !parkingReleased); return "Brakes set.";
                case "set_lights": SetGroup(vessel, KSPActionGroup.Light, Bool(a, "on", true)); return "Lights set.";
                case "set_rcs": SetGroup(vessel, KSPActionGroup.RCS, Bool(a, "on", true)); return "RCS set.";
                case "set_sas": if (Active && Bool(a, "enabled", true)) { if (plan != null) plan.Pause(); Stop(); ChatLog.Write("ap", "released for SAS (player asked)"); } SetGroup(vessel, KSPActionGroup.SAS, Bool(a, "enabled", true)); return "SAS set.";
                case "get_trim_state":
                    var trim = new Dictionary<string, object> { { "pitch", vessel.ctrlState.pitchTrim }, { "roll", vessel.ctrlState.rollTrim }, { "yaw", vessel.ctrlState.yawTrim }, { "craft", vessel.vesselName }, { "heli", props.HasLift(vessel) }, { "notes_saved", false } };
                    trim["collective"] = props.CollectiveValue;
                    trim["notes_saved"] = craftNotes.Load(vessel.vesselName).Count > 0;
                    foreach (var kv in auto) trim["auto_" + kv.Key] = kv.Value;
                    return MiniJson.Serialize(trim);
                case "set_trim":
                    string axis = Str(a, "axis", "pitch"); float value = (float)FlightPolicy.Clamp(Num(a, "value", 0), -1, 1);
                    if (axis.StartsWith("auto_") && auto.ContainsKey(axis.Substring(5))) { auto[axis.Substring(5)] = value >= .5; Save(); return "Auto-trim setting saved."; }
                    if (axis == "collective") { props.Collective((float)FlightPolicy.Clamp(Num(a, "value", 0), 0, 12)); return "Collective set."; }
                    if (axis == "pitch") vessel.ctrlState.pitchTrim = value;
                    else if (axis == "roll") vessel.ctrlState.rollTrim = value;
                    else if (axis == "yaw") vessel.ctrlState.yawTrim = value;
                    else return "Unsupported trim axis.";
                    SyncTrim(); return axis + " trim set.";
                case "auto_trim_now": NudgeTrim(true); return "Trim checked; adjustment requires steady level flight and enabled axes.";
                case "trim":
                    if (Str(a, "direction", "up") != "reset") return "Use the local trim sliders to set trim.";
                    foreach (var pair in originals) if (pair.Key != null) { pair.Value.Restore(pair.Key); recovery.AcceptSurface(pair.Key); }
                    vessel.ctrlState.pitchTrim = vessel.ctrlState.rollTrim = vessel.ctrlState.yawTrim = 0;
                    SyncTrim();
                    trimSuspended = false; watchTrimUntil = 0; return "Trim and original surface state restored.";
                case "plane_hold":
                    if (!Bool(a, "engage", true)) { Stop(); return "Local holds disengaged."; }
                    var off = new HashSet<string>(Str(a, "off", "").Split(','));
                    holdAltitude = !off.Contains("altitude"); holdHeading = !off.Contains("heading"); holdSpeed = !off.Contains("speed");
                    altitude = Num(a, "altitude_m", vessel.altitude);
                    if (altitude < 0) altitude = vessel.altitude;
                    else if (Str(a, "altitude_ref", "agl") == "agl") altitude += vessel.altitude - vessel.radarAltitude;
                    heading = Num(a, "heading", FlightGlobals.ship_heading); if (heading < 0) heading = FlightGlobals.ship_heading;
                    speed = PilotPolicy.HoldEngageSpeed(Num(a, "speed", -1), vessel.srfSpeed, stall);   // capture the current speed; never 0 or below 1.3x stall
                    double vs = Num(a, "vertical_speed", -999); directVs = vs > -999 ? (double?)FlightPolicy.Clamp(vs, -90, 90) : null;
                    if (off.Contains("vertical_speed")) directVs = null;
                    double rollTarget = Num(a, "roll", -999);
                    directBank = !off.Contains("roll") && rollTarget > -999 ? (double?)FlightPolicy.Clamp(rollTarget, -FlightPolicy.BankLimit(vessel.srfSpeed), FlightPolicy.BankLimit(vessel.srfSpeed)) : null;
                    directPitch = null; BeginHold();
                    if (!holdSpeed) FlightInputHandler.state.mainThrottle = vessel.ctrlState.mainThrottle;   // speed hold off: the pilot's throttle continues from here (not a stale 0)
                    ChatLog.Write("ap", "hold engage alt=" + altitude.ToString("0") + " hdg=" + heading.ToString("000") + " spd=" + speed.ToString("0") + " off=" + string.Join(",", new List<string>(off).ToArray()) + " thr=" + Math.Round(vessel.ctrlState.mainThrottle * 100) + "%");
                    if (holdHeading) ApFieldSync.Heading(heading);
                    if (learn != null && !learn.Done) learn.UserHold(holdAltitude ? altitude : double.NaN, holdHeading ? heading : double.NaN, holdSpeed && Num(a, "speed", -1) > 0 ? speed : double.NaN);   // Oct 12: a user hold during Learn wait / fly-home wins
                    if (vessel.LandedOrSplashed) { altitude = Math.Max(altitude, vessel.altitude + 300); return StartTakeoff(); }
                    return "Local aircraft holds engaged.";
                case "takeoff":
                    altitude = vessel.altitude + Math.Max(100, Num(a, "altitude_m", 300)); heading = FlightGlobals.ship_heading;
                    if (!vessel.LandedOrSplashed) return "Already airborne.";
                    BeginHold();
                    {   // GROK TO2: takeoff settings (tablet TAKEOFF page / chat): climb rate, heading after the climbout, max climb pitch, flaps
                        string tr = StartTakeoff(); if (mode != "takeoff" || takeoff == null) return tr;
                        toClimbVs = TakeoffMission.ClimbRateCap(Num(a, "climb_rate", double.NaN)); toHeading = TakeoffMission.HeadingAfter(Num(a, "heading", double.NaN), double.NaN);
                        takeoff.MaxPitch = TakeoffMission.MaxPitchSetting(Num(a, "max_pitch", double.NaN)); takeoffFlaps = Bool(a, "use_flaps", true);
                        ChatLog.Write("takeoff", "settings: to " + (altitude - vessel.altitude).ToString("0") + " m above the runway, climb " + (double.IsNaN(toClimbVs) ? "auto" : toClimbVs.ToString("0") + " m/s") + ", heading " + (double.IsNaN(toHeading) ? "runway" : toHeading.ToString("000")) + " after the climbout, max pitch " + takeoff.MaxPitch.ToString("0") + ", flaps " + (takeoffFlaps ? "on" : "off"));
                        return tr;
                    }
                case "land_here":
                    if (vessel.LandedOrSplashed) return "Already grounded.";
                    if (props.HasLift(vessel)) return "Use helicopter land for a rotorcraft.";
                    if (Bool(a, "use_chutes", false)) return "Local powered descent does not deploy parachutes yet.";
                    var descentController = new NativeVerticalLanding(vessel, Num(a, "touchdown_speed", 1.5), Planetarium.GetUniversalTime());
                    BeginHold(); engines.Cancel(); verticalLanding = descentController;
                    vessel.ctrlState.pitchTrim = vessel.ctrlState.rollTrim = vessel.ctrlState.yawTrim = 0; SyncTrim();
                    SetGroup(vessel, KSPActionGroup.Gear, true); mode = "vertical landing";
                    return "Local powered descent engaged; upright near-vertical descent only.";
                case "land_plane": case "land_at_spot":
                    if (NativeSafety.RefuseRepeatLand(Bool(a, "_from_ferry", false), pendingLand != null, mode)) return "Landing already in progress (" + (pendingLand != null ? "cruise to runway" : runway != null ? runway.Phase : mode) + ") - disengage first to restart it.";   // GROK FIX6: 4 presses = 4 ferry restarts
                    if (Str(a, "mode", "H").ToUpperInvariant() == "V") return "Targeted vertical landing is not yet ported; use local powered descent here.";
                    if (Num(a, "touch_and_go", 0) != 0) return "Local runway landing does not yet support touch-and-go.";
                    if (Num(a, "final_km", 0) != 0 || Num(a, "approach_heading", -1) != -1) ChatLog.Write("ap", "land: ignoring final_km/approach_heading (chart decides)");
                    bool wheels = false, wings = false;
                    foreach (Part part in vessel.parts) { wheels |= part.FindModuleImplementing<ModuleWheelBase>() != null; wings |= part.FindModuleImplementing<ModuleLiftingSurface>() != null; }
                    if (!wheels || !wings) return "Plane autoland requires wheels and wings.";
                    string destination = Str(a, "name", ""), direction = Str(a, "runway", "");
                    var selectedRunway = spots.Runway(destination, vessel.mainBody.bodyName, vessel.mainBody.Radius,
                        (lat, lon) => vessel.mainBody.pqsController == null ? double.NaN : Math.Max(0, vessel.mainBody.pqsController.GetSurfaceHeight(vessel.mainBody.GetRelSurfaceNVector(lat, lon)) - vessel.mainBody.Radius));
                    bool savedRunway = selectedRunway != null;
                    var builtIn = FlightResidualPolicy.BuiltInRunway(destination, direction);
                    if (savedRunway && builtIn != null)   // explicit KSC/Island prefix + built-ins beat a fuzzy saved-spot match
                    {
                        var spotNames = new List<string>(); foreach (var pt in spots.Points(vessel.mainBody.bodyName)) spotNames.Add(pt.Name);
                        if (FlightResidualPolicy.PreferBuiltIn(destination, spotNames)) { selectedRunway = null; savedRunway = false; }
                    }
                    if (destination.Trim().Equals("target", StringComparison.OrdinalIgnoreCase) && !savedRunway)
                {
                    var tgt = FlightGlobals.fetch == null ? null : FlightGlobals.fetch.VesselTarget;
                    if (tgt == null || tgt.GetTransform() == null) return "No KSP target set (set a target, or name a runway).";
                    Vector3d tp = tgt.GetTransform().position;
                    builtIn = Tuple.Create(NearestBuiltIn(vessel.mainBody.GetLatitude(tp), vessel.mainBody.GetLongitude(tp)), direction);
                }
                bool wantNearest = builtIn != null && builtIn.Item1 == "nearest";
                if (wantNearest)   // Luke: closest of built-ins + saved runway spots, then the end best aligned with our heading
                {
                    var names = new List<string> { "ksc", "island" }; var mids = new List<double[]> { new[] { -.0494058, -74.6073375 }, new[] { -1.5154, -71.9093 } };
                    foreach (var pt in spots.Points(vessel.mainBody.bodyName))
                    {
                        try { var row = spots.Find(pt.Name, vessel.mainBody.bodyName); object mv; if (row != null && row.TryGetValue("mode", out mv) && "H".Equals(mv)) { names.Add("spot:" + pt.Name); mids.Add(new[] { pt.Lat, pt.Lon }); } } catch (ArgumentException) { }
                    }
                    if (vessel.mainBody.bodyName != "Kerbin") { names.RemoveRange(0, 2); mids.RemoveRange(0, 2); }
                    int ni = PilotPolicy.Nearest(vessel.latitude, vessel.longitude, mids, vessel.mainBody.Radius);
                    if (ni < 0) return "No runway known on " + vessel.mainBody.bodyName + ".";
                    if (names[ni].StartsWith("spot:"))
                    {
                        selectedRunway = spots.Runway(names[ni].Substring(5), vessel.mainBody.bodyName, vessel.mainBody.Radius, (lat, lon) => vessel.mainBody.pqsController == null ? double.NaN : Math.Max(0, vessel.mainBody.pqsController.GetSurfaceHeight(vessel.mainBody.GetRelSurfaceNVector(lat, lon)) - vessel.mainBody.Radius));
                        savedRunway = selectedRunway != null; builtIn = null;
                    }
                    else builtIn = Tuple.Create(names[ni], "");
                    direction = "";
                }
                bool island = builtIn != null && builtIn.Item1 == "island";
                    if (!savedRunway)
                    {
                        if (builtIn == null && !savedRunway) return "Unknown local runway: " + destination + " (built-in: KSC 09/27, Island 09/27; or a saved spot).";
                        if (vessel.mainBody.bodyName != "Kerbin") return "Built-in runways are on Kerbin.";
                        direction = builtIn.Item2; destination = "";
                        selectedRunway = island ? new RunwayMission { Lat = -1.516092, Lon = -71.856744, EndLat = -1.514809, EndLon = -71.961815, Elevation = 134.6 }
                            : new RunwayMission { Lat = KscRunway.Lat09, Lon = KscRunway.Lon09, EndLat = KscRunway.Lat27, EndLon = KscRunway.Lon27, Elevation = 69.1 };
                    }
                    if ((direction == "" && (wantNearest || !savedRunway)) ? PilotPolicy.SwapEndInbound(NavigationMath.Bearing(selectedRunway.Lat, selectedRunway.Lon, selectedRunway.EndLat, selectedRunway.EndLon), FlightGlobals.ship_heading,
                        NavigationMath.Bearing(vessel.latitude, vessel.longitude, (selectedRunway.Lat + selectedRunway.EndLat) / 2, (selectedRunway.Lon + selectedRunway.EndLon) / 2),
                        NavigationMath.Distance(vessel.latitude, vessel.longitude, (selectedRunway.Lat + selectedRunway.EndLat) / 2, (selectedRunway.Lon + selectedRunway.EndLon) / 2, vessel.mainBody.Radius))
                    : !savedRunway && (island ? direction == "09" : direction == "27"))
                    { double lat = selectedRunway.Lat, lon = selectedRunway.Lon; selectedRunway.Lat = selectedRunway.EndLat; selectedRunway.Lon = selectedRunway.EndLon; selectedRunway.EndLat = lat; selectedRunway.EndLon = lon; }
                    double rwDist = NavigationMath.Distance(vessel.latitude, vessel.longitude, selectedRunway.Lat, selectedRunway.Lon, vessel.mainBody.Radius);
                    double econ = profile != null && !double.IsNaN(profile.EconSpeed) ? PlaneProfile.ScaleSpeed(profile.EconSpeed, profile.M0, vessel.totalMass)
                        : profile != null && !double.IsNaN(profile.Perf.CruiseSpeed) ? PlaneProfile.ScaleSpeed(profile.Perf.CruiseSpeed, profile.M0, vessel.totalMass) : double.NaN;
                    double cruise = PilotPolicy.FerryCruise(stall, econ, PilotPolicy.CapSpeed);
                    double fDecel = profile != null && !double.IsNaN(profile.Decel) ? profile.DecelAt(vessel.totalMass) : ApproachProfile.MassDecel(vessel.totalMass);
                    double handoff = PilotPolicy.FerryHandoffM(cruise, ApproachProfile.Speed(stall), fDecel / Math.Sqrt(Math.Max(1, shold.K)));
                    if (!Bool(a, "_from_ferry", false) && !vessel.LandedOrSplashed && rwDist > handoff + 2000)
                    {   // Luke 9:58 AM: phase 1 FERRY at cruise (great circle), phase 2 APPROACH from the join area, decel started early
                        var keep = new Dictionary<string, object>(a); keep["_from_ferry"] = true;
                        bool jet = false; foreach (var en in vessel.FindPartModulesImplementing<ModuleEngines>()) foreach (var pr in en.propellants) if (pr.name == "IntakeAir") jet = true;
                        double ceil = profile != null ? profile.Perf.CeilingKm() : double.NaN, cvs = profile != null ? (double.IsNaN(profile.ClimbRateAt(vessel.altitude, vessel.totalMass)) ? double.NaN : profile.ClimbRateAt(vessel.altitude, vessel.totalMass) * .6) : double.NaN;
                        double dvs = PilotPolicy.DescentSink(profile != null ? profile.Dives : null, cruise, PilotPolicy.CapSpeed);   // 25-30 m/s if the learned dives stay inside the cap
                        double falt = PilotPolicy.FerryAltitude(rwDist - handoff, vessel.altitude, ceil, cvs, cruise, dvs, vessel.mainBody.atmosphereDepth, jet), brg = NavigationMath.Bearing(vessel.latitude, vessel.longitude, selectedRunway.Lat, selectedRunway.Lon);
                        string hr = Command("plane_hold", new Dictionary<string, object> { { "altitude_m", falt }, { "altitude_ref", "msl" }, { "heading", brg }, { "speed", cruise } });
                        if (mode != "hold") return hr;
                        if (profile != null && !double.IsNaN(profile.EconThrottle) && profile.EconThrottle > 0 && NativeSafety.SeedEconThrottle(vessel.srfSpeed, cruise)) { throttle = FlightPolicy.Clamp(profile.EconThrottle, .05, 1); gate.Seed(throttle, "ferry econ throttle"); }   // AUTOPILOT_REVIEW #3
                        pendingLand = keep; ferryLat = selectedRunway.Lat; ferryLon = selectedRunway.Lon; ferryNext = 0; ferryHandoff = handoff; ferryTod = PilotPolicy.TopOfDescentM(falt, cruise, dvs); ferryDescending = false; ferrySink = dvs;
                        string fuelNote = "";
                        if (profile != null && profile.Perf.HasCruise)
                        {   // AUTOPILOT_REVIEW #13: warn when cruise burn says the trip needs more fuel than on board
                            double need = profile.Perf.ReserveFrac(rwDist / 1000), have; double tot; FuelState(out have, out tot);
                            if (!double.IsNaN(need) && have < need) fuelNote = " WARNING: need ~" + Math.Round(need * 100) + "% fuel for this leg + go-around, have " + Math.Round(have * 100) + "%.";
                        }
                        ChatLog.Write("ap", "phase FERRY: " + (rwDist / 1000).ToString("0") + " km, cruise " + falt.ToString("0") + " m (jet " + jet + ", ceiling " + (double.IsNaN(ceil) ? "-" : ceil.ToString("0.0")) + " km), descent " + dvs.ToString("0") + " m/s from " + ((handoff + ferryTod) / 1000).ToString("0") + " km, approach from " + (handoff / 1000).ToString("0") + " km, brg " + brg.ToString("000") + ", " + cruise.ToString("0") + " m/s, " + falt.ToString("0") + " m" + fuelNote);
                        return "FERRY: runway " + (rwDist / 1000).ToString("0") + " km, great-circle course " + brg.ToString("000") + " at " + cruise.ToString("0") + " m/s, cruise altitude " + falt.ToString("0") + " m (descent from " + ((handoff + ferryTod) / 1000).ToString("0") + " km). APPROACH starts at " + (handoff / 1000).ToString("0") + " km (decel from there)." + fuelNote;
                    }
                    double approachSpeed = Num(a, "approach_speed", 0);
                    selectedRunway.Terrain = (la, lo) => vessel.mainBody.pqsController == null ? double.NaN : Math.Max(0, vessel.mainBody.pqsController.GetSurfaceHeight(vessel.mainBody.GetRelSurfaceNVector(la, lo)) - vessel.mainBody.Radius);
                    double askBank = Num(a, "bank", 0); selectedRunway.BankDeg = askBank > 0 ? FlightPolicy.Clamp(askBank, 10, 60) : 20;
                    selectedRunway.LongAgl = Num(settingsData, "approach_long_agl", -1); selectedRunway.ShortAgl = Num(settingsData, "approach_short_agl", -1);   // <= 0: 3 deg glideslope (default)
                    {
                        double crsK = NavigationMath.Bearing(selectedRunway.Lat, selectedRunway.Lon, selectedRunway.EndLat, selectedRunway.EndLon);
                        string endK = Math.Abs(FlightPolicy.Wrap(crsK - 90)) < 45 ? "09" : "27";
                        selectedRunway.Key = savedRunway ? Str(a, "name", "") : (island ? "Island " : "KSC ") + endK;
                        try
                        {
                            EnsureCharts(); GenerateCharts(vessel.mainBody); string why; object end = ChartStore.Read(selectedRunway.Key, out why);
                            string pin = Str(a, "chart", "").Trim(); string pinWhy;   // GROK LAND2: the chart picked on the tablet is flown, whatever active.json says later
                            object pinned = ChartStore.ReadPinned(selectedRunway.Key, pin, out pinWhy);
                            if (pinned != null) { end = pinned; why = ""; selectedRunway.ChartPin = pin; }
                            else if (pin.Length > 0) ChatLog.Write("approach", "picked chart " + pin + " not used (" + pinWhy + "); active chart instead");
                            if (why.Length > 0) ChatLog.Write("approach", "chart " + selectedRunway.Key + ": " + why + "; computed chart");
                            {
                                if (end != null)
                                {
                                    selectedRunway.Override = ApproachOverride.Parse(end, selectedRunway.Lat, selectedRunway.Lon, selectedRunway.Elevation, vessel.mainBody.Radius, out why, selectedRunway.Terrain);
                                    ChatLog.Write("approach", selectedRunway.Override != null ? "using " + (selectedRunway.ChartPin.Length > 0 ? selectedRunway.ChartPin + ".json (picked)" : System.IO.Path.GetFileName(ChartStore.PathFor(selectedRunway.Key))) + " for " + selectedRunway.Key + "; profile " + (profileTrusted && profile != null ? vessel.vesselName + "|" + profile.Date : "untrusted/default") : "chart " + selectedRunway.Key + " invalid (" + why + "); computed chart");
                                }
                            }
                        }
                        catch (Exception ex) { ChatLog.Write("approach", "chart unreadable (" + ex.Message + "); computed chart"); }
                    }
                    selectedRunway.WantShort = Bool(a, "short_final", false) || destination.ToLowerInvariant().Contains("short") || Str(a, "name", "").ToLowerInvariant().Contains("short");
                    selectedRunway.ForceKind = RunwayMission.PickedKind(Str(a, "final", ""), selectedRunway.ChartPin);   // GROK LAND2: LONG/SHORT as picked (a picked chart = its own long-final join)
                    ChatLog.Write("approach", "engage: " + selectedRunway.Key + ", chart " + (selectedRunway.ChartPin.Length > 0 ? selectedRunway.ChartPin + " (picked)" : selectedRunway.Override != null ? System.IO.Path.GetFileNameWithoutExtension(ChartStore.PathFor(selectedRunway.Key)) + " (active)" : "computed") + ", final " + (selectedRunway.ForceKind.Length > 0 ? selectedRunway.ForceKind + " (picked)" : "auto"));
                goArounds = 0; landMass0 = 0; capLifted = false; lastAskedSpeed = double.NaN; BeginHold(); runway = selectedRunway; reverseRollout = new ReverseRollout(); derot.Reset(); landingAoa = double.NaN; holdAltitude = holdHeading = holdSpeed = true; mode = "landing"; directPitch = directBank = null;
                    object cacheValue; var learned = settingsData.TryGetValue("stall_speeds", out cacheValue) ? cacheValue as Dictionary<string, object> : null;
                    needStallStudy = approachSpeed <= 0 && (learned == null || !learned.ContainsKey(vessel.vesselName));
                    if (approachSpeed > 0) stall = FlightPolicy.Clamp(approachSpeed / 1.3, 20, 200);
                    if (vessel.LandedOrSplashed)
                    {
                        altitude = vessel.altitude + 300; heading = FlightGlobals.ship_heading;
                        string departure = StartTakeoff();
                        landingAfterTakeoff = mode == "takeoff" || mode == "spool";
                        return landingAfterTakeoff ? "Local autoland: takeoff then approach entry." : departure;
                    }
                    return "Local autoland: proceeding to approach entry.";
                case "heli_control":
                    if (!props.HasLift(vessel)) return "No lift rotor found; use aircraft controls.";
                    string heliMode = Str(a, "mode", "hover");
                    if (heliMode != "hover" && heliMode != "hold" && heliMode != "fly" && heliMode != "land" && heliMode != "stop" && heliMode != "waypoint" && heliMode != "learn") return "Local helicopter mode not yet ported: " + heliMode;
                    if (heliMode == "stop") { Stop(); return "Helicopter control stopped."; }
                    TaxiMission.Point heliDestination = null;
                    if (heliMode == "waypoint" || (heliMode == "fly" && Str(a, "direction", "").Length > 0))
                    {
                        try
                        {
                            var heliRoute = new TaxiMission(Str(a, "name", Str(a, "direction", "")), vessel.mainBody.bodyName, 8,
                                label => spots.Point(label, vessel.mainBody.bodyName));
                            if (heliRoute.Points.Count != 1) return "Choose one helicopter waypoint or latitude,longitude pair.";
                            heliDestination = heliRoute.Points[0];
                        }
                        catch (ArgumentException ex) { return ex.Message; }
                        if (Math.Abs(Pitch()) > 60) return "Select a forward control point for waypoint flight.";
                    }
                    altitude = vessel.altitude - vessel.radarAltitude + Math.Max(1, Num(a, "altitude_m", 20));
                    heading = Num(a, "heading", FlightGlobals.ship_heading); if (heading < 0) heading = FlightGlobals.ship_heading;
                    speed = FlightPolicy.Clamp(Num(a, "speed", 0), 0, 50); helicopterLanding = heliMode == "land";
                    helicopterVerticalOnly = Math.Abs(Pitch()) > 60;
                    if (helicopterVerticalOnly && speed > 0) return "Upward control point: only vertical hover/land is supported. Select a forward control point for translation.";
                    var helicopterNotes = craftNotes.Load(vessel.vesselName); object rotorNotesValue;
                    var rotorNotes = helicopterNotes.TryGetValue("rotor", out rotorNotesValue) ? rotorNotesValue as Dictionary<string, object> : null;
                    double savedCollective = rotorNotes == null ? 4 : Num(rotorNotes, "collective", 4);
                    double savedYawBias = rotorNotes == null ? 0 : Num(rotorNotes, "yaw_torque_bias", 0);
                    BeginHold(); collective = new CollectiveController(savedCollective); helicopterYaw = new HelicopterYaw();
                    rotorEmergency = new RotorEmergency();
                    helicopterTouchdown = new TouchdownGate();
                    helicopterPositionHold = heliMode != "fly"; hoverLat = vessel.latitude; hoverLon = vessel.longitude;
                    BeginHeliLearn(heliMode == "learn");
                    heliWaypoint = heliDestination;
                    heliWaypointArrived = false;
                    if (heliWaypoint != null)
                    {
                        helicopterPositionHold = false;
                        speed = Math.Min(FlightPolicy.Clamp(Num(a, "speed", 15), 1, 20), HeliLearned("forward", 20));
                        ChatLog.Write("heli", "waypoint " + heliWaypoint.Name + "; slow inside 200 m, then hover");
                    }
                    lastHeliHeading = FlightGlobals.ship_heading; sideIntegral = 0; rotorYawBias = FlightPolicy.Clamp(savedYawBias, -8, 8); nextRotorTrim = Time.realtimeSinceStartup + 8;
                    if (helicopterVerticalOnly) SetGroup(vessel, KSPActionGroup.SAS, true);
                    return StartRotorMode("helicopter");
                case "set_heading":
                {
                    double rel = Num(a, "relative", double.NaN);
                    heading = double.IsNaN(rel) ? Num(a, "heading", FlightGlobals.ship_heading) : (FlightGlobals.ship_heading + rel + 360) % 360;
                    if (!Active) { var h = new Dictionary<string, object> { { "heading", heading }, { "altitude_m", -1.0 }, { "speed", -1.0 } }; string r0 = Command("plane_hold", h); if (!Active) return r0; }
                    holdHeading = true; directBank = null; ApFieldSync.Heading(heading);
                    if (learn != null && !learn.Done) learn.UserHold(double.NaN, heading, double.NaN);   // Oct 12: chat heading wins over Learn wait / fly-home
                    return (double.IsNaN(rel) ? "Heading " : "Turning " + rel.ToString("0", CultureInfo.InvariantCulture) + " deg to heading ") + heading.ToString("000", CultureInfo.InvariantCulture) + ".";
                }
                case "set_speed":
                {
                    if (ApproachSpeedLocked) return "On approach, speed locked at " + Math.Round(runway.DesiredSpeed) + " m/s (approach speed). Say \"go around\" first to change it.";
                    object req; if (!a.TryGetValue("speed", out req) && !a.TryGetValue("value", out req) && !a.TryGetValue("speed_ms", out req) && !a.TryGetValue("target", out req)) req = Bool(a, "max", false) ? "max" : null;   // live 15:29: model sent {"value":2000}
                    if ("last".Equals(req)) { if (double.IsNaN(lastAskedSpeed)) return "No earlier speed to override; say e.g. set speed 400 override."; req = lastAskedSpeed; }
                    else { double asked; if (req != null && double.TryParse(Convert.ToString(req, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out asked)) lastAskedSpeed = asked; }
                    string reply; bool lifted;
                    double t = PilotPolicy.ResolveSpeed(req, EstimateVMax(), vessel.altitude, Bool(a, "override", false) || PlayerIntent.OverrideRecent(PilotEvents.Now), out lifted, out reply);
                    if (double.IsNaN(t)) return reply;
                    if (!Active) return "Engage local holds first. (" + reply + ")";
                    speed = t; capLifted = lifted; holdSpeed = true; return reply;
                }
                default: return ResidualCommand(name, a);
            }
        }
        /// <summary>Gear sabotage (autopilot on only): gear moved against the autopilot's intent -> callout, fumble 2-4 s, fix.</summary>
        void GearWatch(double now)
        {
            bool actual = vessel.ActionGroups[KSPActionGroup.Gear];
            bool? intent = GearPolicy.Intent(mode, runway == null ? "" : runway.Phase, vessel.radarAltitude);
            bool said = gearSaid.HasValue && gearSaid.Value == actual;
            bool bad = GearPolicy.Violation(intent, actual, said, ApGearCmd);
            if (!bad && !said && intent.HasValue && intent.Value != actual && ApGearCmd == actual) { SetGroup(vessel, KSPActionGroup.Gear, intent.Value); gearGate.Reset(); ChatLog.Write("ap", "gear " + (intent.Value ? "down" : "up") + " (autopilot, " + (runway != null ? runway.Phase : mode) + ")"); return; }   // GROK LAND5g
            bool due = gearGate.Tick(bad, now);
            if (gearGate.JustNoticed)
            {
                string al = "[SYSTEM] WARNING: landing gear " + (actual ? "lowered" : "raised") + " against the autopilot - pilot fixing";
                ChatWindow.Notice(al); ChatLog.Write("alert", al); PilotEvents.Add("landing gear was " + (actual ? "lowered" : "raised") + " without orders; pilot is fixing it", PilotEvents.Now);
                CrewEmergency("tamper", "landing gear");
            }
            if (due) { SetGroup(vessel, KSPActionGroup.Gear, intent.Value); gearGate.Reset(); ChatLog.Write("ap", "gear restored " + (intent.Value ? "down" : "up")); }
        }

        /// <summary>Vehicle max level speed estimate (maxspeed.py port): measured drag area + available thrust.</summary>
        double Twr()
        {
            double t = 0; foreach (Part pt in vessel.parts) foreach (PartModule m in pt.Modules) { var e = m as ModuleEngines; if (e != null && e.EngineIgnited) t += e.maxThrust; }
            double w = vessel.GetTotalMass() * 9.81; return w > 0 ? t / w : 1;
        }
        double EstimateVMax()
        {
            if (vessel == null || vessel.LandedOrSplashed) return double.NaN;
            double drag = 0, thrust = 0;
            foreach (Part part in vessel.parts)
            {
                drag += part.dragScalar;
                foreach (var e in part.FindModulesImplementing<ModuleEngines>())
                    if (e.isOperational && e.EngineIgnited) thrust += e.finalThrust / Math.Max(.1, e.currentThrottle);
            }
            return PilotPolicy.VMax(drag, vessel.dynamicPressurekPa, vessel.atmDensity, thrust, vessel.srfSpeed);
        }
        double toClimbVs = double.NaN, toHeading = double.NaN, climbVsCap = double.NaN;   // GROK TO2
        string StartTakeoff()
        {
            toClimbVs = toHeading = double.NaN; takeoffFlaps = true;   // Oct 12: partial flaps for the roll (no-op without tagged flaps)
            if (props.HasLift(vessel)) { Stop(); return "Use helicopter hover for a rotorcraft takeoff."; }
            bool wheels = false, wings = false;
            foreach (Part part in vessel.parts)
            {
                wheels |= part.FindModuleImplementing<ModuleWheelBase>() != null;
                wings |= part.FindModuleImplementing<ModuleLiftingSurface>() != null;
            }
            if (!wheels || !wings) { Stop(); return "Aircraft takeoff requires wheels and wings."; }
            engines.Cancel(); NativeEngines.Takeoff(vessel);
            parkingReleased = true; SetGroup(vessel, KSPActionGroup.Brakes, false);   // release the parking brake at roll start
            holdAltitude = holdHeading = holdSpeed = true; directVs = directPitch = directBank = null;
            vessel.ctrlState.pitchTrim = 0; SyncTrim();   // no leftover nose-up trim on the roll
            takeoff = new TakeoffMission(Planetarium.GetUniversalTime()); tground = new TakeoffGround(vessel.latitude, vessel.longitude, FlightGlobals.ship_heading); tgHdgPrev = double.NaN; return StartRotorMode("takeoff");
        }
        void OnDestroy() { GameEvents.onPartDie.Remove(OnPartDie); Stop(); WingRelease(null); if (vessel != null) vessel.OnFlyByWire -= Fly; if (instance == this) instance = null; }
    }
}
