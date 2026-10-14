using System;

namespace KSPChatBridge
{
    internal static class DvMapMath
    {
        internal static bool TryHohmann(double mu, double sourceRadius, double destinationRadius,
            out double departureDv, out double arrivalDv, out double transferSeconds)
        {
            departureDv = arrivalDv = transferSeconds = double.NaN;
            if (!Finite(mu) || !Finite(sourceRadius) || !Finite(destinationRadius) ||
                mu <= 0 || sourceRadius <= 0 || destinationRadius <= 0)
                return false;

            RocketMath.Hohmann(mu, sourceRadius, destinationRadius, out departureDv, out arrivalDv, out transferSeconds);
            return Finite(departureDv) && Finite(arrivalDv) && Finite(transferSeconds) && transferSeconds > 0;
        }

        internal static double TotalDeltaV(double departureDv, double arrivalDv)
        {
            if (!Finite(departureDv) || !Finite(arrivalDv)) return double.NaN;
            double total = Math.Abs(departureDv) + Math.Abs(arrivalDv);
            return Finite(total) ? total : double.NaN;
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
