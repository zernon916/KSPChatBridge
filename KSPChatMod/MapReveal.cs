using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KSPChatBridge
{
    /// <summary>Fog-of-war for the AICS map (pure; tested). Airports always revealed; without a mapping mod the craft's
    /// flight path reveals a few km around it (more with height); with SCANsat its coverage is used instead.</summary>
    internal static class MapReveal
    {
        internal const double TileDeg = 0.02, AirportM = 6000, MinM = 3000, MaxM = 12000, StepM = 500;
        static readonly Dictionary<string, HashSet<long>> Tiles = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        static readonly object Gate = new object();
        static double lastLat = double.NaN, lastLon = double.NaN; static string lastBody = "";

        internal sealed class Site { internal string Name; internal double Lat, Lon; internal bool Pad; }
        /// <summary>Stock Kerbin airports + launch sites (incl. Making History).</summary>
        internal static readonly Site[] Airports = {
            new Site { Name = "KSC", Lat = -.0494, Lon = -74.6074 }, new Site { Name = "KSC Launch Pad", Lat = -.0972, Lon = -74.5577, Pad = true },
            new Site { Name = "Island Airfield", Lat = -1.5155, Lon = -71.9093 }, new Site { Name = "Desert Airfield", Lat = -6.5999, Lon = -144.0405 },
            new Site { Name = "Desert Launch Site", Lat = -6.5604, Lon = -143.9500, Pad = true }, new Site { Name = "Woomerang Launch Site", Lat = 45.2900, Lon = 136.1100, Pad = true } };

        internal const double MinSpan = 2000, MaxSpan = 300000;
        /// <summary>Map zoom (+/- buttons x0.5/x2, wheel x0.8/x1.25), clamped 2-300 km across.</summary>
        internal static double Zoom(double span, double factor) { return Math.Max(MinSpan, Math.Min(MaxSpan, span * factor)); }
        internal static double RadiusFor(double agl) { return Math.Max(MinM, Math.Min(MaxM, MinM + 0.5 * Math.Max(0, agl))); }
        static long Key(int row, int col) { return ((long)row << 20) | (uint)col; }
        static int Row(double lat) { return (int)Math.Floor((lat + 90) / TileDeg); }
        static int Col(double lon) { lon = ((lon + 180) % 360 + 360) % 360; return (int)Math.Floor(lon / TileDeg); }
        static double DistM(double la1, double lo1, double la2, double lo2, double r)
        {
            double p1 = la1 * Math.PI / 180, p2 = la2 * Math.PI / 180, dp = p2 - p1, dl = (lo2 - lo1) * Math.PI / 180;
            double h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }

        /// <summary>Reveal tiles within RadiusFor(agl) of the craft; skipped until it moved StepM since the last reveal. Returns tiles added.</summary>
        internal static int Fly(string body, double lat, double lon, double agl, double radius)
        {
            if (body == lastBody && !double.IsNaN(lastLat) && DistM(lat, lon, lastLat, lastLon, radius) < StepM) return 0;
            lastBody = body; lastLat = lat; lastLon = lon;
            return RevealDisk(body, lat, lon, RadiusFor(agl), radius);
        }

        internal static int RevealDisk(string body, double lat, double lon, double rM, double radius)
        {
            double dLat = rM / radius * 180 / Math.PI, dLon = dLat / Math.Max(.05, Math.Cos(lat * Math.PI / 180));
            int added = 0;
            lock (Gate)
            {
                HashSet<long> set; if (!Tiles.TryGetValue(body, out set)) Tiles[body] = set = new HashSet<long>();
                for (double la = lat - dLat; la <= lat + dLat; la += TileDeg)
                    for (double lo = lon - dLon; lo <= lon + dLon; lo += TileDeg)
                    {
                        double cla = (Row(la) + .5) * TileDeg - 90, clo = (Col(lo) + .5) * TileDeg - 180;
                        if (DistM(lat, lon, cla, clo, radius) <= rM + TileDeg * Math.PI / 180 * radius * .7 && set.Add(Key(Row(la), Col(lo)))) added++;
                    }
            }
            return added;
        }

        internal static bool NearAirport(string body, double lat, double lon, double radius, IEnumerable<double[]> extra = null)
        {
            if (string.Equals(body, "Kerbin", StringComparison.OrdinalIgnoreCase))
                foreach (var a in Airports) if (DistM(lat, lon, a.Lat, a.Lon, radius) < AirportM) return true;
            if (extra != null) foreach (var p in extra) if (DistM(lat, lon, p[0], p[1], radius) < AirportM) return true;
            return false;
        }

        /// <summary>covered: SCANsat coverage test (null = no mapping mod -> flight-path tiles).</summary>
        internal static bool Revealed(string body, double lat, double lon, double radius, Func<double, double, bool> covered, IEnumerable<double[]> extra = null)
        {
            if (NearAirport(body, lat, lon, radius, extra)) return true;
            if (covered != null) return covered(lat, lon);
            lock (Gate) { HashSet<long> set; return Tiles.TryGetValue(body, out set) && set.Contains(Key(Row(lat), Col(lon))); }
        }

        internal static int Count(string body) { lock (Gate) { HashSet<long> s; return Tiles.TryGetValue(body, out s) ? s.Count : 0; } }
        internal static void Clear() { lock (Gate) Tiles.Clear(); lastLat = double.NaN; lastBody = ""; }

        /// <summary>Compact per-body text: sorted tile keys as delta varints, base64.</summary>
        internal static Dictionary<string, string> Serialize()
        {
            var outp = new Dictionary<string, string>();
            lock (Gate)
                foreach (var kv in Tiles)
                {
                    var keys = new List<long>(kv.Value); keys.Sort(); var bytes = new List<byte>(); long prev = 0;
                    foreach (var k in keys) { ulong d = (ulong)(k - prev); prev = k; while (d >= 0x80) { bytes.Add((byte)(d | 0x80)); d >>= 7; } bytes.Add((byte)d); }
                    outp[kv.Key] = Convert.ToBase64String(bytes.ToArray());
                }
            return outp;
        }

        internal static void Load(string body, string data)
        {
            var set = new HashSet<long>();
            try
            {
                var b = Convert.FromBase64String(data ?? ""); long prev = 0; ulong cur = 0; int shift = 0;
                foreach (var x in b) { cur |= (ulong)(x & 0x7f) << shift; if ((x & 0x80) != 0) { shift += 7; continue; } prev += (long)cur; set.Add(prev); cur = 0; shift = 0; }
            }
            catch (FormatException) { }
            lock (Gate) Tiles[body] = set;
        }
    }

    /// <summary>approaches.json read/merge for the in-game chart editor (pure; tested).</summary>
    internal static class ApproachFile
    {
        internal sealed class EditFix { internal string Role, Name, Side = "both", Ref = "agl"; internal double Lat, Lon, AltMsl; }
        /// <summary>Replace one runway end in the file text, keeping other ends; altitudes written per fix (agl = MSL - terrain under the fix).</summary>
        internal static string Merge(string existing, string key, List<EditFix> fixes, Func<double, double, double> terrain, double flareStart = 15, double flareSink = 1, double touchdown = 350, double tch = 15)
        {
            Dictionary<string, object> all = null;
            try { all = string.IsNullOrEmpty(existing) ? null : MiniJson.Deserialize(existing); } catch (Exception) { }
            if (all == null) all = new Dictionary<string, object>();
            object old; var prev = all.TryGetValue(key, out old) ? old as Dictionary<string, object> : null;
            var list = new List<object>();
            foreach (var f in fixes)
            {
                double g = terrain == null ? double.NaN : terrain(f.Lat, f.Lon); if (double.IsNaN(g)) g = 0;
                double alt = f.Ref == "msl" ? f.AltMsl : Math.Max(0, f.AltMsl - g);
                list.Add(new Dictionary<string, object> { { "role", f.Role }, { "side", f.Side }, { "alt_ref", f.Ref }, { "name", f.Name }, { "lat", Math.Round(f.Lat, 5) }, { "lon", Math.Round(f.Lon, 5) }, { "alt", Math.Round(alt) } });
            }
            Func<string, double, object> keep = (k, def) => { object v; return prev != null && prev.TryGetValue(k, out v) ? v : def; };
            all[key] = new Dictionary<string, object> { { "alt_ref", "agl" }, { "fixes", list }, { "flare_start_m", keep("flare_start_m", flareStart) }, { "flare_sink_ms", keep("flare_sink_ms", flareSink) }, { "touchdown_m", keep("touchdown_m", touchdown) }, { "tch_m", keep("tch_m", tch) } };
            return MiniJson.Serialize(all);
        }
    }
}
