using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletCaptureScroll;

        void TabletCapture(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletCaptureScroll = GUILayout.BeginScrollView(tabletCaptureScroll);

            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "CAPTURE");
            TabletUi.Label(new Rect(c.x, c.y + pad + 4, c.width - 24, row), "CAPTURE - port in progress", true);

            float y = pad + row + gap;
            GUILayout.Space(y + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
