using System;
using System.IO;
using KSPChatBridge;
static class LandingRepairTests
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception("LAND: " + name); }
    internal static void Run()
    {
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
}
