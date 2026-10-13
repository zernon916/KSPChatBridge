using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletOrbitPlanScroll;

        void TabletOrbitPlan(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletOrbitPlanScroll = GUILayout.BeginScrollView(tabletOrbitPlanScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "ORBIT PLAN");
            TabletUi.Label(new Rect(c.x + 6, c.y + 8, c.width - 12, row), "ORBIT PLAN - port in progress", true);
            GUILayout.Space(pad + row + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}