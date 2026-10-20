using System.Linq;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 approachScroll;

        // Panel 1's shared selection/options; no ILS drawing or autopilot logic here.
        void TabletApproach(Rect area)
        {
            var rw = ChatWindow.SpotsSnapshot().Where(s => s[1] == "H").ToList();
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            approachScroll = GUILayout.BeginScrollView(approachScroll);
            float y = 0;
            string[] lines = (NativeFlightController.ApSummary() + "\n" +
                (landing.Length > 0 ? landing : "No active landing / ETA") + "\nSelected: " + LandingPick.Label(lpAirport, lpEnd, lpApp) +
                (lpAirport != LandingPick.Auto && lpEnd != LandingPick.Auto && ChartStore.Dir != null ? "  [chart in use: " + System.IO.Path.GetFileNameWithoutExtension(ChartStore.PathFor(lpAirport + " " + lpEnd)) + "]" : "")).Split('\n');   // GROK LAND1
            float lh = Mathf.Max(26, tabletFont * 1.5f);
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, pad + lines.Length * lh), "Landing status");
            for (int i = 0; i < lines.Length; i++)
                TabletUi.Label(new Rect(c.x, c.y + i * lh, c.width, lh), lines[i]);
            y += pad + lines.Length * lh + gap;
            if (NativeFlightController.CraftReady)
            {   // TAB-CRAFT1: this craft's approach / landing speeds (the same craft_overrides as AUTOPILOT / CRAFT / DETAILS), applied live
                Rect sc = TabletUi.CardPanel(new Rect(0, y, width, pad + 6 * row + 5 * gap), "Speeds / flare / go-around - " + NativeFlightController.CraftName + " (also in CRAFT > DETAILS)");
                float sy = CraftOverrideRow(sc, sc.y, CraftIndex(CraftOverrides.Approach), "Approach speed", row, TabletLink) + gap;
                sy = CraftOverrideRow(sc, sy, CraftIndex(CraftOverrides.Threshold), "Landing speed (at runway)", row, TabletLink) + gap;
                sy = CraftOverrideRow(sc, sy, CraftIndex(CraftOverrides.FlareStart), "Flare height (above runway)", row, TabletLink) + gap;
                sy = CraftOverrideRow(sc, sy, CraftIndex(CraftOverrides.FlareThrottle), "Flare throttle (blank = idle)", row, TabletLink) + gap;
                sy = CraftOverrideRow(sc, sy, CraftIndex(CraftOverrides.AutoGa), "Auto go-around (this craft)", row, TabletLink) + gap;
                bool gOn = NativeFlightController.GlobalAutoGoAround;   // LAND5k: global default (OFF); manual GO AROUND always works
                TabletUi.Label(new Rect(sc.x, sy, Mathf.Max(260, sc.width * .36f), row), "Auto go-around (all craft): " + (gOn ? "ON" : "OFF"));
                if (TabletUi.Button(new Rect(sc.x + Mathf.Max(260, sc.width * .36f), sy, Mathf.Max(120, tabletFont * 6f), row), gOn ? "TURN OFF" : "TURN ON", gOn, TabletLink)) NativeFlightController.GlobalAutoGoAround = !gOn;
                y += pad + 6 * row + 5 * gap + gap;
            }
            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK for controls", true);
                y += row + gap;
            }
            {   // GROK LAND1: airport / runway end / approach chart pickers (AUTO default)
                if (Time.realtimeSinceStartup - lpScanT > 5 || lpFiles == null)
                {
                    lpScanT = Time.realtimeSinceStartup;
                    try { lpFiles = ChartStore.Dir != null && System.IO.Directory.Exists(ChartStore.Dir) ? System.IO.Directory.GetFiles(ChartStore.Dir, "*.json") : new string[0]; } catch (System.Exception) { lpFiles = new string[0]; }
                }
                var airports = LandingPick.Airports(lpFiles, rw.Select(s => s[0]));
                if (!airports.Contains(lpAirport)) lpAirport = LandingPick.Auto;
                var ends = LandingPick.Ends(lpAirport, lpFiles); if (!ends.Contains(lpEnd)) lpEnd = LandingPick.Auto;
                var apps = LandingPick.Approaches(lpAirport, lpEnd, lpFiles); if (!apps.Contains(lpApp)) lpApp = LandingPick.Auto;
                float sh = row; int rowsA = Mathf.CeilToInt(airports.Count / 4f), rowsP = Mathf.CeilToInt(apps.Count / 2f);
                float ch = pad + 3 * lh + (rowsA + 1) * (sh + gap) + TabletUi.DropdownHeight(sh, apps.Count, lpDrop) + 2 * gap;
                c = TabletUi.CardPanel(new Rect(0, y, width, ch), "Runway / approach");
                float py = c.y;
                TabletUi.Label(new Rect(c.x, py, c.width, lh), "Airport" + (rw.Count == 0 ? "  (saved spots: LOAD below)" : "")); py += lh;
                for (int r0 = 0; r0 < rowsA; r0++)
                {
                    var seg = airports.Skip(r0 * 4).Take(4).ToArray(); int cur = System.Array.IndexOf(seg, lpAirport);
                    int s = TabletUi.Segments(new Rect(c.x, py, c.width, sh), seg, cur); if (TabletLink && s >= 0 && s != cur) { lpAirport = seg[s]; ChatLog.Write("g", "tablet approach: airport " + lpAirport); } py += sh + gap;
                }
                TabletUi.Label(new Rect(c.x, py, c.width, lh), "Runway end"); py += lh;
                { var seg = ends.ToArray(); int cur = System.Array.IndexOf(seg, lpEnd); int s = TabletUi.Segments(new Rect(c.x, py, c.width, sh), seg, cur); if (TabletLink && s >= 0 && s != cur) { lpEnd = seg[s]; ChatLog.Write("g", "tablet approach: runway end " + lpEnd); } py += sh + gap; }
                TabletUi.Label(new Rect(c.x, py, c.width, lh), "Approach chart"); py += lh;
                {   // GROK LAND2: drop-down, each chart under its own title
                    var labels = apps.Select(LandingPick.AppLabel).ToArray(); int cur = apps.IndexOf(lpApp);
                    int s = TabletUi.Dropdown(new Rect(c.x, py, c.width, sh), labels, cur, ref lpDrop, TabletLink); if (TabletLink && s >= 0 && s != cur) { lpApp = apps[s]; ChatLog.Write("g", "tablet approach: approach " + lpApp + " (" + labels[s] + ")"); }
                    py += TabletUi.DropdownHeight(sh, apps.Count, lpDrop) + gap;
                }
                y += ch + gap;
                if (rw.Count == 0 && TabletUi.Button(new Rect(12, y, width - 24, row), "LOAD SAVED RUNWAY SPOTS", false, TabletLink)) { ChatLog.Write("g", "tablet approach: load runways"); ChatWindow.RequestSpots(); }
                if (rw.Count == 0) y += row + gap;
            }
            c = TabletUi.CardPanel(new Rect(0, y, width, pad + 5 * (row + gap)), "Landing options");
            float cy = c.y;
            ApproachToggle(new Rect(c.x, cy, c.width, row), "Short final (5 km; otherwise 11 km)", ref shortFinal); cy += row + gap;
            ApproachToggle(new Rect(c.x, cy, c.width, row), "Vertical landing (otherwise runway)", ref vertical); cy += row + gap;
            ApproachToggle(new Rect(c.x, cy, c.width, row), "Chute / dV check for vertical landing", ref chuteCheck); cy += row + gap;
            ApproachField(new Rect(c.x, cy, c.width, row), "Approach heading", ref apprHdgT, 5); cy += row + gap;
            ApproachField(new Rect(c.x, cy, c.width, row), "Touch-and-go count", ref tgT, 3);
            y += pad + 5 * (row + gap) + gap;
            // Stack touch keys so they remain reachable at the minimum window size and largest font.
            {   // GROK TAB1: LANDING sub-page = one action button (GO AROUND / ABORT moved off; ABORT is a top tab)
                bool enabled = TabletLink;   // GROK LAND1: AUTO is always a valid selection
                if (NativeFlightController.LandingJob) { ActiveJobRow(new Rect(12, y, width - 24, row), "LANDING ACTIVE"); enabled = true; }   // GROK TAB2: no restart on repeat presses
                else if (TabletUi.ActionButton(new Rect(12, y, width - 24, row), "ENGAGE LANDING", enabled)) { ChatLog.Write("g", "tablet approach: LAND " + LandingPick.Label(lpAirport, lpEnd, lpApp)); EngageSelectedLanding(); }
                y += row + gap;

            }
            GUILayout.Space(y);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static string lpAirport = LandingPick.Auto, lpEnd = LandingPick.Auto, lpApp = LandingPick.Auto; static string[] lpFiles; static float lpScanT = -100; static bool lpDrop;
        /// <summary>GROK LAND1: ENGAGE LANDING with the picked airport / end / approach (the approach = active.json variant for that end).</summary>
        static void EngageSelectedLanding()
        {
            string v = LandingPick.Variant(lpApp);
            if (v != null && lpAirport != LandingPick.Auto && lpEnd != LandingPick.Auto)
            {
                string key = lpAirport + " " + lpEnd;
                try { ChartStore.SetActive(key, v); ChatLog.Write("approach", "tablet: chart for " + key + " set to " + lpApp); } catch (System.Exception e) { ChatLog.Write("approach", "tablet: chart select failed: " + e.Message); }
            }
            int tg; int.TryParse(tgT, out tg);
            string ah = apprHdgT.Trim().Length > 0 ? Num(apprHdgT, "-1") : "-1";
            if (vertical && lpAirport != LandingPick.Auto && lpAirport != "KSC" && lpAirport != "Island")
                ChatWindow.ToolFromMenu("land_at_spot", "{\"name\":" + ChatWindow.Json(lpAirport) + ",\"mode\":\"V\",\"chute_check\":" + (chuteCheck ? "true" : "false") + "}");
            else ChatWindow.ToolFromMenu("land_plane", LandingPick.Json(lpAirport, lpEnd, tg, shortFinal, ah, lpApp));
        }
        static void ApproachToggle(Rect r, string label, ref bool value)
        {
            bool next = TabletUi.Toggle(r, label, value, TabletLink);
            if (!TabletLink || next == value) return;
            value = next;
            ChatLog.Write("g", "tablet approach: " + label + " = " + value);
        }

        static void ApproachField(Rect r, string label, ref string value, int limit)
        {
            TabletUi.Label(new Rect(r.x, r.y, r.width * .6f, r.height), label);
            string next = TabletUi.Field(new Rect(r.x + r.width * .6f, r.y, r.width * .4f, r.height), value, TabletLink);
            if (next.Length > limit) next = next.Substring(0, limit);
            if (!TabletLink || next == value) return;
            value = next;
            ChatLog.Write("g", "tablet approach: " + label + " = " + value);
        }
    }
}
