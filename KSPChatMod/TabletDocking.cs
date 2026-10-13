using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletDockingScroll;

        void TabletDocking(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletDockingScroll = GUILayout.BeginScrollView(tabletDockingScroll);

            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "DOCKING");

            var active = FlightGlobals.ActiveVessel;
            if (active == null) { TabletUi.Label(new Rect(c.x, c.y + pad + 4, c.width - 24, row), "No active vessel", true); return; }

            var target = (Vessel)FlightGlobals.fetch.VesselTarget;
            string tName = target != null ? target.vesselName : "None";
            double dist = Vector3d.Distance(active.transform.position, target.transform.position);

            TabletUi.Tile(new Rect(c.x, c.y + pad + 4, c.width - 24, row), "TARGET", tName, dist.ToString("0") + " m", TabletUi.Series[1]);

            float y = pad + row + gap;
            GUILayout.Space(y + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
