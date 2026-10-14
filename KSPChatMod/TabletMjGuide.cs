using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletMjGuideScroll;

        void TabletMjGuide(Rect area)
        {
            var c = TabletUi.CardPanel(area, "GUIDANCE");
            float row = Mathf.Max(44, tabletFont * 2.6f);
            string status = NativeFlightController.MjStatusNow;
            TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, row), string.IsNullOrEmpty(status) ? "MechJeb2 not installed" : status, true);
            TabletUi.Label(new Rect(c.x + 6, c.y + 6 + row, c.width - 12, row), "ASCENT 80 km orbit, LAND (target if set), EXEC NODE, RENDEZV + DOCK with the target, AIRCRAFT hold (current hdg/alt/speed), SPACEPLN autoland, ALL OFF.", true);
            float yb = c.yMax + 8;
            if (!TabletLink) { TabletUi.Label(new Rect(c.x + 6, yb, c.width - 12, row), "INFO ONLY - enable LIVE LINK", true); yb += row + 8; }
        }
    }
}
