using System.Globalization;

namespace KSPChatBridge
{
    internal static class NodeWriter
    {
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
