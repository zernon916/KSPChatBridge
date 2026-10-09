using System;
using UnityEngine;

namespace KSPChatBridge
{
    internal sealed class NativeVerticalLanding
    {
        readonly VerticalLandingPolicy policy;
        double previousPitchError, previousYawError;
        bool sampled;
        internal string Phase { get { return policy.Phase; } }
        internal NativeVerticalLanding(Vessel vessel, double touchdown, double now)
        {
            double acceleration = Acceleration(vessel);
            double horizontal = Vector3d.Exclude(vessel.upAxis, vessel.srf_velocity).magnitude;
            string error = VerticalLandingPolicy.Feasibility(Height(vessel), vessel.verticalSpeed, horizontal,
                acceleration, Gravity(vessel), Vector3d.Dot(vessel.ReferenceTransform.up, vessel.upAxis), Math.Max(.05, vessel.ctrlState.mainThrottle));
            if (error != null) throw new ArgumentException(error);
            policy = new VerticalLandingPolicy(Math.Max(.05, vessel.ctrlState.mainThrottle), touchdown, now);
        }
        static double Height(Vessel v)
        {
            // Use the game's surface clearance query, including its lowest-part offset.
            return Math.Min(v.radarAltitude, v.GetHeightFromSurface());
        }
        static double Gravity(Vessel v) { return v.mainBody.gravParameter / Math.Pow(v.mainBody.Radius + v.altitude, 2); }
        internal static double Acceleration(Vessel v)
        {
            double thrust = 0; int engines = 0;
            foreach (Part part in v.parts) foreach (var engine in part.FindModulesImplementing<ModuleEngines>())
            {
                if (!engine.EngineIgnited || engine.flameout) continue;
                var multi = part.FindModuleImplementing<MultiModeEngine>();
                if (multi != null && engine.engineID != (multi.runningPrimary ? multi.primaryEngineID : multi.secondaryEngineID)) continue;
                if (engine.throttleLocked || engine.useEngineResponseTime || engine.thrustTransforms.Count == 0)
                    throw new ArgumentException("Powered descent requires throttleable immediate-response engines with known thrust axes.");
                foreach (var transform in engine.thrustTransforms)
                    if (transform == null || Vector3.Dot(-transform.forward, v.ReferenceTransform.up) < .98)
                        throw new ArgumentException("Engine thrust must align with the active control point for powered descent.");
                // Prefer measured capacity; fall back to rated max thrust when still at idle.
                double capacity = engine.currentThrottle >= .01 && engine.finalThrust > 0
                    ? engine.finalThrust / engine.currentThrottle
                    : engine.maxThrust;
                if (capacity <= 0) continue;
                thrust += capacity; engines++;
            }
            if (engines == 0) throw new ArgumentException("Powered descent needs at least one ignited aligned engine.");
            return thrust / v.GetTotalMass();
        }
        internal static double TerrainPeakAgl(Vessel v)
        {
            try
            {
                var body = v.mainBody;
                if (body.pqsController == null) return double.NaN;
                double peak = NavigationMath.TerrainAhead(v.latitude, v.longitude, FlightGlobals.ship_heading, Math.Max(5, v.horizontalSrfSpeed), body.Radius,
                    (lat, lon) => Math.Max(0, body.pqsController.GetSurfaceHeight(body.GetRelSurfaceNVector(lat, lon)) - body.Radius));
                if (double.IsNaN(peak)) return double.NaN;
                // Excess elevation of look-ahead peaks above the ground currently under the craft.
                return peak - (v.altitude - Height(v));
            }
            catch (Exception) { return double.NaN; }
        }
        internal void Fly(Vessel v, FlightCtrlState controls, double now, double dt)
        {
            double acceleration;
            try { acceleration = Acceleration(v); }
            catch (Exception)
            {
                controls.mainThrottle = (float)policy.Step(now, Height(v), v.verticalSpeed, 0, Gravity(v),
                    Vector3d.Dot(v.ReferenceTransform.up, v.upAxis), v.LandedOrSplashed);
                controls.pitch = controls.yaw = controls.roll = 0;
                return;
            }
            Vector3d lateral = Vector3d.Exclude(v.upAxis, v.srf_velocity);
            // Small tilt damps drift; this mode never navigates to a remote landing point.
            Vector3d correction = lateral * .04;
            if (correction.magnitude > .15) correction = correction.normalized * .15;
            Vector3d desired = (v.upAxis - correction).normalized;
            double pitchError = Vector3d.Dot(desired, -v.ReferenceTransform.forward);
            double yawError = Vector3d.Dot(desired, v.ReferenceTransform.right);
            double pitchRate = sampled ? (pitchError - previousPitchError) / dt : 0;
            double yawRate = sampled ? (yawError - previousYawError) / dt : 0;
            previousPitchError = pitchError; previousYawError = yawError; sampled = true;
            controls.pitch = (float)FlightPolicy.Clamp(2 * pitchError + .6 * pitchRate, -1, 1);
            controls.yaw = (float)FlightPolicy.Clamp(2 * yawError + .6 * yawRate, -1, 1);
            controls.roll = 0;
            double clearance = VerticalLandingPolicy.ClearanceFloor(Height(v), TerrainPeakAgl(v));
            controls.mainThrottle = (float)policy.Step(now, clearance, v.verticalSpeed, acceleration, Gravity(v),
                Vector3d.Dot(v.ReferenceTransform.up, v.upAxis), v.LandedOrSplashed);
        }
    }
}
