using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletAlarmScroll;

        void TabletAlarm(Rect area)
        {
            var c = TabletUi.CardPanel(area, "MASTER ALARM");
            TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, c.height), "MASTER ALARM - port in progress", true);
        }
    }
}