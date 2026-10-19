// GROK TAB1: Flight tablet navigation model (pure; tested offline). Top tabs FLIGHT / AUTOPILOT / ABORT / CRAFT; TAKEOFF,
// LANDING, STATUS, TAXI and TRIM are AUTOPILOT sub-pages opened by the page's bottom nav buttons (navigation, never actions).
using System;

namespace KSPChatBridge
{
    internal static class TabletNav
    {
        internal static readonly string[] TopTabs = { "FLIGHT", "AUTOPILOT", "ABORT", "CRAFT" };
        internal const int Flight = 0, Autopilot = 1, Abort = 2, Craft = 3;
        internal static readonly string[] ApSubPages = { "TAKEOFF", "LANDING", "STATUS", "TAXI", "TRIM" };
        internal const int SubTakeoff = 0, SubLanding = 1, SubStatus = 2, SubTaxi = 3, SubTrim = 4;
        /// <summary>Saved values >= 100 are the new layout (100 + 10 x (sub + 1) + top); below 100 = the old page index in
        /// FLIGHT, AIRCRAFT, APPROACH, TAXI, ABOUT, TRIM, ABORT, SHIP order, mapped onto the new tabs.</summary>
        internal const int SavedNewBase = 100;
        internal static void DecodeSaved(int saved, out int top, out int sub)
        {
            sub = -1;
            if (saved >= SavedNewBase)
            {
                int v = saved - SavedNewBase; top = v % 10; sub = v / 10 - 1;
                if (top < 0 || top >= TopTabs.Length) top = Flight;
                if (top != Autopilot || sub >= ApSubPages.Length) sub = -1;
                return;
            }
            switch (saved)
            {
                case 1: case 4: top = Autopilot; break;                 // AIRCRAFT, ABOUT (removed) -> AUTOPILOT
                case 2: top = Autopilot; sub = SubLanding; break;       // APPROACH -> AUTOPILOT / LANDING
                case 3: top = Autopilot; sub = SubTaxi; break;
                case 5: top = Autopilot; sub = SubTrim; break;
                case 6: top = Abort; break;
                case 7: top = Craft; break;                             // SHIP -> CRAFT
                default: top = Flight; break;
            }
        }
        internal static int EncodeSaved(int top, int sub) { return SavedNewBase + (top == Autopilot && sub >= 0 && sub < ApSubPages.Length ? (sub + 1) * 10 : 0) + Math.Max(0, Math.Min(TopTabs.Length - 1, top)); }
        /// <summary>Parked craft read 0 m/s (no jitter on the tile or the graph): landed / splashed / pre-launch and below 0.5 m/s.</summary>
        internal const double ParkedSpeed = .5;
        internal static double DisplaySpeed(bool onSurface, double srfSpeed) { return onSurface && Math.Abs(srfSpeed) < ParkedSpeed ? 0 : srfSpeed; }
        /// <summary>Live vs target: a deviation is only meaningful while the holds are really engaged and that hold is on.</summary>
        internal static bool ShowDeviation(bool holdsEngaged, bool holdOn) { return holdsEngaged && holdOn; }
        internal static string DeviationText(bool holdsEngaged, bool holdOn, double err, string fmt, string unit)
        { return !ShowDeviation(holdsEngaged, holdOn) || double.IsNaN(err) ? "-" : (err < 0 ? "-" : "+") + Math.Abs(err).ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) + " " + unit; }
        /// <summary>FLIGHT page light: tapping it disengages when the autopilot is on, else engages holds at the current values
        /// (only airborne). Returns "disengage" | "engage" | null (not allowed).</summary>
        internal static string LightAction(bool apActive, bool airborne, bool link) { return !link ? null : apActive ? "disengage" : airborne ? "engage" : null; }
    }
}
