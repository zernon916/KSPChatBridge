using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    /// <summary>GROK TEST1: one landing-test start point. Position is given ONE of three ways: along/cross from the KSC 27 threshold
    /// (along = metres out on the extended final, east; cross = metres north), bearing/dist from the KSC runway midpoint, or lat/lon.</summary>
    internal sealed class TestPreset
    {
        internal string Name = "", Runway = "27";
        internal double Along = double.NaN, Cross = 0, Bearing = double.NaN, Dist = 0, Lat = double.NaN, Lon = double.NaN;
        internal double AltM = 1000, Speed = 150, Heading = 270;
    }

    /// <summary>GROK TEST1: pure preset geometry + data (no KSP types; unit tested). Presets are data-driven: built-ins below,
    /// overridden by PluginData/landing_tests.json (a JSON list of objects with the same field names) when present.</summary>
    internal static class LandingTestPresets
    {
        internal const double ThrLat = -.0504407, ThrLon = -74.490300, EndLat = -.0486692, EndLon = -74.724375;   // KSC 27 threshold / 09 end (KscRunway)
        internal const double D14Lat = -.049541, D14Lon = -73.270435;   // chart D14.0KSC (DME at the KSC NDB, ~1.2 km behind the threshold)
        internal const double FieldM = 70, GlideDeg = 3.06, Course27 = 270, KerbinR = 600000;
        internal static double GlideAlt(double alongM) { return FieldM + Math.Max(0, alongM) * Math.Tan(GlideDeg * Math.PI / 180); }

        internal static List<TestPreset> Defaults()
        {
            return new List<TestPreset>
            {
                new TestPreset { Name = "a straight-in D14 on glidepath", Lat = D14Lat, Lon = D14Lon, AltM = 818, Speed = 125, Heading = 270 },
                new TestPreset { Name = "b KERBO leg start (IAF, outbound 101)", Lat = -.39021, Lon = -72.854397, AltM = 1070, Speed = 150, Heading = 101 },
                P("c 30 km out, 90 deg off course", 30000, 0, 1500, 160, 0),
                new TestPreset { Name = "d D14, 600 m high, +40 m/s", Lat = D14Lat, Lon = D14Lon, AltM = 818 + 600, Speed = 165, Heading = 270 },
                P("e 5 km short final, 300 m offset", 5000, 300, GlideAlt(5000), 105, 270),
                P("f 15 km east heading 090 (away)", 15000, 0, 1000, 150, 90),
                new TestPreset { Name = "g 20 km NW heading 330 (away)", Bearing = 315, Dist = 20000, AltM = 1500, Speed = 160, Heading = 330 },
                new TestPreset { Name = "h 8 km south heading 200, low", Bearing = 180, Dist = 8000, AltM = 800, Speed = 140, Heading = 200 },
                new TestPreset { Name = "i overhead KSC 2 km heading 045", Bearing = 0, Dist = 0, AltM = 2000, Speed = 150, Heading = 45 },
            };
        }
        static TestPreset P(string n, double along, double cross, double alt, double spd, double hdg) { return new TestPreset { Name = n, Along = along, Cross = cross, AltM = alt, Speed = spd, Heading = hdg }; }

        /// <summary>World lat/lon of a preset (Kerbin radius by default).</summary>
        internal static void Position(TestPreset p, out double lat, out double lon, double radius = KerbinR)
        {
            if (!double.IsNaN(p.Lat) && !double.IsNaN(p.Lon)) { lat = p.Lat; lon = p.Lon; return; }
            if (!double.IsNaN(p.Bearing))
            {
                double mLat = (ThrLat + EndLat) / 2, mLon = (ThrLon + EndLon) / 2;
                if (p.Dist <= 0) { lat = mLat; lon = mLon; return; }
                NavigationMath.Offset(mLat, mLon, p.Bearing, p.Dist, radius, out lat, out lon); return;
            }
            double outbound = (Course27 + 180) % 360, aLat, aLon;
            NavigationMath.Offset(ThrLat, ThrLon, outbound, double.IsNaN(p.Along) ? 0 : p.Along, radius, out aLat, out aLon);
            if (Math.Abs(p.Cross) < 1e-6) { lat = aLat; lon = aLon; return; }
            NavigationMath.Offset(aLat, aLon, p.Cross >= 0 ? 0 : 180, Math.Abs(p.Cross), radius, out lat, out lon);
        }

        /// <summary>Surface-relative velocity in local east/north/up (m/s) for level flight on the preset heading.</summary>
        internal static void VelocityEnu(TestPreset p, out double e, out double n, out double u)
        { double h = p.Heading * Math.PI / 180; e = p.Speed * Math.Sin(h); n = p.Speed * Math.Cos(h); u = 0; }

        static double D(Dictionary<string, object> d, string k, double def)
        { object o; if (!d.TryGetValue(k, out o) || o == null) return def; try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return def; } }

        /// <summary>Parse a JSON list of presets; bad entries are skipped (why lists them). Null/empty = use Defaults().</summary>
        internal static List<TestPreset> Parse(string json, out string why)
        {
            why = ""; var list = new List<TestPreset>();
            object root; try { root = MiniJson.DeserializeObject(json); } catch (Exception ex) { why = ex.Message; return null; }
            var arr = root as System.Collections.IList; if (arr == null) { var dd = root as Dictionary<string, object>; object pr; if (dd != null && dd.TryGetValue("presets", out pr)) arr = pr as System.Collections.IList; }
            if (arr == null) { why = "expected a list of presets"; return null; }
            foreach (var x in arr)
            {
                var d = x as Dictionary<string, object>; if (d == null) { why += "non-object skipped; "; continue; }
                object nm; var p = new TestPreset { Name = d.TryGetValue("name", out nm) && nm != null ? nm.ToString() : "preset " + (list.Count + 1) };
                object rw; if (d.TryGetValue("runway", out rw) && rw != null) p.Runway = rw.ToString();
                p.Along = D(d, "along", double.NaN); p.Cross = D(d, "cross", 0); p.Bearing = D(d, "bearing", double.NaN); p.Dist = D(d, "dist", 0);
                p.Lat = D(d, "lat", double.NaN); p.Lon = D(d, "lon", double.NaN); p.AltM = D(d, "alt", 1000); p.Speed = D(d, "speed", 150); p.Heading = D(d, "heading", 270);
                if (double.IsNaN(p.Along) && double.IsNaN(p.Bearing) && (double.IsNaN(p.Lat) || double.IsNaN(p.Lon))) { why += p.Name + ": no position; "; continue; }
                if (p.AltM < 100 || p.AltM > 20000 || p.Speed < 30 || p.Speed > 600) { why += p.Name + ": alt/speed out of range; "; continue; }
                list.Add(p);
            }
            return list.Count > 0 ? list : null;
        }

        internal static string ToJson(List<TestPreset> ps)
        {
            var arr = new List<object>();
            foreach (var p in ps)
            {
                var d = new Dictionary<string, object> { { "name", p.Name }, { "runway", p.Runway } };
                if (!double.IsNaN(p.Lat)) { d["lat"] = p.Lat; d["lon"] = p.Lon; } else if (!double.IsNaN(p.Bearing)) { d["bearing"] = p.Bearing; d["dist"] = p.Dist; } else { d["along"] = p.Along; d["cross"] = p.Cross; }
                d["alt"] = Math.Round(p.AltM); d["speed"] = p.Speed; d["heading"] = p.Heading; arr.Add(d);
            }
            return MiniJson.Serialize(arr);
        }

        /// <summary>Log lines (one marker per run, one outcome).</summary>
        internal static string RunLine(int run, TestPreset p, string craft) { return "run " + run + " preset=" + p.Name + " craft=" + craft; }
        internal static string RunLine(int run, TestPreset p, string craft, double fuelPct) { return RunLine(run, p, craft) + (double.IsNaN(fuelPct) ? "" : " fuel=" + fuelPct.ToString("0.#", CultureInfo.InvariantCulture) + "%"); }
        /// <summary>TEST4: start speed / altitude overrides (NaN = preset). Speed 30-600 m/s, altitude 100-20000 m; blank / bad = NaN.</summary>
        internal static double ParseOpt(string text, double min, double max)
        { double v; if (!double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsNaN(v) || double.IsInfinity(v)) return double.NaN; return Math.Max(min, Math.Min(max, v)); }
        internal static double Eff(double overrideV, double preset) { return double.IsNaN(overrideV) ? preset : overrideV; }
        internal static double ReadNum(string json, string key)
        { try { var d = MiniJson.DeserializeObject(json) as Dictionary<string, object>; return d != null ? D(d, key, double.NaN) : double.NaN; } catch { return double.NaN; } }
        internal static string WriteNum(string json, string key, double v)
        {
            object root = null; try { root = MiniJson.DeserializeObject(json); } catch { }
            var d = root as Dictionary<string, object>;
            if (d == null) d = new Dictionary<string, object> { { "presets", root as System.Collections.IList ?? (object)new List<object>() } };
            if (double.IsNaN(v)) d.Remove(key); else d[key] = v; return MiniJson.Serialize(d);
        }
        internal static string RunLine(int run, TestPreset p, string craft, double fuelPct, double speed, double alt)
        { return RunLine(run, p, craft, fuelPct) + " speed=" + Math.Round(Eff(speed, p.Speed)).ToString(CultureInfo.InvariantCulture) + (double.IsNaN(speed) ? "" : "*") + " alt=" + Math.Round(Eff(alt, p.AltM)).ToString(CultureInfo.InvariantCulture) + (double.IsNaN(alt) ? "" : "*"); }
        /// <summary>ROLL1: AP off while landed and (nearly) stopped = the landing finished, not a pilot intervention.</summary>
        internal static bool StoppedOff(bool landed, double speed) { return landed && speed < 3; }
        /// <summary>FIX: vessel types cleared from the test save before a launch (everything but asteroids / comets).</summary>
        internal static bool LeftoverType(string type) { return type != "SpaceObject" && type != "Unknown"; }
        /// <summary>TEST3: fuel % 0-100 (bad / blank = 100).</summary>
        internal static double ClampFuel(double pct) { return double.IsNaN(pct) || double.IsInfinity(pct) ? 100 : Math.Max(0, Math.Min(100, pct)); }
        internal static double ParseFuel(string text, double fallback)
        { double v; return double.TryParse((text ?? "").Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? ClampFuel(v) : ClampFuel(fallback); }
        /// <summary>TEST3: propellants the fuel % applies to (fuel / oxidizer / monoprop / xenon / solid); never ElectricCharge, Ablator, Ore, IntakeAir.</summary>
        internal static bool FuelResource(string name)
        {
            if (string.IsNullOrEmpty(name)) return false; string n = name.ToLowerInvariant();
            if (n == "electriccharge" || n.Contains("ablator") || n == "ore" || n == "intakeair" || n.Contains("intake")) return false;
            return n.Contains("fuel") || n.Contains("oxidizer") || n.Contains("monoprop") || n.Contains("xenon") || n.StartsWith("lqd") || n.Contains("hydrazine");
        }
        /// <summary>TEST3: fuel_pct remembered in landing_tests.json (root {"fuel_pct": n, "presets": [...]}; a bare list = 100).</summary>
        internal static double ReadFuel(string json)
        {
            try { var d = MiniJson.DeserializeObject(json) as Dictionary<string, object>; return d != null ? ClampFuel(D(d, "fuel_pct", 100)) : 100; } catch { return 100; }
        }
        internal static string WriteFuel(string json, double pct)
        {
            object root = null; try { root = MiniJson.DeserializeObject(json); } catch { }
            var d = root as Dictionary<string, object>;
            if (d == null) d = new Dictionary<string, object> { { "presets", root as System.Collections.IList ?? (object)new List<object>() } };
            d["fuel_pct"] = ClampFuel(pct); return MiniJson.Serialize(d);
        }
        internal static string Outcome(int run, string what, double sink, double speed, int goArounds)
        { return "run " + run + " outcome=" + what + (double.IsNaN(sink) ? "" : " sink=" + sink.ToString("0.0", CultureInfo.InvariantCulture) + " m/s") + (double.IsNaN(speed) ? "" : " speed=" + speed.ToString("0", CultureInfo.InvariantCulture) + " m/s") + " go-arounds=" + goArounds; }
    }
    /// <summary>AICS2: AICS role changes made IN FLIGHT, remembered per craft + part craftID (test-zone RETRY / respawn reloads the craft file
    /// and dropped them). Entry = [role in the craft file when changed, chosen role]; ignored once the craft file's role changes (SPH edit wins).</summary>
    internal static class SurfaceRoleStore
    {
        internal static string Get(Dictionary<string, object> root, string craft, uint id, string fileRole)
        {
            object c; if (root == null || string.IsNullOrEmpty(craft) || !root.TryGetValue(craft, out c)) return null;
            var d = c as Dictionary<string, object>; object e; if (d == null || !d.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out e)) return null;
            var l = e as System.Collections.IList; if (l == null || l.Count < 2) return null;
            return Convert.ToString(l[0], CultureInfo.InvariantCulture) == (fileRole ?? "") ? Convert.ToString(l[1], CultureInfo.InvariantCulture) : null;
        }
        internal static void Set(Dictionary<string, object> root, string craft, uint id, string fileRole, string role)
        {
            if (root == null || string.IsNullOrEmpty(craft)) return; object c; var d = root.TryGetValue(craft, out c) ? c as Dictionary<string, object> : null;
            if (d == null) root[craft] = d = new Dictionary<string, object>();
            string k = id.ToString(CultureInfo.InvariantCulture);
            if (role == fileRole) d.Remove(k); else d[k] = new List<object> { fileRole ?? "", role ?? "auto" };
        }
        internal static Dictionary<string, object> Parse(string json) { try { return MiniJson.DeserializeObject(json) as Dictionary<string, object> ?? new Dictionary<string, object>(); } catch { return new Dictionary<string, object>(); } }
    }
}
