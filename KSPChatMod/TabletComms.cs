using System;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletCommsScroll;

        void TabletComms(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            try
            {
                tabletCommsScroll = GUILayout.BeginScrollView(tabletCommsScroll);
                try
                {
                    float width = area.width - 26, gap = 8, row = Mathf.Max(44, tabletFont * 2.5f);
                    float y = 0;
                    Vessel vessel = FlightGlobals.ActiveVessel;
                    if (vessel == null)
                    {
                        Rect empty = TabletUi.CardPanel(new Rect(0, y, width, row * 2), "COMMUNICATIONS");
                        TabletUi.Label(new Rect(empty.x, empty.y, empty.width, row), "No active vessel.", true);
                        y += row * 2 + gap;
                    }
                    else
                    {
                        var connection = vessel.Connection;
                        if (connection == null)
                        {
                            Rect unavailable = TabletUi.CardPanel(new Rect(0, y, width, row * 2), "COMMUNICATIONS");
                            TabletUi.Label(new Rect(unavailable.x, unavailable.y, unavailable.width, row),
                                "CommNet data is unavailable for this vessel.", true);
                            y += row * 2 + gap;
                        }
                        else
                        {
                            y = CommsLinkCard(connection, width, row, y, gap);
                            y = CommsMetricsCard(connection, width, row, y, gap);
                            y = CommsCapabilitiesCard(connection, width, row, y, gap);
                        }
                    }
                    GUILayout.Space(y + gap);
                }
                finally
                {
                    GUILayout.EndScrollView();
                }
            }
            finally
            {
                GUILayout.EndArea();
            }
        }

        float CommsLinkCard(CommNet.CommNetVessel connection, float width, float row, float y, float gap)
        {
            float height = row * 3 + CardHeaderHeight;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), "LINK");
            TabletUi.Chip(new Rect(c.x, c.y, c.width, row * .72f), connection.IsConnected ? "CONNECTED" : "NO CONNECTION",
                connection.IsConnected ? TabletUi.Good : TabletUi.Bad);
            string path = connection.ControlPath == null ? "NO PATH" :
                CommsStatus.FormatName(connection.ControlPath.commType.ToString());
            TabletUi.Label(new Rect(c.x, c.y + row, c.width, row),
                "Signal tier: " + CommsStatus.FormatName(connection.Signal.ToString()), true);
            TabletUi.Label(new Rect(c.x, c.y + row * 2, c.width, row),
                "Control path: " + path + "  |  Home link: " + (connection.IsConnectedHome ? "yes" : "no"), true);
            return y + height + gap;
        }

        float CommsMetricsCard(CommNet.CommNetVessel connection, float width, float row, float y, float gap)
        {
            float height = row * 2 + CardHeaderHeight;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), "SIGNAL");
            float barHeight = Mathf.Max(34, row * .8f);
            TabletUi.Bar(new Rect(c.x, c.y, c.width, barHeight), "Link strength",
                CommsStatus.SignalFraction(connection.SignalStrength), CommsStatus.SignalPercent(connection.SignalStrength), TabletUi.Accent);
            TabletUi.Label(new Rect(c.x, c.y + row, c.width, row),
                "Signal delay: " + CommsStatus.FormatDelay(connection.SignalDelay), true);
            return y + height + gap;
        }

        float CommsCapabilitiesCard(CommNet.CommNetVessel connection, float width, float row, float y, float gap)
        {
            float height = row * 3 + CardHeaderHeight;
            Rect c = TabletUi.CardPanel(new Rect(0, y, width, height), "CAPABILITIES");
            TabletUi.Label(new Rect(c.x, c.y, c.width, row),
                "Communications: " + (connection.CanComm ? "available" : "unavailable"), true);
            TabletUi.Label(new Rect(c.x, c.y + row, c.width, row),
                "Science transmission: " + (connection.CanScience ? "available" : "unavailable"), true);
            TabletUi.Label(new Rect(c.x, c.y + row * 2, c.width, row),
                "Plasma blackout: " + (connection.InPlasma ? "yes" : "no"), true);
            return y + height + gap;
        }
    }
}
