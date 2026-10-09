using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KSPChatBridge
{
    // The 6 tools that used to need the bridge, now in-process via MechJeb 2.15 planners (reflection only).
    public partial class NativeFlightController
    {
        string MjGate(out object core)
        {
            core = MechJebLink.Core(vessel);
            if (core == null) return MechJebLink.Missing;
            return MechJebPolicy.VersionGate(core.GetType().Assembly.GetName().Version);
        }

        static Type MjType(object core, string name)
        {
            var t = core.GetType().Assembly.GetType("MuMech." + name);
            if (t == null) throw new InvalidOperationException("MechJeb API mismatch: MuMech." + name);
            return t;
        }

        static void SetTimeRef(object op, string reference)
        {
            object sel = MechJebLink.Get(op, "_timeSelector"); if (sel == null) throw new InvalidOperationException("MechJeb API mismatch: _timeSelector");
            var allowed = MechJebLink.Get(sel, "_allowedTimeRef") as Array; if (allowed == null) throw new InvalidOperationException("MechJeb API mismatch: _allowedTimeRef");
            for (int i = 0; i < allowed.Length; i++)
                if (allowed.GetValue(i).ToString() == reference) { sel.GetType().GetField("_currentTimeRef", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(sel, i); return; }
            throw new InvalidOperationException("MechJeb time reference unavailable: " + reference);
        }

        /// <summary>MakeNodes on an Operation, place them, execute with MechJeb (autowarp, autostage).</summary>
        string MjPlanAndRun(object core, object op, string label, bool firstOnly, Orbit from = null, bool keepExisting = false, bool execute = true)
        {
            object target = MechJebLink.Get(core, "Target");
            var nodes = op.GetType().GetMethod("MakeNodes").Invoke(op, new object[] { from ?? vessel.orbit, Planetarium.GetUniversalTime(), target }) as IList;
            if (nodes == null || nodes.Count == 0)
            {
                object err = op.GetType().GetMethod("GetErrorMessage").Invoke(op, null);
                return label + ": MechJeb could not plan it" + (err != null && err.ToString().Length > 0 ? " - " + err : ".");
            }
            var solver = vessel.patchedConicSolver;
            if (solver == null) return label + ": maneuver nodes need the tracking station upgrade.";
            if (!keepExisting) while (solver.maneuverNodes.Count > 0) solver.maneuverNodes[0].RemoveSelf();
            var place = MjType(core, "VesselExtensions").GetMethod("PlaceManeuverNode", BindingFlags.Static | BindingFlags.Public);
            if (place == null) throw new InvalidOperationException("MechJeb API mismatch: VesselExtensions.PlaceManeuverNode");
            double dv = 0; int count = firstOnly ? 1 : nodes.Count;
            for (int i = 0; i < count; i++)
            {
                object mp = nodes[i];
                var dV = (Vector3d)MechJebLink.Get(mp, "dV"); double ut = (double)MechJebLink.Get(mp, "UT");
                Orbit o = solver.maneuverNodes.Count > 0 ? solver.maneuverNodes[solver.maneuverNodes.Count - 1].nextPatch : (from ?? vessel.orbit);
                place.Invoke(null, new object[] { vessel, o, dV, ut }); dv += dV.magnitude;
            }
            if (!execute) return null;
            object exec = MechJebLink.Get(core, "Node");
            MechJebLink.SetValue(exec, 1, "Autowarp");
            try { MechJebLink.Use(MechJebLink.Get(core, "Staging"), MjOwner); } catch (Exception) { }
            Stop();
            MechJebLink.Call(exec, solver.maneuverNodes.Count > 1 ? "ExecuteAllNodes" : "ExecuteOneNode", MjOwner);
            return label + ": " + solver.maneuverNodes.Count + " node(s), " + dv.ToString("0", Inv) + " m/s; MechJeb node executor is flying it (autowarp, autostage).";
        }

        CelestialBody FindBody(string name)
        {
            foreach (var b in FlightGlobals.Bodies) if (b.bodyName.Equals((name ?? "").Trim(), StringComparison.OrdinalIgnoreCase) || b.displayName.Replace("^N", "").Equals((name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        void MjSetTarget(object core, ITargetable t)
        {
            FlightGlobals.fetch.SetVesselTarget(t);
            MechJebLink.Call(MechJebLink.Get(core, "Target"), "Set", t);
        }

        string MechJebPlannerCommand(string name, Dictionary<string, object> a)
        {
            switch (name)
            {
                case "transfer_to": case "match_target_plane": case "launch_to_target_plane": case "course_correction": case "station_keep": case "apsis_longitude": break;
                default: return null;
            }
            object core; string gate = MjGate(out core); if (gate != null) return name.Replace('_', ' ') + " " + gate;
            if (Active && name != "launch_to_target_plane") Stop();
            try
            {
                switch (name)
                {
                    case "transfer_to":
                    {
                        if (vessel.situation != Vessel.Situations.ORBITING) return "Get into a stable orbit first.";
                        var body = FindBody(Str(a, "body", "Mun")); if (body == null) return "Unknown body '" + Str(a, "body", "") + "'.";
                        var cur = vessel.mainBody;
                        string kind = MechJebPolicy.TransferKind(cur.bodyName, cur.referenceBody == cur ? "" : cur.referenceBody.bodyName, body.bodyName, body.referenceBody == body ? "" : body.referenceBody.bodyName);
                        if (kind == "here") return "Already orbiting " + body.bodyName + ".";
                        if (kind == "unsupported") return "No direct transfer from " + cur.bodyName + " to " + body.bodyName + " (go via its parent first).";
                        object op;
                        if (kind == "moon") { MjSetTarget(core, body); op = Activator.CreateInstance(MjType(core, "OperationGeneric")); }
                        else if (kind == "interplanetary") { MjSetTarget(core, body); op = Activator.CreateInstance(MjType(core, "OperationInterplanetaryTransfer")); MechJebLink.SetValue(op, 1, "WaitForPhaseAngle"); }
                        else { op = Activator.CreateInstance(MjType(core, "OperationMoonReturn")); MechJebLink.SetValue(op, Num(a, "return_altitude_km", 100) * 1000, "MoonReturnAltitude"); }
                        return MjPlanAndRun(core, op, "Transfer to " + body.bodyName + " (" + kind + ")", true);
                    }
                    case "match_target_plane":
                    {
                        if (FlightGlobals.fetch.VesselTarget == null) return "Set a KSP target (vessel or body) first.";
                        MechJebLink.Call(MechJebLink.Get(core, "Target"), "Set", FlightGlobals.fetch.VesselTarget);
                        var op = Activator.CreateInstance(MjType(core, "OperationPlane")); SetTimeRef(op, "REL_HIGHEST_AD");
                        return MjPlanAndRun(core, op, "Match target plane", true);
                    }
                    case "course_correction":
                    {
                        Orbit o = vessel.orbit;
                        if (FlightGlobals.fetch.VesselTarget == null)
                        {
                            CelestialBody enc = o.nextPatch != null && o.patchEndTransition == Orbit.PatchTransitionType.ENCOUNTER ? o.nextPatch.referenceBody : null;
                            if (enc == null) return "Set the destination body as KSP target (no encounter on the current trajectory).";
                            MjSetTarget(core, enc);
                        }
                        else MechJebLink.Call(MechJebLink.Get(core, "Target"), "Set", FlightGlobals.fetch.VesselTarget);
                        var op = Activator.CreateInstance(MjType(core, "OperationCourseCorrection"));
                        if (!MechJebLink.SetValue(op, Num(a, "periapsis_km", 50) * 1000, "CourseCorrectFinalPeA")) return "MechJeb API mismatch: CourseCorrectFinalPeA";
                        return MjPlanAndRun(core, op, "Course correction to " + Num(a, "periapsis_km", 50).ToString("0", Inv) + " km periapsis", true);
                    }
                    case "apsis_longitude":
                    {
                        if (!InSpace || vessel.orbit.eccentricity >= 1) return "Needs a closed orbit.";
                        double lon = Num(a, "longitude_deg", double.NaN); if (double.IsNaN(lon) || Math.Abs(lon) > 360) return "longitude_deg required.";
                        MechJebLink.Call(MechJebLink.Get(core, "Target"), "SetPositionTarget", vessel.mainBody, 0.0, lon);
                        var op = Activator.CreateInstance(MjType(core, "OperationLongitude")); SetTimeRef(op, "APOAPSIS");
                        return MjPlanAndRun(core, op, "Apoapsis over longitude " + lon.ToString("0.##", Inv), true);
                    }
                    case "station_keep":
                    {
                        if (vessel.situation != Vessel.Situations.ORBITING) return "Get into a stable orbit first.";
                        var b = vessel.mainBody; double syncAlt = OrbitMath.SyncAltitude(b.gravParameter, b.rotationPeriod, b.Radius);
                        if (syncAlt + b.Radius >= b.sphereOfInfluence) return b.bodyName + " rotates too slowly: a synchronous orbit is outside its SOI.";
                        double lon = Num(a, "longitude", 999); string where = "longitude " + lon.ToString("0.##", Inv);
                        if (lon > 360)
                        {
                            var tv = FlightGlobals.fetch.VesselTarget as Vessel;
                            if (tv != null && tv.mainBody == b) { lon = tv.longitude; where = tv.vesselName; } else { lon = -74.5577; where = "KSC"; }
                        }
                        // 1) raise apoapsis to synchronous altitude, 2) shift apoapsis longitude, 3) circularize there - planned as a chain on patched orbits
                        var ap = Activator.CreateInstance(MjType(core, "OperationApoapsis"));
                        MechJebLink.SetValue(ap, syncAlt, "NewApA"); SetTimeRef(ap, "PERIAPSIS");
                        string r1 = MjPlanAndRun(core, ap, "sync apoapsis", true, null, false, false); if (r1 != null) return r1;
                        var solver = vessel.patchedConicSolver; Orbit after = solver.maneuverNodes[solver.maneuverNodes.Count - 1].nextPatch;
                        MechJebLink.Call(MechJebLink.Get(core, "Target"), "SetPositionTarget", b, 0.0, lon);
                        var lop = Activator.CreateInstance(MjType(core, "OperationLongitude")); SetTimeRef(lop, "APOAPSIS");
                        string r2 = MjPlanAndRun(core, lop, "apsis longitude", true, after, true, false); if (r2 != null) return r2;
                        after = solver.maneuverNodes[solver.maneuverNodes.Count - 1].nextPatch;
                        var circ = Activator.CreateInstance(MjType(core, "OperationCircularize")); SetTimeRef(circ, "APOAPSIS");
                        string r3 = MjPlanAndRun(core, circ, "Station-keep over " + where + " (synchronous " + (syncAlt / 1000).ToString("0", Inv) + " km)", true, after, true, true);
                        double inc = vessel.orbit.inclination;
                        return r3 + (Math.Abs(inc) > .5 ? " Note: inclination " + inc.ToString("0.0", Inv) + " deg - the spot drifts north/south daily unless you set inclination 0." : "");
                    }
                    case "launch_to_target_plane":
                    {
                        if (vessel.situation != Vessel.Situations.PRELAUNCH && vessel.situation != Vessel.Situations.LANDED) return "Launch to plane is for a craft on the pad.";
                        var tgt = FlightGlobals.fetch.VesselTarget; if (tgt == null || tgt.GetOrbit() == null) return "Set a KSP target (the station / moon whose plane you want) first.";
                        Orbit to = tgt.GetOrbit(); if (to.referenceBody != vessel.mainBody) return "The target must orbit " + vessel.mainBody.bodyName + ".";
                        var b = vessel.mainBody; double now = Planetarium.GetUniversalTime();
                        Vector3d p = to.getRelativePositionAtUT(now).xzy, v = to.getOrbitalVelocityAtUT(now).xzy;
                        Vector3d site = b.GetWorldSurfacePosition(vessel.latitude, vessel.longitude, 0) - b.position;
                        Vector3d east = (b.GetWorldSurfacePosition(vessel.latitude, vessel.longitude + .01, 0) - b.GetWorldSurfacePosition(vessel.latitude, vessel.longitude, 0)).normalized;
                        Vector3d axis = (b.GetWorldSurfacePosition(90, 0, 0) - b.position).normalized;
                        Vector3d par = axis * Vector3d.Dot(site, axis), perp = site - par;
                        bool north; double wait = MechJebPolicy.LaunchWindow(new[] { Vector3d.Cross(p, v).x, Vector3d.Cross(p, v).y, Vector3d.Cross(p, v).z },
                            new[] { par.x, par.y, par.z }, new[] { perp.x, perp.y, perp.z }, new[] { east.x, east.y, east.z }, new[] { axis.x, axis.y, axis.z }, 2 * Math.PI / b.rotationPeriod, out north);
                        if (double.IsNaN(wait)) return "The target's plane (inclination " + to.inclination.ToString("0.0", Inv) + " deg) never passes over this site.";
                        double alt = Num(a, "target_altitude_km", 80) * 1000, inc = north ? to.inclination : -to.inclination;
                        object settings = MechJebLink.Get(core, "AscentSettings"), asc = MechJebLink.Get(core, "Ascent");
                        if (settings == null || asc == null) return "MechJeb API mismatch: Ascent/AscentSettings";
                        if (!MechJebLink.SetValue(settings, alt, "DesiredOrbitAltitude") || !MechJebLink.SetValue(settings, inc, "DesiredInclination")) return "MechJeb API mismatch: ascent altitude/inclination";
                        MechJebLink.SetValue(settings, 1, "_autostage"); MechJebLink.SetValue(settings, 0, "SkipCircularization");
                        Stop(); SetGroup(vessel, KSPActionGroup.SAS, false);
                        try { MechJebLink.Use(MechJebLink.Get(core, "Staging"), MjOwner); } catch (Exception) { }
                        MechJebLink.Use(asc, MjOwner);
                        MechJebLink.Call(asc, "StartCountdown", now + Math.Max(10, wait));
                        return "MechJeb timed launch into the target's plane in " + FlightResidualPolicy.Duration(Math.Max(10, wait)) + ": inclination " + inc.ToString("0.0", Inv) + " deg, " + (alt / 1000).ToString("0", Inv) + " km, autostage on (MechJeb counts down).";
                    }
                }
            }
            catch (Exception ex) { return name.Replace('_', ' ') + " failed in MechJeb: " + (ex.InnerException ?? ex).Message; }
            return null;
        }
    }
}
