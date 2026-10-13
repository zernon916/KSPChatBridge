using System;
using KSPChatBridge;

internal static class RocketTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("RocketTests: " + name);
        passes++;
    }

    static bool Near(double actual, double expected, double tolerance)
    {
        return Math.Abs(actual - expected) <= tolerance;
    }

    internal static void Run()
    {
        passes = 0;
        // Stock KSP: Kerbin mu in m^3/s^2, 600 km radius, 100 km LKO,
        // Mun orbit radius 12,000 km. These are radii, not altitudes.
        const double kerbinMu = 3.5316e12, lkoRadius = 700000;
        double departure, arrival, seconds;
        RocketMath.Hohmann(kerbinMu, lkoRadius, 12000000, out departure, out arrival, out seconds);
        Check(Near(departure, 842, 10), "100 km LKO -> Mun departure about 842 m/s");
        Check(Near(arrival, 362, 5), "Mun-radius circularization about 362 m/s (not Mun capture)");
        Check(Near(seconds, 26750, 100), "LKO -> Mun transfer about 7.43 hours");

        // Ideal coplanar circular Kerbin/Duna orbits about the Sun; departure
        // excess speed feeds the patched-conic ejection from 100 km LKO.
        RocketMath.Hohmann(1.1723328e18, 13599840256, 20726155264,
            out departure, out arrival, out seconds);
        Check(Near(departure, 918, 10), "Kerbin -> Duna hyperbolic excess about 918 m/s");
        Check(Near(RocketMath.EjectionDv(kerbinMu, lkoRadius, departure), 1060, 15),
            "Kerbin -> Duna ejection about 1060 m/s");

        // LegBudget/StageAllocator do not exist yet. Exercise the available
        // split-burn budget: 900 m/s consumes 300 from stage 1 and 600 from stage 2.
        // Isp*g0 = 1000 m/s and thrust = 10000 N, giving 10 kg/s flow.
        double[] budgets = { 300, 800 }, masses = { 1000, 500 };
        double[] thrusts = { 10000, 10000 }, isps = { 1000 / 9.80665, 1000 / 9.80665 };
        Check(Near(RocketMath.BurnTimeStages(900, budgets, masses, thrusts, isps), 48.4776, .001),
            "split leg uses only the required part of the second stage (48.4776 s)");
        Check(double.IsNaN(RocketMath.BurnTimeStages(1101, budgets, masses, thrusts, isps)),
            "split leg rejects insufficient total stage delta-v");

        Check(AbortPolicy.Check(1.6, 46, 0) == "tumbling", "tumble abort");
        Check(AbortPolicy.Check(1.5, 46, 0) == null && AbortPolicy.Check(1.6, 45, 0) == null,
            "tumble requires both angular rate and attitude error above thresholds");
        Check(AbortPolicy.Check(0, 0, .51) == "thrust loss", "thrust-loss abort without tumble");
        Check(AbortPolicy.Check(0, 0, .5) == null, "50 percent thrust loss is the non-abort boundary");
        Console.WriteLine("RocketTests: " + passes + " behavior checks passed.");
    }
}
