using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static readonly string[] TabletSettingsPages = { "SETTINGS" };
        static int tabletSettingsPage;

        void TabletSettingsTab(Rect area)
        {
            float sh = Mathf.Max(44, tabletFont * 2.4f);
            int p = TabletUi.Segments(new Rect(area.x, area.y, area.width, sh), TabletSettingsPages, tabletSettingsPage);
            if (p != tabletSettingsPage) { tabletSettingsPage = p; }
            Rect body = new Rect(area.x, area.y + sh + 8, area.width, Mathf.Max(60, area.height - sh - 8));
            switch (tabletSettingsPage)
            {
                case 0: TabletSettings(body); break;
            }
        }
    }
}