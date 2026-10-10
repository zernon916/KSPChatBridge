using System;
using System.Collections.Generic;
using System.IO;

namespace KSPChatBridge
{
    /// <summary>Taxi routes as ground charts (Luke): one file per route in PluginData/charts (TAXI_&lt;name&gt;.json), directional,
    /// hot-swapped, editable on the Charts page. A route is an ordered list of legs (polyline). From wherever the craft stops it
    /// joins the nearest point AHEAD on the line (never behind it) and then follows the centerline (cross-track, pure pursuit).
    /// Pure (tested); the mod side reads buildings and drives TaxiMission.</summary>
    internal sealed class TaxiRoute
    {
        internal sealed class Wp { internal string Name; internal double Lat, Lon; }
        internal string Name = "", From = "", To = "";
        internal readonly List<Wp> Points = new List<Wp>();
        internal const string Prefix = "TAXI_";
        internal const double Lookahead = 30, MaxJoinM = 400;

        internal static string FileName(string name) { return Prefix + name.Trim().Replace(' ', '_') + ".json"; }

        internal string ToJson()
        {
            var pts = new List<object>();
            foreach (var p in Points) pts.Add(new Dictionary<string, object> { { "name", p.Name ?? "" }, { "lat", Math.Round(p.Lat, 7) }, { "lon", Math.Round(p.Lon, 7) } });
            return MiniJson.Serialize(new Dictionary<string, object> { { "type", "taxi" }, { "name", Name }, { "from", From }, { "to", To }, { "points", pts } });
        }

