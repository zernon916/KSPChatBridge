// GROK CHART1: airport diagrams (FAA airport-diagram style, our own rendering): north-up, runway as a black bar with end numbers
// (09/27), length x width, threshold / field elevation, buildings as blocks, lat/lon ticks, the plane when near. Sources: built-in
// KSC + Island Airfield runway ends, stock KSC facility positions read at runtime (SpaceCenter transforms via KscRoverMap), and
// OPTIONAL Kerbal Konstructs launch sites/runways via reflection only (no hard dependency). Geometry is pure and tested offline.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace KSPChatBridge
{
    internal sealed class AirportDiagram
    {
        internal sealed class Rwy { internal double Lat1, Lon1, Lat2, Lon2, WidthM = 60, ElevM; }
        internal sealed class Block { internal string Name; internal double Lat, Lon, SizeM = 60; }
        internal string Name = "", Source = ""; internal double RefLat, RefLon, ElevM;
        internal readonly List<Rwy> Runways = new List<Rwy>(); internal readonly List<Block> Blocks = new List<Block>();
        /// <summary>CHART shows the diagram in place once zoomed in to this map span (m) near a field.</summary>
        internal const double ShowBelowSpan = 9000;

        static double Norm(double a) { a %= 360; return a < 0 ? a + 360 : a; }
        /// <summary>Runway end number from its landing heading: 090 -> "09", 270 -> "27", 003 -> "36".</summary>
        internal static string EndNumber(double heading) { int n = (int)Math.Round(Norm(heading) / 10, MidpointRounding.AwayFromZero); if (n == 0) n = 36; if (n > 36) n -= 36; return n.ToString("00"); }
        /// <summary>"09/27" for a runway whose end 1 -> end 2 heading is h (lower number first).</summary>
        internal static string Pair(double h) { string a = EndNumber(h), b = EndNumber(h + 180); return string.CompareOrdinal(a, b) <= 0 ? a + "/" + b : b + "/" + a; }
        /// <summary>Local metres east/north of the reference (flat, fine over an airfield).</summary>
        internal static double[] ToLocal(double lat, double lon, double refLat, double refLon, double radius)
        {
            double k = Math.PI / 180 * radius; return new[] { (lon - refLon) * k * Math.Cos(refLat * Math.PI / 180), (lat - refLat) * k };
        }
        /// <summary>North-up pixel position in a size x size square centred on (ce, cn) metres at mpp metres per pixel.</summary>
        internal static double[] ToPx(double e, double n, double ce, double cn, double mpp, double size) { return new[] { size / 2 + (e - ce) / mpp, size / 2 - (n - cn) / mpp }; }
        internal static double Length(Rwy r, double radius) { return NavigationMath.Distance(r.Lat1, r.Lon1, r.Lat2, r.Lon2, radius); }
        internal static string RunwayText(Rwy r, double radius)
        {
            double h = NavigationMath.Bearing(r.Lat1, r.Lon1, r.Lat2, r.Lon2);
            return "RWY " + Pair(h) + "  " + Math.Round(Length(r, radius)) + " x " + Math.Round(r.WidthM) + " m  THR ELEV " + Math.Round(r.ElevM) + " m";
        }
        /// <summary>Lat/lon tick step (deg) giving 3-8 ticks across the span.</summary>
        internal static double TickStep(double spanM, double radius)
        {
            double deg = spanM / (Math.PI / 180 * radius);
            foreach (double s in new[] { .001, .002, .005, .01, .02, .05, .1, .2, .5 }) if (deg / s <= 8) return s;
            return 1;
        }
        /// <summary>The diagram whose reference is nearest (lat, lon) within maxM; null if none.</summary>
        internal static AirportDiagram Nearest(IList<AirportDiagram> all, double lat, double lon, double radius, double maxM)
        {
            AirportDiagram best = null; double bd = maxM;
            if (all != null) foreach (var d in all) { double m = NavigationMath.Distance(lat, lon, d.RefLat, d.RefLon, radius); if (m <= bd) { bd = m; best = d; } }
            return best;
        }
        /// <summary>Built-in Kerbin fields: KSC (runway + the given facility blocks) and Island Airfield.</summary>
        internal static List<AirportDiagram> BuiltIn(IEnumerable<KeyValuePair<string, double[]>> kscBuildings)
        {
            var ksc = new AirportDiagram { Name = "KSC", Source = "stock runway ends + SpaceCenter facility transforms", ElevM = 69.1,
                RefLat = (KscRunway.Lat09 + KscRunway.Lat27) / 2, RefLon = (KscRunway.Lon09 + KscRunway.Lon27) / 2 };
            ksc.Runways.Add(new Rwy { Lat1 = KscRunway.Lat09, Lon1 = KscRunway.Lon09, Lat2 = KscRunway.Lat27, Lon2 = KscRunway.Lon27, WidthM = 70, ElevM = 69.1 });
            if (kscBuildings != null) foreach (var b in kscBuildings)
                {
                    if (b.Value == null || b.Value.Length < 2 || b.Key == "Runway" || b.Key == "KSC Grass" || b.Key == "Shores") continue;
                    ksc.Blocks.Add(new Block { Name = b.Key, Lat = b.Value[0], Lon = b.Value[1], SizeM = b.Key.StartsWith("R&D - ") ? 30 : b.Key == "Crawlerway" ? 25 : 70 });
                }
            var isl = new AirportDiagram { Name = "Island Airfield", Source = "stock runway ends", ElevM = 134.6, RefLat = (-1.516092 - 1.514809) / 2, RefLon = (-71.856744 - 71.961815) / 2 };
            isl.Runways.Add(new Rwy { Lat1 = -1.516092, Lon1 = -71.856744, Lat2 = -1.514809, Lon2 = -71.961815, WidthM = 40, ElevM = 134.6 });
            return new List<AirportDiagram> { ksc, isl };
        }

        static object Get(object o, string name)
        {
            if (o == null) return null; var t = o as Type ?? o.GetType(); object inst = o is Type ? null : o;
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase;
            try
            {
                var fi = t.GetField(name, f); if (fi != null) return fi.GetValue(inst);
                var pi = t.GetProperty(name, f); if (pi != null && pi.GetIndexParameters().Length == 0) return pi.GetValue(inst, null);
                var mi = t.GetMethod(name, f, null, Type.EmptyTypes, null); if (mi != null) return mi.Invoke(inst, null);
            }
            catch (Exception) { }
            return null;
        }
        static double D(object o, params string[] names) { foreach (var n in names) { var v = Get(o, n); if (v != null) try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { } } return double.NaN; }
        /// <summary>OPTIONAL Kerbal Konstructs runways (reflection only; empty when KK isn't loaded or its API differs). Each runway is built
        /// from its reference point, heading and length (heading/length missing = skipped).</summary>
        internal static List<AirportDiagram> FromKerbalKonstructs(double radius, out string note)
        {
            var res = new List<AirportDiagram>(); note = "";
            try
            {
                Assembly kk = null; foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "KerbalKonstructs") { kk = a; break; }
                if (kk == null) { note = "Kerbal Konstructs not loaded"; return res; }
                var mgr = kk.GetType("KerbalKonstructs.Core.LaunchSiteManager"); if (mgr == null) { note = "KK: no LaunchSiteManager"; return res; }
                var sites = (Get(mgr, "allLaunchSites") ?? Get(mgr, "AllLaunchSites") ?? Get(mgr, "getLaunchSites") ?? Get(mgr, "GetLaunchSites")) as System.Collections.IEnumerable;
                if (sites == null) { note = "KK: launch site list not found"; return res; }
                foreach (var s in sites)
                {
                    string cat = Convert.ToString(Get(s, "sitecategory") ?? Get(s, "Category") ?? ""), name = Convert.ToString(Get(s, "LaunchSiteName") ?? Get(s, "Name") ?? "KK site");
                    if (cat.IndexOf("runway", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    double lat = D(s, "refLat", "RefLatitude"), lon = D(s, "refLon", "RefLongitude"), alt = D(s, "refAlt", "RefAltitude"), len = D(s, "LaunchSiteLength"), wid = D(s, "LaunchSiteWidth");
                    double hdg = D(s, "heading", "Heading", "LaunchSiteHeading"); if (double.IsNaN(hdg)) hdg = D(Get(s, "staticInstance"), "heading", "RotationAngle");
                    if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsNaN(hdg) || double.IsNaN(len) || len < 100) continue;
                    double la1, lo1, la2, lo2; NavigationMath.Offset(lat, lon, hdg + 180, len / 2, radius, out la1, out lo1); NavigationMath.Offset(lat, lon, hdg, len / 2, radius, out la2, out lo2);
                    var d = new AirportDiagram { Name = name, Source = "Kerbal Konstructs", RefLat = lat, RefLon = lon, ElevM = double.IsNaN(alt) ? 0 : alt };
                    d.Runways.Add(new Rwy { Lat1 = la1, Lon1 = lo1, Lat2 = la2, Lon2 = lo2, WidthM = double.IsNaN(wid) || wid < 5 ? 40 : wid, ElevM = d.ElevM }); res.Add(d);
                }
                note = "KK: " + res.Count + " runway(s)";
            }
            catch (Exception e) { note = "KK read failed: " + e.GetType().Name; }
            return res;
        }
    }
}
