using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletRendezvousScroll;
        const double RendezvousPredictionHorizon = 3600;

        void TabletRendezvous(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            try
            {
                tabletRendezvousScroll = GUILayout.BeginScrollView(tabletRendezvousScroll);
                try
                {
                    float width = area.width - 26;
                    float row = Mathf.Max(44, tabletFont * 2.4f);
                    float gap = 8;
                    float tileHeight = Mathf.Max(88, tabletFont * 4.4f);
                    float y = 0;

                    Vessel active = FlightGlobals.ActiveVessel;
                    ITargetable targetable = FlightGlobals.fetch != null ? FlightGlobals.fetch.VesselTarget : null;
                    Vessel target = targetable != null ? targetable.GetVessel() : null;
                    Orbit activeOrbit = active == null ? null : active.orbit;
                    Orbit targetOrbit = target == null ? null : target.orbit;
                    bool compatibleOrbits = active != null && target != null && target != active &&
                        activeOrbit != null && targetOrbit != null &&
                        activeOrbit.referenceBody != null && activeOrbit.referenceBody == targetOrbit.referenceBody;
                    RendezvousEstimate estimate = null;
                    if (compatibleOrbits)
                    {
                        double now = Planetarium.GetUniversalTime();
                        Vector3d ownPosition = activeOrbit.getRelativePositionAtUT(now);
                        Vector3d targetPosition = targetOrbit.getRelativePositionAtUT(now);
                        Vector3d ownVelocity = activeOrbit.getOrbitalVelocityAtUT(now);
                        Vector3d targetVelocity = targetOrbit.getOrbitalVelocityAtUT(now);
                        Vector3d relativePosition = targetPosition - ownPosition;
                        Vector3d relativeVelocity = targetVelocity - ownVelocity;
                        estimate = RendezvousMath.Estimate(relativePosition.x, relativePosition.y, relativePosition.z,
                            relativeVelocity.x, relativeVelocity.y, relativeVelocity.z, RendezvousPredictionHorizon);
                    }

                    float cardHeight = row * 2 + tileHeight * 2 + gap * 5 + CardHeaderHeight;
                    Rect card = TabletUi.CardPanel(new Rect(0, y, width, cardHeight), "RENDEZVOUS");
                    float contentY = card.y;
                    if (active == null)
                        TabletUi.Label(new Rect(card.x, contentY, card.width, row), "No active vessel.", true);
                    else if (target == null)
                        TabletUi.Label(new Rect(card.x, contentY, card.width, row), "Set a vessel as target to begin.", true);
                    else
                    {
                        TabletUi.Label(new Rect(card.x, contentY, card.width, row),
                            (active.vesselName ?? "Active vessel") + " -> " + (target.vesselName ?? "Target vessel"));
                        contentY += row;
                        string body = activeOrbit == null || activeOrbit.referenceBody == null
                            ? "Unknown reference body" : activeOrbit.referenceBody.bodyName;
                        TabletUi.Label(new Rect(card.x, contentY, card.width, row), "Reference body: " + body, true);
                    }
                    contentY += row + gap;

                    if (estimate == null)
                    {
                        TabletUi.Label(new Rect(card.x, contentY, card.width, row * 2),
                            target == null ? "No rendezvous estimate." :
                            compatibleOrbits ? "Relative orbit state is unavailable." :
                            "Both vessels need orbits around the same body.", true);
                    }
                    else
                    {
                        float tileGap = 8;
                        float tileWidth = (card.width - tileGap) / 2;
                        TabletUi.Tile(new Rect(card.x, contentY, tileWidth, tileHeight), "RANGE",
                            Distance(estimate.Range), "", TabletUi.Series[0]);
                        TabletUi.Tile(new Rect(card.x + tileWidth + tileGap, contentY, tileWidth, tileHeight), "RELATIVE SPEED",
                            Speed(estimate.RelativeSpeed), "", TabletUi.Series[1]);
                        contentY += tileHeight + gap;
                        TabletUi.Tile(new Rect(card.x, contentY, tileWidth, tileHeight), "RANGE RATE",
                            RangeRate(estimate.RangeRate), "", estimate.RangeRate <= 0 ? TabletUi.Good : TabletUi.Warn);
                        TabletUi.Tile(new Rect(card.x + tileWidth + tileGap, contentY, tileWidth, tileHeight), "LINEAR CPA",
                            Distance(estimate.ClosestApproachRange), "", TabletUi.Series[2]);
                        contentY += tileHeight + gap;
                        TabletUi.Label(new Rect(card.x, contentY, card.width, row),
                            "Closest approach in " + FormatRendezvousTime(estimate.SecondsToClosestApproach) +
                            " (1-hour prediction limit).", true);
                        contentY += row + gap;
                        TabletUi.Label(new Rect(card.x, contentY, card.width, row),
                            "Linear estimate only; negative range rate means closing.", true);
                    }

                    y += cardHeight + gap;
                    if (!TabletLink)
                        TabletUi.Label(new Rect(0, y, width, row), "INFO ONLY - enable LIVE LINK to engage guidance.", true);
                    y += row + gap;
                    if (TabletUi.Button(new Rect(0, y, width, TabletUi.BtnH), "ENGAGE RENDEZVOUS AUTOPILOT",
                        false, TabletLink && compatibleOrbits && estimate != null) &&
                        TabletLink && compatibleOrbits && estimate != null)
                        ChatWindow.ToolFromMenu("mj_rendezvous", "{}");
                    GUILayout.Space(y + TabletUi.BtnH + gap);
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

        static string Distance(double metres)
        {
            if (!RendezvousFinite(metres)) return "--";
            if (metres >= 1000000) return (metres / 1000000).ToString("0.00", CultureInfo.InvariantCulture) + " Mm";
            if (metres >= 1000) return (metres / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km";
            return metres.ToString("0", CultureInfo.InvariantCulture) + " m";
        }

        static string Speed(double metresPerSecond)
        {
            return RendezvousFinite(metresPerSecond)
                ? metresPerSecond.ToString("0.00", CultureInfo.InvariantCulture) + " m/s" : "--";
        }

        static string RangeRate(double metresPerSecond)
        {
            if (!RendezvousFinite(metresPerSecond)) return "--";
            return System.Math.Abs(metresPerSecond).ToString("0.00", CultureInfo.InvariantCulture) +
                " m/s " + (metresPerSecond <= 0 ? "closing" : "opening");
        }

        static string FormatRendezvousTime(double seconds)
        {
            if (!RendezvousFinite(seconds)) return "--";
            if (seconds >= 60) return (seconds / 60).ToString("0.0", CultureInfo.InvariantCulture) + " min";
            return seconds.ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        static bool RendezvousFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

    }
}
