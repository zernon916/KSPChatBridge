using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal static class NativeSettings
    {
        // Copy only flight settings. Cloud keys/chat data are outside this migration.
        /// <summary>GROK LAND5k: per-craft tables keyed by vessel name. Entries under an unresolved '#autoLOC_...' name (test-zone runs before the
        /// name fix, incl. the bogus 31 m/s stall fit) are dropped; craft_overrides is seeded once with Luke's Aeris 3A numbers.</summary>
        internal static readonly string[] CraftTables = { "stall_learn", "stall_speeds", "liftoff_speeds", "throttle_lag", "plane_profiles" };
        internal static int CleanProfiles(Dictionary<string, object> s)
        {
            int dropped = 0; if (s == null) return 0;
            foreach (string t in CraftTables)
            {
                object o; var d = s.TryGetValue(t, out o) ? o as Dictionary<string, object> : null; if (d == null) continue;
                foreach (string k in new List<string>(d.Keys)) if (k.StartsWith("#")) { d.Remove(k); dropped++; }
            }
            if (!s.ContainsKey("craft_overrides_seeded"))
            {
                object co; var cd = s.TryGetValue("craft_overrides", out co) ? co as Dictionary<string, object> : null;
                if (cd == null) s["craft_overrides"] = cd = new Dictionary<string, object>();
                if (!cd.ContainsKey("Aeris 3A")) cd["Aeris 3A"] = new Dictionary<string, object> { { "approach_speed", 100.0 }, { "threshold_speed", 52.0 } };
                s["craft_overrides_seeded"] = true;
            }
            return dropped;
        }
        internal static Dictionary<string, object> Migrate(Dictionary<string, object> current, Dictionary<string, object> legacy)
        {
            var result = new Dictionary<string, object>(current);
            object version;
            if (result.TryGetValue("migration_version", out version) && Convert.ToInt32(version) >= 1) return result;
            foreach (string key in new[] { "altitude_band_m", "autoland_reversers", "autoland_autowarp", "rotor_brake_park", "mayday_lights", "landing_spots", "stall_speeds" })
                if (!result.ContainsKey(key) && legacy.ContainsKey(key)) result[key] = legacy[key];
            object auto;
            if (legacy.TryGetValue("auto_trim_enabled", out auto))
                foreach (string axis in new[] { "master", "pitch", "roll", "yaw", "rotor" })
                {
                    if (result.ContainsKey("auto_" + axis)) continue;
                    var axes = auto as Dictionary<string, object>;
                    if (auto is bool) result["auto_" + axis] = auto;
                    else if (axes != null && axes.ContainsKey(axis)) result["auto_" + axis] = axes[axis];
                }
            result["migration_version"] = 1;
            return result;
        }
    }
}
