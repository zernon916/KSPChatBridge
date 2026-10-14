using System;
using KSPChatBridge;

internal static class DvMapMathTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("DvMapMathTests: " + name);
        passes++;
    }

    static bool Near(double actual, double expected, double tolerance)
    {
        return Math.Abs(actual - expected) <= tolerance;
    }

    internal static void Run()
    {
        passes = 0;
        double departure, arrival, seconds;
        Check(DvMapMath.TryHohmann(3.5316e12, 700000, 12000000, out departure, out arrival, out seconds),
            "valid LKO to Mun-orbit transfer");
        Check(Near(departure, 842, 10), "departure delta-v is in metres per second");
        Check(Near(arrival, 362, 5), "arrival delta-v is in metres per second");
        Check(Near(DvMapMath.TotalDeltaV(departure, arrival), 1204, 15), "total includes both burn magnitudes");
        Check(Near(seconds, 26750, 100), "transfer time is the Hohmann half-period");

        double inwardDeparture, inwardArrival, inwardSeconds;
        Check(DvMapMath.TryHohmann(3.5316e12, 12000000, 700000,
            out inwardDeparture, out inwardArrival, out inwardSeconds), "valid inward transfer");
        Check(inwardDeparture < 0 && inwardArrival < 0, "inward burns retain retrograde signs");
        Check(Near(DvMapMath.TotalDeltaV(inwardDeparture, inwardArrival), 1204, 15),
            "total inward delta-v uses magnitudes");

        double sameDeparture, sameArrival, sameSeconds;
        Check(DvMapMath.TryHohmann(3.5316e12, 700000, 700000,
            out sameDeparture, out sameArrival, out sameSeconds), "equal-radius transfer is valid");
        Check(sameDeparture == 0 && sameArrival == 0 && sameSeconds > 0, "equal radii require no delta-v");

        double badDeparture, badArrival, badSeconds;
        Check(!DvMapMath.TryHohmann(0, 700000, 12000000, out badDeparture, out badArrival, out badSeconds),
            "zero gravity rejected");
        Check(!DvMapMath.TryHohmann(3.5316e12, 0, 12000000, out badDeparture, out badArrival, out badSeconds),
            "zero radius rejected");
        Check(!DvMapMath.TryHohmann(3.5316e12, double.NaN, 12000000, out badDeparture, out badArrival, out badSeconds),
            "non-finite radius rejected");
        Check(double.IsNaN(badDeparture) && double.IsNaN(badArrival) && double.IsNaN(badSeconds),
            "invalid estimate outputs are explicitly unavailable");
        Check(double.IsNaN(DvMapMath.TotalDeltaV(double.PositiveInfinity, 1)),
            "non-finite burn delta-v rejected");
        Console.WriteLine("DvMapMathTests: " + passes + " behavior checks passed.");
    }
}
