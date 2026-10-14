using System;

namespace KSPChatBridge
{
    internal static class PlanetViewMath
    {
        // Fit a sphere with a 15% border, including narrow portrait viewports.
        internal static double Distance(double radius, double verticalFovDeg, double aspect)
        {
            if (!Finite(radius) || !Finite(verticalFovDeg) || !Finite(aspect) ||
                radius <= 0 || aspect <= 0 || verticalFovDeg <= 0 || verticalFovDeg >= 179)
                return double.NaN;
            double half = verticalFovDeg * Math.PI / 360;
            double limiting = Math.Min(half, Math.Atan(Math.Tan(half) * aspect));
            return radius * 1.15 / Math.Sin(limiting);
        }

        internal static double Wrap(double degrees)
        {
            return Finite(degrees) ? (degrees % 360 + 360) % 360 : 0;
        }

        static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
    }
}
