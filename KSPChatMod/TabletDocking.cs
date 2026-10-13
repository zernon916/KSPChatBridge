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
            int rows = 3;
            GUILayout.BeginArea(area);
            tabletDockingScroll = GUILayout.BeginScrollView(tabletDockingScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row * rows + gap), "DOCKING");
            float y = c.y, w = c.width - 24;

            var active = FlightGlobals.ActiveVessel;
            ITargetable tgt = FlightGlobals.fetch != null ? FlightGlobals.fetch.VesselTarget : null;
            Vessel target = tgt != null ? tgt.GetVessel() : null;
            if (active == null) TabletUi.Label(new Rect(c.x, y, w, row), "No active vessel", true);
            else if (target == null) TabletUi.Label(new Rect(c.x, y, w, row), "No target selected", true);
            else
            {
                double dist = Vector3d.Distance(active.GetWorldPos3D(), target.GetWorldPos3D());
                TabletUi.Tile(new Rect(c.x, y, w, row), "TARGET", target.vesselName, dist.ToString("0") + " m", TabletUi.Series[1]);
            }
            y += row;

            if (!TabletLink) { TabletUi.Label(new Rect(c.x, y, w, row), "INFO ONLY - enable LIVE LINK", true); y += row; }
            if (TabletUi.Button(new Rect(c.x, y, w, row), "Dock with target", false, TabletLink) && TabletLink)
                ChatWindow.ToolFromMenu("dock_with", "{\"name\":\"target\"}");

            GUILayout.Space(pad + row * rows + gap * 2);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}