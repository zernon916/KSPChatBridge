using System;
using System.Collections.Generic;
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
        PlanetViewMathChecks();
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
        SlopeChecks();
        DvTrackerChecks();
        Console.WriteLine("RocketTests: " + passes + " behavior checks passed.");
    }

    static void PlanetViewMathChecks()
    {
        double d = PlanetViewMath.Distance(100, 60, 1);
        Check(Near(d, 230, 1e-10), "planet sphere fit includes border");
        Check(Near(PlanetViewMath.Distance(200, 60, 1), 2 * d, 1e-10), "planet framing scales with radius");
        Check(Near(PlanetViewMath.Distance(100, 60, 2), d, 1e-10), "wide viewport limited by vertical FOV");
        double narrow = PlanetViewMath.Distance(100, 60, .5);
        Check(narrow > d && Math.Asin(100 / narrow) < Math.Atan(Math.Tan(Math.PI / 6) * .5),
            "narrow viewport contains whole sphere horizontally");
        Check(double.IsNaN(PlanetViewMath.Distance(0, 60, 1)) &&
            double.IsNaN(PlanetViewMath.Distance(1, 180, 1)) &&
            double.IsNaN(PlanetViewMath.Distance(1, 60, 0)) &&
            double.IsNaN(PlanetViewMath.Distance(double.PositiveInfinity, 60, 1)) &&
            double.IsNaN(PlanetViewMath.Distance(1, double.NaN, 1)), "invalid framing rejected");
        Check(PlanetViewMath.Wrap(-20) == 340 && PlanetViewMath.Wrap(740) == 20 &&
            PlanetViewMath.Wrap(double.NaN) == 0, "camera yaw wraps safely");
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

    static void SlopeChecks()
    {
        var flat = new double[5, 5];
        double s, e, n;
        Check(Math.Abs(SlopeMath.PlaneSlopeDeg(flat, 10)) < 1e-6, "slope: flat grid is 0 deg");
        Check(!SlopeMath.Nudge(flat, 10, out s, out e, out n), "slope: flat grid needs no nudge");
        var tilt = new double[5, 5];
        for (int r = 0; r < 5; r++) for (int c = 0; c < 5; c++) tilt[r, c] = c * 10 * Math.Tan(20 * Math.PI / 180);
        Check(Math.Abs(SlopeMath.PlaneSlopeDeg(tilt, 10) - 20) < 0.5, "slope: tilted plane reads 20 deg");
        var corner = new double[5, 5];
        for (int r = 0; r < 5; r++) for (int c = 0; c < 5; c++) corner[r, c] = (r < 2 && c < 2) ? 0 : (r + c) * 4.0 * ((r * 7 + c * 3) % 2 == 0 ? 1 : -1);
        bool moved = SlopeMath.Nudge(corner, 10, out s, out e, out n);
        Check(!moved || Math.Abs(e) + Math.Abs(n) > 0, "slope: nudge gives an offset when it moves");
    }

    static void DvTrackerChecks()
    {
        var planned = new List<double> { 842.0, 362.0, 310.0 };
        var tracker = new DvTracker(planned, 10.0);
        Check(tracker.LegCount == 3, "tracker has three legs");
        Check(Near(tracker.Planned(0), 842.0, 1e-9), "planned dv leg 0");
        Check(Near(tracker.Planned(1), 362.0, 1e-9), "planned dv leg 1");
        Check(Near(tracker.Planned(2), 310.0, 1e-9), "planned dv leg 2");
        Check(double.IsNaN(tracker.Planned(3)), "out-of-range leg returns NaN");
        Check(Near(tracker.Used(0), 0.0, 1e-9), "initial used dv is zero");
        Check(Near(tracker.Remaining(0), 842.0, 1e-9), "initial remaining equals planned");
        Check(Near(tracker.PercentOverUnder(0), -100.0, 1e-9), "initial percent is -100% (nothing used)");
        Check(!tracker.IsBigDifference(0), "no big difference initially");
        Check(!tracker.AlreadyFlagged(0), "not flagged initially");

        tracker.AddSample(0, 100.0);
        Check(Near(tracker.Used(0), 100.0, 1e-9), "used dv after first sample");
        Check(Near(tracker.Remaining(0), 742.0, 1e-9), "remaining = planned - used");
        Check(Near(tracker.PercentOverUnder(0), -100.0 * (842.0 - 100.0) / 842.0, 1e-6), "percent calculation");
        Check(!tracker.IsBigDifference(0), "no big difference at 11.9% under");

        tracker.AddSample(0, 742.0);
        Check(Near(tracker.Used(0), 842.0, 1e-9), "used dv after second sample");
        Check(Near(tracker.Remaining(0), 0.0, 1e-9), "remaining at zero");
        Check(Near(tracker.PercentOverUnder(0), 0.0, 1e-9), "percent at exact match (842 used of 842 planned)");
        Check(!tracker.IsBigDifference(0), "no big difference at exact match");

        tracker.AddSample(0, 84.3);
        Check(Near(tracker.Used(0), 926.3, 1e-9), "used dv after exceeding planned");
        Check(Near(tracker.Remaining(0), -84.3, 1e-9), "remaining negative when over");
        Check(Near(tracker.PercentOverUnder(0), 10.01, 1e-2), "percent at just over 10% over");
        Check(tracker.IsBigDifference(0), "big difference at >10% over");
        Check(!tracker.AlreadyFlagged(0), "not flagged yet");
        tracker.FlagBigDifference(0);
        Check(tracker.AlreadyFlagged(0), "flagged after calling FlagBigDifference");

        var tracker5 = new DvTracker(planned, 10.0);
        tracker5.AddSample(1, 362.0 * 1.05);
        Check(Near(tracker5.Used(1), 362.0 * 1.05, 1e-9), "used dv at 5% over");
        Check(!tracker5.IsBigDifference(1), "no big difference at 5% over");

        double dv = DvTracker.DvFromMassChange(1000.0, 900.0, 300.0);
        Check(Near(dv, 300.0 * 9.80665 * Math.Log(1000.0 / 900.0), 1e-6), "rocket equation matches known example");
        Check(double.IsNaN(DvTracker.DvFromMassChange(0, 900, 300)), "invalid mass0 returns NaN");
        Check(double.IsNaN(DvTracker.DvFromMassChange(1000, 0, 300)), "invalid mass1 returns NaN");
        Check(double.IsNaN(DvTracker.DvFromMassChange(1000, 1100, 300)), "mass1 > mass0 returns NaN");
        Check(double.IsNaN(DvTracker.DvFromMassChange(1000, 900, 0)), "zero Isp returns NaN");
        FuelTimelineChecks();
    }

    static void FuelTimelineChecks()
    {
        var stages = new List<StageInfo>
        {
            new StageInfo { Stage = 0, BurnTime = 60, FuelMass = 4000 },
            new StageInfo { Stage = 1, BurnTime = 120, FuelMass = 2000 },
            new StageInfo { Stage = 2, BurnTime = 180, FuelMass = 1000 }
        };

        var timeline = FuelTimeline.Build(stages);
        Check(timeline != null, "timeline builds from stages");
        Check(Near(timeline.TotalFuel, 7000, 1e-9), "total fuel sum correct");
        Check(Near(timeline.TotalDuration, 360, 1e-9), "total duration sum correct");
        Check(timeline.Events.Count == 9, "3 stages generate 9 events (start, mid, end each)");

        Check(Near(timeline.Events[0].Time, 0, 1e-9) && timeline.Events[0].Type == "stage", "first event is stage start at t=0");
        Check(Near(timeline.Events[1].Time, 30, 1e-9) && timeline.Events[1].Type == "burn", "second event is mid-burn at 30s");
        Check(Near(timeline.Events[2].Time, 60, 1e-9) && timeline.Events[2].Type == "depletion", "third event is depletion at 60s");

        Check(Near(FuelTimeline.FuelAtTime(timeline, 0), 7000, 1e-9), "fuel at start is total");
        Check(Near(FuelTimeline.FuelAtTime(timeline, 30), 5000, 1e-9), "fuel at mid-stage 0 is half");
        Check(Near(FuelTimeline.FuelAtTime(timeline, 60), 3000, 1e-9), "fuel after stage 0 is remaining");
        Check(Near(FuelTimeline.FuelAtTime(timeline, 180), 1000, 1e-9), "fuel after stage 1 is remaining");
        Check(Near(FuelTimeline.FuelAtTime(timeline, 360), 0, 1e-9), "fuel at end is zero");

        Check(FuelTimeline.StageAtTime(timeline, 0) == 0, "stage at start is stage 0");
        Check(FuelTimeline.StageAtTime(timeline, 30) == 0, "stage during stage 0 burn is stage 0");
        Check(FuelTimeline.StageAtTime(timeline, 61) == 1, "stage after stage 0 depletion is stage 1");
        Check(FuelTimeline.StageAtTime(timeline, 181) == 2, "stage after stage 1 depletion is stage 2");
        Check(FuelTimeline.StageAtTime(timeline, 361) == -1, "stage after final depletion is -1");

        var empty = FuelTimeline.Build(new List<StageInfo>());
        Check(empty != null && empty.TotalDuration == 0 && empty.TotalFuel == 0 && empty.Events.Count == 0,
            "empty stages produce empty timeline");

        var single = new List<StageInfo> { new StageInfo { Stage = 0, BurnTime = 100, FuelMass = 5000 } };
        var singleTimeline = FuelTimeline.Build(single);
        Check(singleTimeline.Events.Count == 3, "single stage generates 3 events");
        Check(Near(singleTimeline.TotalDuration, 100, 1e-9), "single stage duration");
    }
}
