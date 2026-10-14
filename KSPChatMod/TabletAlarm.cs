using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletAlarmScroll;

        void TabletAlarm(Rect area)
        {
            var c = TabletUi.CardPanel(area, "MASTER ALARM");
            float row = Mathf.Max(44, tabletFont * 2.6f);
            if (FlightGlobals.ActiveVessel == null)
            {
                TabletUi.Label(new Rect(c.x + 6, c.y + 6, c.width - 12, row), "No active vessel", true);
                return;
            }
            string lvl = StatusWindow.AlarmLevel;
            TabletUi.Chip(new Rect(c.x + 6, c.y + 6, c.width - 12, row),
                string.IsNullOrEmpty(lvl) ? "NO ALARM" : lvl.ToUpperInvariant(),
                lvl == "warning" ? TabletUi.Bad : lvl == "caution" ? TabletUi.Warn : TabletUi.Good);
            float y = c.y + 6 + row + 8;
            if (!string.IsNullOrEmpty(StatusWindow.AlarmText))
            {
                TabletUi.Label(new Rect(c.x + 6, y, c.width - 12, row), StatusWindow.AlarmText, true);
                y += row + 8;
            }
            TabletUi.Label(new Rect(c.x + 6, y, c.width - 12, row), StatusWindow.AlarmUnacked ? "UNACKNOWLEDGED" : "acknowledged", true);
            y += row + 8;
            foreach (string line in StatusWindow.AlarmLines())
            {
                if (string.IsNullOrEmpty(line)) continue;
                TabletUi.Label(new Rect(c.x + 6, y, c.width - 12, row), line, true);
                y += row + 8;
            }
            float yb = c.yMax + 8;
            if (!TabletLink) { TabletUi.Label(new Rect(c.x + 6, yb, c.width - 12, row), "INFO ONLY - enable LIVE LINK", true); yb += row + 8; }
            foreach (string key in MfdNav.Right("alarm")) { if (string.IsNullOrEmpty(key) || key == "BACK") continue; if (TabletUi.Button(new Rect(c.x + 6, yb, c.width - 12, row), key, false, TabletLink) && TabletLink) { ChatLog.Write("g", "tablet alarm: " + key); OnKey("alarm", key); } yb += row + 8; }
            GUILayout.Space(yb + 8);
        }
    }
}