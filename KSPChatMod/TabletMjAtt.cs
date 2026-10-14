using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletMjAttScroll;

        void TabletMjAtt(Rect area)
        {
            var c = TabletUi.CardPanel(area, "SMARTASS");
            TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, c.height), "SMARTASS - port in progress", true);
        }
    }
}
