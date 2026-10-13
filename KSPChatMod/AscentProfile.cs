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

        internal static double PitchDeg(double alt, double turnStartAlt, double turnEndAlt, double twr)
        {
            if (alt < turnStartAlt) return 90.0;
            if (alt > turnEndAlt) return 0.0;
            
            double f = (alt - turnStartAlt) / (turnEndAlt - turnStartAlt);
            double s = Math.Min(0.8, Math.Max(0.4, 0.4 + 0.15 * twr));
            return 90.0 * (1.0 - Math.Pow(f, s));
        }
    }
}
