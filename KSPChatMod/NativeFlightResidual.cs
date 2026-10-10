using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    // P5-2: flight residual ports (aliases, fly_to, touch-and-go/go-around/circle, propulsion modes, engine/throttle/SAS,
    // abort/action groups, telemetry reports). Decisions live in FlightResidualPolicy (offline-tested).
    public partial class NativeFlightController
    {
        internal static readonly string[] ResidualTools =
        {
            "land", "land_at", "land_at_ksc", "fly_to", "fly_to_place", "touch_and_go", "go_around", "circle_here", "turn", "plane_pitch",
            "prop_control", "afterburner", "engine_mode", "flaps", "set_throttle", "set_engines", "cut_engines", "set_altitude",
            "level_off", "set_sas_mode", "abort_ag", "action_group", "fuel_check", "get_delta_v", "get_landing_eta", "how_far",
            "landing_check", "crew_report", "flight_report", "damage_report"
        };
        double flyLat, flyLon; string flyName; bool flying2, touchAndGo; float nextFlyTo;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        bool IsPlane()
        {
            bool wheels = false, wings = false;
            foreach (Part part in vessel.parts) { wheels |= part.FindModuleImplementing<ModuleWheelBase>() != null; wings |= part.FindModuleImplementing<ModuleLiftingSurface>() != null; }
            return wheels && wings;
        }
        static Dictionary<string, object> Args(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        /// <summary>fly_to navigation + touch-and-go, called from the native flight tick.</summary>
        void ResidualTick()
        {
            if (!flying2) return;
            if (mode != "hold") { flying2 = false; return; }
            if (Time.realtimeSinceStartup < nextFlyTo) return;
            nextFlyTo = Time.realtimeSinceStartup + 1;
            double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, flyLat, flyLon, vessel.mainBody.Radius);
            if (d < 3000)
            {
                flying2 = false; directBank = FlightResidualPolicy.CircleBank("left", 15, vessel.srfSpeed);
                ChatWindow.Notice("Arrived over " + flyName + "; circling left. Say land or a new heading.");
                return;
            }
            heading = NavigationMath.Bearing(vessel.latitude, vessel.longitude, flyLat, flyLon);
        }

        /// <summary>True when the landing just touched down and touch-and-go should relaunch.</summary>
        bool TouchAndGoNow(string phase)
        {
            if (!touchAndGo || phase != "rollout") return false;
            touchAndGo = false; reverseRollout = null; altitude = vessel.altitude + 300;
            ChatWindow.Notice("Touch-and-go: " + StartTakeoff());
            return true;
        }

        string ResidualCommand(string name, Dictionary<string, object> a)
        {
            string life = LifecycleCommand(name, a); if (life != null) return life;
            string orbital = OrbitalCommand(name, a); if (orbital != null) return orbital;
            string planner = MechJebPlannerCommand(name, a); if (planner != null) return planner;
            switch (name)
            {
                case "land":
                {
                    string where = Str(a, "where", "");
                    bool known = false;
                    try { known = where.Length > 0 && spots.Find(where, vessel.mainBody.bodyName) != null; } catch (ArgumentException) { }
                    string route = FlightResidualPolicy.LandRoute(where, vessel.LandedOrSplashed, props.HasLift(vessel), IsPlane(), known);
                    if (route == "grounded") return "Already grounded.";
                    if (route == "heli") return Command("heli_control", Args("mode", "land"));
                    if (route == "spot") return Command("land_plane", Args("name", FlightResidualPolicy.RunwayAlias(where)));
                    if (route == "plane") return Command("land_plane", Args());
                    return Command("land_here", Args());
                }
                case "fly_to": case "fly_to_place":
                {
                    if (vessel.LandedOrSplashed) return "Take off first, then fly_to.";
                    string target = Str(a, "name", "");
                    TaxiMission.Point p = null;
                    try { p = spots.Point(target, vessel.mainBody.bodyName); } catch (ArgumentException ex) { return ex.Message; }
                    if (p == null && vessel.mainBody.bodyName == "Kerbin" && target.Trim().Equals("KSC", StringComparison.OrdinalIgnoreCase))
                        p = new TaxiMission.Point { Name = "KSC", Lat = -.0485997, Lon = -74.724375 };
                    if (p == null) return "Unknown place: " + target + ". Save it as a landing spot first.";
                    double cruiseAlt = Num(a, "cruise_altitude_m", 0), cruiseSpeed = Num(a, "cruise_speed", 0);
                    string r = Command("plane_hold", Args("altitude_m", cruiseAlt > 0 ? cruiseAlt : -1, "altitude_ref", "msl",
                        "speed", cruiseSpeed > 0 ? cruiseSpeed : -1, "heading", NavigationMath.Bearing(vessel.latitude, vessel.longitude, p.Lat, p.Lon)));
                    if (mode != "hold") return r;
                    flyLat = p.Lat; flyLon = p.Lon; flyName = p.Name; flying2 = true; nextFlyTo = 0;
                    return "Flying to " + p.Name + " (" + FlightResidualPolicy.Distance(NavigationMath.Distance(vessel.latitude, vessel.longitude, p.Lat, p.Lon, vessel.mainBody.Radius)) + "); will circle on arrival.";
                }
                case "touch_and_go":
                {
                    if (mode == "landing") { touchAndGo = true; return "Touch-and-go armed for this approach."; }
                    string r = Command("land_plane", Args("name", Str(a, "name", "")));
                    if (mode == "landing") { touchAndGo = true; return r + " Touch-and-go armed."; }
                    return r;
                }
                case "go_around":
                    if (mode != "landing") return "Not on a local approach.";
                    touchAndGo = false; altitude = vessel.altitude + 500; speed = Math.Max(speed, 1.5 * stall); mode = "hold"; directPitch = null; directVs = null;
                    holdAltitude = holdHeading = holdSpeed = true;
                    return "Going around: climbing 500 m on runway heading.";
                case "circle_here":
                {
                    bool ov = Num(a, "bank", 15) > FlightPolicy.BankLimit(vessel.srfSpeed);   // an explicit bank number is Luke's explicit ask (max 45)
                    double cb = PilotPolicy.CircleBank(Str(a, "direction", "left"), Num(a, "bank", 15), vessel.srfSpeed, ov);
                    if (mode == "takeoff") { pendingCircle = cb; bankOverride = ov; return "Will start circling as soon as we climb out (100 m AGL)."; }
                    if (!Active || vessel.LandedOrSplashed) return "Engage local holds in flight first.";
                    flying2 = false; patternOn = false; directBank = cb; bankOverride = ov;
                }
                    return "Circling " + (directBank > 0 ? "right" : "left") + " at " + Math.Abs(directBank.Value).ToString("0", Inv) + " deg bank.";
                case "turn":
                    if (!Active) return "Engage local holds first.";
                    flying2 = false; directBank = null; holdHeading = true;
                    heading = FlightResidualPolicy.Turn(heading, Str(a, "direction", "left"), Num(a, "degrees", 90));
                    return "Turning to heading " + heading.ToString("0", Inv) + ".";
                case "plane_pitch":
                {
                    if (!Active) return "Engage local holds first.";
                    double deg = FlightPolicy.Clamp(Math.Abs(Num(a, "degrees", 5)), 0, 20) * (Str(a, "direction", "up").StartsWith("d", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                    directPitch = deg; directVs = null; holdAltitude = false;
                    return "Holding " + deg.ToString("0", Inv) + " deg pitch; set_altitude or level_off to resume altitude hold.";
                }
                case "set_altitude":
                {
                    double alt = Num(a, "altitude_m", double.NaN);
                    if (double.IsNaN(alt) || alt < 0) return "altitude_m required.";
                    if (Str(a, "ref", "msl").ToLowerInvariant() == "agl") alt += vessel.altitude - vessel.radarAltitude;
                    if (!Active) return Command("plane_hold", Args("altitude_m", alt, "altitude_ref", "msl"));
                    altitude = alt; holdAltitude = true; directPitch = null; directVs = null; capture = null;
                    return "Target altitude " + alt.ToString("0", Inv) + " m MSL.";
                }
                case "level_off":
                    if (!Active) return Command("plane_hold", Args("altitude_m", vessel.altitude, "altitude_ref", "msl"));
                    altitude = vessel.altitude; holdAltitude = true; directPitch = null; directVs = null; capture = null;
                    return "Levelling off at " + vessel.altitude.ToString("0", Inv) + " m.";
                case "set_throttle":
                {
                    bool manualThr = Active; if (Active) holdSpeed = false;   // live bug: refused while holds were on
                    float t = (float)FlightResidualPolicy.Throttle(Num(a, "value", 0));
                    vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = t;
                    throttle = t;
                    return "Throttle " + (t * 100).ToString("0", Inv) + "%." + (manualThr ? " Speed hold off (say set speed to hand it back)." : "");
                }
                case "set_engines": case "cut_engines":
                {
                    bool on = name == "set_engines" && Bool(a, "on", true);
                    if (name == "cut_engines") { string gate = FlightResidualPolicy.ConfirmGate(Bool(a, "confirmed", false), "cutting all engines"); if (gate != null) return gate; }
                    if (!on && Active) Stop();
                    int n = 0;
                    foreach (Part part in vessel.parts) foreach (PartModule m in part.Modules)
                    {
                        var e = m as ModuleEngines; if (e == null) continue;
                        if (on && !e.EngineIgnited) { e.Activate(); n++; } else if (!on && e.EngineIgnited) { e.Shutdown(); n++; }
                    }
                    if (!on) vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0;
                    return (on ? "Started " : "Shut down ") + n + " engine(s)." + (on ? "" : " Throttle zero.");
                }
                case "afterburner": case "engine_mode":
                {
                    int n = 0; var states = new List<string>();
                    bool want = Bool(a, "on", true);
                    foreach (Part part in vessel.parts) foreach (PartModule m in part.Modules)
                    {
                        var mm = m as MultiModeEngine; if (mm == null) continue;
                        if (name == "engine_mode") { mm.ToggleMode(); n++; }
                        else if (mm.runningPrimary == want) { mm.ToggleMode(); n++; }   // secondary = afterburner/wet on stock jets
                        states.Add(mm.runningPrimary ? mm.primaryEngineID : mm.secondaryEngineID);
                    }
                    if (states.Count == 0) return "No multi-mode engines on this craft.";
                    return (name == "afterburner" ? "Afterburner " + (want ? "on" : "off") : "Engine mode toggled") + " (" + n + " changed): " + string.Join(", ", states.ToArray()) + ".";
                }
                case "flaps":
                {
                    double deg = FlightResidualPolicy.FlapDegrees(Str(a, "setting", "1"));
                    if (deg < 0) return "Flap setting must be 0-3, up or full.";
                    int n = 0;
                    foreach (Part part in vessel.parts) foreach (PartModule m in part.Modules)
                    {
                        var s = m as ModuleControlSurface;
                        if (s == null || part.partInfo.title.IndexOf("blade", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (part.partInfo.title.IndexOf("flap", StringComparison.OrdinalIgnoreCase) < 0 && !(s.ignorePitch && s.ignoreRoll && s.ignoreYaw)) continue;
                        s.deploy = deg > 0; if (deg > 0) s.deployAngle = (float)deg * (s.deployAngle < 0 ? -1 : 1);
                        if (recovery != null) recovery.AcceptSurface(s); n++;
                    }
                    return n == 0 ? "No flaps found (parts named flap, or deploy-only surfaces)." : "Flaps " + deg.ToString("0", Inv) + " deg on " + n + " surface(s)." + (mode == "landing" || mode == "takeoff" ? " Autoland schedule may override." : "");
                }
                case "prop_control":
                {
                    if (!props.HasLift(vessel) && props.Sample().Count == 0) return "No robotic rotors on this craft.";
                    var rows = props.Sample(); var first = rows.Count > 0 ? rows[0] : null;
                    double rpm = FlightResidualPolicy.PropField(Str(a, "rpm", ""), 0, 460), torque = FlightResidualPolicy.PropField(Str(a, "torque", ""), 0, 100);
                    double pitch = FlightResidualPolicy.PropField(Str(a, "pitch", ""), -12, 45);
                    bool? motor = FlightResidualPolicy.PropSwitch(Str(a, "motor", ""));
                    if (Str(a, "reverse", "") != "" || Str(a, "group", "") != "") return "prop_control reverse/group is not ported locally; set rpm/torque/motor/pitch.";
                    if (first != null && (!double.IsNaN(rpm) || !double.IsNaN(torque) || motor.HasValue))
                        props.Set((float)(double.IsNaN(torque) ? first.Torque : torque), (float)(double.IsNaN(rpm) ? first.Limit : rpm), motor ?? first.Motor, 0);
                    if (!double.IsNaN(pitch)) props.Collective((float)pitch);
                    return "Rotors updated.";
                }
                case "set_sas_mode":
                {
                    if (Active) return "Stop local control before changing SAS mode.";
                    string m = FlightResidualPolicy.SasMode(Str(a, "mode", ""));
                    if (m == null) return "Unknown SAS mode. Use stability, prograde, retrograde, normal, antinormal, radial_in, radial_out, target, antitarget or maneuver.";
                    var am = (VesselAutopilot.AutopilotMode)Enum.Parse(typeof(VesselAutopilot.AutopilotMode), m);
                    SetGroup(vessel, KSPActionGroup.SAS, true);
                    if (!vessel.Autopilot.CanSetMode(am)) return "SAS mode " + m + " unavailable (pilot skill/probe level or no target/node).";
                    vessel.Autopilot.SetMode(am); return "SAS " + m + ".";
                }
                case "abort_ag":
                {
                    string gate = FlightResidualPolicy.ConfirmGate(Bool(a, "confirmed", false), "firing the Abort action group"); if (gate != null) return gate;
                    if (Active) Stop();
                    vessel.ActionGroups.ToggleGroup(KSPActionGroup.Abort); return "Abort action group fired.";
                }
                case "action_group":
                {
                    int g = FlightResidualPolicy.ActionGroup(Num(a, "group", 0)); if (g == 0) return "Action group must be 1-10.";
                    var ag = (KSPActionGroup)Enum.Parse(typeof(KSPActionGroup), "Custom" + g.ToString("00", Inv));
                    string st = Str(a, "state", "toggle").ToLowerInvariant();
                    if (st == "on") vessel.ActionGroups.SetGroup(ag, true); else if (st == "off") vessel.ActionGroups.SetGroup(ag, false); else vessel.ActionGroups.ToggleGroup(ag);
                    return "Action group " + g + " " + (st == "on" || st == "off" ? st : "toggled") + ".";
                }
                case "fuel_check":
                {
                    var amt = new Dictionary<string, double>(); var cap = new Dictionary<string, double>();
                    foreach (Part p in vessel.parts) foreach (PartResource r in p.Resources)
                    { double x; amt.TryGetValue(r.resourceName, out x); amt[r.resourceName] = x + r.amount; cap.TryGetValue(r.resourceName, out x); cap[r.resourceName] = x + r.maxAmount; }
                    return FlightResidualPolicy.FuelReport(amt, cap);
                }
                case "get_delta_v":
                {
                    var dv = vessel.VesselDeltaV;
                    if (dv == null || !dv.IsReady) return "{\"ready\":false}";
                    return "{\"ready\":true,\"total_actual\":" + dv.TotalDeltaVActual.ToString("0", Inv) + ",\"total_vac\":" + dv.TotalDeltaVVac.ToString("0", Inv)
                        + ",\"total_asl\":" + dv.TotalDeltaVASL.ToString("0", Inv) + ",\"burn_time_s\":" + dv.TotalBurnTime.ToString("0", Inv) + "}";
                }
                case "get_landing_eta":
                {
                    double eta = FlightResidualPolicy.LandingEta(vessel.LandedOrSplashed ? 0 : vessel.radarAltitude, vessel.verticalSpeed);
                    return "{\"agl_m\":" + vessel.radarAltitude.ToString("0", Inv) + ",\"vertical_speed\":" + vessel.verticalSpeed.ToString("0.0", Inv)
                        + ",\"eta_s\":" + (double.IsNaN(eta) ? "null" : eta.ToString("0", Inv)) + "}";
                }
                case "how_far":
                {
                    string target = Str(a, "name", "KSC"); TaxiMission.Point p = null;
                    try { p = spots.Point(target, vessel.mainBody.bodyName); } catch (ArgumentException ex) { return ex.Message; }
                    if (p == null && vessel.mainBody.bodyName == "Kerbin" && target.Trim().Equals("KSC", StringComparison.OrdinalIgnoreCase)) p = new TaxiMission.Point { Name = "KSC", Lat = -.0485997, Lon = -74.724375 };
                    if (p == null) return "Unknown place: " + target + ".";
                    double d = NavigationMath.Distance(vessel.latitude, vessel.longitude, p.Lat, p.Lon, vessel.mainBody.Radius);
                    double brg = NavigationMath.Bearing(vessel.latitude, vessel.longitude, p.Lat, p.Lon);
                    return p.Name + ": " + FlightResidualPolicy.Distance(d) + " at bearing " + brg.ToString("000", Inv) + (vessel.srfSpeed > 5 ? ", ~" + FlightResidualPolicy.Duration(d / vessel.srfSpeed) + " at current speed" : "") + ".";
                }
                case "landing_check":
                {
                    bool wheels = false; foreach (Part p in vessel.parts) if (p.FindModuleImplementing<ModuleWheelBase>() != null) { wheels = true; break; }
                    return FlightResidualPolicy.LandingCheck(wheels, vessel.ActionGroups[KSPActionGroup.Gear], vessel.srfSpeed, vessel.verticalSpeed, vessel.radarAltitude);
                }
                case "crew_report":
                {
                    var crew = vessel.GetVesselCrew();
                    if (crew.Count == 0) return "No crew aboard.";
                    var rows = new List<string>();
                    foreach (var k in crew) rows.Add(k.name + " (" + k.experienceTrait.Title + ", " + k.experienceLevel + "*)");
                    return crew.Count + " aboard: " + string.Join("; ", rows.ToArray()) + ".";
                }
                case "flight_report":
                    return vessel.vesselName + ": " + vessel.situation + ", alt " + vessel.altitude.ToString("0", Inv) + " m (AGL " + vessel.radarAltitude.ToString("0", Inv)
                        + "), speed " + vessel.srfSpeed.ToString("0", Inv) + " m/s, VS " + vessel.verticalSpeed.ToString("0.0", Inv) + ", heading " + FlightGlobals.ship_heading.ToString("000", Inv)
                        + ", throttle " + (vessel.ctrlState.mainThrottle * 100).ToString("0", Inv) + "%, local " + mode + ".";
                case "damage_report":
                {
                    var bad = new List<string>();
                    foreach (Part p in vessel.parts)
                    {
                        double heat = Math.Max(p.temperature / Math.Max(1, p.maxTemp), p.skinTemperature / Math.Max(1, p.skinMaxTemp));
                        if (heat > .8) bad.Add(p.partInfo.title + " hot " + (heat * 100).ToString("0", Inv) + "%");
                        foreach (PartModule m in p.Modules)
                        {
                            var e = m as ModuleEngines; if (e != null && e.EngineIgnited && e.flameout) bad.Add(p.partInfo.title + " flameout");
                            var w = m as ModuleWheels.ModuleWheelDamage; if (w != null && w.isDamaged) bad.Add(p.partInfo.title + " wheel damaged");
                        }
                    }
                    return bad.Count == 0 ? "No damage detected (" + vessel.parts.Count + " parts)." : "Damage: " + string.Join("; ", bad.ToArray()) + ".";
                }
            }
            return "Not yet ported to local control: " + name + ".";
        }
    }
}
