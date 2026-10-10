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

        // ---- fuel_check_return ----
        internal const double ApproachMargin = 15000;   // m of fuel-range kept for the approach/pattern at home

        /// <summary>Smoothed burn rate (units/s) from two fuel samples dt seconds apart (refuel/no burn keeps the old rate).</summary>
        internal static double BurnRate(double prevUnits, double units, double dt, double prevRate)
        {
            if (dt <= 0 || double.IsNaN(prevUnits)) return prevRate;
            double r = (prevUnits - units) / dt;
            if (r <= 0) return prevRate;
            return double.IsNaN(prevRate) || prevRate <= 0 ? r : .8 * prevRate + .2 * r;
        }
        /// <summary>Still-air range left in metres (infinite when we can't tell yet).</summary>
        internal static double Range(double units, double rate, double groundSpeed)
        {
            if (double.IsNaN(rate) || rate <= 1e-6 || groundSpeed < 1) return double.PositiveInfinity;
            return units / rate * groundSpeed;
        }
        /// <summary>Turn for home when the range left only just covers the trip home (+reserve % + approach margin).</summary>
        internal static bool ReturnNow(double range, double distHome, double reservePct)
        {
            return range <= distHome * (1 + Math.Max(0, reservePct) / 100) + ApproachMargin;
        }

        // ---- formation (AI wingman) ----
        internal const double PhysicsRange = 2300;   // m: KSP unloads/packs other craft ~2.5 km away; disengage before that
        internal const double ThrottleStepPct = .05; internal const float ThrottleWait = 2.5f;   // Luke: 5% steps, wait for spool

        /// <summary>Wingman slot error in the LEAD's frame: along = how far the slot is ahead of the wingman (m), cross = slot to the
        /// wingman's right (m). Echelon slot: spacing behind and spacing to the side (side +1 right, -1 left).</summary>
        internal static void SlotError(double leadLat, double leadLon, double leadHdg, double wingLat, double wingLon, double spacing, int side, double radius,
            out double along, out double cross)
        {
            double d2r = Math.PI / 180, h = leadHdg * d2r;
            double n = (wingLat - leadLat) * d2r * radius, e = (wingLon - leadLon) * d2r * radius * Math.Cos(leadLat * d2r);   // wing rel lead
            double fwd = n * Math.Cos(h) + e * Math.Sin(h), right = -n * Math.Sin(h) + e * Math.Cos(h);
            along = -spacing - fwd; cross = side * spacing - right;
        }
        /// <summary>Wingman targets: heading toward the slot (<=30 deg off the lead), speed lead +- 25 m/s to close, lead altitude.</summary>
        internal static double WingHeading(double leadHdg, double cross, double along)
        {
            double off = Math.Atan2(cross, Math.Max(150, 150 + Math.Abs(along) * .5)) * 180 / Math.PI;
            return Norm(leadHdg + Math.Max(-30, Math.Min(30, off)));
        }
        internal static double WingSpeed(double leadSpeed, double along) { return leadSpeed + Math.Max(-25, Math.Min(25, along * .08)); }
        internal static double HeadingError(double target, double current) { double e = Norm(target - current); return e > 180 ? e - 360 : e; }
        /// <summary>Bank command (deg, + right) for a heading error, capped by Luke's bank rule.</summary>
        internal static double WingBank(double headingErr, double speed) { double m = MaxBank(speed); return Math.Max(-m, Math.Min(m, headingErr * 1.2)); }
        /// <summary>Throttle in 5% steps, at most one step per 2.5 s; never below 5% in flight (only a hard abort goes to 0).</summary>
        internal static double ThrottleStep(double current, double speedErr, double sinceLast)
        {
            if (sinceLast < ThrottleWait || Math.Abs(speedErr) < 3) return current;
            double t = current + (speedErr > 0 ? ThrottleStepPct : -ThrottleStepPct);
            return Math.Max(.05, Math.Min(1, Math.Round(t / ThrottleStepPct) * ThrottleStepPct));
        }
        /// <summary>Climb/descent target (m/s) to the lead's altitude: capped, gentle.</summary>
        internal static double WingVs(double altErr) { return Math.Max(-12, Math.Min(15, altErr * .12)); }

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
