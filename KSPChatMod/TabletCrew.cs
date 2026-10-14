using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletCrewScroll;

        void TabletCrew(Rect area)
        {
            Rect cardRect = TabletUi.CardPanel(area, "CREW / EVA");
            TabletUi.Label(cardRect, "CREW / EVA - port in progress");
        }
    }
}