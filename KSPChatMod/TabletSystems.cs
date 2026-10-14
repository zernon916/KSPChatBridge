using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletSystemsScroll;

        void TabletSystems(Rect area)
        {
            var c = TabletUi.CardPanel(area, "SYSTEMS");
            TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, c.height), "SYSTEMS - port in progress", true);
        }
    }
}