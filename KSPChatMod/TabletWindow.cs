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
        internal static readonly string[] TabletTabs = { "Mission", "Burns", "Map", "Graphs", "Tests" };
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
            tabletRect = GUI.Window(TabletId, tabletRect, DrawTablet, "", GUIStyle.none);
        }

        void DrawTablet(int id)
        {
            float W = tabletRect.width, H = tabletRect.height, b = TabletBezel;
            GUI.Box(new Rect(0, 0, W, H), "", tabletFrame);
            // header in the bezel: title, font size, close
            GUI.Label(new Rect(b + 6, 6, W - 260, TabletHead - 6), "AICS TABLET  " + TabletTabs[tabletTab].ToUpperInvariant(), tabletHead);
            float kw = Mathf.Max(40, tabletFont * 2.4f), kh = TabletHead - 12;
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
            for (int i = 0; i < TabletMapPages.Length; i++)
            {
                float w = tabletFont * 4.5f; if (GUI.Button(new Rect(x, area.y, w, kh), TabletMapPages[i], i == tabletMapPage ? tabletTabOn : tabletTabOff)) { tabletMapPage = i; Save(); }
                x += w + 4;
            }
            x += 12; var keys = tabletMapPage == 1 ? new[] { "RWY <", "RWY >", "VARIANT" } : tabletMapPage == 2 ? new[] { "RWY <", "RWY >" } : new[] { "ZOOM+", "ZOOM-", "CENTER", "CTR RWY" };
            foreach (var k in keys) { float w = tabletFont * (k.Length * .65f + 1.2f); if (x + w > area.xMax) break; if (GUI.Button(new Rect(x, area.y, w, kh), k, tabletKey)) OnKey(TabletMapPages[tabletMapPage].ToLowerInvariant(), k); x += w + 4; }
            MapWindow.Inst.DrawEmbedded(new Rect(area.x, area.y + kh + 6, area.width, area.height - kh - 6), tabletMapPage);   // the cockpit MFD map pass
        }

        void TabletTests(Rect area)
        {   // S5: the LEARN page; keys go through OnKey("learn", ...) exactly like the MFD LEARN page's right column
            var keys = MfdNav.Right("learn"); float kh = tabletFont * 1.9f, x = area.x;
            foreach (var k in keys)
            {
                if (string.IsNullOrEmpty(k)) continue; float w = tabletFont * (k.Length * .65f + 1.4f);
                if (GUI.Button(new Rect(x, area.y, w, kh), k, tabletKey)) OnKey("learn", k);
                x += w + 6;
            }
            var ll = NativeFlightController.LearnLines(); float lh = Mathf.Max(12, tabletFont * 1.45f);
            float top = area.y + kh + 8; int vis = Mathf.Max(3, (int)((area.yMax - top) / lh));
            if (Event.current.type == EventType.ScrollWheel && area.Contains(Event.current.mousePosition)) { MfdNav.AboutScroll(Event.current.delta.y > 0 ? 3 : -3); Event.current.Use(); }
            int off = MfdNav.AboutWindow(ll, vis);
            GUILayout.BeginArea(new Rect(area.x, top, area.width, area.yMax - top));
            for (int i = off; i < ll.Count && i < off + vis; i++) GUILayout.Label(ll[i], tabletText);
            GUILayout.EndArea();
        }

        static string TabletSave()
        {
            return string.Format(CultureInfo.InvariantCulture, "tx={0}\nty={1}\ntw={2}\nth={3}\ntvis={4}\nttab={5}\ntfont={6}\ntmap={7}\n",
                tabletRect.x, tabletRect.y, tabletRect.width, tabletRect.height, TabletVisible ? 1 : 0, tabletTab, tabletFont, tabletMapPage);
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
                case "tmap": tabletMapPage = Mathf.Clamp((int)f, 0, 2); return true;
            }
            return false;
        }
    }
}
