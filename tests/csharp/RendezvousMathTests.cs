using System;
using KSPChatBridge;

internal static class RendezvousMathTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("RendezvousMathTests: " + name);
        passes++;
    }

    static bool Near(double actual, double expected, double tolerance)
    {
        return Math.Abs(actual - expected) <= tolerance;
    }

    internal static void Run()
    {
        passes = 0;
        RendezvousEstimate estimate = RendezvousMath.Estimate(1000, 0, 0, -10, 0, 0, 3600);
        Check(estimate != null, "valid relative state produces estimate");
        Check(Near(estimate.Range, 1000, 1e-9) && Near(estimate.RelativeSpeed, 10, 1e-9),
            "range and relative speed use SI units");
        Check(Near(estimate.RangeRate, -10, 1e-9), "negative range rate is closing");
        Check(Near(estimate.SecondsToClosestApproach, 100, 1e-9) &&
            Near(estimate.ClosestApproachRange, 0, 1e-9), "head-on motion predicts intercept");

        estimate = RendezvousMath.Estimate(1000, 0, 0, 10, 0, 0, 3600);
        Check(Near(estimate.SecondsToClosestApproach, 0, 1e-9) &&
            Near(estimate.ClosestApproachRange, 1000, 1e-9), "opening target has no future CPA");
        estimate = RendezvousMath.Estimate(1000, 0, 0, -1, 1, 0, 3600);
        Check(Near(estimate.SecondsToClosestApproach, 500, 1e-9) &&
            Near(estimate.ClosestApproachRange, Math.Sqrt(500000), 1e-9), "skew pass predicts miss distance");
        estimate = RendezvousMath.Estimate(1000, 0, 0, -1, 0, 0, 10);
        Check(Near(estimate.SecondsToClosestApproach, 10, 1e-9) &&
            Near(estimate.ClosestApproachRange, 990, 1e-9), "prediction horizon caps CPA");
        estimate = RendezvousMath.Estimate(1000, 0, 0, 0, 0, 0, 3600);
        Check(Near(estimate.SecondsToClosestApproach, 0, 1e-9) &&
            Near(estimate.ClosestApproachRange, 1000, 1e-9), "stationary relative state remains finite");
        estimate = RendezvousMath.Estimate(0, 0, 0, 1, 0, 0, 3600);
        Check(Near(estimate.RangeRate, 0, 1e-9) && Near(estimate.ClosestApproachRange, 0, 1e-9),
            "coincident vessels avoid division by zero");
        Check(RendezvousMath.Estimate(1, 0, 0, 0, 0, 0, -1) == null, "negative horizon rejected");
        Check(RendezvousMath.Estimate(double.NaN, 0, 0, 0, 0, 0, 10) == null,
            "non-finite position rejected");
        Check(RendezvousMath.Estimate(1, 0, 0, double.PositiveInfinity, 0, 0, 10) == null,
            "non-finite velocity rejected");
        Check(RendezvousMath.Estimate(double.MaxValue, 0, 0, 0, 0, 0, 10) == null,
            "overflowed vector magnitude rejected");
        Console.WriteLine("RendezvousMathTests: " + passes + " behavior checks passed.");
    }
}
