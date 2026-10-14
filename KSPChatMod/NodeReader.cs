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
            LiveRocket live = LiveRocket.Read(v);
            double thrust = live == null ? 0 : live.Thrust * 1000;
            double isp = live != null && live.FuelFlow > 0
                ? live.Thrust / (live.FuelFlow * 9.80665)
                : 0;
            foreach (ManeuverNode node in v.patchedConicSolver.maneuverNodes)
            {
                if (node == null) continue;
                double dv = node.DeltaV.magnitude;
                double burnTime = RocketMath.BurnTime(dv, v.totalMass * 1000, thrust, isp);
                string burn = double.IsNaN(burnTime) || double.IsInfinity(burnTime)
                    ? "n/a"
                    : burnTime.ToString("F0", CultureInfo.InvariantCulture);
                lines.Add("dv " + dv.ToString("F0", CultureInfo.InvariantCulture) +
                    " m/s at T-" + (node.UT - now).ToString("F0", CultureInfo.InvariantCulture) +
                    " s  burn " + burn + " s");
            }
            return lines;
        }
    }
}
