using System;

namespace KSPChatBridge
{
    internal static class EncyclopediaMath
    {
        internal static double IdealLandingDeltaV(double gravitationalParameter, double bodyRadius,
            double peakAltitude, double clearance)
        {
            if (!IsFinite(gravitationalParameter) || gravitationalParameter <= 0 ||
                !IsFinite(bodyRadius) || bodyRadius <= 0 ||
                !IsFinite(peakAltitude) || peakAltitude < 0 ||
                !IsFinite(clearance) || clearance < 0)
                return double.NaN;

            double orbitRadius = bodyRadius + peakAltitude + clearance;
            if (!IsFinite(orbitRadius) || orbitRadius <= 0) return double.NaN;
            return Math.Sqrt(gravitationalParameter / orbitRadius);
        }

        static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
