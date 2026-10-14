using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletMjGuideScroll;

        void TabletMjGuide(Rect area)
        {
            Rect cardRect = TabletUi.CardPanel(area, "GUIDANCE");
            TabletUi.Label(cardRect, "GUIDANCE - port in progress");
        }
    }
}
