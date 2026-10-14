using System;

namespace KSPChatBridge
{
    internal sealed class DockingEstimate
    {
        internal readonly double AlignmentErrorDeg;
        internal readonly double LateralOffset;
        internal readonly double ClosingSpeed;

        internal DockingEstimate(double alignmentErrorDeg, double lateralOffset, double closingSpeed)
        {
            AlignmentErrorDeg = alignmentErrorDeg;
            LateralOffset = lateralOffset;
            ClosingSpeed = closingSpeed;
        }
    }

    internal static class DockingMath
    {
        /// <summary>
        /// Estimates docking geometry. Relative position is active port minus target port;
        /// relative velocity uses the same direction. Port axes point outward from each port.
        /// </summary>
        internal static DockingEstimate Estimate(
            double rx, double ry, double rz,
            double vx, double vy, double vz,
            double activeAxisX, double activeAxisY, double activeAxisZ,
            double targetAxisX, double targetAxisY, double targetAxisZ)
        {
            if (!Finite(rx) || !Finite(ry) || !Finite(rz) ||
                !Finite(vx) || !Finite(vy) || !Finite(vz) ||
                !Normalize(ref activeAxisX, ref activeAxisY, ref activeAxisZ) ||
                !Normalize(ref targetAxisX, ref targetAxisY, ref targetAxisZ))
                return null;

            double facingDot = activeAxisX * targetAxisX +
                activeAxisY * targetAxisY + activeAxisZ * targetAxisZ;
            double alignmentCos = Math.Max(-1.0, Math.Min(1.0, -facingDot));
            double alignmentErrorDeg = Math.Acos(alignmentCos) * 180.0 / Math.PI;

            double axialPosition = rx * targetAxisX + ry * targetAxisY + rz * targetAxisZ;
            double lateralX = rx - axialPosition * targetAxisX;
            double lateralY = ry - axialPosition * targetAxisY;
            double lateralZ = rz - axialPosition * targetAxisZ;
            double lateralOffset = Magnitude(lateralX, lateralY, lateralZ);
            double closingSpeed = -(vx * targetAxisX + vy * targetAxisY + vz * targetAxisZ);
            if (!Finite(axialPosition) || !Finite(lateralX) || !Finite(lateralY) ||
                !Finite(lateralZ) || !Finite(lateralOffset) || !Finite(closingSpeed))
                return null;

            return new DockingEstimate(alignmentErrorDeg, lateralOffset, closingSpeed);
        }

        static bool Normalize(ref double x, ref double y, ref double z)
        {
            if (!Finite(x) || !Finite(y) || !Finite(z)) return false;
            double scale = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
            if (scale == 0) return false;
            x /= scale;
            y /= scale;
            z /= scale;
            double magnitude = Math.Sqrt(x * x + y * y + z * z);
            x /= magnitude;
            y /= magnitude;
            z /= magnitude;
            return true;
        }

        static double Magnitude(double x, double y, double z)
        {
            double scale = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
            if (scale == 0) return 0;
            x /= scale;
            y /= scale;
            z /= scale;
            return scale * Math.Sqrt(x * x + y * y + z * z);
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
