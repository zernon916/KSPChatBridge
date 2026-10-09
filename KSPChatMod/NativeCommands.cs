using System;
using System.Collections.Generic;
using System.IO;

namespace KSPChatBridge
{
    // Single validated command surface for menu, AI-off tools, bridge handoff and future AI routing.
    internal static class NativeCommands
    {
        internal static readonly HashSet<string> Ported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "abort", "stop_current", "plane_hold", "takeoff", "land_here", "land_plane", "land_at_spot",
            "heli_control", "taxi_to", "taxi/list", "get_trim_state", "set_trim", "save_craft_notes",
            "save_landing_spot", "get_status", "autopilot_status", "set_gear", "set_brakes", "set_lights",
            "set_rcs", "set_sas", "auto_trim_now", "trim", "set_heading", "set_speed",
            "flightplan/check", "flightplan/fly", "flightplan/stop", "flightplan/resume", "flightplan/status"
        };
        internal static bool IsPorted(string name) { return !string.IsNullOrEmpty(name) && Ported.Contains(name); }
        internal static string StatusPath
        {
            get
            {
                return Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KSPChatBridge", "PluginData", "native_control.json");
            }
        }
        internal static void WriteStatus(bool busy, string mode, string vesselId)
        {
            try
            {
                string dir = Path.GetDirectoryName(StatusPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(StatusPath, "{\"busy\":" + (busy ? "true" : "false")
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
