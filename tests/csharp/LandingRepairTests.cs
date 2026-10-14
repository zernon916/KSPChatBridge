using System;
using System.IO;
using KSPChatBridge;
static class LandingRepairTests
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception("LAND: " + name); }
    internal static void Run()
    {
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
