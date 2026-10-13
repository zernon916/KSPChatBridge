using System;
using System.IO;
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
        CorrectionChecks();
        EjectionChecks();
        PorkchopChecks();
        MissionStoreChecks();
        AscentGuidanceChecks();
        AtmoLandMathChecks();
        TargetLandMathChecks();
        Console.WriteLine("RocketTests: " + passes + " behavior checks passed.");
    }

    static void TargetLandMathChecks()
    {
        const double radius = 600000, mu = 3.5316e12;
        var baseline = TargetLandMath.Predict(radius, mu, 0, 0, 0, 10000, 100, 20, -100, 0, 0);
        Check(baseline != null && baseline.Time > 0, "ballistic descent intersects sphere");
        double targetLat = baseline.Latitude + .1, targetLon = baseline.Longitude + .2;
        var solution = TargetLandMath.Solve(radius, mu, 0, 0, 0, 10000, 100, 20, -100, targetLat, targetLon);
        var corrected = TargetLandMath.Predict(radius, mu, 0, 0, 0, 10000,
            100 + solution.DvEast, 20 + solution.DvNorth, -100 + solution.DvUp, targetLat, targetLon);
        Check(corrected != null && corrected.Error < solution.Impact.Error * .1, "suggested dv shrinks target impact error");
        var onTarget = TargetLandMath.Solve(radius, mu, 0, 0, 0, 10000, 100, 20, -100, baseline.Latitude, baseline.Longitude);
        Check(onTarget.Impact.Error < 1e-6 && onTarget.DvEast == 0 && onTarget.DvNorth == 0 && onTarget.DvUp == 0,
            "already on target needs zero correction");
        var rotating = TargetLandMath.Predict(radius, mu, .0002, 0, 0, 10000, 100, 20, -100, 0, 0);
        Check(rotating.Longitude < baseline.Longitude && Near(TargetLandMath.Wrap(rotating.Longitude - baseline.Longitude),
            -.0002 * baseline.Time * 180 / Math.PI, 1e-8), "eastward body rotation moves impact longitude west");
        var surface = TargetLandMath.Predict(radius, mu, 0, 20, 179.9, 0, 0, 0, 0, 20, 179.9);
        Check(surface.Time == 0 && surface.Error < 1e-8, "surface target has zero error");
        var seam = TargetLandMath.Predict(radius, mu, 0, 0, 179.9, 0, 0, 0, 0, 0, -179.9);
        Check(seam.Error < 2200 && seam.EastError > 0, "dateline error follows short eastward path");
        double circular = Math.Sqrt(mu / 680000);
        Check(TargetLandMath.Predict(radius, mu, 0, 0, 0, 80000, circular, 0, 0, 0, 0) == null, "circular orbit has no ballistic impact");
        var deorbit = TargetLandMath.Solve(radius, mu, 0, 0, 0, 80000, circular, 0, 0, 0, 40);
        Check(deorbit.CorrectedImpact != null && deorbit.DvEast < 0 &&
            Math.Sqrt(deorbit.DvEast * deorbit.DvEast + deorbit.DvNorth * deorbit.DvNorth + deorbit.DvUp * deorbit.DvUp) <= 500.000001,
            "bounded deorbit suggestion reaches surface");
        var drag = TargetLandMath.Predict(radius, mu, 0, 0, 0, 10000, 100, 20, -100, 0, 0, .001);
        Check(drag != null && TargetLandMath.Finite(drag.Time), "optional drag propagation remains finite");
        bool rejected = false;
        try { TargetLandMath.Predict(radius, mu, 0, 91, 0, 10000, 100, 0, -100, 0, 0); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid landing latitude rejected");
    }

    static void AtmoLandMathChecks()
    {
        var fast = AtmoLandMath.Evaluate(1000, -300, 1, 20, 10, 300, true, true);
        Check(!fast.ArmDrogue && !fast.DeployDrogue && !fast.ArmMain && !fast.DeployMain, "landing chutes stay stowed when too fast");
        var safe = AtmoLandMath.Evaluate(1000, -100, 1, 20, 10, 100, true, true);
        Check(safe.DeployDrogue && safe.DeployMain, "landing chutes deploy at safe speed and altitude");
        var lateral = AtmoLandMath.Evaluate(1000, -10, 1, 20, 10, 300, true, true);
        Check(!lateral.DeployDrogue && !lateral.DeployMain, "chute safety includes horizontal speed");
        var arm = AtmoLandMath.Evaluate(6000, -100, 1, 20, 10, 100, true, true);
        Check(arm.ArmDrogue && arm.ArmMain && !arm.DeployDrogue && !arm.DeployMain, "arming and deployment altitude gates differ");
        var none = AtmoLandMath.Evaluate(1000, -100, 1, 20, 10, 100, false, false);
        Check(!none.DeployDrogue && !none.DeployMain, "unavailable chutes cannot deploy");
        var custom = AtmoLandMath.Evaluate(1000, -100, 1, 20, 10, 100, true, true, mainSafeSpeed: 80);
        Check(custom.DeployDrogue && !custom.DeployMain, "configured chute speed limit honored");
        var above = AtmoLandMath.Evaluate(603, -100, 0, 20, 10, 100, false, false);
        var below = AtmoLandMath.Evaluate(601, -100, 0, 20, 10, 100, false, false);
        // (100² - 2²)/(2*(20-10))*1.2 + 2 = 601.76 m.
        Check(Near(above.BurnAltitude, 601.76, 1e-9) && !above.StartBurn && above.Throttle == 0 && below.StartBurn,
            "final burn starts at expected stopping height");
        var touchdown = AtmoLandMath.Evaluate(1, -2, 0, 20, 10, 2, false, false, burnActive: true);
        Check(below.Throttle > touchdown.Throttle && Near(touchdown.Throttle, .5, 1e-9), "throttle falls toward two metre per second touchdown");
        var drag = AtmoLandMath.Evaluate(1, -2, 4, 20, 10, 2, false, false, burnActive: true);
        Check(drag.Throttle < touchdown.Throttle, "live drag reduces needed landing thrust");
        var rebound = AtmoLandMath.Evaluate(1, 20, 0, 20, 10, 20, false, false, burnActive: true);
        Check(rebound.Throttle == 0 && !rebound.StartBurn, "landing rebound cuts thrust instead of sustaining ascent");
        var weak = AtmoLandMath.Evaluate(100, -50, 0, 5, 10, 50, false, false);
        Check(double.IsPositiveInfinity(weak.BurnAltitude) && weak.StartBurn && weak.Throttle == 1, "insufficient braking thrust requests immediate maximum burn");
        var noEngine = AtmoLandMath.Evaluate(100, -50, 0, 0, 10, 50, true, true);
        Check(!noEngine.StartBurn && noEngine.Throttle == 0 && noEngine.DeployMain, "chute-only descent remains finite");
        var ground = AtmoLandMath.Evaluate(0, -2, 0, 20, 10, 2, true, true, burnActive: true);
        Check(ground.Throttle == 0 && !ground.DeployMain, "touchdown shuts off thrust and chute commands");
        bool rejected = false;
        try { AtmoLandMath.Evaluate(100, -10, double.NaN, 20, 10, 10, true, true); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "landing rejects nonfinite measurements");
    }

    static void AscentGuidanceChecks()
    {
        var launch = AscentGuidance.Evaluate(0, 0, 0, 2, 0, 80000, 70000, 0, 0);
        Check(Near(launch.TargetPitch, 90, .01) && launch.Phase == AscentPhase.Climb, "ascent launch is vertical");
        var low = AscentGuidance.Evaluate(10000, 300, 200, 2, 10, 80000, 70000, 20000, 100);
        var high = AscentGuidance.Evaluate(50000, 500, 1000, 2, 1, 80000, 70000, 60000, 100);
        var edge = AscentGuidance.Evaluate(70000, 100, 2000, 2, 0, 80000, 70000, 79000, 100);
        Check(launch.TargetPitch > low.TargetPitch && low.TargetPitch > high.TargetPitch && Near(edge.TargetPitch, 0, .01), "ascent pitch falls toward horizontal");
        var pressure = AscentGuidance.Evaluate(10000, 300, 200, 2, 50, 80000, 70000, 20000, 100);
        Check(pressure.Throttle < low.Throttle && Near(pressure.Throttle, low.Throttle / 2, 1e-12), "high q reduces ascent throttle");
        var staged = AscentGuidance.Evaluate(10000, 300, 200, 4, 10, 80000, 70000, 20000, 100);
        Check(Near(staged.Throttle * 4, 1.9, 1e-12) && staged.TargetPitch != low.TargetPitch, "live stage TWR reshapes pitch and limits acceleration");
        var coast = AscentGuidance.Evaluate(50000, 500, 1000, 2, 1, 80000, 70000, 80000, 100);
        Check(coast.Phase == AscentPhase.CoastToAp && coast.Throttle == 0, "target apoapsis cuts throttle and starts coast");
        var circularize = AscentGuidance.Evaluate(79000, 10, 2000, 2, 0, 80000, 70000, 81000, 20);
        Check(circularize.Phase == AscentPhase.Circularize && circularize.Throttle == 0, "near apoapsis hands off to circularization without burning");
        var early = AscentGuidance.Evaluate(500, 150, 0, 2, 1, 80000, 70000, 2000, 100);
        Check(early.TargetPitch < 90, "speed starts turn before one kilometre");
        var vacuum = AscentGuidance.Evaluate(5000, 100, 200, 3, 0, 20000, 0, 10000, 100);
        Check(vacuum.Throttle == 1 && vacuum.TargetPitch < 90, "airless ascent has no atmospheric TWR cap");
        var noThrust = AscentGuidance.Evaluate(0, 0, 0, 0, 0, 80000, 70000, 0, 0);
        Check(noThrust.Throttle == 0, "zero available thrust is bounded");
        bool rejected = false;
        try { AscentGuidance.Evaluate(0, 0, 0, double.NaN, 0, 80000, 70000, 0, 0); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid ascent telemetry rejected");
    }

    static void MissionStoreChecks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AicsMissionTests_" + Guid.NewGuid().ToString("N"));
        string previous = MissionStore.PathOverride;
        MissionStore.PathOverride = dir;
        try
        {
            Check(MissionStore.Load("missing") == null, "missing mission returns null");
            var mission = new MissionData { TargetBodyName = "Mun", DepartureUT = 123456.75 };
            mission.Legs.Add(new MissionLeg { From = "Kerbin", To = "Mun", PlannedDv = 842.25 });
            mission.Legs.Add(new MissionLeg { From = "Mun", To = "Kerbin", PlannedDv = 310 });
            const string key = "../Mun: rocket/CON";
            MissionStore.Save(key, mission);
            var loaded = MissionStore.Load(key);
            Check(loaded != null && loaded.SchemaVersion == 1 && loaded.TargetBodyName == "Mun" && loaded.DepartureUT == 123456.75,
                "mission header round-trip");
            Check(loaded.Legs.Count == 2 && loaded.Legs[0].From == "Kerbin" && loaded.Legs[0].To == "Mun" && loaded.Legs[0].PlannedDv == 842.25 &&
                loaded.Legs[1].From == "Mun" && loaded.Legs[1].To == "Kerbin" && loaded.Legs[1].PlannedDv == 310, "mission legs round-trip in order");
            string file = Directory.GetFiles(dir, "*.json")[0];
            Check(Directory.GetDirectories(dir).Length == 0, "craft key remains a safe filename");
            mission.DepartureUT = null;
            MissionStore.Save(key, mission);
            Check(MissionStore.Load(key).DepartureUT == null, "atomic replacement clears optional departure");
            string valid = File.ReadAllText(file);
            foreach (string corrupt in new[] { "{broken", "\0\0", "{}", valid + "garbage",
                valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
                valid.Replace("842.25", "\"wrong type\"") })
            {
                File.WriteAllText(file, corrupt);
                Check(MissionStore.Load(key) == null, "corrupt mission returns null");
            }
            MissionStore.Save("123456789", mission);
            Check(MissionStore.Load("123456789") != null, "persistent ID key round-trip");
            MissionStore.Delete("123456789");
            MissionStore.Delete("123456789");
            Check(MissionStore.Load("123456789") == null && File.Exists(file), "delete affects only chosen craft and tolerates missing file");
            Check(MissionStore.Load(null) == null, "invalid load key never throws");
            MissionStore.Save("Mun/rocket", mission);
            MissionStore.Save("Mun:rocket", new MissionData { TargetBodyName = "Duna" });
            Check(MissionStore.Load("Mun/rocket").TargetBodyName == "Mun" && MissionStore.Load("Mun:rocket").TargetBodyName == "Duna",
                "sanitized craft names do not overwrite one another");
        }
        finally
        {
            MissionStore.PathOverride = previous;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    static void CorrectionChecks()
    {
        double r, n, p, pea;
        int calls = 0;
        CorrectionMath.Prediction quadratic = delegate(double radial, double normal, double prograde,
            out double altitude, out double miss)
        {
            calls++;
            altitude = 30000+1000*((radial-3.25)*(radial-3.25)+
                (normal+7.75)*(normal+7.75)+(prograde-12.5)*(prograde-12.5));
            miss = double.NaN;
        };
        Check(CorrectionMath.Search(quadratic, 30000, out r, out n, out p, out pea),
            "correction quadratic converges on all three dv axes");
        Check(Math.Abs(pea-30000) <= 2000, "correction meets 2 km tolerance floor");
        Check(calls <= 361, "correction bounded to 60 six-direction iterations");
        CorrectionMath.Prediction acquire = delegate(double radial, double normal, double prograde,
            out double altitude, out double miss)
        {
            double q = (radial-25)*(radial-25)+(normal+15)*(normal+15)+(prograde-5)*(prograde-5);
            altitude = q < 200 ? 100000+1000*q : double.NaN;
            miss = 1000000+q;
        };
        Check(CorrectionMath.Search(acquire, 100000, out r, out n, out p, out pea),
            "correction acquires encounter before tuning periapsis");
        Check(Math.Abs(pea-100000) <= 5000, "correction meets 5 percent tolerance");
        Check(CorrectionMath.Score(1e9, 0, 30000) < CorrectionMath.Score(double.NaN, 0, 30000),
            "any encounter outranks a miss");
        CorrectionMath.Prediction invalid = delegate(double a, double b, double c, out double alt, out double miss)
        { alt = double.NaN; miss = double.PositiveInfinity; };
        Check(!CorrectionMath.Search(invalid, 30000, out r, out n, out p, out pea),
            "unavailable predictions cannot report success");
        Check(!CorrectionMath.Search(quadratic, double.NaN, out r, out n, out p, out pea),
            "invalid correction target rejected");
    }

    static void EjectionChecks()
    {
        double excess, arrival, seconds, dv, angle;
        double[] point, impulse;
        RocketMath.Hohmann(1.1723328e18, 13599840256, 20726155264,
            out excess, out arrival, out seconds);
        Check(EjectionMath.Solve(3.5316e12, 600000, 680000, excess, 0, 0,
            out dv, out angle, out point, out impulse), "80 km LKO ejection solves");
        Check(dv > 1000 && dv < 1100, "80 km LKO -> Duna dv range");
        Check(angle > -160 && angle < -130, "outbound burn behind parent velocity");
        Check(Near(Math.Sqrt(point[0]*point[0]+point[1]*point[1]),680000,1e-6), "burn lies on parking circle");
        Check(Math.Abs(point[0]*impulse[0]+point[1]*impulse[1]) < .01, "coplanar impulse tangent");
        Console.WriteLine("N6 80 km LKO -> Duna: dv {0:F6} m/s, signed ejection angle {1:F6} deg.",dv,angle);
        // Reconstruct the eccentricity vector from r,v to verify the outgoing asymptote,
        // including a plane-changing burn. This detects the wrong side/sign of the burn point.
        foreach (double z in new[] { 0.0, 200.0 })
        {
            Check(EjectionMath.Solve(3.5316e12,600000,680000,excess,150,z,
                out dv,out angle,out point,out impulse), "inclined excess solves");
            double circular = Math.Sqrt(3.5316e12/680000);
            double[] v = { impulse[0]-circular*point[1]/680000, impulse[1]+circular*point[0]/680000, impulse[2] };
            double v2 = v[0]*v[0]+v[1]*v[1]+v[2]*v[2];
            double vinf = Math.Sqrt(excess*excess+150*150+z*z);
            Check(Near(v2-2*3.5316e12/680000,vinf*vinf,1e-7), "hyperbolic energy matches required excess");
            double e = 680000*v2/3.5316e12-1, c = -1/e, sn = Math.Sqrt(1-c*c);
            double speed = Math.Sqrt(v2);
            double[] desired = { excess/vinf,150/vinf,z/vinf };
            for (int k=0;k<3;k++)
                Check(Near(c*point[k]/680000+sn*v[k]/speed,desired[k],1e-12), "outgoing asymptote matches vector");
        }
        Check(!EjectionMath.Solve(3.5316e12,600000,600000,excess,0,0,out dv,out angle,out point,out impulse), "surface parking rejected");
        Check(!EjectionMath.Solve(double.NaN,600000,680000,excess,0,0,out dv,out angle,out point,out impulse), "NaN rejected");
        Check(!EjectionMath.Solve(3.5316e12,600000,680000,0,0,0,out dv,out angle,out point,out impulse), "zero asymptote rejected");
        Check(!EjectionMath.Solve(3.5316e12,600000,680000,0,0,excess,out dv,out angle,out point,out impulse), "infeasible polar asymptote rejected");
        Check(EjectionMath.Solve(3.5316e12,600000,680000,-excess,0,0,out dv,out angle,out point,out impulse)
            && angle > 20 && angle < 50, "inward transfer rotates burn to opposite side");
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
