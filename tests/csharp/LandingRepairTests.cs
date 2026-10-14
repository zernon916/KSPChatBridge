using System;
using System.IO;
using KSPChatBridge;
static class LandingRepairTests
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception("LAND: " + name); }
    internal static void Run()
    {
        Check(!PilotPolicy.RudderSteer(1.9, false, 80, true), "localizer bank correction is not suppressed by rudder-only steering");
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
