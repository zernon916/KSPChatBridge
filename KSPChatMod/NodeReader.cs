using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal static class NodeReader
    {
        internal static List<string> Lines(Vessel v)
        {
            List<string> lines = new List<string>();
            if (v == null || v.patchedConicSolver == null || v.patchedConicSolver.maneuverNodes == null)
                return lines;

            double now = Planetarium.GetUniversalTime();
            foreach (ManeuverNode node in v.patchedConicSolver.maneuverNodes)
            {
                if (node == null) continue;
                lines.Add("dv " + node.DeltaV.magnitude.ToString("F0", CultureInfo.InvariantCulture) +
                    " m/s at T-" + (node.UT - now).ToString("F0", CultureInfo.InvariantCulture) + " s");
            }
            return lines;
        }
    }
}
