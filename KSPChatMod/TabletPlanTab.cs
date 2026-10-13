using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static readonly string[] TabletPlanPages = { "FLIGHT PLAN" };
        static int tabletPlanPage;

        void TabletPlanTab(Rect area)
        {
            float sh = Mathf.Max(44, tabletFont * 2.4f);
            int p = TabletUi.Segments(new Rect(area.x, area.y, area.width, sh), TabletPlanPages, tabletPlanPage);
            if (p != tabletPlanPage) { tabletPlanPage = p; }
            Rect body = new Rect(area.x, area.y + sh + 8, area.width, Mathf.Max(60, area.height - sh - 8));
            switch (tabletPlanPage)
            {
                case 0: TabletFlightPlan(body); break;
            }
        }
    }
}
