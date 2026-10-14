using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        sealed class DvMapRow
        {
            public string TargetName;
            public string CenterName;
            public string SourceOrbit;
            public double SourceRadius;
            public double DestinationRadius;
            public double DepartureDv;
            public double ArrivalDv;
            public double TransferSeconds;
            public double TotalDv;
        }

        static Vector2 tabletDvMapScroll;

        void TabletDvMap(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            try
            {
                tabletDvMapScroll = GUILayout.BeginScrollView(tabletDvMapScroll);
                try
                {
                    float width = area.width - 26, gap = 8, rowHeight = Mathf.Max(44, tabletFont * 2.5f);
                    float y = 0;
                    Vessel vessel = FlightGlobals.ActiveVessel;
                    CelestialBody origin = vessel == null ? null : vessel.mainBody;
                    List<DvMapRow> rows = BuildDvMapRows(vessel);
                    double stageDv = 0;
                    bool hasStageDv = TryStageDeltaV(out stageDv);
                    float overviewHeight = rowHeight * 4 + CardHeaderHeight;
                    Rect overview = TabletUi.CardPanel(new Rect(0, y, width, overviewHeight), "DELTA-V MAP");
                    TabletUi.Label(new Rect(overview.x, overview.y, overview.width, rowHeight),
                        vessel == null ? "No active vessel." : "Origin body: " +
                        (origin == null ? "unknown" : origin.bodyName) + "  |  Vessel: " +
                        (string.IsNullOrEmpty(vessel.vesselName) ? "(unnamed)" : vessel.vesselName), true);
                    TabletUi.Label(new Rect(overview.x, overview.y + rowHeight, overview.width, rowHeight),
                        hasStageDv ? "Current vessel stage delta-v (stock actual): " +
                        stageDv.ToString("#,0", CultureInfo.InvariantCulture) + " m/s" : "Current vessel stage delta-v: unavailable", true);
                    TabletUi.Label(new Rect(overview.x, overview.y + rowHeight * 2, overview.width, rowHeight),
                        "Ideal coplanar circular Hohmann transfers using source and target orbit semi-major axes.", true);
                    TabletUi.Label(new Rect(overview.x, overview.y + rowHeight * 3, overview.width, rowHeight),
                        "Estimates exclude launch, SOI escape/capture, plane changes, and surface landing.", true);
                    y += overviewHeight + gap;

                    if (vessel == null || origin == null)
                    {
                        Rect empty = TabletUi.CardPanel(new Rect(0, y, width, rowHeight * 2), "TARGETS");
                        TabletUi.Label(new Rect(empty.x, empty.y, empty.width, rowHeight),
                            "An active vessel and reference body are required.", true);
                        y += rowHeight * 2 + gap;
                    }
                    else if (rows.Count == 0)
                    {
                        Rect empty = TabletUi.CardPanel(new Rect(0, y, width, rowHeight * 2), "TARGETS");
                        TabletUi.Label(new Rect(empty.x, empty.y, empty.width, rowHeight),
                            "No comparable target orbits are available for this vessel.", true);
                        y += rowHeight * 2 + gap;
                    }
                    else
                    {
                        double maxDv = rows[rows.Count - 1].TotalDv;
                        for (int i = 0; i < rows.Count; i++)
                        {
                            DvMapRow estimate = rows[i];
                            float barHeight = Mathf.Max(34, rowHeight * .8f);
                            float cardHeight = CardHeaderHeight + rowHeight * 2 + barHeight + 8;
                            Rect card = TabletUi.CardPanel(new Rect(0, y, width, cardHeight),
                                estimate.TargetName + "  |  " + estimate.CenterName + "-CENTERED");
                            string departureDirection = estimate.DepartureDv < 0 ? "retrograde" : "prograde";
                            string arrivalDirection = estimate.ArrivalDv < 0 ? "retrograde" : "prograde";
                            TabletUi.Bar(new Rect(card.x, card.y, card.width, barHeight),
                                "TOTAL TRANSFER DV", maxDv > 0 ? (float)(estimate.TotalDv / maxDv) : 0,
                                estimate.TotalDv.ToString("#,0", CultureInfo.InvariantCulture) + " m/s",
                                i % 2 == 0 ? TabletUi.Series[0] : TabletUi.Series[1]);
                            TabletUi.Label(new Rect(card.x, card.y + barHeight + 2, card.width, rowHeight),
                                "Departure: " + Math.Abs(estimate.DepartureDv).ToString("#,0", CultureInfo.InvariantCulture) +
                                " m/s " + departureDirection + "  |  Arrival: " +
                                Math.Abs(estimate.ArrivalDv).ToString("#,0", CultureInfo.InvariantCulture) +
                                " m/s " + arrivalDirection, true);
                            TabletUi.Label(new Rect(card.x, card.y + barHeight + rowHeight + 2, card.width, rowHeight),
                                "Transfer: " + EncDur(estimate.TransferSeconds) + "  |  r1 " +
                                EncDist(estimate.SourceRadius) + " -> r2 " + EncDist(estimate.DestinationRadius) +
                                "  |  " + estimate.SourceOrbit, true);
                            y += cardHeight + gap;
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

        List<DvMapRow> BuildDvMapRows(Vessel vessel)
        {
            var rows = new List<DvMapRow>();
            if (vessel == null || vessel.mainBody == null) return rows;

            CelestialBody origin = vessel.mainBody;
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null) return rows;

            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody target = bodies[i];
                if (target == null || target == origin || target.orbit == null) continue;

                CelestialBody center;
                double sourceRadius;
                string sourceOrbit;
                if (target.referenceBody == origin && vessel.orbit != null)
                {
                    center = origin;
                    sourceRadius = vessel.orbit.semiMajorAxis;
                    sourceOrbit = "active vessel orbit";
                }
                else if (origin.referenceBody != null && origin.orbit != null &&
                    target.referenceBody == origin.referenceBody)
                {
                    center = origin.referenceBody;
                    sourceRadius = origin.orbit.semiMajorAxis;
                    sourceOrbit = origin.bodyName + " orbit";
                }
                else
                    continue;

                double departureDv, arrivalDv, transferSeconds;
                if (!DvMapMath.TryHohmann(center.gravParameter, sourceRadius, target.orbit.semiMajorAxis,
                    out departureDv, out arrivalDv, out transferSeconds))
                    continue;

                double totalDv = DvMapMath.TotalDeltaV(departureDv, arrivalDv);
                if (double.IsNaN(totalDv) || double.IsInfinity(totalDv)) continue;
                rows.Add(new DvMapRow
                {
                    TargetName = target.bodyName ?? "Unknown body",
                    CenterName = center.bodyName ?? "Unknown",
                    SourceOrbit = sourceOrbit,
                    SourceRadius = sourceRadius,
                    DestinationRadius = target.orbit.semiMajorAxis,
                    DepartureDv = departureDv,
                    ArrivalDv = arrivalDv,
                    TransferSeconds = transferSeconds,
                    TotalDv = totalDv
                });
            }

            rows.Sort(delegate(DvMapRow left, DvMapRow right)
            {
                int byDeltaV = left.TotalDv.CompareTo(right.TotalDv);
                return byDeltaV != 0 ? byDeltaV :
                    string.Compare(left.TargetName, right.TargetName, StringComparison.OrdinalIgnoreCase);
            });
            return rows;
        }

        static bool TryStageDeltaV(out double total)
        {
            total = 0;
            List<StageInfo> stages = StageReader.ForFlight();
            if (stages == null || stages.Count == 0) return false;
            for (int i = 0; i < stages.Count; i++)
            {
                StageInfo stage = stages[i];
                if (stage == null) return false;
                double dv = stage.DvActual;
                if (double.IsNaN(dv) || double.IsInfinity(dv) || dv < 0) return false;
                total += dv;
            }
            return !double.IsNaN(total) && !double.IsInfinity(total);
        }

    }
}
