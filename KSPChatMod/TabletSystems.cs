using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletSystemsScroll;

        void TabletSystems(Rect area)
        {
            var c = TabletUi.CardPanel(area, "SYSTEMS");
            float row = Mathf.Max(44, tabletFont * 2.6f);
            if (FlightGlobals.ActiveVessel == null)
            {
                TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, row), "No active vessel", true);
                return;
            }
            TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, row), StatusWindow.RotorsTab ? "ROTORS" : "OVERVIEW", true);
            float y = c.y + 6 + row + 8;
            foreach (var r in StatusWindow.SystemRows())
            {
                TabletUi.Chip(new Rect(c.x + 6, y, 70, row), r[0] == "fail" ? "FAIL" : r[0] == "caution" ? "CAUT" : "OK",
                    r[0] == "fail" ? TabletUi.Bad : r[0] == "caution" ? TabletUi.Warn : TabletUi.Good);
                TabletUi.Label(new Rect(c.x + 82, y, c.width - 88, row), r[1] + ": " + r[2], true);
                y += row + 8;
            }
            float yb = c.yMax + 8;
            if (!TabletLink) { TabletUi.Label(new Rect(c.x + 6, yb, c.width - 12, row), "INFO ONLY - enable LIVE LINK", true); yb += row + 8; }
            foreach (string key in MfdNav.Right("systems")) { if (string.IsNullOrEmpty(key) || key == "BACK") continue; if (TabletUi.Button(new Rect(c.x + 6, yb, c.width - 12, row), key, false, TabletLink) && TabletLink) { ChatLog.Write("g", "tablet systems: " + key); OnKey("systems", key); } yb += row + 8; }
            GUILayout.Space(yb + 8);
        }
    }
}