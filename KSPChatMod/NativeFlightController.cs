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
        internal static string ApLine { get { if (instance == null) return MfdNav.ApStatus("idle", false, false); var r = instance.runway; bool fin = instance.mode == "landing" && r != null && r.Phase == "final"; return MfdNav.ApStatus(instance.mode, fin && r.LocCoupled, fin && r.GsCoupled); } }
        internal static bool Busy { get { return Active || (instance != null && instance.plan != null && instance.plan.Running); } }
        internal static bool PlanRunning { get { return instance != null && instance.plan != null && instance.plan.Running; } }
        internal static string Phase { get { return instance == null ? "idle" : instance.mode; } }
        internal static void SetGroup(Vessel target, KSPActionGroup group, bool value)
        {
            target.ActionGroups.SetGroup(group, value);
            if (instance != null && instance.vessel == target && instance.recovery != null)
                instance.recovery.AcceptGroup(target, group, value);
        }
        readonly ControlLease lease = new ControlLease();
        readonly Dictionary<ModuleControlSurface, SurfaceState> originals = new Dictionary<ModuleControlSurface, SurfaceState>();
        readonly Dictionary<string, bool> auto = new Dictionary<string, bool> { { "master", true }, { "pitch", true }, { "roll", true }, { "yaw", true }, { "rotor", true } };
        Vessel vessel;
        string mode = "idle";
        double altitude, heading, speed = 150, band = 150, throttle, lastThrottle = -10, prevPitch, prevRoll, pitchIntegral, vsIntegral;
        double? capture;
        double terrainFloor = double.NaN, terrainAheadM = double.NaN, terrainMargin = 300;
        float nextTerrain;
        float nextTrim, watchTrimUntil;
        double trimBaselineVs, trimBaselinePitch;
        bool trimSuspended, parkingSet, parkingReleased, wasAirborne, prevBrakes;
        int partCount;
        string settingsPath;
        Dictionary<string, object> settingsData = new Dictionary<string, object>();
        bool settingsWritable = true;
        NativeSpots spots;
        NativeCraftNotes craftNotes;
        float lastCapCut = -10; bool landingGearDown; int goArounds, lastAlarmSeq = -1; double bankCmd, bankPrevWant; float gTraceUntil, gTraceNext; readonly PitchShaper pitchShape = new PitchShaper(); string shapedMode = ""; double landMass0, landStall0; string loggedSmooth = "";
        readonly AlertGate alertGate = new AlertGate();
        string loggedRoute = ""; double lastDesiredPitch; double lastAskedSpeed = double.NaN; readonly RecoveryGate gearGate = new RecoveryGate(); bool? gearSaid;
        double planBank, prevSpd = -1; bool capLifted;
        double climbPitchMax = 15, descentPitchMin = -5;
        double? directVs, directPitch, directBank, pendingCircle; bool bankOverride; string loggedMode = ""; float nextTelemetry;
        float elevator;
        NativePropulsion props;
        NativePower power;
        NativeRecovery recovery;
        NativeReversers reversers;
        NativeFlaps flaps;
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
        RotorPark rotorPark;
        float nextRotorPark;
        float nextRotorTrim;
        bool holdAltitude = true, holdHeading = true, holdSpeed = true;
        TakeoffMission takeoff; TakeoffGround tground; readonly SpeedHold shold = new SpeedHold(); string shapedSpeedMode = ""; bool landFloor, lowEnergy; float lowTraceNext;
        StallLearner learner; double stallGuess = 45, liftArea; float nextStallSample, nextStallSave;
        void ConfigureInertia()
        {
            double thrust = 0, spool = 0; int n = 0;
            foreach (Part ep in vessel.parts) foreach (PartModule pm in ep.Modules) { var en = pm as ModuleEngines; if (en == null) continue; thrust += en.maxThrust; n++; if (en.useEngineResponseTime && en.engineAccelerationSpeed > 0) spool = Math.Max(spool, 1 / en.engineAccelerationSpeed); }
            double twr = n == 0 ? double.NaN : thrust / (vessel.totalMass * 9.81);
            shold.Configure(vessel.totalMass, twr, spool);
            { object lo; var lc = settingsData.TryGetValue("throttle_lag", out lo) ? lo as Dictionary<string, object> : null; double ml = lc == null ? double.NaN : Num(lc, vessel.vesselName, double.NaN); if (!double.IsNaN(ml)) shold.SetLag(ml); lagMeasured = !double.IsNaN(ml); }
            lagMeter = new SpeedHold.LagMeter();
            ChatLog.Write("vessel", string.Format(System.Globalization.CultureInfo.InvariantCulture, "inertia modifier {0:0.00} (m={1:0.0} t, TWR={2:0.00}, spool={3:0.0} s): throttle gains /{0:0.00}, lag {6:0.0} s {7}, decel {4:0.00} m/s2 {5}",
                shold.K, vessel.totalMass, twr, spool, double.IsNaN(Decel.Estimate(vessel.vesselName, 150, 100, true)) ? ApproachProfile.MassDecel(vessel.totalMass) : Decel.Estimate(vessel.vesselName, 150, 100, true), double.IsNaN(Decel.Estimate(vessel.vesselName, 150, 100, true)) ? "(mass estimate)" : "(measured)", shold.Lag, lagMeasured ? "(measured)" : "(estimate)"));
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
            c.pitch = c.yaw = c.roll = c.wheelSteer = 0; elevator = 0; pitchIntegral = vsIntegral = 0;
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
            if (air) { c.mainThrottle = vessel.ctrlState.mainThrottle = Math.Max(c.mainThrottle, .6f); SetGroup(vessel, KSPActionGroup.SAS, true); }
            Stop(); ChatWindow.Notice("Local controller released after error (" + ex.GetType().Name + " in " + where + "): controls centred" + (air ? ", power 60%+, SAS on." : "."));
        }
        double LearnedStall() { if (learner == null || vessel == null) return stallGuess; double s0 = StallLearner.Effective(stallGuess, learner.MeasuredStall(vessel.GetTotalMass() * 1000, liftArea), learner.Confidence); if (double.IsNaN(s0) || s0 <= 0) s0 = stallGuess; s0 = CraftClass.ConservativeStall(s0, liftoffSpeed); if (vessel != null && CraftClass.Gentle(CraftCls)) s0 = Math.Max(s0, ApproachProfile.HeavyStallFloor(vessel.totalMass)); return FlightPolicy.Clamp(s0, 20, 200); }
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
            return "Craft class: " + CraftClass.Label(CraftCls, craftOverride) + (CraftClass.Gentle(CraftCls) ? " - max 30 deg bank, 6 deg/s roll, gentle pitch." : " - full g rating.");
        }
        RunwayMission runway;
        bool landingAfterTakeoff;
        NativeVerticalLanding verticalLanding;
        TaxiMission taxi; readonly Derotation derot = new Derotation();
        bool poweredTaxi;
        double stall = 45, liftoffSpeed = double.NaN; float trimHandoffAt = -1000;
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
            { s.deployAngle = Angle; s.authorityLimiter = Authority; s.deploy = Deploy; s.deployInvert = Invert; s.partDeployInvert = PartInvert; s.ignorePitch = Pitch; s.ignoreRoll = Roll; s.ignoreYaw = Yaw; }
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
            vessel = active; originals.Clear(); parkingSet = parkingReleased = false;
            if (vessel == null) return;
            ChatLog.Write("vessel", "switched to " + vessel.vesselName + " (" + vessel.GetTotalMass().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " t, " + vessel.parts.Count + " parts)");
            vessel.OnFlyByWire += Fly;
            props = new NativePropulsion(vessel);
            power = new NativePower(vessel);
            recovery = new NativeRecovery(vessel); wasNative = false;
            terrainFloor = double.NaN; nextTerrain = 0;
            reversers = new NativeReversers(vessel);
            flaps = new NativeFlaps(vessel);
            object cached; var stallCache = settingsData.TryGetValue("stall_speeds", out cached) ? cached as Dictionary<string, object> : null;
            double liftSum = 0; foreach (Part lp in vessel.parts) foreach (PartModule lm in lp.Modules) { var ls = lm as ModuleLiftingSurface; if (ls != null) liftSum += ls.deflectionLiftCoeff; }
            double estStall = ApproachProfile.EstimateStall(vessel.totalMass * 1000, liftSum);
            ApproachProfile.JoinG = FlightPolicy.Clamp(Num(settingsData, "approach_join_g", 4.0), 1.1, ApproachProfile.GCap); ApproachProfile.CrewG = FlightPolicy.Clamp(Num(settingsData, "crew_g_limit", 6.0), 1.5, ApproachProfile.GCap);
            { var tol = new List<double>(); foreach (Part tp in vessel.parts) tol.Add(tp.gTolerance); ApproachProfile.StructG = ApproachProfile.StructuralG(tol); ApproachProfile.Backoff = 1; } ApproachProfile.FinalG = FlightPolicy.Clamp(Num(settingsData, "approach_final_g", 1.5), 1.05, ApproachProfile.GCap);
            try { stall = stallCache == null ? estStall : Math.Max(estStall, FlightPolicy.Clamp(Num(stallCache, vessel.vesselName, estStall), 20, 200)); }   // a stale low cache never undercuts the estimate
            catch (Exception) { stall = 45; ChatWindow.Notice("Invalid saved stall speed ignored for this craft."); }
            { object lo; var lc = settingsData.TryGetValue("liftoff_speeds", out lo) ? lo as Dictionary<string, object> : null; liftoffSpeed = lc == null ? double.NaN : Num(lc, vessel.vesselName, double.NaN); }
            partCount = vessel.parts.Count;
            stallGuess = stall; liftArea = liftSum;
            { object so; var sc = settingsData.TryGetValue("stall_learn", out so) ? so as Dictionary<string, object> : null; object lo2 = null; if (sc != null) sc.TryGetValue(vessel.vesselName, out lo2); learner = StallLearner.Load(lo2 as Dictionary<string, object>); }
            ChatLog.Write("vessel", string.Format(System.Globalization.CultureInfo.InvariantCulture, "stall inputs: m={0:0.0} t, liftCoeff={1:0.00} -> S={2:0.0} m2, rho={3}, CLmax={4}, Vs est={5:0.0} m/s, heavy floor={6:0}, liftoff={7:0}",
                vessel.totalMass, liftSum, liftSum * ApproachProfile.AreaPerCoeff, ApproachProfile.RhoSea, ApproachProfile.ClMaxConservative, estStall, ApproachProfile.HeavyStallFloor(vessel.totalMass), liftoffSpeed));
            ConfigureInertia(); LoadProfile();
            stall = LearnedStall(); ChatLog.Write("vessel", "stall " + StallLearner.Label(stallGuess, learner.MeasuredStall(vessel.GetTotalMass() * 1000, liftArea), learner.Confidence));
            { double lo = 0, hi = 0; Vector3 rt = vessel.ReferenceTransform.right; foreach (Part sp in vessel.parts) { double x = Vector3.Dot(sp.transform.position - vessel.CoM, rt); lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
              craftAuto = CraftClass.Classify(vessel.GetTotalMass(), hi - lo, vessel.parts.Count); craftOverride = Str(settingsData, "craft_class", "auto");
              ApproachProfile.JoinG = CraftClass.JoinG(CraftCls, ApproachProfile.JoinG); ApproachProfile.FinalG = CraftClass.JoinG(CraftCls, ApproachProfile.FinalG);
              ChatLog.Write("vessel", "class " + CraftClass.Label(CraftCls, craftOverride) + " span " + (hi - lo).ToString("0") + " m"); }
            foreach (Part p in vessel.parts) foreach (PartModule m in p.Modules)
            { var s = m as ModuleControlSurface; if (s != null) originals[s] = new SurfaceState(s); }
        }
        /// <summary>Brakes AG (stock airbrakes; wheel brakes in the air are harmless) set by the approach speed logic, not tampering.</summary>
        internal static bool AirbrakesByAutopilot;
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
                flaps = new NativeFlaps(vessel);
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
            if (manual && Busy) { Stop(); ChatWindow.Notice("Local autopilot released: manual input; plan paused."); }
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
                bool revert = NativeSafety.ShouldRevert(Active, flying);
                int restored = recovery.Tick(vessel, now, revert);
                restored += reversers.Recover(vessel, now, revert);
                var noticed = new List<string>(recovery.Noticed); noticed.AddRange(reversers.Noticed);
                if (Active && flying && TimeWarp.WarpMode == TimeWarp.Modes.LOW && vessel.mainBody.atmosphere && vessel.altitude < vessel.mainBody.atmosphereDepth)
                { int capI = PilotPolicy.WarpIndexCap(true, TimeWarp.CurrentRateIndex, TimeWarp.fetch != null ? TimeWarp.fetch.physicsWarpRates : null); if (capI < TimeWarp.CurrentRateIndex) { TimeWarp.SetRate(capI, true); ChatLog.Write("ap", "physics warp capped at 3x in atmosphere"); } }
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
            bool wheels = false;
            if (!flying) foreach (Part p in vessel.parts) if (p.FindModuleImplementing<ModuleWheelBase>() != null) { wheels = true; break; }
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
        double Track() { return vessel.srfSpeed < 5 ? double.NaN : FlightGlobals.ship_heading; }   // nose heading ~ track (no wind in KSP)
        double Pitch() { return Math.Asin(FlightPolicy.Clamp(Vector3d.Dot(vessel.ReferenceTransform.up, vessel.upAxis), -1, 1)) * 180 / Math.PI; }
        double Roll() { return -Math.Atan2(Vector3d.Dot(vessel.ReferenceTransform.right, vessel.upAxis), Vector3d.Dot(-vessel.ReferenceTransform.forward, vessel.upAxis)) * 180 / Math.PI; }
        void BeginHold()
        {
            var savedTrim = craftNotes.Load(vessel.vesselName);
            foreach (string key in new[] { "pitch_trim", "roll_trim", "yaw_trim", "pitch_deploy_bias" }) Num(savedTrim, key, 0);
            Stop(false);
            if (!lease.Acquire(NativeIds.Vessel(vessel.id), "native")) throw new InvalidOperationException("Another controller owns this vessel");
            mode = "hold"; throttle = vessel.ctrlState.mainThrottle; prevPitch = Pitch(); prevRoll = Roll();
            elevator = vessel.ctrlState.pitch; lastThrottle = Planetarium.GetUniversalTime() - 3;
            pitchIntegral = vsIntegral = 0; lastDesiredPitch = Pitch(); capLifted = false; prevSpd = -1; capture = null; trimSuspended = false;
            SetGroup(vessel, KSPActionGroup.SAS, false);
            ApplyCraftNotes();
            if (!props.HasLift(vessel)) engines.Request(vessel, Time.realtimeSinceStartup);
        }
        void Fly(FlightCtrlState c)
        {
            if (mode == "idle" || vessel == null || vessel != FlightGlobals.ActiveVessel || vessel.packed) return;
            try
            {
                if (mode == "spool") return;
                if (mode == "vertical landing")
                {
                    string previous = verticalLanding.Phase;
                    verticalLanding.Fly(vessel, c, Planetarium.GetUniversalTime(), Math.Max(.001, Time.fixedDeltaTime));
                    if (verticalLanding.Phase != previous) ChatWindow.Notice("Local powered descent: " + verticalLanding.Phase);
                    if (verticalLanding.Phase == "abort")
                    {
                        c.mainThrottle = 0; vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0;
                        Stop(); ChatWindow.Notice("Powered descent aborted: insufficient thrust."); return;
                    }
                    if (verticalLanding.Phase == "landed") { Stop(false); SetGroup(vessel, KSPActionGroup.SAS, true); }
                    return;
                }
                if (mode == "taxi")
                {
                    TaxiHotSwap(); taxi.Step(Planetarium.GetUniversalTime(), vessel.latitude, vessel.longitude, FlightGlobals.ship_heading, vessel.srfSpeed, vessel.mainBody.Radius, vessel.LandedOrSplashed, poweredTaxi, Twr());
                    c.wheelSteer = (float)taxi.Wheel; c.wheelThrottle = (float)taxi.Drive; c.yaw = (float)taxi.Yaw;
                    c.mainThrottle = vessel.LandedOrSplashed ? (float)taxi.Throttle : Math.Max(.05f, c.mainThrottle);
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
                    if (tground != null && tground.Released && tground.Since(nowT) < 5 && Time.realtimeSinceStartup >= gTraceNext)
                    { gTraceNext = Time.realtimeSinceStartup + .25f; ChatLog.Write("g", "release+" + tground.Since(nowT).ToString("0.00") + "s spd=" + vessel.srfSpeed.ToString("0.0") + " hdg=" + FlightGlobals.ship_heading.ToString("0.0") + " pit=" + pitch.ToString("0.0") + " q=" + q.ToString("0.0") + " thr=" + c.mainThrottle.ToString("0.00") + " wheel=" + tground.Wheel.ToString("0.00") + " yaw=" + tground.Yaw.ToString("0.00") + " elev=" + c.pitch.ToString("0.00") + " g=" + vessel.geeForce.ToString("0.0")); }
                    directPitch = takeoff.Step(Planetarium.GetUniversalTime(), vessel.srfSpeed, vessel.radarAltitude, vessel.LandedOrSplashed, stall, liftoffSpeed);
                    if (takeoff.Phase == "takeoff timeout") { c.mainThrottle = 0; SetGroup(vessel, KSPActionGroup.Brakes, true); Stop(); ChatWindow.Notice("Takeoff timed out on the ground."); return; }
                    if (takeoff.Phase == "climbout complete")
                    {
                        mode = landingAfterTakeoff ? "landing" : "hold";
                        landingAfterTakeoff = false; directPitch = null; directBank = pendingCircle;
                        throttle = PilotPolicy.HandoffThrottle(throttle); speed = FlightPolicy.Clamp(1.8 * stall, 80, 200);   // never hand off at full power
                        c.pitchTrim = vessel.ctrlState.pitchTrim = 0; trimHandoffAt = Time.realtimeSinceStartup; trimSuspended = false;   // trim starts clean after takeoff
                        if (!double.IsNaN(takeoff.LiftoffSpeed)) { RememberLiftoff(takeoff.LiftoffSpeed); ChatLog.Write("takeoff", "liftoff at " + takeoff.LiftoffSpeed.ToString("0") + " m/s (Vr " + takeoff.Vr.ToString("0") + ", stall est " + stall.ToString("0") + ")"); }
                        if (pendingCircle.HasValue) ChatWindow.Notice("Climbing out; circling now at " + Math.Abs(pendingCircle.Value).ToString("0") + " deg bank.");
                        pendingCircle = null;
                    }
                    else
                    {
                        directBank = 0; speed = 200;
                        parkingReleased = true;
                        if (vessel.ActionGroups[KSPActionGroup.Brakes]) SetGroup(vessel, KSPActionGroup.Brakes, false);   // live bug: brakes were held until the 5%-stepped throttle reached 60%
                        if (vessel.radarAltitude > 25) SetGroup(vessel, KSPActionGroup.Gear, false);
                        if (tground != null && vessel.LandedOrSplashed)
                        {   // gentle ground roll: rate-limited, speed-scaled steering + rudder toward the takeoff line
                            double err = tground.HeadingError(FlightGlobals.ship_heading, tground.CrossTrack(vessel.latitude, vessel.longitude, vessel.mainBody.Radius));
                            tground.Steer(err, vessel.srfSpeed, dt); c.wheelSteer = (float)tground.Wheel; c.yaw = (float)tground.Yaw;
                        }
                        else c.wheelSteer = (float)FlightPolicy.WheelSteering(heading - FlightGlobals.ship_heading, .5);
                    }
                }
                if (mode == "landing" || mode == "takeoff")
                    flaps.Tick(vessel, recovery, Time.realtimeSinceStartup, vessel.srfSpeed, stall, mode == "landing" ? 30 : 5);
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
                    if (landMass0 > 0) { runway.DecelA = Decel.Estimate(vessel.vesselName, ApproachProfile.HiSpeed(stall), ApproachProfile.AppSpeed(stall), true); if (double.IsNaN(runway.DecelA)) runway.DecelA = ApproachProfile.MassDecel(vessel.totalMass); runway.DecelA /= Math.Sqrt(shold.K); }   // heavies start slowing earlier (reaction time)   // measured decel (NaN = conservative default)
                    runway.Step(vessel.latitude, vessel.longitude, vessel.altitude, vessel.radarAltitude, vessel.srfSpeed, vessel.LandedOrSplashed, vessel.mainBody.Radius, ApproachProfile.StallAt(landStall0, landMass0, vessel.totalMass), Track(), FlightGlobals.ship_heading);
                    if (runway.Phase == "go around" && goArounds < 3) { goArounds++; CrewEmergency("landing", runway.Why.Length > 0 ? runway.Why : "missed touchdown"); runway.GoAroundReset(); ChatWindow.Notice("Local autoland: going around - " + (runway.Why.Length > 0 ? runway.Why : "missed touchdown") + "; re-entering the approach (" + goArounds + "/3)."); ChatLog.Write("approach", "go around " + goArounds + ": " + runway.Why); runway.Why = ""; directPitch = null; directVs = null; }   // round 3: keep landing
                if (runway.Phase == "go around") { altitude = vessel.altitude + 500; speed = 1.5 * stall; mode = "hold"; directPitch = null; directVs = null; SetGroup(vessel, KSPActionGroup.Brakes, false); AirbrakesByAutopilot = false; string gw = runway.Why.Length > 0 ? runway.Why : "missed touchdown"; CrewEmergency("landing", "3 go-arounds used: " + gw); ChatWindow.Notice("Local autoland: 3 go-arounds used (last: " + gw + "). Holding " + Math.Round(altitude) + " m, heading " + Math.Round(heading) + ". Say \"land\" to try again or take control."); ChatLog.Write("approach", "go-around limit reached, holding: " + gw); }
                    else if (runway.Phase == "stopped") { c.mainThrottle = 0; Stop(false); return; }
                    else
                    {
                        heading = runway.DesiredHeading; altitude = runway.DesiredAltitude; speed = runway.DesiredSpeed; directVs = runway.DesiredVs;
                        if (runway.RouteLog != loggedRoute) { loggedRoute = runway.RouteLog; ChatLog.Write("approach", "chart " + runway.Kind + " " + loggedRoute); }
                    if (runway.SmoothLog != loggedSmooth) { if (runway.JoinLog.Length > 0) ChatLog.Write("approach", runway.JoinLog); loggedSmooth = runway.SmoothLog; if (loggedSmooth.Length > 0) ChatLog.Write("approach", "plane " + Math.Round(runway.PlanSpeed) + " m/s " + runway.PlanG.ToString("0.0") + " g bank " + Math.Round(runway.PlanBank) + " r=" + Math.Round(runway.PlanRadius) + " m: " + loggedSmooth); }
                        { bool airPhase = runway.Phase == "entry" || runway.Phase == "intercept" || runway.Phase == "final", on = vessel.ActionGroups[KSPActionGroup.Brakes]; bool want = !vessel.LandedOrSplashed && vessel.altitude - runway.Elevation > 30 && SpeedHold.Airbrakes(on, vessel.srfSpeed, runway.DesiredSpeed); if (airPhase && want != on) { SetGroup(vessel, KSPActionGroup.Brakes, want); AirbrakesByAutopilot = want; } }   // airbrakes/spoilers (Brakes group) when fast, +8/+2 m/s hysteresis
                        runway.Heavy = CraftClass.Gentle(CraftCls);
                        double vsBefore = directVs.Value; directVs = PilotPolicy.ApproachFloorVs(runway.Phase, runway.Distance, vessel.radarAltitude, vessel.altitude, terrainFloor, directVs.Value, vessel.verticalSpeed, runway.Heavy);
                        landFloor = directVs.Value > vsBefore + .5 || (runway.Phase == "final" && RunwayMission.BelowGsM(runway.Ils) > RunwayMission.GsMargin && runway.Distance > ApproachChart.ShortFix && vessel.verticalSpeed < -3);
                        if (landFloor && Time.realtimeSinceStartup >= gTraceNext) { gTraceNext = Time.realtimeSinceStartup + 1; ChatLog.Write("g", "approach floor: agl=" + vessel.radarAltitude.ToString("0") + " vs=" + vessel.verticalSpeed.ToString("0") + " belowGS=" + (double.IsNaN(RunwayMission.BelowGsM(runway.Ils)) ? "n/a" : RunwayMission.BelowGsM(runway.Ils).ToString("0")) + " spd=" + vessel.srfSpeed.ToString("0") + " pit=" + pitch.ToString("0")); }   // AGL floor: never sink into a hill on approach
                        if (runway.Phase == "entry" || runway.Phase == "intercept") landingGearDown = false; else if (runway.Gear && !landingGearDown) { SetGroup(vessel, KSPActionGroup.Gear, true); landingGearDown = true; ChatLog.Write("approach", "gear down on final"); }   // never fight the player on the outbound leg
                        if (TouchAndGoNow(runway.Phase)) return;
                        if (runway.Phase == "rollout")
                        {
                            c.mainThrottle = 0;
                            c.mainThrottle = (float)reverseRollout.Tick(vessel.LandedOrSplashed, vessel.srfSpeed, Planetarium.GetUniversalTime(), reverse => (!reverse || Bool(settingsData, "autoland_reversers", true)) && reversers.Set(vessel, reverse));
                            if (!vessel.LandedOrSplashed) c.mainThrottle = Math.Max(.05f, c.mainThrottle);
                            derot.Step(pitch, dt, NoseGearDown()); c.pitch = (float)derot.Elevator(pitch, q);   // derotation: hold attitude, lower nose at 2.5 deg/s, no forward spike
                            c.roll = (float)FlightPolicy.Clamp(-.02 * roll - .006 * p, -1, 1);
                            c.wheelSteer = (float)FlightPolicy.WheelSteering(heading - FlightGlobals.ship_heading, .4);
                            SetGroup(vessel, KSPActionGroup.Brakes, derot.BrakesAllowed && (vessel.srfSpeed < 25 || ((int)(Planetarium.GetUniversalTime() * 2) % 2 == 0)));   // wheel brakes only once the nose wheel is down
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
                    if (emergency != "normal") { vs = -3; forwardTarget = vessel.radarAltitude < 12 ? 0 : 30; rightTarget = 0; }
                    double pitchTarget = props.Compound ? 0 : FlightPolicy.Clamp(-1.5 * (forwardTarget - fwd), -12, 12);
                    double rollTarget = FlightPolicy.Clamp(1.5 * (rightTarget - side), -12, 12);
                    double yawRate = FlightPolicy.Wrap(FlightGlobals.ship_heading - lastHeliHeading) / dt; lastHeliHeading = FlightGlobals.ship_heading;
                    double yawDemand = vessel.LandedOrSplashed ? 0 : helicopterYaw.Step(FlightPolicy.Wrap(heading - FlightGlobals.ship_heading), yawRate, dt);
                    if (helicopterYaw.Observe(Time.realtimeSinceStartup, yawDemand, yawRate)) ChatWindow.Notice("Helicopter yaw actuator sign corrected from measured response.");
                    if (!helicopterVerticalOnly)
                    {
                        c.pitch = (float)FlightPolicy.Clamp(.022 * (pitchTarget - pitch) - .012 * q, -1, 1);
                        c.roll = (float)FlightPolicy.Clamp(.014 * (rollTarget - roll) - .01 * p, -1, 1);
                        c.yaw = (float)yawDemand;
                    }
                    double collectiveOutput = collective.Step(vs, vessel.verticalSpeed, dt, vessel.LandedOrSplashed);
                    if (emergency == "autorotation" && liftKnown) collectiveOutput = HelicopterPolicy.Autorotation(vessel.radarAltitude, vessel.verticalSpeed, liftRpm, liftLimit);
                    props.Collective((float)collectiveOutput, "lift");
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
                double targetVs = directVs ?? FlightPolicy.VerticalSpeed(vessel.altitude, mode == "hold" ? PilotPolicy.EffectiveAltitude(altitude, terrainFloor) : altitude, 25, 15, kin, band, ref capture);   // round 3: floor as target, not a fighting VS override (porpoise)
                if (!directVs.HasValue) targetVs *= CraftClass.VsScale(CraftCls);   // heavy: gentle altitude corrections
                if (mode == "hold" && !double.IsNaN(terrainFloor) && vessel.altitude < terrainFloor)
                    targetVs = Math.Max(targetVs, FlightPolicy.Clamp((terrainFloor - vessel.altitude) * .08, 3, 25));
                bool landBank = mode == "landing" && runway != null && runway.TurnBank > 0;   // G-rated approach turns (and explicit "bank 60")   // "bank 60, land nearest"
                double blim = CraftClass.MaxBank(CraftCls, landBank ? runway.TurnBank : FlightPolicy.BankLimit(vessel.srfSpeed));
                double arcFf = mode == "landing" && runway != null && runway.Phase == "entry" ? runway.ArcBank : 0;   // curvature feed-forward; heading error is the trim
                double bank = directBank ?? FlightPolicy.Clamp(arcFf + .5 * FlightPolicy.Wrap(heading - FlightGlobals.ship_heading), -blim, blim);
                bank = landBank && !directBank.HasValue ? FlightPolicy.Clamp(bank, -blim, blim) : PilotPolicy.ClampBank(bank, vessel.srfSpeed, bankOverride);
                double decel = prevSpd < 0 || dt <= 0 ? 0 : (prevSpd - vessel.srfSpeed) / dt; prevSpd = vessel.srfSpeed;
                if (!vessel.LandedOrSplashed && !directBank.HasValue)
                {   // turn onset: roll-rate + g-onset ramp (no choppy g at turn entry)
                    double want = bank; bank = CraftClass.RollStep(CraftCls, bankCmd, TurnOnset.Bank(bankCmd, want, dt), dt); bankCmd = bank;
                    if (Math.Abs(want) > 30 && Math.Abs(bankPrevWant) < 10) gTraceUntil = Time.realtimeSinceStartup + 6;
                    bankPrevWant = want;
                    if (Time.realtimeSinceStartup < gTraceUntil && Time.realtimeSinceStartup >= gTraceNext) { gTraceNext = Time.realtimeSinceStartup + .5f; ChatLog.Write("g", "turn entry: want=" + want.ToString("0") + " cmd=" + bankCmd.ToString("0") + " bank=" + Roll().ToString("0") + " n=" + vessel.geeForce.ToString("0.00")); }
                }
                else bankCmd = vessel.LandedOrSplashed ? 0 : bank;
                if (!bankOverride && !vessel.LandedOrSplashed && mode != "takeoff") bank = PilotPolicy.SafeBank(bank, vessel.indicatedAirSpeed, stall, decel);
                bool heavyFloor = !vessel.LandedOrSplashed && (mode == "hold" || (mode == "landing" && runway != null && runway.Phase == "entry")) && CraftClass.FloorRecover(CraftCls, vessel.radarAltitude, vessel.verticalSpeed);
                if (heavyFloor) { bank = 0; bankCmd = CraftClass.RollStep(CraftCls, bankCmd, 0, dt); targetVs = Math.Max(targetVs, 8); if (Time.realtimeSinceStartup >= gTraceNext) { gTraceNext = Time.realtimeSinceStartup + 2; ChatLog.Write("g", "heavy floor recovery: agl=" + vessel.radarAltitude.ToString("0") + " vs=" + vessel.verticalSpeed.ToString("0")); } }   // heavy: wings level + climb early   // no tight turns while slow / bleeding speed
                lowEnergy = !vessel.LandedOrSplashed && mode != "takeoff" && !(mode == "landing" && runway != null && (runway.Phase == "flare" || runway.Phase == "rollout" || runway.Distance < ApproachChart.ShortFix)) && PilotPolicy.LowEnergy(lowEnergy, vessel.srfSpeed, stall, vessel.verticalSpeed, vessel.radarAltitude);
                if (!vessel.LandedOrSplashed && mode != "takeoff" && !(mode == "landing" && runway != null && (runway.Phase == "flare" || runway.Phase == "rollout"))) speed = Math.Max(speed, PilotPolicy.MinSafeSpeed(stall));
                if (lowEnergy)
                {   // hard minimum power + climb; airbrakes in
                    bank = FlightPolicy.Clamp(bank, -15, 15); if (vessel.radarAltitude < 300) targetVs = Math.Max(targetVs, 4);
                    if (vessel.ActionGroups[KSPActionGroup.Brakes] && AirbrakesByAutopilot) { SetGroup(vessel, KSPActionGroup.Brakes, false); AirbrakesByAutopilot = false; }
                    if (Time.realtimeSinceStartup >= lowTraceNext) { lowTraceNext = Time.realtimeSinceStartup + 2; ChatLog.Write("g", "low energy: spd=" + vessel.srfSpeed.ToString("0") + " min=" + PilotPolicy.MinSafeSpeed(stall).ToString("0") + " stall=" + stall.ToString("0") + " vs=" + vessel.verticalSpeed.ToString("0") + " agl=" + vessel.radarAltitude.ToString("0") + " -> power 90%+, climb"); }
                }
                vsIntegral = FlightPolicy.Clamp(vsIntegral + (targetVs - vessel.verticalSpeed) * dt * .25 * kin, -5, 5);
                double kinH = kin * CraftClass.GainScale(CraftCls);
                double desiredPitch = directPitch ?? FlightPolicy.Clamp(1 + .8 * kinH * (targetVs - vessel.verticalSpeed) + vsIntegral + CraftClass.PitchLead(CraftCls, roll), targetVs < -1 ? descentPitchMin : -2, climbPitchMax);
                if ((landFloor || lowEnergy) && !directPitch.HasValue && vessel.radarAltitude < 300) { desiredPitch = Math.Max(desiredPitch, vessel.srfSpeed > 1.3 * stall ? 8 : 3); vsIntegral = Math.Max(0, vsIntegral); pitchIntegral = Math.Max(0, pitchIntegral); }   // sink arrest: nose up unless near the stall (then power does it)
                if (targetVs > 3 && !directPitch.HasValue) desiredPitch = FlightPolicy.Clamp(desiredPitch, Math.Min(5, climbPitchMax), climbPitchMax);
                if (mode != shapedMode || vessel.LandedOrSplashed) { shapedMode = mode; pitchShape.Reset(pitch); pitchIntegral = 0; vsIntegral = 0; }   // every mode change starts from the current state (no stale filter jump)
            ApproachProfile.Backoff = ApproachProfile.StressStep(ApproachProfile.Backoff, vessel.geeForce, ApproachProfile.StructG, dt);
            double gLim = mode == "landing" && runway != null && runway.Phase == "entry" ? Math.Max(3, ApproachProfile.LoadFactor(ApproachProfile.JoinG, vessel.srfSpeed, stall) + .5) : mode == "landing" && runway != null && (runway.Phase == "intercept" || runway.Phase == "final" || runway.Phase == "flare") ? Math.Max(1.2, ApproachProfile.FinalG) : 3;   // gentler once established inbound   // joins maneuver to the dynamic limit; final/flare stay gentle
            if (!(mode == "landing" && runway != null && runway.Phase == "entry") && mode != "takeoff") gLim = Math.Min(gLim, 3);
            gLim = PitchShaper.Limit(gLim, bankCmd); if (CraftClass.Gentle(CraftCls) && mode != "takeoff") gLim = Math.Min(gLim, CraftClass.HeavyPitchG); if (landFloor || lowEnergy || heavyFloor) gLim = Math.Max(gLim, 2);   // sink arrest needs real pitch authority   // airliner: gentle pitch   // high g only in commanded turns/joins; climbs/level 1.8 g pitch rate, 3 g cap
            if (vessel.LandedOrSplashed) lastDesiredPitch = desiredPitch; else { desiredPitch = pitchShape.Step(desiredPitch, pitch, vessel.srfSpeed, gLim, bankCmd, dt); lastDesiredPitch = desiredPitch; }
                if (desiredPitch < pitch - 5 && pitchIntegral > 0) pitchIntegral = 0;   // anti-windup: dump nose-up integral when pushing over
                if (targetVs < vessel.verticalSpeed - 5 && vsIntegral > 0) vsIntegral = 0;
                pitchIntegral = FlightPolicy.Clamp(pitchIntegral + (desiredPitch - pitch) * .04 * dt * kin, -.3, .3);
                double pitchOut = FlightPolicy.Clamp(.022 * kin * (desiredPitch - pitch) - .016 * q + pitchIntegral + PitchShaper.ElevatorFF(bankCmd), -1, 1);   // g feed-forward + more pitch-rate damping
                pitchOut *= PilotPolicy.GScale(vessel.geeForce, Math.Max(gLim, 3));
                float eCmd = (float)PilotPolicy.PitchCommand(pitchOut, roll);
                elevator = Mathf.MoveTowards(elevator, eCmd, (float)(PitchShaper.ElevatorRate(vessel.geeForce, Math.Max(gLim, 3), eCmd, elevator) * dt));
                if (holdAltitude || directVs.HasValue || directPitch.HasValue) c.pitch = elevator;
                if (mode == "takeoff" && vessel.LandedOrSplashed && takeoff != null && !double.IsNaN(TakeoffGround.Elevator(vessel.srfSpeed, TakeoffMission.RotateSpeed(stall, liftoffSpeed)))) { c.pitch = elevator = (float)TakeoffGround.Elevator(vessel.srfSpeed, TakeoffMission.RotateSpeed(stall, liftoffSpeed)); pitchIntegral = vsIntegral = 0; }   // below rotate: elevator slightly nose-down, no integrator wind-up
                if (mode == "hold" && holdAltitude) c.pitchTrim = vessel.ctrlState.pitchTrim = (float)PilotPolicy.TrimBleed(vessel.ctrlState.pitchTrim, elevator, dt);   // level-off beats trim
                if (holdHeading || directBank.HasValue) c.roll = (float)FlightPolicy.Clamp(.014 * (bank - roll) - .01 * p, -1, 1);
                double targetThrottle = throttle;
                if (mode == "hold" && holdAltitude && vessel.altitude > altitude + 30 && vessel.verticalSpeed > 3) targetThrottle -= .05;   // above the target and still climbing: power off
                else if (targetVs > 3) targetThrottle += .05 * Math.Sign(targetVs - vessel.verticalSpeed);
                else if (Math.Abs(speed - vessel.srfSpeed) > 3) targetThrottle += .05 * Math.Sign(speed - vessel.srfSpeed);
                if (vessel.altitude < 6000 && vessel.indicatedAirSpeed > 220) targetThrottle = throttle - .05;
                if (mode == "takeoff" && vessel.indicatedAirSpeed < 200) targetThrottle = 1;
                if (mode == "takeoff" || vessel.LandedOrSplashed || mode != shapedSpeedMode) { shold.Reset(); shapedSpeedMode = mode; }
                if (mode == "takeoff" || vessel.LandedOrSplashed) throttle = FlightPolicy.Throttle(throttle, FlightPolicy.Clamp(targetThrottle, .05, 1), !vessel.LandedOrSplashed, Planetarium.GetUniversalTime(), ref lastThrottle);
                else throttle = shold.Step(ApproachSpeedLocked ? runway.DesiredSpeed : speed, vessel.srfSpeed, dt, throttle);
                if (mode == "landing" && runway != null && directVs.HasValue && !vessel.LandedOrSplashed && (runway.Phase == "final" || runway.Phase == "flare"))
                {   // learned sink map: throttle for the target sink rate (flare: map only; final: blended with the speed hold)
                    double st = SinkThrottle(directVs.Value);
                    if (!double.IsNaN(st)) throttle = runway.Phase == "flare" ? st : .5 * throttle + .5 * st;
                }   // smooth PI on airspeed (pitch flies the vertical speed)
            if ((landFloor && mode == "landing" || lowEnergy) && !vessel.LandedOrSplashed) throttle = Math.Max(throttle, .9);   // arrest the sink with power, not only pitch
            if (mode == "takeoff" && vessel.LandedOrSplashed) throttle = tground == null ? 1 : tground.Throttle(Planetarium.GetUniversalTime());   // ramp 25%/s from brake release (no 0->100% jolt)
            LearnDecel(dt); MeasureLag(dt);
            bool speedLock = ApproachSpeedLocked;
            if (speedLock && !vessel.LandedOrSplashed) { speed = runway.DesiredSpeed; }   // FORCED approach speed: no 3 s/5% stepping, idle when fast, never a climb-first throttle
            { bool cut; double capT = PilotPolicy.SpeedCapThrottle(throttle, vessel.indicatedAirSpeed, vessel.altitude, Time.realtimeSinceStartup - lastCapCut, out cut);
              if (cut && mode != "takeoff" && !capLifted) { throttle = capT; lastCapCut = Time.realtimeSinceStartup; } }
            if (!speedLock && mode != "takeoff" && !vessel.LandedOrSplashed && (runway == null || (runway.Phase != "flare" && runway.Phase != "rollout" && runway.Phase != "stopped")))
                if (mode == "takeoff" || vessel.LandedOrSplashed) throttle = PilotPolicy.ThrottleFloor(throttle, vessel.indicatedAirSpeed, speed, stall); else if (vessel.indicatedAirSpeed < 1.15 * stall) throttle = 1;   // airborne: the PI holds speed; only a real near-stall slams power   // never trade airspeed below the band / stall margin   // Luke: 200 target / 220 cap low down, cut fast when over
                if (holdSpeed) c.mainThrottle = (float)throttle;
            LearnApply(c);
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
            bankOverride = false; pendingCircle = null;
            if (mode == "taxi" && vessel != null)
            {
                vessel.ctrlState.wheelThrottle = vessel.ctrlState.wheelSteer = 0;
                FlightInputHandler.state.wheelThrottle = FlightInputHandler.state.wheelSteer = 0;
                if (vessel.LandedOrSplashed) { vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0; SetGroup(vessel, KSPActionGroup.Brakes, true); }
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
                if (input > 0 && Pitch() > 8) continue;   // anti-windup: no nose-up surface trim while nose-high
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
            if (!NativeCommands.IsPorted(name)) return "Local command not yet ported: " + name;
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
                case "abort":
                    if (plan != null) plan.Pause();
                    bool descending = mode == "vertical landing";
                    Stop(); vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0;
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
                case "get_status": case "autopilot_status": return PilotPolicy.StatusLine(mode, vessel.vesselName, mode == "landing" && runway != null ? runway.Phase : null, mode == "landing" && runway != null ? runway.Distance : double.NaN, vessel.altitude, vessel.srfSpeed, FlightGlobals.ship_heading, plan != null && plan.Running ? plan.Index + 1 : 0);
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
                    speed = Num(a, "speed", Math.Min(200, vessel.srfSpeed)); if (speed < 0) speed = Math.Min(200, vessel.srfSpeed);
                    speed = FlightPolicy.Clamp(speed, 25, 200);
                    double vs = Num(a, "vertical_speed", -999); directVs = vs > -999 ? (double?)FlightPolicy.Clamp(vs, -90, 90) : null;
                    if (off.Contains("vertical_speed")) directVs = null;
                    double rollTarget = Num(a, "roll", -999);
                    directBank = !off.Contains("roll") && rollTarget > -999 ? (double?)FlightPolicy.Clamp(rollTarget, -FlightPolicy.BankLimit(vessel.srfSpeed), FlightPolicy.BankLimit(vessel.srfSpeed)) : null;
                    directPitch = null; BeginHold();
                    if (vessel.LandedOrSplashed) { altitude = Math.Max(altitude, vessel.altitude + 300); return StartTakeoff(); }
                    return "Local aircraft holds engaged.";
                case "takeoff":
                    altitude = vessel.altitude + Math.Max(100, Num(a, "altitude_m", 300)); heading = FlightGlobals.ship_heading;
                    if (!vessel.LandedOrSplashed) return "Already airborne.";
                    BeginHold(); return StartTakeoff();
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
                    if (Str(a, "mode", "H").ToUpperInvariant() == "V") return "Targeted vertical landing is not yet ported; use local powered descent here.";
                    if (Num(a, "touch_and_go", 0) != 0 || Num(a, "final_km", 0) != 0 || Num(a, "approach_heading", -1) != -1)
                        return "Local runway landing does not yet support touch-and-go, final-length or approach-heading overrides.";
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
                            : new RunwayMission { Lat = KscRunway.Lat, Lon = KscRunway.Lon09, EndLat = KscRunway.Lat, EndLon = KscRunway.Lon27, Elevation = 69.1 };
                    }
                    if ((direction == "" && (wantNearest || !savedRunway)) ? PilotPolicy.SwapEnd(NavigationMath.Bearing(selectedRunway.Lat, selectedRunway.Lon, selectedRunway.EndLat, selectedRunway.EndLon), FlightGlobals.ship_heading)
                    : !savedRunway && (island ? direction == "09" : direction == "27"))
                    { double lat = selectedRunway.Lat, lon = selectedRunway.Lon; selectedRunway.Lat = selectedRunway.EndLat; selectedRunway.Lon = selectedRunway.EndLon; selectedRunway.EndLat = lat; selectedRunway.EndLon = lon; }
                    if (NavigationMath.Distance(vessel.latitude, vessel.longitude, selectedRunway.Lat, selectedRunway.Lon, vessel.mainBody.Radius) > 150000) return "Runway is beyond the 150 km approach limit.";
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
                            EnsureCharts(); string why; object end = ChartStore.Read(selectedRunway.Key, out why);
                            if (why.Length > 0) ChatLog.Write("approach", "chart " + selectedRunway.Key + ": " + why + "; computed chart");
                            {
                                if (end != null)
                                {
                                    selectedRunway.Override = ApproachOverride.Parse(end, selectedRunway.Lat, selectedRunway.Lon, selectedRunway.Elevation, vessel.mainBody.Radius, out why, selectedRunway.Terrain);
                                    ChatLog.Write("approach", selectedRunway.Override != null ? "using Luke's chart for " + selectedRunway.Key : "chart " + selectedRunway.Key + " invalid (" + why + "); computed chart");
                                }
                            }
                        }
                        catch (Exception ex) { ChatLog.Write("approach", "chart unreadable (" + ex.Message + "); computed chart"); }
                    }
                    selectedRunway.WantShort = Bool(a, "short_final", false) || destination.ToLowerInvariant().Contains("short") || Str(a, "name", "").ToLowerInvariant().Contains("short");
                goArounds = 0; landMass0 = 0; capLifted = false; lastAskedSpeed = double.NaN; BeginHold(); runway = selectedRunway; reverseRollout = new ReverseRollout(); derot.Reset(); holdAltitude = holdHeading = holdSpeed = true; mode = "landing"; directPitch = directBank = null;
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
                    if (heliMode != "hover" && heliMode != "hold" && heliMode != "fly" && heliMode != "land" && heliMode != "stop") return "Local helicopter mode not yet ported: " + heliMode;
                    if (heliMode == "stop") { Stop(); return "Helicopter control stopped."; }
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
                    lastHeliHeading = FlightGlobals.ship_heading; sideIntegral = 0; rotorYawBias = FlightPolicy.Clamp(savedYawBias, -8, 8); nextRotorTrim = Time.realtimeSinceStartup + 8;
                    if (helicopterVerticalOnly) SetGroup(vessel, KSPActionGroup.SAS, true);
                    return StartRotorMode("helicopter");
                case "set_heading":
                {
                    double rel = Num(a, "relative", double.NaN);
                    heading = double.IsNaN(rel) ? Num(a, "heading", FlightGlobals.ship_heading) : (FlightGlobals.ship_heading + rel + 360) % 360;
                    if (!Active) { var h = new Dictionary<string, object> { { "heading", heading }, { "altitude_m", -1.0 }, { "speed", -1.0 } }; string r0 = Command("plane_hold", h); if (!Active) return r0; }
                    holdHeading = true; directBank = null;
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
            bool bad = GearPolicy.Violation(intent, actual, said);
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
        string StartTakeoff()
        {
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
            takeoff = new TakeoffMission(Planetarium.GetUniversalTime()); tground = new TakeoffGround(vessel.latitude, vessel.longitude, FlightGlobals.ship_heading); return StartRotorMode("takeoff");
        }
        void OnDestroy() { GameEvents.onPartDie.Remove(OnPartDie); Stop(); WingRelease(null); if (vessel != null) vessel.OnFlyByWire -= Fly; if (instance == this) instance = null; }
    }
}
