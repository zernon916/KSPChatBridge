using System;

namespace KSPChatBridge
{
    internal static class AbortPolicy
    {
        internal static string Check(double angRateRadS, double attErrDeg, double thrustDropFrac)
        {
            if (angRateRadS > 1.5 && attErrDeg > 45) return "tumbling";
            if (thrustDropFrac > 0.5) return "thrust loss";
            return null;
        }
    }
}
