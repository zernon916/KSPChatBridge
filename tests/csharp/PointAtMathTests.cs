using System;
using KSPChatBridge;

internal static class PointAtMathTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("PointAtMathTests: " + name);
        passes++;
    }

    static bool Near(double actual, double expected)
    {
        return Math.Abs(actual - expected) < 1e-9;
    }

    static bool Direction(string mode, double px, double py, double pz, double vx, double vy, double vz,
        double ax, double ay, double az, double ex, double ey, double ez)
    {
        double x, y, z;
        return PointAtMath.TryDirection(px, py, pz, vx, vy, vz, ax, ay, az, mode, out x, out y, out z) &&
            Near(x, ex) && Near(y, ey) && Near(z, ez);
    }

    internal static void Run()
    {
        passes = 0;
        Check(Direction("prograde", 0, 1, 0, 3, 0, 0, 0, 0, 0, 1, 0, 0), "prograde follows orbital velocity");
        Check(Direction("retro", 0, 1, 0, 3, 0, 0, 0, 0, 0, -1, 0, 0), "retro opposes orbital velocity");
        Check(Direction("normal", 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, -1), "normal follows the angular-momentum vector");
        Check(Direction("antinormal", 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1), "antinormal opposes angular momentum");
        Check(Direction("radialout", 0, 3, 4, 1, 0, 0, 0, 0, 0, 0, .6, .8), "radialout follows position");
        Check(Direction("radialin", 0, 3, 4, 1, 0, 0, 0, 0, 0, 0, -.6, -.8), "radialin opposes position");
        Check(Direction("target", 0, 0, 0, 0, 0, 0, 0, 3, 4, 0, .6, .8), "target uses the supplied target direction");
        Check(Direction("node", 0, 0, 0, 0, 0, 0, 0, 3, 4, 0, .6, .8), "node uses the supplied burn direction");
        Check(Direction("PROGRADE", 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 1, 0), "mode matching is case-insensitive");

        double x, y, z;
        Check(!PointAtMath.TryDirection(0, 0, 0, 0, 0, 0, 0, 0, 0, "prograde", out x, out y, out z) &&
            x == 0 && y == 0 && z == 0, "zero direction is unavailable");
        Check(!PointAtMath.TryDirection(0, 0, 0, double.NaN, 0, 0, 0, 0, 0, "prograde", out x, out y, out z),
            "non-finite direction is unavailable");
        Check(!PointAtMath.TryDirection(0, 0, 0, 1, 0, 0, 0, 0, 0, "unknown", out x, out y, out z),
            "unknown mode is rejected");
        Check(Direction("prograde", 0, 0, 0, 1e300, 1e300, 0, 0, 0, 0,
            Math.Sqrt(.5), Math.Sqrt(.5), 0), "normalization handles large finite vectors");
        Console.WriteLine("PointAtMathTests: " + passes + " behavior checks passed.");
    }
}
