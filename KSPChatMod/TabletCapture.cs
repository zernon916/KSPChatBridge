using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletCaptureScroll;

        void TabletCapture(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletCaptureScroll = GUILayout.BeginScrollView(tabletCaptureScroll);

            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "CAPTURE");
            var v = FlightGlobals.ActiveVessel;
            string bodyName = (v?.orbit?.referenceBody?.bodyName ?? "N/A").Trim();
            double peA = v?.orbit?.PeA ?? 0;
            double ecc = v?.orbit?.eccentricity ?? 0;

            float tileW = c.width - 24;
            float tileH = Mathf.Max(30, tabletFont * 1.8f);
            TabletUi.Tile(new Rect(c.x + 6, c.y + pad + 4, tileW, tileH), "Body", bodyName, "", new Color(.5f, .5f, .5f));
            float y = c.y + pad + 4 + tileH;
            TabletUi.Tile(new Rect(c.x + 6, y, tileW, tileH), "PeA", peA.ToString("0.0"), "km", new Color(.3f, .8f, .5f));
            y += tileH;
            TabletUi.Tile(new Rect(c.x + 6, y, tileW, tileH), "Ecc", ecc.ToString("0.000"), "", new Color(.7f, .7f, .7f));

            y += tileH + gap;
            TabletUi.Label(new Rect(c.x + 6, y, c.width - 12, tabletFont * 1.4f), "Cap Pe: " + peA.ToString("0"), true);
            
            float tileH2 = Mathf.Max(30, tabletFont * 1.8f);
            TabletUi.Field(new Rect(c.x + 6, y + tabletFont * 1.4f + 4, c.width - 12, tileH2), capPeT, TabletLink);
            y += tileH2 + gap;

            float btnW = Mathf.Max(80, tabletFont * (15 * .7f + 2f));
            if (TabletUi.Button(new Rect(c.x + 6, y, btnW, tileH), "Capture numbers", false, TabletLink) && TabletLink) ChatWindow.ToolFromMenu("capture_plan", "{}");
            y += tileH;
            if (TabletUi.Button(new Rect(c.x + 6, y, btnW, tileH), "Capture (circularize)", false, TabletLink) && TabletLink) ChatWindow.ToolFromMenu("circularize", "{}");
            y += tileH;
            float btnW2 = Mathf.Max(80, tabletFont * ("Set Pe (MJ burn)".Length * .7f + 2f));
            if (TabletUi.Button(new Rect(c.x + 6, y, btnW2, tileH), "Set Pe (MJ burn)", false, TabletLink) && TabletLink) ChatWindow.ToolFromMenu("change_periapsis", "{\"altitude_km\":" + Num(capPeT, "30") + "}");
            y += tileH;

            if (!TabletLink) {
                float infoH = Mathf.Max(28, tabletFont * 1.6f);
                TabletUi.Label(new Rect(c.x + 6, y, c.width - 12, infoH), "INFO ONLY - enable LIVE LINK", true);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
