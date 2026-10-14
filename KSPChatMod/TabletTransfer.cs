using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        sealed class TransferRoute
        {
            public CelestialBody Center;
            public Orbit SourceOrbit;
            public string SourceName;
        }

        static Vector2 tabletTransferScroll;
        static string tabletTransferTargetName;

        void TabletTransfer(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            try
            {
                tabletTransferScroll = GUILayout.BeginScrollView(tabletTransferScroll);
                try
                {
                    float width = area.width - 26, gap = 8, row = Mathf.Max(44, tabletFont * 2.5f);
                    float y = 0;
                    Vessel vessel = FlightGlobals.ActiveVessel;
                    List<CelestialBody> targets = TransferTargets(vessel);
                    if (targets.Count == 0)
                    {
                        Rect empty = TabletUi.CardPanel(new Rect(0, y, width, row * 2 + CardHeaderHeight), "TRANSFER DIAGRAM");
                        TabletUi.Label(new Rect(empty.x, empty.y, empty.width, row),
                            "No compatible target orbit is available for the active vessel.", true);
                        TabletUi.Label(new Rect(empty.x, empty.y + row, empty.width, row),
                            "Select an orbiting vessel with a body sharing its central body.", true);
                        y += row * 2 + CardHeaderHeight + gap;
                    }
                    else
                    {
                        int selected = FindTargetIndex(targets, tabletTransferTargetName);
                        tabletTransferTargetName = targets[selected].bodyName;
                        float selectorHeight = row + CardHeaderHeight;
                        Rect selector = TabletUi.CardPanel(new Rect(0, y, width, selectorHeight), "DESTINATION");
                        float buttonWidth = Mathf.Min(row * 1.6f, selector.width * .2f);
                        if (TabletUi.Button(new Rect(selector.x, selector.y, buttonWidth, row), "PREV", false, targets.Count > 1))
                            selected = (selected + targets.Count - 1) % targets.Count;
                        TabletUi.Label(new Rect(selector.x + buttonWidth + 6, selector.y,
                            selector.width - buttonWidth * 2 - 12, row), targets[selected].bodyName ?? "Unknown body");
                        if (TabletUi.Button(new Rect(selector.xMax - buttonWidth, selector.y, buttonWidth, row),
                            "NEXT", false, targets.Count > 1))
                            selected = (selected + 1) % targets.Count;
                        tabletTransferTargetName = targets[selected].bodyName;
                        y += selectorHeight + gap;

                        CelestialBody target = targets[selected];
                        TransferRoute route;
                        if (!TryTransferRoute(vessel, target, out route))
                        {
                            Rect unavailable = TabletUi.CardPanel(new Rect(0, y, width, row + CardHeaderHeight), "TRANSFER DIAGRAM");
                            TabletUi.Label(new Rect(unavailable.x, unavailable.y, unavailable.width, row),
                                "Orbit data is unavailable for this transfer.", true);
                            y += row + CardHeaderHeight + gap;
                        }
                        else
                        {
                            double now = Planetarium.GetUniversalTime();
                            double departureDv, arrivalDv, transferSeconds;
                            double sourceRadius = route.SourceOrbit.getRelativePositionAtUT(now).magnitude;
                            double destinationRadius = target.orbit.semiMajorAxis;
                            bool validTransfer = DvMapMath.TryHohmann(route.Center.gravParameter, sourceRadius,
                                destinationRadius, out departureDv, out arrivalDv, out transferSeconds);

                            float plotSize = Mathf.Min(selector.width, Mathf.Max(250, width * .62f));
                            float diagramHeight = plotSize + CardHeaderHeight;
                            Rect diagram = TabletUi.CardPanel(new Rect(0, y, width, diagramHeight),
                                (route.Center.bodyName ?? "Unknown").ToUpperInvariant() + "-CENTERED TRANSFER");
                            Rect plot = new Rect(diagram.x + (diagram.width - plotSize) / 2, diagram.y, plotSize, plotSize);
                            if (validTransfer)
                                DrawTransferDiagram(plot, route, target, now, sourceRadius, destinationRadius, transferSeconds);
                            else
                                TabletUi.Label(new Rect(diagram.x, diagram.y, diagram.width, row),
                                    "A finite Hohmann estimate is unavailable for these orbits.", true);
                            y += diagramHeight + gap;

                            float infoHeight = row * 5 + CardHeaderHeight;
                            Rect info = TabletUi.CardPanel(new Rect(0, y, width, infoHeight), "TRANSFER ESTIMATE");
                            TabletUi.Label(new Rect(info.x, info.y, info.width, row),
                                "Source: " + route.SourceName + "  |  Destination: " + (target.bodyName ?? "Unknown body"), true);
                            TabletUi.Label(new Rect(info.x, info.y + row, info.width, row),
                                validTransfer ? "Ideal departure: " + departureDv.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture) +
                                " m/s  |  arrival: " + arrivalDv.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture) + " m/s" :
                                "Ideal departure / arrival delta-v unavailable.", true);
                            TabletUi.Label(new Rect(info.x, info.y + row * 2, info.width, row),
                                validTransfer ? "Flight time: " + TransferDuration(transferSeconds) +
                                "  |  instantaneous source radius to target mean orbital radius" :
                                "Only finite, positive orbital radii can be estimated.", true);
                            TabletUi.Label(new Rect(info.x, info.y + row * 3, info.width, row),
                                "Legend: blue source, green target, cyan transfer, orange target at arrival.", true);
                            TabletUi.Label(new Rect(info.x, info.y + row * 4, info.width, row),
                                "2D projection; source and target markers show current positions.", true);
                            y += infoHeight + gap;
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

        static List<CelestialBody> TransferTargets(Vessel vessel)
        {
            var targets = new List<CelestialBody>();
            if (vessel == null || vessel.mainBody == null || FlightGlobals.Bodies == null) return targets;
            CelestialBody origin = vessel.mainBody;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody body = FlightGlobals.Bodies[i];
                if (body == null || body == origin || body.orbit == null) continue;
                bool local = vessel.orbit != null && body.referenceBody == origin;
                bool sameParent = origin.referenceBody != null && origin.referenceBody != origin &&
                    origin.orbit != null && body.referenceBody == origin.referenceBody;
                if (local || sameParent) targets.Add(body);
            }
            return targets;
        }

        static int FindTargetIndex(List<CelestialBody> targets, string name)
        {
            if (targets == null || targets.Count == 0) return 0;
            for (int i = 0; i < targets.Count; i++)
                if (string.Equals(targets[i].bodyName, name, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        static bool TryTransferRoute(Vessel vessel, CelestialBody target, out TransferRoute route)
        {
            route = null;
            if (vessel == null || vessel.mainBody == null || vessel.orbit == null || target == null || target.orbit == null)
                return false;
            CelestialBody origin = vessel.mainBody;
            if (target.referenceBody == origin)
            {
                route = new TransferRoute { Center = origin, SourceOrbit = vessel.orbit, SourceName = "Active vessel" };
                return true;
            }
            if (origin.referenceBody != null && origin.referenceBody != origin &&
                origin.orbit != null && target.referenceBody == origin.referenceBody)
            {
                route = new TransferRoute { Center = origin.referenceBody, SourceOrbit = origin.orbit, SourceName = origin.bodyName ?? "Origin body" };
                return true;
            }
            return false;
        }

        static void DrawTransferDiagram(Rect plot, TransferRoute route, CelestialBody target, double now,
            double sourceRadius, double destinationRadius, double transferSeconds)
        {
            TabletUi.Fill(plot, TabletUi.Bg);
            Vector3d sourcePosition = route.SourceOrbit.getRelativePositionAtUT(now);
            Vector3d targetPosition = target.orbit.getRelativePositionAtUT(now);
            Vector3d arrivalPosition = target.orbit.getRelativePositionAtUT(now + transferSeconds);
            double extent = Math.Max(sourceRadius, destinationRadius);
            extent = Math.Max(extent, OrbitExtent(route.SourceOrbit, sourceRadius));
            extent = Math.Max(extent, OrbitExtent(target.orbit, destinationRadius));
            if (!Finite(extent) || extent <= 0) return;
            float scale = Mathf.Min(plot.width, plot.height) * .43f / (float)extent;
            Vector2 center = plot.center;

            DrawOrbitPath(route.SourceOrbit, now, center, scale, TabletUi.Series[0]);
            DrawOrbitPath(target.orbit, now, center, scale, TabletUi.Series[2]);

            double startAngle = Math.Atan2(sourcePosition.z, sourcePosition.x);
            Vector2 last = Vector2.zero;
            for (int i = 0; i <= 48; i++)
            {
                double x, y;
                if (!TransferDiagramMath.TryHohmannPoint(sourceRadius, destinationRadius, startAngle,
                    i / 48.0, out x, out y))
                    continue;
                Vector2 point = new Vector2(center.x + (float)x * scale, center.y - (float)y * scale);
                if (i > 0) TabletUi.Line(last, point, 3, TabletUi.Accent);
                last = point;
            }

            DrawMarker(center, TabletUi.Text);
            if (Finite(sourcePosition.x) && Finite(sourcePosition.z))
                DrawMarker(Project(sourcePosition, center, scale), TabletUi.Series[0]);
            if (Finite(targetPosition.x) && Finite(targetPosition.z))
                DrawMarker(Project(targetPosition, center, scale), TabletUi.Series[2]);
            if (Finite(arrivalPosition.x) && Finite(arrivalPosition.z))
                DrawMarker(Project(arrivalPosition, center, scale), TabletUi.Warn);
        }

        static double OrbitExtent(Orbit orbit, double fallback)
        {
            if (orbit == null || !Finite(orbit.semiMajorAxis) || orbit.semiMajorAxis <= 0 ||
                !Finite(orbit.eccentricity) || orbit.eccentricity < 0 || orbit.eccentricity >= 1)
                return fallback;
            return orbit.semiMajorAxis * (1 + orbit.eccentricity);
        }

        static void DrawOrbitPath(Orbit orbit, double now, Vector2 center, float scale, Color color)
        {
            if (orbit == null || !Finite(orbit.period) || orbit.period <= 0) return;
            Vector2 last = Vector2.zero;
            bool haveLast = false;
            for (int i = 0; i <= 64; i++)
            {
                Vector3d position = orbit.getRelativePositionAtUT(now + orbit.period * i / 64.0);
                if (!Finite(position.x) || !Finite(position.z))
                {
                    haveLast = false;
                    continue;
                }
                Vector2 point = Project(position, center, scale);
                if (haveLast) TabletUi.Line(last, point, 1.5f, color);
                last = point;
                haveLast = true;
            }
        }

        static Vector2 Project(Vector3d position, Vector2 center, float scale)
        {
            return new Vector2(center.x + (float)position.x * scale, center.y - (float)position.z * scale);
        }

        static void DrawMarker(Vector2 point, Color color)
        {
            TabletUi.Fill(new Rect(point.x - 4, point.y - 4, 8, 8), color);
        }

        static string TransferDuration(double seconds)
        {
            if (!Finite(seconds) || seconds < 0) return "--";
            if (seconds >= 86400)
                return (seconds / 86400).ToString("0.0", CultureInfo.InvariantCulture) + " days";
            return (seconds / 3600).ToString("0.0", CultureInfo.InvariantCulture) + " hours";
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
