using System;

namespace KSPChatBridge
{
    /// <summary>Pure rocket math for Hohmann transfer (no MechJeb needed to plan; MechJeb optional to execute).</summary>
    internal static class RocketMath
    {
        /// <summary>Hohmann transfer: semi-major axis, delta-v at r1 and r2, time of flight.</summary>
        internal static void Hohmann(double mu, double r1, double r2, out double dv1, out double dv2, out double time)
        {
            if (mu <= 0 || r1 <= 0 || r2 <= 0)
            {
                dv1 = double.NaN;
                dv2 = double.NaN;
                time = double.NaN;
                return;
            }

            double a = (r1 + r2) / 2.0;
            dv1 = Math.Sqrt(mu / r1) * (Math.Sqrt(2.0 * r2 / (r1 + r2)) - 1.0);
            dv2 = Math.Sqrt(mu / r2) * (1.0 - Math.Sqrt(2.0 * r1 / (r1 + r2)));
            time = Math.PI * Math.Sqrt(Math.Pow(a, 3.0) / mu);
        }
    }
}
