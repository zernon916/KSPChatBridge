using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletMjAttScroll;

        void TabletMjAtt(Rect area)
        {
            var c = TabletUi.CardPanel(area, "SMARTASS");
            
            // Check MechJeb status from the controller
            string status = NativeFlightController.MjStatusNow;
            
            if (string.IsNullOrEmpty(status))
            {
                TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, c.height), "MechJeb2 not installed", true);
            }
            else
            {
                TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, c.height), status, true);
            }
            float row = Mathf.Max(44, tabletFont * 2.6f), y = c.y + row + 14;
            if (!TabletLink) { TabletUi.Label(new Rect(c.x + 6, y, c.width - 12, row), "INFO ONLY - enable LIVE LINK", true); y += row + 8; }
        }
    }
}
