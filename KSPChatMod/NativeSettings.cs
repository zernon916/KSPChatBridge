using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal static class NativeSettings
    {
        // Copy only flight settings. Cloud keys/chat data are outside this migration.
        internal static Dictionary<string, object> Migrate(Dictionary<string, object> current, Dictionary<string, object> legacy)
        {
            var result = new Dictionary<string, object>(current);
            object version;
            if (result.TryGetValue("migration_version", out version) && Convert.ToInt32(version) >= 1) return result;
            foreach (string key in new[] { "altitude_band_m", "autoland_reversers", "rotor_brake_park", "mayday_lights", "landing_spots", "stall_speeds" })
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
