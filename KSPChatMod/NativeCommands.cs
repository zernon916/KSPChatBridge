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
            "flightplan/check", "flightplan/fly", "flightplan/stop", "flightplan/resume", "flightplan/status", "flightplan/follow",
            "list_landing_spots", "list_taxi_points", "set_ai_name", "remember_preference",
            // P5-2 flight residuals (NativeFlightResidual.cs)
            "land", "fly_to", "fly_to_place", "touch_and_go", "go_around", "circle_here", "turn", "plane_pitch",
            "prop_control", "afterburner", "engine_mode", "flaps", "set_throttle", "set_engines", "cut_engines", "set_altitude",
            "level_off", "set_sas_mode", "abort_ag", "action_group", "fuel_check", "get_delta_v", "get_landing_eta", "how_far",
            "landing_check", "crew_report", "flight_report", "damage_report",
            // P5-3 lifecycle + science (NativeLifecycle.cs)
            "stage", "recover_vessel", "launch_craft", "deploy_parachutes", "eject_kerbal", "run_science", "reset_experiments", "set_science_watcher",
            // P5-4 orbital / docking, MechJeb optional (NativeOrbital.cs)
            "sync_orbit_altitude", "time_to_target", "warp_to_apoapsis", "warp_to_soi_change", "circularize", "change_apoapsis",
            "change_periapsis", "deorbit_burn", "change_inclination", "sun_lock", "antenna_lock", "mechjeb_ascent", "dock_with",
            "land_at", "land_at_ksc",
            // MechJeb 2.15 planners in-process (NativeMechJebOps.cs)
            "transfer_to", "match_target_plane", "launch_to_target_plane", "course_correction", "station_keep", "apsis_longitude",
            // POST-TESTING features (native-only tools, schemas in tools/gen_tool_schemas.py NATIVE_ONLY)
            "hold_pattern", "follow_terrain", "fuel_check_return", "formation", "tech_advisor", "drive_to_building", "roll", "autopilot", "make_flight_plan", "scan_coverage", "mapping_orbit"
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
                AtomicFile.Write(path, "{\"busy\":" + (busy ? "true" : "false")
                    + ",\"mode\":\"" + (mode ?? "idle").Replace("\\", "\\\\").Replace("\"", "\\\"")
                    + "\",\"vessel\":\"" + (vesselId ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                    + "\",\"owner\":\"native\"}");
            }
            catch (Exception) { }
        }
        internal static void ClearStatus()
        {
            try { if (File.Exists(StatusPath)) AtomicFile.Write(StatusPath, "{\"busy\":false,\"mode\":\"idle\",\"owner\":null}"); }
            catch (Exception) { }
        }
    }
}
