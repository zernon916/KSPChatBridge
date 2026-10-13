using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Expansions.Serenity;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        // All vectors below use Orbit's unswizzled orbital coordinates, including Lambert.
        public bool PlanEjectionNode(CelestialBody target, out string msg)
        {
            msg = "No active vessel or patched-conic solver.";
            var craft = FlightGlobals.ActiveVessel;
            if (craft == null || craft.patchedConicSolver == null || craft.orbit == null) return false;
            var parking = craft.orbit;
            var home = parking.referenceBody;
            if (home == null || home.orbit == null || target == null || target == home || target.orbit == null ||
                home.referenceBody == null || target.referenceBody != home.referenceBody)
            { msg = "Choose another planet orbiting the departure body's parent."; return false; }
            double floor = home.atmosphere ? home.atmosphereDepth : 0;
            if (parking.eccentricity > .02 || parking.PeA <= floor || !EjectionMath.Finite(parking.period) || parking.period <= 0)
            { msg = "Ejection needs a near-circular parking orbit (eccentricity <= 0.02) above terrain and atmosphere."; return false; }
            var solver = craft.patchedConicSolver;
            if (solver.maneuverNodes.Count != 0)
            { msg = "Clear existing maneuver nodes before planning an ejection."; return false; }
            double now = Planetarium.GetUniversalTime(), radius = (parking.ApA+parking.PeA)/2+home.Radius;
            double ignored1, ignored2, travel;
            RocketMath.Hohmann(home.referenceBody.gravParameter, home.orbit.semiMajorAxis,
                target.orbit.semiMajorAxis, out ignored1, out ignored2, out travel);
            double rate = Math.Abs(1/home.orbit.period-1/target.orbit.period);
            double horizon = rate > 1e-12 ? 1/rate : home.orbit.period;
            double best = double.PositiveInfinity, burnUT = 0, arrivalUT = 0;
            Vector3d bestImpulse = Vector3d.zero;
            // Original bounded Lambert search over one synodic window and transfer durations.
            for (int i = 0; i <= 40; i++)
            for (int j = 0; j < 9; j++)
            {
                double depart = now+60+horizon*i/40, tof = travel*(.7+.075*j);
                Vector3d impulse;
                double ut, cost;
                if (!EjectionSeed(parking, home, target, radius, depart, tof, out ut, out impulse, out cost)) continue;
                if (cost < best) { best = cost; burnUT = ut; arrivalUT = ut+tof; bestImpulse = impulse; }
            }
            if (!EjectionMath.Finite(best)) { msg = "No feasible ejection geometry found in the search window."; return false; }
            ManeuverNode node = null;
            try
            {
                node = solver.AddManeuverNode(burnUT);
                Vector3d local = EjectionLocal(parking, burnUT, bestImpulse);
                node.DeltaV = local;
                solver.UpdateFlightPlan();
                bool encounter;
                double closest = EjectionMiss(node.nextPatch, target, burnUT, arrivalUT, out encounter);
                double timeStep = Math.Min(30, parking.period/100), dvStep = 5;
                for (int iteration = 0; iteration < 40 && !encounter; iteration++)
                {
                    bool improved = false;
                    for (int direction = 0; direction < 4 && !encounter; direction++)
                    {
                        double trialUT = burnUT + (direction == 2 ? timeStep : direction == 3 ? -timeStep : 0);
                        if (trialUT <= now+10) continue;
                        Vector3d trial = local;
                        trial.z += direction == 0 ? dvStep : direction == 1 ? -dvStep : 0;
                        node.OnGizmoUpdated(trial, trialUT);
                        solver.UpdateFlightPlan();
                        bool hit;
                        double miss = EjectionMiss(node.nextPatch, target, trialUT, arrivalUT, out hit);
                        if (hit || miss < closest)
                        { local = trial; burnUT = trialUT; closest = miss; encounter = hit; improved = true; }
                        else { node.OnGizmoUpdated(local, burnUT); solver.UpdateFlightPlan(); }
                    }
                    if (!improved) { timeStep *= .5; dvStep *= .5; }
                }
                msg = string.Format(CultureInfo.InvariantCulture,
                    "{0} ejection: {1:F1} m/s, UT {2:F1}; {3}; closest approach {4}.",
                    target.bodyName, local.magnitude, burnUT, encounter ? "target SOI encounter" : "no target SOI encounter after refinement",
                    EjectionMath.Finite(closest) ? (closest/1000).ToString("F1", CultureInfo.InvariantCulture)+" km from center" : "unavailable");
                ChatLog.Write("rocket", msg);
                // A non-encounter candidate remains visible for inspection; false is not a successful encounter.
                return encounter;
            }
            catch (Exception ex)
            {
                if (node != null) { node.RemoveSelf(); solver.UpdateFlightPlan(); }
                msg = "Ejection planning failed: "+ex.Message;
                return false;
            }
        }

        static bool EjectionSeed(Orbit parking, CelestialBody home, CelestialBody target, double radius,
            double departure, double tof, out double ut, out Vector3d impulse, out double cost)
        {
            ut = departure; impulse = Vector3d.zero; cost = double.NaN;
            // Iterate the desired burn longitude against the actual parking-orbit phase.
            for (int pass = 0; pass < 8; pass++)
            {
                Vector3d r = parking.getRelativePositionAtUT(ut), v = parking.getOrbitalVelocityAtUT(ut);
                Vector3d z = Vector3d.Cross(r, v).normalized;
                Vector3d parentV = home.orbit.getOrbitalVelocityAtUT(ut);
                Vector3d x = (parentV-z*Vector3d.Dot(parentV,z)).normalized;
                if (x.sqrMagnitude < .5) return false;
                Vector3d y = Vector3d.Cross(z,x);
                Vector3d start = home.orbit.getRelativePositionAtUT(ut), end = target.orbit.getRelativePositionAtUT(ut+tof);
                double[] first, last;
                if (!Porkchop.Lambert(home.referenceBody.gravParameter, new[] {start.x,start.y,start.z},
                    new[] {end.x,end.y,end.z}, tof, out first, out last)) return false;
                Vector3d excess = new Vector3d(first[0],first[1],first[2])-parentV;
                double angle; double[] point, kick;
                if (!EjectionMath.Solve(home.gravParameter, home.Radius, radius,
                    Vector3d.Dot(excess,x), Vector3d.Dot(excess,y), Vector3d.Dot(excess,z),
                    out cost, out angle, out point, out kick)) return false;
                impulse = x*kick[0]+y*kick[1]+z*kick[2];
                double phase = Math.Atan2(Vector3d.Dot(r,y),Vector3d.Dot(r,x));
                double delta = angle*Math.PI/180-phase;
                delta = Math.Atan2(Math.Sin(delta),Math.Cos(delta));
                if (pass == 0 && delta < 0) delta += 2*Math.PI;
                if (Math.Abs(delta) < 1e-5) return true;
                if (pass == 7) return false;
                ut += delta*parking.period/(2*Math.PI);
                if (ut < departure) ut += parking.period;
            }
            return false;
        }

        static Vector3d EjectionLocal(Orbit orbit, double ut, Vector3d impulse)
        {
            Vector3d r = orbit.getRelativePositionAtUT(ut), p = orbit.getOrbitalVelocityAtUT(ut).normalized;
            Vector3d n = Vector3d.Cross(r,p).normalized, radial = Vector3d.Cross(p,n);
            return new Vector3d(Vector3d.Dot(impulse,radial),Vector3d.Dot(impulse,n),Vector3d.Dot(impulse,p));
        }

        static double EjectionDistance(Orbit patch, CelestialBody target, double ut)
        {
            return (patch.getRelativePositionAtUT(ut)-target.orbit.getRelativePositionAtUT(ut)).magnitude;
        }

        static double EjectionMiss(Orbit patch, CelestialBody target, double burn, double arrival, out bool encounter)
        {
            encounter = false;
            double best = double.PositiveInfinity;
            for (int count = 0; patch != null && count < 5; count++, patch = patch.nextPatch)
            {
                if (patch.referenceBody == target)
                { encounter = true; return Math.Max(0,patch.PeA+target.Radius); }
                if (patch.referenceBody != target.referenceBody) continue;
                double lo = Math.Max(burn,patch.StartUT), hi = arrival+(arrival-burn)*.5;
                if (EjectionMath.Finite(patch.EndUT) && patch.EndUT > lo) hi = Math.Min(hi,patch.EndUT);
                if (hi <= lo) continue;
                double step = (hi-lo)/128, nearest = lo, sampled = double.PositiveInfinity;
                for (int k = 0; k <= 128; k++)
                {
                    double t = lo+k*step, d = EjectionDistance(patch,target,t);
                    if (d < sampled) { sampled = d; nearest = t; }
                }
                double left = Math.Max(lo,nearest-step), right = Math.Min(hi,nearest+step);
                for (int k = 0; k < 32; k++)
                {
                    double a = left+(right-left)/3, b = right-(right-left)/3;
                    if (EjectionDistance(patch,target,a) < EjectionDistance(patch,target,b)) right = b; else left = a;
                }
                best = Math.Min(best,Math.Min(sampled,EjectionDistance(patch,target,(left+right)/2)));
            }
            return best;
        }
    }
}
