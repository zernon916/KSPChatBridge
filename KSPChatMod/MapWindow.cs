using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>Per-save fog-of-war tiles for the AICS map.</summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public class AicsMapScenario : ScenarioModule
    {
        public override void OnLoad(ConfigNode node)
        {
            MapReveal.Clear();
            foreach (ConfigNode n in node.GetNodes("MAP")) MapReveal.Load(n.GetValue("body"), n.GetValue("tiles"));
        }
        public override void OnSave(ConfigNode node)
        {
            foreach (var kv in MapReveal.Serialize()) { var n = node.AddNode("MAP"); n.AddValue("body", kv.Key); n.AddValue("tiles", kv.Value); }
        }
    }

    /// <summary>AICS map / approach chart window: terrain under fog, airports always shown, craft, active route, editable charts.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapWindow : MonoBehaviour
    {
        const int WindowId = 0x4B434D41, N = 96; static float Px = 384;   // map side: scales with the window (resizable)
        internal static bool MapVisible; internal static int Tab; static readonly string[] Tabs = { "Maps", "Charts", "ILS" };
        static Rect rect = new Rect(420, 120, Px + 20, Px + 210);
        internal static double ViewSpan = 30000;
        static double Zoom(double span, double factor) { return MapReveal.Zoom(span, factor); }
        static int rwIdx; static bool centerRunway, follow = true; static double fixLat, fixLon;
        Texture2D tex, texGrey; Color[] px = new Color[N * N], pxGrey = new Color[N * N]; int buildRow = -1; double bLat, bLon, bSpan; float nextRebuild;
        List<NativeFlightController.MapRunway> runways = new List<NativeFlightController.MapRunway>();
        List<ApproachFile.EditFix> fixes = new List<ApproachFile.EditFix>(); string fixesFor = ""; int fixesVer = -1; int sel = -1; bool dragging; string note = "";
        double cLat, cLon, span; CelestialBody body;

        internal static void ToggleMap() { AicsMenu.ShowMfdItem("map"); }
        internal static MapWindow Inst;
        void Awake() { Inst = this; }
        void OnDestroy() { if (Inst == this) Inst = null; MapVisible = false; }
        List<TaxiRoute> taxiRoutes = new List<TaxiRoute>(); float taxiAt;

        // ---- MFD soft-key actions (the AICS MFD hosts this page; no window of its own any more) ----
        internal static void ZoomIn() { ViewSpan = Zoom(ViewSpan, .5); }
        internal static void ZoomOut() { ViewSpan = Zoom(ViewSpan, 2); }
        internal static void Recenter() { follow = true; centerRunway = false; }
        internal static void ToggleCenterRunway() { centerRunway = !centerRunway; follow = true; }
        internal static void ToggleGuidance() { guidance = !guidance; }
        internal static void ToggleDetails() { details = !details; }
        internal static void PanView(double bearing) { var m = Inst; if (m == null || m.body == null) return; if (follow) { fixLat = m.cLat; fixLon = m.cLon; follow = false; } MapReveal.Pan(ref fixLat, ref fixLon, bearing, m.span, m.body.Radius); }
        internal static void RunwayStep(int d) { var m = Inst; if (m == null || m.runways.Count == 0) return; rwIdx = (rwIdx + d + m.runways.Count) % m.runways.Count; m.fixesFor = ""; }
        /// <summary>GROK LAND2: chart variants of the CHART page's runway end (drop-down) and the active one.</summary>
        internal static List<string> VariantList(out string key, out string cur) { key = cur = ""; var m = Inst; var rw = m == null ? null : m.Rw(); if (rw == null) return new List<string>(); NativeFlightController.EnsureCharts(); var vs = ChartStore.Variants(rw.Key); if (!vs.Contains("")) vs.Insert(0, ""); key = rw.Key; cur = ChartStore.Active(rw.Key); return vs; }
        internal static void SetVariant(string variant) { var m = Inst; var rw = m == null ? null : m.Rw(); if (rw == null) return; ChartStore.SetActive(rw.Key, variant ?? ""); m.fixesFor = ""; ChatLog.Write("approach", "chart page: " + rw.Key + " -> " + ChartStore.Label(rw.Key, variant)); }
        internal static void NextVariant() { var m = Inst; var rw = m == null ? null : m.Rw(); if (rw == null) return; NativeFlightController.EnsureCharts(); var vs = ChartStore.Variants(rw.Key); if (!vs.Contains("")) vs.Insert(0, ""); string cur = ChartStore.Active(rw.Key); ChartStore.SetActive(rw.Key, vs[(vs.IndexOf(cur) + 1 + vs.Count) % vs.Count]); m.fixesFor = ""; }
        internal static void AddWaypoint() { if (Inst != null) Inst.AddWp(); }
        internal static void RemoveWaypoint() { var m = Inst; if (m == null) return; if (m.sel >= 0 && m.sel < m.fixes.Count && m.fixes[m.sel].Role != "faf" && m.fixes[m.sel].Role != "sf") { if (m.added.Remove(m.fixes[m.sel])) { m.fixes.RemoveAt(m.sel); m.Publish(); } else m.fixes.RemoveAt(m.sel); m.sel = -1; } else m.note = "Select a waypoint (FAF/SF are locked)."; }
        internal static void SelectWp(int d) { var m = Inst; if (m == null || m.fixes.Count == 0) return; m.sel = m.sel < 0 ? (d > 0 ? 0 : m.fixes.Count - 1) : (m.sel + d + m.fixes.Count) % m.fixes.Count; }
        internal static void AltStep(double dm) { var m = Inst; if (m != null && m.sel >= 0 && m.sel < m.fixes.Count) m.fixes[m.sel].AltMsl += dm; }
        internal static void ToggleRef() { var m = Inst; if (m != null && m.sel >= 0 && m.sel < m.fixes.Count) { var f = m.fixes[m.sel]; f.Ref = f.Ref == "agl" ? "msl" : "agl"; } }
        internal static void ZoomCycle() { ViewSpan = ViewSpan >= 40000 ? 5000 : ViewSpan * 2; }
        internal static void ResetChart() { var m = Inst; if (m != null) { m.fixesFor = ""; m.note = "Reset to computed (SAVE to keep)."; } }
        internal static void SaveChart() { var m = Inst; var rw = m == null ? null : m.Rw(); if (rw != null) m.Save(rw); }
        internal static string Title { get { var m = Inst; var rw = m == null ? null : m.Rw(); var act = NativeFlightController.ActiveRunway; return act != null && !string.IsNullOrEmpty(act.Key) ? act.Key : rw != null ? rw.Key : ""; } }
        NativeFlightController.MapRunway Rw() { return runways.Count > 0 ? runways[Math.Min(rwIdx, runways.Count - 1)] : null; }
        /// <summary>Draw the MAP / CHART / ILS page inside the outside MFD screen (IMGUI: the map mouse works here).</summary>
        internal void DrawEmbedded(Rect area, int tab)
        {
            Tab = tab;
            GUILayout.BeginArea(area);
            try { if (body == null) GUILayout.Label("No map in this scene."); else DrawPages(GuiCanvas.Shared, new Rect(0, 0, area.width, area.height), tab, () => HandleMouse(Rw())); } catch (Exception ex) { GuiGuard.Log("MapWindow.Embedded", ex); }
            GUILayout.EndArea();
        }
        /// <summary>Draw the same MAP / CHART / ILS page into an IVA screen's canvas (RtCanvas): identical graphics, no mouse and no
        /// window chrome - the bezel soft keys drive it, and the caller clips the page to the screen rect. item: MfdNav item id.</summary>
        internal void DrawIva(IMfdCanvas c, Rect area, string item)
        {
            try { if (body != null) DrawPages(c, area, item == "chart" ? 1 : item == "ils" ? 2 : 0, null); }
            catch (Exception ex) { GuiGuard.Log("MapWindow.Iva", ex); }
        }

        void Update()
        {
            var v = FlightGlobals.ActiveVessel; if (v == null) return;
            if (!ScanSatLink.Installed && v.situation != Vessel.Situations.PRELAUNCH)
                MapReveal.Fly(v.mainBody.bodyName, v.latitude, v.longitude, Math.Max(0, v.altitude - Math.Max(0, v.terrainAltitude)), v.mainBody.Radius);
            if (!MapVisible && !AicsIvaMfd.WantsMap) return;   // the IVA screens keep the map bitmap alive while the outside MFD is hidden
            body = v.mainBody;
            if (runways.Count == 0 || Time.realtimeSinceStartup > nextRebuild) runways = NativeFlightController.MapRunways(body);
            var act = NativeFlightController.ActiveRunway;
            if (act != null && !string.IsNullOrEmpty(act.Key)) { int i = runways.FindIndex(r => r.Key == act.Key); if (i >= 0) rwIdx = i; }
            if (rwIdx >= runways.Count) rwIdx = 0;
            var rw = runways.Count > 0 ? runways[rwIdx] : null;
            if (rw != null && (fixesFor != rw.Key || (fixesVer != ChartDesign.Version && added.Count == 0))) LoadFixes(rw);   // Oct 12: per-plane chart -> redraw on profile/vessel change (keeps unsaved added WPs)
            span = ViewSpan;
            if (!follow) { cLat = fixLat; cLon = fixLon; } else if (centerRunway && rw != null) { cLat = rw.Lat; cLon = rw.Lon; } else { cLat = v.latitude; cLon = v.longitude; }
            double moved = NavigationMath.Distance(cLat, cLon, bLat, bLon, body.Radius);
            if (buildRow < 0 && (bSpan != span || moved > span * .15 || Time.realtimeSinceStartup > nextRebuild)) { bLat = cLat; bLon = cLon; bSpan = span; buildRow = 0; nextRebuild = Time.realtimeSinceStartup + 15; }
            if (buildRow >= 0) BuildRows(12);
        }

        void BuildRows(int rows)
        {
            if (tex == null) { tex = new Texture2D(N, N, TextureFormat.RGB24, false); tex.filterMode = FilterMode.Bilinear; }
            if (texGrey == null) { texGrey = new Texture2D(N, N, TextureFormat.RGB24, false); texGrey.filterMode = FilterMode.Bilinear; }
            var extra = NativeFlightController.SpotPositions(body);
            Func<double, double, bool> cov = null; if (ScanSatLink.Installed) cov = (la, lo) => ScanSatLink.IsCovered(la, lo, body);
            double m = bSpan / N, cosL = Math.Cos(bLat * Math.PI / 180);   // bSpan metres across N texels: one bitmap serves every screen size
            for (int k = 0; k < rows && buildRow < N; k++, buildRow++)
                for (int x = 0; x < N; x++)
                {
                    double la = bLat + (N / 2 - (buildRow + .5)) * m / body.Radius * 180 / Math.PI;
                    double lo = bLon + ((x + .5) - N / 2) * m / (body.Radius * cosL) * 180 / Math.PI;
                    Color c;
                    if (!MapReveal.Revealed(body.bodyName, la, lo, body.Radius, cov, extra)) c = new Color(.45f, .45f, .47f);
                    else { double h = NativeFlightController.MapTerrain(body, la, lo); c = HeightColor(double.IsNaN(h) ? 0 : h); }
                    px[(N - 1 - buildRow) * N + x] = c;
                }
            if (buildRow >= N)
            {   // both looks every cycle (RPM-style desaturated MAP + colour CHART), so any screen can draw any page at any time
                for (int i = 0; i < px.Length; i++) { float g = px[i].grayscale * .75f + .08f; pxGrey[i] = new Color(g, g, g * 1.04f); }
                tex.SetPixels(px); tex.Apply(); texGrey.SetPixels(pxGrey); texGrey.Apply(); buildRow = -1;
            }
        }
        /// <summary>The bitmap a page draws: MAP desaturated, CHART colour (the ILS page draws no terrain).</summary>
        internal Texture2D TexFor(int tab) { return tab == 0 ? texGrey : tex; }
        /// <summary>A map bitmap is ready: the IVA screens then draw the real MAP / CHART / ILS graphics instead of a text page.</summary>
        internal static bool HasMap { get { var m = Inst; return m != null && m.body != null && m.tex != null && m.texGrey != null; } }

        internal static Color HeightColor(double h)
        {
            if (h <= 0) return new Color(.59f, .75f, .9f);
            float t = (float)Math.Min(1, h / 1500); Color lo = new Color(.47f, .67f, .35f), mid = new Color(.78f, .67f, .43f), hi = new Color(.92f, .92f, .92f);
            var c = t < .5f ? Color.Lerp(lo, mid, t * 2) : Color.Lerp(mid, hi, (t - .5f) * 2);
            return ((int)(h / 100)) % 2 == 0 ? c : c * .92f;
        }

        void Unproj(double la0, double lo0, double sp, float x, float y, out double lat, out double lon)
        {
            double m = sp / Px, R = body.Radius;
            lat = la0 + (Px / 2 - y) * m / R * 180 / Math.PI;
            lon = lo0 + (x - Px / 2) * m / (R * Math.Cos(la0 * Math.PI / 180)) * 180 / Math.PI;
        }
        Vector2 Proj(double lat, double lon)
        {
            double m = span / Px, R = body.Radius;
            return new Vector2((float)(Px / 2 + (lon - cLon) * Math.PI / 180 * R * Math.Cos(cLat * Math.PI / 180) / m), (float)(Px / 2 - (lat - cLat) * Math.PI / 180 * R / m));
        }

        List<AirportDiagram> diagrams; float diagAt = -100; string kkNote = "";
        /// <summary>GROK CHART1: FAA-style airport diagram for the field nearest the map centre (north-up; runway bar + end numbers,
        /// length x width, elevations, building blocks, lat/lon ticks). The plane dot is drawn on top by the caller.</summary>
        void DrawDiagram(IMfdCanvas c)
        {
            if (Time.realtimeSinceStartup > diagAt)
            {
                diagAt = Time.realtimeSinceStartup + 10; var b = new List<KeyValuePair<string, double[]>>();
                if (body.bodyName == "Kerbin") { if (KscRoverMap.Points.Count == 0) KscRoverMap.Refresh(body); foreach (var p in KscRoverMap.Points) b.Add(new KeyValuePair<string, double[]>(p.Name, new[] { p.Lat, p.Lon })); }
                diagrams = body.bodyName == "Kerbin" ? AirportDiagram.BuiltIn(b) : new List<AirportDiagram>();
                string nt; var kk = AirportDiagram.FromKerbalKonstructs(body.Radius, out nt); diagrams.AddRange(kk);
                if (nt != kkNote) { kkNote = nt; ChatLog.Write("chart", "airport diagrams: " + diagrams.Count + " field(s); " + nt); }
            }
            var d = AirportDiagram.Nearest(diagrams, cLat, cLon, body.Radius, span); if (d == null) return;
            double mpp = span / Px, R = body.Radius; var ink = new Color(.1f, .1f, .1f);
            c.Fill(new Rect(0, 0, Px, Px), new Color(.96f, .96f, .93f, .82f));   // paper
            foreach (var bl in d.Blocks)
            {
                var p = Proj(bl.Lat, bl.Lon); float s = Mathf.Max(4, (float)(bl.SizeM / mpp));
                c.Fill(new Rect(p.x - s / 2, p.y - s / 2, s, s), new Color(.35f, .35f, .38f));
                if (span <= 5000) Tag(c, new Rect(p.x + s / 2 + 2, p.y - 9, 130, 18), bl.Name);
            }
            int line = 0;
            Tag(c, new Rect(6, 6, 360, 18), d.Name.ToUpperInvariant() + "  AIRPORT DIAGRAM   FIELD ELEV " + Math.Round(d.ElevM) + " m"); line++;
            foreach (var r in d.Runways)
            {
                Vector2 a = Proj(r.Lat1, r.Lon1), e = Proj(r.Lat2, r.Lon2); int th = Mathf.Clamp((int)(r.WidthM / mpp), 4, 40);
                MfdInk.Line(c, a, e, Color.black, th);
                double h12 = NavigationMath.Bearing(r.Lat1, r.Lon1, r.Lat2, r.Lon2); Vector2 dir = (a - e).normalized * (th + 12);
                Tag(c, new Rect(a.x + dir.x - 12, a.y + dir.y - 9, 30, 18), AirportDiagram.EndNumber(h12));     // painted at end 1: landing from end 1 toward end 2
                Tag(c, new Rect(e.x - dir.x - 12, e.y - dir.y - 9, 30, 18), AirportDiagram.EndNumber(h12 + 180));
                Tag(c, new Rect(6, 6 + 20 * line++, 360, 18), AirportDiagram.RunwayText(r, R));
            }
            double step = AirportDiagram.TickStep(span, R), half = span / 2 / (Math.PI / 180 * R), halfLon = half / Math.Cos(cLat * Math.PI / 180);
            for (double la = Math.Ceiling((cLat - half) / step) * step; la <= cLat + half; la += step)
            { var p = Proj(la, cLon); MfdInk.Line(c, new Vector2(0, p.y), new Vector2(10, p.y), ink, 2); Tag(c, new Rect(12, p.y - 9, 80, 18), Math.Abs(la).ToString("0.000") + (la < 0 ? "S" : "N")); }
            for (double lo = Math.Ceiling((cLon - halfLon) / step) * step; lo <= cLon + halfLon; lo += step)
            { var p = Proj(cLat, lo); MfdInk.Line(c, new Vector2(p.x, Px), new Vector2(p.x, Px - 10), ink, 2); Tag(c, new Rect(p.x - 30, Px - 30, 80, 18), Math.Abs(lo).ToString("0.000") + (lo < 0 ? "W" : "E")); }
        }
        void LoadFixes(NativeFlightController.MapRunway rw)
        {
            fixesFor = rw.Key; fixesVer = ChartDesign.Version; sel = -1; fixes.Clear(); note = "";
            double crs = NavigationMath.Bearing(rw.Lat, rw.Lon, rw.EndLat, rw.EndLon);
            var ch = ApproachChart.Build(rw.Lat, rw.Lon, crs, rw.Elevation, 150, 20, body.Radius, (la, lo) => NativeFlightController.MapTerrain(body, la, lo));
            string why; var ov = NativeFlightController.LoadOverride(rw.Key, rw, body, out why);
            if (ov != null && ov.Fixes.Count > 0)
            {
                foreach (var f in ov.Fixes) fixes.Add(new ApproachFile.EditFix { Role = f.Role, Name = f.Name, Side = f.Side, Ref = f.AltRef, Lat = f.Lat, Lon = f.Lon, AltMsl = f.Alt });
                note = "Chart from charts/" + System.IO.Path.GetFileName(ChartStore.PathFor(rw.Key));
            }
            else
            {
                Action<string, string, ApproachChart.Wp> add = (role, side, w) => fixes.Add(new ApproachFile.EditFix { Role = role, Name = w.Name, Side = side, Lat = w.Lat, Lon = w.Lon, AltMsl = w.Alt });
                add("dw_left", "left", ch.DownLeftA); add("dwend_left", "left", ch.DownLeftB); add("base_left", "left", ch.ApexLeft);
                add("dw_right", "right", ch.DownRightA); add("dwend_right", "right", ch.DownRightB); add("base_right", "right", ch.ApexRight);
                add("faf", "both", ch.Long); add("sf", "both", ch.Short);
                note = why.Length > 0 ? "chart file invalid (" + why + "); computed chart" : "Computed chart (not saved yet)";
            }
        }

        void OnGUI() { try { OnGUIInner(); } catch (System.Exception ex) { GuiGuard.Log(GetType().Name, ex); } }

        object autoIlsFor;
        /// <summary>Autoland only: switch to the ILS tab once per approach when entering localizer capture range; a manual tab change afterwards wins.</summary>
        void AutoIlsTab()
        {
            var act = NativeFlightController.ActiveRunway; var v = FlightGlobals.ActiveVessel;
            if (act == null || v == null || ReferenceEquals(autoIlsFor, act)) return;
            var r = Ils.Compute(v.latitude, v.longitude, v.altitude, act.Lat, act.Lon, act.EndLat, act.EndLon, act.Elevation, v.mainBody.Radius, act.TouchdownM, act.FinalSlope);
            if (Ils.InLocCaptureZone(r, RunwayMission.LocRange)) { autoIlsFor = act; Tab = 2; AicsMenu.AutoIls(); }
        }
        static bool details, guidance;
        /// <summary>Map/ILS display side for a window size: fills the width, leaves room for the rows below (pure; tested).</summary>
        void OnGUIInner()
        {
            if (!MapVisible || body == null) return;
            AutoIlsTab();
            // drawn inside the AICS MFD (AicsMfd.cs): no window of its own
        }

        /// <summary>MAP / CHART: the map square fills the page edge to edge (a square covering the area, clipped); every text overlay
        /// sits on a dark box. No on-screen buttons: each action is a bezel soft key (AicsMfd); waypoints are selected by clicking
        /// the map (outside window only) or with WP &lt; / WP &gt;. Drawn through IMfdCanvas, so the outside MFD (GuiCanvas) and
        /// every IVA screen (RtCanvas, into its RenderTexture) render this one pass, each at its own size. tab: 0 MAP, 1 CHART,
        /// 2 ILS. mapMouse: the IMGUI map mouse (pan / waypoint drag / wheel zoom), called inside the map square; null on the IVA
        /// screens, where the bezel soft keys do everything.</summary>
        void DrawPages(IMfdCanvas c, Rect area, int tab, Action mapMouse)
        {
            if (body == null) return;   // the page is the tab argument; the static Tab stays the outside window's own (DrawEmbedded sets it)
            if (tab == 2) { DrawIls(c, area); return; }
            var rw = Rw();
            Px = Mathf.Max(80, Mathf.Max(area.width, area.height));
            tagPx = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(area.width, area.height) / 34), 9, 12);
            Rect map = new Rect((area.width - Px) / 2, (area.height - Px) / 2, Px, Px);
            float th = Mathf.Max(16, area.height * .06f);
            c.Fill(area, new Color(.45f, .45f, .47f));
            if (TexFor(tab) != null) c.Image(map, TexFor(tab));
            c.Group(map, () =>
            {
                foreach (var r in runways) MfdInk.Line(c, Proj(r.Lat, r.Lon), Proj(r.EndLat, r.EndLon), Color.black, 1);
                if (tab == 1 && span <= AirportDiagram.ShowBelowSpan) DrawDiagram(c);   // GROK CHART1: zoomed in near a field -> airport diagram layer in place
                if (Time.realtimeSinceStartup > taxiAt) { taxiAt = Time.realtimeSinceStartup + 5; taxiRoutes = body.bodyName == "Kerbin" ? NativeFlightController.TaxiRoutes() : new List<TaxiRoute>(); }
                foreach (var tr in taxiRoutes)
                {   // ground charts (PluginData/charts/TAXI_*.json): amber dotted centerlines
                    for (int i = 1; i < tr.Points.Count; i++) MfdInk.Line(c, Proj(tr.Points[i - 1].Lat, tr.Points[i - 1].Lon), Proj(tr.Points[i].Lat, tr.Points[i].Lon), new Color(1, .75f, .2f), 3);
                    if (span <= 8000) foreach (var p in tr.Points) { var q = Proj(p.Lat, p.Lon); if (q.x > 0 && q.y > 0 && q.x < Px && q.y < Px) Tag(c, new Rect(q.x + 4, q.y + 2, 110, 18), p.Name); }
                }
                foreach (var a in MapReveal.Airports) if (body.bodyName == "Kerbin") { var p = Proj(a.Lat, a.Lon); if (p.x > 0 && p.y > 0 && p.x < Px && p.y < Px) Tag(c, new Rect(p.x + 4, p.y - 8, 140, 18), a.Pad ? "▲ " + a.Name : a.Name); }
                var v = FlightGlobals.ActiveVessel;
                bool kscRover = tab == 0 && IsKscRover(v);
                if (kscRover) DrawKscRoverMap(c, v);
            // chart legs: each side's join list, then FAF -> SF -> threshold
                if (tab == 1 && rw != null && fixes.Count > 0)
                {
                    var faf = fixes.Find(f => f.Role == "faf");
                    foreach (var side in new[] { "left", "right" })
                    {
                        var seq = fixes.FindAll(f => f.Role != "faf" && f.Role != "sf" && (f.Side == side || f.Side == "both")); if (faf != null) seq.Add(faf);
                        for (int i = 1; i < seq.Count; i++) MfdInk.Line(c, Proj(seq[i - 1].Lat, seq[i - 1].Lon), Proj(seq[i].Lat, seq[i].Lon), side == "left" ? new Color(.1f, .35f, .8f) : new Color(.75f, .1f, .4f), 4);
                    }
                    if (faf != null) MfdInk.Line(c, Proj(faf.Lat, faf.Lon), Proj(rw.Lat, rw.Lon), Color.black, 6);
                    for (int i = 0; i < fixes.Count; i++)
                    {
                        var p = Proj(fixes[i].Lat, fixes[i].Lon); bool fin = fixes[i].Role == "faf" || fixes[i].Role == "sf";
                        c.Dot(p, i == sel ? 10 : 7, i == sel ? Color.yellow : fin ? Color.cyan : Color.white);
                        Tag(c, new Rect(p.x + 6, p.y - 9, 120, 18), fixes[i].Name.Split('(')[0].Trim());
                    }
                }
                var act = NativeFlightController.ActiveRunway;
                if (act != null && act.Route != null)
                    for (int i = Math.Max(0, act.RouteIndex); i < act.Route.Count; i++)
                    {   // the mod-adjusted (smoothed) route actually flown
                        var p = Proj(act.Route[i].Lat, act.Route[i].Lon); c.Dot(p, 5, Color.green);
                        if (i > 0) MfdInk.Line(c, Proj(act.Route[i - 1].Lat, act.Route[i - 1].Lon), p, Color.green, 3);
                    }
                if (act != null) { double dla, dlo, crsD = NavigationMath.Bearing(act.Lat, act.Lon, act.EndLat, act.EndLon); NavigationMath.Offset(act.Lat, act.Lon, crsD + 180, act.DecelStartDist, body.Radius, out dla, out dlo); var dp = Proj(dla, dlo); c.Dot(dp, 9, new Color(1, .55f, 0)); Tag(c, new Rect(dp.x + 6, dp.y - 20, 120, 18), "DECEL " + (act.DecelStartDist / 1000).ToString("0.0") + " km"); }
                if (act != null && v != null && act.Phase == "entry" && act.Route != null && act.RouteIndex >= 0 && act.RouteIndex < act.Route.Count)
            {   // GROK MAP1: predicted path to the current fix at the bank the controller will fly (amber)
                double pb = act.TurnBank > 0 ? act.TurnBank : 25; var tw = act.Route[act.RouteIndex]; var amber = new Color(1f, .78f, .15f);
                Vector3d sv = v.srf_velocity; double trk = sv.magnitude > 5 ? (Math.Atan2(Vector3d.Dot(sv, Vector3d.Cross(v.upAxis, Vector3d.Exclude(v.upAxis, v.mainBody.transform.up).normalized)), Vector3d.Dot(sv, Vector3d.Exclude(v.upAxis, v.mainBody.transform.up).normalized)) * 180 / Math.PI + 360) % 360 : FlightGlobals.ship_heading;
                var pp = act.DubinsPts != null && act.DubinsPts.Count > 1 ? act.DubinsPts : PredictedPath.Points(v.latitude, v.longitude, trk, v.srfSpeed, pb, tw.Lat, tw.Lon, body.Radius);   // GROK JOIN1: the planned Dubins path, aligned at the fix
                for (int i = 1; i < pp.Count; i++) MfdInk.Line(c, Proj(pp[i - 1][0], pp[i - 1][1]), Proj(pp[i][0], pp[i][1]), amber, 2);
                var lp = Proj(pp[Math.Min(pp.Count - 1, Math.Max(1, pp.Count / 2))][0], pp[Math.Min(pp.Count - 1, Math.Max(1, pp.Count / 2))][1]);
                Tag(c, new Rect(lp.x + 6, lp.y + 4, 150, 18), "predicted (bank " + Math.Round(pb) + ")");
            }
                double fLa, fLo;
                if (act == null && v != null && NativeFlightController.FerryTarget(out fLa, out fLo))
                {   // GROK LAND2: FERRY / cruise to the runway - the predicted path (amber) too
                    double pb = 25; var amber = new Color(1f, .78f, .15f);
                    Vector3d sv = v.srf_velocity; double trk = sv.magnitude > 5 ? (Math.Atan2(Vector3d.Dot(sv, Vector3d.Cross(v.upAxis, Vector3d.Exclude(v.upAxis, v.mainBody.transform.up).normalized)), Vector3d.Dot(sv, Vector3d.Exclude(v.upAxis, v.mainBody.transform.up).normalized)) * 180 / Math.PI + 360) % 360 : FlightGlobals.ship_heading;
                    var pp = PredictedPath.Points(v.latitude, v.longitude, trk, v.srfSpeed, pb, fLa, fLo, body.Radius);
                    for (int i = 1; i < pp.Count; i++) MfdInk.Line(c, Proj(pp[i - 1][0], pp[i - 1][1]), Proj(pp[i][0], pp[i][1]), amber, 2);
                    var lp = Proj(pp[Math.Min(pp.Count - 1, Math.Max(1, pp.Count / 2))][0], pp[Math.Min(pp.Count - 1, Math.Max(1, pp.Count / 2))][1]);
                    Tag(c, new Rect(lp.x + 6, lp.y + 4, 170, 18), "predicted (cruise to runway)");
                }
            if (act != null && act.Chart != null && act.Chart.JoinPoint != null && act.Phase == "entry") { var jp = Proj(act.Chart.JoinPoint.Lat, act.Chart.JoinPoint.Lon); c.Dot(jp, 11, Color.magenta); Tag(c, new Rect(jp.x + 7, jp.y + 4, 90, 18), "JOIN"); }   // where the line is intercepted
                if (v != null) { var p = Proj(v.latitude, v.longitude); c.Dot(p, 8, kscRover ? Color.magenta : Color.red); double hd = FlightGlobals.ship_heading * Math.PI / 180; MfdInk.Line(c, p, p + new Vector2((float)Math.Sin(hd), -(float)Math.Cos(hd)) * 18, kscRover ? Color.magenta : Color.red, 2); if (kscRover) Tag(c, new Rect(p.x + 7, p.y + 5, 70, 18), "ROVER"); }
                if (mapMouse != null) mapMouse();   // the outside window only: the IMGUI mouse is map-square relative inside the group
            });
            var vv = FlightGlobals.ActiveVessel;
            if (tab == 0)
            {   // RPM 'Asc/Desc Mode' look: navy title bar, corner readouts, bottom data + status line - all over the map
                int tp = Mathf.Max(9, Mathf.RoundToInt(th * .7f));
                c.Fill(new Rect(0, 0, area.width, th), new Color(.06f, .09f, .28f, .92f));
                c.Text(new Rect(6, 0, area.width / 2, th), "MAP MODE", tp, Color.white, TextAnchor.MiddleLeft, false);
                c.Text(new Rect(area.width / 2, 0, area.width / 2 - 6, th), "Body: " + body.bodyName, tp, Color.white, TextAnchor.MiddleRight, false);
                if (vv != null) RpmCorners(c, vv, area, th);
                RpmBottom(c, area); return;
            }

            DataBox(c, area, DataBlock(rw, tab, mapMouse != null));
        }

        bool IsKscRover(Vessel v)
        {
            if (v == null || !v.LandedOrSplashed || body == null || body.bodyName != "Kerbin" || !NativeFlightController.ActiveRover) return false;
            double midLon = (KscRunway.Lon09 + KscRunway.Lon27) * .5;
            return NavigationMath.Distance(v.latitude, v.longitude, KscRunway.LatAt(midLon), midLon, body.Radius) <= 12000;
        }

        void DrawKscRoverMap(IMfdCanvas canvas, Vessel v)
        {
            foreach (KscRoverWaypoint waypoint in KscRoverMap.Points)
            {
                Vector2 p = Proj(waypoint.ApproachLat, waypoint.ApproachLon);
                if (p.x < 0 || p.y < 0 || p.x > Px || p.y > Px) continue;
                canvas.Dot(p, 7, new Color(1, .78f, .18f));
                Tag(canvas, new Rect(p.x + 5, p.y - 8, 64, 18), RoverShortName(waypoint.Name));
            }
            double lat, lon; string destination;
            if (NativeFlightController.RoverNavigation(out lat, out lon, out destination))
            {
                Vector2 from = Proj(v.latitude, v.longitude), to = Proj(lat, lon);
                MfdInk.Line(canvas, from, to, new Color(.2f, 1, .8f), 3);
                if (to.x >= 0 && to.y >= 0 && to.x <= Px && to.y <= Px)
                {
                    canvas.Dot(to, 9, new Color(.2f, 1, .8f));
                    Tag(canvas, new Rect(to.x + 6, to.y - 8, 100, 18), "TO " + RoverShortName(destination));
                }
            }
        }

        static string RoverShortName(string name)
        {
            switch (name)
            {
                case "Launch Pad": return "LP";
                case "Runway": return "RWY";
                case "VAB": return "VAB";
                case "SPH": return "SPH";
                case "Tracking Station": return "TRACK";
                case "Astronaut Complex": return "ASTRO";
                case "Mission Control": return "MISSION";
                case "R&D": return "R&D";
                case "Admin": return "ADMIN";
                case "Crawlerway": return "CRAWL";
                case "KSC Grass": return "GRASS";
                case "Shores": return "SHORE";
                default:
                    if (name.StartsWith("R&D - ", StringComparison.OrdinalIgnoreCase)) return "R&D-" + name.Substring(6, Math.Min(4, name.Length - 6));
                    return name.Length <= 8 ? name : name.Substring(0, 8);
            }
        }

        /// <summary>The MAP / CHART data box rows. tab: the page being drawn (not the outside window's Tab: the IVA screens draw their
        /// own page). mouse: true on the outside window only - the IVA screens have no mouse, so the hints name the soft keys.</summary>
        List<string> DataBlock(NativeFlightController.MapRunway rw, int tab, bool mouse)
        {
            var o = new List<string>(); var v = FlightGlobals.ActiveVessel; var act = NativeFlightController.ActiveRunway;
            o.Add((tab == 1 ? "CHART " : "MAP ") + (rw != null ? rw.Key : "no runway"));
            o.Add("SPAN  " + (ViewSpan >= 10000 ? (ViewSpan / 1000).ToString("0") : (ViewSpan / 1000).ToString("0.0")) + " km  " + (follow ? (centerRunway ? "RWY CTR" : "FOLLOW") : "PANNED"));
            if (tab == 1 && rw != null)
            {
                string cur = ChartStore.Active(rw.Key); o.Add("VAR   " + ChartStore.Label(rw.Key, cur));   // GROK LAND2: the chart's own title
                if (act != null && act.Key == rw.Key && act.ChartPin.Length > 0) { string pv; ChartStore.KeyOf(act.ChartPin + ".json", out pv); o.Add("FLY   " + ChartStore.Label(rw.Key, pv) + " (picked)"); }
                o.Add(NativeFlightController.ApLine);   // AP annunciator (+ LOC/GS on final)
                o.Add(PlaneLine(rw, act));
                if (sel >= 0 && sel < fixes.Count)
                {
                    var f = fixes[sel]; double gnd = NativeFlightController.MapTerrain(body, f.Lat, f.Lon); if (double.IsNaN(gnd)) gnd = 0;
                    o.Add("WP    " + f.Name.Split('(')[0].Trim() + "  " + Math.Round(f.Ref == "msl" ? f.AltMsl : f.AltMsl - gnd) + " m " + f.Ref.ToUpper() + "  gnd " + Math.Round(gnd));
                }
                else o.Add(mouse ? "WP    none (click the map or WP < / >)" : "WP    none (WP < / >)");
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < fixes.Count; i++) sb.Append(i == sel ? "[" : " ").Append(fixes[i].Name.Split('(')[0].Trim()).Append(i == sel ? "]" : " ");
                o.Add(sb.ToString()); if (note.Length > 0) o.Add(note);
            }
            else
            {
                if (v != null) o.Add("POS   " + v.latitude.ToString("0.000") + " " + v.longitude.ToString("0.000") + "\nALT   " + Math.Round(v.altitude) + " m\nHDG   " + Math.Round(FlightGlobals.ship_heading).ToString("000"));
                if (act != null) o.Add("APP   " + act.Key + " " + act.Phase.ToUpperInvariant() + "  " + (act.ToGo / 1000).ToString("0.0") + " km\nDECEL " + (act.DecelStartDist / 1000).ToString("0.0") + " km");
                o.Add(ScanSatLink.Installed ? "SCANsat coverage" : "flight-path reveal, " + MapReveal.Count(body.bodyName) + " tiles");
            }
            return o;
        }

        /// <summary>Instrument-style ILS: needles box (left) + data block (right), through the shared canvas, so the outside MFD and
        /// every IVA screen render the same instrument. Single source of truth with the autopilot.</summary>
        /// <summary>GROK TAB-ILS1: one narrow vertical deviation bar (label on top, centre line = target, marker = actual, arrow when clamped).</summary>
        static void DevBar(IMfdCanvas cv, Rect r, string label, double dev, double scale, Color c, int px)
        {
            int lp = Mathf.Max(8, px - 3); float top = r.y + lp + 4, bot = r.yMax - lp - 6, mid = (top + bot) / 2, half = (bot - top) / 2 - 4;
            cv.Text(new Rect(r.x - 4, r.y + 1, r.width + 8, lp + 3), label, lp, Color.white, TextAnchor.UpperCenter, false);
            cv.Fill(new Rect(r.x + r.width / 2 - 1, top, 2, bot - top), new Color(.35f, .35f, .35f));
            cv.Fill(new Rect(r.x, mid - 1, r.width, 2), Color.white);
            bool clamped; float y = mid - (float)Ils.BarOffset(dev, scale, half, out clamped);
            cv.Fill(new Rect(r.x + 1, y - 3, r.width - 2, 6), c);
            if (clamped) cv.Text(new Rect(r.x - 4, dev > 0 ? top - 2 : bot - lp, r.width + 8, lp + 2), dev > 0 ? "^" : "v", lp, c, TextAnchor.MiddleCenter, false);
            cv.Text(new Rect(r.x - 6, bot + 1, r.width + 12, lp + 4), dev.ToString("+0;-0"), lp, c, TextAnchor.UpperCenter, false);
        }
        void DrawIls(IMfdCanvas cv, Rect area)
        {
            var v = FlightGlobals.ActiveVessel; var act = NativeFlightController.ActiveRunway; var rw = Rw();
            double tla, tlo, ela, elo, elev; string label;
            if (act != null) { tla = act.Lat; tlo = act.Lon; ela = act.EndLat; elo = act.EndLon; elev = act.Elevation; label = string.IsNullOrEmpty(act.Key) ? "active runway" : act.Key; }
            else if (rw != null) { tla = rw.Lat; tlo = rw.Lon; ela = rw.EndLat; elo = rw.EndLon; elev = rw.Elevation; label = rw.Key; }
            else { cv.Text(area, "No runway on this body.", IlsPx(area), Text, TextAnchor.MiddleCenter, false); return; }
            if (v == null) return;
            var r = act != null && act.Coupled && act.Ils != null ? act.Ils : Ils.Compute(v.latitude, v.longitude, v.altitude, tla, tlo, ela, elo, elev, v.mainBody.Radius, act != null ? act.TouchdownM : 350, act != null ? act.FinalSlope : double.NaN);
            // the instrument fills the page; the data block overlays the right side on a dark box
            float dw = Mathf.Clamp(area.width * .44f, Mathf.Min(170, area.width * .6f), area.width * .6f);
            bool bars = act != null && act.Phase != "rollout" && act.Phase != "stopped"; float barsW = bars ? 46 : 0;   // GROK TAB-ILS1: SPD + VS bars between the instrument and the text
            float Sx = Mathf.Max(60, area.width - dw - barsW), Sy = Mathf.Max(60, area.height); Rect box = new Rect(0, 0, Sx, Sy);
            cv.Fill(area, new Color(.02f, .05f, .03f));
            Vector2 ctr = box.center;
            for (int i = -2; i <= 2; i++) { if (i == 0) continue; cv.Dot(new Vector2(ctr.x + i * Sx * .2f, ctr.y), 5, Color.white); cv.Dot(new Vector2(ctr.x, ctr.y + i * Sy * .2f), 5, Color.white); }
            cv.Dot(ctr, 8, Color.yellow);
            bool valid = r.Front && r.DmeM < 40000;
            float lx = ctr.x - (float)Ils.Needle(r.LocDeg, Ils.LocFullScale) * Sx * .4f, gy = ctr.y + (float)Ils.Needle(r.GsDeg, Ils.GsFullScale) * Sy * .4f;
            var nd = valid ? Color.magenta : new Color(.45f, .45f, .45f);   // needles: magenta in range, dim out of range
            cv.Fill(new Rect(lx - 1.5f, box.y + 10, 3, Sy - 20), nd); cv.Fill(new Rect(box.x + 10, gy - 1.5f, Sx - 20, 3), nd);
            if (!valid) Tag(cv, new Rect(box.x + 8, box.y + 6, Mathf.Min(Sx, Sy), 20), r.Front ? "OUT OF RANGE" : "BEHIND THE RUNWAY");
            bool gear = v.ActionGroups[KSPActionGroup.Gear], brk = v.ActionGroups[KSPActionGroup.Brakes]; double hat = v.altitude - elev, ias = v.indicatedAirSpeed, gs = v.horizontalSrfSpeed;
            double tgt = act != null && act.DesiredSpeed > 0 ? act.DesiredSpeed : 1.3 * Math.Max(30, NativeFlightController.MapStall);
            cv.Fill(new Rect(area.width - dw, 0, dw, area.height), Back);
            if (bars)
            {
                double apS = v.srfSpeed; string sb = Ils.SpdBarBand(apS, tgt, act.SpeedFloor); Color amber = new Color(1, .7f, 0);
                DevBar(cv, new Rect(Sx + 3, 0, 18, Sy), "SPD", apS - tgt, Ils.SpdBarScale, sb == "green" ? Color.green : sb == "amber" ? amber : Color.red, IlsPx(area));
                double dv = v.verticalSpeed - act.DesiredVs;   // + = shallower than the want (above), - = steeper
                DevBar(cv, new Rect(Sx + 25, 0, 18, Sy), "VS", dv, Ils.VsBarScale, Math.Abs(dv) <= 2 ? Color.green : Math.Abs(dv) <= 5 ? amber : Color.red, IlsPx(area));
            }
            var rows = new List<KeyValuePair<string, Color>>();   // the data block: text rows on a dark box, wrapped by the canvas
            rows.Add(new KeyValuePair<string, Color>("ILS " + label, Text));
            rows.Add(new KeyValuePair<string, Color>(act == null ? "AP OFF  raw ILS" : act.Coupled ? (act.GsCoupled ? "COUPLED LOC+GS" : "COUPLED LOC") : act.Phase == "entry" || act.Phase == "intercept" ? "LOC ARMED" : "NOT COUPLED", Text));
            rows.Add(new KeyValuePair<string, Color>("DME  " + (r.DmeM / 1000).ToString("0.0") + " km" + (gs > 1 ? "  ETA " + Math.Round(r.DmeM / gs) + " s" : ""), Text));
            rows.Add(new KeyValuePair<string, Color>("LOC  " + r.LocDeg.ToString("+0.00;-0.00") + "  XTK " + Math.Abs(r.CrossM).ToString("0.0") + " m " + (r.CrossM > 0 ? "R" : "L"), Text));
            rows.Add(new KeyValuePair<string, Color>("G/S  " + r.GsDeg.ToString("+0.00;-0.00") + "  " + Math.Abs(r.AboveGsM).ToString("0") + " m " + (r.AboveGsM > 0 ? "HIGH" : "LOW"), Text));
            rows.Add(new KeyValuePair<string, Color>("CRS  " + Math.Round(r.Course).ToString("000") + "   HAT " + Math.Round(hat) + " m", Text));
            double apSpd = v.srfSpeed;   // GROK LAND5e: the AP flies surface speed against the plan (IAS read 103-123 while it flew 150-161)
            string band = Ils.SpeedBand(apSpd, tgt, RunwayMission.PredictedNow, RunwayMission.AppNow);
            rows.Add(new KeyValuePair<string, Color>(Ils.SpdLine(apSpd, tgt, ias), band == "green" ? Color.green : band == "amber" ? new Color(1, .7f, 0) : Color.red));
            rows.Add(new KeyValuePair<string, Color>("VS   " + v.verticalSpeed.ToString("+0;-0") + " m/s", Text));
            { string fl = NativeFlightController.FlapLine; if (fl.Length > 0) rows.Add(new KeyValuePair<string, Color>(fl, Text)); }
            { var ar = NativeFlightController.ActiveRunway; if (ar != null && ar.EntryChart.Length > 0) rows.Add(new KeyValuePair<string, Color>("ENTRY " + EntryPolicy.Label(ar.EntryChart), Text)); }
            rows.Add(new KeyValuePair<string, Color>("GEAR " + (gear ? "DOWN" : "UP") + "   " + (brk ? (NativeFlightController.AirbrakesByAutopilot ? "AIRBRAKES" : "BRAKES") : ""), !gear && r.Front && r.DmeM < 3000 ? Color.red : Text));
            if (act != null) rows.Add(new KeyValuePair<string, Color>("DECEL " + (act.DecelStartDist / 1000).ToString("0.0") + " km " + (double.IsNaN(act.DecelA) ? "est" : act.DecelA.ToString("0.0") + " m/s2") + (r.DmeM > act.DecelStartDist ? "" : " DECEL"), Text));
            if (guidance && valid)
            {
                double fh = Ils.FdHeading(r, v.srfSpeed), fv = Ils.FdVs(r, v.srfSpeed), dh = FlightPolicy.Wrap(fh - FlightGlobals.ship_heading), dv = fv - v.verticalSpeed;
                rows.Add(new KeyValuePair<string, Color>("FD   " + (dh < -2 ? "<< LEFT" : dh > 2 ? "RIGHT >>" : "ON CRS") + "  " + (dv > 2 ? "UP" : dv < -2 ? "DOWN" : "ON PATH"), Text));
                var co = Ils.Callouts(r, hat, gear, ias, tgt, RunwayMission.PredictedNow, RunwayMission.AppNow); if (co.Count > 0) rows.Add(new KeyValuePair<string, Color>(string.Join(" ", co.ToArray()), new Color(1, .7f, 0)));
            }
            else rows.Add(new KeyValuePair<string, Color>("FD   " + (guidance ? "no signal" : "off (GUIDE)"), Text));
            int ip = IlsPx(area); float wd = dw - 8, rx = area.width - dw + 6, ry = 2;
            foreach (var row in rows)
            {   // each row is sized by the canvas (a wrapped row takes the height it needs); drawing stops at the bottom edge
                float h = cv.TextHeight(row.Key, wd, ip, true); if (ry + h > area.height) break;
                cv.Text(new Rect(rx, ry, wd, h), row.Key, ip, row.Value, TextAnchor.UpperLeft, true); ry += h;
            }
        }
        /// <summary>ILS data-block text size for a page size (the same clamp the CHART data block uses).</summary>
        static int IlsPx(Rect area) { return Mathf.Clamp(Mathf.RoundToInt(area.height / 30), 10, 15); }

        string planeLine = ""; float planeAt;
        /// <summary>This craft's join speed / bank / radius and which chart turns are tight for it (active route or a preview of the editor fixes).</summary>
        string PlaneLine(NativeFlightController.MapRunway rw, RunwayMission act)
        {
            if (act != null && act.PlanSpeed > 0) return "Plane " + NativeFlightController.CraftLabel + " " + NativeFlightController.StallLabel + " " + Math.Round(act.PlanSpeed) + " m/s, " + act.PlanG.ToString("0.0") + " g (" + ApproachProfile.Limit + "), bank " + Math.Round(act.PlanBank) + (act.PlanBankRaw > act.PlanBank + .5 ? " (capped from " + Math.Round(act.PlanBankRaw) + ")" : "") + ", r " + (act.PlanRadius / 1000).ToString("0.0") + " km" + (details ? "\ncrew " + ApproachProfile.CrewG.ToString("0") + " g, structure " + ApproachProfile.StructG.ToString("0.0") + " g, bank now " + Math.Round(act.TurnBank) + "\n" + act.JoinLog + "\n" + act.SmoothLog : "");
            if (rw == null || Time.realtimeSinceStartup < planeAt) return planeLine;
            planeAt = Time.realtimeSinceStartup + 1;
            double v = ApproachProfile.Speed(NativeFlightController.MapStall), gN = ApproachProfile.LoadFactor(ApproachProfile.JoinG, v, NativeFlightController.MapStall), b = ApproachProfile.BankForG(gN), crs = NavigationMath.Bearing(rw.Lat, rw.Lon, rw.EndLat, rw.EndLon);
            var faf = fixes.Find(f => f.Role == "faf"); string tight = "";
            foreach (var side in new[] { "left", "right" })
            {
                var seq = new List<ApproachChart.Wp>();
                foreach (var f in fixes) if (f.Role != "faf" && f.Role != "sf" && (f.Side == side || f.Side == "both")) seq.Add(new ApproachChart.Wp { Name = f.Name.Split('(')[0].Trim(), Lat = f.Lat, Lon = f.Lon, Alt = f.AltMsl });
                if (faf != null) seq.Add(new ApproachChart.Wp { Name = "FAF", Lat = faf.Lat, Lon = faf.Lon, Alt = faf.AltMsl });
                string lg; ApproachChart.Smooth(seq, crs, rw.Lat, rw.Lon, v, b, body.Radius, out lg);
                int t = lg.IndexOf("TIGHT"); if (t >= 0) tight += side[0] + ": " + lg.Substring(t + 15) + " ";
            }
            planeLine = "Plane " + NativeFlightController.CraftLabel + " " + Math.Round(v) + " m/s, " + gN.ToString("0.0") + " g (" + ApproachProfile.Limit + "), bank " + Math.Round(b) + ", r " + (ApproachProfile.Radius(v, b) / 1000).ToString("0.0") + " km, " + (tight.Length > 0 ? "TIGHT" + (details ? ": " + tight : " (details)") : "turns fit") + (details ? "\n1.5 x stall " + Math.Round(NativeFlightController.MapStall) + " m/s" : "");
            return planeLine;
        }

        bool panning; Vector2 panFrom;
        static readonly Color Cyan = new Color(.35f, .9f, 1f);
        internal static readonly Color Back = new Color(0, 0, 0, .62f); internal static readonly Color Text = new Color(.85f, 1f, .85f), CyanText = new Color(.35f, .9f, 1f);
        static int tagPx = 12;   // map label text size, scaled to the page (set per draw in DrawPages)
        /// <summary>Map label on a dark backing box (never grey-on-grey): the canvas sizes the box to the text.</summary>
        static void Tag(IMfdCanvas c, Rect r, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            float w = c.TextWidth(text, tagPx), h = c.TextHeight(text, w + 8, tagPx, false);
            c.Fill(new Rect(r.x - 2, r.y, w + 4, h), Back);
            c.Text(new Rect(r.x, r.y, w + 2, h), text, tagPx, Color.white, TextAnchor.UpperLeft, false);
        }
        /// <summary>CHART data block: overlaid top-right on a dark box, wrapped, sized to its text.</summary>
        static void DataBox(IMfdCanvas c, Rect area, List<string> lines)
        {
            float w = Mathf.Clamp(area.width * .46f, Mathf.Min(170, area.width), area.width); int px = Mathf.Clamp(Mathf.RoundToInt(area.height / 30), 10, 16);
            float h = 4; var hs = new List<float>(); foreach (var l in lines) { float lh = c.TextHeight(l, w - 8, px, true); hs.Add(lh); h += lh; }
            h = Mathf.Min(h, area.height); Rect box = new Rect(area.width - w, 0, w, h); c.Fill(box, Back);
            float y = 2; for (int i = 0; i < lines.Count && y < h; i++) { c.Text(new Rect(box.x + 4, y, w - 8, hs[i]), lines[i], px, Text, TextAnchor.UpperLeft, true); y += hs[i]; }
        }
        internal static bool TextColorOk(Color c) { return MfdNav.TextColorOk(c.r, c.g, c.b); }
        static string Col(string hex, string s) { return "<color=#" + hex + ">" + s + "</color>"; }
        void RpmCorners(IMfdCanvas c, Vessel v, Rect area, float top)
        {
            int fs = Mathf.Max(9, Mathf.RoundToInt(Mathf.Min(area.width, area.height) / 26)); float lh = fs * 1.25f, w = area.width / 2 - 4, bw = Mathf.Min(w, fs * 9.5f);
            double thrust = 0; foreach (var e in v.FindPartModulesImplementing<ModuleEngines>()) thrust += e.finalThrust;
            double twr = thrust / Math.Max(.001, v.GetTotalMass() * 9.81 * v.mainBody.GeeASL);
            var tl = new[] { "ALT " + Col("FFFFFF", (v.altitude / 1000).ToString("0.00") + "km"), "V.spd " + Col("FFFFFF", v.verticalSpeed.ToString("0.0")), "H.spd " + Col("FFFFFF", v.horizontalSrfSpeed.ToString("0.0")), "TWR " + Col("FFFFFF", twr.ToString("0.00")) };
            var tr = new[] { Col("FFFFFF", (-Roll(v)).ToString("0.0")) + " Roll", Col("FFFFFF", Pitch(v).ToString("0.0")) + " Pitch", Col("FFFFFF", FlightGlobals.ship_heading.ToString("000")) + " Hdg", Col("FFFFFF", thrust.ToString("0")) + "kN Thrust" };
            c.Fill(new Rect(0, top, bw, lh * 4 + 4), Back); c.Fill(new Rect(area.width - bw, top, bw, lh * 4 + 4), Back);
            for (int i = 0; i < 4; i++)
            {
                c.Text(new Rect(4, top + 2 + i * lh, w, lh), tl[i], fs, Cyan, TextAnchor.MiddleLeft, false);
                c.Text(new Rect(area.width - w - 4, top + 2 + i * lh, w, lh), tr[i], fs, Cyan, TextAnchor.MiddleRight, false);
            }
        }
        static double Pitch(Vessel v) { var t = v.ReferenceTransform; return t == null ? 0 : Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(t.up, v.upAxis)))) * 180 / Math.PI; }
        static double Roll(Vessel v) { var t = v.ReferenceTransform; return t == null ? 0 : -Math.Atan2(Vector3d.Dot(t.right, v.upAxis), Vector3d.Dot(-t.forward, v.upAxis)) * 180 / Math.PI; }
        void RpmBottom(IMfdCanvas c, Rect area)
        {
            var v = FlightGlobals.ActiveVessel; if (v == null) return;
            float lh = Mathf.Max(13, area.height * .058f), y = area.height - 3 * lh - 2; int fs = Mathf.Max(9, Mathf.RoundToInt(lh * .72f));
            c.Fill(new Rect(0, y - 2, area.width, 2 * lh + 2), Back);
            double h0 = NativeFlightController.MapTerrain(body, v.latitude, v.longitude), la, lo; NavigationMath.Offset(v.latitude, v.longitude, FlightGlobals.ship_heading, 100, body.Radius, out la, out lo);
            double h1 = NativeFlightController.MapTerrain(body, la, lo), slope = double.IsNaN(h0) || double.IsNaN(h1) ? 0 : Math.Atan2(Math.Max(0, h1) - Math.Max(0, h0), 100) * 180 / Math.PI;
            var act = NativeFlightController.ActiveRunway; string eta = "--";
            if (act != null && v.horizontalSrfSpeed > 1) { double s = NavigationMath.Distance(v.latitude, v.longitude, act.Lat, act.Lon, body.Radius) / v.horizontalSrfSpeed; eta = ((int)(s / 60)).ToString("0") + "m" + ((int)(s % 60)).ToString("00") + "s"; }
            c.Text(new Rect(4, y, area.width - 8, lh), "Lat " + Col("FFFFFF", v.latitude.ToString("0.0000")) + "  Lon " + Col("FFFFFF", v.longitude.ToString("0.0000")), fs, Cyan, TextAnchor.MiddleLeft, false);
            c.Text(new Rect(4, y + lh, area.width - 8, lh), "Slope " + Col("FFFFFF", slope.ToString("0.0") + "\u00b0") + "   ETA " + Col("FFFFFF", eta) + (act != null ? "  " + Col("FFFFFF", act.Key ?? "") : ""), fs, Cyan, TextAnchor.MiddleLeft, false);
            double lf = 0, lfc = 0; foreach (Part p in v.parts) foreach (PartResource r in p.Resources) if (r.resourceName == "LiquidFuel") { lf += r.amount; lfc += r.maxAmount; }
            bool sas = v.ActionGroups[KSPActionGroup.SAS], rcs = v.ActionGroups[KSPActionGroup.RCS]; string fuel = MfdNav.FuelStatus(lf, lfc), ap = NativeFlightController.ApLine;
            string line = (sas ? Col("7CFF7C", "SAS") : Col("C08080", "SAS")) + " " + (rcs ? Col("7CFF7C", "RCS") : Col("C08080", "RCS")) + "  " + (fuel == "!Empty!" ? Col("FF4040", fuel) : Col("FFFFFF", fuel))
                + "  " + (ap == "AP OFF" ? Col("FFB020", ap) : Col("7CFF7C", ap));
            c.Fill(new Rect(0, y + 2 * lh, area.width, lh + 2), new Color(0, 0, 0, .75f));
            c.Text(new Rect(4, y + 2 * lh, area.width - 8, lh), line, fs, Color.white, TextAnchor.MiddleLeft, false);
        }
        void HandleMouse(NativeFlightController.MapRunway rw)
        {
            var e = Event.current;
            if (e.type == EventType.ScrollWheel && e.mousePosition.x <= Px && e.mousePosition.y <= Px) { ViewSpan = Zoom(ViewSpan, e.delta.y > 0 ? 1.25 : 0.8); e.Use(); return; }
            // click-drag on empty map area pans (MAP and CHART); on a waypoint (CHART) it moves the waypoint
            if (e.type == EventType.MouseDown && e.button == 0 && e.mousePosition.x <= Px && e.mousePosition.y <= Px && Tab <= 1)
            {
                sel = -1; float best = 12;
                if (rw != null && Tab == 1) for (int i = 0; i < fixes.Count; i++) { float d = Vector2.Distance(Proj(fixes[i].Lat, fixes[i].Lon), e.mousePosition); if (d < best) { best = d; sel = i; } }
                dragging = sel >= 0; panning = !dragging; panFrom = e.mousePosition;
                if (panning && follow) { fixLat = cLat; fixLon = cLon; follow = false; }
                e.Use(); return;
            }
            if (e.type == EventType.MouseDrag && panning)
            {
                double la0, lo0, la1, lo1; Unproj(cLat, cLon, span, panFrom.x, panFrom.y, out la0, out lo0); Unproj(cLat, cLon, span, e.mousePosition.x, e.mousePosition.y, out la1, out lo1);
                fixLat -= la1 - la0; fixLon -= lo1 - lo0; cLat = fixLat; cLon = fixLon; panFrom = e.mousePosition; e.Use(); return;
            }
            if (e.type == EventType.MouseUp && panning) { panning = false; e.Use(); return; }
            if (rw == null || Tab != 1) return;
            else if (e.type == EventType.MouseDrag && dragging && sel >= 0)
            {
                double la, lo; Unproj(cLat, cLon, span, e.mousePosition.x, e.mousePosition.y, out la, out lo);
                var f = fixes[sel];
                if (f.Role == "faf" || f.Role == "sf")
                {   // locked to the extended centerline: only slides along it
                    double crs = NavigationMath.Bearing(rw.Lat, rw.Lon, rw.EndLat, rw.EndLon), d = NavigationMath.Distance(rw.Lat, rw.Lon, la, lo, body.Radius);
                    double th = (NavigationMath.Bearing(rw.Lat, rw.Lon, la, lo) - (crs + 180)) * Math.PI / 180, along = Math.Max(500, Math.Min(60000, d * Math.Cos(th)));
                    NavigationMath.Offset(rw.Lat, rw.Lon, crs + 180, along, body.Radius, out la, out lo);
                }
                f.Lat = la; f.Lon = lo; e.Use();
            }
            else if (e.type == EventType.MouseUp) { if (dragging) Publish(); dragging = false; }
        }

        void AddWp()
        {
            int at = sel >= 0 && fixes[sel].Role != "faf" && fixes[sel].Role != "sf" ? sel + 1 : fixes.FindIndex(f => f.Role == "faf" || f.Role == "sf");
            if (at < 0) at = fixes.Count;
            var a = at > 0 ? fixes[at - 1] : fixes.Find(f => f.Role == "faf"); if (a == null) return;
            var b = at < fixes.Count && fixes[at].Role == "wp" ? fixes[at] : fixes.Find(f => f.Role == "faf") ?? a;
            int n = fixes.FindAll(f => f.Role == "wp").Count + 1;
            fixes.Insert(at, new ApproachFile.EditFix { Role = "wp", Name = "WP" + n, Side = a.Side, Ref = a.Ref, Lat = (a.Lat + b.Lat) / 2 + (a == b ? .02 : 0), Lon = (a.Lon + b.Lon) / 2, AltMsl = a.AltMsl });
            sel = at; added.Add(fixes[at]); Publish();
        }
        readonly HashSet<ApproachFile.EditFix> added = new HashSet<ApproachFile.EditFix>();
        /// <summary>Oct 12: WPs added this session (saved or not) go to the approach planner right away.</summary>
        void Publish() { UserWaypoints.Put(fixesFor, fixes.FindAll(f => added.Contains(f)).ConvertAll(f => new ApproachChart.Wp { Name = f.Name, Lat = f.Lat, Lon = f.Lon, Alt = f.AltMsl })); }

        void Save(NativeFlightController.MapRunway rw)
        {
            try
            {
                NativeFlightController.EnsureCharts();
                string variant = ChartStore.Active(rw.Key), path = ChartStore.PathFor(rw.Key); if (!File.Exists(path)) path = System.IO.Path.Combine(ChartStore.Dir, ChartStore.FileName(rw.Key, variant));
                string old = File.Exists(path) ? File.ReadAllText(path) : "";
                if (old.Length > 0) File.Copy(path, path + ".bak", true);
                string wrapped = old.Trim().Length > 0 ? "{" + MiniJson.Serialize(rw.Key) + ":" + old + "}" : "";
                var merged = MiniJson.Deserialize(ApproachFile.Merge(wrapped, rw.Key, fixes, (la, lo) => NativeFlightController.MapTerrain(body, la, lo)));
                AtomicFile.Write(path, MiniJson.Serialize(merged[rw.Key]));   // same per-end file the hot-swap watcher reloads
                note = "Saved " + rw.Key + " to charts/" + System.IO.Path.GetFileName(path) + " (reloads live).";
                ChatLog.Write("approach", "map editor saved " + rw.Key + " (" + fixes.Count + " fixes)");
            }
            catch (Exception ex) { note = "Save failed: " + ex.Message; }
        }
    }
}
