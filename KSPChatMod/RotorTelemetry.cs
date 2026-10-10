using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using Expansions.Serenity;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RotorTelemetry : MonoBehaviour
    {
        internal sealed class Row
        {
            internal string PartId, Title, Label, Direction;
            internal float Rpm, Limit, Torque, Brake;
            internal bool Motor;
            internal Vector3 Position, Axis;
        }
        internal static List<Row> Rows = new List<Row>();
        float next;
        internal static bool Available;

        void Update()
        {
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + .5f;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) { Rows = new List<Row>(); Available = false; LocalVesselState.Clear(); return; }
            var rows = new List<Row>();
            foreach (Part part in vessel.parts)
                foreach (PartModule module in part.Modules)
                {
                    var rotor = module as ModuleRoboticServoRotor;
                    if (rotor == null) continue;
                    try
                    {
                        var frame = vessel.ReferenceTransform;
                        Vector3 offset = part.transform.position - vessel.CoM;
                        var axisField = typeof(BaseServo).GetField("axis", BindingFlags.Instance | BindingFlags.NonPublic);
                        Vector3 worldAxis = axisField == null ? Vector3.zero : part.transform.TransformDirection((Vector3)axisField.GetValue(rotor)).normalized;
                        // Position / Axis stored as (right, forward, up) in vessel frame — see RotorPlacement.LabelFromVesselFrame.
                        float posRight = Vector3.Dot(offset, frame.right);
                        float posForward = Vector3.Dot(offset, frame.up);
                        float posUp = Vector3.Dot(offset, -frame.forward);
                        float axisRight = Vector3.Dot(worldAxis, frame.right);
                        float axisForward = Vector3.Dot(worldAxis, frame.up);
                        float axisUp = Vector3.Dot(worldAxis, -frame.forward);
                        rows.Add(new Row {
                            PartId = NativeIds.Part(part.flightID),
                            Title = part.partInfo.title, Label = part.partInfo.title,
                            Direction = rotor.rotateCounterClockwise ? "CCW" : "CW",
                            Rpm = RotorMeasurements.Rpm(rotor), Limit = rotor.rpmLimit,
                            Torque = rotor.servoMotorLimit, Brake = rotor.brakePercentage,
                            Motor = rotor.servoMotorIsEngaged,
                            Position = new Vector3(posRight, posForward, posUp),
                            Axis = new Vector3(axisRight, axisForward, axisUp)
                        });
                    }
                    catch (Exception ex) { Debug.LogWarning("[AICS] rotor sample: " + ex.Message); }
                }
            int liftCount = rows.FindAll(r => RotorPlacement.IsLiftRotorAxis(r.Axis.z)).Count;
            foreach (var row in rows)
                row.Label = RotorPlacement.LabelFromVesselFrame(row.Position.x, row.Position.y, row.Position.z,
                    row.Axis.x, row.Axis.y, row.Axis.z, liftCount);
            Rows = rows; Available = true;
            try { LocalVesselState.Sample(vessel); }
            catch (Exception ex) { LocalVesselState.Clear(); Debug.LogWarning("[AICS] local telemetry: " + ex.Message); }
        }

    }
}
