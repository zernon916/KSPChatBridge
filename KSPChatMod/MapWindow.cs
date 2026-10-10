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
        Texture2D tex; Color[] px = new Color[N * N]; int buildRow = -1; double bLat, bLon, bSpan; float nextRebuild;
        List<NativeFlightController.MapRunway> runways = new List<NativeFlightController.MapRunway>();
        List<ApproachFile.EditFix> fixes = new List<ApproachFile.EditFix>(); string fixesFor = ""; int sel = -1; bool dragging; string note = "";
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
        internal static void NextVariant() { var m = Inst; var rw = m == null ? null : m.Rw(); if (rw == null) return; NativeFlightController.EnsureCharts(); var vs = ChartStore.Variants(rw.Key); if (!vs.Contains("")) vs.Insert(0, ""); string cur = ChartStore.Active(rw.Key); ChartStore.SetActive(rw.Key, vs[(vs.IndexOf(cur) + 1 + vs.Count) % vs.Count]); m.fixesFor = ""; }
        internal static void AddWaypoint() { if (Inst != null) Inst.AddWp(); }
        internal static void RemoveWaypoint() { var m = Inst; if (m == null) return; if (m.sel >= 0 && m.sel < m.fixes.Count && m.fixes[m.sel].Role != "faf" && m.fixes[m.sel].Role != "sf") { m.fixes.RemoveAt(m.sel); m.sel = -1; } else m.note = "Select a waypoint (FAF/SF are locked)."; }
        internal static void ResetChart() { var m = Inst; if (m != null) { m.fixesFor = ""; m.note = "Reset to computed (SAVE to keep)."; } }
        internal static void SaveChart() { var m = Inst; var rw = m == null ? null : m.Rw(); if (rw != null) m.Save(rw); }
        internal static string Title { get { var m = Inst; var rw = m == null ? null : m.Rw(); var act = NativeFlightController.ActiveRunway; return act != null && !string.IsNullOrEmpty(act.Key) ? act.Key : rw != null ? rw.Key : ""; } }
        NativeFlightController.MapRunway Rw() { return runways.Count > 0 ? runways[Math.Min(rwIdx, runways.Count - 1)] : null; }
        /// <summary>Draw the MAP / CHART / ILS page inside the MFD screen.</summary>
        internal void DrawEmbedded(Rect area, int tab)
        {
            Tab = tab; rect.width = area.width + 20; rect.height = area.height + 40; Px = MapSide(rect.width, rect.height, Tab);
            GUILayout.BeginArea(area);
            try { if (body == null) GUILayout.Label("No map in this scene."); else DrawInner(); } catch (Exception ex) { GuiGuard.Log("MapWindow.Embedded", ex); }
            GUILayout.EndArea();
        }

        void Update()
        {
            var v = FlightGlobals.ActiveVessel; if (v == null) return;
            if (!ScanSatLink.Installed && v.situation != Vessel.Situations.PRELAUNCH)
                MapReveal.Fly(v.mainBody.bodyName, v.latitude, v.longitude, Math.Max(0, v.altitude - Math.Max(0, v.terrainAltitude)), v.mainBody.Radius);
            if (!MapVisible) return;
            body = v.mainBody;
            if (runways.Count == 0 || Time.realtimeSinceStartup > nextRebuild) runways = NativeFlightController.MapRunways(body);
            var act = NativeFlightController.ActiveRunway;
            if (act != null && !string.IsNullOrEmpty(act.Key)) { int i = runways.FindIndex(r => r.Key == act.Key); if (i >= 0) rwIdx = i; }
            if (rwIdx >= runways.Count) rwIdx = 0;
            var rw = runways.Count > 0 ? runways[rwIdx] : null;
            if (rw != null && fixesFor != rw.Key) LoadFixes(rw);
            span = ViewSpan;
            if (!follow) { cLat = fixLat; cLon = fixLon; } else if (centerRunway && rw != null) { cLat = rw.Lat; cLon = rw.Lon; } else { cLat = v.latitude; cLon = v.longitude; }
            double moved = NavigationMath.Distance(cLat, cLon, bLat, bLon, body.Radius);
            if (buildRow < 0 && (bSpan != span || moved > span * .15 || Time.realtimeSinceStartup > nextRebuild)) { bLat = cLat; bLon = cLon; bSpan = span; buildRow = 0; nextRebuild = Time.realtimeSinceStartup + 15; }
            if (buildRow >= 0) BuildRows(12);
        }

        void BuildRows(int rows)
        {
            if (tex == null) { tex = new Texture2D(N, N, TextureFormat.RGB24, false); tex.filterMode = FilterMode.Bilinear; }
            var extra = NativeFlightController.SpotPositions(body);
            Func<double, double, bool> cov = null; if (ScanSatLink.Installed) cov = (la, lo) => ScanSatLink.IsCovered(la, lo, body);
            for (int k = 0; k < rows && buildRow < N; k++, buildRow++)
                for (int x = 0; x < N; x++)
                {
                    double la, lo; Unproj(bLat, bLon, bSpan, (x + .5f) * Px / N, (buildRow + .5f) * Px / N, out la, out lo);
                    Color c;
                    if (!MapReveal.Revealed(body.bodyName, la, lo, body.Radius, cov, extra)) c = new Color(.45f, .45f, .47f);
                    else { double h = NativeFlightController.MapTerrain(body, la, lo); c = HeightColor(double.IsNaN(h) ? 0 : h); }
                    px[(N - 1 - buildRow) * N + x] = c;
                }
            if (buildRow >= N) { tex.SetPixels(px); tex.Apply(); buildRow = -1; }
        }

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

        void LoadFixes(NativeFlightController.MapRunway rw)
        {
            fixesFor = rw.Key; sel = -1; fixes.Clear(); note = "";
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
            var r = Ils.Compute(v.latitude, v.longitude, v.altitude, act.Lat, act.Lon, act.EndLat, act.EndLon, act.Elevation, v.mainBody.Radius, act.TouchdownM);
            if (Ils.InLocCaptureZone(r, RunwayMission.LocRange)) { autoIlsFor = act; Tab = 2; AicsMenu.AutoIls(); }
        }
        static bool details, guidance;
        /// <summary>Map/ILS display side for a window size: fills the width, leaves room for the rows below (pure; tested).</summary>
        internal static float MapSide(float w, float h, int tab) { return Mathf.Max(160, Mathf.Min(w - 20, h - (tab == 1 ? 250 : tab == 2 ? 170 : 140))); }
        static GUIStyle wrap;
        static GUIStyle Wrap { get { if (wrap == null || wrap.normal.textColor != GUI.skin.label.normal.textColor) wrap = new GUIStyle(GUI.skin.label) { wordWrap = true }; return wrap; } }
        void OnGUIInner()
        {
            if (!MapVisible || body == null) return;
            AutoIlsTab();
            // drawn inside the AICS MFD (AicsMfd.cs): no window of its own
        }

        static void Dot(Vector2 p, float s, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(p.x - s / 2, p.y - s / 2, s, s), Texture2D.whiteTexture); GUI.color = o; }
        static void Line(Vector2 a, Vector2 b, Color c, float step = 4)
        {
            float d = Vector2.Distance(a, b); int n = Mathf.Min(400, Mathf.Max(1, (int)(d / step)));
            for (int i = 0; i <= n; i++) { var p = Vector2.Lerp(a, b, i / (float)n); if (p.x >= 0 && p.y >= 0 && p.x <= Px && p.y <= Px) Dot(p, 2, c); }
        }

        void DrawInner()
        {
            if (Tab == 2) { DrawIls(); return; }
            var rw = runways.Count > 0 ? runways[Math.Min(rwIdx, runways.Count - 1)] : null;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(24)) && runways.Count > 0) { rwIdx = (rwIdx + runways.Count - 1) % runways.Count; fixesFor = ""; }
            GUILayout.Label(rw != null ? rw.Key : "no runway", GUILayout.Width(110));
            if (GUILayout.Button(">", GUILayout.Width(24)) && runways.Count > 0) { rwIdx = (rwIdx + 1) % runways.Count; fixesFor = ""; }
            if (GUILayout.Button("-", GUILayout.Width(24))) ViewSpan = Zoom(ViewSpan, 2);
            GUILayout.Label((ViewSpan >= 10000 ? (ViewSpan / 1000).ToString("0") : (ViewSpan / 1000).ToString("0.0")) + " km", GUILayout.Width(48));
            if (GUILayout.Button("+", GUILayout.Width(24))) ViewSpan = Zoom(ViewSpan, .5);
            centerRunway = GUILayout.Toggle(centerRunway, "center rwy");
            GUILayout.EndHorizontal();
            if (Tab == 1 && rw != null)
            {   GUILayout.BeginHorizontal();   // second row: never clipped   // chart variant selector (default = Luke's chart; e.g. "tight"); writes charts/active.json, hot-swap reloads it
                NativeFlightController.EnsureCharts(); var vs = ChartStore.Variants(rw.Key); if (!vs.Contains("")) vs.Insert(0, "");
                string cur = ChartStore.Active(rw.Key);
                if (vs.Count > 1 && GUILayout.Button("chart: " + (cur.Length == 0 ? "default" : cur), GUILayout.Width(110)))
                { string next = vs[(vs.IndexOf(cur) + 1 + vs.Count) % vs.Count]; ChartStore.SetActive(rw.Key, next); fixesFor = ""; }
                details = GUILayout.Toggle(details, "details");
                GUILayout.FlexibleSpace(); GUILayout.EndHorizontal();
            }
            Rect map = GUILayoutUtility.GetRect(Px, Px, GUILayout.Width(Px), GUILayout.Height(Px));
            if (tex != null) GUI.DrawTexture(map, tex);
            GUI.BeginGroup(map);
            try {
            foreach (var r in runways) Line(Proj(r.Lat, r.Lon), Proj(r.EndLat, r.EndLon), Color.black, 1);
            if (Time.realtimeSinceStartup > taxiAt) { taxiAt = Time.realtimeSinceStartup + 5; taxiRoutes = body.bodyName == "Kerbin" ? NativeFlightController.TaxiRoutes() : new List<TaxiRoute>(); }
            foreach (var tr in taxiRoutes)
            {   // ground charts (PluginData/charts/TAXI_*.json): amber dotted centerlines
                for (int i = 1; i < tr.Points.Count; i++) Line(Proj(tr.Points[i - 1].Lat, tr.Points[i - 1].Lon), Proj(tr.Points[i].Lat, tr.Points[i].Lon), new Color(1, .75f, .2f), 3);
                if (span <= 8000) foreach (var p in tr.Points) { var q = Proj(p.Lat, p.Lon); if (q.x > 0 && q.y > 0 && q.x < Px && q.y < Px) GUI.Label(new Rect(q.x + 4, q.y + 2, 110, 18), p.Name); }
            }
            foreach (var a in MapReveal.Airports) if (body.bodyName == "Kerbin") { var p = Proj(a.Lat, a.Lon); if (p.x > 0 && p.y > 0 && p.x < Px && p.y < Px) GUI.Label(new Rect(p.x + 4, p.y - 8, 140, 18), a.Pad ? "▲ " + a.Name : a.Name); }
            // chart legs: each side's join list, then FAF -> SF -> threshold
            if (Tab == 1 && rw != null && fixes.Count > 0)
            {
                var faf = fixes.Find(f => f.Role == "faf"); var sf = fixes.Find(f => f.Role == "sf");
                foreach (var side in new[] { "left", "right" })
                {
                    var seq = fixes.FindAll(f => f.Role != "faf" && f.Role != "sf" && (f.Side == side || f.Side == "both")); if (faf != null) seq.Add(faf);
                    for (int i = 1; i < seq.Count; i++) Line(Proj(seq[i - 1].Lat, seq[i - 1].Lon), Proj(seq[i].Lat, seq[i].Lon), side == "left" ? new Color(.1f, .35f, .8f) : new Color(.75f, .1f, .4f));
                }
                if (faf != null) Line(Proj(faf.Lat, faf.Lon), Proj(rw.Lat, rw.Lon), Color.black, 6);
                for (int i = 0; i < fixes.Count; i++)
                {
                    var p = Proj(fixes[i].Lat, fixes[i].Lon); bool fin = fixes[i].Role == "faf" || fixes[i].Role == "sf";
                    Dot(p, i == sel ? 10 : 7, i == sel ? Color.yellow : fin ? Color.cyan : Color.white);
                    GUI.Label(new Rect(p.x + 6, p.y - 9, 120, 18), fixes[i].Name.Split('(')[0].Trim());
                }
            }
            var act = NativeFlightController.ActiveRunway;
            if (act != null && act.Route != null)
                for (int i = Math.Max(0, act.RouteIndex); i < act.Route.Count; i++)
                {   // the mod-adjusted (smoothed) route actually flown
                    var p = Proj(act.Route[i].Lat, act.Route[i].Lon); Dot(p, 5, Color.green);
                    if (i > 0) Line(Proj(act.Route[i - 1].Lat, act.Route[i - 1].Lon), p, Color.green, 3);
                }
            if (act != null) { double dla, dlo, crsD = NavigationMath.Bearing(act.Lat, act.Lon, act.EndLat, act.EndLon); NavigationMath.Offset(act.Lat, act.Lon, crsD + 180, act.DecelStartDist, body.Radius, out dla, out dlo); var dp = Proj(dla, dlo); Dot(dp, 9, new Color(1, .55f, 0)); GUI.Label(new Rect(dp.x + 6, dp.y - 20, 120, 18), "DECEL " + (act.DecelStartDist / 1000).ToString("0.0") + " km"); }
            if (act != null && act.Chart != null && act.Chart.JoinPoint != null && act.Phase == "entry") { var jp = Proj(act.Chart.JoinPoint.Lat, act.Chart.JoinPoint.Lon); Dot(jp, 11, Color.magenta); GUI.Label(new Rect(jp.x + 7, jp.y + 4, 90, 18), "JOIN"); }   // where the line is intercepted
            var v = FlightGlobals.ActiveVessel;
            if (v != null) { var p = Proj(v.latitude, v.longitude); Dot(p, 8, Color.red); double hd = FlightGlobals.ship_heading * Math.PI / 180; Line(p, p + new Vector2((float)Math.Sin(hd), -(float)Math.Cos(hd)) * 18, Color.red, 2); }
            HandleMouse(rw);
            } finally { GUI.EndGroup(); }
            GUILayout.Label((ScanSatLink.Installed ? "Mapping: SCANsat coverage" : "Mapping: flight path (no mapping mod), " + MapReveal.Count(body.bodyName) + " tiles") + "  |  airports always shown");
            if (Tab == 0)
            {   // pan the view (not the window): 25% of the shown width per press; panning stops follow-craft until re-center
                GUILayout.BeginHorizontal();
                foreach (var pb in new[] { new KeyValuePair<string, double>("<", 270), new KeyValuePair<string, double>("^", 0), new KeyValuePair<string, double>("v", 180), new KeyValuePair<string, double>(">", 90) })
                    if (GUILayout.Button(pb.Key, GUILayout.Width(30))) { if (follow) { fixLat = cLat; fixLon = cLon; follow = false; } MapReveal.Pan(ref fixLat, ref fixLon, pb.Value, span, body.Radius); }
                if (GUILayout.Button(follow ? "following" : "re-center", GUILayout.Width(80))) follow = true;
                GUILayout.EndHorizontal();
            }
            if (Tab != 1) return;
            GUILayout.Label(PlaneLine(rw, NativeFlightController.ActiveRunway), Wrap);
            if (sel >= 0 && sel < fixes.Count)
            {
                var f = fixes[sel]; double g = NativeFlightController.MapTerrain(body, f.Lat, f.Lon); if (double.IsNaN(g)) g = 0;
                GUILayout.BeginHorizontal();
                GUILayout.Label(f.Name.Split('(')[0].Trim() + ": " + Math.Round(f.Ref == "msl" ? f.AltMsl : f.AltMsl - g) + " m " + f.Ref.ToUpper() + " (gnd " + Math.Round(g) + ")", GUILayout.Width(220));
                if (GUILayout.Button("-50")) f.AltMsl -= 50; if (GUILayout.Button("+50")) f.AltMsl += 50;
                if (GUILayout.Button(f.Ref == "agl" ? "->MSL" : "->AGL")) f.Ref = f.Ref == "agl" ? "msl" : "agl";
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add waypoint")) AddWp();
            if (GUILayout.Button("Remove")) { if (sel >= 0 && fixes[sel].Role != "faf" && fixes[sel].Role != "sf") { fixes.RemoveAt(sel); sel = -1; } else note = "Select a waypoint (FAF/SF are locked)."; }
            if (GUILayout.Button("Reset") && rw != null) { fixesFor = ""; note = "Reset to computed (Save to keep)."; }
            if (GUILayout.Button("Save") && rw != null) Save(rw);
            GUILayout.EndHorizontal();
            GUILayout.Label(note, Wrap);   // auto-height
        }

        /// <summary>Cockpit-style ILS for the active approach runway end (or the selected one); works hand flying too.</summary>
        void DrawIls()
        {
            var v = FlightGlobals.ActiveVessel; var act = NativeFlightController.ActiveRunway;
            NativeFlightController.MapRunway rw = runways.Count > 0 ? runways[Math.Min(rwIdx, runways.Count - 1)] : null;
            double tla, tlo, ela, elo, elev; string label;
            if (act != null) { tla = act.Lat; tlo = act.Lon; ela = act.EndLat; elo = act.EndLon; elev = act.Elevation; label = string.IsNullOrEmpty(act.Key) ? "active runway" : act.Key; }
            else if (rw != null) { tla = rw.Lat; tlo = rw.Lon; ela = rw.EndLat; elo = rw.EndLon; elev = rw.Elevation; label = rw.Key; }
            else { GUILayout.Label("No runway on this body."); return; }
            GUILayout.BeginHorizontal();
            if (act == null && GUILayout.Button("<", GUILayout.Width(24)) && runways.Count > 0) rwIdx = (rwIdx + runways.Count - 1) % runways.Count;
            GUILayout.Label("ILS " + label + (act != null ? "  (autoland active)" : "  (selected)"));
            if (act == null && GUILayout.Button(">", GUILayout.Width(24)) && runways.Count > 0) rwIdx = (rwIdx + 1) % runways.Count;
            GUILayout.EndHorizontal();
            if (v == null) return;
            var r = act != null && act.Coupled && act.Ils != null ? act.Ils : Ils.Compute(v.latitude, v.longitude, v.altitude, tla, tlo, ela, elo, elev, v.mainBody.Radius, act != null ? act.TouchdownM : 350);   // single source of truth with the autopilot
            if (act != null) GUILayout.Label(act.Coupled ? (act.GsCoupled ? "COUPLED  LOC + GS" : "COUPLED  LOC  (GS armed)") : act.Phase == "entry" || act.Phase == "intercept" ? "LOC armed" : "not coupled");
            float S = Ils.BoxSide(rect.width, rect.height, guidance); Rect box = GUILayoutUtility.GetRect(S, S, GUILayout.Width(S), GUILayout.Height(S));
            var o = GUI.color; GUI.color = new Color(.05f, .05f, .08f); GUI.DrawTexture(box, Texture2D.whiteTexture); GUI.color = o;
            Vector2 c = box.center;
            for (int i = -2; i <= 2; i++) { if (i == 0) continue; Dot(new Vector2(c.x + i * S * .2f, c.y), 5, Color.white); Dot(new Vector2(c.x, c.y + i * S * .2f), 5, Color.white); }
            Dot(c, 8, Color.yellow);
            bool valid = r.Front && r.DmeM < 40000;
            float lx = c.x - (float)Ils.Needle(r.LocDeg, Ils.LocFullScale) * S * .4f;   // needle shows where the course is: right of us -> needle right
            float gy = c.y + (float)Ils.Needle(r.GsDeg, Ils.GsFullScale) * S * .4f;    // above the slope -> GS needle below centre
            GUI.color = valid ? Color.magenta : Color.gray;
            GUI.DrawTexture(new Rect(lx - 1.5f, box.y + 10, 3, S - 20), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(box.x + 10, gy - 1.5f, S - 20, 3), Texture2D.whiteTexture);
            GUI.color = o;
            if (!valid) GUI.Label(new Rect(box.x + 8, box.y + 6, 200, 20), r.Front ? "OUT OF RANGE" : "BEHIND THE RUNWAY");
            double gs = v.horizontalSrfSpeed;
            if (act != null) GUILayout.Label("decel at " + (act.DecelStartDist / 1000).ToString("0.0") + " km (" + (double.IsNaN(act.DecelA) ? "est. " + ApproachProfile.DefaultDecel.ToString("0.0") : "measured " + act.DecelA.ToString("0.0")) + " m/s2)" + (r.DmeM > act.DecelStartDist ? "" : "  DECELERATING"));
            GUILayout.Label("DME " + (r.DmeM / 1000).ToString("0.0") + " km to threshold   GS " + Math.Round(gs) + " m/s" + (gs > 1 ? "   ETA " + Math.Round(r.DmeM / gs) + " s" : ""));
            GUILayout.Label("LOC " + r.LocDeg.ToString("+0.00;-0.00") + " deg   cross-track " + Math.Abs(r.CrossM).ToString("0.0") + " m " + (r.CrossM > 0 ? "right" : "left"));
            {   // status row + hand-flying guidance
                bool gear = v.ActionGroups[KSPActionGroup.Gear], brk = v.ActionGroups[KSPActionGroup.Brakes]; double hat = v.altitude - elev, ias = v.indicatedAirSpeed;
                double tgt = act != null && act.DesiredSpeed > 0 ? act.DesiredSpeed : 1.3 * Math.Max(30, NativeFlightController.MapStall);
                var oc = GUI.contentColor; GUILayout.BeginHorizontal();
                GUI.contentColor = !gear && r.Front && r.DmeM < 3000 ? Color.red : Color.white; GUILayout.Label("GEAR " + (gear ? "DOWN" : "UP"));
                GUI.contentColor = Color.white; GUILayout.Label(brk ? (NativeFlightController.AirbrakesByAutopilot ? "AIRBRAKES" : "BRAKES") : "brakes off");
                GUILayout.Label("VS " + v.verticalSpeed.ToString("+0;-0"));
                string band = Ils.SpeedBand(ias, tgt); GUI.contentColor = band == "green" ? Color.green : band == "amber" ? new Color(1, .7f, 0) : Color.red;
                GUILayout.Label("IAS " + Math.Round(ias) + "/" + Math.Round(tgt)); GUI.contentColor = Color.white;
                GUILayout.Label("HAT " + Math.Round(hat) + " m"); GUILayout.FlexibleSpace(); GUI.contentColor = oc; GUILayout.EndHorizontal();
                guidance = GUILayout.Toggle(guidance, "ILS guidance (hand-flying) " + (guidance ? "ON" : "OFF"));
                if (guidance && valid)
                {
                    double fh = Ils.FdHeading(r, v.srfSpeed), fv = Ils.FdVs(r, v.srfSpeed), dh = FlightPolicy.Wrap(fh - FlightGlobals.ship_heading), dv = fv - v.verticalSpeed;
                    GUILayout.Label((dh < -2 ? "<<< STEER LEFT" : dh > 2 ? "STEER RIGHT >>>" : "ON COURSE") + "   " + (dv > 2 ? "FLY UP" : dv < -2 ? "FLY DOWN" : "ON PATH") + "   HDG " + Math.Round(fh).ToString("000") + "  VS " + fv.ToString("0"), Wrap);
                    var co = Ils.Callouts(r, hat, gear, ias, tgt); if (co.Count > 0) { GUI.contentColor = new Color(1, .7f, 0); GUILayout.Label(string.Join("  ", co.ToArray())); GUI.contentColor = oc; }
                }
            }
            GUILayout.Label("G/S " + r.GsDeg.ToString("+0.00;-0.00") + " deg   " + Math.Abs(r.AboveGsM).ToString("0") + " m " + (r.AboveGsM > 0 ? "above" : "below") + " the 3 deg path   course " + Math.Round(r.Course).ToString("000"));
        }

        string planeLine = ""; float planeAt;
        /// <summary>This craft's join speed / bank / radius and which chart turns are tight for it (active route or a preview of the editor fixes).</summary>
        string PlaneLine(NativeFlightController.MapRunway rw, RunwayMission act)
        {
            if (act != null && act.PlanSpeed > 0) return "Plane " + Math.Round(act.PlanSpeed) + " m/s, " + act.PlanG.ToString("0.0") + " g (" + ApproachProfile.Limit + "), bank " + Math.Round(act.PlanBank) + ", r " + (act.PlanRadius / 1000).ToString("0.0") + " km" + (details ? "\ncrew " + ApproachProfile.CrewG.ToString("0") + " g, structure " + ApproachProfile.StructG.ToString("0.0") + " g, bank now " + Math.Round(act.TurnBank) + "\n" + act.JoinLog + "\n" + act.SmoothLog : "");
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
            planeLine = "Plane " + Math.Round(v) + " m/s, " + gN.ToString("0.0") + " g (" + ApproachProfile.Limit + "), bank " + Math.Round(b) + ", r " + (ApproachProfile.Radius(v, b) / 1000).ToString("0.0") + " km, " + (tight.Length > 0 ? "TIGHT" + (details ? ": " + tight : " (details)") : "turns fit") + (details ? "\n1.5 x stall " + Math.Round(NativeFlightController.MapStall) + " m/s" : "");
            return planeLine;
        }

        void HandleMouse(NativeFlightController.MapRunway rw)
        {
            var e = Event.current;
            if (e.type == EventType.ScrollWheel && e.mousePosition.x <= Px && e.mousePosition.y <= Px) { ViewSpan = Zoom(ViewSpan, e.delta.y > 0 ? 1.25 : 0.8); e.Use(); return; }
            if (rw == null || Tab != 1) return;
            if (e.type == EventType.MouseDown && e.button == 0 && e.mousePosition.x <= Px && e.mousePosition.y <= Px)
            {
                sel = -1; float best = 12;
                for (int i = 0; i < fixes.Count; i++) { float d = Vector2.Distance(Proj(fixes[i].Lat, fixes[i].Lon), e.mousePosition); if (d < best) { best = d; sel = i; } }
                dragging = sel >= 0; e.Use();
            }
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
            else if (e.type == EventType.MouseUp) dragging = false;
        }

        void AddWp()
        {
            int at = sel >= 0 && fixes[sel].Role != "faf" && fixes[sel].Role != "sf" ? sel + 1 : fixes.FindIndex(f => f.Role == "faf" || f.Role == "sf");
            if (at < 0) at = fixes.Count;
            var a = at > 0 ? fixes[at - 1] : fixes.Find(f => f.Role == "faf"); if (a == null) return;
            var b = at < fixes.Count && fixes[at].Role == "wp" ? fixes[at] : fixes.Find(f => f.Role == "faf") ?? a;
            int n = fixes.FindAll(f => f.Role == "wp").Count + 1;
            fixes.Insert(at, new ApproachFile.EditFix { Role = "wp", Name = "WP" + n, Side = a.Side, Ref = a.Ref, Lat = (a.Lat + b.Lat) / 2 + (a == b ? .02 : 0), Lon = (a.Lon + b.Lon) / 2, AltMsl = a.AltMsl });
            sel = at;
        }

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
