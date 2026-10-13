using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletFlightPlanScroll;

        void TabletFlightPlan(Rect area)
        {
            GUILayout.BeginArea(area);
            tabletFlightPlanScroll = GUILayout.BeginScrollView(tabletFlightPlanScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, area.width - 26, 50), "FLIGHT PLAN");
            TabletUi.Label(new Rect(c.x + 12, c.y + 12, c.width - 24, 30), "FLIGHT PLAN - port in progress");
            GUILayout.Space(50 + 8);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
