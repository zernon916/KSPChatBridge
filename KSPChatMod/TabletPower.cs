using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletPowerScroll;

        void TabletPower(Rect area)
        {
            var c = TabletUi.CardPanel(area, "POWER");
            float row = Mathf.Max(44, tabletFont * 2.6f);
            Rect content = new Rect(c.x + 6, c.y + 6, c.width - 12, c.height - 12);
            if (FlightGlobals.ActiveVessel == null)
            {
                TabletUi.Label(new Rect(content.x, content.y, content.width, row), "No active vessel.", true);
                return;
            }

            float frac = ecMax > 0 ? Mathf.Clamp01((float)(ecAmt / ecMax)) : 0f;
            Color chargeColor = frac > 0.2f ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.9f, 0.3f, 0.2f);
            TabletUi.Tile(new Rect(content.x, content.y, content.width, row * 1.5f), "ELECTRIC CHARGE", ecAmt.ToString("F0"), "EC", chargeColor);
            string charge = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:F0} / {1:F0} EC ({2:P0})",
                ecAmt, ecMax, frac);
            TabletUi.Bar(new Rect(content.x, content.y + row * 1.6f, content.width, row), "CHARGE", frac, charge, chargeColor);

            float rate = (float)System.Math.Abs(ecRate);
            string generation = ecRate > 1e-4 ? rate.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " EC/s" : "0.00 EC/s";
            string use = ecRate < -1e-4 ? rate.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " EC/s" : "0.00 EC/s";
            TabletUi.Chip(new Rect(content.x, content.y + row * 2.8f, content.width * 0.48f, row), "GEN " + generation, new Color(0.3f, 0.8f, 0.3f));
            TabletUi.Chip(new Rect(content.x + content.width * 0.52f, content.y + row * 2.8f, content.width * 0.48f, row), "USE " + use, new Color(0.9f, 0.3f, 0.2f));

            string eta = System.Math.Abs(ecRate) < 1e-4 ? "steady" :
                ecRate < 0 ? "empty in " + Dur(ecAmt / -ecRate) :
                ecMax - ecAmt < 0.5 ? "full" : "full in " + Dur((ecMax - ecAmt) / ecRate);
            TabletUi.Label(new Rect(content.x, content.y + row * 3.9f, content.width, row), "Net rate " + ecRate.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture) + " EC/s   " + eta);
        }
    }
}