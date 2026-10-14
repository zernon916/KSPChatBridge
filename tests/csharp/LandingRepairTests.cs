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
        double ci = 0, crab = 0;
        RunwayMission.CenterlineHeading(50, 270, 270, 80, ref ci, 8, 270, ref crab, false, 1500);
        Check(ci > 0, "persistent 50 metre offset participates in integral correction");
        var rw = new RunwayMission { Lat = 0, Lon = 0, EndLat = 0, EndLon = -.2, Elevation = 70, Phase = "final",
            Chart = new ApproachChart { Elevation = 70, LongAlt = 870, FafD = 10000 } };
        double la, lo; NavigationMath.Offset(0, 0, 90, 5000, 600000, out la, out lo);
        rw.Step(la, lo, 70 + 5350 * .08, 428, 80, false, 600000, 45, 270, 270);
        Check(Math.Abs(rw.DesiredVs + 80 * .08) < .01, "vertical guidance follows the selected chart slope");
        Check(Math.Abs(rw.Ils.AboveGsM) < .01, "ILS display and guidance share the chart glidepath");
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
