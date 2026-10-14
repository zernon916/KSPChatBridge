using System;

namespace KSPChatBridge
{
    internal static class LegBudget
    {
        internal static double LaunchDv(double mu, double radius, double orbitAlt, double atmoDepth)
        {
            double v = Math.Sqrt(mu / (radius + orbitAlt));
            // Rough estimate for atmospheric and launch losses.
            double loss = atmoDepth > 0 ? 1.15 + atmoDepth / 70000 * 0.35 : 1.05;
            return v * loss;
        }
    }
}
