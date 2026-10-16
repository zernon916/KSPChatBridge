using System;
using KSPChatBridge;

static class GpsInfoTests
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception("GPS: " + name); }
    internal static void Run()
    {
        double r = 600000;
        Check(Math.Abs(GpsInfo.DistanceM(KscRunway.Lat09, KscRunway.Lon09, KscRunway.Lat27, KscRunway.Lon27, r) - 2450) < 30, "runway end distance");
        Check(Math.Abs(GpsInfo.BearingDeg(KscRunway.Lat09, KscRunway.Lon09, KscRunway.Lat27, KscRunway.Lon27) - 90.4) < .2, "09 to 27 bearing");
        Check(Math.Abs(GpsInfo.BearingDeg(KscRunway.Lat27, KscRunway.Lon27, KscRunway.Lat09, KscRunway.Lon09) - 270.4) < .2, "27 to 09 bearing");
        Check(Math.Abs(GpsInfo.CenterlineOffsetM(KscRunway.Lat09, KscRunway.Lon09, r)) < .1, "west end lies on centerline");
        double midLat = (KscRunway.Lat09 + KscRunway.Lat27) / 2 + .001, midLon = (KscRunway.Lon09 + KscRunway.Lon27) / 2;
        Check(Math.Abs(GpsInfo.CenterlineOffsetM(midLat, midLon, r) - 10.5) < 1, "north offset positive");
        string line = GpsInfo.Format("Foo", "Kerbin", r, -.04867, -74.72438, 1234, 1200, 90, double.NaN, 120.5, false, "flying", 12345);
        Check(line.Contains("lat -0.04867") && line.Contains("trk n/a") && line.Contains("rwy 09"), "formatted coordinates, slow track and runway");
        Check(!GpsInfo.Format("Foo", "Mun", r, 0, 0, 0, 0, 0, 0, 0, false, "flying", 0).Contains("rwy"), "no runway off Kerbin");
        Check(GpsInfo.Compass(359.7) == "000" && GpsInfo.Compass(-1) == "359" && GpsInfo.Compass(double.NaN) == "n/a", "compass wraps 000-359");
        Check(GpsInfo.Format("Foo", "Kerbin", r, -.0486, -74.6, 70, 2, 90, 90, 0, true, "SPLASHED", 1).Contains("| SPLASHED |"), "situation shown");
        Check(ToolRouter.Direct("what are my coordinates?", "plane").Value.Key == "get_gps", "direct coordinates route");
    }
}
