using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletOrbitPlanScroll;

        // TB12b: Orbit Plan info page (read only - no buttons, no setters).
        void TabletOrbitPlan(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            float tile = row * 1.6f;
            Vessel v = FlightGlobals.ActiveVessel;
            Orbit o = v != null ? v.orbit : null;
            GUILayout.BeginArea(area);
            tabletOrbitPlanScroll = GUILayout.BeginScrollView(tabletOrbitPlanScroll);
            float y = 0;
            if (o == null)
            {
                Rect c = TabletUi.CardPanel(new Rect(0, y, width, pad + row), "ORBIT PLAN");
                TabletUi.Label(new Rect(c.x, c.y, c.width, row), "NO ACTIVE VESSEL", true);
                y += pad + row + gap;
            }
            else
            {
                string body = o.referenceBody != null ? o.referenceBody.bodyName : "?";
                string[] names = { "APOAPSIS", "PERIAPSIS", "INCLINATION", "PERIOD", "BODY" };
                string[] vals = { o.ApA.ToString("0"), o.PeA.ToString("0"), o.inclination.ToString("0.00"), o.period.ToString("0"), body };
                string[] units = { "m", "m", "deg", "s", "" };
                for (int i = 0; i < names.Length; i++)
                {
                    TabletUi.Tile(new Rect(0, y, width, tile), names[i], vals[i], units[i], TabletUi.Accent);
                    y += tile + gap;
                }
            }

            Rect t = TabletUi.CardPanel(new Rect(0, y, width, pad + row * 4), "TARGETS");
            TabletUi.Label(new Rect(t.x, t.y, t.width, row), "AP TARGET: " + orbApT + " m", true);
            TabletUi.Label(new Rect(t.x, t.y + row, t.width, row), "PE TARGET: " + orbPeT + " m", true);
            TabletUi.Label(new Rect(t.x, t.y + row * 2, t.width, row), "INC TARGET: " + orbIncT + " deg", true);
            TabletUi.Label(new Rect(t.x, t.y + row * 3, t.width, row), "TRANSFER TO: " + xferBodyT, true);
            y += pad + row * 4 + gap;

            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK", true);
                y += row;
            }
            GUILayout.Space(y + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}