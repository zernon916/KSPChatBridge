using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using UnityEngine;
using Expansions.Serenity;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NativeFlightController : MonoBehaviour
    {
        static NativeFlightController instance;
        internal static bool Active { get { return instance != null && instance.mode != "idle"; } }
        internal static bool Busy { get { return Active || (instance != null && instance.plan != null && instance.plan.Running); } }
        internal static bool PlanRunning { get { return instance != null && instance.plan != null && instance.plan.Running; } }
        internal static string Phase { get { return instance == null ? "idle" : instance.mode; } }
        readonly ControlLease lease = new ControlLease();
        readonly Dictionary<ModuleControlSurface, SurfaceState> originals = new Dictionary<ModuleControlSurface, SurfaceState>();
        readonly Dictionary<string, bool> auto = new Dictionary<string, bool> { { "master", true }, { "pitch", true }, { "roll", true }, { "yaw", true }, { "rotor", true } };
        Vessel vessel;
        string mode = "idle";
        double altitude, heading, speed = 150, band = 150, throttle, lastThrottle = -10, prevPitch, prevRoll, pitchIntegral, vsIntegral;
        double? capture;
        float nextTrim, watchTrimUntil;
        double trimBaselineVs, trimBaselinePitch;
        bool trimSuspended, parkingSet, parkingReleased;
        int partCount;
        string settingsPath;
        double? directVs, directPitch, directBank;
        float elevator;
        NativePropulsion props;
        NativePower power;
        NativeRecovery recovery;
        bool wasNative;
        readonly NativeEngines engines = new NativeEngines();
        RotorSpool spool;
        CollectiveController collective = new CollectiveController();
        string afterSpool;
        float nextSpool;
        long spoolSequence;
        bool helicopterLanding;
        bool holdAltitude = true, holdHeading = true, holdSpeed = true;
        TakeoffMission takeoff;
        RunwayMission runway;
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
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 16 }; }
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
            settingsPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/native_settings.json");
            try
            {
                if (File.Exists(settingsPath))
                {
                    var data = Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsPath));
                    band = FlightPolicy.Clamp(Num(data, "altitude_band_m", 150), 10, 500);
                    foreach (string key in new List<string>(auto.Keys)) auto[key] = Bool(data, "auto_" + key, true);
                }
            }
            catch (Exception ex) { ChatWindow.Notice("Local settings: " + ex.Message); }
        }
        void Save()
        {
            var data = new Dictionary<string, object> { { "altitude_band_m", band } };
            foreach (var kv in auto) data["auto_" + kv.Key] = kv.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            File.WriteAllText(settingsPath, Json().Serialize(data));
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
            partCount = vessel.parts.Count;
            foreach (Part p in vessel.parts) foreach (PartModule m in p.Modules)
            { var s = m as ModuleControlSurface; if (s != null) originals[s] = new SurfaceState(s); }
        }
        void Update()
        {
            Bind();
            if (!BridgeLauncher.NativeReady || BridgeLauncher.AiEnabled || vessel == null) { wasNative = false; return; }
            if (!wasNative) { recovery = new NativeRecovery(vessel); wasNative = true; }
            bool manual = GameSettings.PITCH_UP.GetKey() || GameSettings.PITCH_DOWN.GetKey() || GameSettings.ROLL_LEFT.GetKey()
                || GameSettings.ROLL_RIGHT.GetKey() || GameSettings.YAW_LEFT.GetKey() || GameSettings.YAW_RIGHT.GetKey();
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
                    { mode = afterSpool; parkingReleased = true; vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, false); ChatWindow.Notice("Rotor spool ready: " + mode); }
                    else if (spool.Result != null) { string failure = spool.Result; Stop(); ChatWindow.Notice(failure); }
                }
                catch (Exception ex) { Stop(); ChatWindow.Notice("Rotor spool stopped: " + ex.Message); }
            }
            if (vessel.parts.Count != partCount)
            {
                partCount = vessel.parts.Count;
                foreach (var surface in new List<ModuleControlSurface>(originals.Keys)) if (surface == null) originals.Remove(surface);
                ChatWindow.Notice("Vessel parts changed; local controller state refreshed.");
                if (mode != "spool") props = new NativePropulsion(vessel);
                power = new NativePower(vessel);
            }
            try { power.Tick(vessel, Time.realtimeSinceStartup); }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] Local power recovery: " + ex.Message); }
            try { engines.Tick(vessel, Time.realtimeSinceStartup); }
            catch (Exception ex) { ChatWindow.Notice("Local engine restart failed: " + ex.Message); }
            try
            {
                int restored = recovery.Tick(vessel, Time.realtimeSinceStartup, Active && !vessel.LandedOrSplashed);
                if (restored > 0) ChatWindow.Notice("Local pilot: restored configuration on " + restored + " module(s).");
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] Local configuration recovery: " + ex.Message); }
            if (GameSettings.PITCH_UP.GetKey() || GameSettings.PITCH_DOWN.GetKey() || GameSettings.ROLL_LEFT.GetKey()
                || GameSettings.ROLL_RIGHT.GetKey() || GameSettings.YAW_LEFT.GetKey() || GameSettings.YAW_RIGHT.GetKey())
                if (Active) { Stop(); ChatWindow.Notice("Local autopilot released: manual input."); }
            if (vessel.LandedOrSplashed && !parkingReleased && mode == "idle")
            {
                bool wheels = false;
                foreach (Part p in vessel.parts) if (p.FindModuleImplementing<ModuleWheelBase>() != null) { wheels = true; break; }
                if (wheels && !parkingSet) { vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true); parkingSet = true; }
                else if (parkingSet && !vessel.ActionGroups[KSPActionGroup.Brakes]) parkingReleased = true;
            }
            if (mode == "hold" && auto["master"] && !trimSuspended && Time.realtimeSinceStartup >= nextTrim)
            {
                nextTrim = Time.realtimeSinceStartup + 8;
                if (Math.Abs(vessel.verticalSpeed) < 1 && Math.Abs(Roll()) < 5 && vessel.srfSpeed > 25 && !directPitch.HasValue) NudgeTrim(false);
            }
            if (watchTrimUntil > Time.realtimeSinceStartup && (Math.Abs(vessel.verticalSpeed) > Math.Abs(trimBaselineVs) + 2 || Math.Abs(Pitch() - trimBaselinePitch) > 3))
            { trimSuspended = true; watchTrimUntil = 0; ChatWindow.Notice("Auto-trim paused: flight drifted from level. Reset restores surfaces."); }
        }
        double Pitch() { return Math.Asin(FlightPolicy.Clamp(Vector3d.Dot(vessel.ReferenceTransform.up, vessel.upAxis), -1, 1)) * 180 / Math.PI; }
        double Roll() { return -Math.Atan2(Vector3d.Dot(vessel.ReferenceTransform.right, vessel.upAxis), Vector3d.Dot(-vessel.ReferenceTransform.forward, vessel.upAxis)) * 180 / Math.PI; }
        void BeginHold()
        {
            if (!lease.Acquire(vessel.id.ToString(), "native")) throw new InvalidOperationException("Another controller owns this vessel");
            mode = "hold"; throttle = vessel.ctrlState.mainThrottle; prevPitch = Pitch(); prevRoll = Roll();
            elevator = vessel.ctrlState.pitch; lastThrottle = Planetarium.GetUniversalTime() - 3;
            pitchIntegral = vsIntegral = 0; capture = null; trimSuspended = false;
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            if (!props.HasLift(vessel)) engines.Request(vessel, Time.realtimeSinceStartup);
        }
        void Fly(FlightCtrlState c)
        {
            if (!BridgeLauncher.NativeReady || BridgeLauncher.AiEnabled || mode == "idle" || vessel == null || vessel != FlightGlobals.ActiveVessel || vessel.packed) return;
            try
            {
                if (mode == "spool") return;
                double dt = Math.Max(.001, Math.Min(.1, Time.fixedDeltaTime));
                double pitch = Pitch(), roll = Roll(), kin = FlightPolicy.Clamp(Math.Pow(10 / Math.Max(1, vessel.GetTotalMass()), .4), .3, 1.3);
                double q = FlightPolicy.Wrap(pitch - prevPitch) / dt, p = FlightPolicy.Wrap(roll - prevRoll) / dt;
                prevPitch = pitch; prevRoll = roll;
                if (mode == "takeoff")
                {
                    directPitch = takeoff.Step(Planetarium.GetUniversalTime(), vessel.srfSpeed, vessel.radarAltitude, vessel.LandedOrSplashed, stall);
                    if (takeoff.Phase == "takeoff timeout") { c.mainThrottle = 0; vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, true); Stop(); ChatWindow.Notice("Takeoff timed out on the ground."); return; }
                    if (takeoff.Phase == "climbout complete") { mode = "hold"; directPitch = directBank = null; }
                    else
                    {
                        directBank = 0; speed = 200;
                        parkingReleased = true;
                        vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, throttle < .6 && vessel.LandedOrSplashed);
                        if (vessel.radarAltitude > 25) vessel.ActionGroups.SetGroup(KSPActionGroup.Gear, false);
                        c.wheelSteer = (float)FlightPolicy.Clamp(.04 * FlightPolicy.Wrap(heading - FlightGlobals.ship_heading), -.5, .5);
                    }
                }
                if (mode == "landing")
                {
                    runway.Step(vessel.latitude, vessel.longitude, vessel.altitude, vessel.radarAltitude, vessel.srfSpeed, vessel.LandedOrSplashed, vessel.mainBody.Radius, stall);
                    if (runway.Phase == "go around") { altitude = vessel.altitude + 500; speed = 1.5 * stall; mode = "hold"; directPitch = null; directVs = null; ChatWindow.Notice("Local autoland: going around after missed touchdown."); }
                    else if (runway.Phase == "stopped") { c.mainThrottle = 0; Stop(false); return; }
                    else
                    {
                        heading = runway.DesiredHeading; altitude = runway.DesiredAltitude; speed = runway.DesiredSpeed; directVs = runway.DesiredVs;
                        vessel.ActionGroups.SetGroup(KSPActionGroup.Gear, runway.Gear);
                        if (runway.Phase == "rollout")
                        {
                            c.mainThrottle = 0; c.pitch = (float)FlightPolicy.Clamp(-.04 * q, -.2, .3);
                            c.roll = (float)FlightPolicy.Clamp(-.02 * roll - .006 * p, -1, 1);
                            c.wheelSteer = (float)FlightPolicy.Clamp(.04 * FlightPolicy.Wrap(heading - FlightGlobals.ship_heading), -.4, .4);
                            vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, vessel.srfSpeed < 25 || ((int)(Planetarium.GetUniversalTime() * 2) % 2 == 0));
                            return;
                        }
                    }
                }
                if (mode == "helicopter")
                {
                    if (helicopterLanding && vessel.LandedOrSplashed && Math.Abs(vessel.verticalSpeed) < .5)
                    { props.Collective(0); props.Set(0, 460, false); Stop(); return; }
                    double vs = helicopterLanding ? (vessel.radarAltitude > 30 ? -3 : vessel.radarAltitude > 10 ? -2 : vessel.radarAltitude > 3 ? -1 : -.7)
                        : FlightPolicy.Clamp(.3 * (altitude - vessel.altitude), -3, 3);
                    double fwd = Vector3d.Dot(vessel.srf_velocity, vessel.ReferenceTransform.up);
                    double side = Vector3d.Dot(vessel.srf_velocity, vessel.ReferenceTransform.right);
                    double pitchTarget = props.Compound ? 0 : FlightPolicy.Clamp(-1.5 * (speed - fwd), -12, 12);
                    double rollTarget = FlightPolicy.Clamp(-1.5 * side, -12, 12);
                    c.pitch = (float)FlightPolicy.Clamp(.022 * (pitchTarget - pitch) - .012 * q, -1, 1);
                    c.roll = (float)FlightPolicy.Clamp(.014 * (rollTarget - roll) - .01 * p, -1, 1);
                    c.yaw = (float)FlightPolicy.Clamp(.03 * FlightPolicy.Wrap(heading - FlightGlobals.ship_heading), -1, 1);
                    props.Collective((float)collective.Step(vs, vessel.verticalSpeed, dt, vessel.LandedOrSplashed), "lift");
                    props.Yaw(c.yaw, (float)FlightPolicy.Clamp(.8 * (speed - fwd), -12, 20));
                    return; // rotorcraft lift must never command main throttle
                }
                double targetVs = directVs ?? FlightPolicy.VerticalSpeed(vessel.altitude, altitude, 25, 15, kin, band, ref capture);
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
            if (interruptPlan && plan != null) plan.Interrupt();
            mode = "idle"; lease.Release(); capture = null; spool = null;
            engines.Cancel();
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
                else if (step.Op == "land") { args["name"] = step.Destination; result = Command("land_at_spot", args); }
                else if (step.Op != "wait")
                {
                    args["altitude_m"] = step.Altitude; args["altitude_ref"] = step.Reference;
                    args["heading"] = step.Heading; args["speed"] = step.Speed;
                    result = Command("plane_hold", args);
                    if (step.Op == "circle") { directBank = step.Bank; planTurn = 0; planLastHeading = FlightGlobals.ship_heading; }
                }
                bool accepted = step.Op == "wait" || result == "Local aircraft holds engaged."
                    || result == "Local takeoff engaged." || result == "Local rotor spool started; waiting for measured RPM."
                    || result == "Local autoland: proceeding to approach entry.";
                if (!accepted) { plan.Interrupt(); ChatWindow.Notice("Plan paused: " + result); return; }
                plan.Entered = true;
                ChatWindow.Notice("Local plan: " + step.Text);
            }
            else plan.Elapsed += dt;
            bool complete = false;
            if (step.Op == "wait" || step.Op == "cruise") complete = plan.Elapsed >= step.Seconds;
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
            if (!auto["master"] || !auto["pitch"] || trimSuspended) return;
            if (Math.Abs(vessel.verticalSpeed) >= 1 || Math.Abs(Roll()) >= 5) return;
            double input = vessel.ctrlState.pitch;
            if (!force && Math.Abs(input) < .04) return;
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
            trimBaselineVs = vessel.verticalSpeed; trimBaselinePitch = Pitch(); watchTrimUntil = Time.realtimeSinceStartup + 8;
        }
        internal static string Execute(string name, string argsJson)
        {
            if (!BridgeLauncher.NativeReady || BridgeLauncher.AiEnabled) return "Local mode is not ready.";
            if (instance == null || FlightGlobals.ActiveVessel == null) return "No active flight vessel.";
            try { instance.Bind(); return instance.Command(name, Json().Deserialize<Dictionary<string, object>>(argsJson ?? "{}")); }
            catch (Exception ex) { return "Local command failed: " + ex.Message; }
        }
        string Command(string name, Dictionary<string, object> a)
        {
            switch (name)
            {
                case "abort": if (plan != null) plan.Pause(); Stop(); vessel.ctrlState.mainThrottle = 0; return "Hard abort: local control stopped, throttle zero.";
                case "stop_current": if (plan != null) plan.Pause(); Stop(); return "Local controller stopped; throttle preserved.";
                case "flightplan/check": NativePlan.Parse(Str(a, "plan", "")); return "Local plan valid.";
                case "flightplan/stop": if (plan != null) plan.Pause(); return "Plan paused; active controller continues.";
                case "flightplan/fly":
                    string text = Str(a, "plan", "");
                    if (plan == null || plan.Text != text || !plan.Paused) plan = NativePlan.Parse(text);
                    plan.Start(); planLastTime = Planetarium.GetUniversalTime(); return "Local plan " + plan.Status;
                case "get_status": case "autopilot_status": return "Local " + mode + "; " + vessel.vesselName;
                case "set_gear": vessel.ActionGroups.SetGroup(KSPActionGroup.Gear, Bool(a, "down", true)); return "Gear set.";
                case "set_brakes": parkingReleased = !Bool(a, "on", true); vessel.ActionGroups.SetGroup(KSPActionGroup.Brakes, !parkingReleased); return "Brakes set.";
                case "set_lights": vessel.ActionGroups.SetGroup(KSPActionGroup.Light, Bool(a, "on", true)); return "Lights set.";
                case "set_rcs": vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, Bool(a, "on", true)); return "RCS set.";
                case "set_sas": if (Active) return "Stop local control before enabling SAS."; vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, Bool(a, "enabled", true)); return "SAS set.";
                case "get_trim_state":
                    var trim = new Dictionary<string, object> { { "pitch", vessel.ctrlState.pitchTrim }, { "roll", vessel.ctrlState.rollTrim }, { "yaw", vessel.ctrlState.yawTrim }, { "craft", vessel.vesselName }, { "heli", props.HasLift(vessel) }, { "notes_saved", false } };
                    foreach (var kv in auto) trim["auto_" + kv.Key] = kv.Value;
                    return Json().Serialize(trim);
                case "set_trim":
                    string axis = Str(a, "axis", "pitch"); float value = (float)FlightPolicy.Clamp(Num(a, "value", 0), -1, 1);
                    if (axis.StartsWith("auto_") && auto.ContainsKey(axis.Substring(5))) { auto[axis.Substring(5)] = value >= .5; Save(); return "Auto-trim setting saved."; }
                    if (axis == "collective") { props.Collective((float)FlightPolicy.Clamp(Num(a, "value", 0), 0, 12)); return "Collective set."; }
                    if (axis == "pitch") vessel.ctrlState.pitchTrim = value;
                    else if (axis == "roll") vessel.ctrlState.rollTrim = value;
                    else if (axis == "yaw") vessel.ctrlState.yawTrim = value;
                    else return "Unsupported trim axis.";
                    return axis + " trim set.";
                case "auto_trim_now": NudgeTrim(true); return "Trim checked; adjustment requires steady level flight and enabled pitch trim.";
                case "trim":
                    if (Str(a, "direction", "up") != "reset") return "Use the local trim sliders to set trim.";
                    foreach (var pair in originals) if (pair.Key != null) { pair.Value.Restore(pair.Key); recovery.AcceptSurface(pair.Key); }
                    vessel.ctrlState.pitchTrim = vessel.ctrlState.rollTrim = vessel.ctrlState.yawTrim = 0;
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
                    double vs = Num(a, "vertical_speed", -999); directVs = vs > -999 ? (double?)FlightPolicy.Clamp(vs, -15, 25) : null;
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
                case "land_plane": case "land_at_spot":
                    if (vessel.mainBody.bodyName != "Kerbin") return "Local built-in runways are on Kerbin.";
                    bool wheels = false, wings = false;
                    foreach (Part part in vessel.parts) { wheels |= part.FindModuleImplementing<ModuleWheelBase>() != null; wings |= part.FindModuleImplementing<ModuleLiftingSurface>() != null; }
                    if (!wheels || !wings) return "Plane autoland requires wheels and wings.";
                    if (vessel.LandedOrSplashed) return "Take off before starting this local landing approach.";
                    string destination = Str(a, "name", ""), direction = Str(a, "runway", "");
                    runway = new RunwayMission { Lat = -.0485997, Lon = -74.724375, EndLat = -.0502119, EndLon = -74.490300, Elevation = 69.1 };
                    if (destination.IndexOf("Island", StringComparison.OrdinalIgnoreCase) >= 0) runway = new RunwayMission { Lat = -1.516092, Lon = -71.856744, EndLat = -1.514809, EndLon = -71.961815, Elevation = 134.6 };
                    else if (destination.Length > 0 && destination.IndexOf("Runway", StringComparison.OrdinalIgnoreCase) < 0) return "Local saved-spot migration pending: " + destination;
                    if (direction == "27" || destination.EndsWith("27") || (direction == "" && destination == "" && vessel.longitude > -74.6))
                    { double lat = runway.Lat, lon = runway.Lon; runway.Lat = runway.EndLat; runway.Lon = runway.EndLon; runway.EndLat = lat; runway.EndLon = lon; }
                    if (NavigationMath.Distance(vessel.latitude, vessel.longitude, runway.Lat, runway.Lon, vessel.mainBody.Radius) > 150000) return "Runway is beyond the 150 km approach limit.";
                    BeginHold(); holdAltitude = holdHeading = holdSpeed = true; mode = "landing"; directPitch = directBank = null; return "Local autoland: proceeding to approach entry.";
                case "heli_control":
                    if (!props.HasLift(vessel)) return "No lift rotor found; use aircraft controls.";
                    string heliMode = Str(a, "mode", "hover");
                    if (heliMode != "hover" && heliMode != "fly" && heliMode != "land" && heliMode != "stop") return "Local helicopter mode not yet ported: " + heliMode;
                    if (heliMode == "stop") { Stop(); return "Helicopter control stopped."; }
                    altitude = vessel.altitude - vessel.radarAltitude + Math.Max(1, Num(a, "altitude_m", 20));
                    heading = Num(a, "heading", FlightGlobals.ship_heading); if (heading < 0) heading = FlightGlobals.ship_heading;
                    speed = FlightPolicy.Clamp(Num(a, "speed", 0), 0, 50); helicopterLanding = heliMode == "land";
                    BeginHold(); collective = new CollectiveController(); return StartRotorMode("helicopter");
                case "set_heading": heading = Num(a, "heading", FlightGlobals.ship_heading); return Active ? "Heading updated." : "Engage local holds first.";
                case "set_speed": speed = FlightPolicy.Clamp(Num(a, "speed", 150), 25, 200); return Active ? "Speed updated." : "Engage local holds first.";
                default: return "Not yet ported to local control: " + name + ". Enable AI & Bridge for the existing implementation.";
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
            holdAltitude = holdHeading = holdSpeed = true; directVs = directPitch = directBank = null;
            takeoff = new TakeoffMission(Planetarium.GetUniversalTime()); return StartRotorMode("takeoff");
        }
        void OnDestroy() { Stop(); if (vessel != null) vessel.OnFlyByWire -= Fly; if (instance == this) instance = null; }
    }
}
