using System;

namespace KSPChatBridge
{
    internal static class TouchdownMath
    {
        internal static double Throttle(double localGravity, double verticalSpeed, double mass, double thrust)
        {
            if (!Finite(localGravity) || !Finite(verticalSpeed) || !Finite(mass) || !Finite(thrust) ||
                localGravity < 0 || mass <= 0 || thrust <= 0)
                return 0;

            double throttle = (localGravity + (-2 - verticalSpeed)) * mass / thrust;
            if (!Finite(throttle)) return 0;
            return Math.Max(0, Math.Min(1, throttle));
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
