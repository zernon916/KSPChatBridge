using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletFlightPlanScroll;

        // TB11b: Flight Plan info page (read only - no buttons, no setters).
        void TabletFlightPlan(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            string st = planStatus, msg = planMsg, text = planText;   // volatile: read once
            Vessel v = FlightGlobals.ActiveVessel;
            GUILayout.BeginArea(area);
            tabletFlightPlanScroll = GUILayout.BeginScrollView(tabletFlightPlanScroll);
            Rect c = TabletUi.CardPanel(new Rect(0, 0, width, pad + row * 3), "FLIGHT PLAN");
            bool running = !string.IsNullOrEmpty(st);
            TabletUi.Chip(new Rect(c.x, c.y, c.width, row), running ? "RUNNING" : "IDLE", running ? TabletUi.Good : TabletUi.Dim);
            TabletUi.Label(new Rect(c.x, c.y + row, c.width, row), "STATUS: " + (running ? st : "idle"), true);
            TabletUi.Label(new Rect(c.x, c.y + row * 2, c.width, row), "MSG: " + (string.IsNullOrEmpty(msg) ? "none" : msg), true);
            float y = pad + row * 3 + gap;

            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            int shown = 0;
            Rect pc = TabletUi.CardPanel(new Rect(0, y, width, pad + row * 6), "PLAN (first lines)");
            foreach (string ln in lines)
            {
                if (ln.Trim().Length == 0) continue;
                if (shown == 6) break;
                TabletUi.Label(new Rect(pc.x, pc.y + row * shown, pc.width, row), ln.Trim(), true);
                shown++;
            }
            if (shown == 0) TabletUi.Label(new Rect(pc.x, pc.y, pc.width, row), "(empty)", true);
            y += pad + row * 6 + gap;

            TabletUi.Label(new Rect(12, y, width - 24, row), v == null ? "NO ACTIVE VESSEL" : "VESSEL: " + v.vesselName, true);
            y += row;
            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK", true);
                y += row;
            }
            foreach (string key in new[] { "CHECK", "FLY", "STOP" })
            {
                if (TabletUi.Button(new Rect(12, y, width - 24, row), key, false, TabletLink) && TabletLink) OnKey("flightplan", key);
                y += row + gap;
            }
            GUILayout.Space(y + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
