using System;

namespace KSPChatBridge
{
    internal static class WindowFinder
    {
        internal static double SecondsToPhase(double currentDeg, double targetDeg, double n1, double n2)
        {
            double relativeRateDeg = (n2 - n1) * 180 / Math.PI;
            if (relativeRateDeg == 0) return double.PositiveInfinity;

            double wait = (targetDeg - currentDeg) / relativeRateDeg;
            double period = 360 / Math.Abs(relativeRateDeg);
            while (wait < 0) wait += period;
            return wait;
        }
    }
}
