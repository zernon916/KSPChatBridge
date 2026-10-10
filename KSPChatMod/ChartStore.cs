using System;
using System.Collections.Generic;
using System.IO;

namespace KSPChatBridge
{
    /// <summary>Luke: approach charts as separate, hot-swappable files in PluginData/charts/ (KSC_09.json, KSC_09_tight.json, ...).
    /// active.json picks a variant per runway end ({"KSC 09":"tight"}); missing/empty = Luke's default file. Pure file IO (tested).</summary>
    internal static class ChartStore
    {
        internal static string Dir;   // set by the mod to PluginData/charts
        static readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();
        internal static string FileName(string key, string variant) { return key.Trim().Replace(' ', '_') + (string.IsNullOrEmpty(variant) ? "" : "_" + variant) + ".json"; }
        internal static string KeyOf(string file, out string variant)
        {
            string n = Path.GetFileNameWithoutExtension(file); variant = "";
            var parts = n.Split('_');
            if (parts.Length >= 3) { variant = string.Join("_", parts, 2, parts.Length - 2); n = parts[0] + "_" + parts[1]; }
            return n.Replace('_', ' ');
        }
        static string ActivePath { get { return Path.Combine(Dir, "active.json"); } }
        internal static Dictionary<string, object> ActiveMap()
        {
            try { if (File.Exists(ActivePath)) return MiniJson.Deserialize(File.ReadAllText(ActivePath)) ?? new Dictionary<string, object>(); } catch (Exception) { }
            return new Dictionary<string, object>();
        }
        internal static string Active(string key) { object v; return ActiveMap().TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }
        internal static void SetActive(string key, string variant)
        {
            Directory.CreateDirectory(Dir); var m = ActiveMap(); m[key] = variant ?? ""; AtomicFile.Write(ActivePath, MiniJson.Serialize(m));
        }
        /// <summary>Variants present for a runway end ("" = default), from the files on disk.</summary>
        internal static List<string> Variants(string key)
        {
            var list = new List<string>();
            if (Dir == null || !Directory.Exists(Dir)) return list;
            foreach (var f in Directory.GetFiles(Dir, "*.json")) { string v; if (Path.GetFileName(f) != "active.json" && KeyOf(f, out v) == key && !list.Contains(v)) list.Add(v); }
            list.Sort(); return list;
        }
        /// <summary>Path of the chart used for this end: the active variant if its file exists, else the default.</summary>
        internal static string PathFor(string key)
        {
            string v = Active(key), p = Path.Combine(Dir, FileName(key, v));
            return v.Length > 0 && !File.Exists(p) ? Path.Combine(Dir, FileName(key, "")) : p;
        }
        /// <summary>Raw chart object for the end (null = none/invalid; why says which).</summary>
        internal static object Read(string key, out string why)
        {
            why = ""; if (Dir == null) return null; string p = PathFor(key);
            if (!File.Exists(p)) return null;
            string txt = File.ReadAllText(p); if (AtomicFile.Torn(txt)) { why = Path.GetFileName(p) + " empty"; return null; }
            var d = MiniJson.Deserialize(txt); if (d == null) { why = Path.GetFileName(p) + " is not valid JSON"; return null; }
            return d;
        }
        internal static void Write(string key, string variant, string json) { Directory.CreateDirectory(Dir); AtomicFile.Write(Path.Combine(Dir, FileName(key, variant)), json); }
        /// <summary>One-time split of the old approaches.json into per-end files (backup kept as approaches.json.premigrate.bak). Returns files written.</summary>
        internal static int Migrate(string approachesPath)
        {
            if (Dir == null || !File.Exists(approachesPath)) return 0;
            Directory.CreateDirectory(Dir);
            var all = MiniJson.Deserialize(File.ReadAllText(approachesPath)); if (all == null) return 0;
            int n = 0;
            foreach (var kv in all)
            {
                string p = Path.Combine(Dir, FileName(kv.Key, ""));
                if (File.Exists(p)) continue;
                AtomicFile.Write(p, MiniJson.Serialize(kv.Value)); n++;
            }
            string bak = approachesPath + ".premigrate.bak"; if (!File.Exists(bak)) File.Copy(approachesPath, bak);
            return n;
        }
        /// <summary>Cheap mtime scan: runway keys whose chart files (or active.json) changed since the last call. First call primes.</summary>
        internal static List<string> Changed()
        {
            var keys = new List<string>(); if (Dir == null || !Directory.Exists(Dir)) return keys;
            bool prime = seen.Count == 0; var now = new HashSet<string>();
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                now.Add(f); DateTime t = File.GetLastWriteTimeUtc(f); DateTime old;
                if (seen.TryGetValue(f, out old) && old == t) continue;
                seen[f] = t; if (prime) continue;
                if (Path.GetFileName(f) == "active.json") { foreach (var k in ActiveMap().Keys) if (!keys.Contains(k)) keys.Add(k); continue; }
                string v; string key = KeyOf(f, out v); if (!keys.Contains(key)) keys.Add(key);
            }
            if (prime) seen["<primed>"] = DateTime.MinValue;
            return keys;
        }
        internal static void ResetWatch() { seen.Clear(); }
    }
}
