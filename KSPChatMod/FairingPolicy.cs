using System;

namespace KSPChatBridge
{
    internal static class FairingPolicy
    {
        internal static string CanDeploy(double qKpa, double altM, double heatRatio)
        {
            if (heatRatio > 0.6) return null;
            if (qKpa < 1) return "q low";
            if (altM > 40000) return "above 40 km";
            return null;
        }
    }
}
