using System;
using System.Collections.Generic;
using System.IO;

namespace KSPChatBridge
{
    // P5-6: versioned PluginData migration. Backs up user files before any version step and never deletes,
    // overwrites or wipes user data; steps only ADD missing values. No Unity types (offline-tested).
    internal static class PluginDataMigration
    {
        internal const int Current = 2;
        internal const string VersionFile = "data_version.txt";
        static readonly string[] UserExt = { ".json", ".txt", ".cfg", ".md", ".env" };

        internal static int Version(string pluginData)
        {
            string p = Path.Combine(pluginData, VersionFile); int v;
            return File.Exists(p) && int.TryParse(File.ReadAllText(p).Trim(), out v) ? v : 0;
        }

        /// <summary>User files in PluginData root (settings, keys, spots/notes in native_settings.json, memory, layouts).</summary>
        internal static List<string> UserFiles(string pluginData)
        {
            var files = new List<string>();
            if (!Directory.Exists(pluginData)) return files;
            foreach (string f in Directory.GetFiles(pluginData))
            {
                string name = Path.GetFileName(f), ext = Path.GetExtension(f).ToLowerInvariant();
                if (name == VersionFile || name.EndsWith(".partial") || name.EndsWith(".tmp")) continue;
                if (name == ".env" || Array.IndexOf(UserExt, ext) >= 0) files.Add(f);
            }
            return files;
        }

        /// <summary>Run pending steps. Returns a short report, or null when already current.</summary>
        internal static string Run(string pluginData, DateTime utcNow)
        {
            if (!Directory.Exists(pluginData)) { Directory.CreateDirectory(pluginData); File.WriteAllText(Path.Combine(pluginData, VersionFile), Current.ToString()); return null; }
            int from = Version(pluginData);
            if (from >= Current) return null;
            string backup = Path.Combine(Path.Combine(pluginData, "backups"), "v" + from + "-" + utcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backup);
            int copied = 0;
            foreach (string f in UserFiles(pluginData)) { File.Copy(f, Path.Combine(backup, Path.GetFileName(f)), false); copied++; }
            var notes = new List<string>();
            if (from < 2) notes.Add(StepScienceMode(pluginData));
            File.WriteAllText(Path.Combine(pluginData, VersionFile), Current.ToString());
            return "PluginData migrated v" + from + " -> v" + Current + " (" + copied + " file(s) backed up to " + Path.GetFileName(backup) + ")" + (notes.Count > 0 ? "; " + string.Join("; ", notes.ToArray()) : "") + ".";
        }

        /// <summary>v2: carry the bridge watcher's science_mode into native settings when native has none.</summary>
        static string StepScienceMode(string pluginData)
        {
            string bridge = Path.Combine(pluginData, "bridge_settings.json"), native = Path.Combine(pluginData, "native_settings.json");
            if (!File.Exists(bridge)) return "no bridge settings";
            var b = MiniJson.Deserialize(File.ReadAllText(bridge));
            var n = File.Exists(native) ? MiniJson.Deserialize(File.ReadAllText(native)) : new Dictionary<string, object>();
            object mode;
            if (n.ContainsKey("science_mode") || !b.TryGetValue("science_mode", out mode) || SciencePolicy.NormalizeMode(mode as string) == null) return "science mode kept";
            n["science_mode"] = SciencePolicy.NormalizeMode((string)mode);
            string tmp = native + ".tmp"; File.WriteAllText(tmp, MiniJson.Serialize(n));
            if (File.Exists(native)) File.Replace(tmp, native, null); else File.Move(tmp, native);
            return "science mode imported";
        }
        /// <summary>ai_enabled from aics.cfg (or the old bridge.cfg on first run); fallback when absent.</summary>
        internal static bool ParseAiEnabled(string text, bool fallback)
        {
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim(); int eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq <= 0 || line.Substring(0, eq).Trim() != "ai_enabled") continue;
                return line.Substring(eq + 1).Trim().ToLowerInvariant() != "false";
            }
            return fallback;
        }
    }
}
