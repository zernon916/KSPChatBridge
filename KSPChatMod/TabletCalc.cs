using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletCalcScroll;
        static string calcR1T = "700000";
        static string calcR2T = "12000000";
        static string calcBodyT = "Mun";

        void TabletCalc(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletCalcScroll = GUILayout.BeginScrollView(tabletCalcScroll);

            Rect card = TabletUi.CardPanel(new Rect(0, 0, width, pad + row * 9 + gap * 8), "HOHMANN CALCULATOR");
            float y = pad;
            TabletUi.Label(new Rect(12, y, width - 24, row), "BODY", true);
            y += row + gap;
            calcBodyT = TabletUi.Field(new Rect(12, y, width - 24, row), calcBodyT, false);
            y += row + gap;
            TabletUi.Label(new Rect(12, y, width - 24, row), "START RADIUS (m)", true);
            y += row + gap;
            calcR1T = TabletUi.Field(new Rect(12, y, width - 24, row), calcR1T, true);
            y += row + gap;
            TabletUi.Label(new Rect(12, y, width - 24, row), "TARGET RADIUS (m)", true);
            y += row + gap;
            calcR2T = TabletUi.Field(new Rect(12, y, width - 24, row), calcR2T, true);
            y += row + gap;

            CelestialBody body = BodyInfo.Find(calcBodyT);
            double r1, r2;
            if (body == null)
                TabletUi.Label(new Rect(12, y, width - 24, row), "Unknown body", true);
            else if (!double.TryParse(calcR1T, NumberStyles.Float, CultureInfo.InvariantCulture, out r1) ||
                     !double.TryParse(calcR2T, NumberStyles.Float, CultureInfo.InvariantCulture, out r2))
                TabletUi.Label(new Rect(12, y, width - 24, row), "Enter valid orbit radii", true);
            else
            {
                double dv1, dv2, time;
                RocketMath.Hohmann(body.gravParameter, r1, r2, out dv1, out dv2, out time);
                TabletUi.Label(new Rect(12, y, width - 24, row), "Departure " + dv1.ToString("F0") + " m/s", true);
                y += row + gap;
                TabletUi.Label(new Rect(12, y, width - 24, row), "Arrival " + dv2.ToString("F0") + " m/s", true);
                y += row + gap;
                TabletUi.Label(new Rect(12, y, width - 24, row), "Transfer " + time.ToString("F0") + " s", true);
            }

            GUILayout.Space(card.height + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
