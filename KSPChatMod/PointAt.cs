using System;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        string pointMode;
        string pointStatus;

        internal static string PointAt(string button)
        {
            if (instance == null || FlightGlobals.ActiveVessel == null) return "No active flight vessel.";
            // Match the existing set_sas_mode command's ownership rule.
            if (Busy) return "Stop local control before changing SAS mode.";
            instance.pointMode = button;
            instance.PointAtStep(FlightGlobals.ActiveVessel, FlightGlobals.ActiveVessel.ctrlState);
            return instance.pointStatus;
        }

        // One application is sufficient: stock SAS maintains the direction on
        // subsequent physics ticks. No additional Fly subscription is needed.
        void PointAtStep(Vessel v, FlightCtrlState c)
        {
            pointStatus = "SAS unavailable on this vessel.";
            if (v == null || c == null || v.Autopilot == null || v.ActionGroups == null) return;
            bool previousSas = v.ActionGroups[KSPActionGroup.SAS];
            try
            {
                if (pointMode == "OFF")
                {
                    SetGroup(v, KSPActionGroup.SAS, false);
                    pointMode = null; pointStatus = "Native point-at OFF.";
                    ChatLog.Write("rocket", pointStatus);
                    return;
                }
                VesselAutopilot.AutopilotMode selected;
                switch (pointMode)
                {
                    case "PROGRADE": selected = VesselAutopilot.AutopilotMode.Prograde; break;
                    case "RETRO": selected = VesselAutopilot.AutopilotMode.Retrograde; break;
                    case "NORMAL": selected = VesselAutopilot.AutopilotMode.Normal; break;
                    case "ANTI-NORM": selected = VesselAutopilot.AutopilotMode.Antinormal; break;
                    case "RAD-IN": selected = VesselAutopilot.AutopilotMode.RadialIn; break;
                    case "RAD-OUT": selected = VesselAutopilot.AutopilotMode.RadialOut; break;
                    case "TARGET": selected = VesselAutopilot.AutopilotMode.Target; break;
                    case "MANEUVER": selected = VesselAutopilot.AutopilotMode.Maneuver; break;
                    default: pointStatus = "Unknown native point-at mode."; return;
                }
                if (pointMode == "TARGET" && (FlightGlobals.fetch == null || FlightGlobals.fetch.VesselTarget == null))
                { pointStatus = "Select a target before using TARGET."; return; }
                if (pointMode == "MANEUVER" && (v.patchedConicSolver == null || v.patchedConicSolver.maneuverNodes == null || v.patchedConicSolver.maneuverNodes.Count == 0))
                { pointStatus = "Create a maneuver node before using MANEUVER."; return; }
                SetGroup(v, KSPActionGroup.SAS, true);
                if (!v.Autopilot.CanSetMode(selected) || !v.Autopilot.SetMode(selected))
                {
                    SetGroup(v, KSPActionGroup.SAS, previousSas);
                    pointStatus = "SAS " + pointMode + " unavailable (pilot/probe capability or missing target/node).";
                    return;
                }
                pointStatus = "Native point-at " + pointMode + ".";
                ChatLog.Write("rocket", pointStatus);
            }
            catch (Exception ex)
            {
                try { SetGroup(v, KSPActionGroup.SAS, previousSas); } catch (Exception) { }
                pointStatus = "Native point-at unavailable: " + ex.Message;
            }
        }
    }

    internal static class PointAt
    {
        internal static Vector3d Dir(Vessel v, string mode)
        {
            if (v == null || string.IsNullOrWhiteSpace(mode)) return Vector3d.zero;

            Vector3d position = Vector3d.zero;
            Vector3d velocity = v.obt_velocity;
            Vector3d auxiliary = Vector3d.zero;
            if (v.mainBody != null)
                position = v.GetWorldPos3D() - v.mainBody.position;

            if (string.Equals(mode, "target", StringComparison.OrdinalIgnoreCase))
            {
                ITargetable target = FlightGlobals.fetch == null ? null : FlightGlobals.fetch.VesselTarget;
                Transform targetTransform = target == null ? null : target.GetTransform();
                if (targetTransform == null) return Vector3d.zero;
                auxiliary = (Vector3d)targetTransform.position - v.GetWorldPos3D();
            }
            else if (string.Equals(mode, "node", StringComparison.OrdinalIgnoreCase))
            {
                Orbit orbit = v.orbit;
                if (orbit == null || v.patchedConicSolver == null ||
                    v.patchedConicSolver.maneuverNodes == null || v.patchedConicSolver.maneuverNodes.Count == 0)
                    return Vector3d.zero;
                auxiliary = v.patchedConicSolver.maneuverNodes[0].GetBurnVector(orbit);
            }

            double x, y, z;
            if (!PointAtMath.TryDirection(position.x, position.y, position.z, velocity.x, velocity.y, velocity.z,
                auxiliary.x, auxiliary.y, auxiliary.z, mode, out x, out y, out z))
                return Vector3d.zero;
            return new Vector3d(x, y, z);
        }
    }
}
