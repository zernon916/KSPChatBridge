using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletOrbitApScroll;

        void TabletOrbitAp(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletOrbitApScroll = GUILayout.BeginScrollView(tabletOrbitApScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row * 4 + gap), "ORBITAL AP");
            float y = c.y;

            var v = FlightGlobals.ActiveVessel;
            if (v != null && v.orbit != null)
            {
                bool mjActive = StatusWindow.Active("ind_mechjeb");
                TabletUi.Chip(new Rect(c.x, y, c.width - 24, row), mjActive ? "ENGAGED" : "OFF", mjActive ? TabletUi.Good : TabletUi.Dim);

                float y1 = y + row;
                if (y1 < c.yMax)
                    TabletUi.Label(new Rect(c.x, y1, c.width - 24, row), "skLonT: " + skLonT, true);

                float y2 = y1 + row;
                if (y2 < c.yMax)
                    TabletUi.Tile(new Rect(c.x, y2, c.width - 24, row), "Apogee", v.orbit.ApA.ToString("0"), "m", TabletUi.Series[0]);

                float y3 = y2 + row;
                if (y3 < c.yMax)
                    TabletUi.Tile(new Rect(c.x, y3, c.width - 24, row), "Perigee", v.orbit.PeA.ToString("0"), "m", TabletUi.Series[1]);
            }
            else
            {
                TabletUi.Label(new Rect(c.x, y, c.width - 24, row), "No active vessel or orbit data.", true);
            }

            GUILayout.Space(pad + row * 4 + gap * 2);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
