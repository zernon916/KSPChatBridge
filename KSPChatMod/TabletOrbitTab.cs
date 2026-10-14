using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static readonly string[] TabletOrbitPages = { "ORBITAL AP", "CAPTURE", "DOCKING", "SUN LOCK", "SMARTASS", "GUIDANCE", "RENDEZVOUS" };
        static int tabletOrbitPage;

        void TabletOrbitTab(Rect area)
        {
            float sh = Mathf.Max(44, tabletFont * 2.4f);
            int p = TabletUi.Segments(new Rect(area.x, area.y, area.width, sh), TabletOrbitPages, tabletOrbitPage);
            if (p != tabletOrbitPage) { tabletOrbitPage = p; }
            Rect body = new Rect(area.x, area.y + sh + 8, area.width, Mathf.Max(60, area.height - sh - 8));
            switch (tabletOrbitPage)
            {
                case 0: TabletOrbitAp(body); break;
                case 1: TabletCapture(body); break;
                case 2: TabletDocking(body); break;
                case 3: TabletSunLock(body); break;
                case 4: TabletMjAtt(body); break;
                case 5: TabletMjGuide(body); break;
                case 6: TabletRendezvous(body); break;
            }
        }
    }
}
