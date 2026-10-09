using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    // P5-4: orbital / docking ports. Plans with native maneuver nodes (OrbitMath); executes with MechJeb when present,
    // otherwise says so clearly. Sun/antenna lock use stock SAS LockRotation (no MechJeb, no kRPC).
    public partial class NativeFlightController
    {
        string attitudeLock; float nextAttitudeLock;
        static readonly object MjOwner = new object();

        bool InSpace { get { var s = vessel.situation; return s == Vessel.Situations.ORBITING || s == Vessel.Situations.SUB_ORBITAL || s == Vessel.Situations.ESCAPING; } }

        string ExecuteNode(ManeuverNode node, string what)
        {
            double dv = node.DeltaV.magnitude;
            object core = MechJebLink.Core(vessel);
            if (core == null) return what + " node created (" + dv.ToString("0.0", Inv) + " m/s). Burn it manually - auto-execute " + MechJebLink.Missing;
            try
            {
                try { MechJebLink.Use(MechJebLink.Module(core, "MechJebModuleStagingController"), MjOwner); } catch (Exception) { }
                MechJebLink.Call(MechJebLink.Module(core, "MechJebModuleNodeExecutor"), "ExecuteOneNode", MjOwner);
                return what + " node created (" + dv.ToString("0.0", Inv) + " m/s); MechJeb is executing it (autostage on).";
            }
            catch (Exception ex) { return what + " node created (" + dv.ToString("0.0", Inv) + " m/s) but MechJeb execute failed: " + (ex.InnerException ?? ex).Message; }
        }

        ManeuverNode AddNode(double ut, double radial, double normal, double prograde)
        {
            var solver = vessel.patchedConicSolver;
            if (solver == null) throw new InvalidOperationException("Maneuver nodes need the tracking station / mission control upgrade.");
            while (solver.maneuverNodes.Count > 0) solver.maneuverNodes[0].RemoveSelf();
            var node = solver.AddManeuverNode(ut);
            node.DeltaV = new Vector3d(radial, normal, prograde);
            solver.UpdateFlightPlan();
            return node;
        }

        void AttitudeLockTick()
        {
            if (attitudeLock == null || Time.realtimeSinceStartup < nextAttitudeLock) return;
            nextAttitudeLock = Time.realtimeSinceStartup + 2;
            if (!InSpace) { attitudeLock = null; ChatWindow.Notice("Attitude lock released: not in space."); return; }
            CelestialBody target = attitudeLock == "sun" ? Planetarium.fetch.Sun : FlightGlobals.GetHomeBody();
            if (target == vessel.mainBody && attitudeLock == "antenna" && vessel.mainBody == FlightGlobals.GetHomeBody()) target = vessel.mainBody;
            Vector3d dir = (target.position - vessel.CoMD).normalized;
            if (!vessel.ActionGroups[KSPActionGroup.SAS]) SetGroup(vessel, KSPActionGroup.SAS, true);
            if (vessel.Autopilot.Mode != VesselAutopilot.AutopilotMode.StabilityAssist) vessel.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
            vessel.Autopilot.SAS.LockRotation(Quaternion.LookRotation(dir) * Quaternion.Euler(90, 0, 0));
        }

        string OrbitalCommand(string name, Dictionary<string, object> a)
        {
            double now = Planetarium.GetUniversalTime();
            Orbit o = vessel.orbit; double mu = vessel.mainBody.gravParameter, R = vessel.mainBody.Radius;
            switch (name)
            {
                case "sync_orbit_altitude":
                {
                    var b = vessel.mainBody; double alt = OrbitMath.SyncAltitude(b.gravParameter, b.rotationPeriod, b.Radius);
                    return "{\"body\":\"" + b.bodyName + "\",\"sync_altitude_km\":" + (alt / 1000).ToString("0.00", Inv) + ",\"rotation_period_s\":" + b.rotationPeriod.ToString("0", Inv)
                        + ",\"fits_in_soi\":" + (alt + b.Radius < b.sphereOfInfluence ? "true" : "false") + "}";
                }
                case "time_to_target":
                {
                    if (mode == "landing" && runway != null) return "Local autoland in progress (" + runway.Phase + ").";
                    var t = FlightGlobals.fetch.VesselTarget; if (t == null) return "No landing in progress and no KSP target set.";
                    double d = (t.GetTransform().position - vessel.transform.position).magnitude;
                    return t.GetName() + ": " + FlightResidualPolicy.Distance(d) + (vessel.srfSpeed > 5 ? ", ~" + FlightResidualPolicy.Duration(d / vessel.srfSpeed) + " at " + vessel.srfSpeed.ToString("0", Inv) + " m/s." : ".");
                }
                case "warp_to_apoapsis": case "warp_to_soi_change":
                {
                    if (Active) return "Stop local control before warping.";
                    double lead = Num(a, "lead_seconds", name == "warp_to_apoapsis" ? 30 : 60), ev;
                    if (name == "warp_to_apoapsis") { if (o.eccentricity >= 1) return "No apoapsis on an escape trajectory."; ev = now + o.timeToAp; }
                    else { if (o.patchEndTransition != Orbit.PatchTransitionType.ENCOUNTER && o.patchEndTransition != Orbit.PatchTransitionType.ESCAPE) return "No SOI change on the current trajectory."; ev = o.EndUT; }
                    TimeWarp.fetch.WarpTo(OrbitMath.WarpUt(now, ev, lead));
                    return "Warping to " + FlightResidualPolicy.Duration(Math.Max(0, ev - now - lead)) + " from now (" + lead.ToString("0", Inv) + " s lead).";
                }
                case "circularize":
                {
                    if (!InSpace) return "Circularize is for orbit / sub-orbital flight.";
                    bool apo = OrbitMath.UseApoapsis(o.timeToAp, o.timeToPe, o.PeA) && o.eccentricity < 1;
                    double ut = now + (apo ? o.timeToAp : o.timeToPe), r = apo ? o.ApR : o.PeR;
                    return ExecuteNode(AddNode(ut, 0, 0, OrbitMath.CircularizeDv(mu, r, o.semiMajorAxis)), "Circularization at " + (apo ? "apoapsis" : "periapsis"));
                }
                case "change_apoapsis": case "change_periapsis": case "deorbit_burn":
                {
                    if (!InSpace) return "Orbit changes are for orbit / sub-orbital flight.";
                    if (o.eccentricity >= 1) return "Not on a closed orbit.";
                    bool isAp = name == "change_apoapsis";
                    double km = name == "deorbit_burn" ? Num(a, "periapsis_km", 30) : Num(a, "altitude_km", double.NaN);
                    if (name == "deorbit_burn" && vessel.mainBody.atmosphere && km * 1000 > vessel.mainBody.atmosphereDepth && !Bool(a, "force", false))
                        return "Periapsis " + km.ToString("0", Inv) + " km is above the atmosphere; it won't deorbit. Use force=true to override.";
                    string gate = OrbitMath.ApsisGate(isAp ? "Apoapsis" : "Periapsis", km, isAp ? o.PeA : o.ApA, isAp, 0); if (gate != null) return gate;
                    double rBurn = isAp ? o.PeR : o.ApR, ut = now + (isAp ? o.timeToPe : o.timeToAp);
                    double dv = OrbitMath.ApsisChangeDv(mu, rBurn, o.semiMajorAxis, R + km * 1000);
                    return ExecuteNode(AddNode(ut, 0, 0, dv), (name == "deorbit_burn" ? "Deorbit" : isAp ? "Apoapsis change" : "Periapsis change") + " to " + km.ToString("0", Inv) + " km");
                }
                case "change_inclination":
                {
                    if (!InSpace || o.eccentricity >= 1) return "Inclination change needs a closed orbit.";
                    double delta = Num(a, "inclination_deg", o.inclination) - o.inclination;
                    double taAn = (360 - o.argumentOfPeriapsis) * Math.PI / 180, taDn = taAn + Math.PI;
                    double utAn = o.GetUTforTrueAnomaly(taAn, now), utDn = o.GetUTforTrueAnomaly(taDn, now);
                    if (utAn < now) utAn += o.period; if (utDn < now) utDn += o.period;
                    bool an = utAn < utDn; double ut = an ? utAn : utDn;
                    double speed = o.getOrbitalSpeedAt(ut), normal, prograde;
                    OrbitMath.InclinationDv(speed, delta, an, out normal, out prograde);
                    return ExecuteNode(AddNode(ut, 0, normal, prograde), "Inclination change " + delta.ToString("+0.0;-0.0", Inv) + " deg at " + (an ? "AN" : "DN"));
                }
                case "sun_lock": case "antenna_lock":
                    if (!Bool(a, "on", true)) { attitudeLock = null; return (name == "sun_lock" ? "Sun" : "Antenna") + " lock off; SAS stays on."; }
                    if (!InSpace) return (name == "sun_lock" ? "Sun" : "Antenna") + " lock is for space (orbit / sub-orbital) only.";
                    if (Active) return "Stop local control first.";
                    if (name == "antenna_lock" && vessel.mainBody == FlightGlobals.GetHomeBody()) return "Already orbiting the home body; antenna lock points at it only from elsewhere.";
                    attitudeLock = name == "sun_lock" ? "sun" : "antenna"; nextAttitudeLock = 0;
                    return (name == "sun_lock" ? "Sun lock: nose held on the sun" : "Antenna lock: nose held on " + FlightGlobals.GetHomeBody().bodyName) + " (stock SAS). Say off to release.";
                case "mechjeb_ascent":
                {
                    if (vessel.LandedOrSplashed && IsPlane()) return "This is a plane on the ground: use takeoff, not a MechJeb rocket ascent.";
                    object core = MechJebLink.Core(vessel); if (core == null) return "Ascent " + MechJebLink.Missing;
                    try
                    {
                        double alt = Num(a, "target_altitude_km", 80) * 1000, inc = Num(a, "inclination_deg", 0);
                        object ap = MechJebLink.Module(core, "MechJebModuleAscentAutopilot"), settings = null;
                        try { settings = MechJebLink.Module(core, "MechJebModuleAscentSettings"); } catch (Exception) { }
                        bool okAlt = (settings != null && MechJebLink.SetValue(settings, alt, "DesiredOrbitAltitude", "desiredOrbitAltitude")) || MechJebLink.SetValue(ap, alt, "desiredOrbitAltitude", "DesiredOrbitAltitude");
                        bool okInc = (settings != null && MechJebLink.SetValue(settings, inc, "DesiredInclination", "desiredInclination")) || MechJebLink.SetValue(ap, inc, "desiredInclination", "DesiredInclination");
                        if (!okAlt || !okInc) return "MechJeb API mismatch: could not set ascent altitude/inclination.";
                        try { MechJebLink.Use(MechJebLink.Module(core, "MechJebModuleStagingController"), MjOwner); } catch (Exception) { }
                        Stop(); SetGroup(vessel, KSPActionGroup.SAS, false);
                        MechJebLink.Use(ap, MjOwner);
                        string msg = "MechJeb ascent engaged: target " + (alt / 1000).ToString("0", Inv) + " km, inclination " + inc.ToString("0.#", Inv) + " deg, autostage on.";
                        if (vessel.situation == Vessel.Situations.PRELAUNCH)
                        { vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 1; KSP.UI.Screens.StageManager.ActivateNextStage(); msg += " Lift-off: staged first stage."; }
                        return msg;
                    }
                    catch (Exception ex) { return "MechJeb ascent failed: " + (ex.InnerException ?? ex).Message; }
                }
                case "dock_with":
                {
                    string target = Str(a, "name", "target").Trim();
                    if (target.Length > 0 && !target.Equals("target", StringComparison.OrdinalIgnoreCase))
                    {
                        Vessel found = null;
                        foreach (var v in FlightGlobals.Vessels) if (v != vessel && v.vesselName.Equals(target, StringComparison.OrdinalIgnoreCase)) { found = v; break; }
                        if (found == null) return "No vessel named '" + target + "'.";
                        FlightGlobals.fetch.SetVesselTarget(found);
                    }
                    if (FlightGlobals.fetch.VesselTarget == null) return "No KSP target set; target the station on the map or name it.";
                    bool rcs = vessel.FindPartModulesImplementing<ModuleRCS>().Count > 0;
                    if (!rcs) return "No RCS thrusters: MechJeb's docking autopilot needs RCS.";
                    object core = MechJebLink.Core(vessel); if (core == null) return "Docking autopilot " + MechJebLink.Missing;
                    if (!(FlightGlobals.fetch.VesselTarget is ModuleDockingNode)) return "Target set; now target a docking port on " + FlightGlobals.fetch.VesselTarget.GetName() + " (right-click it) and ask again.";
                    try { Stop(); MechJebLink.Use(MechJebLink.Module(core, "MechJebModuleDockingAutopilot"), MjOwner); return "MechJeb docking autopilot engaged on " + FlightGlobals.fetch.VesselTarget.GetName() + "."; }
                    catch (Exception ex) { return "MechJeb docking failed: " + (ex.InnerException ?? ex).Message; }
                }
                case "land_at": case "land_at_ksc":
                {
                    if (name == "land_at_ksc" && IsPlane()) return Command("land_plane", Args("name", "KSC Runway"));
                    object core = MechJebLink.Core(vessel); if (core == null) return "Targeted landing " + MechJebLink.Missing;
                    double lat = name == "land_at_ksc" ? -.0972 : Num(a, "latitude", double.NaN), lon = name == "land_at_ksc" ? -74.5577 : Num(a, "longitude", double.NaN);
                    if (double.IsNaN(lat) || double.IsNaN(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) return "latitude/longitude required.";
                    if (name == "land_at_ksc" && vessel.mainBody != FlightGlobals.GetHomeBody()) return "KSC is on " + FlightGlobals.GetHomeBody().bodyName + ".";
                    try
                    {
                        object tc = MechJebLink.Get(core, "target"); if (tc == null) return "MechJeb API mismatch: core.target";
                        MechJebLink.Call(tc, "SetPositionTarget", vessel.mainBody, lat, lon);
                        object land = MechJebLink.Module(core, "MechJebModuleLandingAutopilot");
                        MechJebLink.SetValue(land, Num(a, "touchdown_speed", 1.5), "touchdownSpeed", "TouchdownSpeed");
                        Stop(); MechJebLink.Call(land, "LandAtPositionTarget", MjOwner);
                        return "MechJeb landing autopilot: landing at " + lat.ToString("0.000", Inv) + ", " + lon.ToString("0.000", Inv) + ".";
                    }
                    catch (Exception ex) { return "MechJeb landing failed: " + (ex.InnerException ?? ex).Message; }
                }
            }
            return null;
        }
    }
}
