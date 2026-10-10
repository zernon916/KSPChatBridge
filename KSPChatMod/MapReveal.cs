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
        /// <summary>Pan the map centre by 25% of the shown width; heading 0/90/180/270 = up/right/down/left.</summary>
        internal static void Pan(ref double lat, ref double lon, double heading, double span, double radius)
        { double la, lo; NavigationMath.Offset(lat, lon, heading, .25 * span, radius, out la, out lo); lat = la; lon = lo; }
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

    /// <summary>ILS-style raw data for a runway end (pure; tested): localizer (antenna at the far end) and 3 deg glideslope
    /// (antenna at the touchdown point) deviations, DME to the threshold, cross-track and height vs the glide path.</summary>
    internal static class Ils
    {
        internal const double GlideDeg = 3, LocFullScale = 2.5, GsFullScale = .7;
        internal sealed class Reading { internal double LocDeg, GsDeg, DmeM, CrossM, AboveGsM, Course; internal bool Front; }
        internal static Reading Compute(double lat, double lon, double alt, double thrLat, double thrLon, double endLat, double endLon, double elevation, double radius, double touchdownM = 300)
        {
            double crs = NavigationMath.Bearing(thrLat, thrLon, endLat, endLon), len = NavigationMath.Distance(thrLat, thrLon, endLat, endLon, radius);
            double d = NavigationMath.Distance(thrLat, thrLon, lat, lon, radius), th = FlightPolicy.Wrap(NavigationMath.Bearing(thrLat, thrLon, lat, lon) - crs) * Math.PI / 180;
            double along = d * Math.Cos(th), cross = d * Math.Sin(th);   // along < 0 = before the threshold; cross > 0 = right of centerline
            var r = new Reading { Course = crs, DmeM = d, CrossM = cross, Front = along < len };
            r.LocDeg = Math.Atan2(cross, len - along) * 180 / Math.PI;                       // + = right of the localizer course (fly left)
            double toGs = touchdownM - along, path = elevation + Math.Max(0, toGs) * Math.Tan(GlideDeg * Math.PI / 180);
            r.AboveGsM = alt - path;
            r.GsDeg = Math.Atan2(alt - elevation, Math.Max(1, toGs)) * 180 / Math.PI - GlideDeg; // + = above the glideslope (fly down)
            return r;
        }
        /// <summary>Hand-flying flight director: heading to steer onto the localizer, VS to hold/capture the 3 deg path (pure; tested).</summary>
        internal static double FdHeading(Reading r, double speed) { return (r.Course - FlightPolicy.Clamp(Math.Atan2(r.CrossM, Math.Max(1500, 10 * speed)) * 180 / Math.PI, -30, 30) + 360) % 360; }
        internal static double FdVs(Reading r, double speed) { return FlightPolicy.Clamp(-speed * Math.Tan(GlideDeg * Math.PI / 180) - .1 * r.AboveGsM, -15, 3); }
        /// <summary>IAS vs target approach speed: green within 5, amber within 10, else red.</summary>
        internal static string SpeedBand(double ias, double target) { double d = Math.Abs(ias - target); return d <= 5 ? "green" : d <= 10 ? "amber" : "red"; }
        /// <summary>Text callouts (no audio): GEAR inside 3 km gear up, TOO FAST/SLOW, GLIDESLOPE off the path, MINIMUMS at 60 m above the threshold.</summary>
        internal static List<string> Callouts(Reading r, double hat, bool gearDown, double ias, double target)
        {
            var c = new List<string>(); if (!r.Front) return c;
            if (!gearDown && r.DmeM < 3000) c.Add("GEAR");
            if (ias > target + 10) c.Add("TOO FAST"); else if (ias < target - 5) c.Add("SLOW");
            if (r.DmeM < 10000 && Math.Abs(r.AboveGsM) > Math.Max(15, .02 * r.DmeM)) c.Add("GLIDESLOPE");
            if (hat < 60 && hat > 20) c.Add("MINIMUMS");
            return c;
        }
        /// <summary>Localizer capture zone: in front, within range, inside the 35 deg ILS capture sector.</summary>
        internal static bool InLocCaptureZone(Reading r, double range) { return r.Front && r.DmeM < range && Math.Abs(r.LocDeg) < 35; }
        /// <summary>Needle position -1..1 (full-scale deflection), sign as the deviation.</summary>
        internal static double Needle(double dev, double fullScale) { return FlightPolicy.Clamp(dev / fullScale, -1, 1); }
    }

    /// <summary>Text pages for IVA MFDs (RasterPropMonitor PAGEHANDLER; pure, tested). Fixed-width characters, cols x rows.</summary>
    internal static class MfdText
    {
        static string Bar(double needle, int width, char mark)
        {
            int w = Math.Max(5, width), mid = w / 2, at = (int)Math.Round(mid + needle * mid); var c = new char[w];
            for (int i = 0; i < w; i++) c[i] = i == mid ? '|' : (i % Math.Max(1, w / 8) == 0 ? '.' : ' ');
            c[Math.Max(0, Math.Min(w - 1, at))] = mark; return new string(c);
        }
        internal static string IlsPage(Ils.Reading r, string label, string couple, bool gear, bool brakes, double ias, double target, double hat, double vs, int cols, int rows)
        {
            var sb = new StringBuilder(); cols = Math.Max(20, cols);
            Action<string> L = s => sb.Append(s.Length > cols ? s.Substring(0, cols) : s).Append('\n');
            L("AICS ILS " + label); L(couple);
            L("DME " + (r.DmeM / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km  CRS " + Math.Round(r.Course).ToString("000"));
            L("LOC " + Bar(-Ils.Needle(r.LocDeg, Ils.LocFullScale), cols - 4, '#'));
            L("G/S " + Bar(Ils.Needle(r.GsDeg, Ils.GsFullScale), cols - 4, '#'));
            L("X-TRK " + Math.Abs(r.CrossM).ToString("0", CultureInfo.InvariantCulture) + " m " + (r.CrossM > 0 ? "R" : "L") + "  GS " + Math.Abs(r.AboveGsM).ToString("0", CultureInfo.InvariantCulture) + " m " + (r.AboveGsM > 0 ? "HI" : "LO"));
            L("IAS " + Math.Round(ias) + "/" + Math.Round(target) + " " + Ils.SpeedBand(ias, target).ToUpperInvariant() + "  VS " + vs.ToString("+0;-0", CultureInfo.InvariantCulture));
            L("GEAR " + (gear ? "DN" : "UP") + "  " + (brakes ? "BRK" : "   ") + "  HAT " + Math.Round(hat) + " m");
            var co = Ils.Callouts(r, hat, gear, ias, target); if (co.Count > 0) L(string.Join(" ", co.ToArray()));
            if (!r.Front) L("BEHIND THE RUNWAY");
            return sb.ToString();
        }
        /// <summary>Plane-up north-up ASCII map: '^' plane, '=' runway, '*' route fixes, 'J' join point. span = metres across.</summary>
        internal static string MapPage(double lat, double lon, double radius, double span, IList<double[]> runway, IList<double[]> route, double[] join, int cols, int rows)
        {
            cols = Math.Max(20, cols); rows = Math.Max(8, rows); int h = rows - 1; var g = new char[h, cols];
            for (int y = 0; y < h; y++) for (int x = 0; x < cols; x++) g[y, x] = ' ';
            double mx = span / cols, my = span / h * .5, k = Math.PI / 180 * radius, cl = Math.Cos(lat * Math.PI / 180);   // chars ~2:1 tall
            Action<double, double, char> put = (la, lo, ch) => { int x = (int)Math.Round(cols / 2.0 + (lo - lon) * k * cl / mx), y = (int)Math.Round(h / 2.0 - (la - lat) * k / (my * 2)); if (x >= 0 && y >= 0 && x < cols && y < h) g[y, x] = ch; };
            if (runway != null && runway.Count == 2) for (int i = 0; i <= 20; i++) put(runway[0][0] + (runway[1][0] - runway[0][0]) * i / 20, runway[0][1] + (runway[1][1] - runway[0][1]) * i / 20, '=');
            if (route != null) foreach (var p in route) put(p[0], p[1], '*');
            if (join != null) put(join[0], join[1], 'J');
            put(lat, lon, '^');
            var sb = new StringBuilder("AICS MAP " + (span / 1000).ToString("0", CultureInfo.InvariantCulture) + " km\n");
            for (int y = 0; y < h; y++) { for (int x = 0; x < cols; x++) sb.Append(g[y, x]); sb.Append('\n'); }
            return sb.ToString();
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
