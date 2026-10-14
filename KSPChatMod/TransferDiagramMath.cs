using System;

namespace KSPChatBridge
{
    internal static class TransferDiagramMath
    {
        internal static bool TryPoint(double radius, double angle, out double x, out double y)
        {
            x = y = double.NaN;
            if (!Finite(radius) || !Finite(angle) || radius <= 0) return false;
            x = radius * Math.Cos(angle);
            y = radius * Math.Sin(angle);
            return Finite(x) && Finite(y);
        }

        internal static bool TryHohmannPoint(double sourceRadius, double destinationRadius, double startAngle,
            double fraction, out double x, out double y)
        {
            x = y = double.NaN;
            if (!Finite(sourceRadius) || !Finite(destinationRadius) || !Finite(startAngle) || !Finite(fraction) ||
                sourceRadius <= 0 || destinationRadius <= 0 || fraction < 0 || fraction > 1)
                return false;

            double a = (sourceRadius + destinationRadius) / 2;
            double eccentricity = Math.Abs(destinationRadius - sourceRadius) / (sourceRadius + destinationRadius);
            double startTrueAnomaly = sourceRadius <= destinationRadius ? 0 : Math.PI;
            double trueAnomaly = startTrueAnomaly + Math.PI * fraction;
            double radius = a * (1 - eccentricity * eccentricity) /
                (1 + eccentricity * Math.Cos(trueAnomaly));
            double angle = startAngle + Math.PI * fraction;
            return TryPoint(radius, angle, out x, out y);
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
