using System;
using System.Globalization;

namespace KSPChatBridge
{
    internal static class NodeWriter
    {
        internal static string TransferTo(Vessel v, CelestialBody target)
        {
            if (v == null || v.orbit == null || target == null || target.orbit == null)
                return "Cannot create transfer node: orbit unavailable.";

            Orbit orbit = v.orbit;
            CelestialBody primary = orbit.referenceBody;
            if (primary == null || target.orbit.referenceBody != primary)
                return "different SOI";

            double sourceRadius = orbit.semiMajorAxis;
            double targetRadius = target.orbit.semiMajorAxis;
            double mu = primary.gravParameter;
            if (sourceRadius <= 0 || targetRadius <= 0 || mu <= 0)
                return "Cannot create transfer node: orbit values are invalid.";

            double dv1, dv2, transferTime;
            RocketMath.Hohmann(mu, sourceRadius, targetRadius, out dv1, out dv2, out transferTime);
            double targetPhase = RocketMath.PhaseAngleDeg(mu, sourceRadius, targetRadius);
            double now = Planetarium.GetUniversalTime();
            Vector3d sourcePosition = orbit.getRelativePositionAtUT(now);
            Vector3d targetPosition = target.orbit.getRelativePositionAtUT(now);
            Vector3d normal = Vector3d.Cross(sourcePosition, orbit.getOrbitalVelocityAtUT(now)).normalized;
            Vector3d sourceDirection = sourcePosition - normal * Vector3d.Dot(sourcePosition, normal);
            Vector3d targetDirection = targetPosition - normal * Vector3d.Dot(targetPosition, normal);
            if (normal.sqrMagnitude < .5 || sourceDirection.sqrMagnitude < 1 ||
                targetDirection.sqrMagnitude < 1 || double.IsNaN(dv1) || double.IsInfinity(dv1) ||
                double.IsNaN(transferTime) || double.IsInfinity(transferTime))
                return "Cannot create transfer node: orbit values are invalid.";

            double currentPhase = Math.Atan2(
                Vector3d.Dot(normal, Vector3d.Cross(sourceDirection, targetDirection)),
                Vector3d.Dot(sourceDirection, targetDirection)) * 180.0 / Math.PI;
            double sourceRate = Math.Sqrt(mu / Math.Pow(sourceRadius, 3));
            double targetRate = Math.Sqrt(mu / Math.Pow(targetRadius, 3));
            double wait = WindowFinder.SecondsToPhase(currentPhase, targetPhase, sourceRate, targetRate);
            if (double.IsNaN(wait) || double.IsInfinity(wait))
                return "Cannot create transfer node: no transfer window.";

            if (Add(v, now + wait, 0, 0, dv1) == null)
                return "Cannot create transfer node.";
            return "Hohmann transfer node added for " + target.bodyName + ".";
        }

        internal static string CaptureAtPe(Vessel v, double apTarget)
        {
            if (v == null || v.orbit == null || v.orbit.referenceBody == null)
                return "Cannot create capture node: orbit unavailable.";

            Orbit orbit = v.orbit;
            double mu = orbit.referenceBody.gravParameter;
            double periapsis = orbit.PeR;
            if (mu <= 0 || periapsis <= 0 || apTarget <= periapsis)
                return "Cannot create capture node: orbit values are invalid.";

            double speed = orbit.getOrbitalSpeedAt(periapsis);
            double escapeSpeed = Math.Sqrt(2.0 * mu / periapsis);
            double excessSquared = speed * speed - escapeSpeed * escapeSpeed;
            if (excessSquared < 0 || double.IsNaN(excessSquared) || double.IsInfinity(excessSquared))
                return "Cannot create capture node: orbit values are invalid.";

            double dv = -RocketMath.CaptureDv(mu, periapsis, Math.Sqrt(excessSquared), apTarget);
            if (double.IsNaN(dv) || double.IsInfinity(dv))
                return "Cannot create capture node: orbit values are invalid.";

            double ut = Planetarium.GetUniversalTime() + orbit.timeToPe;
            if (Add(v, ut, 0, 0, dv) == null)
                return "Cannot create capture node.";
            return "Capture node added at periapsis.";
        }

        internal static string CircularizeAtAp(Vessel v)
        {
            if (v == null || v.orbit == null || v.orbit.referenceBody == null)
                return "Cannot create circularization node: orbit unavailable.";

            Orbit orbit = v.orbit;
            double dv = RocketMath.CircularizeAtAp(orbit.referenceBody.gravParameter, orbit.ApR, orbit.PeR);
            if (double.IsNaN(dv) || double.IsInfinity(dv))
                return "Cannot create circularization node: orbit values are invalid.";

            double ut = Planetarium.GetUniversalTime() + orbit.timeToAp;
            if (Add(v, ut, 0, 0, dv) == null)
                return "Cannot create circularization node.";
            return "Circularization node added at apoapsis.";
        }

        internal static ManeuverNode Add(Vessel v, double ut, double radial, double normal, double prograde)
        {
            if (v == null || v.patchedConicSolver == null) return null;

            ManeuverNode node = v.patchedConicSolver.AddManeuverNode(ut);
            if (node == null) return null;
            node.DeltaV = new Vector3d(radial, normal, prograde);
            v.patchedConicSolver.UpdateFlightPlan();
            ChatLog.Write("rocket", "node at UT " + ut.ToString("F1", CultureInfo.InvariantCulture) +
                " dv " + node.DeltaV.magnitude.ToString("F1", CultureInfo.InvariantCulture));
            return node;
        }
    }
}
