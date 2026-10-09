using System;
using System.Collections.Generic;
using System.IO;

namespace KSPChatBridge
{
    // Single validated command surface for menu, AI-off tools, bridge handoff and future AI routing.
    // Keep this file free of Unity/KSP types so the offline C# suite can compile it.
    internal static class NativeCommands
    {
        internal static readonly HashSet<string> Ported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "abort", "stop_current", "plane_hold", "takeoff", "land_here", "land_plane", "land_at_spot",
            "heli_control", "taxi_to", "taxi/list", "get_trim_state", "set_trim", "save_craft_notes",
            "save_landing_spot", "get_status", "autopilot_status", "set_gear", "set_brakes", "set_lights",
            "set_rcs", "set_sas", "auto_trim_now", "trim", "set_heading", "set_speed",
            "flightplan/check", "flightplan/fly", "flightplan/stop", "flightplan/resume", "flightplan/status",
            "list_landing_spots", "list_taxi_points", "set_ai_name", "remember_preference"
        };
        internal static bool IsPorted(string name) { return !string.IsNullOrEmpty(name) && Ported.Contains(name); }
        internal static Func<string> StatusPathProvider = DefaultStatusPath;
        static string DefaultStatusPath()
        {
            return Path.Combine("GameData", "KSPChatBridge", "PluginData", "native_control.json");
        }
        internal static string StatusPath { get { return StatusPathProvider(); } }
        internal static void WriteStatus(bool busy, string mode, string vesselId)
        {
            try
            {
                string path = StatusPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, "{\"busy\":" + (busy ? "true" : "false")
                    + ",\"mode\":\"" + (mode ?? "idle").Replace("\\", "\\\\").Replace("\"", "\\\"")
                    + "\",\"vessel\":\"" + (vesselId ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                    + "\",\"owner\":\"native\"}");
            }
            catch (Exception) { }
        }
        internal static void ClearStatus()
        {
            try { if (File.Exists(StatusPath)) File.WriteAllText(StatusPath, "{\"busy\":false,\"mode\":\"idle\",\"owner\":null}"); }
            catch (Exception) { }
        }
    }
}
