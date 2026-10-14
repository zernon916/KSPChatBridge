using System;

namespace KSPChatBridge
{
    internal sealed class RendezvousEstimate
    {
        internal readonly double Range;
        internal readonly double RelativeSpeed;
        internal readonly double RangeRate;
        internal readonly double SecondsToClosestApproach;
        internal readonly double ClosestApproachRange;

        internal RendezvousEstimate(double range, double relativeSpeed, double rangeRate,
            double secondsToClosestApproach, double closestApproachRange)
        {
            Range = range;
            RelativeSpeed = relativeSpeed;
            RangeRate = rangeRate;
            SecondsToClosestApproach = secondsToClosestApproach;
            ClosestApproachRange = closestApproachRange;
        }
    }

    internal static class RendezvousMath
    {
        internal static RendezvousEstimate Estimate(double rx, double ry, double rz,
            double vx, double vy, double vz, double maxSeconds)
        {
            if (!Finite(rx) || !Finite(ry) || !Finite(rz) ||
                !Finite(vx) || !Finite(vy) || !Finite(vz) ||
                !Finite(maxSeconds) || maxSeconds < 0)
                return null;

            double rangeSquared = rx * rx + ry * ry + rz * rz;
            double speedSquared = vx * vx + vy * vy + vz * vz;
            double dot = rx * vx + ry * vy + rz * vz;
            if (!Finite(rangeSquared) || !Finite(speedSquared) || !Finite(dot))
                return null;

            double range = Math.Sqrt(rangeSquared);
            double speed = Math.Sqrt(speedSquared);
            double rangeRate = range > 0 ? dot / range : 0;
            double seconds = speedSquared > 0
                ? Math.Max(0, Math.Min(maxSeconds, -dot / speedSquared))
                : 0;
            double cx = rx + vx * seconds;
            double cy = ry + vy * seconds;
            double cz = rz + vz * seconds;
            double closestRange = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (!Finite(rangeRate) || !Finite(closestRange)) return null;

            return new RendezvousEstimate(range, speed, rangeRate, seconds, closestRange);
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
