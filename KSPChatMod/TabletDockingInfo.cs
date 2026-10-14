using System.Collections.Generic;
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
                    Vessel targetVessel = targetPort != null && targetPort.part != null
                        ? targetPort.part.vessel : selectedTarget == null ? null : selectedTarget.GetVessel();
                    List<ModuleDockingNode> targetPorts = targetVessel == null || targetVessel == active
                        ? new List<ModuleDockingNode>() : DockingPorts(targetVessel);

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

                        Vessel targetPortVessel = targetPort.part.vessel;
                        if (targetPortVessel == null)
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
                                    PortVelocity(active, activeWorldPosition) - PortVelocity(targetPortVessel, targetWorldPosition);
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

                    float controlsHeight = row * (Mathf.Max(1, targetPorts.Count) + 3) + CardHeaderHeight;
                    Rect controls = TabletUi.CardPanel(new Rect(0, cardHeight + gap, width, controlsHeight), "DOCKING CONTROLS");
                    float controlsY = controls.y;
                    if (!TabletLink)
                    {
                        TabletUi.Label(new Rect(controls.x, controlsY, controls.width, row),
                            "INFO ONLY - enable LIVE LINK to change the target or engage docking.", true);
                        controlsY += row;
                    }

                    if (targetPorts.Count == 0)
                    {
                        TabletUi.Label(new Rect(controls.x, controlsY, controls.width, row),
                            active == null ? "No active vessel." : targetVessel == active
                                ? "Select a docking port on another vessel." : "No target docking ports available.", true);
                        controlsY += row;
                    }
                    else
                    {
                        TabletUi.Label(new Rect(controls.x, controlsY, controls.width, row),
                            "Target vessel: " + (targetVessel.vesselName ?? "(unnamed vessel)"), true);
                        controlsY += row;
                        foreach (ModuleDockingNode port in targetPorts)
                        {
                            bool isSelected = targetPort == port;
                            if (TabletUi.Button(new Rect(controls.x, controlsY, controls.width, row),
                                (isSelected ? "TARGETED: " : "SET TARGET: ") + PortName(port), isSelected,
                                TabletLink && active != null && FlightGlobals.fetch != null) && FlightGlobals.fetch != null)
                                FlightGlobals.fetch.SetVesselTarget(port);
                            controlsY += row;
                        }
                    }

                    bool canEngage = targetPort != null && targetPort.part != null &&
                        targetPort.part.vessel != null && active != null && targetPort.part.vessel != active &&
                        FlightGlobals.fetch != null;
                    if (TabletUi.Button(new Rect(controls.x, controlsY, controls.width, row), "ENGAGE DOCKING",
                        false, TabletLink && canEngage))
                        ChatWindow.ToolFromMenu("dock_with", "{\"name\":\"target\"}");

                    GUILayout.Space(controlsHeight + gap);
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
            ModuleDockingNode closest = null;
            double closestDistance = double.PositiveInfinity;
            foreach (ModuleDockingNode port in DockingPorts(vessel))
            {
                Vector3 position = port.nodeTransform.position;
                Vector3d delta = new Vector3d(position.x, position.y, position.z) - targetPosition;
                double distance = delta.magnitude;
                if (distance < closestDistance)
                {
                    closest = port;
                    closestDistance = distance;
                }
            }

            return closest;
        }

        static List<ModuleDockingNode> DockingPorts(Vessel vessel)
        {
            List<ModuleDockingNode> ports = new List<ModuleDockingNode>();
            if (vessel == null || vessel.parts == null) return ports;

            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    ModuleDockingNode port = module as ModuleDockingNode;
                    if (port != null && port.nodeTransform != null) ports.Add(port);
                }
            }

            return ports;
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
