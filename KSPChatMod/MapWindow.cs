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
        const int WindowId = 0x4B434D41, N = 96; const float Px = 384;
        internal static bool MapVisible;
        static Rect rect = new Rect(420, 120, Px + 20, Px + 210);
        static readonly double[] Spans = { 10000, 30000, 80000 };
        static int spanIdx = 1, rwIdx; static bool centerRunway;
        Texture2D tex; Color[] px = new Color[N * N]; int buildRow = -1; double bLat, bLon, bSpan; float nextRebuild;
        List<NativeFlightController.MapRunway> runways = new List<NativeFlightController.MapRunway>();
        List<ApproachFile.EditFix> fixes = new List<ApproachFile.EditFix>(); string fixesFor = ""; int sel = -1; bool dragging; string note = "";
        double cLat, cLon, span; CelestialBody body;

        internal static void ToggleMap() { MapVisible = !MapVisible; }

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
            span = Spans[spanIdx];
            if (centerRunway && rw != null) { cLat = rw.Lat; cLon = rw.Lon; } else { cLat = v.latitude; cLon = v.longitude; }
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
                note = "Chart from approaches.json";
            }
            else
            {
                Action<string, string, ApproachChart.Wp> add = (role, side, w) => fixes.Add(new ApproachFile.EditFix { Role = role, Name = w.Name, Side = side, Lat = w.Lat, Lon = w.Lon, AltMsl = w.Alt });
                add("dw_left", "left", ch.DownLeftA); add("dwend_left", "left", ch.DownLeftB); add("base_left", "left", ch.ApexLeft);
                add("dw_right", "right", ch.DownRightA); add("dwend_right", "right", ch.DownRightB); add("base_right", "right", ch.ApexRight);
                add("faf", "both", ch.Long); add("sf", "both", ch.Short);
                note = why.Length > 0 ? "approaches.json invalid (" + why + "); computed chart" : "Computed chart (not saved yet)";
            }
        }

        void OnGUI() { try { OnGUIInner(); } catch (System.Exception ex) { GuiGuard.Log(GetType().Name, ex); } }

        void OnGUIInner()
        {
            if (!MapVisible || body == null) return;
            var skin = AicsMenu.EnsureSkin(); if (skin != null) GUI.skin = skin;
            rect.x = Mathf.Clamp(rect.x, 0, Mathf.Max(0, Screen.width - 80)); rect.y = Mathf.Clamp(rect.y, 0, Mathf.Max(0, Screen.height - 40));
            rect = GUI.Window(WindowId, rect, Draw, "AICS Map & Charts  (x = close)");
        }

        static void Dot(Vector2 p, float s, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(p.x - s / 2, p.y - s / 2, s, s), Texture2D.whiteTexture); GUI.color = o; }
        static void Line(Vector2 a, Vector2 b, Color c, float step = 4)
        {
            float d = Vector2.Distance(a, b); int n = Mathf.Min(400, Mathf.Max(1, (int)(d / step)));
            for (int i = 0; i <= n; i++) { var p = Vector2.Lerp(a, b, i / (float)n); if (p.x >= 0 && p.y >= 0 && p.x <= Px && p.y <= Px) Dot(p, 2, c); }
        }

        void Draw(int id)
        {
            if (GUI.Button(new Rect(rect.width - 22, 2, 20, 16), "x")) { MapVisible = false; return; }
            try { DrawInner(); } catch (Exception ex) { GuiGuard.Log("MapWindow.Draw", ex); }
            GUI.DragWindow();
        }

        void DrawInner()
        {
            var rw = runways.Count > 0 ? runways[Math.Min(rwIdx, runways.Count - 1)] : null;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(24)) && runways.Count > 0) { rwIdx = (rwIdx + runways.Count - 1) % runways.Count; fixesFor = ""; }
            GUILayout.Label(rw != null ? rw.Key : "no runway", GUILayout.Width(110));
            if (GUILayout.Button(">", GUILayout.Width(24)) && runways.Count > 0) { rwIdx = (rwIdx + 1) % runways.Count; fixesFor = ""; }
            if (GUILayout.Button((Spans[spanIdx] / 1000) + " km", GUILayout.Width(56))) { spanIdx = (spanIdx + 1) % Spans.Length; }
            centerRunway = GUILayout.Toggle(centerRunway, "center rwy");
            GUILayout.EndHorizontal();
            Rect map = GUILayoutUtility.GetRect(Px, Px, GUILayout.Width(Px), GUILayout.Height(Px));
            if (tex != null) GUI.DrawTexture(map, tex);
            GUI.BeginGroup(map);
            try {
            foreach (var r in runways) Line(Proj(r.Lat, r.Lon), Proj(r.EndLat, r.EndLon), Color.black, 1);
            foreach (var a in MapReveal.Airports) if (body.bodyName == "Kerbin") { var p = Proj(a.Lat, a.Lon); if (p.x > 0 && p.y > 0 && p.x < Px && p.y < Px) GUI.Label(new Rect(p.x + 4, p.y - 8, 140, 18), a.Pad ? "▲ " + a.Name : a.Name); }
            // chart legs: each side's join list, then FAF -> SF -> threshold
            if (rw != null && fixes.Count > 0)
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
            if (act != null && act.Route != null) for (int i = Math.Max(0, act.RouteIndex); i < act.Route.Count; i++) Dot(Proj(act.Route[i].Lat, act.Route[i].Lon), 5, Color.green);
            var v = FlightGlobals.ActiveVessel;
            if (v != null) { var p = Proj(v.latitude, v.longitude); Dot(p, 8, Color.red); double hd = FlightGlobals.ship_heading * Math.PI / 180; Line(p, p + new Vector2((float)Math.Sin(hd), -(float)Math.Cos(hd)) * 18, Color.red, 2); }
            HandleMouse(rw);
            } finally { GUI.EndGroup(); }
            GUILayout.Label((ScanSatLink.Installed ? "Mapping: SCANsat coverage" : "Mapping: flight path (no mapping mod), " + MapReveal.Count(body.bodyName) + " tiles") + "  |  airports always shown");
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
            GUILayout.Label(note);
        }

        void HandleMouse(NativeFlightController.MapRunway rw)
        {
            var e = Event.current; if (rw == null) return;
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
                string path = NativeFlightController.ApproachPath, old = File.Exists(path) ? File.ReadAllText(path) : "";
                if (old.Length > 0) File.Copy(path, path + ".bak", true);
                AtomicFile.Write(path, ApproachFile.Merge(old, rw.Key, fixes, (la, lo) => NativeFlightController.MapTerrain(body, la, lo)));
                note = "Saved " + rw.Key + " to approaches.json (used on the next landing).";
                ChatLog.Write("approach", "map editor saved " + rw.Key + " (" + fixes.Count + " fixes)");
            }
            catch (Exception ex) { note = "Save failed: " + ex.Message; }
        }
    }
}
