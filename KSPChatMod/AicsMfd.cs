// AICS MFD (Luke 4:22 / 4:29 / 4:50 PM): ONE movable, resizable control panel replacing the old overhead menu bar, its item
// windows, the Map & Charts window, the status / systems pop-ups and (optionally) the chat window. Procedural bezel with an
// engraved title plate, MASTER ALARM annunciator, 8 keys per side + 8 bottom + a spare top row; green-on-black monospace
// screen; everything scales uniformly with the window. Layout: IvaLayout (shared with the IVA screen), navigation: MfdNav.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        const int MfdId = 0x4B434D46;
        static Rect mfdRect = new Rect(-1, 60, 800, 700);
        const float MfdMinW = 560, MfdMinH = 490;
        static string mfdGroup, mfdItem; static int mfdPage; static bool mfdResizing;
        static Vector2 mfdScroll; static string mfdNote = ""; static float mfdNoteAt;
        static string commsText = "";
        static GUIStyle keyStyle, keyOff, scrHead, scrText, plateStyle, plateShadow, lampStyle; static GUISkin mfdSkin; static Texture2D bezelTex, screwTex, keyTex, keyHot, keyDown, plateTex;
        static Font mono; static float mfdScale = -1;
        static readonly Color Phos = new Color(.35f, 1f, .45f), PhosDim = new Color(.12f, .35f, .16f);

        static bool HasMj { get { if (mj == null) mj = AssemblyLoader.loadedAssemblies.Any(a => a.name.StartsWith("MechJeb2")); return mj.Value; } }
        static MfdNav.Group CurGroup { get { return mfdGroup == null ? null : MfdNav.Groups(HasMj).FirstOrDefault(g => g.Id == mfdGroup); } }
        static MfdNav.Item CurItem { get { var g = CurGroup; return g == null ? null : g.Items.FirstOrDefault(i => i.Id == mfdItem) ?? g.Items[0]; } }

        /// <summary>Open the MFD on an item (e.g. MapWindow.ToggleMap -> "map", ToggleSystems -> "systems").</summary>
        internal static void ShowMfdItem(string item)
        {
            foreach (var g in MfdNav.Groups(HasMj)) { int i = g.Items.FindIndex(x => x.Id == item); if (i < 0) continue; mfdGroup = g.Id; mfdItem = item; mfdPage = i / MfdNav.Side; Expanded = true; Save(); return; }
        }
        /// <summary>Localizer capture on autoland: if the MAP group is showing, switch to the ILS page (once per approach).</summary>
        internal static void AutoIls() { if (Expanded && mfdGroup == "map") mfdItem = "ils"; }

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
                t.SetPixel(x, y, r > 7.5f ? Color.clear : Mathf.Abs(dx + dy) < 1.2f ? new Color(.12f, .12f, .13f) : Color.Lerp(new Color(.62f, .63f, .66f), new Color(.3f, .3f, .32f), (dy + 8) / 16f));
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
            plateTex = Noise(32, new Color(.50f, .51f, .53f), new Color(.62f, .63f, .65f), 3);
            keyStyle = new GUIStyle(GUI.skin.button) { font = mono, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true, clipping = TextClipping.Clip, padding = new RectOffset(2, 2, 2, 2) };
            keyStyle.normal.background = keyTex; keyStyle.hover.background = keyHot; keyStyle.active.background = keyDown;
            keyStyle.normal.textColor = keyStyle.hover.textColor = keyStyle.active.textColor = new Color(.85f, .9f, .85f);
            keyOff = new GUIStyle(keyStyle); keyOff.hover.background = keyOff.active.background = keyTex;
            lampStyle = new GUIStyle(keyStyle);
            plateStyle = new GUIStyle(GUI.skin.label) { font = mono, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
            plateStyle.normal.textColor = new Color(.13f, .13f, .14f);
            plateShadow = new GUIStyle(plateStyle); plateShadow.normal.textColor = new Color(.85f, .86f, .88f, .55f);   // engraved: light lower edge
            mfdSkin = UnityEngine.Object.Instantiate(skin);
            foreach (var st in new[] { mfdSkin.label, mfdSkin.button, mfdSkin.toggle, mfdSkin.textField, mfdSkin.textArea, mfdSkin.box })
            { st.font = mono; st.normal.textColor = st.hover.textColor = st.onNormal.textColor = st.onHover.textColor = Phos; }
            scrHead = new GUIStyle(mfdSkin.label) { fontStyle = FontStyle.Bold }; scrText = new GUIStyle(mfdSkin.label) { wordWrap = true };
            mfdScale = -1;
        }

        /// <summary>Uniform font scale with the window (no squashed text: glyphs keep their aspect, long text wraps).</summary>
        static void ApplyScale(float k)
        {
            if (Mathf.Abs(k - mfdScale) < .01f) return; mfdScale = k; int f = Mathf.RoundToInt(13 * k);
            foreach (var st in new[] { mfdSkin.label, mfdSkin.button, mfdSkin.toggle, mfdSkin.textField, mfdSkin.textArea, mfdSkin.box, scrText }) st.fontSize = f;
            scrHead.fontSize = Mathf.RoundToInt(15 * k); plateStyle.fontSize = plateShadow.fontSize = Mathf.RoundToInt(14 * k);
        }

        void DrawMfdWindow()
        {
            if (mfdSkin == null) BuildMfdSkin();
            if (mfdRect.x < 0) mfdRect.x = Mathf.Max(0, Screen.width - mfdRect.width - 40);
            mfdRect.width = Mathf.Clamp(mfdRect.width, MfdMinW, Screen.width); mfdRect.height = Mathf.Clamp(mfdRect.height, MfdMinH, Screen.height);
            mfdRect = GUI.Window(MfdId, mfdRect, DrawScreen, "", GUIStyle.none);
            for (int i = 1; i < ScreenCount; i++)
            {
                var s = extra[i]; if (s.R.x < 0) s.R.x = Mathf.Max(0, Screen.width - s.R.width - 40 - 60 * i);
                s.R.width = Mathf.Clamp(s.R.width, MfdMinW, Screen.width); s.R.height = Mathf.Clamp(s.R.height, MfdMinH, Screen.height);
                s.R = GUI.Window(MfdId + i, s.R, DrawScreen, "", GUIStyle.none);
            }
            bool anyMap = mfdGroup == "map"; for (int i = 1; i < ScreenCount; i++) anyMap |= extra[i].G == "map";
            MapWindow.MapVisible = Expanded && anyMap && HighLogic.LoadedSceneIsFlight;
        }

        // ---- External MFD screens 1/2/3 (Luke 5:53 PM): screen 0 lives in the mfd* statics; screens 1-2 swap their own
        // page/rect/scroll into those statics only while their (deferred) window callback runs, so every page is independent.
        internal sealed class ScreenState { internal Rect R; internal string G, I; internal int P; internal Vector2 S; }
        internal static int ScreenCount = 1; static bool inExtra, savePending;
        static readonly ScreenState[] extra = { null, new ScreenState { R = new Rect(20, 80, 640, 560), G = "map", I = "ils" }, new ScreenState { R = new Rect(20, 660, 640, 520), G = "comms", I = "all" } };
        void DrawScreen(int id)
        {
            int i = id - MfdId; if (i <= 0 || i >= extra.Length) { SyncOpen(); DrawMfd(id); return; }
            var s = extra[i];
            Rect r0 = mfdRect; string g0 = mfdGroup, i0 = mfdItem; int p0 = mfdPage; Vector2 sc0 = mfdScroll;
            mfdRect = s.R; mfdGroup = s.G; mfdItem = s.I; mfdPage = s.P; mfdScroll = s.S;
            inExtra = true; try { SyncOpen(); DrawMfd(id); }
            finally
            {
                s.R.width = mfdRect.width; s.R.height = mfdRect.height; s.G = mfdGroup; s.I = mfdItem; s.P = mfdPage; s.S = mfdScroll;
                mfdRect = r0; mfdGroup = g0; mfdItem = i0; mfdPage = p0; mfdScroll = sc0; inExtra = false;
                if (savePending) { savePending = false; Save(); }
            }
        }
        static bool OverAnyScreen(Vector2 m) { if (mfdRect.Contains(m)) return true; for (int i = 1; i < ScreenCount; i++) if (extra[i].R.Contains(m)) return true; return false; }
        static string ExtraSave()
        {
            var b = new System.Text.StringBuilder(); b.Append("screens=" + ScreenCount + "\n");
            for (int i = 1; i < extra.Length; i++) { var s = extra[i]; b.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "s{0}={1},{2},{3},{4},{5},{6},{7}\n", i, s.R.x, s.R.y, s.R.width, s.R.height, s.G ?? "", s.I ?? "", s.P)); }
            return b.ToString();
        }
        static void ExtraLoad(string key, string val)
        {
            if (key == "screens") { int n; if (int.TryParse(val.Trim(), out n)) ScreenCount = Mathf.Clamp(n, 1, 3); return; }
            if (key.Length != 2 || key[0] != 's') return; int k = key[1] - '0'; if (k < 1 || k >= extra.Length) return;
            var a = val.Split(','); if (a.Length < 7) return; var ci = System.Globalization.CultureInfo.InvariantCulture; float x, y, w, h; int pg;
            if (!float.TryParse(a[0], System.Globalization.NumberStyles.Float, ci, out x) || !float.TryParse(a[1], System.Globalization.NumberStyles.Float, ci, out y) || !float.TryParse(a[2], System.Globalization.NumberStyles.Float, ci, out w) || !float.TryParse(a[3], System.Globalization.NumberStyles.Float, ci, out h)) return;
            int.TryParse(a[6], out pg); extra[k] = new ScreenState { R = new Rect(x, y, Mathf.Max(MfdMinW, w), Mathf.Max(MfdMinH, h)), G = a[4].Length == 0 ? null : a[4], I = a[5].Length == 0 ? null : a[5], P = pg };
        }

        static string Title()
        {
            var g = CurGroup; var it = g == null ? null : CurItem;
            return "AICS  " + (g == null ? "HOME" : g.Label + (it != null && it.Label != g.Label ? " / " + it.Label : "") + (g.Id == "map" && MapWindow.Title != "" ? "  " + MapWindow.Title : ""));
        }

        void DrawMfd(int id)
        {
            float W = mfdRect.width, H = mfdRect.height, k = IvaLayout.Scale(W, H); ApplyScale(k);
            GUI.color = new Color(1, 1, 1, Mathf.Max(.75f, opacity)); GUI.DrawTextureWithTexCoords(new Rect(0, 0, W, H), bezelTex, new Rect(0, 0, W / 64, H / 64)); GUI.color = Color.white;
            Frame(new Rect(0, 0, W, H), new Color(.4f, .41f, .43f), new Color(.07f, .07f, .08f));
            foreach (var p in new[] { new Vector2(5, 5), new Vector2(W - 21, 5), new Vector2(5, H - 21), new Vector2(W - 21, H - 21) }) GUI.DrawTexture(new Rect(p.x, p.y, 16, 16), screwTex);
            // engraved title plate in the bezel housing
            var pb = IvaLayout.Plate(W, H); Rect plate = new Rect(pb.X, pb.Y, pb.Wd, pb.Ht);
            Frame(new Rect(plate.x - 2, plate.y - 2, plate.width + 4, plate.height + 4), new Color(.06f, .06f, .07f), new Color(.45f, .46f, .48f));
            GUI.DrawTextureWithTexCoords(plate, plateTex, new Rect(0, 0, plate.width / 32, 1));
            string title = Title();
            GUI.Label(new Rect(plate.x, plate.y + 1, plate.width, plate.height), title, plateShadow); GUI.Label(plate, title, plateStyle);
            // screen
            var sb = IvaLayout.ScreenOf(W, H); Rect scr = new Rect(sb.X, sb.Y, sb.Wd, sb.Ht);
            GUI.color = Color.black; GUI.DrawTexture(scr, Texture2D.whiteTexture); GUI.color = new Color(PhosDim.r, PhosDim.g, PhosDim.b, .35f);
            float grid = 40 * k;
            for (float x = scr.x + grid; x < scr.xMax; x += grid) GUI.DrawTexture(new Rect(x, scr.y, 1, scr.height), Texture2D.whiteTexture);
            for (float y = scr.y + grid; y < scr.yMax; y += grid) GUI.DrawTexture(new Rect(scr.x, y, scr.width, 1), Texture2D.whiteTexture);
            GUI.color = Color.white; Frame(new Rect(scr.x - 3, scr.y - 3, scr.width + 6, scr.height + 6), new Color(.05f, .05f, .05f), new Color(.38f, .39f, .41f));
            var saved = GUI.skin; GUI.skin = mfdSkin; GUI.contentColor = Phos;
            try { DrawScreen(new Rect(scr.x + 6 * k, scr.y + 4 * k, scr.width - 12 * k, scr.height - 8 * k)); }
            catch (Exception ex) { GUI.Label(new Rect(scr.x + 8, scr.yMax - 24, scr.width, 20), "page error: " + ex.Message); }
            GUI.skin = saved; GUI.contentColor = Color.white;
            // soft keys
            var g = CurGroup; var it = g == null ? null : CurItem; bool home = g == null; var items = home ? null : MfdNav.PageItems(g, mfdPage);
            string[] left = new string[MfdNav.Side], right;
            if (home) { var hk = MfdNav.HomeKeys(HasMj); Array.Copy(hk, left, MfdNav.Side); right = hk.Skip(MfdNav.Side).ToArray(); }
            else { for (int i = 0; i < items.Count; i++) left[i] = (items[i].Id == it.Id ? "\u25B8" : "") + items[i].Label + Indicator(items[i]); right = MfdNav.Right(it.Id); }
            var bottom = MfdNav.Bottom(home, it == null ? null : it.Id, mfdPage, home ? 1 : MfdNav.Pages(g));
            int kf = Mathf.RoundToInt(13 * k);
            foreach (var b in IvaLayout.KeysOf(W, H))
            {
                Rect r = new Rect(b.X, b.Y, b.Wd, b.Ht); int n = b.Id[1] - '0';
                switch (b.Id[0])
                {
                    case 'A': if (Annunciator(r, kf)) ShowMfdItem("alarm"); break;
                    case 'H': if (Key(r, "COMMS", kf)) OpenGroupId("comms"); break;
                    case 'T': { var tl = home || it == null ? new string[MfdNav.TopCount] : MfdNav.Top(it.Id); if (Key(r, n < tl.Length ? tl[n] : null, kf) && n < tl.Length && tl[n] != null) OnKey(it.Id, tl[n]); } break;   // pan/zoom on MAP+CHART
                    case 'L': if (Key(r, left[n], kf)) { if (home) OpenGroup(n); else { mfdItem = items[n].Id; mfdScroll = Vector2.zero; Save(); } } break;
                    case 'R': if (Key(r, right[n], kf)) { if (home) OpenGroup(MfdNav.Side + n); else OnKey(it.Id, right[n]); } break;
                    case 'B':
                        if (!Key(r, bottom[n], kf)) break;
                        if (n == MfdNav.BackKey) { mfdGroup = null; mfdItem = null; mfdPage = 0; Save(); }
                        else if (n == MfdNav.PrevKey && bottom[n] == "PREV") { mfdPage--; mfdItem = MfdNav.PageItems(g, mfdPage)[0].Id; }
                        else if (n == MfdNav.NextKey && bottom[n] == "NEXT") { mfdPage++; mfdItem = MfdNav.PageItems(g, mfdPage)[0].Id; }
                        else OnKey(it.Id, bottom[n]);
                        break;
                }
            }
            if (GUI.Button(new Rect(W - 26, 2, 22, 18), "\u2715", keyStyle)) { Expanded = false; Save(); }   // close (Alt+J reopens)
            Rect grip = new Rect(W - 22, H - 22, 22, 22); Event e = Event.current;
            if (e.type == EventType.MouseDown && grip.Contains(e.mousePosition)) { mfdResizing = true; e.Use(); }
            if (mfdResizing)
            {
                if (e.type == EventType.MouseDrag) { mfdRect.width = Mathf.Max(MfdMinW, e.mousePosition.x + 4); mfdRect.height = Mathf.Max(MfdMinH, e.mousePosition.y + 4); e.Use(); }
                if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp) { mfdResizing = false; Save(); }
            }
            GUI.DragWindow(new Rect(0, 0, W, pb.Y + pb.Ht));   // drag by the top bezel / plate
        }

        static void Frame(Rect r, Color hi, Color lo)
        {
            var o = GUI.color;
            GUI.color = hi; GUI.DrawTexture(new Rect(r.x, r.y, r.width, 2), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(r.x, r.y, 2, r.height), Texture2D.whiteTexture);
            GUI.color = lo; GUI.DrawTexture(new Rect(r.x, r.yMax - 2, r.width, 2), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(r.xMax - 2, r.y, 2, r.height), Texture2D.whiteTexture);
            GUI.color = o;
        }
        static bool Key(Rect r, string label, int maxFont)
        {
            if (string.IsNullOrEmpty(label)) { GUI.Box(r, "", keyOff); return false; }
            keyStyle.fontSize = IvaLayout.KeyFont(label, r.width, r.height, maxFont);
            return GUI.Button(r, label, keyStyle);
        }
        /// <summary>MASTER ALARM annunciator: red (warning) / amber (caution), flashing until acknowledged; press = alarm page.</summary>
        static bool Annunciator(Rect r, int maxFont)
        {
            string lvl = StatusWindow.AlarmLevel; bool on = lvl != "" && (!StatusWindow.AlarmUnacked || (Time.realtimeSinceStartup % 1f) < .5f);
            Color bg = !on ? new Color(.16f, .12f, .12f) : lvl == "warning" ? new Color(.85f, .1f, .08f) : new Color(.95f, .62f, .05f);
            var o = GUI.color; GUI.color = bg; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o;
            lampStyle.fontSize = IvaLayout.KeyFont("MASTER ALARM", r.width, r.height, maxFont);
            lampStyle.normal.background = lampStyle.hover.background = null; lampStyle.normal.textColor = lampStyle.hover.textColor = on ? Color.black : new Color(.45f, .35f, .35f);
            return GUI.Button(r, lvl == "warning" ? "MASTER WARNING" : lvl == "caution" ? "MASTER CAUTION" : "MASTER ALARM", lampStyle);
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
        static void OpenGroupId(string id) { var gs = MfdNav.Groups(HasMj); int i = gs.FindIndex(x => x.Id == id); if (i >= 0) OpenGroup(i); Expanded = true; }
        static void Note(string s) { mfdNote = s ?? ""; mfdNoteAt = Time.realtimeSinceStartup + 8; }
        static void Tool(string name, string args) { ChatWindow.ToolFromMenu(name, args ?? "{}"); Note(name.Replace('_', ' ') + " sent (result in COMMS)."); }

        void DrawScreen(Rect area)
        {
            var g = CurGroup; var it = CurItem;
            if (Time.realtimeSinceStartup < mfdNoteAt && mfdNote.Length > 0) { GUI.Label(new Rect(area.x, area.yMax - 40 * mfdScale, area.width, 40 * mfdScale), mfdNote, scrText); area.height -= 42 * mfdScale; }
            if (g == null) { DrawHome(area); return; }
            if (g.Id == "map")
            {
                if (!HighLogic.LoadedSceneIsFlight || MapWindow.Inst == null) { GUI.Label(area, "Map, charts and ILS are available in flight.", scrText); return; }
                MapWindow.Inst.DrawEmbedded(area, it.Id == "map" ? 0 : it.Id == "chart" ? 1 : 2); return;
            }
            if (g.Id == "comms") { DrawComms(area, it.Id); return; }
            GUILayout.BeginArea(area); mfdScroll = GUILayout.BeginScrollView(mfdScroll);
            try
            {
                if (it.Panel >= 0)
                {
                    bool ok = LocalOnly[it.Panel] || DepsOk; if (!ok) DepsWarning();
                    GUI.enabled = ok; DrawPanel(it.Panel); GUI.enabled = true;
                }
                else DrawCustom(it.Id);
            }
            finally { GUI.enabled = true; GUILayout.EndScrollView(); GUILayout.EndArea(); }
        }

        void DrawHome(Rect area)
        {
            GUILayout.BeginArea(area);
            GUILayout.Label("Mode   " + NativeFlightController.Phase.ToUpperInvariant());
            var act = NativeFlightController.ActiveRunway; if (act != null) GUILayout.Label("Rwy    " + act.Key + "  " + act.Phase + "  " + (act.Distance / 1000).ToString("0.0") + " km");
            GUILayout.Label("Plan   " + NativeFlightController.PlanStatus, scrText);
            var v = FlightGlobals.ActiveVessel;
            if (HighLogic.LoadedSceneIsFlight && v != null) GUILayout.Label(v.vesselName + "\nALT " + Math.Round(v.altitude) + " m   SPD " + Math.Round(v.srfSpeed) + " m/s   HDG " + Math.Round(FlightGlobals.ship_heading).ToString("000") + "\n" + v.situation, scrText);
            if (StatusWindow.AlarmLevel != "") GUILayout.Label("ALARM  " + StatusWindow.AlarmText, scrText);
            if (MultiplayerCheck.Warning != null) GUILayout.Label(MultiplayerCheck.Warning, scrText);
            DepsWarning();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Soft keys pick a group. BACK (bottom-left) returns here. COMMS (top right) is the chat. Alt+J hides the MFD.", scrText);
            GUILayout.EndArea();
        }

        /// <summary>COMMS: filtered chat log (INTERCOM / SYSTEM / PILOT / ALL) with the input line at the bottom.</summary>
        void DrawComms(Rect area, string page)
        {
            float lineH = 22 * mfdScale; Event e = Event.current;
            bool focused = GUI.GetNameOfFocusedControl() == "aics_comms";
            if (focused && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) { SendComms(); e.Use(); }
            if (focused && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { GUI.FocusControl(""); GUIUtility.keyboardControl = 0; e.Use(); }
            GUILayout.BeginArea(new Rect(area.x, area.y, area.width, area.height - lineH - 6));
            mfdScroll = GUILayout.BeginScrollView(new Vector2(0, float.MaxValue));
            foreach (var l in ChatWindow.Recent(200)) if (MfdNav.ChatShows(page, l)) GUILayout.Label(l, scrText);
            GUILayout.EndScrollView(); GUILayout.EndArea();
            GUI.SetNextControlName("aics_comms");
            commsText = GUI.TextField(new Rect(area.x, area.yMax - lineH, area.width, lineH), commsText, 500);
        }
        static void SendComms() { string t = (commsText ?? "").Trim(); commsText = ""; if (t.Length > 0) ChatWindow.SubmitText(t); }

        void DrawCustom(string id)
        {
            switch (id)
            {
                case "trim": GUILayout.Label("TRIM WIN opens the trim window (pitch / roll / yaw trim). AUTO TRIM trims now.", scrText); GUILayout.Label("Trim window " + (TrimWindow.TrimVisible ? "OPEN" : "closed")); break;
                case "alarm": foreach (var l in StatusWindow.AlarmLines()) GUILayout.Label(l, scrText); break;
                case "about":
                    {   // scrollable (wheel + SCROLL keys); keeps the current [>] step visible
                        var al = NativeFlightController.AboutLines(); float lh = Mathf.Max(10, scrText.fontSize * 1.45f); int vis = Mathf.Max(4, (int)((mfdRect.height * .55f) / lh));
                        int off = MfdNav.AboutWindow(al, vis); if (Mathf.Abs(aboutApplied - off) > 0) { mfdScroll.y = off * lh; aboutApplied = off; } else if (Event.current.type == EventType.ScrollWheel) { MfdNav.AboutOffset = Mathf.RoundToInt(mfdScroll.y / lh); aboutApplied = MfdNav.AboutOffset; }
                        foreach (var l in al) GUILayout.Label(l, scrText); break;
                    }
                case "systems":
                    GUILayout.Label(StatusWindow.RotorsTab ? "ROTORS" : "OVERVIEW", scrHead);
                    foreach (var r in StatusWindow.SystemRows())
                    {
                        var oc = GUI.contentColor; GUILayout.BeginHorizontal();
                        GUI.contentColor = r[0] == "fail" ? Color.red : r[0] == "caution" ? new Color(1, .7f, 0) : Phos; GUILayout.Label("\u25CF", GUILayout.Width(16 * mfdScale)); GUI.contentColor = oc;
                        GUILayout.Label(r[1], GUILayout.Width(Mathf.Max(80, 190 * mfdScale))); GUILayout.Label(r[2], scrText); GUILayout.EndHorizontal();
                    }
                    break;
                case "status":
                    foreach (var r in StatusWindow.StatusRows()) { GUILayout.BeginHorizontal(); GUILayout.Label(r[0], GUILayout.Width(Mathf.Max(80, 130 * mfdScale))); GUILayout.Label(r[1], scrText); GUILayout.EndHorizontal(); }
                    break;
                case "mjatt":
                    GUILayout.Label("MECHJEB SMARTASS", scrHead); GUILayout.Label(NativeFlightController.MjStatusNow, scrText);
                    GUILayout.Label("Right keys: prograde / retrograde / normal / radial.  Bottom: KILLROT, NODE, TARGET+, OFF.", scrText); break;
                case "mjguide":
                    GUILayout.Label("MECHJEB GUIDANCE", scrHead); GUILayout.Label(NativeFlightController.MjStatusNow, scrText);
                    GUILayout.Label("ASCENT 80 km orbit, LAND (target if set), EXEC NODE, RENDEZV + DOCK with the target, AIRCRAFT hold (current hdg/alt/speed), SPACEPLN autoland, ALL OFF.", scrText); break;
            }
        }

        static int aboutApplied = -1;
        internal static void OnKey(string item, string key)
        {
            if (item == "about" && NativeFlightController.LearnToggleMode && (key == "SCROLL UP" || key == "SCROLL DN")) { MfdNav.MoveCursor(key == "SCROLL UP" ? -1 : 1, LearnFlight.StepCount); return; }
            if (item == "about" && key == "TOGGLE") { ChatWindow.Notice("[LEARN] " + NativeFlightController.ToggleLearnStep(MfdNav.LearnCursor + 1)); return; }
            if (item == "about" && key == "SCROLL UP") { MfdNav.AboutScroll(-3); return; }
            if (item == "about" && key == "SCROLL DN") { MfdNav.AboutScroll(3); return; }
            if (item == "about" && key == "<") { MfdNav.AboutTurn(-1, NativeFlightController.AboutTitles.Length); return; }
            if (item == "about" && key == ">") { MfdNav.AboutTurn(1, NativeFlightController.AboutTitles.Length); return; }
            if (item == "about" && key == "LEARN") { Tool("learn_plane", "{}"); return; }
            if (key == null) return;
            switch (item)
            {
                case "map": case "chart": case "ils":
                    switch (key)
                    {
                        case "ZOOM+": MapWindow.ZoomIn(); break; case "ZOOM-": MapWindow.ZoomOut(); break; case "ZOOM": MapWindow.ZoomCycle(); break; case "CENTER": MapWindow.Recenter(); break;
                        case "RWY <": MapWindow.RunwayStep(-1); break; case "RWY >": MapWindow.RunwayStep(1); break; case "CTR RWY": MapWindow.ToggleCenterRunway(); break;
                        case "\u2190": MapWindow.PanView(270); break; case "\u2193": MapWindow.PanView(180); break; case "\u2191": MapWindow.PanView(0); break; case "\u2192": MapWindow.PanView(90); break;
                        case "VARIANT": MapWindow.NextVariant(); break; case "ADD WP": MapWindow.AddWaypoint(); break; case "REMOVE": MapWindow.RemoveWaypoint(); break;
                        case "WP <": MapWindow.SelectWp(-1); break; case "WP >": MapWindow.SelectWp(1); break;
                        case "ALT +50": MapWindow.AltStep(50); break; case "ALT -50": MapWindow.AltStep(-50); break; case "AGL/MSL": MapWindow.ToggleRef(); break;
                        case "SAVE": MapWindow.SaveChart(); break; case "RESET": MapWindow.ResetChart(); break; case "GUIDE": MapWindow.ToggleGuidance(); break;
                    }
                    return;
                case "intercom": case "system": case "pilot": case "all":
                    if (key == "SEND") SendComms(); else if (key == "CLEAR") commsText = ""; else if (key == "CHAT WIN") ChatWindow.ToggleChat();
                    return;
                case "alarm": if (key == "ACK") StatusWindow.Ack(); else if (key == "MAYDAY LT") StatusWindow.MaydayLights = !StatusWindow.MaydayLights; return;
                case "systems": if (key == "MAYDAY LT") StatusWindow.MaydayLights = !StatusWindow.MaydayLights; else StatusWindow.RotorsTab = key == "ROTORS"; return;
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
                case "flightplan":
                    if (key == "STOP") PlanPost("flightplan/stop", "{}", true);
                    else PlanPost(key == "FLY" ? "flightplan/fly" : "flightplan/check", "{\"plan\":" + ChatWindow.JsonStr(planText) + "}", key == "FLY");
                    return;
            }
            switch (key)
            {
                case "TAKEOFF": Tool("takeoff", "{}"); break; case "LAND": Tool("land", "{}"); break; case "GO AROUND": Tool("go_around", "{}"); break;
                case "ABORT": Tool("abort", "{}"); break; case "STOP": Tool("stop_current", "{}"); break; case "STATUS": Tool("autopilot_status", "{}"); break; case "LEARN": Tool("learn_plane", "{}"); break;
            }
        }
    }

    /// <summary>Multiplayer mods (Luna Multiplayer, DarkMultiPlayer, ...): AICS is single-player only. One-time chat notice + MFD line.</summary>
    internal static class MultiplayerCheck
    {
        internal const string Text = "AICS is single-player only; multiplayer is not supported or tested.";
        static bool? found; static bool told;
        internal static void Notify() { if (Warning == null) return; }
        internal static string Warning
        {
            get
            {
                if (found == null) { try { found = MfdNav.IsMultiplayer(AssemblyLoader.loadedAssemblies.Select(a => a.name)); } catch (Exception) { found = false; } }
                if (found.Value && !told) { told = true; ChatWindow.Notice("[SYSTEM] " + Text); }
                return found.Value ? Text : null;
            }
        }
    }
}