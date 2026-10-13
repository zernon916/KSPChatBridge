using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 taxiScroll;

        void TabletTaxi(Rect area)
        {
            // Loading the shared list is informational and works with LIVE LINK off.
            if (taxiBody == null && taxiPts.Count == 0 && Time.realtimeSinceStartup > nextTaxiLoad)
            {
                nextTaxiLoad = Time.realtimeSinceStartup + 10f;
                LoadTaxi();
            }
            string body = taxiBody;
            if (body != null)
            {
                taxiBody = null;
                taxiPts.Clear();
                foreach (string line in body.Split('\n'))
                {
                    string[] fields = line.TrimEnd('\r').Split('\t');
                    if (fields.Length >= 3) taxiPts.Add(fields);
                }
            }
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f, y = 0;
            GUILayout.BeginArea(area);
            taxiScroll = GUILayout.BeginScrollView(taxiScroll);
            var lines = NativeFlightController.ApSummary().Split('\n');
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, pad + lines.Length * row), "Taxi status");
            foreach (string line in lines)
            {
                TabletUi.Label(new Rect(c.x, c.y, c.width, row), line);
                c.y += row;
            }
            y += pad + lines.Length * row + gap;
            if (!TabletLink)
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), "INFO ONLY - enable LIVE LINK", true);
                y += row + gap;
            }
            foreach (string key in MfdNav.Right("taxi"))
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (TabletUi.Button(new Rect(12, y, width - 24, row), key, false, TabletLink) && TabletLink)
                {
                    ChatLog.Write("g", "tablet taxi: " + key);
                    OnKey("taxi", key);
                }
                y += row + gap;
            }
            TaxiField(new Rect(12, y, width - 24, row), "Speed cap m/s", ref taxiSpdT, 4);
            y += row + gap;
            if (TabletUi.Button(new Rect(12, y, width - 24, row), "REFRESH POINTS", false, TabletLink) && TabletLink)
            {
                ChatLog.Write("g", "tablet taxi: refresh points");
                LoadTaxi();
            }
            y += row + gap;
            var v = FlightGlobals.ActiveVessel;
            foreach (string[] point in taxiPts)
            {
                string where = "Position unavailable";
                if (v != null && v.mainBody != null)
                {
                    double lat = ParseD(point[1], 0), lon = ParseD(point[2], 0);
                    double distance = GcDist(v.latitude, v.longitude, lat, lon, v.mainBody.Radius);
                    where = distance < 10000
                        ? string.Format(CultureInfo.InvariantCulture, "{0:F0} m, brg {1:000}", distance, Brg(v.latitude, v.longitude, lat, lon))
                        : string.Format(CultureInfo.InvariantCulture, "{0:F1} km", distance / 1000);
                }
                c = TabletUi.CardPanel(new Rect(0, y, width, pad + 3 * row + gap), "Taxi point");
                TabletUi.Label(new Rect(c.x, c.y, c.width, row), point[0]);
                TabletUi.Label(new Rect(c.x, c.y + row, c.width, row), where, true);
                if (TabletUi.Button(new Rect(c.x, c.y + 2 * row, c.width, row), "GO", false, TabletLink) && TabletLink)
                    TabletTaxiGo(point[0]);
                y += pad + 3 * row + 2 * gap;
            }
            TaxiField(new Rect(12, y, width - 24, row), "Route", ref taxiRouteT, 200); y += row + gap;
            if (TabletUi.Button(new Rect(12, y, width - 24, row), "GO ROUTE", false, TabletLink && taxiRouteT.Trim().Length > 0) && TabletLink)
                TabletTaxiGo(taxiRouteT.Trim());
            y += row + gap;
            foreach (string hint in new[] { "Route: names separated by ';' or lat,lon.", "Straight lines; no obstacle avoidance.", "Rovers: wheels. Planes: engines + brakes." })
            {
                TabletUi.Label(new Rect(12, y, width - 24, row), hint, true);
                y += row;
            }
            TaxiField(new Rect(12, y, width - 24, row), "Point name", ref taxiMarkT, 40); y += row + gap;
            if (TabletUi.Button(new Rect(12, y, width - 24, row), "MARK HERE", false, TabletLink && taxiMarkT.Trim().Length > 0) && TabletLink)
            {
                ChatLog.Write("g", "tablet taxi: mark " + taxiMarkT.Trim());
                ChatWindow.ToolFromMenu("save_landing_spot", "{\"name\":" + ChatWindow.Json(taxiMarkT.Trim()) + ",\"mode\":\"V\"}");
                taxiMarkT = "";
                LoadTaxi();
            }
            GUILayout.Space(y + row + gap);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static void TaxiField(Rect r, string label, ref string value, int limit)
        {
            TabletUi.Label(new Rect(r.x, r.y, r.width * .5f, r.height), label);
            string next = TabletUi.Field(new Rect(r.x + r.width * .5f, r.y, r.width * .5f, r.height), value, TabletLink);
            if (next.Length > limit) next = next.Substring(0, limit);
            if (!TabletLink || next == value) return;
            value = next;
            ChatLog.Write("g", "tablet taxi: " + label + " = " + value);
        }

        static void TabletTaxiGo(string name)
        {
            if (!TabletLink) return;
            ChatLog.Write("g", "tablet taxi: go " + name + " at " + Num(taxiSpdT, "8") + " m/s");
            ChatWindow.ToolFromMenu("taxi_to", "{\"name\":" + ChatWindow.Json(name) + ",\"speed\":" + Num(taxiSpdT, "8") + "}");
        }
    }
}
