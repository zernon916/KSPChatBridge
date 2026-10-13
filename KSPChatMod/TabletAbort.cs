using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 abortScroll;

        // Panel 13 data and the control box's shared command handlers.
        void TabletAbort(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            abortScroll = GUILayout.BeginScrollView(abortScroll);
            float y = 0;
            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK", true);
                y += row + gap;
            }
            foreach (string key in MfdNav.Right("abort"))
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (TabletUi.Button(new Rect(12, y, width - 24, row), key, false, TabletLink) && TabletLink)
                {
                    ChatLog.Write("g", "tablet abort: " + key);
                    OnKey("abort", key);
                }
                y += row + gap;
            }
            var v = FlightGlobals.ActiveVessel;
            string[] labels = { "Radar altitude", "MSL altitude", "Speed", "Vertical speed", "Heading" };
            string[] units = { "m", "m", "m/s", "m/s", "deg" };
            double[] values = v == null ? null : new[] { v.radarAltitude, v.altitude, v.srfSpeed,
                v.verticalSpeed, (double)FlightGlobals.ship_heading };
            float tileHeight = Mathf.Max(84, tabletFont * 4.3f);
            for (int i = 0; i < labels.Length; i++)
            {
                string value = values == null ? "-" : values[i].ToString(
                    i == 3 ? "+0.0;-0.0;0.0" : i == 4 ? "000" : "N0", CultureInfo.InvariantCulture);
                TabletUi.Tile(new Rect(0, y, width, tileHeight), labels[i], value, units[i], TabletUi.Accent);
                y += tileHeight + gap;
            }
            string[] lines = (NativeFlightController.ApSummary() + "\n" +
                (landing.Length > 0 ? landing : "(no active landing / ETA)")).Split('\n');
            float lh = Mathf.Max(26, tabletFont * 1.5f);
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, pad + lines.Length * lh), "Autopilot / landing status");
            for (int i = 0; i < lines.Length; i++)
                TabletUi.Label(new Rect(c.x, c.y + i * lh, c.width, lh), lines[i]);
            y += pad + lines.Length * lh + gap;
            GUILayout.Space(y);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
