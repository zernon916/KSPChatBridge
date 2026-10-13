using System;

namespace KSPChatBridge
{
    internal static class AscentProfile
    {
        /// <summary>Calculates the recommended speed (m/s) to begin an ascent turn based on TWR.</summary>
        internal static double TurnStartSpeed(double twr)
        {
            double val = 100.0 - (twr - 1.2) * 40.0;
            return Math.Max(40.0, Math.Min(val, 100.0));
        }
    }
}
