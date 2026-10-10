using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>POST-TESTING flight extras (hold_pattern, follow_terrain, fuel_check_return, formation): pure math/policy.
    /// Luke's flying rules: bank max 20 deg slow / 10 deg fast, never trade altitude for speed, throttle in 5% steps.</summary>
    internal static class FlightExtrasPolicy
    {
        internal const double FastSpeed = 200;   // m/s: above this, 10 deg bank cap
        internal static double MaxBank(double speed) { return speed > FastSpeed ? 10 : 20; }
        internal const double MinAgl = 50, MaxAgl = 5000, DescentStep = 8;   // m per 1 s tick: descend gently, climb at once

        /// <summary>follow_terrain altitude target (MSL): agl above the higher of the ground below and the highest ground ahead.
        /// Climbs at once; descends at most DescentStep per tick from the previous target (never dive for speed).</summary>
        internal static double TerrainTarget(double groundBelow, double highestAhead, double agl, double previous)
        {
            double ground = double.IsNaN(highestAhead) ? groundBelow : Math.Max(groundBelow, highestAhead);
            double t = Math.Max(0, ground) + ClampAgl(agl);
            if (!double.IsNaN(previous) && t < previous - DescentStep) t = previous - DescentStep;
            return t;
        }
        internal static double ClampAgl(double agl) { return Math.Max(MinAgl, Math.Min(MaxAgl, agl)); }
        /// <summary>The terrain-floor safety climb margin while following terrain (below the requested AGL so they don't fight).</summary>
        internal static double FloorMargin(double agl) { return Math.Max(30, .6 * ClampAgl(agl)); }

        internal static double Norm(double h) { h %= 360; return h < 0 ? h + 360 : h; }

        /// <summary>Smallest circle the plane can fly within Luke's bank limit (+30% margin), never below 1.5 km.</summary>
        internal static double MinRadius(double speed)
        {
            double r = speed * speed / (9.81 * Math.Tan(MaxBank(speed) * Math.PI / 180));
            return Math.Max(1500, 1.3 * r);
        }

        /// <summary>Heading that flies a circle of radius r around a center at bearingToCenter, dist away.
        /// right = clockwise (center on the right wing). Radial error turns in/out up to 45 deg.</summary>
        internal static double PatternHeading(double bearingToCenter, double dist, double radius, bool right)
        {
            double tangent = right ? bearingToCenter - 90 : bearingToCenter + 90;
            double err = Math.Max(-1, Math.Min(1, (dist - radius) / Math.Max(1, radius) * 2));
            double corr = 45 * err;   // > 0: too far out -> turn toward the center
            return Norm(right ? tangent + corr : tangent - corr);
        }
    }
}
