using System;

namespace KSPChatBridge
{
    // MechJeb planner decisions (pure). Coded against MechJeb2 2.15.3.1 (assembly 2.15.0.0) found in Luke's GameData.
    internal static class MechJebPolicy
    {
        internal const string Missing = "needs MechJeb: install MechJeb2 (and have a MechJeb part or the tech unlocked) for this.";
        internal const string BuiltAgainst = "2.15.3.1";
        internal static readonly Version MinSupported = new Version(2, 15);

        internal static string VersionGate(Version v)
        {
            if (v == null) return Missing;
            if (v.Major != 2 || v < MinSupported) return "Unsupported MechJeb version " + v + " (AICS planners are built against MechJeb " + BuiltAgainst + "); update MechJeb2.";
            return null;
        }

        /// <summary>transfer_to: "moon" (target orbits our body), "return" (target is our parent), "interplanetary"
        /// (target is a sibling of our body), "here", or "unsupported".</summary>
        internal static string TransferKind(string current, string currentParent, string target, string targetParent)
        {
            if (string.IsNullOrEmpty(target)) return "unsupported";
            if (target == current) return "here";
            if (targetParent == current) return "moon";
            if (target == currentParent) return "return";
            if (!string.IsNullOrEmpty(currentParent) && targetParent == currentParent) return "interplanetary";
            return "unsupported";
        }

        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        internal static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

        /// <summary>Seconds until the rotating launch site crosses the target plane (normal n = pos x vel), scanning one
        /// rotation. Site r(t) = rPar + |rPerp| (cos wt * uPerp + sin wt * east). north = target moves toward the north
        /// axis there (launch with +inclination). NaN when the plane never passes over the site (|lat| > inclination).</summary>
        internal static double LaunchWindow(double[] n, double[] rPar, double[] rPerp, double[] east, double[] northAxis, double w, out bool north)
        {
            north = true;
            double len = Math.Sqrt(Dot(rPerp, rPerp)); if (len <= 0 || w <= 0) return double.NaN;
            double[] u = { rPerp[0] / len, rPerp[1] / len, rPerp[2] / len };
            Func<double, double[]> at = t => new[] {
                rPar[0] + len * (Math.Cos(w * t) * u[0] + Math.Sin(w * t) * east[0]),
                rPar[1] + len * (Math.Cos(w * t) * u[1] + Math.Sin(w * t) * east[1]),
                rPar[2] + len * (Math.Cos(w * t) * u[2] + Math.Sin(w * t) * east[2]) };
            double period = 2 * Math.PI / w, step = period / 720, prev = Dot(n, at(0));
            for (double t = step; t <= period + step; t += step)
            {
                double cur = Dot(n, at(t));
                if (prev == 0 || Math.Sign(cur) != Math.Sign(prev))
                {
                    double lo = t - step, hi = t;
                    for (int i = 0; i < 60; i++) { double mid = (lo + hi) / 2; if (Math.Sign(Dot(n, at(mid))) == Math.Sign(Dot(n, at(lo)))) lo = mid; else hi = mid; }
                    north = Dot(Cross(n, at(hi)), northAxis) > 0;
                    return hi;
                }
                prev = cur;
            }
            return double.NaN;
        }
    }
}
