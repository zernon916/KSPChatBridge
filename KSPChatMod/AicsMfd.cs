// AICS MFD (Luke 4:22 / 4:29 PM): ONE movable, resizable control panel that replaces the old overhead AICS menu bar, its item
// windows and the Map & Charts window. Procedural bezel + soft keys, green-on-black monospace screen. Navigation: MfdNav.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        const int MfdId = 0x4B434D46;
        static Rect mfdRect = new Rect(-1, 60, 780, 600);
        const float MfdMinW = 640, MfdMinH = 480;
        static string mfdGroup, mfdItem; static int mfdPage; static bool mfdResizing;
        static Vector2 mfdScroll; static string mfdNote = ""; static float mfdNoteAt;
        static GUIStyle keyStyle, keyOff, scrHead, scrText; static GUISkin mfdSkin; static Texture2D bezelTex, screwTex, keyTex, keyHot, keyDown;
        static Font mono;
        static readonly Color Phos = new Color(.35f, 1f, .45f), PhosDim = new Color(.12f, .35f, .16f);

        static bool HasMj { get { if (mj == null) mj = AssemblyLoader.loadedAssemblies.Any(a => a.name.StartsWith("MechJeb2")); return mj.Value; } }
        static MfdNav.Group CurGroup { get { return mfdGroup == null ? null : MfdNav.Groups(HasMj).FirstOrDefault(g => g.Id == mfdGroup); } }
        static MfdNav.Item CurItem { get { var g = CurGroup; return g == null ? null : g.Items.FirstOrDefault(i => i.Id == mfdItem) ?? g.Items[0]; } }

        /// <summary>Open the MFD on an item (e.g. MapWindow.ToggleMap -> "map").</summary>
        internal static void ShowMfdItem(string item)
        {
            foreach (var g in MfdNav.Groups(HasMj)) { int i = g.Items.FindIndex(x => x.Id == item); if (i < 0) continue; mfdGroup = g.Id; mfdItem = item; mfdPage = i / MfdNav.Side; Expanded = true; Save(); return; }
        }
        /// <summary>Localizer capture on autoland: if the MAP group is showing, switch to the ILS page (once per approach).</summary>
        internal static void AutoIls() { if (Expanded && mfdGroup == "map") mfdItem = "ils"; }

        /// <summary>Panel polling flags follow the page on screen.</summary>
        static void SyncOpen()
        {
            var it = Expanded ? CurItem : null;
            for (int i = 0; i < Open.Length; i++) Open[i] = it != null && it.Panel == i;
            MapWindow.MapVisible = Expanded && mfdGroup == "map" && HighLogic.LoadedSceneIsFlight;
        }

        static Texture2D Noise(int n, Color a, Color b, int seed)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat }; var rnd = new System.Random(seed);
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) { float k = (float)rnd.NextDouble() * .5f + .5f * y / n; t.SetPixel(x, y, Color.Lerp(a, b, k)); }
            t.Apply(); return t;
        }
        static Texture2D KeyTex(Color top, Color bottom, Color edge)
        {
            const int n = 24; var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) { bool e = x == 0 || y == 0 || x == n - 1 || y == n - 1; t.SetPixel(x, y, e ? edge : Color.Lerp(bottom, top, y / (float)n)); }
            t.Apply(); return t;
        }
        static Texture2D Screw()
        {
            const int n = 16; var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float dx = x - 7.5f, dy = y - 7.5f, r = Mathf.Sqrt(dx * dx + dy * dy);
                Color c = r > 7.5f ? Color.clear : Mathf.Abs(dx + dy) < 1.2f ? new Color(.12f, .12f, .13f) : Color.Lerp(new Color(.62f, .63f, .66f), new Color(.3f, .3f, .32f), (dy + 8) / 16f);
                t.SetPixel(x, y, c);
            }
            t.Apply(); return t;
        }

        static void BuildMfdSkin()
        {
            if (mono == null) mono = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New", "DejaVu Sans Mono", "Liberation Mono" }, 14);
            bezelTex = Noise(64, new Color(.17f, .18f, .19f), new Color(.25f, .26f, .27f), 7); screwTex = Screw();
            keyTex = KeyTex(new Color(.30f, .31f, .33f), new Color(.18f, .19f, .2f), new Color(.08f, .08f, .09f));
            keyHot = KeyTex(new Color(.36f, .38f, .40f), new Color(.22f, .23f, .25f), new Color(.35f, .9f, .4f));
            keyDown = KeyTex(new Color(.14f, .15f, .16f), new Color(.22f, .23f, .25f), new Color(.35f, .9f, .4f));
            keyStyle = new GUIStyle(GUI.skin.button) { font = mono, fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            keyStyle.normal.background = keyTex; keyStyle.hover.background = keyHot; keyStyle.active.background = keyDown;
            keyStyle.normal.textColor = keyStyle.hover.textColor = keyStyle.active.textColor = new Color(.85f, .9f, .85f);
            keyOff = new GUIStyle(keyStyle); keyOff.hover.background = keyOff.active.background = keyTex;
            mfdSkin = UnityEngine.Object.Instantiate(skin);
            foreach (var st in new[] { mfdSkin.label, mfdSkin.button, mfdSkin.toggle, mfdSkin.textField, mfdSkin.textArea, mfdSkin.box })
            { st.font = mono; st.fontSize = 13; st.normal.textColor = st.hover.textColor = st.onNormal.textColor = st.onHover.textColor = Phos; }
            scrHead = new GUIStyle(mfdSkin.label) { fontStyle = FontStyle.Bold, fontSize = 15 }; scrText = new GUIStyle(mfdSkin.label) { wordWrap = true };
        }

        void DrawMfdWindow()
        {
            if (mfdSkin == null) BuildMfdSkin();
            if (mfdRect.x < 0) mfdRect.x = Mathf.Max(0, Screen.width - mfdRect.width - 40);
            mfdRect.width = Mathf.Clamp(mfdRect.width, MfdMinW, Screen.width); mfdRect.height = Mathf.Clamp(mfdRect.height, MfdMinH, Screen.height);
            SyncOpen();
            mfdRect = GUI.Window(MfdId, mfdRect, DrawMfd, "", GUIStyle.none);
        }

        void DrawMfd(int id)
        {
            float W = mfdRect.width, H = mfdRect.height, m = 14, kw = 96, bh = 46, head = 26;
            GUI.color = new Color(1, 1, 1, Mathf.Max(.75f, opacity)); GUI.DrawTextureWithTexCoords(new Rect(0, 0, W, H), bezelTex, new Rect(0, 0, W / 64, H / 64)); GUI.color = Color.white;
            Frame(new Rect(0, 0, W, H), new Color(.4f, .41f, .43f), new Color(.07f, .07f, .08f));
            foreach (var p in new[] { new Vector2(5, 5), new Vector2(W - 21, 5), new Vector2(5, H - 21), new Vector2(W - 21, H - 21) }) GUI.DrawTexture(new Rect(p.x, p.y, 16, 16), screwTex);
            Rect scr = new Rect(m + kw + 10, m + head + 6, W - 2 * (m + kw + 10), H - (m + head + 6) - (bh + m + 10));
            // header strip on the bezel: drag handle, title, CHAT, close
            var g = CurGroup; var it = g == null ? null : CurItem;
            string title = "AICS  " + (g == null ? "HOME" : g.Label + (it != null && it.Label != g.Label ? " / " + it.Label : "") + (g.Id == "map" && MapWindow.Title != "" ? "  - " + MapWindow.Title : ""));
            GUI.Label(new Rect(m + 22, m - 4, W - 2 * m - 200, head), title, scrHead);
            bool chatOn = ChatWindow.IsVisible;
            if (GUI.Button(new Rect(W - m - 22 - 140, m - 4, 80, head - 2), chatOn ? "CHAT \u25CF" : "CHAT", keyStyle)) ChatWindow.ToggleChat();
            if (GUI.Button(new Rect(W - m - 22 - 52, m - 4, 50, head - 2), "\u2715", keyStyle)) { Expanded = false; Save(); }
            // screen
            GUI.color = Color.black; GUI.DrawTexture(scr, Texture2D.whiteTexture); GUI.color = new Color(PhosDim.r, PhosDim.g, PhosDim.b, .35f);
            for (float x = scr.x + 40; x < scr.xMax; x += 40) GUI.DrawTexture(new Rect(x, scr.y, 1, scr.height), Texture2D.whiteTexture);
            for (float y = scr.y + 40; y < scr.yMax; y += 40) GUI.DrawTexture(new Rect(scr.x, y, scr.width, 1), Texture2D.whiteTexture);
            GUI.color = Color.white; Frame(new Rect(scr.x - 3, scr.y - 3, scr.width + 6, scr.height + 6), new Color(.05f, .05f, .05f), new Color(.38f, .39f, .41f));
            var saved = GUI.skin; GUI.skin = mfdSkin; GUI.contentColor = Phos;
            try { DrawScreen(new Rect(scr.x + 6, scr.y + 4, scr.width - 12, scr.height - 8)); }
            catch (Exception ex) { GUI.Label(new Rect(scr.x + 8, scr.yMax - 24, scr.width, 20), "page error: " + ex.Message); }
            GUI.skin = saved; GUI.contentColor = Color.white;
            // soft keys
            string[] left, right; bool home = g == null; var items = home ? null : MfdNav.PageItems(g, mfdPage);
            if (home) { var hk = MfdNav.HomeKeys(HasMj); left = hk.Take(6).ToArray(); right = hk.Skip(6).ToArray(); }
            else { left = new string[6]; for (int i = 0; i < items.Count; i++) left[i] = (it != null && items[i].Id == it.Id ? "\u25B8" : "") + items[i].Label + Indicator(items[i]); right = MfdNav.Right(it.Id); }
            float kh = (scr.height - 5 * 8) / 6;
            for (int i = 0; i < 6; i++)
            {
                float y = scr.y + i * (kh + 8);
                if (Key(new Rect(m, y, kw, kh), left[i])) { if (home) OpenGroup(i); else { mfdItem = items[i].Id; mfdScroll = Vector2.zero; Save(); } }
                if (Key(new Rect(W - m - kw, y, kw, kh), i < right.Length ? right[i] : null)) { if (home) OpenGroup(6 + i); else OnKey(it.Id, right[i]); }
            }
            var bottom = MfdNav.Bottom(home, it == null ? null : it.Id, mfdPage, home ? 1 : MfdNav.Pages(g));
            float bw = (W - 2 * m - 6 * 8) / MfdNav.BottomCount;
            for (int i = 0; i < MfdNav.BottomCount; i++)
            {
                if (!Key(new Rect(m + i * (bw + 8), H - m - bh, bw, bh), bottom[i])) continue;
                if (i == MfdNav.BackKey) { mfdGroup = null; mfdItem = null; mfdPage = 0; Save(); }
                else if (i == MfdNav.PrevKey && bottom[i] == "PREV") { mfdPage--; mfdItem = MfdNav.PageItems(g, mfdPage)[0].Id; }
                else if (i == MfdNav.NextKey && bottom[i] == "NEXT") { mfdPage++; mfdItem = MfdNav.PageItems(g, mfdPage)[0].Id; }
                else OnKey(it.Id, bottom[i]);
            }
            // resize grip (bottom-right screw corner) + drag (top bezel strip)
            Rect grip = new Rect(W - 22, H - 22, 22, 22); Event e = Event.current;
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition)) { mfdResizing = true; e.Use(); }
            if (mfdResizing)
            {
                if (e.type == EventType.MouseDrag) { mfdRect.width = Mathf.Max(MfdMinW, e.mousePosition.x + 4); mfdRect.height = Mathf.Max(MfdMinH, e.mousePosition.y + 4); e.Use(); }
                if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp) { mfdResizing = false; Save(); }
            }
            GUI.DragWindow(new Rect(0, 0, W, m + head - 4));
        }

        static void Frame(Rect r, Color hi, Color lo)
        {
            var o = GUI.color;
            GUI.color = hi; GUI.DrawTexture(new Rect(r.x, r.y, r.width, 2), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(r.x, r.y, 2, r.height), Texture2D.whiteTexture);
            GUI.color = lo; GUI.DrawTexture(new Rect(r.x, r.yMax - 2, r.width, 2), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(r.xMax - 2, r.y, 2, r.height), Texture2D.whiteTexture);
            GUI.color = o;
        }
        static bool Key(Rect r, string label)
        {
            if (string.IsNullOrEmpty(label)) { GUI.Box(r, "", keyOff); return false; }
            return GUI.Button(r, label, keyStyle);
        }
        static string Indicator(MfdNav.Item it)
        {
            if (it.Panel < 0 || it.Panel >= IndKeys.Length || IndKeys[it.Panel] == null) return "";
            return StatusWindow.Active(IndKeys[it.Panel]) ? " \u25CF" : "";
        }
        static void OpenGroup(int i)
        {
            var gs = MfdNav.Groups(HasMj); if (i >= gs.Count) return;
            mfdGroup = gs[i].Id; mfdPage = 0; mfdItem = gs[i].Items[0].Id; mfdScroll = Vector2.zero; Save();
        }
        static void Note(string s) { mfdNote = s ?? ""; mfdNoteAt = Time.realtimeSinceStartup + 8; }
        static void Tool(string name, string args) { ChatWindow.ToolFromMenu(name, args ?? "{}"); Note(name.Replace('_', ' ') + " sent (result in chat)."); }

        void DrawScreen(Rect area)
        {
            var g = CurGroup; var it = CurItem;
            if (Time.realtimeSinceStartup < mfdNoteAt && mfdNote.Length > 0) { GUI.Label(new Rect(area.x, area.yMax - 40, area.width, 40), mfdNote, scrText); area.height -= 42; }
            if (g == null) { DrawHome(area); return; }
            if (g.Id == "map")
            {
                if (!HighLogic.LoadedSceneIsFlight || MapWindow.Inst == null) { GUI.Label(area, "Map, charts and ILS are available in flight.", scrText); return; }
                MapWindow.Inst.DrawEmbedded(area, it.Id == "map" ? 0 : it.Id == "chart" ? 1 : 2); return;
            }
            GUILayout.BeginArea(area); mfdScroll = GUILayout.BeginScrollView(mfdScroll);
            try
            {
                if (it.Panel >= 0)
                {
                    bool ok = LocalOnly[it.Panel] || DepsOk; if (!ok) DepsWarning();
                    GUI.enabled = ok; GUILayout.Label(Items[it.Panel].ToUpperInvariant(), scrHead); DrawPanel(it.Panel); GUI.enabled = true;
                }
                else DrawCustom(it.Id);
            }
            finally { GUI.enabled = true; GUILayout.EndScrollView(); GUILayout.EndArea(); }
        }

        void DrawHome(Rect area)
        {
            GUILayout.BeginArea(area);
            GUILayout.Label("AICS  CONTROL", scrHead);
            GUILayout.Label("Mode   " + NativeFlightController.Phase.ToUpperInvariant());
            var act = NativeFlightController.ActiveRunway; if (act != null) GUILayout.Label("Rwy    " + act.Key + "  " + act.Phase + "  " + (act.Distance / 1000).ToString("0.0") + " km");
            GUILayout.Label("Plan   " + NativeFlightController.PlanStatus);
            var v = FlightGlobals.ActiveVessel;
            if (HighLogic.LoadedSceneIsFlight && v != null) GUILayout.Label(v.vesselName + "\nALT " + Math.Round(v.altitude) + " m   SPD " + Math.Round(v.srfSpeed) + " m/s   HDG " + Math.Round(FlightGlobals.ship_heading).ToString("000") + "\n" + v.situation);
            DepsWarning();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Pick a group with the soft keys.  BACK (bottom-left) returns here.  Alt+J hides the MFD; right-click the toolbar button too.", scrText);
            GUILayout.EndArea();
        }

        void DrawCustom(string id)
        {
            switch (id)
            {
                case "trim": GUILayout.Label("Trim panel: TRIM WIN opens the trim window (pitch / roll / yaw trim, auto-trim).", scrText); GUILayout.Label("Trim window " + (TrimWindow.TrimVisible ? "OPEN" : "closed")); break;
                case "status": GUILayout.Label("Live autopilot state window: " + (StatusWindow.StatusVisible ? "OPEN" : "closed") + ".  TOGGLE opens / closes it.", scrText); break;
                case "systems": GUILayout.Label("Parts / emergency dashboard: " + (StatusWindow.SystemsVisible ? "OPEN" : "closed") + ".  TOGGLE opens / closes it.", scrText); break;
                case "mjatt":
                    GUILayout.Label("MECHJEB SMARTASS", scrHead); GUILayout.Label(NativeFlightController.MjStatusNow, scrText);
                    GUILayout.Label("Right keys: prograde / retrograde / normal / radial.  Bottom: KILLROT, NODE, TARGET+, OFF.", scrText); break;
                case "mjguide":
                    GUILayout.Label("MECHJEB GUIDANCE", scrHead); GUILayout.Label(NativeFlightController.MjStatusNow, scrText);
                    GUILayout.Label("ASCENT 80 km orbit, LAND (target if set), EXEC NODE, RENDEZV + DOCK with the target, AIRCRAFT hold (current hdg/alt/speed), SPACEPLN autoland, ALL OFF.", scrText); break;
            }
        }

        void OnKey(string item, string key)
        {
            if (key == null) return;
            switch (item)
            {
                case "map": case "chart": case "ils":
                    switch (key)
                    {
                        case "ZOOM+": MapWindow.ZoomIn(); break; case "ZOOM-": MapWindow.ZoomOut(); break; case "CENTER": MapWindow.Recenter(); break;
                        case "RWY <": MapWindow.RunwayStep(-1); break; case "RWY >": MapWindow.RunwayStep(1); break; case "CTR RWY": MapWindow.ToggleCenterRunway(); break;
                        case "\u2190": MapWindow.PanView(270); break; case "\u2193": MapWindow.PanView(180); break; case "\u2191": MapWindow.PanView(0); break; case "\u2192": MapWindow.PanView(90); break;
                        case "VARIANT": MapWindow.NextVariant(); break; case "ADD WP": MapWindow.AddWaypoint(); break; case "REMOVE": MapWindow.RemoveWaypoint(); break;
                        case "SAVE": MapWindow.SaveChart(); break; case "RESET": MapWindow.ResetChart(); break; case "DETAILS": MapWindow.ToggleDetails(); break; case "GUIDE": MapWindow.ToggleGuidance(); break;
                    }
                    return;
                case "mjatt": { string m = MfdNav.SmartMode(key); if (m != null) Tool("mj_smartass", "{\"mode\":\"" + m + "\"}"); return; }
                case "mjguide":
                    switch (key)
                    {
                        case "ASCENT": Tool("mechjeb_ascent", "{}"); break; case "LAND": Tool("mj_land", "{}"); break; case "EXEC NODE": Tool("mj_node", "{}"); break;
                        case "RENDEZV": Tool("mj_rendezvous", "{}"); break; case "DOCK": Tool("dock_with", "{}"); break; case "AIRCRAFT": Tool("mj_aircraft", "{}"); break;
                        case "SPACEPLN": Tool("mj_spaceplane", "{}"); break; case "STATUS": Tool("mj_status", "{}"); break; case "ALL OFF": Tool("mj_off", "{}"); break;
                    }
                    return;
                case "taxi":
                    switch (key)
                    {
                        case "HANGAR": Tool("taxi_route", "{\"dest\":\"hangar\"}"); break; case "RWY 09": Tool("taxi_route", "{\"dest\":\"runway 09\"}"); break;
                        case "RWY 27": Tool("taxi_route", "{\"dest\":\"runway 27\"}"); break; case "STOP": Tool("stop_current", "{}"); break;
                    }
                    return;
                case "trim": if (key == "TRIM WIN") TrimWindow.ToggleTrim(); else Tool("auto_trim_now", "{}"); return;
                case "status": StatusWindow.ToggleStatus(); return;
                case "systems": StatusWindow.ToggleSystems(); return;
                case "flightplan":
                    if (key == "STOP") PlanPost("flightplan/stop", "{}", true);
                    else PlanPost(key == "FLY" ? "flightplan/fly" : "flightplan/check", "{\"plan\":" + ChatWindow.JsonStr(planText) + "}", key == "FLY");
                    return;
            }
            switch (key)
            {
                case "TAKEOFF": Tool("takeoff", "{}"); break; case "LAND": Tool("land", "{}"); break; case "GO AROUND": Tool("go_around", "{}"); break;
                case "ABORT": Tool("abort", "{}"); break; case "STOP": Tool("stop_current", "{}"); break; case "STATUS": Tool("autopilot_status", "{}"); break;
            }
        }
    }
}