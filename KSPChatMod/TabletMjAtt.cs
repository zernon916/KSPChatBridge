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
        }
    }
}