        /// <summary>null + why on bad JSON / fewer than 2 points (the old route stays active).</summary>
        internal static TaxiRoute Parse(string json, out string why)
        {
            why = null; Dictionary<string, object> d = null;
            try { d = MiniJson.Deserialize(json); } catch (Exception ex) { why = "bad JSON: " + ex.Message; return null; }
            if (d == null) { why = "bad JSON"; return null; }
            object v; var r = new TaxiRoute();
            if (d.TryGetValue("name", out v) && v != null) r.Name = Convert.ToString(v);
            if (d.TryGetValue("from", out v) && v != null) r.From = Convert.ToString(v);
            if (d.TryGetValue("to", out v) && v != null) r.To = Convert.ToString(v);
            var list = d.TryGetValue("points", out v) ? v as System.Collections.IList : null;
            if (list != null) foreach (var o in list)
            {
                var p = o as Dictionary<string, object>; if (p == null) continue; object la, lo, n;
                if (!p.TryGetValue("lat", out la) || !p.TryGetValue("lon", out lo)) continue;
                double lat = Convert.ToDouble(la), lon = Convert.ToDouble(lo);
                if (double.IsNaN(lat) || double.IsNaN(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) { why = "bad point"; return null; }
                r.Points.Add(new Wp { Name = p.TryGetValue("name", out n) && n != null ? Convert.ToString(n) : "", Lat = lat, Lon = lon });
            }
            if (r.Points.Count < 2) { why = "a taxi route needs at least 2 points"; return null; }
            if (r.Points.Count > 60) { why = "max 60 points"; return null; }
            return r;
        }

        /// <summary>Along/cross-track of (lat,lon) on leg i (flat-earth metres, fine for taxi distances).</summary>
        internal void LegFrame(int i, double lat, double lon, double radius, out double along, out double cross, out double len)
        {
            var a = Points[i]; var b = Points[i + 1];
            double k = Math.PI / 180 * radius, c = Math.Cos(a.Lat * Math.PI / 180);
            double bx = (b.Lon - a.Lon) * k * c, by = (b.Lat - a.Lat) * k, px = (lon - a.Lon) * k * c, py = (lat - a.Lat) * k;
            len = Math.Sqrt(bx * bx + by * by); if (len < 1e-6) { along = 0; cross = Math.Sqrt(px * px + py * py); return; }
            along = (px * bx + py * by) / len; cross = (px * by - py * bx) / len;   // + = right of the leg
        }

        /// <summary>Join: the leg whose closest point is nearest (directional: the join is at or ahead of the projection on that
        /// leg, never back up a leg). Returns the leg index, -1 when the whole route is farther than MaxJoinM.</summary>
        internal int Join(double lat, double lon, double radius)
        {
            int best = -1; double bestD = double.MaxValue;
            for (int i = 0; i + 1 < Points.Count; i++)
            {
                double al, cr, len; LegFrame(i, lat, lon, radius, out al, out cr, out len);
                double off = al < 0 ? -al : al > len ? al - len : 0, d = Math.Sqrt(off * off + cr * cr);
                if (al > len && i + 2 < Points.Count) d += 1;   // past this leg's end: prefer the next leg
                if (d < bestD) { bestD = d; best = i; }
            }
            return bestD > MaxJoinM ? -1 : best;
        }

        /// <summary>Pure-pursuit target on the centerline: Lookahead metres ahead of the projection on leg i, spilling into the
        /// next legs; advances i when past a leg end. done = the last point is reached.</summary>
        internal void Target(ref int leg, double lat, double lon, double radius, out double tLat, out double tLon, out double cross, out double toEnd)
        {
            double al, len; LegFrame(leg, lat, lon, radius, out al, out cross, out len);
            while (al >= len - 1 && leg + 2 < Points.Count) { leg++; LegFrame(leg, lat, lon, radius, out al, out cross, out len); }
            double ahead = Math.Max(0, al) + Lookahead; int i = leg; double rem = ahead;
            while (rem > len && i + 2 < Points.Count) { rem -= len; i++; double a2, c2; LegFrame(i, Points[i].Lat, Points[i].Lon, radius, out a2, out c2, out len); }
            rem = Math.Min(rem, len);
            var a = Points[i]; var b = Points[i + 1]; double f = len < 1e-6 ? 1 : rem / len;
            tLat = a.Lat + (b.Lat - a.Lat) * f; tLon = a.Lon + (b.Lon - a.Lon) * f;
            toEnd = Math.Max(0, len - Math.Max(0, al)); for (int j = leg + 1; j + 1 < Points.Count; j++) { double a3, c3, l3; LegFrame(j, Points[j].Lat, Points[j].Lon, radius, out a3, out c3, out l3); toEnd += l3; }
        }

        // ---- default KSC routes (built from the in-game SPH / KSC hub positions the first time, then edited as files) ----
        internal static double ExitLon = -74.6073;   // runway middle: the taxiway into the KSC
        internal static List<TaxiRoute> KscDefaults(double sphLat, double sphLon, double hubLat, double hubLon)
        {
            double lat = KscRunway.Lat;
            Func<string, double, double, Wp> W = (n, a, o) => new Wp { Name = n, Lat = a, Lon = o };
            // apron point in front of the SPH (hub side) and a taxiway point between the runway exit and the hub
            double t = .5, frontLat = sphLat + (hubLat - sphLat) * .25, frontLon = sphLon + (hubLon - sphLon) * .25;
            Wp exit = W("RWY EXIT", lat, ExitLon), twy = W("TWY", lat + (hubLat - lat) * t, ExitLon + (hubLon - ExitLon) * t), hub = W("HUB", hubLat, hubLon), apron = W("SPH APRON", frontLat, frontLon);
            var res = new List<TaxiRoute>();
            Func<string, string, string, Wp[], TaxiRoute> R = (n, f, to, ps) => { var r = new TaxiRoute { Name = n, From = f, To = to }; r.Points.AddRange(ps); return r; };
            // from either runway end: roll along the centerline to the exit, then in
            res.Add(R("RWY27_TO_SPH", "runway 27", "SPH", new[] { W("RWY 27 THR", lat, KscRunway.Lon27), exit, twy, hub, apron }));
            res.Add(R("RWY09_TO_SPH", "runway 09", "SPH", new[] { W("RWY 09 THR", lat, KscRunway.Lon09), exit, twy, hub, apron }));
            res.Add(R("SPH_TO_RWY09", "SPH", "runway 09", new[] { apron, hub, twy, exit, W("RWY 09 THR", lat, KscRunway.Lon09 + .002) }));
            res.Add(R("SPH_TO_RWY27", "SPH", "runway 27", new[] { apron, hub, twy, exit, W("RWY 27 THR", lat, KscRunway.Lon27 - .002) }));
            return res;
        }

        /// <summary>Which route file for a destination ("hangar"/"sph", "09", "27") from where the craft is (lon picks the runway leg).</summary>
        internal static string Pick(string dest, double lat, double lon)
        {
            string d = (dest ?? "").ToLowerInvariant();
            if (d.Contains("09")) return "SPH_TO_RWY09";
            if (d.Contains("27")) return "SPH_TO_RWY27";
            bool onRunway = Math.Abs(lat - KscRunway.Lat) < .003 && lon > KscRunway.Lon09 - .003 && lon < KscRunway.Lon27 + .003;
            return onRunway && lon < ExitLon ? "RWY09_TO_SPH" : "RWY27_TO_SPH";   // west of the exit: the 09 leg (eastbound); else the 27 leg
        }

        internal static List<TaxiRoute> LoadAll(string dir)
        {
            var res = new List<TaxiRoute>();
            if (dir == null || !Directory.Exists(dir)) return res;
            foreach (var f in Directory.GetFiles(dir, Prefix + "*.json")) { string why; var r = Parse(File.ReadAllText(f), out why); if (r != null) res.Add(r); }
            return res;
        }
    }
}