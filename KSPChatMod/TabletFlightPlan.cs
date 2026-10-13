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
            
            // Increase card height to fit content within the scroll area
            Rect c = TabletUi.CardPanel(new Rect(0, 0, area.width - 26, area.height), "FLIGHT PLAN");
            
            // Status Label
            string statusText = planStatus != null ? planStatus : "idle";
            TabletUi.Label(new Rect(c.x + 12, c.y + 8, c.width - 24, 30), "STATUS: " + statusText);
            
            float y = c.y + 50;
            
            // Message Label
            string msgText = planMsg != null ? planMsg : "none";
            TabletUi.Label(new Rect(c.x + 12, y, c.width - 24, 30), "MSG: " + msgText);
            
            float y2 = c.y + 80;
            
            // Plan Text Label (Preview)
            string planTextPreview = planText != null && planText.Length > 0 ? planText : "(empty)";
            TabletUi.Label(new Rect(c.x + 12, y2, c.width - 24, 30), "PLAN: " + planTextPreview);
            
            GUILayout.Space(50 + 8);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
