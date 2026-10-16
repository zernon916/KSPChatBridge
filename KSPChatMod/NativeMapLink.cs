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
                outp.Add(new MapRunway { Key = "KSC 09", Lat = KscRunway.Lat09, Lon = KscRunway.Lon09, EndLat = KscRunway.Lat27, EndLon = KscRunway.Lon27, Elevation = 69.1 });
                outp.Add(new MapRunway { Key = "KSC 27", Lat = KscRunway.Lat27, Lon = KscRunway.Lon27, EndLat = KscRunway.Lat09, EndLon = KscRunway.Lon09, Elevation = 69.1 });
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

        internal static string ApproachPath { get { return Path.Combine(AicsCore.PluginDataDirectory, "approaches.json"); } }

        static bool chartsReady;
        /// <summary>PluginData/charts (hot-swappable per-end chart files); migrates approaches.json once.</summary>
        internal static void EnsureCharts()
        {
            if (chartsReady) return; chartsReady = true;
            ChartStore.Dir = Path.Combine(AicsCore.PluginDataDirectory, "charts");
            try
            {   // shipped default charts (e.g. KSC_09_tight.json) are copied once, never over the player's files
                string defs = Path.Combine(Path.GetDirectoryName(AicsCore.PluginDataDirectory.TrimEnd('/', '\\')), "Defaults", "charts");
                if (Directory.Exists(defs)) { Directory.CreateDirectory(ChartStore.Dir); foreach (var f in Directory.GetFiles(defs, "*.json")) { string to = Path.Combine(ChartStore.Dir, Path.GetFileName(f)); if (!File.Exists(to)) File.Copy(f, to); } }
            }
            catch (Exception ex) { ChatLog.Write("approach", "default charts not copied: " + ex.Message); }
            try { int n = ChartStore.Migrate(ApproachPath); if (n > 0) ChatLog.Write("approach", "migrated " + n + " charts from approaches.json to charts/ (backup approaches.json.premigrate.bak)"); }
            catch (Exception ex) { ChatLog.Write("approach", "chart migration failed: " + ex.Message); }
        }
        /// <summary>Luke: HEAD-ON / SIDE / REAR charts for every runway end on this body, sized from this plane's turn envelope.
        /// Written as KEY_generated.json (reference / selectable variant); never touches the player's own chart files.</summary>
        void GenerateCharts(CelestialBody body)
        {
            try
            {
                double v = Math.Max(ApproachProfile.Speed(stall), 60); int n = 0;
                foreach (var rw in MapRunways(body))
                {
                    double crs = NavigationMath.Bearing(rw.Lat, rw.Lon, rw.EndLat, rw.EndLon);
                    var c = ApproachChart.Build(rw.Lat, rw.Lon, crs, rw.Elevation, v, 0, body.Radius, (la, lo) => MapTerrain(body, la, lo));
                    ChartStore.Write(rw.Key, "generated", EntryPolicy.ChartJson(c, "generated for " + vessel.vesselName + (ChartDesign.Known ? " (learned profile " + ChartDesign.Key + ", FAF " + (c.FafD / 1000).ToString("0.0") + " km)" : " (no learned profile: fixed chart)") + ": join r " + Math.Round(c.TurnRadius) + " m at " + Math.Round(v) + " m/s")); n++;
                }
                ChatLog.Write("approach", "generated HEAD-ON/SIDE/REAR charts for " + n + " runway ends on " + body.bodyName + " (KEY_generated.json; your own chart files untouched)");
            }
            catch (Exception ex) { ChatLog.Write("approach", "chart generation failed: " + ex.Message); }
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
