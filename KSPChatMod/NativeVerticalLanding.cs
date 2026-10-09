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
                acceleration, Gravity(vessel), Vector3d.Dot(vessel.ReferenceTransform.up, vessel.upAxis), vessel.ctrlState.mainThrottle);
            if (error != null) throw new ArgumentException(error);
            policy = new VerticalLandingPolicy(vessel.ctrlState.mainThrottle, touchdown, now);
        }
        static double Height(Vessel v)
        {
            // Use the game's surface clearance query, including its lowest-part offset.
            return Math.Min(v.radarAltitude, v.GetHeightFromSurface());
        }
        static double Gravity(Vessel v) { return v.mainBody.gravParameter / Math.Pow(v.mainBody.Radius + v.altitude, 2); }
        static double Acceleration(Vessel v)
        {
            double thrust = 0;
            foreach (Part part in v.parts) foreach (var engine in part.FindModulesImplementing<ModuleEngines>())
            {
                if (!engine.EngineIgnited) continue;
                var multi = part.FindModuleImplementing<MultiModeEngine>();
                if (multi != null && engine.engineID != (multi.runningPrimary ? multi.primaryEngineID : multi.secondaryEngineID)) continue;
                if (engine.throttleLocked || engine.useEngineResponseTime || engine.thrustTransforms.Count == 0)
                    throw new ArgumentException("Powered descent requires throttleable immediate-response engines with known thrust axes.");
                foreach (var transform in engine.thrustTransforms)
                    if (transform == null || Vector3.Dot(-transform.forward, v.ReferenceTransform.up) < .98)
                        throw new ArgumentException("Engine thrust must align with the active control point for powered descent.");
                // Infer capacity only from actual running thrust; no assumed sea-level/vacuum rating.
                if (engine.currentThrottle < .01 || engine.finalThrust <= 0) continue;
                thrust += engine.finalThrust / engine.currentThrottle;
            }
            return thrust / v.GetTotalMass();
        }
        internal void Fly(Vessel v, FlightCtrlState controls, double now, double dt)
        {
            double acceleration = Acceleration(v);
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
            controls.mainThrottle = (float)policy.Step(now, Height(v), v.verticalSpeed, acceleration, Gravity(v),
                Vector3d.Dot(v.ReferenceTransform.up, v.upAxis), v.LandedOrSplashed);
        }
    }
}
