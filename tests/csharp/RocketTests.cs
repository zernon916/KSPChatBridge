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
        PorkchopChecks();
        Console.WriteLine("RocketTests: " + passes + " behavior checks passed.");
    }

    // Independent RK4 integration of Newton's equation, not Lambert's f/g formulas.
    static double[] Derivative(double[] state, double mu)
    {
        double r = Math.Sqrt(state[0]*state[0]+state[1]*state[1]+state[2]*state[2]);
        double factor = -mu/(r*r*r);
        return new[] { state[3], state[4], state[5], factor*state[0], factor*state[1], factor*state[2] };
    }

    static double[] Offset(double[] state, double[] slope, double dt)
    {
        double[] next = new double[6];
        for (int k = 0; k < 6; k++) next[k] = state[k]+dt*slope[k];
        return next;
    }

    static void RoundTrip(double[] r2, double tof)
    {
        const double mu = 3.5316e12;
        double[] r1 = { 700000, 0, 0 }, v1, v2;
        Check(Porkchop.Lambert(mu, r1, r2, tof, out v1, out v2), "Lambert converges");
        Check(v1[1] > 0, "Lambert selects prograde branch");
        double[] state = { r1[0], r1[1], r1[2], v1[0], v1[1], v1[2] };
        double dt = tof/20000;
        for (int i = 0; i < 20000; i++)
        {
            double[] a = Derivative(state, mu), b = Derivative(Offset(state, a, dt/2), mu);
            double[] c = Derivative(Offset(state, b, dt/2), mu), d = Derivative(Offset(state, c, dt), mu);
            for (int k = 0; k < 6; k++) state[k] += dt*(a[k]+2*b[k]+2*c[k]+d[k])/6;
        }
        for (int k = 0; k < 3; k++)
        {
            Check(Near(state[k], r2[k], .1), "Lambert propagated position within 10 cm");
            Check(Near(state[k+3], v2[k], .001), "Lambert arrival velocity within 1 mm/s");
        }
    }

    static void PorkchopChecks()
    {
        RoundTrip(new[] { 200000.0, 850000, 150000 }, 600); // inclined, short way
        RoundTrip(new[] { 200000.0, -850000, 150000 }, 2500); // prograde long way
        RoundTrip(new[] { 200000.0, 850000, 0 }, 100); // hyperbolic, negative z
        double[] a, b;
        Check(!Porkchop.Lambert(1, new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 }, 1, out a, out b),
            "ambiguous collinear plane is explicitly rejected");
        Check(!Porkchop.Lambert(double.NaN, new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, 1, out a, out b),
            "invalid Lambert inputs rejected");

        const double sun = 1.1723328e18, kerbin = 13599840256, duna = 20726155264, day = 21600;
        const double kerbinMu = 3.5316e12, dunaMu = 3.0136321e11;
        // Arrange the ideal circular Hohmann window at day 100 relative to t0.
        double n1 = Math.Sqrt(sun/kerbin)/kerbin, n2 = Math.Sqrt(sun/duna)/duna;
        double phase = RocketMath.PhaseAngleDeg(sun, kerbin, duna)*Math.PI/180 + (n1-n2)*100*day;
        double[,] grid = Porkchop.Grid(sun, kerbin, 0, duna, phase, 60*day, 140*day, 41,
            220*day, 360*day, 71, kerbinMu, 680000, dunaMu, 370000);
        double best = double.PositiveInfinity;
        int di = -1, ti = -1;
        for (int i = 0; i < 41; i++)
        for (int j = 0; j < 71; j++)
            if (grid[i,j] < best) { best = grid[i,j]; di = i; ti = j; }
        Check(best > 1500 && best < 1800, "Duna total includes LKO ejection AND 50 km circular capture");
        double dep = (60+2*di)*day, tof = (220+2*ti)*day;
        double p1 = n1*dep, p2 = phase+n2*(dep+tof);
        Check(Porkchop.Lambert(sun, new[] { kerbin*Math.Cos(p1), kerbin*Math.Sin(p1), 0 },
            new[] { duna*Math.Cos(p2), duna*Math.Sin(p2), 0 }, tof, out a, out b), "minimum transfer solves");
        double vx = a[0]+kerbin*n1*Math.Sin(p1), vy = a[1]-kerbin*n1*Math.Cos(p1);
        double ejection = RocketMath.EjectionDv(kerbinMu, 680000, Math.Sqrt(vx*vx+vy*vy));
        Check(ejection > 1000 && ejection < 1100, "minimum has sensible 80 km LKO ejection");
        Check(dep/day > 80 && dep/day < 120 && tof/day > 250 && tof/day < 330, "minimum near known circular window");
        double[,] single = Porkchop.Grid(sun, kerbin, 0, duna, phase, dep, dep, 1, tof, tof, 1,
            kerbinMu, 680000, dunaMu, 370000);
        Check(Near(single[0,0], best, 1e-6), "one-cell grid and inclusive grid indexing agree");
        double[,] excess = Porkchop.Grid(sun, kerbin, 0, duna, phase, dep, dep, 1, tof, tof, 1);
        double ax = b[0]+duna*n2*Math.Sin(p2), ay = b[1]-duna*n2*Math.Cos(p2);
        Check(Near(excess[0,0], Math.Sqrt(vx*vx+vy*vy)+Math.Sqrt(ax*ax+ay*ay), 1e-6), "default grid sums both excess speeds");
        bool rejected = false;
        try { Porkchop.Grid(sun, kerbin, 0, duna, phase, 0, 1, 0, 1, 2, 1); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid grid counts rejected");
        Console.WriteLine("Duna porkchop: total {0:F3} m/s, ejection {1:F3} m/s, departure day {2:F1}, travel {3:F1} Kerbin days (6 h); t0 phases Kerbin=0, Duna={4:F6} rad.",
            best, ejection, dep/day, tof/day, phase);
    }
}
