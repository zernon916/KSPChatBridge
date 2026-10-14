using System;
using KSPChatBridge;

internal static class DockingMathTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("DockingMathTests: " + name);
        passes++;
    }

    static bool Near(double actual, double expected, double tolerance)
    {
        return Math.Abs(actual - expected) <= tolerance;
    }

    internal static void Run()
    {
        passes = 0;
        DockingEstimate estimate = DockingMath.Estimate(
            100, 0, 0, -2, 0, 0,
            -1, 0, 0, 1, 0, 0);
        Check(estimate != null, "valid port geometry produces an estimate");
        Check(Near(estimate.AlignmentErrorDeg, 0, 1e-9),
            "opposing port axes have zero alignment error");
        Check(Near(estimate.LateralOffset, 0, 1e-9),
            "on-axis approach has zero lateral offset");
        Check(Near(estimate.ClosingSpeed, 2, 1e-9),
            "approach toward the target port has positive closing speed");

        estimate = DockingMath.Estimate(
            100, 3, 4, -2, 1, 0,
            -1, 0, 0, 1, 0, 0);
        Check(Near(estimate.LateralOffset, 5, 1e-9),
            "lateral offset is perpendicular distance from target port axis");
        Check(Near(estimate.ClosingSpeed, 2, 1e-9),
            "sideways velocity does not change closing speed");

        double angle = 30 * Math.PI / 180.0;
        estimate = DockingMath.Estimate(
            10, 0, 0, 0, 0, 0,
            -Math.Cos(angle), Math.Sin(angle), 0, 1, 0, 0);
        Check(Near(estimate.AlignmentErrorDeg, 30, 1e-9),
            "alignment error measures deviation from opposing axes");

        estimate = DockingMath.Estimate(
            100, 0, 0, 2, 0, 0,
            -1, 0, 0, 1, 0, 0);
        Check(Near(estimate.ClosingSpeed, -2, 1e-9),
            "opening motion has negative closing speed");

        estimate = DockingMath.Estimate(
            1, 0, 0, 0, 0, 0,
            0, 1, 0, 1, 0, 0);
        Check(Near(estimate.AlignmentErrorDeg, 90, 1e-9),
            "perpendicular port axes have ninety degree error");

        estimate = DockingMath.Estimate(
            1e200, 0, 0, 0, 0, 0,
            -2, 0, 0, 5, 0, 0);
        Check(estimate != null && Near(estimate.AlignmentErrorDeg, 0, 1e-9),
            "port axis magnitudes do not affect alignment");
        Check(Near(estimate.LateralOffset, 0, 1e-9),
            "large finite on-axis position remains stable");

        Check(DockingMath.Estimate(1, 0, 0, 0, 0, 0,
            0, 0, 0, 1, 0, 0) == null, "zero active port axis rejected");
        Check(DockingMath.Estimate(1, 0, 0, 0, 0, 0,
            -1, 0, 0, 0, 0, 0) == null, "zero target port axis rejected");
        Check(DockingMath.Estimate(double.NaN, 0, 0, 0, 0, 0,
            -1, 0, 0, 1, 0, 0) == null, "non-finite relative position rejected");
        Check(DockingMath.Estimate(1, 0, 0, double.PositiveInfinity, 0, 0,
            -1, 0, 0, 1, 0, 0) == null, "non-finite relative velocity rejected");
        Check(DockingMath.Estimate(1, 0, 0, 0, 0, 0,
            -1, 0, 0, double.NaN, 0, 0) == null, "non-finite port axis rejected");

        Console.WriteLine("DockingMathTests: " + passes + " behavior checks passed.");
    }
}
