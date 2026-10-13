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
            int rows = 10;
            GUILayout.BeginArea(area);
            tabletOrbitApScroll = GUILayout.BeginScrollView(tabletOrbitApScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row * rows + gap), "ORBITAL AP");
            float y = c.y, w = c.width - 24;

            var v = FlightGlobals.ActiveVessel;
            if (v != null && v.orbit != null)
            {
                bool mjActive = StatusWindow.Active("ind_mechjeb");
                TabletUi.Chip(new Rect(c.x, y, w, row), mjActive ? "ENGAGED" : "OFF", mjActive ? TabletUi.Good : TabletUi.Dim); y += row;
                TabletUi.Label(new Rect(c.x, y, w, row), "Station-keep lon: " + skLonT, true); y += row;
                TabletUi.Tile(new Rect(c.x, y, w, row), "Apoapsis", v.orbit.ApA.ToString("0"), "m", TabletUi.Series[0]); y += row;
                TabletUi.Tile(new Rect(c.x, y, w, row), "Periapsis", v.orbit.PeA.ToString("0"), "m", TabletUi.Series[1]); y += row;
            }
            else { TabletUi.Label(new Rect(c.x, y, w, row), "No active vessel or orbit data.", true); y += row; }

            if (!TabletLink) { TabletUi.Label(new Rect(c.x, y, w, row), "INFO ONLY - enable LIVE LINK", true); y += row; }
            if (TabletUi.Button(new Rect(c.x, y, w, row), "Antenna Lock", false, TabletLink) && TabletLink)
                ChatWindow.ToolFromMenu("antenna_lock", "{\"on\":true}");
            y += row;
            if (TabletUi.Button(new Rect(c.x, y, w, row), "Release", false, TabletLink) && TabletLink)
                ChatWindow.ToolFromMenu("antenna_lock", "{\"on\":false}");
            y += row;
            skLonT = TabletUi.Field(new Rect(c.x, y, w, row), skLonT, TabletLink); y += row;
            if (TabletUi.Button(new Rect(c.x, y, w, row), "Station-keep over lon", false, TabletLink) && TabletLink)
                ChatWindow.ToolFromMenu("station_keep", "{\"longitude\":" + (skLonT.Trim().Length > 0 ? Num(skLonT, "999") : "999") + "}");
            y += row;
            if (TabletUi.Button(new Rect(c.x, y, w, row), "Sync alt?", false, TabletLink) && TabletLink)
                ChatWindow.ToolFromMenu("sync_orbit_altitude", "{}");

            GUILayout.Space(pad + row * rows + gap * 2);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}