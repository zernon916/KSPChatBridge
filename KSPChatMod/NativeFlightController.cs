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
        double terrainFloor = double.NaN;
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
        double? directVs, directPitch, directBank;
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
        TakeoffMission takeoff;
        RunwayMission runway;
        bool landingAfterTakeoff;
        NativeVerticalLanding verticalLanding;
        TaxiMission taxi;
        bool poweredTaxi;
        double stall = 45;
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
                string legacyPath = Path.Combine(BridgeLauncher.DataDirectory, "bridge_settings.json");
                var legacy = Num(settingsData, "migration_version", 0) < 1 && File.Exists(legacyPath) ? MiniJson.Deserialize(File.ReadAllText(legacyPath)) : new Dictionary<string, object>();
                settingsData = NativeSettings.Migrate(settingsData, legacy);
                AiSettings.MergeInto(settingsData);
                if (!settingsData.ContainsKey("craft_notes_imported"))
                {
                    string notesPath = Path.Combine(BridgeLauncher.DataDirectory, "craft_notes.json");
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
            vessel.OnFlyByWire += Fly;
            props = new NativePropulsion(vessel);
            power = new NativePower(vessel);
            recovery = new NativeRecovery(vessel); wasNative = false;
            terrainFloor = double.NaN; nextTerrain = 0;
            reversers = new NativeReversers(vessel);
            flaps = new NativeFlaps(vessel);
            object cached; var stallCache = settingsData.TryGetValue("stall_speeds", out cached) ? cached as Dictionary<string, object> : null;
            try { stall = stallCache == null ? 45 : FlightPolicy.Clamp(Num(stallCache, vessel.vesselName, 45), 20, 200); }
            catch (Exception) { stall = 45; ChatWindow.Notice("Invalid saved stall speed ignored for this craft."); }
            partCount = vessel.parts.Count;
            foreach (Part p in vessel.parts) foreach (PartModule m in p.Modules)
            { var s = m as ModuleControlSurface; if (s != null) originals[s] = new SurfaceState(s); }
        }
        internal static bool OwnsControls { get { return NativeSafety.NativeOwns(BridgeLauncher.AiEnabled, BridgeLauncher.NativeReady, BridgeLauncher.NativeChat); } }
        void Update()
        {
            Bind();
            bool nativeMode = OwnsControls;
            // P5-1.7: the in-mod safety tick (power, sabotage revert, parking, engine restart) runs whenever
            // NativeSafety.ShouldRun says so; the local flight tick (plans/hold/spool/trim) only in native mode.
            bool safetyNet = nativeMode || NativeSafety.ShouldRun(BridgeLauncher.AiEnabled, BridgeLauncher.BridgeResponding);
            if (!nativeMode && !safetyNet) { wasNative = false; return; }
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
            SafetyTick(nativeMode, safetyNet);
            if (!nativeMode) return;
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
                    terrainFloor = double.IsNaN(highest) ? double.NaN : highest + 300;
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
            ExtrasTick();
            if (mode == "hold" && auto["master"] && !trimSuspended && Time.realtimeSinceStartup >= nextTrim)
            {
                nextTrim = Time.realtimeSinceStartup + 8;
                if (Math.Abs(vessel.verticalSpeed) < 1 && Math.Abs(Roll()) < 5 && vessel.srfSpeed > 25 && !directPitch.HasValue) NudgeTrim(false);
            }
            if (watchTrimUntil > Time.realtimeSinceStartup && (Math.Abs(vessel.verticalSpeed) > Math.Abs(trimBaselineVs) + 2 || Math.Abs(Pitch() - trimBaselinePitch) > 3))
            { trimSuspended = true; watchTrimUntil = 0; ChatWindow.Notice("Auto-trim paused: flight drifted from level. Reset restores surfaces."); }
        }
        void SafetyTick(bool nativeMode, bool safetyNet)
        {
            float now = Time.realtimeSinceStartup;
            bool flying = !vessel.LandedOrSplashed;
            try { ChatterTick(); } catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] crew chatter: " + ex.Message); }
            try { power.Tick(vessel, now); }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] Local power recovery: " + ex.Message); }
            try { engines.Tick(vessel, now); }
            catch (Exception ex) { ChatWindow.Notice("Local engine restart failed: " + ex.Message); }
            try
            {
                bool revert = NativeSafety.ShouldRevert(!nativeMode && safetyNet, nativeMode && Active, flying);
                int restored = recovery.Tick(vessel, now, revert);
                restored += reversers.Recover(vessel, now, revert);
                if (restored > 0) ChatWindow.Notice("Local pilot: restored configuration on " + restored + " module(s).");
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
            pitchIntegral = vsIntegral = 0; capture = null; trimSuspended = false;
            SetGroup(vessel, KSPActionGroup.SAS, false);
            ApplyCraftNotes();
            if (!props.HasLift(vessel)) engines.Request(vessel, Time.realtimeSinceStartup);
        }
        void Fly(FlightCtrlState c)
        {
            if (!OwnsControls || mode == "idle" || vessel == null || vessel != FlightGlobals.ActiveVessel || vessel.packed) return;
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
                    taxi.Step(Planetarium.GetUniversalTime(), vessel.latitude, vessel.longitude, FlightGlobals.ship_heading, vessel.srfSpeed, vessel.mainBody.Radius, vessel.LandedOrSplashed, poweredTaxi);
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
                if (mode == "takeoff")
                {
                    if (NativeSafety.ReleaseForTakeoff(mode, takeoff.Phase, vessel.LandedOrSplashed, vessel.ActionGroups[KSPActionGroup.Brakes])) { parkingReleased = true; SetGroup(vessel, KSPActionGroup.Brakes, false); }
                    directPitch = takeoff.Step(Planetarium.GetUniversalTime(), vessel.srfSpeed, vessel.radarAltitude, vessel.LandedOrSplashed, stall);
                    if (takeoff.Phase == "takeoff timeout") { c.mainThrottle = 0; SetGroup(vessel, KSPActionGroup.Brakes, true); Stop(); ChatWindow.Notice("Takeoff timed out on the ground."); return; }
                    if (takeoff.Phase == "climbout complete")
                    {
                        mode = landingAfterTakeoff ? "landing" : "hold";
                        landingAfterTakeoff = false; directPitch = directBank = null;
                    }
                    else
                    {
                        directBank = 0; speed = 200;
                        parkingReleased = true;
                        SetGroup(vessel, KSPActionGroup.Brakes, throttle < .6 && vessel.LandedOrSplashed);
                        if (vessel.radarAltitude > 25) SetGroup(vessel, KSPActionGroup.Gear, false);
                        c.wheelSteer = (float)FlightPolicy.WheelSteering(heading - FlightGlobals.ship_heading, .5);
                    }
                }
                if (mode == "landing" || mode == "takeoff")
                    flaps.Tick(vessel, recovery, Time.realtimeSinceStartup, vessel.srfSpeed, stall, mode == "landing" ? 30 : 5);
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
                            stall = stallStudy.Measured.Value;
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
                if (mode == "landing" && (stallStudy == null || stallStudy.Finished))
                {
                    runway.Step(vessel.latitude, vessel.longitude, vessel.altitude, vessel.radarAltitude, vessel.srfSpeed, vessel.LandedOrSplashed, vessel.mainBody.Radius, stall);
                    if (runway.Phase == "go around") { altitude = vessel.altitude + 500; speed = 1.5 * stall; mode = "hold"; directPitch = null; directVs = null; ChatWindow.Notice("Local autoland: going around after missed touchdown."); }
                    else if (runway.Phase == "stopped") { c.mainThrottle = 0; Stop(false); return; }
                    else
                    {
                        heading = runway.DesiredHeading; altitude = runway.DesiredAltitude; speed = runway.DesiredSpeed; directVs = runway.DesiredVs;
                        SetGroup(vessel, KSPActionGroup.Gear, runway.Gear);
                        if (TouchAndGoNow(runway.Phase)) return;
                        if (runway.Phase == "rollout")
                        {
                            c.mainThrottle = 0;
                            c.mainThrottle = (float)reverseRollout.Tick(vessel.LandedOrSplashed, vessel.srfSpeed, Planetarium.GetUniversalTime(), reverse => (!reverse || Bool(settingsData, "autoland_reversers", true)) && reversers.Set(vessel, reverse));
                            if (!vessel.LandedOrSplashed) c.mainThrottle = Math.Max(.05f, c.mainThrottle);
                            c.pitch = (float)FlightPolicy.Clamp(-.04 * q, -.2, .3);
                            c.roll = (float)FlightPolicy.Clamp(-.02 * roll - .006 * p, -1, 1);
                            c.wheelSteer = (float)FlightPolicy.WheelSteering(heading - FlightGlobals.ship_heading, .4);
                            SetGroup(vessel, KSPActionGroup.Brakes, vessel.srfSpeed < 25 || ((int)(Planetarium.GetUniversalTime() * 2) % 2 == 0));
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
                double targetVs = directVs ?? FlightPolicy.VerticalSpeed(vessel.altitude, altitude, 25, 15, kin, band, ref capture);
                if (mode == "hold" && !double.IsNaN(terrainFloor) && vessel.altitude < terrainFloor)
                    targetVs = Math.Max(targetVs, FlightPolicy.Clamp((terrainFloor - vessel.altitude) * .08, 3, 25));
                double bank = directBank ?? FlightPolicy.Clamp(.5 * FlightPolicy.Wrap(heading - FlightGlobals.ship_heading), -FlightPolicy.BankLimit(vessel.srfSpeed), FlightPolicy.BankLimit(vessel.srfSpeed));
                bank = FlightPolicy.Clamp(bank, -FlightPolicy.BankLimit(vessel.srfSpeed), FlightPolicy.BankLimit(vessel.srfSpeed));
                vsIntegral = FlightPolicy.Clamp(vsIntegral + (targetVs - vessel.verticalSpeed) * dt * .25 * kin, -5, 5);
                double desiredPitch = directPitch ?? FlightPolicy.Clamp(1 + .8 * kin * (targetVs - vessel.verticalSpeed) + vsIntegral, targetVs < -1 ? -5 : -2, 15);
                if (targetVs > 3 && !directPitch.HasValue) desiredPitch = FlightPolicy.Clamp(desiredPitch, 5, 15);
                pitchIntegral = FlightPolicy.Clamp(pitchIntegral + (desiredPitch - pitch) * .04 * dt * kin, -.3, .3);
                double pitchOut = FlightPolicy.Clamp(.022 * kin * (desiredPitch - pitch) - .012 * q + pitchIntegral, -1, 1);
                elevator = Mathf.MoveTowards(elevator, (float)pitchOut, (float)(.35 * dt));
                if (holdAltitude || directVs.HasValue || directPitch.HasValue) c.pitch = elevator;
                if (holdHeading || directBank.HasValue) c.roll = (float)FlightPolicy.Clamp(.014 * (bank - roll) - .01 * p, -1, 1);
                double targetThrottle = throttle;
                if (targetVs > 3) targetThrottle += .05 * Math.Sign(targetVs - vessel.verticalSpeed);
                else if (Math.Abs(speed - vessel.srfSpeed) > 3) targetThrottle += .05 * Math.Sign(speed - vessel.srfSpeed);
                if (vessel.altitude < 6000 && vessel.indicatedAirSpeed > 220) targetThrottle = throttle - .05;
                if (mode == "takeoff" && vessel.indicatedAirSpeed < 200) targetThrottle = 1;
                throttle = FlightPolicy.Throttle(throttle, FlightPolicy.Clamp(targetThrottle, .05, 1), !vessel.LandedOrSplashed, Planetarium.GetUniversalTime(), ref lastThrottle);
                if (holdSpeed) c.mainThrottle = (float)throttle;
                if (props.Rotors.Count > 0 && !props.HasLift(vessel) && mode != "spool")
                {
                    var samples = props.Sample(); double rpm = double.PositiveInfinity;
                    foreach (var rotor in samples) rpm = Math.Min(rpm, rotor.Rpm);
                    if (!double.IsNaN(rpm) && !double.IsInfinity(rpm) && rpm > 1 && !double.IsNaN(props.Radius))
                        props.Collective((float)FlightPolicy.Clamp(Math.Atan2(vessel.srfSpeed, rpm * 2 * Math.PI / 60 * props.Radius) * 180 / Math.PI + 8, 5, 45));
                }
            }
            catch (Exception ex) { Stop(); ChatWindow.Notice("Local controller released after error: " + ex.Message); }
        }
        void Stop(bool interruptPlan = true)
        {
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
                else if (step.Op == "land") { args["name"] = step.Destination; result = Command("land_at_spot", args); }
                else if (step.Op != "wait")
                {
                    args["altitude_m"] = step.Altitude; args["altitude_ref"] = step.Reference;
                    args["heading"] = step.Heading; args["speed"] = step.Speed;
                    args["vertical_speed"] = step.VerticalSpeed;
                    result = Command("plane_hold", args);
                    if (step.Op == "circle") { directBank = step.Bank; planTurn = 0; planLastHeading = FlightGlobals.ship_heading; }
                }
                bool accepted = step.Op == "wait" || result == "Local aircraft holds engaged."
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
                s.deployAngle = (float)FlightPolicy.Clamp(s.deployAngle + Math.Sign(input) * sign * Math.Min(.35, Math.Abs(input) * 3), -12, 12);
                s.deploy = Math.Abs(s.deployAngle) > .05;
                recovery.AcceptSurface(s);
            }
            vessel.ctrlState.pitchTrim = (float)FlightPolicy.Clamp(vessel.ctrlState.pitchTrim + Math.Sign(input) * Math.Min(.015, Math.Abs(input) * .05), -1, 1);
            SyncTrim();
            trimBaselineVs = vessel.verticalSpeed; trimBaselinePitch = Pitch(); watchTrimUntil = Time.realtimeSinceStartup + 8;
        }
        internal static string Execute(string name, string argsJson)
        {
            // AI-off autopilot, or in-mod chat tool dispatch (NativeChat) may call into the local command layer.
            bool allowed = OwnsControls;
            if (!allowed) return "Local mode is not ready.";
            if (instance == null || FlightGlobals.ActiveVessel == null) return "No active flight vessel.";
            try { instance.Bind(); instance.ExtrasCancel(name); return instance.Command(name, MiniJson.Deserialize(argsJson ?? "{}")); }
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
                case "flightplan/fly":
                    string text = Str(a, "plan", "");
                    if (plan == null || plan.Text != text || !plan.Paused) plan = ParsePlan(text);
                    plan.Start(); planLastTime = Planetarium.GetUniversalTime(); return "Local plan " + plan.Status;
                case "get_status": case "autopilot_status": return "Local " + mode + "; " + vessel.vesselName;
                case "set_gear": SetGroup(vessel, KSPActionGroup.Gear, Bool(a, "down", true)); return "Gear set.";
                case "set_brakes": parkingReleased = !Bool(a, "on", true); SetGroup(vessel, KSPActionGroup.Brakes, !parkingReleased); return "Brakes set.";
                case "set_lights": SetGroup(vessel, KSPActionGroup.Light, Bool(a, "on", true)); return "Lights set.";
                case "set_rcs": SetGroup(vessel, KSPActionGroup.RCS, Bool(a, "on", true)); return "RCS set.";
                case "set_sas": if (Active) return "Stop local control before enabling SAS."; SetGroup(vessel, KSPActionGroup.SAS, Bool(a, "enabled", true)); return "SAS set.";
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
                    bool island = builtIn != null && builtIn.Item1 == "island";
                    if (!savedRunway)
                    {
                        if (builtIn == null) return "Unknown local runway: " + destination + " (built-in: KSC 09/27, Island 09/27; or a saved spot).";
                        if (vessel.mainBody.bodyName != "Kerbin") return "Built-in runways are on Kerbin.";
                        direction = builtIn.Item2; destination = "";
                        selectedRunway = island ? new RunwayMission { Lat = -1.516092, Lon = -71.856744, EndLat = -1.514809, EndLon = -71.961815, Elevation = 134.6 }
                            : new RunwayMission { Lat = -.0485997, Lon = -74.724375, EndLat = -.0502119, EndLon = -74.490300, Elevation = 69.1 };
                    }
                    if (!savedRunway && (island ? direction == "09" : direction == "27" || (direction == "" && vessel.longitude > -74.6)))
                    { double lat = selectedRunway.Lat, lon = selectedRunway.Lon; selectedRunway.Lat = selectedRunway.EndLat; selectedRunway.Lon = selectedRunway.EndLon; selectedRunway.EndLat = lat; selectedRunway.EndLon = lon; }
                    if (NavigationMath.Distance(vessel.latitude, vessel.longitude, selectedRunway.Lat, selectedRunway.Lon, vessel.mainBody.Radius) > 150000) return "Runway is beyond the 150 km approach limit.";
                    double approachSpeed = Num(a, "approach_speed", 0);
                    BeginHold(); runway = selectedRunway; reverseRollout = new ReverseRollout(); holdAltitude = holdHeading = holdSpeed = true; mode = "landing"; directPitch = directBank = null;
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
                case "set_heading": heading = Num(a, "heading", FlightGlobals.ship_heading); return Active ? "Heading updated." : "Engage local holds first.";
                case "set_speed": speed = FlightPolicy.Clamp(Num(a, "speed", 150), 25, 200); return Active ? "Speed updated." : "Engage local holds first.";
                default: return ResidualCommand(name, a);
            }
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
            takeoff = new TakeoffMission(Planetarium.GetUniversalTime()); return StartRotorMode("takeoff");
        }
        void OnDestroy() { GameEvents.onPartDie.Remove(OnPartDie); Stop(); if (vessel != null) vessel.OnFlyByWire -= Fly; if (instance == this) instance = null; }
    }
}
