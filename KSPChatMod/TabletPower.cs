using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletPowerScroll;

        void TabletPower(Rect area)
        {
            var c = TabletUi.CardPanel(area, "POWER");
            float row = Mathf.Max(44, tabletFont * 2.6f);
            TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, row), "POWER - port in progress", true);
        }
    }
}