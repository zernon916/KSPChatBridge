using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletSunLockScroll;

        void TabletSunLock(Rect area)
        {
            if (FlightGlobals.ActiveVessel == null)
            {
                TabletUi.Label(area, "No active vessel.");
                return;
            }
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletSunLockScroll = GUILayout.BeginScrollView(tabletSunLockScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "SUN LOCK");
            TabletUi.Label(new Rect(c.x, c.y, c.width, row), "Space only: nose to the sun and hold (stock SAS). Best-face (side panels) not available yet.", true);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
