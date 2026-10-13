using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 trimScroll;

        void TabletTrim(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            trimScroll = GUILayout.BeginScrollView(trimScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "Trim window");
            TabletUi.Chip(new Rect(c.x, c.y, c.width, row),
                TrimWindow.TrimVisible ? "OPEN" : "CLOSED", TrimWindow.TrimVisible ? TabletUi.Good : TabletUi.Dim);
            float y = pad + row + gap;
            foreach (string hint in new[] { "TRIM WIN: pitch / roll / yaw trim.", "AUTO TRIM: trim now." })
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), hint, true);
                y += row;
            }
            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK", true);
                y += row + gap;
            }
            foreach (string key in MfdNav.Right("trim"))
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (TabletUi.Button(new Rect(12, y, width - 24, row), key,
                    key == "TRIM WIN" && TrimWindow.TrimVisible, TabletLink) && TabletLink)
                {
                    ChatLog.Write("g", "tablet trim: " + key);
                    OnKey("trim", key);
                }
                y += row + gap;
            }
            GUILayout.Space(y + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
