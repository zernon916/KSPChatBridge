using System.Globalization;

namespace KSPChatBridge
{
    internal static class NodeWriter
    {
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
