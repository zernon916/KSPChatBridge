using System;

namespace KSPChatBridge
{
    internal static class PointAtMath
    {
        internal static bool TryDirection(double px, double py, double pz, double vx, double vy, double vz,
            double ax, double ay, double az, string mode, out double dx, out double dy, out double dz)
        {
            dx = dy = dz = 0;
            if (string.IsNullOrWhiteSpace(mode)) return false;

            if (string.Equals(mode, "prograde", StringComparison.OrdinalIgnoreCase))
            { dx = vx; dy = vy; dz = vz; }
            else if (string.Equals(mode, "retro", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "retrograde", StringComparison.OrdinalIgnoreCase))
            { dx = -vx; dy = -vy; dz = -vz; }
            else if (string.Equals(mode, "normal", StringComparison.OrdinalIgnoreCase))
            { dx = py * vz - pz * vy; dy = pz * vx - px * vz; dz = px * vy - py * vx; }
            else if (string.Equals(mode, "antinormal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "anti-normal", StringComparison.OrdinalIgnoreCase))
            { dx = pz * vy - py * vz; dy = px * vz - pz * vx; dz = py * vx - px * vy; }
            else if (string.Equals(mode, "radialin", StringComparison.OrdinalIgnoreCase))
            { dx = -px; dy = -py; dz = -pz; }
            else if (string.Equals(mode, "radialout", StringComparison.OrdinalIgnoreCase))
            { dx = px; dy = py; dz = pz; }
            else if (string.Equals(mode, "target", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "node", StringComparison.OrdinalIgnoreCase))
            { dx = ax; dy = ay; dz = az; }
            else return false;

            double scale = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) return false;
            dx /= scale;
            dy /= scale;
            dz /= scale;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (double.IsNaN(length) || double.IsInfinity(length) || length <= 0)
            { dx = dy = dz = 0; return false; }
            dx /= length;
            dy /= length;
            dz /= length;
            return true;
        }
    }
}
