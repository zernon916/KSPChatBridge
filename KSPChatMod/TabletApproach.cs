using System.Linq;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 approachScroll;

        // Panel 1's shared selection/options; no ILS drawing or autopilot logic here.
        void TabletApproach(Rect area)
        {
            var rw = ChatWindow.SpotsSnapshot().Where(s => s[1] == "H").ToList();
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            approachScroll = GUILayout.BeginScrollView(approachScroll);
            float y = 0;
            string[] lines = (NativeFlightController.ApSummary() + "\n" +
                (landing.Length > 0 ? landing : "No active landing / ETA")).Split('\n');
            float lh = Mathf.Max(26, tabletFont * 1.5f);
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, pad + lines.Length * lh), "Approach status");
            for (int i = 0; i < lines.Length; i++)
                TabletUi.Label(new Rect(c.x, c.y + i * lh, c.width, lh), lines[i]);
            y += pad + lines.Length * lh + gap;
            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK for controls", true);
                y += row + gap;
            }
            c = TabletUi.CardPanel(new Rect(0, y, width, pad + Mathf.Max(1, rw.Count) * (row + gap)), "Runway / landing spot");
            if (rw.Count == 0 && TabletUi.Button(new Rect(c.x, c.y, c.width, row), "LOAD RUNWAYS", false, TabletLink))
            {
                ChatLog.Write("g", "tablet approach: load runways");
                ChatWindow.RequestSpots();
            }
            for (int i = 0; i < rw.Count; i++)
                if (TabletUi.Button(new Rect(c.x, c.y + i * (row + gap), c.width, row),
                    rw[i][0] + (rw[i][5].Length > 0 ? "  [" + rw[i][5] + "]" : ""), rwySel == i, TabletLink) && rwySel != i)
                {
                    rwySel = i;
                    ChatLog.Write("g", "tablet approach: selected " + rw[i][0]);
                }
            y += pad + Mathf.Max(1, rw.Count) * (row + gap) + gap;
            c = TabletUi.CardPanel(new Rect(0, y, width, pad + 5 * (row + gap)), "Landing options");
            float cy = c.y;
            ApproachToggle(new Rect(c.x, cy, c.width, row), "Short final (5 km; otherwise 11 km)", ref shortFinal); cy += row + gap;
            ApproachToggle(new Rect(c.x, cy, c.width, row), "Vertical landing (otherwise runway)", ref vertical); cy += row + gap;
            ApproachToggle(new Rect(c.x, cy, c.width, row), "Chute / dV check for vertical landing", ref chuteCheck); cy += row + gap;
            ApproachField(new Rect(c.x, cy, c.width, row), "Approach heading", ref apprHdgT, 5); cy += row + gap;
            ApproachField(new Rect(c.x, cy, c.width, row), "Touch-and-go count", ref tgT, 3);
            y += pad + 5 * (row + gap) + gap;
            // Stack touch keys so they remain reachable at the minimum window size and largest font.
            {   // GROK TAB1: LANDING sub-page = one action button (GO AROUND / ABORT moved off; ABORT is a top tab)
                bool enabled = TabletLink && rwySel >= 0 && rwySel < rw.Count;
                if (TabletUi.ActionButton(new Rect(12, y, width - 24, row), "ENGAGE LANDING", enabled)) { ChatLog.Write("g", "tablet approach: LAND"); OnKey("approach", "LAND"); }
                y += row + gap;
                if (!enabled && TabletLink) { TabletUi.Label(new Rect(12, y, width - 24, row), "Pick a runway above first.", true); y += row + gap; }
            }
            GUILayout.Space(y);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static void ApproachToggle(Rect r, string label, ref bool value)
        {
            bool next = TabletUi.Toggle(r, label, value, TabletLink);
            if (!TabletLink || next == value) return;
            value = next;
            ChatLog.Write("g", "tablet approach: " + label + " = " + value);
        }

        static void ApproachField(Rect r, string label, ref string value, int limit)
        {
            TabletUi.Label(new Rect(r.x, r.y, r.width * .6f, r.height), label);
            string next = TabletUi.Field(new Rect(r.x + r.width * .6f, r.y, r.width * .4f, r.height), value, TabletLink);
            if (next.Length > limit) next = next.Substring(0, limit);
            if (!TabletLink || next == value) return;
            value = next;
            ChatLog.Write("g", "tablet approach: " + label + " = " + value);
        }
    }
}
