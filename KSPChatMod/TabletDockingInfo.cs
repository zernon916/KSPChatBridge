using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletDockingInfoScroll;

        void TabletDockingInfo(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            try
            {
                tabletDockingInfoScroll = GUILayout.BeginScrollView(tabletDockingInfoScroll);
                try
                {
                    float width = area.width - 26;
                    float row = Mathf.Max(44, tabletFont * 2.4f);
                    float gap = 8;
                    float cardHeight = row * 6 + CardHeaderHeight;
                    Rect card = TabletUi.CardPanel(new Rect(0, 0, width, cardHeight), "DOCKING INFO");
                    float y = card.y;

                    Vessel active = FlightGlobals.ActiveVessel;
                    ITargetable selectedTarget = FlightGlobals.fetch == null ? null : FlightGlobals.fetch.VesselTarget;
                    ModuleDockingNode targetPort = selectedTarget as ModuleDockingNode;

                    if (active == null)
                    {
                        TabletUi.Label(new Rect(card.x, y, card.width, row), "No active vessel.", true);
                    }
                    else if (targetPort == null)
                    {
                        TabletUi.Label(new Rect(card.x, y, card.width, row), "Target a docking port to see alignment data.", true);
                    }
                    else if (targetPort.part == null || targetPort.nodeTransform == null)
                    {
                        TabletUi.Label(new Rect(card.x, y, card.width, row), "Target docking port data unavailable.", true);
                    }
                    else
                    {
                        Vector3 targetPosition = targetPort.nodeTransform.position;
                        Vector3d targetWorldPosition = new Vector3d(targetPosition.x, targetPosition.y, targetPosition.z);
                        TabletUi.Label(new Rect(card.x, y, card.width, row), "Target port: " + PortName(targetPort));
                        y += row;

                        Vessel targetVessel = targetPort.part.vessel;
                        if (targetVessel == null)
                        {
                            TabletUi.Label(new Rect(card.x, y, card.width, row), "Target vessel data unavailable.", true);
                        }
                        else if (targetVessel == active)
                        {
                            TabletUi.Label(new Rect(card.x, y, card.width, row), "Target port belongs to the active vessel.", true);
                        }
                        else
                        {
                            ModuleDockingNode activePort = ClosestDockingPort(active, targetWorldPosition);
                            if (activePort == null)
                            {
                                TabletUi.Label(new Rect(card.x, y, card.width, row), "No docking port found on the active vessel.", true);
                            }
                            else if (activePort.nodeTransform == null)
                            {
                                TabletUi.Label(new Rect(card.x, y, card.width, row), "Active docking port data unavailable.", true);
                            }
                            else
                            {
                                Vector3 activePosition = activePort.nodeTransform.position;
                                Vector3d activeWorldPosition = new Vector3d(activePosition.x, activePosition.y, activePosition.z);
                                Vector3 relativePosition = activePosition - targetPosition;
                                Vector3 activeAxis = activePort.nodeTransform.up;
                                Vector3 targetAxis = targetPort.nodeTransform.up;
                                Vector3d relativeVelocity =
                                    PortVelocity(active, activeWorldPosition) - PortVelocity(targetVessel, targetWorldPosition);
                                DockingEstimate estimate = DockingMath.Estimate(
                                    relativePosition.x, relativePosition.y, relativePosition.z,
                                    relativeVelocity.x, relativeVelocity.y, relativeVelocity.z,
                                    activeAxis.x, activeAxis.y, activeAxis.z,
                                    targetAxis.x, targetAxis.y, targetAxis.z);

                                TabletUi.Label(new Rect(card.x, y, card.width, row), "Active port: " + PortName(activePort), true);
                                y += row;
                                if (estimate == null)
                                {
                                    TabletUi.Label(new Rect(card.x, y, card.width, row * 2),
                                        "Docking geometry is unavailable.", true);
                                }
                                else
                                {
                                    double distance = new Vector3d(relativePosition.x, relativePosition.y, relativePosition.z).magnitude;
                                    TabletUi.Label(new Rect(card.x, y, card.width, row),
                                        "Port distance: " + DockingInfoValue(distance, "0.0", " m"));
                                    y += row;
                                    TabletUi.Label(new Rect(card.x, y, card.width, row),
                                        "Alignment error: " + DockingInfoValue(estimate.AlignmentErrorDeg, "0.0", " deg"));
                                    y += row;
                                    TabletUi.Label(new Rect(card.x, y, card.width, row),
                                        "Lateral offset: " + DockingInfoValue(estimate.LateralOffset, "0.00", " m"));
                                    y += row;
                                    TabletUi.Label(new Rect(card.x, y, card.width, row),
                                        "Closing speed: " + DockingInfoValue(estimate.ClosingSpeed, "+0.00;-0.00;0.00", " m/s"));
                                }
                            }
                        }
                    }

                    GUILayout.Space(cardHeight + gap);
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

        static ModuleDockingNode ClosestDockingPort(Vessel vessel, Vector3d targetPosition)
        {
            if (vessel == null || vessel.parts == null) return null;

            ModuleDockingNode closest = null;
            double closestDistance = double.PositiveInfinity;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    ModuleDockingNode port = module as ModuleDockingNode;
                    if (port == null || port.nodeTransform == null) continue;

                    Vector3 position = port.nodeTransform.position;
                    Vector3d delta = new Vector3d(position.x, position.y, position.z) - targetPosition;
                    double distance = delta.magnitude;
                    if (distance < closestDistance)
                    {
                        closest = port;
                        closestDistance = distance;
                    }
                }
            }

            return closest;
        }

        static Vector3d PortVelocity(Vessel vessel, Vector3d portPosition)
        {
            return vessel.GetSrfVelocity() + Vector3d.Cross(vessel.angularVelocity, portPosition - vessel.GetWorldPos3D());
        }

        static string PortName(ModuleDockingNode port)
        {
            return port == null ? "Unknown" : port.GetName();
        }

        static string DockingInfoValue(double value, string format, string unit)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? "--" : value.ToString(format, CultureInfo.InvariantCulture) + unit;
        }
    }
}
