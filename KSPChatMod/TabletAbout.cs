using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletAboutScroll;

        void TabletAbout(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), width = area.width - 26, y = 0;
            float pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletAboutScroll = GUILayout.BeginScrollView(tabletAboutScroll);
            float keyWidth = (width - 16) / 3;
            var keys = MfdNav.Right("about");
            for (int i = 0; i < keys.Length; i++)
            {
                if (TabletUi.Button(new Rect((i % 3) * (keyWidth + 8), (i / 3) * (row + 8), keyWidth, row), keys[i], false, TabletLink) && TabletLink)
                {
                    ChatLog.Write("g", "tablet about: " + keys[i]);
                    OnKey("about", keys[i]);
                    if (keys[i] == "LEARN") { tabletTab = 4; Save(); }
                }
            }
            y = ((keys.Length + 2) / 3) * (row + 8);
            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK", true);
                y += row + 8;
            }
            var lines = NativeFlightController.AboutLines();
            var curve = NativeFlightController.AboutCurve();
            if (tabletAboutStyle == null || tabletAboutFont != tabletFont) { tabletAboutStyle = new GUIStyle(TabletUi.TextStyle) { wordWrap = true }; tabletAboutFont = tabletFont; }   // cached, rebuilt on font change
            var textStyle = tabletAboutStyle;
            if (curve != null)
            {
                var series = curve.Series1D();
                if (series.Count > 0)
                {
                    float[] xs = new float[series.Count], ys = new float[series.Count];
                    for (int i = 0; i < series.Count; i++) { xs[i] = (float)series[i][0]; ys[i] = (float)series[i][1]; }
                    Rect c = TabletUi.CardPanel(new Rect(0, y, width, pad + 260 + row * 2), "Performance curve");
                    TabletUi.Graph(new Rect(c.x, c.y, c.width, 260), new[] { curve.YLabel }, new[] { ys }, ys.Length, ys.Length, new[] { "" }, xs);
                    GUI.Label(new Rect(c.x, c.y + 260, c.width, row * 2), curve.XLabel + ": " + xs[0].ToString("0.##") + " to " + xs[xs.Length - 1].ToString("0.##") + " (mean samples, unscaled)", textStyle);
                    y += pad + 260 + row * 2 + 8;
                }
            }
            // Shared SCROLL keys select the first data row; the local scrollbar reaches all cards.
            int offset = MfdNav.AboutWindow(lines, 1);
            for (int i = offset; i < lines.Count; i++)
            {
                float height = Mathf.Max(row, textStyle.CalcHeight(new GUIContent(lines[i]), width - 24));
                Rect c = TabletUi.CardPanel(new Rect(0, y, width, height + 16), null);
                GUI.Label(new Rect(c.x, c.y, c.width, height), lines[i], textStyle);
                y += height + 24;
            }
            GUILayout.Space(y);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
        static GUIStyle tabletAboutStyle; static int tabletAboutFont = -1;
    }
}
