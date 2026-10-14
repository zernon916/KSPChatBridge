// AICS tablet (ROCKET_PLAN section 4): a separate rounded, bezel-framed window with big fonts and bottom tabs
// Mission / Burns / Map / Graphs / Tests. Partial AicsMenu so it shares the settings file (tx/ty/tw/th), the MFD skin
// textures and the MFD key handlers (OnKey) - the Tests tab drives the same LEARN code paths as the MFD LEARN page.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        const int TabletId = 0x4B434D54;
        const float TabletMinW = 520, TabletMinH = 420, TabletBezel = 16, TabletHead = 40, TabletTabH = 46;
        internal static readonly string[] TabletTabs = { "Mission", "Burns", "Map", "Graphs", "Tests", "Encyclopedia", "Flight", "Plan", "Orbit", "Log", "Comms", "Contracts", "Science", "Dv Map", "Xfer", "Campaign", "Crew", "Systems" };
        /// <summary>LIVE LINK (default OFF): OFF = information only - the tablet shows live craft data but every control is
        /// disabled and does nothing (the autopilot itself keeps running). ON = the tablet keys work like the MFD keys.</summary>
        internal static bool TabletLink;
        internal static bool TabletVisible;
        static Rect tabletRect = new Rect(-1, 120, 900, 680);
        static int tabletTab = 4, tabletFont = 18, tabletMapPage; static bool tabletResizing;
        static Texture2D tabletRound, tabletScreenRound; static GUIStyle tabletFrame, tabletScreen, tabletText, tabletHead, tabletKey, tabletTabOn, tabletTabOff;
        static int tabletStyledFont = -1;

        /// <summary>The tablet Map tab needs the map bitmap kept alive (MapWindow.Update), same as the MFD MAP group.</summary>
        static bool TabletMapOn { get { return TabletVisible && tabletTab == 2 && HighLogic.LoadedSceneIsFlight; } }
        internal static void ToggleTablet() { TabletVisible = !TabletVisible; Save(); }

        static Texture2D RoundTex(int n, float rad, Color fill, Color edge)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float cx = Mathf.Clamp(x + .5f, rad, n - rad), cy = Mathf.Clamp(y + .5f, rad, n - rad);
                    float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(cx, cy));
                    float a = Mathf.Clamp01(rad - d + .5f); bool e = d > rad - 2.5f;
                    var c = e ? edge : Color.Lerp(fill, fill * .8f, (float)y / n); c.a = a * (e ? edge.a : fill.a); t.SetPixel(x, y, c);
                }
            t.Apply(); return t;
        }

        static void BuildTabletStyles()
        {
            if (mfdSkin == null) BuildMfdSkin();
            if (tabletRound == null)
            {
                tabletRound = RoundTex(48, 18, new Color(.22f, .23f, .24f, 1), new Color(.45f, .46f, .48f, 1));
                tabletScreenRound = RoundTex(32, 10, new Color(0, 0, 0, 1), new Color(.06f, .06f, .07f, 1));
            }
            tabletFrame = new GUIStyle { border = new RectOffset(20, 20, 20, 20) }; tabletFrame.normal.background = tabletRound;
            tabletScreen = new GUIStyle { border = new RectOffset(12, 12, 12, 12) }; tabletScreen.normal.background = tabletScreenRound;
            tabletText = new GUIStyle(GUI.skin.label) { font = mono, fontSize = tabletFont, wordWrap = true, richText = true, clipping = TextClipping.Clip };
            tabletText.normal.textColor = Phos;
            tabletHead = new GUIStyle(tabletText) { fontStyle = FontStyle.Bold, fontSize = tabletFont + 4, wordWrap = false };
            tabletKey = new GUIStyle(keyStyle) { fontSize = tabletFont, wordWrap = false };
            tabletTabOff = new GUIStyle(keyStyle) { fontSize = tabletFont };
            tabletTabOn = new GUIStyle(tabletTabOff); tabletTabOn.normal.background = tabletTabOn.hover.background = keyDown;
            tabletTabOn.normal.textColor = tabletTabOn.hover.textColor = Phos;
            tabletStyledFont = tabletFont;
        }

        void DrawTabletWindow()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;   // flight + map view only (map view is the flight scene); never the editor
            if (mfdSkin == null || tabletFrame == null || tabletRound == null || tabletStyledFont != tabletFont) BuildTabletStyles();
            if (tabletRect.x < 0) tabletRect.x = Mathf.Max(0, (Screen.width - tabletRect.width) / 2);
            tabletRect.width = Mathf.Clamp(tabletRect.width, TabletMinW, Mathf.Max(TabletMinW, Screen.width));
            tabletRect.height = Mathf.Clamp(tabletRect.height, TabletMinH, Mathf.Max(TabletMinH, Screen.height));
            tabletRect.x = Mathf.Clamp(tabletRect.x, -tabletRect.width + 80, Screen.width - 80); tabletRect.y = Mathf.Clamp(tabletRect.y, 0, Screen.height - 40);
            if (savePendingTablet) { savePendingTablet = false; Save(); }   // bezel drag ended last event
            FlightSample();
            tabletRect = GUI.Window(TabletId, tabletRect, DrawTablet, "", GUIStyle.none);
        }

        void DrawTablet(int id)
        {
            float W = tabletRect.width, H = tabletRect.height, b = TabletBezel;
            GUI.Box(new Rect(0, 0, W, H), "", tabletFrame);
            // header in the bezel: title, font size, close
            float lw = Mathf.Max(150, tabletFont * 8.5f);
            GUI.Label(new Rect(b + 6, 6, W - 290 - lw, TabletHead - 6), "AICS TABLET  " + TabletTabs[tabletTab].ToUpperInvariant(), tabletHead);
            float kw = Mathf.Max(40, tabletFont * 2.4f), kh = TabletHead - 12;
            var oc = GUI.backgroundColor; GUI.backgroundColor = TabletLink ? new Color(.5f, 1f, .5f) : new Color(1f, .75f, .35f);
            if (GUI.Button(new Rect(W - b - kh - 2 * kw - lw - 26, 6, lw, kh), TabletLink ? "LIVE LINK: ON" : "LIVE LINK: OFF", TabletLink ? tabletTabOn : tabletKey)) { TabletLink = !TabletLink; Save(); }
            GUI.backgroundColor = oc;
            if (GUI.Button(new Rect(W - b - kh - 2 * kw - 16, 6, kw, kh), "A-", tabletKey)) { tabletFont = Mathf.Max(12, tabletFont - 2); Save(); }
            if (GUI.Button(new Rect(W - b - kh - kw - 10, 6, kw, kh), "A+", tabletKey)) { tabletFont = Mathf.Min(32, tabletFont + 2); Save(); }
            if (GUI.Button(new Rect(W - b - kh, 6, kh, kh), "\u2715", tabletKey)) { TabletVisible = false; Save(); }
            // screen
            Rect scr = new Rect(b, TabletHead, W - 2 * b, H - TabletHead - TabletTabH - b);
            GUI.Box(scr, "", tabletScreen);
            Rect area = new Rect(scr.x + 10, scr.y + 8, scr.width - 20, scr.height - 16);
            var saved = GUI.skin; GUI.skin = mfdSkin;
            try
            {
                switch (tabletTab)
                {
                    case 2: TabletMap(area); break;
                    case 4: TabletTests(area); break;
                    case 5: TabletEncyclopedia(area); break;
                    case 6: TabletFlightTab(area); break;
                    case 7: TabletPlanTab(area); break;
                    case 8: TabletOrbitTab(area); break;
                    case 9: TabletFlightLog(area); break;
                    case 10: TabletComms(area); break;
                    case 11: TabletContracts(area); break;
                    case 12: TabletScience(area); break;
                    case 13: TabletDvMap(area); break;
                    case 14: TabletTransfer(area); break;
                    case 15: TabletCampaign(area); break;
                    case 16: TabletCrewTab(area); break;
                    default:
                        GUI.Label(new Rect(area.x, area.y + area.height * .4f, area.width, tabletFont * 4), TabletTabs[tabletTab].ToUpperInvariant() + "\n\ncoming soon", new GUIStyle(tabletHead) { alignment = TextAnchor.MiddleCenter, wordWrap = true });
                        break;
                }
            }
            catch (Exception ex) { GUI.Label(new Rect(area.x, area.yMax - 30, area.width, 30), "page error: " + ex.Message, tabletText); }
            GUI.skin = saved;
            // bottom tabs
            float tw = (W - 2 * b) / TabletTabs.Length;
            for (int i = 0; i < TabletTabs.Length; i++)
                if (GUI.Button(new Rect(b + i * tw + 2, H - TabletTabH - b + 6, tw - 4, TabletTabH - 6), TabletTabs[i], i == tabletTab ? tabletTabOn : tabletTabOff) && tabletTab != i) { tabletTab = i; Save(); }
            // resize corner (bottom-right bezel) + drag by the bezel
            Rect grip = new Rect(W - b - 4, H - b - 4, b + 4, b + 4); Event e = Event.current;
            var gs = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.LowerRight }; gs.normal.textColor = new Color(.6f, .62f, .65f); GUI.Label(grip, "\u25E2", gs);
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition)) { tabletResizing = true; e.Use(); }
            if (tabletResizing)
            {
                if (e.type == EventType.MouseDrag) { tabletRect.width = Mathf.Max(TabletMinW, e.mousePosition.x + 4); tabletRect.height = Mathf.Max(TabletMinH, e.mousePosition.y + 4); e.Use(); }
                if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp) { tabletResizing = false; Save(); }
            }
            if (e.type == EventType.MouseUp && !tabletResizing && new Rect(0, 0, W, TabletHead).Contains(e.mousePosition)) savePendingTablet = true;
            GUI.DragWindow(new Rect(0, 0, W, TabletHead));   // drag by the top bezel
            GUI.DragWindow(new Rect(0, 0, b, H)); GUI.DragWindow(new Rect(W - b, 0, b, H - b - 4));
        }
        static bool savePendingTablet;

        static readonly string[] TabletMapPages = { "MAP", "CHART", "ILS" };
        void TabletMap(Rect area)
        {
            if (MapWindow.Inst == null) { GUI.Label(area, "Map, charts and ILS are available in flight.", tabletText); return; }
            float kh = tabletFont * 1.9f, x = area.x;
            if (!TabletLink && tabletMapPage == 2) tabletMapPage = 0;   // info-only: the ILS page is hidden and never drawn from the tablet
            int pages = TabletLink ? TabletMapPages.Length : 2;
            for (int i = 0; i < pages; i++)
            {
                float w = tabletFont * 4.5f; if (GUI.Button(new Rect(x, area.y, w, kh), TabletMapPages[i], i == tabletMapPage ? tabletTabOn : tabletTabOff)) { tabletMapPage = i; Save(); }
                x += w + 4;
            }
            x += 12; var keys = tabletMapPage == 1 ? new[] { "RWY <", "RWY >", "VARIANT" } : tabletMapPage == 2 ? new[] { "RWY <", "RWY >" } : new[] { "ZOOM+", "ZOOM-", "CENTER", "CTR RWY" };
            foreach (var k in keys) { float w = tabletFont * (k.Length * .65f + 1.2f); if (x + w > area.xMax) break; GUI.enabled = TabletLink; bool hit = GUI.Button(new Rect(x, area.y, w, kh), k, tabletKey); GUI.enabled = true; if (hit && TabletLink) OnKey(TabletMapPages[tabletMapPage].ToLowerInvariant(), k); x += w + 4; }
            var et = Event.current.type;   // info-only: no map mouse (pan / waypoint drag / wheel) reaches MapWindow
            if (!TabletLink && (et == EventType.MouseDown || et == EventType.MouseDrag || et == EventType.MouseUp || et == EventType.ScrollWheel)) return;
            if (!TabletLink) GUI.Label(new Rect(area.x, area.yMax - tabletFont * 1.6f, area.width, tabletFont * 1.6f), "INFO ONLY - LIVE LINK off", tabletText);
            MapWindow.Inst.DrawEmbedded(new Rect(area.x, area.y + kh + 6, area.width, area.height - kh - 6), tabletMapPage);   // the cockpit MFD map pass
        }

        /// <summary>Learn checklist row -> index into LearnFlight.Codes (rows "&gt;[x] 3 ..." / "   [ ] 9b ..."), -1 for text rows.</summary>
        internal static List<int> LearnRowCodes(List<string> ll)
        {
            var r = new List<int>(); int j = 0;
            foreach (var l in ll) r.Add(System.Text.RegularExpressions.Regex.IsMatch(l, @"^[> ] {0,2}\[[ x*>]\] \d") ? j++ : -1);
            return r;
        }
        static Vector2 tabletTestsScroll;
        void TabletTests(Rect area)
        {   // S5: the LEARN page; keys go through OnKey("learn", ...) exactly like the MFD LEARN page's right column.
            // Touch: tap a test row to put the TOGGLE cursor there, tap the selected row again to toggle it (LIVE LINK on only).
            var keys = MfdNav.Right("learn"); float kh = Mathf.Max(48, tabletFont * 2.6f), x = area.x, y = area.y;
            bool savedEnabled = GUI.enabled;
            GUI.enabled = TabletLink;
            try
            {
                foreach (var k in keys)
                {
                    if (string.IsNullOrEmpty(k)) continue;
                    float w = Mathf.Max(110, tabletFont * (k.Length * .7f + 2f));
                    if (x + w > area.xMax) { x = area.x; y += kh + 6; }
                    if (GUI.Button(new Rect(x, y, w, kh), k, tabletKey) && TabletLink)
                    {
                        OnKey("learn", k);
                        if (k == "SCROLL UP") tabletTestsScroll.y = Mathf.Max(0, tabletTestsScroll.y - kh * 2);
                        else if (k == "SCROLL DN") tabletTestsScroll.y += kh * 2;
                    }
                    x += w + 8;
                }
            }
            finally
            {
                GUI.enabled = savedEnabled;
            }

            TabletUi.Ensure(tabletFont);
            if (!TabletLink)
            {
                GUI.Label(new Rect(area.x, y + kh + 2, area.width, kh), "INFO ONLY - turn LIVE LINK on to use the controls", tabletText);
                y += kh + 6;
            }
            var ll = NativeFlightController.LearnLines();
            var codes = LearnRowCodes(ll);
            bool toggle = NativeFlightController.LearnToggleMode;
            PlaneProfile profile = NativeFlightController.LearnGraphProfile;
            float top = y + kh + 8;
            float cardW = Mathf.Max(0, area.width - 18);
            var graphs = new LearnTestGraph[LearnFlight.Codes.Length];
            for (int j = 0; j < graphs.Length; j++) graphs[j] = LearnTestGraphFor(profile, LearnFlight.Codes[j]);
            float contentHeight = 8;
            for (int i = 1; i < ll.Count; i++)
            {
                int j = codes[i];
                contentHeight += (j >= 0 && j < LearnFlight.Codes.Length && graphs[j] != null && graphs[j].Count > 1 ? 130 : j >= 0 ? 78 : 42) + 8;
            }
            Rect scrollArea = new Rect(area.x, top, area.width, Mathf.Max(0, area.yMax - top));
            tabletTestsScroll.y = Mathf.Min(tabletTestsScroll.y, Mathf.Max(0, contentHeight - scrollArea.height));
            Event current = Event.current;
            if (current.type == EventType.ScrollWheel && scrollArea.Contains(current.mousePosition))
            {
                tabletTestsScroll.y = Mathf.Clamp(tabletTestsScroll.y + current.delta.y * 20f, 0,
                    Mathf.Max(0, contentHeight - scrollArea.height));
                current.Use();
            }
            tabletTestsScroll = GUI.BeginScrollView(scrollArea, tabletTestsScroll,
                new Rect(0, 0, cardW, contentHeight), false, true);
            try
            {
                float cy = 8;
                for (int i = 1; i < ll.Count; i++)
                {
                    int j = codes[i];
                    string line = ll[i];
                    if (j < 0 || j >= LearnFlight.Codes.Length)
                    {
                        float h = 42;
                        GUI.Label(new Rect(6, cy, cardW - 12, h), line.Trim(), tabletText);
                        cy += h + 8;
                        continue;
                    }

                    int code = LearnFlight.Codes[j];
                    string chip;
                    Color chipColor;
                    string name = LearnRowParse(line, out chip, out chipColor);
                    LearnTestGraph graph = graphs[j];
                    bool hasGraph = graph != null && graph.Count > 1;
                    float hCard = hasGraph ? 130 : 78;
                    Rect row = new Rect(0, cy, cardW, hCard);
                    Rect inner = TabletUi.CardPanel(row, null);
                    float chipW = Mathf.Max(90, tabletFont * 5.5f);
                    TabletUi.Label(new Rect(inner.x + 2, inner.y, inner.width - chipW - 12, tabletFont * 1.8f), name);
                    TabletUi.Chip(new Rect(inner.xMax - chipW, inner.y + 2, chipW, tabletFont * 1.6f), chip, chipColor);
                    TabletUi.Label(new Rect(inner.x + 2, inner.y + tabletFont * 1.8f, inner.width - 4, tabletFont * 1.5f), LearnTestSummary(profile, code), true);
                    if (hasGraph)
                    {
                        TabletUi.Graph(new Rect(inner.x + 2, inner.y + tabletFont * 3.15f, inner.width - 4, 54),
                            new[] { graph.YLabel + " vs " + graph.XLabel }, new[] { graph.Y }, graph.Count, graph.Count, new[] { graph.Unit }, graph.X);
                    }
                    bool selected = toggle && j == MfdNav.LearnCursor;
                    if (selected) TabletUi.Rect(new Rect(row.x, row.y + 6, 4, row.height - 12), TabletUi.Accent);
                    if (toggle && TabletLink && GUI.Button(row, GUIContent.none, GUIStyle.none))
                    {
                        if (selected) OnKey("learn", "TOGGLE");
                        else MfdNav.LearnCursor = j;
                    }
                    cy += hCard + 8;
                }
            }
            finally
            {
                GUI.EndScrollView();
            }
        }
        static LearnTestGraph LearnTestGraphFor(PlaneProfile p, int code)
        {
            if (p == null) return null;
            switch (code)
            {
                case 2: return LearnTestGraph.FromPoints(p.Curves.Climb.Points, 0, 1, "Altitude (km)", "Climb rate", "m/s");
                case 3: return LearnTestGraph.FromPoints(p.Curves.Accel.Points, 0, 1, "Speed", "Acceleration", "m/s2");
                case 4: return LearnTestGraph.FromPoints(p.Curves.FuelBurn.Points, 0, 3, "Speed", "Fuel burn", "%/s");
                case 6: return LearnTestGraph.FromPoints(p.Curves.RollRate.Points, 0, 1, "Speed", "Roll rate", "deg/s");
                case 7: return LearnTestGraph.FromPoints(p.Envelope, 0, 1, "Speed", "Safe bank", "deg");
                case 8: return LearnTestGraph.FromPoints(p.Dives, 0, 1, "Dive pitch", "Sink rate", "m/s");
                case 9:
                case 91: return LearnTestGraph.FromPoints(LearnSinkPoints(p, code == 91), 1, 3, "Speed", "Sink rate", "m/s");
                case 10: return LearnTestGraph.FromPoints(LearnStallPoints(p, false), 0, 1, "Flap setting", "Stall speed", "m/s");
                case 101: return LearnTestGraph.FromPoints(LearnStallPoints(p, true), 0, 1, "Flap setting", "Stall speed", "m/s");
                default: return null;
            }
        }
        static List<double[]> LearnSinkPoints(PlaneProfile p, bool flapsFull)
        {
            var points = new List<double[]>();
            foreach (var point in p.Curves.Sink.Points)
                if (point.Length >= 5 && (point[2] >= 2) == flapsFull) points.Add(point);
            return points;
        }
        static List<double[]> LearnStallPoints(PlaneProfile p, bool flapsFull)
        {
            var points = new List<double[]>();
            foreach (var point in p.Curves.Stall.Points)
                if (point.Length >= 3 && (point[0] >= 2) == flapsFull) points.Add(point);
            return points;
        }
        static string LearnTestSummary(PlaneProfile p, int code)
        {
            if (p == null) return "No learned measurements yet.";
            switch (code)
            {
                case 1: return "Liftoff " + LearnMetric(p.Liftoff, "m/s") + "   Ground roll " + LearnMetric(p.Perf.GroundRoll, "m");
                case 2: return "Mean climb rate " + LearnMetric(p.ClimbVs, "m/s") + "   Climb time " + LearnMetric(p.Perf.ClimbTime, "s");
                case 3: return "Acceleration " + LearnMetric(p.Accel, "m/s2") + "   Idle deceleration " + LearnMetric(p.Decel, "m/s2");
                case 4: return "Cruise speed " + LearnMetric(p.Perf.CruiseSpeed, "m/s") + "   Fuel burn " + LearnMetric(p.Perf.CruiseFracPerS * 6000, "%/min");
                case 5: return "Max level speed " + LearnMetric(p.Perf.MaxLevel, "m/s");
                case 6: return "Roll rate " + LearnMetric(p.Perf.RollRate, "deg/s") + "   Pitch rate " + LearnMetric(p.Perf.PitchRate, "deg/s");
                case 7: return "Safe-turn envelope samples: " + p.Envelope.Count;
                case 8: return "Dive samples: " + p.Dives.Count;
                case 9: return "Sink-map samples: " + LearnSinkPoints(p, false).Count;
                case 91: return "Flaps-full sink-map samples: " + LearnSinkPoints(p, true).Count;
                case 10: return "Clean stall speed " + LearnMetric(p.Stall, "m/s") + "   Altitude loss " + LearnMetric(p.StallAltLoss, "m");
                case 101: return "Flaps-full stall speed " + LearnMetric(p.FlapStall, "m/s") + "   Altitude loss " + LearnMetric(p.FlapStallAltLoss, "m");
                case 11: return "Recovery time " + LearnMetric(p.StallT.RecoverS, "s") + "   Total altitude loss " + LearnMetric(p.StallT.TotalLoss, "m");
                case 12: return "Landing rollout " + LearnMetric(p.Perf.Rollout, "m");
                default: return "No learned measurements yet.";
            }
        }
        static string LearnMetric(double value, string unit)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? "-" : value.ToString("0.##", CultureInfo.InvariantCulture) + " " + unit;
        }
        /// <summary>"&gt;  [x] 9b Flaps-full sink map (done)" -> name "9b Flaps-full sink map", chip DONE / QUEUED / RUNNING / SETUP / OFF.</summary>
        internal static string LearnRowParse(string l, out string chip, out Color c)
        {
            int b = l.IndexOf('['); char m = b >= 0 && b + 1 < l.Length ? l[b + 1] : ' '; string name = b >= 0 && b + 4 <= l.Length ? l.Substring(b + 4).Trim() : l.Trim();
            bool done = name.EndsWith("(done)"), queued = name.EndsWith("(queued)"); if (done) name = name.Substring(0, name.Length - 6).Trim(); if (queued) name = name.Substring(0, name.Length - 8).Trim();
            if (m == '>') { chip = "RUNNING"; c = TabletUi.Accent; } else if (m == '*') { chip = "SETUP"; c = TabletUi.Dim; }
            else if (m == 'x') { chip = done ? "DONE" : queued ? "QUEUED" : "ON"; c = done ? TabletUi.Good : TabletUi.Warn; } else { chip = "OFF"; c = new Color(.4f, .43f, .47f); }
            return name;
        }

        // ---- Flight: reference template for the modern tablet pages (TabletUi). Info only; fine with LIVE LINK off. ----
        const int FlightHist = 120; static readonly float[] histAlt = new float[FlightHist], histSpd = new float[FlightHist]; static int histHead, histN; static float histNext;
        static readonly string[] FlightNames = { "Altitude", "Speed" }, FlightUnits = { "m", "m/s" }; static float[][] flightData;
        static void FlightSample()
        {
            if (Time.realtimeSinceStartup < histNext) return; histNext = Time.realtimeSinceStartup + 1f;   // 1 Hz, last ~2 min
            var v = FlightGlobals.ActiveVessel; if (v == null) return;
            histAlt[histHead] = (float)v.altitude; histSpd[histHead] = (float)v.srfSpeed; histHead = (histHead + 1) % FlightHist; histN = Math.Min(FlightHist, histN + 1);
        }
        static void Attitude(Vessel v, out double hdg, out double pitch, out double roll, out double aoa)
        {
            hdg = pitch = roll = aoa = 0; var t = v.ReferenceTransform; if (t == null) return;
            Vector3d up = v.upAxis, nose = t.up, north = Vector3d.Exclude(up, v.mainBody.transform.up).normalized, east = Vector3d.Cross(up, north);
            pitch = Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(nose, up)))) * 180 / Math.PI;
            hdg = (Math.Atan2(Vector3d.Dot(nose, east), Vector3d.Dot(nose, north)) * 180 / Math.PI + 360) % 360;
            roll = -Math.Atan2(Vector3d.Dot(t.right, up), Vector3d.Dot(-t.forward, up)) * 180 / Math.PI;
            Vector3d vel = v.srf_velocity; if (vel.magnitude > 1) aoa = Math.Atan2(Vector3d.Dot(vel, t.forward), Vector3d.Dot(vel, nose)) * 180 / Math.PI;
        }
        void TabletFlight(Rect area)
        {
            TabletUi.Ensure(tabletFont); var v = FlightGlobals.ActiveVessel;
            if (v == null) { TabletUi.Label(area, "No active vessel."); return; }
            double hdg, pitch, roll, aoa; Attitude(v, out hdg, out pitch, out roll, out aoa);
            float gap = 10, th = Mathf.Max(80, tabletFont * 4.4f), tw = (area.width - 3 * gap) / 4;
            // row 1: big tiles
            TabletUi.Tile(new Rect(area.x, area.y, tw, th), "SPEED (SRF)", v.srfSpeed.ToString("0"), "m/s", TabletUi.Series[1]);
            TabletUi.Tile(new Rect(area.x + (tw + gap), area.y, tw, th), "ALTITUDE", v.altitude.ToString("#,0"), "m", TabletUi.Series[0]);
            TabletUi.Tile(new Rect(area.x + 2 * (tw + gap), area.y, tw, th), "VERT SPEED", v.verticalSpeed.ToString("+0.0;-0.0;0"), "m/s", v.verticalSpeed < -15 ? TabletUi.Warn : TabletUi.Good);
            TabletUi.Tile(new Rect(area.x + 3 * (tw + gap), area.y, tw, th), "HEADING", hdg.ToString("000"), "deg", TabletUi.Accent);
            // row 2: attitude dials + throttle / AP card
            float y2 = area.y + th + gap, dh = Mathf.Max(130, tabletFont * 7.5f), dw = (area.width - 3 * gap) / 4;
            var ap = NativeFlightController.ApLine; bool apOn = !ap.StartsWith("AP OFF");
            Rect c1 = TabletUi.CardPanel(new Rect(area.x, y2, dw * 2 + gap, dh), "Attitude"); float sub = (c1.width - 2 * gap) / 3;
            TabletUi.Dial(new Rect(c1.x, c1.y, sub, c1.height), "PITCH " + pitch.ToString("0.0"), (float)((pitch + 90) / 180), pitch.ToString("0") + "\u00B0", TabletUi.Accent);
            TabletUi.Dial(new Rect(c1.x + sub + gap, c1.y, sub, c1.height), "BANK " + roll.ToString("0"), (float)((roll + 180) / 360), roll.ToString("0") + "\u00B0", Math.Abs(roll) > 45 ? TabletUi.Warn : TabletUi.Good);
            TabletUi.Dial(new Rect(c1.x + 2 * (sub + gap), c1.y, sub, c1.height), "AoA " + aoa.ToString("0.0"), (float)((aoa + 10) / 40), aoa.ToString("0.0") + "\u00B0", aoa > 15 ? TabletUi.Bad : TabletUi.Series[3]);
            Rect c2 = TabletUi.CardPanel(new Rect(area.x + 2 * (dw + gap), y2, dw * 2 + gap, dh), "Engines / Autopilot");
            float thr = v.ctrlState != null ? v.ctrlState.mainThrottle : 0, bh = Mathf.Max(30, tabletFont * 2.4f);
            TabletUi.Bar(new Rect(c2.x, c2.y, c2.width, bh), "Throttle", thr, (thr * 100).ToString("0") + " %", TabletUi.Series[1]);
            TabletUi.Bar(new Rect(c2.x, c2.y + bh + 6, c2.width, bh), "Mach", (float)(v.mach / 3), v.mach.ToString("0.00"), TabletUi.Series[2]);
            TabletUi.Chip(new Rect(c2.x, c2.yMax - Mathf.Max(28, tabletFont * 1.8f), c2.width, Mathf.Max(28, tabletFont * 1.8f)), ap, apOn ? TabletUi.Good : TabletUi.Warn);
            // row 3: live graph
            float y3 = y2 + dh + gap; if (area.yMax - y3 < 80) return;
            Rect c3 = TabletUi.CardPanel(new Rect(area.x, y3, area.width, area.yMax - y3), "Last 2 minutes");
            if (flightData == null) flightData = new[] { histAlt, histSpd };
            TabletUi.Graph(c3, FlightNames, flightData, histN, histHead, FlightUnits);
        }
        // ---- Encyclopedia: our own short facts read live from CelestialBody (info only; works with LIVE LINK off) ----
        static Vector2 encListScroll, encInfoScroll; static string encSel;
        internal static string BodyClass(bool hasParent, bool parentIsStar, double radius)
        {
            if (!hasParent) return "Stars";
            if (parentIsStar) return radius < 100000 ? "Asteroids" : "Planets";
            return "Moons";
        }
        static bool IsStar(CelestialBody b) { return b.referenceBody == null || b.referenceBody == b; }
        void TabletEncyclopedia(Rect area)
        {
            var groups = new Dictionary<string, List<string>> { { "Stars", new List<string>() }, { "Planets", new List<string>() }, { "Moons", new List<string>() }, { "Asteroids", new List<string>() } };
            var bodies = FlightGlobals.Bodies ?? new List<CelestialBody>();
            foreach (var b in bodies) { if (b == null) continue; groups[BodyClass(!IsStar(b), !IsStar(b) && IsStar(b.referenceBody), b.Radius)].Add(b.bodyName); }
            var objs = new List<Vessel>(); if (FlightGlobals.Vessels != null) foreach (var v in FlightGlobals.Vessels) if (v != null && v.vesselType == VesselType.SpaceObject) objs.Add(v);
            float lw = Mathf.Min(area.width * .35f, Mathf.Max(200, tabletFont * 12)), rh = Mathf.Max(28, tabletFont * 1.7f);
            var head = new GUIStyle(tabletHead) { fontSize = tabletFont }; var item = new GUIStyle(tabletText) { wordWrap = false, alignment = TextAnchor.MiddleLeft };
            GUILayout.BeginArea(new Rect(area.x, area.y, lw, area.height));
            try
            {
                encListScroll = GUILayout.BeginScrollView(encListScroll);
                try
                {
                    foreach (var g in new[] { "Stars", "Planets", "Moons", "Asteroids" })
                    {
                        GUILayout.Label(g.ToUpperInvariant(), head);
                        foreach (var n in groups[g]) if (GUILayout.Button((encSel == n ? "\u25B8 " : "  ") + n, item, GUILayout.Height(rh))) { encSel = n; encInfoScroll = Vector2.zero; }
                        if (g == "Asteroids") foreach (var v in objs) { string k = "#" + v.id; if (GUILayout.Button((encSel == k ? "\u25B8 " : "  ") + v.vesselName, item, GUILayout.Height(rh))) { encSel = k; encInfoScroll = Vector2.zero; } }
                        if (g == "Asteroids" && groups[g].Count == 0 && objs.Count == 0) GUILayout.Label("  (none tracked)", tabletText);
                    }
                }
                finally
                {
                    GUILayout.EndScrollView();
                }
            }
            finally
            {
                GUILayout.EndArea();
            }
            var o = GUI.color; GUI.color = new Color(.35f, 1f, .45f, .4f); GUI.DrawTexture(new Rect(area.x + lw + 6, area.y, 1, area.height), Texture2D.whiteTexture); GUI.color = o;
            var lines = new List<string>();
            if (encSel == null) lines.Add("Select a body on the left.");
            else if (encSel.StartsWith("#")) { var v = objs.Find(x => "#" + x.id == encSel); if (v == null) lines.Add("Object no longer tracked."); else { lines.Add(v.vesselName); lines.Add("Tracked space object (asteroid / comet)"); OrbitLines(lines, v.orbit); } }
            else { var b = bodies.Find(x => x != null && x.bodyName == encSel); if (b == null) lines.Add("Body not found."); else BodyLines(lines, b); }
            GUILayout.BeginArea(new Rect(area.x + lw + 16, area.y, area.width - lw - 16, area.height));
            try
            {
                encInfoScroll = GUILayout.BeginScrollView(encInfoScroll);
                try
                {
                    TabletPlanetView(area.width - lw - 42, bodies.Find(x => x != null && x.bodyName == encSel));
                    for (int i = 0; i < lines.Count; i++) GUILayout.Label(lines[i], i == 0 ? tabletHead : tabletText);
                }
                finally
                {
                    GUILayout.EndScrollView();
                }
            }
            finally
            {
                GUILayout.EndArea();
            }
        }
        internal static string EncDist(double m) { if (double.IsInfinity(m) || double.IsNaN(m)) return "-"; return Math.Abs(m) >= 1e9 ? (m / 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " Gm" : Math.Abs(m) >= 1e4 ? (m / 1000).ToString("#,0.#", CultureInfo.InvariantCulture) + " km" : m.ToString("0", CultureInfo.InvariantCulture) + " m"; }
        internal static string EncDur(double s)
        {
            if (double.IsInfinity(s) || double.IsNaN(s) || s <= 0) return "-";
            double d = Math.Floor(s / 21600), h = Math.Floor((s - d * 21600) / 3600), mi = Math.Floor((s % 3600) / 60);   // Kerbin days (6 h)
            return (d > 0 ? d + "d " : "") + (h > 0 || d > 0 ? h + "h " : "") + mi + "m " + Math.Floor(s % 60) + "s";
        }
        static void BodyLines(List<string> o, CelestialBody b)
        {
            var ci = CultureInfo.InvariantCulture; bool star = IsStar(b);
            o.Add(b.bodyName); o.Add(BodyClass(!star, !star && IsStar(b.referenceBody), b.Radius).TrimEnd('s').Replace("Asteroid", "Small body"));
            o.Add("Radius: " + EncDist(b.Radius));
            o.Add("Mass: " + b.Mass.ToString("0.###e+0", ci) + " kg");
            o.Add("Surface gravity: " + b.GeeASL.ToString("0.###", ci) + " g");
            o.Add("SOI: " + (star || double.IsInfinity(b.sphereOfInfluence) ? "infinite" : EncDist(b.sphereOfInfluence)));
            double peakAltitude;
            bool hasPeak = TryMappedPeakAltitude(b, out peakAltitude);
            o.Add(hasPeak ? "Mapped terrain peak: " + EncDist(peakAltitude) : "Mapped terrain peak: unavailable");
            o.Add(BiomeLine(b));
            if (!b.hasSolidSurface)
                o.Add("Landing Δv: not applicable (no solid surface)");
            else if (hasPeak)
            {
                double landingDv = EncyclopediaMath.IdealLandingDeltaV(b.gravParameter, b.Radius, peakAltitude, 1000);
                o.Add(double.IsNaN(landingDv) || double.IsInfinity(landingDv)
                    ? "Landing Δv: unavailable"
                    : "Landing Δv baseline (circular orbit 1 km above peak; vacuum, no rotation): " +
                        landingDv.ToString("#,0", ci) + " m/s");
            }
            else o.Add("Landing Δv: unavailable (terrain peak unknown)");
            o.Add("Day (sidereal): " + EncDur(b.rotationPeriod) + (b.tidallyLocked ? "  - tidally locked" : ""));
            if (b.atmosphere)
            {
                o.Add("Atmosphere: yes, up to " + EncDist(b.atmosphereDepth));
                o.Add("Sea-level pressure: " + b.atmospherePressureSeaLevel.ToString("0.###", ci) + " kPa (" + (b.atmospherePressureSeaLevel / 101.325).ToString("0.###", ci) + " atm)");
                o.Add("Oxygen: " + (b.atmosphereContainsOxygen ? "yes (jets / air intakes work)" : "no"));
            }
            else o.Add("Atmosphere: none");
            if (!star) OrbitLines(o, b.orbit);
        }
        static string BiomeLine(CelestialBody body)
        {
            if (body == null || body.BiomeMap == null || body.BiomeMap.Attributes == null)
                return "Biomes: unavailable";
            var names = new List<string>();
            foreach (var attribute in body.BiomeMap.Attributes)
                if (attribute != null && !string.IsNullOrEmpty(attribute.name)) names.Add(attribute.name);
            return names.Count == 0 ? "Biomes: unavailable" : "Biomes: " + string.Join(", ", names.ToArray());
        }
        static bool TryMappedPeakAltitude(CelestialBody body, out double altitude)
        {
            altitude = double.NaN;
            if (body == null || body.pqsController == null) return false;
            double mappedPeak = body.pqsController.mapMaxHeight;
            if (double.IsNaN(mappedPeak) || double.IsInfinity(mappedPeak) || mappedPeak < 0) return false;
            altitude = mappedPeak;
            return true;
        }
        static void OrbitLines(List<string> o, Orbit orb)
        {
            if (orb == null) { o.Add("Orbit: -"); return; }
            var ci = CultureInfo.InvariantCulture;
            o.Add("");
            o.Add("Orbits: " + (orb.referenceBody != null ? orb.referenceBody.bodyName : "-"));
            o.Add("Semi-major axis: " + EncDist(orb.semiMajorAxis));
            o.Add("Period: " + EncDur(orb.period));
            o.Add("Inclination: " + orb.inclination.ToString("0.##", ci) + " deg");
            o.Add("Eccentricity: " + orb.eccentricity.ToString("0.####", ci));
        }
        static string TabletSave()
        {
            return string.Format(CultureInfo.InvariantCulture, "tx={0}\nty={1}\ntw={2}\nth={3}\ntvis={4}\nttab={5}\ntfont={6}\ntmap={7}\ntlink={8}\ntfl={9}\n",
                tabletRect.x, tabletRect.y, tabletRect.width, tabletRect.height, TabletVisible ? 1 : 0, tabletTab, tabletFont, tabletMapPage, TabletLink ? 1 : 0, tabletFlightPage);
        }
        static bool TabletLoad(string key, string val)
        {
            float f; float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
            switch (key)
            {
                case "tx": tabletRect.x = f; return true;
                case "ty": tabletRect.y = f; return true;
                case "tw": tabletRect.width = Mathf.Max(TabletMinW, f); return true;
                case "th": tabletRect.height = Mathf.Max(TabletMinH, f); return true;
                case "tvis": TabletVisible = val.Trim() == "1"; return true;
                case "ttab": tabletTab = Mathf.Clamp((int)f, 0, TabletTabs.Length - 1); return true;
                case "tfont": tabletFont = Mathf.Clamp((int)f, 12, 32); return true;
                case "tlink": TabletLink = val.Trim() == "1"; return true;
                case "tmap": tabletMapPage = Mathf.Clamp((int)f, 0, 2); return true;
                case "tfl": tabletFlightPage = Mathf.Clamp((int)f, 0, TabletFlightPages.Length - 1); return true;
            }
            return false;
        }
    }
}
