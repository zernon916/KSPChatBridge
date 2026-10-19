// GROK TAB1: Flight tablet navigation model (pure; tested offline). Top tabs FLIGHT / AUTOPILOT / ABORT / CRAFT; TAKEOFF,
// LANDING, STATUS, TAXI and TRIM are AUTOPILOT sub-pages opened by the page's bottom nav buttons (navigation, never actions).
using System;
using System.Collections.Generic;

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
        /// <summary>GROK TAB2: plain-words job + stage. A landing job is LANDING from the ferry on (never "hold"):
        /// CRUISE TO RUNWAY / APPROACH (join / intercept) / FINAL / TOUCHDOWN / ROLLOUT / GO AROUND.</summary>
        internal static void JobLabel(string mode, string phase, bool ferryPending, out string job, out string stage)
        {
            stage = "";
            if (ferryPending) { job = "LANDING"; stage = "CRUISE TO RUNWAY"; return; }
            switch (mode ?? "idle")
            {
                case "landing":
                    job = "LANDING";
                    stage = phase == "entry" ? "APPROACH (join)" : phase == "intercept" ? "APPROACH (intercept)" : phase == "final" ? "FINAL"
                        : phase == "flare" ? "TOUCHDOWN" : phase == "rollout" ? "ROLLOUT" : phase == "go around" ? "GO AROUND" : (phase ?? "").ToUpperInvariant();
                    return;
                case "takeoff": job = "TAKEOFF"; stage = (phase ?? "").ToUpperInvariant(); return;
                case "hold": job = "HOLDS"; return;
                case "idle": job = "IDLE"; return;
                default: job = mode.ToUpperInvariant(); return;
            }
        }
        internal static string JobText(string mode, string phase, bool ferryPending)
        { string j, s; JobLabel(mode, phase, ferryPending, out j, out s); return s.Length > 0 ? j + " - " + s : j; }
        internal static string LightAction(bool apActive, bool airborne, bool link) { return !link ? null : apActive ? "disengage" : airborne ? "engage" : null; }
    }
    /// <summary>GROK TO2: TAKEOFF sub-page settings (persisted in PluginData/takeoff_settings.txt) and the takeoff tool JSON they send.
    /// Blank climb rate = auto; blank heading = runway heading.</summary>
    internal sealed class TakeoffSettings
    {
        internal string Alt = "300", Climb = "", Hdg = "", MaxPitch = "30"; internal bool Flaps = true;
        static double N(string s) { double d; return double.TryParse((s ?? "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : double.NaN; }
        static string J(double d) { return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
        internal string Serialize() { return string.Join("|", new[] { Alt, Climb, Hdg, MaxPitch, Flaps ? "1" : "0" }); }
        internal static TakeoffSettings Parse(string t)
        {
            var s = new TakeoffSettings(); if (string.IsNullOrEmpty(t)) return s; var p = t.Trim().Split('|'); if (p.Length < 5) return s;
            s.Alt = p[0]; s.Climb = p[1]; s.Hdg = p[2]; s.MaxPitch = p[3]; s.Flaps = p[4] != "0"; return s;
        }
        internal string ToJson()
        {
            double a = N(Alt), c = N(Climb), h = N(Hdg), m = N(MaxPitch);
            var parts = new System.Collections.Generic.List<string>();
            if (!double.IsNaN(a) && a > 0) parts.Add("\"altitude_m\":" + J(a));
            if (!double.IsNaN(c) && c > 0) parts.Add("\"climb_rate\":" + J(c));
            if (!double.IsNaN(h)) parts.Add("\"heading\":" + J(((h % 360) + 360) % 360));
            if (!double.IsNaN(m) && m > 0) parts.Add("\"max_pitch\":" + J(m));
            parts.Add("\"use_flaps\":" + (Flaps ? "true" : "false"));
            return "{" + string.Join(",", parts.ToArray()) + "}";
        }
    }
    /// <summary>GROK LAND1: LANDING sub-page pickers - airport (AUTO = nearest, KSC, Island, other chart prefixes, saved runway spots),
    /// runway end (AUTO = best aligned, else from the built-ins + chart files), approach chart variant (AUTO = keep the active one).</summary>
    internal static class LandingPick
    {
        internal const string Auto = "AUTO";
        static IEnumerable<string> Keys(IEnumerable<string> files)
        {
            foreach (var f in files ?? new string[0])
            {
                string n = System.IO.Path.GetFileName(f ?? ""); if (!n.EndsWith(".json") || n == "active.json" || n.StartsWith("TAXI")) continue;
                string v; string k = ChartStore.KeyOf(n, out v); if (k.Split(' ').Length == 2) yield return k;
            }
        }
        internal static List<string> Airports(IEnumerable<string> files, IEnumerable<string> spots)
        {
            var l = new List<string> { Auto, "KSC", "Island" };
            foreach (var k in Keys(files)) { string a = k.Split(' ')[0]; if (!l.Contains(a)) l.Add(a); }
            foreach (var s in spots ?? new string[0]) if (!string.IsNullOrEmpty(s) && !l.Contains(s) && !s.StartsWith("Runway")) l.Add(s);
            return l;
        }
        internal static List<string> Ends(string airport, IEnumerable<string> files)
        {
            var l = new List<string> { Auto }; if (airport == Auto || string.IsNullOrEmpty(airport)) return l;
            if (airport == "KSC" || airport == "Island") { l.Add("09"); l.Add("27"); }
            foreach (var k in Keys(files)) { var p = k.Split(' '); if (p[0] == airport && !l.Contains(p[1])) l.Add(p[1]); }
            var e = l.GetRange(1, l.Count - 1); e.Sort(); l.RemoveRange(1, l.Count - 1); l.AddRange(e); return l;
        }
        /// <summary>Approach charts for one end: AUTO + file names without .json (KSC_27, KSC_27_generated, KSC_27_tight ...).</summary>
        internal static List<string> Approaches(string airport, string end, IEnumerable<string> files)
        {
            var l = new List<string> { Auto }; if (airport == Auto || end == Auto) return l; string key = airport + " " + end; var found = new List<string>();
            foreach (var f in files ?? new string[0])
            {
                string n = System.IO.Path.GetFileName(f ?? ""); if (!n.EndsWith(".json") || n == "active.json") continue;
                string v; if (ChartStore.KeyOf(n, out v) == key) found.Add(n.Substring(0, n.Length - 5));
            }
            found.Sort(); l.AddRange(found); return l;
        }
        /// <summary>Chart variant for active.json from an approach item ("" = the end's default file; null = AUTO, leave as is).</summary>
        internal static string Variant(string approach) { if (approach == Auto || string.IsNullOrEmpty(approach)) return null; string v; ChartStore.KeyOf(approach + ".json", out v); return v; }
        internal static string Json(string airport, string end, int touchAndGo, bool shortFinal, string approachHeading, string approach = null)
        {
            string chart = approach == null || approach == Auto || airport == Auto || end == Auto ? "" : ",\"chart\":\"" + approach.Replace("\"", "") + "\"";
            string name = airport == Auto || string.IsNullOrEmpty(airport) ? "nearest" : airport, rw = end == Auto || string.IsNullOrEmpty(end) ? "" : end;
            return "{\"name\":\"" + name.Replace("\\", "").Replace("\"", "") + "\",\"runway\":\"" + rw + "\",\"touch_and_go\":" + touchAndGo + ",\"final_km\":" + (shortFinal ? "5" : "0") + ",\"approach_heading\":" + (string.IsNullOrEmpty(approachHeading) ? "-1" : approachHeading) + ",\"final\":\"" + (shortFinal ? "short" : "long") + "\"" + chart + "}";
        }
        internal static string Label(string airport, string end, string approach)
        {
            return (airport == Auto ? "nearest airfield" : airport) + " " + (end == Auto ? "(best-aligned end)" : end) + ", approach " + (approach == Auto ? "AUTO (active chart)" : approach);
        }
    }
}
