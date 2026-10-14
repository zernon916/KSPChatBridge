using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        const int PorkchopSamples = 16;
        const double PorkchopDay = 86400;
        const double PorkchopDepartureDays = 180;
        const double PorkchopMinFlightDays = 30;
        const double PorkchopMaxFlightDays = 400;
        static readonly Color[] PorkchopColors =
        {
            new Color(.25f, .78f, .38f),
            new Color(.72f, .82f, .30f),
            new Color(1f, .70f, .20f),
            new Color(.96f, .40f, .22f),
            new Color(.65f, .25f, .36f)
        };
        static Vector2 tabletPorkchopScroll;
        static CelestialBody tabletPorkchopDeparture, tabletPorkchopArrival;
        static CelestialBody plottedPorkchopDeparture, plottedPorkchopArrival;
        static double[,] tabletPorkchopGrid;
        static int[,] tabletPorkchopLevels;
        static double tabletPorkchopStartUt;

        void TabletPorkchop(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            try
            {
                tabletPorkchopScroll = GUILayout.BeginScrollView(tabletPorkchopScroll);
                try
                {
                    float width = area.width - 26;
                    float row = Mathf.Max(44, tabletFont * 2.5f);
                    float gap = 8;
                    float header = 16 + tabletFont * 1.6f;
                    List<CelestialBody> departures = PorkchopBodies();
                    if (departures.Count < 2)
                    {
                        Rect empty = TabletUi.CardPanel(new Rect(0, 0, width, header + row), "PORKCHOP TRANSFER WINDOW");
                        TabletUi.Label(new Rect(empty.x, empty.y, empty.width, row),
                            "At least two bodies with valid orbits around the same parent are required.", true);
                        GUILayout.Space(header + row + gap);
                    }
                    else
                    {
                        EnsurePorkchopSelection(departures);
                        float y = DrawPorkchopSelectors(width, row, gap, header, departures, 0);
                        Rect plotCard = DrawPorkchopPlot(width, row, gap, header, y);
                        GUILayout.Space(plotCard.yMax + gap);
                    }
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

        static void EnsurePorkchopSelection(List<CelestialBody> bodies)
        {
            if (tabletPorkchopDeparture == null || !bodies.Contains(tabletPorkchopDeparture))
            {
                Vessel active = FlightGlobals.ActiveVessel;
                tabletPorkchopDeparture = active != null && active.mainBody != null && bodies.Contains(active.mainBody)
                    ? active.mainBody : bodies[0];
            }

            List<CelestialBody> arrivals = PorkchopArrivals(tabletPorkchopDeparture, bodies);
            if (arrivals.Count == 0)
            {
                tabletPorkchopDeparture = bodies[0];
                arrivals = PorkchopArrivals(tabletPorkchopDeparture, bodies);
            }
            if (tabletPorkchopArrival == null || !arrivals.Contains(tabletPorkchopArrival))
                tabletPorkchopArrival = arrivals.Count == 0 ? null : arrivals[0];
            if (!object.ReferenceEquals(plottedPorkchopDeparture, tabletPorkchopDeparture) ||
                !object.ReferenceEquals(plottedPorkchopArrival, tabletPorkchopArrival))
                InvalidatePorkchopGrid();
        }

        static List<CelestialBody> PorkchopBodies()
        {
            var candidates = new List<CelestialBody>();
            if (FlightGlobals.Bodies == null) return candidates;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody body = FlightGlobals.Bodies[i];
                if (body == null || body.orbit == null || body.orbit.referenceBody == null ||
                    !Finite(body.orbit.semiMajorAxis) || body.orbit.semiMajorAxis <= 0)
                    continue;
                candidates.Add(body);
            }

            var bodies = new List<CelestialBody>();
            for (int i = 0; i < candidates.Count; i++)
                if (PorkchopArrivals(candidates[i], candidates).Count > 0)
                    bodies.Add(candidates[i]);
            return bodies;
        }

        static List<CelestialBody> PorkchopArrivals(CelestialBody departure, List<CelestialBody> bodies)
        {
            var arrivals = new List<CelestialBody>();
            if (departure == null || departure.orbit == null || bodies == null) return arrivals;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody body = bodies[i];
                if (body != departure && body.orbit != null &&
                    body.orbit.referenceBody == departure.orbit.referenceBody)
                    arrivals.Add(body);
            }
            return arrivals;
        }

        static float DrawPorkchopSelectors(float width, float row, float gap, float header,
            List<CelestialBody> departures, float y)
        {
            Rect card = TabletUi.CardPanel(new Rect(0, y, width, header + row * 2 + gap), "TRANSFER BODIES");
            float labelWidth = Mathf.Min(124, card.width * .24f);
            float buttonWidth = Mathf.Min(76, card.width * .15f);
            float rowY = card.y;

            int departureIndex = departures.IndexOf(tabletPorkchopDeparture);
            TabletUi.Label(new Rect(card.x, rowY, labelWidth, row), "DEPARTURE");
            if (TabletUi.Button(new Rect(card.x + labelWidth, rowY, buttonWidth, row), "PREV"))
                ChangePorkchopDeparture(departures, (departureIndex + departures.Count - 1) % departures.Count);
            TabletUi.Label(new Rect(card.x + labelWidth + buttonWidth + 4, rowY,
                card.width - labelWidth - buttonWidth * 2 - 8, row),
                tabletPorkchopDeparture.bodyName ?? "Unknown body");
            if (TabletUi.Button(new Rect(card.xMax - buttonWidth, rowY, buttonWidth, row), "NEXT"))
                ChangePorkchopDeparture(departures, (departureIndex + 1) % departures.Count);

            List<CelestialBody> arrivals = PorkchopArrivals(tabletPorkchopDeparture, departures);
            rowY += row + gap;
            TabletUi.Label(new Rect(card.x, rowY, labelWidth, row), "ARRIVAL");
            int arrivalIndex = arrivals.IndexOf(tabletPorkchopArrival);
            bool hasArrivals = arrivals.Count > 0;
            if (TabletUi.Button(new Rect(card.x + labelWidth, rowY, buttonWidth, row), "PREV", false, hasArrivals))
                ChangePorkchopArrival(arrivals, (arrivalIndex + arrivals.Count - 1) % arrivals.Count);
            TabletUi.Label(new Rect(card.x + labelWidth + buttonWidth + 4, rowY,
                card.width - labelWidth - buttonWidth * 2 - 8, row),
                hasArrivals ? tabletPorkchopArrival.bodyName ?? "Unknown body" : "No compatible body");
            if (TabletUi.Button(new Rect(card.xMax - buttonWidth, rowY, buttonWidth, row), "NEXT", false, hasArrivals))
                ChangePorkchopArrival(arrivals, (arrivalIndex + 1) % arrivals.Count);

            return card.yMax + gap;
        }

        static void ChangePorkchopDeparture(List<CelestialBody> bodies, int index)
        {
            if (bodies == null || index < 0 || index >= bodies.Count) return;
            tabletPorkchopDeparture = bodies[index];
            List<CelestialBody> arrivals = PorkchopArrivals(tabletPorkchopDeparture, bodies);
            tabletPorkchopArrival = arrivals.Count == 0 ? null : arrivals[0];
            InvalidatePorkchopGrid();
        }

        static void ChangePorkchopArrival(List<CelestialBody> bodies, int index)
        {
            if (bodies == null || index < 0 || index >= bodies.Count) return;
            tabletPorkchopArrival = bodies[index];
            InvalidatePorkchopGrid();
        }

        static void InvalidatePorkchopGrid()
        {
            tabletPorkchopGrid = null;
            tabletPorkchopLevels = null;
        }

        static Rect DrawPorkchopPlot(float width, float row, float gap, float header, float y)
        {
            float chartSize = Mathf.Min(500, width - 72);
            float cardHeight = header + chartSize + row * 5.4f + gap * 2;
            Rect card = TabletUi.CardPanel(new Rect(0, y, width, cardHeight), "PORKCHOP DELTA-V");
            Rect refresh = new Rect(card.xMax - Mathf.Min(142, card.width * .24f), card.y,
                Mathf.Min(142, card.width * .24f), row);
            if (TabletUi.Button(refresh, "REFRESH"))
                ComputePorkchopGrid();

            if (tabletPorkchopGrid == null) ComputePorkchopGrid();
            bool available = HasPorkchopGrid();
            float summaryWidth = card.width - refresh.width - 8;
            TabletUi.Label(new Rect(card.x, card.y, summaryWidth, row),
                available ? BestPorkchopLabel() : "No finite transfers in the selected range.", true);

            float chartX = card.x + 42;
            float chartY = card.y + row * 2 + gap;
            float plotWidth = chartSize - 42;
            TabletUi.Label(new Rect(chartX, card.y + row, plotWidth, row),
                "Transfer duration: 30 to 400 days", true);
            Rect chart = new Rect(chartX, chartY, plotWidth, chartSize);
            DrawPorkchopCells(chart);
            TabletUi.Label(new Rect(card.x, chart.y, 38, row), "0 d", true);
            TabletUi.Label(new Rect(card.x, chart.yMax - row, 38, row),
                PorkchopDepartureDays.ToString("0", CultureInfo.InvariantCulture) + " d", true);
            TabletUi.Label(new Rect(chart.x, chart.yMax + 2, chart.width * .5f, row), "30 d", true);
            TabletUi.LabelRight(new Rect(chart.x + chart.width * .5f, chart.yMax + 2, chart.width * .5f, row),
                "400 d", true);

            float legendY = chart.yMax + row + 4;
            float swatchWidth = Mathf.Max(14, (chart.width - 4 * 6) / PorkchopColorMap.LevelCount);
            for (int i = 0; i < PorkchopColorMap.LevelCount; i++)
                TabletUi.Rect(new Rect(chart.x + i * (swatchWidth + 6), legendY, swatchWidth, row * .35f),
                    PorkchopColors[i]);
            TabletUi.Label(new Rect(chart.x, legendY + row * .35f, chart.width, row),
                "Green = lower delta-v; red/purple = higher delta-v. Grey = unavailable.", true);
            float footY = legendY + row * 1.35f;
            string model = "Rows: departure delay (0-" + PorkchopDepartureDays.ToString("0", CultureInfo.InvariantCulture) +
                " days); columns: flight time. Circular, coplanar estimate; parking-orbit burns use 100 km altitude.";
            TabletUi.Label(new Rect(card.x, footY, card.width, row), model, true);
            return card;
        }

        static void DrawPorkchopCells(Rect chart)
        {
            if (tabletPorkchopGrid == null || tabletPorkchopLevels == null) return;
            int departures = tabletPorkchopGrid.GetLength(0), flights = tabletPorkchopGrid.GetLength(1);
            float cellWidth = chart.width / flights, cellHeight = chart.height / departures;
            for (int i = 0; i < departures; i++)
            for (int j = 0; j < flights; j++)
            {
                int level = tabletPorkchopLevels[i, j];
                Color color = level < 0 ? TabletUi.CardHi : PorkchopColors[level];
                TabletUi.Rect(new Rect(chart.x + j * cellWidth, chart.y + i * cellHeight,
                    cellWidth + .5f, cellHeight + .5f), color);
            }
        }

        static void ComputePorkchopGrid()
        {
            if (tabletPorkchopDeparture == null || tabletPorkchopArrival == null ||
                tabletPorkchopDeparture.orbit == null || tabletPorkchopArrival.orbit == null ||
                tabletPorkchopDeparture.orbit.referenceBody == null ||
                tabletPorkchopArrival.orbit.referenceBody != tabletPorkchopDeparture.orbit.referenceBody)
            {
                InvalidatePorkchopGrid();
                return;
            }

            double now = Planetarium.GetUniversalTime();
            Vector3d departurePosition = tabletPorkchopDeparture.orbit.getRelativePositionAtUT(now);
            Vector3d arrivalPosition = tabletPorkchopArrival.orbit.getRelativePositionAtUT(now);
            double centralMu = tabletPorkchopDeparture.orbit.referenceBody.gravParameter;
            double departureRadius = tabletPorkchopDeparture.orbit.semiMajorAxis;
            double arrivalRadius = tabletPorkchopArrival.orbit.semiMajorAxis;
            if (!Finite(now) || !Finite(centralMu) || centralMu <= 0 ||
                !Finite(departureRadius) || departureRadius <= 0 ||
                !Finite(arrivalRadius) || arrivalRadius <= 0 ||
                !Finite(departurePosition.x) || !Finite(departurePosition.y) ||
                !Finite(arrivalPosition.x) || !Finite(arrivalPosition.y) ||
                (departurePosition.x == 0 && departurePosition.y == 0) ||
                (arrivalPosition.x == 0 && arrivalPosition.y == 0))
            {
                InvalidatePorkchopGrid();
                return;
            }

            double departureParking = ParkingRadius(tabletPorkchopDeparture);
            double arrivalParking = ParkingRadius(tabletPorkchopArrival);
            double departureMu = departureParking > 0 ? tabletPorkchopDeparture.gravParameter : 0;
            double arrivalMu = arrivalParking > 0 ? tabletPorkchopArrival.gravParameter : 0;
            tabletPorkchopGrid = Porkchop.Grid(centralMu, departureRadius,
                Math.Atan2(departurePosition.y, departurePosition.x), arrivalRadius,
                Math.Atan2(arrivalPosition.y, arrivalPosition.x),
                0, PorkchopDepartureDays * PorkchopDay, PorkchopSamples,
                PorkchopMinFlightDays * PorkchopDay, PorkchopMaxFlightDays * PorkchopDay, PorkchopSamples,
                departureMu, departureParking, arrivalMu, arrivalParking);
            tabletPorkchopLevels = PorkchopColorMap.Levels(tabletPorkchopGrid);
            plottedPorkchopDeparture = tabletPorkchopDeparture;
            plottedPorkchopArrival = tabletPorkchopArrival;
            tabletPorkchopStartUt = now;
        }

        static double ParkingRadius(CelestialBody body)
        {
            if (body == null || !Finite(body.Radius) || body.Radius <= 0 ||
                !Finite(body.gravParameter) || body.gravParameter <= 0)
                return 0;
            return body.Radius + 100000;
        }

        static bool HasPorkchopGrid()
        {
            if (tabletPorkchopGrid == null) return false;
            for (int i = 0; i < tabletPorkchopGrid.GetLength(0); i++)
            for (int j = 0; j < tabletPorkchopGrid.GetLength(1); j++)
                if (Finite(tabletPorkchopGrid[i, j])) return true;
            return false;
        }

        static string BestPorkchopLabel()
        {
            double best = double.PositiveInfinity;
            int bestDeparture = -1, bestFlight = -1;
            for (int i = 0; i < tabletPorkchopGrid.GetLength(0); i++)
            for (int j = 0; j < tabletPorkchopGrid.GetLength(1); j++)
            {
                double value = tabletPorkchopGrid[i, j];
                if (Finite(value) && value < best)
                {
                    best = value;
                    bestDeparture = i;
                    bestFlight = j;
                }
            }
            if (bestDeparture < 0) return "No finite transfers in the selected range.";
            double departureDays = PorkchopDepartureDays * bestDeparture / (PorkchopSamples - 1);
            double flightDays = PorkchopMinFlightDays +
                (PorkchopMaxFlightDays - PorkchopMinFlightDays) * bestFlight / (PorkchopSamples - 1);
            return "Best: " + best.ToString("0", CultureInfo.InvariantCulture) + " m/s; depart UT " +
                (tabletPorkchopStartUt + departureDays * PorkchopDay).ToString("0", CultureInfo.InvariantCulture) +
                " (+" + departureDays.ToString("0", CultureInfo.InvariantCulture) + " d); flight " +
                flightDays.ToString("0", CultureInfo.InvariantCulture) + " d";
        }

    }
}
