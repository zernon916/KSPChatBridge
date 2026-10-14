using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static readonly string[] TabletSystemsPages = { "MASTER ALARM", "SYSTEMS" };
        static int tabletSystemsPage;

        void TabletSystemsTab(Rect area)
        {
            float sh = Mathf.Max(44, tabletFont * 2.4f);
            int p = TabletUi.Segments(new Rect(area.x, area.y, area.width, sh), TabletSystemsPages, tabletSystemsPage);
            if (p != tabletSystemsPage) { tabletSystemsPage = p; }
            Rect body = new Rect(area.x, area.y + sh + 8, area.width, Mathf.Max(60, area.height - sh - 8));
            switch (tabletSystemsPage)
            {
                case 0: TabletAlarm(body); break;
            }
        }
    }
}