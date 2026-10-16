using System;
using System.IO;
using KSPChatBridge;
static class LandingRepairTests
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception("LAND: " + name); }
    internal static void Run()
    {
        Check(Math.Abs(FlightPolicy.GroundTrack(10, 0) - 0) < 1e-9, "ground track north");
        Check(Math.Abs(FlightPolicy.GroundTrack(0, 10) - 90) < 1e-9, "ground track east");
        Check(Math.Abs(FlightPolicy.GroundTrack(-10, 0) - 180) < 1e-9, "ground track south");
        Check(Math.Abs(FlightPolicy.GroundTrack(0, -10) - 270) < 1e-9, "ground track west");
        Check(Math.Abs(FlightPolicy.GroundTrack(10, 10) - 45) < 1e-9, "ground track northeast");
        Check(double.IsNaN(FlightPolicy.GroundTrack(3, 3)), "ground track below minimum speed");
        Check(double.IsNaN(FlightPolicy.GroundTrack(double.NaN, 10)), "ground track rejects NaN");
        RunwayGeometry();
        Check(!PilotPolicy.RudderSteer(1.9, false, 80, true), "localizer bank correction is not suppressed by rudder-only steering");
        var invalid = new LearnFlight();
        invalid.Step(new LearnIn { T = 0, Speed = double.NaN, Alt = 5000, FuelFrac = 1 });
        Check(invalid.Phase == "start", "nonfinite telemetry cannot advance learning");
        var sg = new LearningSampleGate();
        Check(sg.Accept(0, 1, true) && !sg.Accept(1, 2, true), "physics warp samples rejected");
        Check(!sg.Accept(2, 1, true) && !sg.Accept(4.99, 1, true) && sg.Accept(5, 1, true), "three second settle after warp");
        var warped = new LearnFlight(); warped.P.Decel = 2;
        warped.Step(new LearnIn { T = 500, WarpRate = 100, Speed = 100, Alt = 5000, FuelFrac = 1 });
        Check(warped.Phase == "start" && warped.P.Decel == 2 && warped.P.RejectedSamples == 1, "warp neither advances tests nor replaces known values");
        var qp = PlaneProfile.FromDict(warped.P.ToDict());
        Check(qp.LearningSession == warped.P.LearningSession && qp.RejectedSamples == 1, "learning provenance survives persistence");
        Check(FinalApproachPolicy.BankCommand(80, true) == 30 && FinalApproachPolicy.BankCommand(-80, true) == -30, "landing command has a hard signed 30 degree cap");
        Check(FinalApproachPolicy.BankCommand(80, false) == 80 && FinalApproachPolicy.BankCommand(12, true) == 12, "cap preserves nonlanding and modest landing commands");
        Check(LearnFlight.StallTargetFor(12000) == 11000 && LearnFlight.StallTargetFor(7670) == 6670, "stall test is 1000 metres below the learned ceiling");
        var ceilingProfile = new PlaneProfile { Ceiling = 12000 };
        Check(LearnFlight.ProfileChecklist(ceilingProfile, 0).Exists(line => line.Contains("Stall") && line.Contains("11000")), "stall checklist title exposes actual target altitude");
        var oldRunning = MfdNav.LearnRunning;
        try { MfdNav.LearnRunning = () => true; Check(Array.IndexOf(MfdNav.Right("learn"), "PAUSE") >= 0, "running LEARN exposes a PAUSE button"); }
        finally { MfdNav.LearnRunning = oldRunning; }
        var pause = new LearnFlight(); var pi = new LearnIn { T = 0, Speed = 120, Alt = 5000, FuelFrac = 1 };
        pause.Step(pi); pause.Phase = "decel"; pause.SetPaused(true);
        int beforePause = pause.P.AcceptedSamples; pi.T = 1000; pause.Step(pi);
        Check(pause.Paused && pause.Phase == "decel" && pause.P.AcceptedSamples == beforePause && double.IsNaN(pause.Throttle), "pause releases test commands without advancing phase or collecting samples");
        pause.SetPaused(false); pi.T = 1001; pause.Step(pi);
        Check(pause.HoldingTest && pause.Phase == "decel", "resume includes settling instead of skipping dwell timers");
        pi.T = 1004; pause.Step(pi);
        Check(!pause.HoldingTest && pause.Phase == "decel", "resume restarts interrupted segment");
        var oldPaused = MfdNav.LearnPaused;
        try { MfdNav.LearnRunning = () => true; MfdNav.LearnPaused = () => true; Check(Array.IndexOf(MfdNav.Right("learn"), "RESUME") >= 0, "paused LEARN exposes RESUME"); }
        finally { MfdNav.LearnRunning = oldRunning; MfdNav.LearnPaused = oldPaused; }
        var range = new LearnFlight(null, new int[0], true);
        var ri = new LearnIn { T = 0, Speed = 120, Alt = 5000, FuelFrac = 1, HomeKnown = true, HomeDist = 140001, HomeBrg = 210 };
        range.Step(ri);
        Check(range.HoldHdg == 210 && range.P.AcceptedSamples == 0, "outside 140 km return toward KSC without learning the return turn");
        ri.T = 100; ri.HomeDist = 135000; ri.HomeBrg = 200; range.Step(ri);
        Check(range.RangeReturning && range.HoldHdg == 200 && range.P.AcceptedSamples == 0, "range return tracks home with hysteresis");
        range.Request(12);
        Check(range.Phase == "flyhome" && !range.RangeReturning, "LAND preempts a range hold even during setup");
        var returnOnly = new LearnFlight(); returnOnly.Step(ri); ri.HomeDist = 141000; returnOnly.Step(ri);
        ri.T = 200; ri.HomeDist = 129999; returnOnly.Step(ri);
        Check(!returnOnly.RangeReturning && !returnOnly.Done, "test resumes after returning within 130 km");
        var lowFuelRange = new LearnFlight(); ri.HomeDist = 141000; ri.FuelFrac = .2; lowFuelRange.Step(ri);
        Check(lowFuelRange.Done && lowFuelRange.Aborted && !lowFuelRange.HoldingTest, "fuel abort exits range holding and allows landing handoff");
        double ci = 0, crab = 0;
        RunwayMission.CenterlineHeading(50, 270, 270, 80, ref ci, 8, 270, ref crab, false, 1500);
        Check(ci > 0, "persistent 50 metre offset participates in integral correction");
        var rw = new RunwayMission { Lat = 0, Lon = 0, EndLat = 0, EndLon = -.2, Elevation = 70, Phase = "final",
            Chart = new ApproachChart { Elevation = 70, LongAlt = 870, FafD = 10000 } };
        double la, lo; NavigationMath.Offset(0, 0, 90, 5000, 600000, out la, out lo);
        rw.Step(la, lo, 70 + 5350 * .08, 428, 80, false, 600000, 45, 270, 270);
        Check(Math.Abs(rw.DesiredVs + 80 * .08) < .01, "vertical guidance follows the selected chart slope");
        Check(Math.Abs(rw.Ils.AboveGsM) < .01, "ILS display and guidance share the chart glidepath");
        var unsafeFinal = new RunwayMission { Lat = 0, Lon = 0, EndLat = 0, EndLon = -.2, Elevation = 70, Phase = "final" };
        NavigationMath.Offset(0, 0, 90, 800, 600000, out la, out lo);
        NavigationMath.Offset(la, lo, 0, 50, 600000, out la, out lo);
        unsafeFinal.Step(la, lo, 80, 10, 62, false, 600000, 45, 270, 270);
        Check(unsafeFinal.Phase == "go around", "parallel offset must not enter flare below the alignment gate");
        // Pure final-guidance replay: direct/default and generated-chart inputs, both runway heights and offset signs.
        foreach (bool generated in new[] { false, true }) foreach (double elevation in new[] { 0.0, 70.0 }) foreach (double offset in new[] { -50.0, 50.0 })
        {
            var replay = new RunwayMission { Lat = 0, Lon = 0, EndLat = 0, EndLon = -.2, Elevation = elevation, Phase = "final" };
            var chart = ApproachChart.Build(0, 0, 270, elevation, 80, 20, 600000, null);
            if (generated) {
                string why; var ov = ApproachOverride.Parse(MiniJson.Deserialize(EntryPolicy.ChartJson(chart, "replay")), 0, 0, elevation, 600000, out why);
                Check(ov != null, "generated chart parses for replay"); chart.Apply(ov);
            }
            replay.Chart = chart;
            NavigationMath.Offset(0, 0, 90, 8000, 600000, out la, out lo);
            NavigationMath.Offset(la, lo, 0, offset, 600000, out la, out lo);
            double h = 270;
            for (int k = 0; k < 1500; k++) {
                double dist = NavigationMath.Distance(0, 0, la, lo, 600000);
                replay.Step(la, lo, elevation + (dist + 350) * replay.FinalSlope, 500, 80, false, 600000, 45, h, h);
                double error = FlightPolicy.Wrap(replay.DesiredHeading - h);
                double bank = FlightPolicy.Clamp(PilotPolicy.HeadingBank(error, PilotPolicy.RudderSteer(error, false, 80, true)), -30, 30);
                h += 9.81 * Math.Tan(bank * Math.PI / 180) / 80 * 180 / Math.PI * .1;
                NavigationMath.Offset(la, lo, h, 8, 600000, out la, out lo);
                if (replay.Distance < 1500) break;
            }
            Check(replay.Phase == "final" && replay.Distance < 1500 && Math.Abs(replay.Cross) < 5, "final capture converges for chart source/runway/offset matrix: " + replay.Cross);
        }
        string previous = ChartStore.Dir, dir = Path.Combine(Path.GetTempPath(), "landing-" + Guid.NewGuid());
        try {
            ChartStore.Dir = dir;
            ChartStore.Write("KSC 27", "", "{\"fixes\":[]}");
            ChartStore.Write("KSC 27", "generated", "{\"fixes\":[],\"variant\":\"generated\"}");
            Check(ChartStore.PathFor("KSC 27").EndsWith("KSC_27_generated.json"), "fresh generated chart is the automatic default");
            ChartStore.SetActive("KSC 27", "");
            Check(ChartStore.PathFor("KSC 27").EndsWith("KSC_27.json"), "explicit manual default is preserved");
        } finally { ChartStore.Dir = previous; if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    internal static void RunwayGeometry()
    {
        const double radius = 600000, rad = Math.PI / 180;
        // Independent analytic fixture: equator, eastbound runway, midpoint at lon=0.
        // Latitude offset in radians * radius gives the known north/south distance.
        foreach (double north in new[] { -100.0, -30, 0, 30, 100 })
        {
            double lat = north / radius / rad;
            var east = Ils.Compute(lat, 0, 70, 0, -.1, 0, .1, 70, radius);
            var west = Ils.Compute(lat, 0, 70, 0, .1, 0, -.1, 70, radius);
            Check(Math.Abs(east.CrossM + north) < .001, "eastbound right is south, metres " + north);
            Check(Math.Abs(west.CrossM - north) < .001, "westbound right is north, metres " + north);
            Check(Math.Abs(east.CrossM + west.CrossM) < .001, "reciprocal cross-track reverses sign");
            Check(north == 0 ? Math.Abs(east.LocDeg) < 1e-9 : Math.Sign(east.LocDeg) == -Math.Sign(north), "localizer sign");
            double integral = 0, crab = 0;
            double ask = RunwayMission.CenterlineHeading(east.CrossM, 90, 90, 100, ref integral, 0, 90, ref crab, false, 1500);
            Check(north == 0 ? Math.Abs(ask - 90) < 1e-9 : Math.Sign(FlightPolicy.Wrap(ask - 90)) == Math.Sign(north), "heading corrects toward centerline");
        }
        Check(Math.Abs(NavigationMath.Distance(0, 0, 0, 1, radius) - radius * rad) < 1e-6, "distance uses body radius in metres");
        // Independent user survey: these are approximate interior points, not thresholds.
        const double a = -.04882020197652228, x = -74.704418240108154;
        const double b = -.050267211662821734, y = -74.513222861563392;
        Check(Math.Abs(NavigationMath.Distance(a, x, b, y, radius) - 2002.25) < .02, "survey separation");
        Check(Math.Abs(NavigationMath.Bearing(a, x, b, y) - 90.43370) < .00002, "survey eastbound bearing");
        Check(Math.Abs(NavigationMath.Bearing(b, y, a, x) - 270.4337) < .001, "survey reciprocal bearing");
        var atWest = Ils.Compute(a, x, 70, a, x, b, y, 70, radius);
        var atEast = Ils.Compute(b, y, 70, a, x, b, y, 70, radius);
        Check(Math.Abs(atWest.CrossM) < 1e-6 && Math.Abs(atEast.CrossM) < 1e-6, "survey line endpoints have zero cross-track");
        var codeWest = Ils.Compute(a, x, 70, KscRunway.Lat09, KscRunway.Lon09, KscRunway.Lat27, KscRunway.Lon27, 69.1, radius);
        var codeEast = Ils.Compute(b, y, 70, KscRunway.Lat09, KscRunway.Lon09, KscRunway.Lat27, KscRunway.Lon27, 69.1, radius);
        Check(Math.Abs(NavigationMath.Bearing(KscRunway.Lat09, KscRunway.Lon09, KscRunway.Lat27, KscRunway.Lon27) - 90.4337) < .01, "coded runway bearing matches surveyed eastbound heading");
        Check(Math.Abs(NavigationMath.Bearing(KscRunway.Lat27, KscRunway.Lon27, KscRunway.Lat09, KscRunway.Lon09) - 270.4337) < .01, "coded runway bearing matches surveyed reciprocal heading");
        Check(Math.Abs(codeWest.CrossM) < .5 && Math.Abs(codeEast.CrossM) < .5, "survey points lie on coded runway centerline");
        Console.WriteLine("Survey versus current runway: west cross={0:F2} m, east cross={1:F2} m", codeWest.CrossM, codeEast.CrossM);
        Console.WriteLine("Runway geometry: 30 checks passed (analytic offsets, reciprocal signs, units, survey alignment).");
    }
}
