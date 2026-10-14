using System;
using KSPChatBridge;

internal static class EncyclopediaMathTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("EncyclopediaMathTests: " + name);
        passes++;
    }

    static bool Near(double actual, double expected, double tolerance)
    {
        return Math.Abs(actual - expected) <= tolerance;
    }

    internal static void Run()
    {
        passes = 0;
        double dv = EncyclopediaMath.IdealLandingDeltaV(3.5316e12, 600000, 20000, 1000);
        Check(Near(dv, Math.Sqrt(3.5316e12 / 621000), 1e-10),
            "landing baseline uses the body mu, radius, peak, and clearance");
        Check(EncyclopediaMath.IdealLandingDeltaV(4.9049e12, 1737400, 0, 1000) <
            EncyclopediaMath.IdealLandingDeltaV(3.5316e12, 600000, 0, 1000),
            "landing baseline uses each body's local gravity parameter");
        Check(double.IsNaN(EncyclopediaMath.IdealLandingDeltaV(0, 600000, 0, 1000)) &&
            double.IsNaN(EncyclopediaMath.IdealLandingDeltaV(3.5316e12, 0, 0, 1000)),
            "rejects missing gravitational parameter or radius");
        Check(double.IsNaN(EncyclopediaMath.IdealLandingDeltaV(3.5316e12, 600000, -1, 1000)) &&
            double.IsNaN(EncyclopediaMath.IdealLandingDeltaV(3.5316e12, 600000, 0, -1)),
            "rejects negative peak altitude or clearance");
        Check(double.IsNaN(EncyclopediaMath.IdealLandingDeltaV(double.PositiveInfinity, 600000, 0, 1000)) &&
            double.IsNaN(EncyclopediaMath.IdealLandingDeltaV(3.5316e12, 600000, double.NaN, 1000)),
            "rejects non-finite body data");
        Console.WriteLine("Encyclopedia math: " + passes + " behavior checks passed.");
    }
}
