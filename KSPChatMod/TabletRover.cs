// RS1: rover tablet pages (AUTOPILOT when the CRAFT page ROVER switch is on): DRIVE / SCIENCE TOUR / PLACES / CRAFT, plus the
// PLANE | ROVER switch card drawn at the very top of AUTOPILOT / CRAFT. Commands go through NativeFlightController.Execute.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static int tabletRoverSub = -1; static Vector2 roverScroll; static double tabletRoverSpeed = 6; static string tabletRoverNote = "";
        static readonly string[] RoverBuildings = { "VAB", "SPH", "R&D", "Tracking Station", "Mission Control", "Astronaut Complex", "Administration", "Launch Pad", "Runway" };

        /// <summary>RS1: PLANE | ROVER switch + the classifier hint. Returns the height used.</summary>
        float TabletVehicleSwitch(Rect r, float row)
        {
            string hint = "Detected: " + VehicleClass.Name(NativeFlightController.CurrentClass) + " (" + NativeFlightController.CurrentClassReason + ")";
            float pad = 16 + tabletFont * 1.6f, hh = TabletUi.SmallWrapHeight(hint, r.width - 24) + 4, h = pad + row + 6 + hh;
            Rect c = TabletUi.CardPanel(new Rect(r.x, r.y, r.width, h), "VEHICLE - plane pages or rover pages for this craft");
            bool rover = NativeFlightController.RoverMode; float bw = Mathf.Max(160, tabletFont * 8f);
            if (TabletUi.Button(new Rect(c.x, c.y, bw, row), "PLANE", !rover, TabletLink) && rover) { NativeFlightController.SetRoverMode(false); tabletRoverSub = -1; }
            if (TabletUi.Button(new Rect(c.x + bw + 8, c.y, bw, row), "ROVER", rover, TabletLink) && !rover) { NativeFlightController.SetRoverMode(true); tabletRoverSub = NativeFlightController.RoverMode ? TabletNav.RoverCraft : -1; }
            TabletUi.SmallWrap(new Rect(c.x, c.y + row + 6, c.width, hh), hint);
            return h;
        }

        void TabletRoverAutopilot(Rect area)
        {
            float kh = Mathf.Max(48, tabletFont * 2.6f);
            if (tabletRoverSub >= 0 && tabletRoverSub < TabletNav.RoverSubPages.Length)
            {
                float bw = Mathf.Max(150, tabletFont * 8.5f);
                if (TabletUi.Button(new Rect(area.x, area.y, bw, kh), "< ROVER")) { tabletRoverSub = -1; return; }
                TabletUi.Label(new Rect(area.x + bw + 14, area.y, area.width - bw - 14, kh), "ROVER / " + TabletNav.RoverSubPages[tabletRoverSub]);
                Rect sb = new Rect(area.x, area.y + kh + 8, area.width, Mathf.Max(60, area.height - kh - 8));
                switch (tabletRoverSub)
                {
                    case TabletNav.RoverDrive: TabletRoverDrive(sb); break;
                    case TabletNav.RoverTour: TabletRoverTour(sb); break;
                    case TabletNav.RoverPlaces: TabletRoverPlaces(sb); break;
                    default: TabletCraftSub(sb); break;
                }
                return;
            }
            int n = TabletNav.RoverSubPages.Length; float gap = 8, nw = (area.width - gap * (n - 1)) / n, ny = area.yMax - kh;
            for (int i = 0; i < n; i++)
                if (TabletUi.Button(new Rect(area.x + i * (nw + gap), ny, nw, kh), TabletNav.RoverSubPages[i] + " >")) tabletRoverSub = i;
            Rect body = new Rect(area.x, area.y, area.width, Mathf.Max(60, area.height - kh - gap));
            GUILayout.BeginArea(body);
            try
            {
                roverScroll = GUILayout.BeginScrollView(roverScroll);
                try { float h = TextCard(new Rect(0, 0, body.width - 26, 0), "ROVER STATUS", NativeFlightController.RoverStatusLines()); GUILayout.Space(h); }
                finally { GUILayout.EndScrollView(); }
            }
            finally { GUILayout.EndArea(); }
        }

        static void RoverRun(string tool, string json)
        {
            string r = NativeFlightController.Execute(tool, json ?? "{}"); tabletRoverNote = r ?? "";
            ChatLog.Write("tablet", "rover " + tool + " " + (json ?? "{}") + " -> " + tabletRoverNote);
        }
        static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        void TabletRoverDrive(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.5f), gap = 8, bw = Mathf.Max(120, tabletFont * 6f), y = area.y; bool en = TabletLink;
            float x = area.x;
            if (TabletUi.ActionButton(new Rect(x, y, bw * .6f, row), "-1", en, false)) tabletRoverSpeed = Math.Max(1, tabletRoverSpeed - 1); x += bw * .6f + 4;
            TabletUi.Label(new Rect(x, y, bw * 1.4f, row), "Speed " + tabletRoverSpeed.ToString("0", CultureInfo.InvariantCulture) + " m/s"); x += bw * 1.4f + 4;
            if (TabletUi.ActionButton(new Rect(x, y, bw * .6f, row), "+1", en, false)) tabletRoverSpeed = Math.Min(25, tabletRoverSpeed + 1); x += bw * .6f + 4;
            if (TabletUi.ActionButton(new Rect(x, y, bw * 1.3f, row), "HOLD SPEED", en)) RoverRun("drive_speed", "{\"speed\":" + tabletRoverSpeed.ToString(CultureInfo.InvariantCulture) + "}"); x += bw * 1.3f + 4;
            if (TabletUi.ActionButton(new Rect(x, y, bw, row), "STOP", en)) RoverRun("stop_current", "{}");
            y += row + gap;
            TabletUi.Label(new Rect(area.x, y, area.width, row * .8f), "DRIVE TO (KSC)", true); y += row * .8f;
            x = area.x; float cw = Mathf.Max(bw * 1.4f, tabletFont * 9f);
            foreach (string b in RoverBuildings)
            {
                if (x + cw > area.xMax) { x = area.x; y += row + 4; }
                if (TabletUi.ActionButton(new Rect(x, y, cw, row), b, en)) RoverRun("drive_to_building", "{\"buildings\":" + Q(b) + ",\"science\":true,\"speed\":" + tabletRoverSpeed.ToString(CultureInfo.InvariantCulture) + "}");
                x += cw + 4;
            }
            y += row + gap;
            if (!string.IsNullOrEmpty(tabletRoverNote)) TabletUi.LabelWrap(new Rect(area.x, y, area.width, Mathf.Max(row, area.yMax - y)), tabletRoverNote, true);
        }

        static string tabletPlaceName = "";
        /// <summary>RS5: PLACES - pin here (name field; blank = nearest building), list with STOP / KIND / RENAME / DELETE / DRIVE.</summary>
        void TabletRoverPlaces(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.5f), gap = 8, bw = Mathf.Max(110, tabletFont * 5.5f); bool en = TabletLink;
            float fw = Mathf.Max(220, area.width - 2 * bw - 3 * gap - 20);
            tabletPlaceName = TabletUi.Field(new Rect(area.x, area.y, fw, row), tabletPlaceName, en);
            if (TabletUi.ActionButton(new Rect(area.x + fw + gap, area.y, bw * 1.4f, row), "PIN HERE", en)) { tabletRoverNote = NativeFlightController.PlacePinHere(tabletPlaceName, null); tabletPlaceName = ""; }
            Rect body = new Rect(area.x, area.y + row + gap, area.width, Mathf.Max(60, area.height - row - gap));
            GUILayout.BeginArea(body);
            try
            {
                roverScroll = GUILayout.BeginScrollView(roverScroll);
                try
                {
                    float w = body.width - 26, y = 0, sb = Mathf.Max(90, tabletFont * 4.5f);
                    if (!string.IsNullOrEmpty(tabletRoverNote)) { TabletUi.LabelWrap(new Rect(0, y, w, row), tabletRoverNote, true); y += row; }
                    TabletUi.SmallWrap(new Rect(0, y, w, row), "Type a name, then PIN HERE (blank = nearest building) or RENAME on a row. STOP = visit on the rover science tour."); y += row;
                    string body0 = NativeFlightController.CraftReady && FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.mainBody.bodyName : "";
                    foreach (var p in NativeFlightController.AllPlaces())
                    {
                        if (p.Body != body0) continue;
                        float bh = row * 2 + 3 * 4; TabletUi.RowBox(new Rect(0, y, w, bh)); TabletUi.Label(new Rect(8, y + 4, w - 16, row), p.Line()); float x = 8, by = y + row + 8;
                        if (TabletUi.Button(new Rect(x, by, sb, row), "STOP", p.Stop, en)) NativeFlightController.PlaceToggleStop(p.Name); x += sb + 4;
                        if (TabletUi.Button(new Rect(x, by, sb * 1.3f, row), p.Kind.ToUpperInvariant(), false, en)) NativeFlightController.PlaceCycleKind(p.Name); x += sb * 1.3f + 4;
                        if (TabletUi.ActionButton(new Rect(x, by, sb * 1.2f, row), "RENAME", en && tabletPlaceName.Trim().Length > 0, false)) { tabletRoverNote = NativeFlightController.PlaceRename(p.Name, tabletPlaceName.Trim()); tabletPlaceName = ""; } x += sb * 1.2f + 4;
                        if (NativeFlightController.PlaceIsManual(p.Name) && TabletUi.ActionButton(new Rect(x, by, sb, row), "DELETE", en, false)) NativeFlightController.PlaceDelete(p.Name); x += sb + 4;
                        if (TabletUi.ActionButton(new Rect(x, by, sb, row), "DRIVE", en, false)) RoverRun("drive_to", "{\"name\":" + Q(p.Lat.ToString("0.000000", CultureInfo.InvariantCulture) + "," + p.Lon.ToString("0.000000", CultureInfo.InvariantCulture)) + "}");
                        y += bh + gap;
                    }
                    GUILayout.Space(y);
                }
                finally { GUILayout.EndScrollView(); }
            }
            finally { GUILayout.EndArea(); }
        }

        /// <summary>RS7b: green check mark drawn from two rotated bars (no font glyph needed).</summary>
        static void CheckMark(Rect r)
        {
            var m = GUI.matrix; var col = new Color(.30f, .85f, .40f); float t = Mathf.Max(3, r.height * .14f);
            Vector2 a = new Vector2(r.x + r.width * .18f, r.y + r.height * .55f), b = new Vector2(r.x + r.width * .42f, r.y + r.height * .80f), c2 = new Vector2(r.x + r.width * .85f, r.y + r.height * .22f);
            foreach (var seg in new[] { new[] { a, b }, new[] { b, c2 } })
            {
                Vector2 p0 = seg[0], p1 = seg[1]; float len = Vector2.Distance(p0, p1), ang = Mathf.Atan2(p1.y - p0.y, p1.x - p0.x) * Mathf.Rad2Deg;
                GUI.matrix = m; GUIUtility.RotateAroundPivot(ang, p0);
                TabletUi.Fill(new Rect(p0.x, p0.y - t / 2, len + t / 2, t), col);
            }
            GUI.matrix = m;
        }

        void TabletRoverTour(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.5f), gap = 8, bw = Mathf.Max(200, tabletFont * 10f); bool en = TabletLink;
            // RS7: science tour checklist - top bar ALL / NONE / checked total / START TOUR / STOP, then one box per site
            var sites = NativeFlightController.RoverChecklist();
            float sb = Mathf.Max(90, tabletFont * 4.5f), x0 = area.x;
            if (TabletUi.Button(new Rect(x0, area.y, sb, row), "ALL", false, en && sites.Count > 0)) NativeFlightController.RoverChecklistAll(true);
            if (TabletUi.Button(new Rect(x0 + sb + 6, area.y, sb, row), "NONE", false, en && sites.Count > 0)) NativeFlightController.RoverChecklistAll(false);
            float tx = x0 + 2 * (sb + 6) + 4, sw = Mathf.Max(150, tabletFont * 7f);
            float rightW = bw + 8 + bw * .6f;
            TabletUi.Label(new Rect(tx, area.y, Mathf.Max(60, area.width - (tx - area.x) - rightW - 8), row), "Checked: " + TourChecklist.CheckedTotal(sites).ToString("0.0", CultureInfo.InvariantCulture) + " science");
            float bx = area.xMax - rightW;
            if (TabletUi.ActionButton(new Rect(bx, area.y, bw, row), NativeFlightController.RoverChecklistRunning ? "TOUR RUNNING" : "START TOUR", en && TourChecklist.RouteSites(sites).Count > 0))
            { tabletRoverNote = NativeFlightController.RoverChecklistStart(20); }   // same 20 m/s cap as the old tablet tour; the guard limits per surface
            if (TabletUi.ActionButton(new Rect(bx + bw + 8, area.y, bw * .6f, row), "STOP", en)) RoverRun("stop_current", "{}");
            Rect body = new Rect(area.x, area.y + row + gap, area.width, Mathf.Max(60, area.height - row - gap));
            GUILayout.BeginArea(body);
            try
            {
                roverScroll = GUILayout.BeginScrollView(roverScroll);
                try
                {
                    float w = body.width - 26, y = 0;
                    if (!string.IsNullOrEmpty(tabletRoverNote)) y += TextCard(new Rect(0, y, w, 0), "LAST COMMAND", new List<string> { tabletRoverNote }) + gap;
                    if (!string.IsNullOrEmpty(NativeFlightController.RoverAlert)) y += TextCard(new Rect(0, y, w, 0), "ROVER ALERT", new List<string> { NativeFlightController.RoverAlert }) + gap;   // RS7b
                    float rh = Mathf.Max(34, tabletFont * 2f), cb = rh - 6;
                    if (sites.Count == 0) y += TextCard(new Rect(0, y, w, 0), "SCIENCE SITES", new List<string> { "No sites here: land the rover near KSC or mark PLACES on this body." }) + gap;
                    foreach (var s in sites)
                    {
                        float h = rh * (1 + s.Exps.Count) + 8;
                        TabletUi.RowBox(new Rect(0, y, w, h));
                        float yy = y + 4;
                        if (TabletUi.Button(new Rect(6, yy + 3, cb, cb), s.On ? "X" : "", s.On, en && s.Total > 0)) NativeFlightController.RoverChecklistSite(s, !s.On);
                        string dist = double.IsNaN(s.Distance) ? "?" : s.Distance < 1000 ? s.Distance.ToString("0", CultureInfo.InvariantCulture) + " m" : (s.Distance / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km";
                        TabletUi.Label(new Rect(cb + 14, yy, w * .55f, rh), s.Name, s.Total <= 0 || !s.On);
                        TabletUi.Label(new Rect(w * .55f, yy, w * .45f - 8, rh), dist + "   " + s.Total.ToString("0.0", CultureInfo.InvariantCulture) + " sci", s.Total <= 0 || !s.On);
                        yy += rh;
                        foreach (var e in s.Exps)
                        {
                            if (TabletUi.Button(new Rect(cb + 14, yy + 3, cb, cb), e.On ? "X" : "", e.On && !e.Blocked, en && e.Gain > 0)) NativeFlightController.RoverChecklistExp(s, e, !e.On);
                            string t = e.Title + "  +" + e.Gain.ToString("0.0", CultureInfo.InvariantCulture) + (e.Blocked ? "   (one-shot: runs at " + e.RunsAt + ")" : "");
                            TabletUi.Label(new Rect(2 * cb + 24, yy, w - 2 * cb - 30 - (e.Ran ? cb + 12 : 0), rh), t, e.Dim);
                            if (e.Ran) CheckMark(new Rect(w - cb - 10, yy + 3, cb, cb));   // RS7b: ran on this tour (right side, separate from the selection box)
                            yy += rh;
                        }
                        y += h + gap;
                    }
                    y += TextCard(new Rect(0, y, w, 0), "SCIENCE ON BOARD (worth here)", NativeFlightController.ScienceInventoryLines()) + gap;
                    GUILayout.Space(y);
                }
                finally { GUILayout.EndScrollView(); }
            }
            finally { GUILayout.EndArea(); }
        }
    }
}