using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletOrbitApScroll;

        void TabletOrbitAp(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletOrbitApScroll = GUILayout.BeginScrollView(tabletOrbitApScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "ORBITAL AP");
            float y = pad + row + gap;
            TabletUi.Label(new Rect(c.x, y, c.width - 24, row), "ORBITAL AP - port in progress", true);
            GUILayout.Space(y + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
