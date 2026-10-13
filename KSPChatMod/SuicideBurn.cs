using System;

namespace KSPChatBridge
{
    internal static class SuicideBurn
    {
        internal static double StartAlt(double vDown, double g, double thrust, double mass, double margin)
        {
            double a = thrust / mass - g;
            if (a <= 0) return double.PositiveInfinity;
            return vDown * vDown / (2 * a) * (1 + margin);
        }
    }
}
