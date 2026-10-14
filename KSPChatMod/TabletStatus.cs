using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletStatusScroll;

        void TabletStatus(Rect area)
        {
            var c = TabletUi.CardPanel(area, "STATUS");
            float row = Mathf.Max(44, tabletFont * 2.6f);
            if (FlightGlobals.ActiveVessel == null)
            {
                TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, row), "No active vessel", true);
                return;
            }
            float y = c.y + 6;
            foreach (var r in StatusWindow.StatusRows())
            {
                TabletUi.Label(new Rect(c.x + 6, y, c.width - 12, row), r[0] + ": " + r[1], true);
                y += row + 8;
            }
        }
    }
}