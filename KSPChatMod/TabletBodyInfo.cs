using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletBodyInfoScroll;

        void TabletBodyInfo(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletBodyInfoScroll = GUILayout.BeginScrollView(tabletBodyInfoScroll);

            float y = 0;
            var bodies = FlightGlobals.Bodies;
            if (bodies != null)
            {
                foreach (CelestialBody body in bodies)
                {
                    if (body == null) continue;
                    TabletUi.CardPanel(new Rect(0, y, width, pad + row), body.bodyName);
                    TabletUi.Label(new Rect(12, y + pad, width - 24, row), BodyInfo.Line(body), true);
                    y += pad + row + gap;
                }
            }
            if (y == 0)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "No body data", true);
                y += row;
            }

            GUILayout.Space(y + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
