using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace KSPChatBridge
{
    // Optional SCANsat link by reflection (soft dependency, like MechJebLink). Without SCANsat every call reports "not installed".
    internal static class ScanSatLink
    {
        internal const string Missing = "needs SCANsat (not installed)";
        internal static readonly string[] Types = { "AltimetryLoRes", "AltimetryHiRes", "Biome", "Anomaly", "VisualLoRes", "VisualHiRes", "ResourceLoRes", "ResourceHiRes" };
        static Type util, module; static bool searched;

        static void Find()
        {
            if (searched) return; searched = true;
            foreach (var la in AssemblyLoader.loadedAssemblies)
            {
                try
                {
                    if (util == null) util = la.assembly.GetType("SCANsat.SCANUtil");
                    if (module == null) module = la.assembly.GetType("SCANsat.SCAN_PartModules.SCANsat");
                }
                catch (Exception) { }
            }
        }

        internal static bool Installed { get { Find(); return util != null; } }

        static int TypeMask(string name)
        {
            var mi = util.GetMethod("GetSCANtype", new[] { typeof(string) });
            if (mi == null) return 0;
            try { return (int)mi.Invoke(null, new object[] { name }); } catch (Exception) { return 0; }
        }

        /// <summary>Coverage 0-100 per scan type for a body (types SCANsat doesn't know are skipped). Null = SCANsat missing.</summary>
        internal static Dictionary<string, double> Coverage(CelestialBody body)
        {
            if (!Installed || body == null) return null;
            var mi = util.GetMethod("GetCoverage", new[] { typeof(int), typeof(CelestialBody) });
            if (mi == null) return null;
            var outp = new Dictionary<string, double>();
            foreach (var t in Types)
            {
                int mask = TypeMask(t); if (mask == 0) continue;
                try { outp[t] = Math.Max(0, Math.Min(100, (double)mi.Invoke(null, new object[] { mask, body }))); } catch (Exception) { }
            }
            return outp;
        }

        internal sealed class Scanner { internal string Part, Name, Types; internal int Mask; internal float Fov, Min, Best, Max; internal bool Scanning; }

        internal static List<Scanner> Scanners(Vessel v)
        {
            var outp = new List<Scanner>();
            Find(); if (module == null || v == null) return outp;
            foreach (Part p in v.parts) foreach (PartModule m in p.Modules)
            {
                if (!module.IsInstanceOfType(m)) continue;
                try
                {
                    var s = new Scanner { Part = p.partInfo != null ? p.partInfo.title : p.name };
                    s.Mask = Convert.ToInt32(F(m, "sensorType")); s.Fov = Convert.ToSingle(F(m, "fov"));
                    s.Min = Convert.ToSingle(F(m, "min_alt")); s.Best = Convert.ToSingle(F(m, "best_alt")); s.Max = Convert.ToSingle(F(m, "max_alt"));
                    s.Name = (F(m, "scanName") as string) ?? ""; s.Types = (F(m, "scanType") as string) ?? "";
                    var pi = module.GetProperty("scanningNow"); s.Scanning = pi != null && (bool)pi.GetValue(m, null);
                    if (s.Mask != 0) outp.Add(s);
                }
                catch (Exception) { }
            }
            return outp;
        }

        static object F(object o, string n)
        {
            var f = o.GetType().GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f == null ? null : f.GetValue(o);
        }
    }

    /// <summary>Pure math for mapping orbits (testable without KSP).</summary>
    internal static class ScanSatPolicy
    {
        /// <summary>Best mapping altitude (m) shared by all scanners: max of their mins, min of their maxes, aiming at the average best; above atmosphere + 10 km margin.</summary>
        internal static double MappingAltitude(IList<float[]> minBestMax, double atmosphereTop, out bool overlap)
        {
            double lo = 0, hi = double.MaxValue, best = 0;
            foreach (var s in minBestMax) { lo = Math.Max(lo, s[0]); hi = Math.Min(hi, s[2]); best += s[1]; }
            best = minBestMax.Count > 0 ? best / minBestMax.Count : 250000;
            lo = Math.Max(lo, atmosphereTop + 10000);
            overlap = lo <= hi;
            if (!overlap) return Math.Max(best, lo);
            return Math.Max(lo, Math.Min(hi, best));
        }
        internal const double Inclination = 88;   // near-polar but not exactly 90, so ground tracks drift and fill gaps
        internal static int Milestone(double pct) { return pct >= 99.5 ? 100 : pct >= 75 ? 75 : pct >= 50 ? 50 : pct >= 25 ? 25 : 0; }
    }
}
