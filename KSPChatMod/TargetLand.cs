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
        double landTargetLat, landTargetLon;
        bool landTargetSet;
        Vessel targetLandCraft;
        bool targetLandPlanned, targetLandFinal;
        NativeVerticalLanding targetLandVacuum;
        TargetLandSolution targetLandSolution;
        double targetLandNextSample;
        double targetLandPitchError, targetLandYawError;
        bool targetLandSteering;

        // MapWindow.Unproj supplies degrees. Map/Fly/Command wiring is separate.
        public bool SetLandTarget(double lat, double lon)
        {
            if (!TargetLandMath.Finite(lat) || !TargetLandMath.Finite(lon) || Math.Abs(lat) > 90) return false;
            landTargetLat = lat; landTargetLon = TargetLandMath.Wrap(lon); landTargetSet = true;
            ResetTargetLanding();
            return true;
        }

        void ResetTargetLanding()
        {
            targetLandPlanned = targetLandFinal = targetLandSteering = false;
            targetLandVacuum = null; targetLandSolution = null; targetLandNextSample = 0;
            // Reset the atmospheric controller's per-engagement estimator/latch too.
            atmoLandCraft = null;
        }

        internal bool TargetLandStep(Vessel v, FlightCtrlState c, out string status)
        {
            status = "Select a landing target first.";
            if (c == null) return false;
            c.mainThrottle = 0; c.pitch = c.yaw = c.roll = 0;
            if (!landTargetSet) return false;
            if (v == null || v.mainBody == null || v.mainBody.transform == null || v.orbit == null ||
                v.ReferenceTransform == null || v.parts == null)
            { status = "Target landing needs live vessel geometry."; return false; }
            try
            {
                if (targetLandCraft != v) { targetLandCraft = v; ResetTargetLanding(); }
                if (v.LandedOrSplashed) { status = "Target landing: touchdown."; ResetTargetLanding(); return true; }
                double now = Planetarium.GetUniversalTime();
                var body = v.mainBody;
                double rotation = body.rotates && body.rotationPeriod != 0 ? 2 * Math.PI / body.rotationPeriod : 0;
                double agl = v.radarAltitude;
                if (!TargetLandMath.Finite(agl) || agl < 0) agl = v.heightFromTerrain;
                if (!TargetLandMath.Finite(now) || !TargetLandMath.Finite(agl) || agl < 0)
                { status = "Target landing: AGL/time unavailable."; return false; }
                if (targetLandSolution == null || now >= targetLandNextSample)
                {
                    targetLandSolution = TargetLandMath.Solve(body.Radius, body.gravParameter, rotation, v.latitude, v.longitude,
                        Math.Max(0, v.altitude), Vector3d.Dot(v.obt_velocity, v.east), Vector3d.Dot(v.obt_velocity, v.north),
                        Vector3d.Dot(v.obt_velocity, v.upAxis), landTargetLat, landTargetLon, maxDv: 100);
                    targetLandNextSample = now + 1;
                }
                if (!targetLandFinal && targetLandSolution.Impact == null)
                {
                    if (targetLandPlanned)
                    { status = "Deorbit node planned; execute it separately. Current trajectory does not intersect the surface."; return true; }
                    return PlanTargetDeorbit(v, now, rotation, out status);
                }

                double acceleration = 0;
                // Reuses the existing live, throttleable, aligned-engine reader.
                // Chute-only atmospheric landings can proceed without an engine.
                try { acceleration = NativeVerticalLanding.Acceleration(v); }
                catch (ArgumentException) { }
                double radius = body.Radius + v.altitude, gravity = body.gravParameter / (radius * radius);
                double burnHeight = SuicideBurn.StartAlt(Math.Max(0, -v.verticalSpeed), gravity, acceleration, 1, .25);
                if (v.verticalSpeed < 0 && (agl <= (body.atmosphere ? 10000 : 500) ||
                    (acceleration > 0 && agl <= burnHeight * 1.5))) targetLandFinal = true;
                if (targetLandFinal)
                {
                    if (body.atmosphere) return AtmoLandStep(v, c, out status);
                    if (targetLandVacuum == null)
                    { targetLandVacuum = new NativeVerticalLanding(v, 2, now); ChatLog.Write("rocket", "Target landing: final vacuum descent."); }
                    targetLandVacuum.Fly(v, c, now, Math.Max(.001, TimeWarp.fixedDeltaTime));
                    if (agl < 200 && v.ActionGroups != null) v.ActionGroups.SetGroup(KSPActionGroup.Gear, true);
                    status = "Target landing: " + targetLandVacuum.Phase;
                    return targetLandVacuum.Phase != "abort";
                }

                Vector3d retro = v.srfSpeed > .5 ? -v.srf_velocity.normalized : v.upAxis;
                Vector3d correction = v.east * targetLandSolution.DvEast + v.north * targetLandSolution.DvNorth + v.upAxis * targetLandSolution.DvUp;
                Vector3d side = Vector3d.Exclude(retro, correction);
                // Limit the steering offset to about eight degrees. Only thrust
                // when the achievable direction agrees with the solved correction.
                Vector3d desired = (retro + (side.sqrMagnitude > 1e-8 ? side.normalized * .14 : Vector3d.zero)).normalized;
                double pe = Vector3d.Dot(desired, -v.ReferenceTransform.forward), ye = Vector3d.Dot(desired, v.ReferenceTransform.right);
                double dt = Math.Max(.001, TimeWarp.fixedDeltaTime);
                double pr = targetLandSteering ? (pe - targetLandPitchError) / dt : 0;
                double yr = targetLandSteering ? (ye - targetLandYawError) / dt : 0;
                targetLandPitchError = pe; targetLandYawError = ye; targetLandSteering = true;
                c.pitch = (float)Math.Max(-1, Math.Min(1, 2 * pe + .6 * pr));
                c.yaw = (float)Math.Max(-1, Math.Min(1, 2 * ye + .6 * yr));
                double useful = Vector3d.Dot(correction, desired);
                if (acceleration > 0 && useful > .1 && Vector3d.Dot(desired, v.ReferenceTransform.up) > .98)
                    c.mainThrottle = (float)Math.Min(.2, useful / (acceleration * 5));
                status = "Target descent: predicted miss " + targetLandSolution.Impact.Error.ToString("F0", CultureInfo.InvariantCulture) +
                    " m (spherical vacuum estimate); bounded retrograde correction.";
                return true;
            }
            catch (Exception ex)
            {
                c.mainThrottle = 0; c.pitch = c.yaw = c.roll = 0;
                status = "Target landing stopped: " + ex.Message;
                return false;
            }
        }

        bool PlanTargetDeorbit(Vessel v, double now, double rotation, out string status)
        {
            status = "Target deorbit needs an empty maneuver plan and a bound orbit.";
            var solver = v.patchedConicSolver;
            if (solver == null || solver.maneuverNodes == null || solver.maneuverNodes.Count != 0 ||
                v.orbit.eccentricity >= 1 || !TargetLandMath.Finite(v.orbit.period) || v.orbit.period <= 0) return false;
            var body = v.mainBody;
            double bestError = double.PositiveInfinity, bestUT = 0;
            Vector3d bestImpulse = Vector3d.zero;
            // A bounded one-orbit search. Node execution remains a separate action.
            for (int i = 0; i < 12; i++)
            {
                double ut = now + 30 + v.orbit.period * i / 12;
                Vector3d relative = v.orbit.getRelativePositionAtUT(ut).xzy;
                Vector3d velocity = v.orbit.getOrbitalVelocityAtUT(ut).xzy;
                Vector3d up = relative.normalized;
                Vector3d east = Vector3d.Cross(body.transform.up, up).normalized;
                if (east.sqrMagnitude < .5) continue;
                Vector3d north = Vector3d.Cross(up, east).normalized;
                Vector3d position = body.position + relative;
                double lat = body.GetLatitude(position), lon = TargetLandMath.Wrap(body.GetLongitude(position) - rotation * (ut - now) * 180 / Math.PI);
                var plan = TargetLandMath.Solve(body.Radius, body.gravParameter, rotation, lat, lon, Math.Max(0, relative.magnitude - body.Radius),
                    Vector3d.Dot(velocity, east), Vector3d.Dot(velocity, north), Vector3d.Dot(velocity, up), landTargetLat, landTargetLon);
                if (plan.CorrectedImpact == null || plan.CorrectedImpact.Error >= bestError) continue;
                bestError = plan.CorrectedImpact.Error; bestUT = ut;
                bestImpulse = east * plan.DvEast + north * plan.DvNorth + up * plan.DvUp;
            }
            if (!TargetLandMath.Finite(bestError)) { status = "No surface-reaching deorbit found within the 500 m/s search budget."; return false; }
            ManeuverNode node = null;
            try
            {
                node = solver.AddManeuverNode(bestUT);
                if (node == null) { status = "Target deorbit node creation failed."; return false; }
                // EjectionNode's helper uses unswizzled orbital coordinates.
                node.DeltaV = EjectionLocal(v.orbit, bestUT, bestImpulse.xzy);
                solver.UpdateFlightPlan(); targetLandPlanned = true;
                status = "Deorbit node ready; predicted miss " + bestError.ToString("F0", CultureInfo.InvariantCulture) +
                    " m. Execute separately; terrain and atmospheric drag are not modeled by this node.";
                ChatLog.Write("rocket", status);
                return true;
            }
            catch
            {
                if (node != null) node.RemoveSelf();
                throw;
            }
        }
    }
}
