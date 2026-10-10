using System;
using System.Collections.Generic;
using System.IO;

namespace KSPChatBridge
{
    /// <summary>Map window hooks into the native controller: active approach, runway catalog, chart overrides.</summary>
    public partial class NativeFlightController
    {
        internal sealed class MapRunway { internal string Key; internal double Lat, Lon, EndLat, EndLon, Elevation; }

        internal static double MapStall { get { return instance != null && instance.vessel != null ? instance.stall : 45; } }
        internal static RunwayMission ActiveRunway { get { return instance != null && instance.mode == "landing" ? instance.runway : null; } }

        internal static List<MapRunway> MapRunways(CelestialBody body)
        {
            var outp = new List<MapRunway>();
            if (body == null) return outp;
            if (body.bodyName == "Kerbin")
            {
                outp.Add(new MapRunway { Key = "KSC 09", Lat = -.0485997, Lon = -74.724375, EndLat = -.0485997, EndLon = -74.490300, Elevation = 69.1 });
                outp.Add(new MapRunway { Key = "KSC 27", Lat = -.0485997, Lon = -74.490300, EndLat = -.0485997, EndLon = -74.724375, Elevation = 69.1 });
                outp.Add(new MapRunway { Key = "Island 27", Lat = -1.516092, Lon = -71.856744, EndLat = -1.514809, EndLon = -71.961815, Elevation = 134.6 });
                outp.Add(new MapRunway { Key = "Island 09", Lat = -1.514809, Lon = -71.961815, EndLat = -1.516092, EndLon = -71.856744, Elevation = 134.6 });
            }
            if (instance == null || instance.spots == null) return outp;
            foreach (var pt in instance.spots.Points(body.bodyName))
            {
                try
                {
                    var row = instance.spots.Find(pt.Name, body.bodyName); object mv;
                    if (row == null || !row.TryGetValue("mode", out mv) || !"H".Equals(mv)) continue;
                    var rw = instance.spots.Runway(pt.Name, body.bodyName, body.Radius, (la, lo) => MapTerrain(body, la, lo));
                    if (rw != null) outp.Add(new MapRunway { Key = pt.Name, Lat = rw.Lat, Lon = rw.Lon, EndLat = rw.EndLat, EndLon = rw.EndLon, Elevation = rw.Elevation });
                }
                catch (ArgumentException) { }
            }
            return outp;
        }

        internal static List<double[]> SpotPositions(CelestialBody body)
        {
            var outp = new List<double[]>();
            if (instance != null && instance.spots != null && body != null) foreach (var p in instance.spots.Points(body.bodyName)) outp.Add(new[] { p.Lat, p.Lon });
            return outp;
        }

        internal static double MapTerrain(CelestialBody body, double lat, double lon)
        {
            if (body == null || body.pqsController == null) return double.NaN;
            try { return Math.Max(0, body.TerrainAltitude(lat, lon, false)); } catch (Exception) { return double.NaN; }
        }

        internal static string ApproachPath { get { return Path.Combine(BridgeLauncher.PluginDataDirectory, "approaches.json"); } }

        static bool chartsReady;
        /// <summary>PluginData/charts (hot-swappable per-end chart files); migrates approaches.json once.</summary>
        internal static void EnsureCharts()
        {
            if (chartsReady) return; chartsReady = true;
            ChartStore.Dir = Path.Combine(BridgeLauncher.PluginDataDirectory, "charts");
            try { int n = ChartStore.Migrate(ApproachPath); if (n > 0) ChatLog.Write("approach", "migrated " + n + " charts from approaches.json to charts/ (backup approaches.json.premigrate.bak)"); }
            catch (Exception ex) { ChatLog.Write("approach", "chart migration failed: " + ex.Message); }
        }
        internal static ApproachOverride LoadOverride(string key, MapRunway rw, CelestialBody body, out string why)
        {
            why = "";
            try
            {
                EnsureCharts(); object end = ChartStore.Read(key, out why); if (end == null) return null;
                return ApproachOverride.Parse(end, rw.Lat, rw.Lon, rw.Elevation, body.Radius, out why, (la, lo) => MapTerrain(body, la, lo));
            }
            catch (Exception ex) { why = ex.Message; return null; }
        }
    }
}
