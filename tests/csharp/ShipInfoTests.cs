using System;
using KSPChatBridge;

internal static class ShipInfoTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("ShipInfoTests: " + name);
        passes++;
    }

    internal static void Run()
    {
        passes = 0;
        Check(ShipInfo.Distance(999.5) == "1,000 m", "formats sub-kilometer distances in meters");
        Check(ShipInfo.Distance(12500) == "12.5 km", "formats large distances in kilometers");
        Check(ShipInfo.Distance(double.NaN) == "--" && ShipInfo.Distance(double.NegativeInfinity) == "--",
            "invalid distance is safe to display");
        Check(ShipInfo.Speed(-12.34) == "-12.3 m/s", "formats signed speed");
        Check(ShipInfo.Mass(.42) == "420 kg" && ShipInfo.Mass(2.125) == "2.13 t",
            "formats mass in readable units");
        Check(ShipInfo.Ratio(1.236) == "1.24x" && ShipInfo.Ratio(double.NaN) == "--",
            "formats thrust ratio and unknown values");
        Check(ShipInfo.Percent(.875) == "88%" && ShipInfo.Angle(4.25) == "4.3\u00B0",
            "formats temperature percentage and angle");
        Check(ShipInfo.Resource(123.45, 400) == "123.5 / 400.0" &&
            ShipInfo.Resource(0, 0) == "--", "formats resource totals safely");
        Check(ShipInfo.ResourceFraction(120, 400) == .3f &&
            ShipInfo.ResourceFraction(500, 400) == 1f &&
            ShipInfo.ResourceFraction(-1, 400) == 0f &&
            ShipInfo.ResourceFraction(1, 0) == 0f, "resource bar fraction is bounded and safe");
        Console.WriteLine("ShipInfoTests: " + passes + " behavior checks passed.");
    }
}
