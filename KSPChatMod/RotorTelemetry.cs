using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using Expansions.Serenity;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class RotorTelemetry : MonoBehaviour
    {
        internal sealed class Row
        {
            internal string PartId, RpcId, Title, Label, Direction;
            internal float Rpm, Limit, Torque, Brake;
            internal bool Motor;
            internal Vector3 Position, Axis;
        }
        internal static List<Row> Rows = new List<Row>();
        readonly string session = Guid.NewGuid().ToString("N");
        long sequence;
        float next;
        int sending;
        internal static bool Available;

        // Optional compatibility mapping only. Never register objects or issue RPC.
        // Inspected against installed kRPC 0.6: wrappers compare vessel GUID/part flightID.
        static string RpcId(object value, string typeName)
        {
            try
            {
                Type storeType = Type.GetType("KRPC.Service.ObjectStore, KRPC.Core", false);
                Type wrapper = Type.GetType(typeName + ", KRPC.SpaceCenter", false);
                if (storeType == null || wrapper == null) return "";
                object store = storeType.GetProperty("Instance").GetValue(null, null);
                return Convert.ToString(storeType.GetMethod("GetObjectId").Invoke(store,
                    new[] { Activator.CreateInstance(wrapper, new[] { value }) }), CultureInfo.InvariantCulture);
            }
            catch (Exception) { return ""; }
        }

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
                        rows.Add(new Row {
                            PartId = part.flightID.ToString(CultureInfo.InvariantCulture),
                            RpcId = RpcId(part, "KRPC.SpaceCenter.Services.Parts.Part"),
                            Title = part.partInfo.title, Label = part.partInfo.title,
                            Direction = rotor.rotateCounterClockwise ? "CCW" : "CW",
                            Rpm = RotorMeasurements.Rpm(rotor), Limit = rotor.rpmLimit,
                            Torque = rotor.servoMotorLimit, Brake = rotor.brakePercentage,
                            Motor = rotor.servoMotorIsEngaged,
                            Position = new Vector3(Vector3.Dot(offset, frame.right), Vector3.Dot(offset, frame.up), Vector3.Dot(offset, -frame.forward)),
                            Axis = new Vector3(Vector3.Dot(worldAxis, frame.right), Vector3.Dot(worldAxis, frame.up), Vector3.Dot(worldAxis, -frame.forward))
                        });
                    }
                    catch (Exception ex) { Debug.LogWarning("[AICS] rotor sample: " + ex.Message); }
                }
            int liftCount = rows.FindAll(r => Math.Abs(r.Axis.z) >= .7).Count;
            foreach (var row in rows) row.Label = RotorPlacement.Label(row.Position.x, row.Position.y, row.Axis.x, row.Axis.y, row.Axis.z, liftCount);
            Rows = rows; Available = true;
            try { LocalVesselState.Sample(vessel); }
            catch (Exception ex) { LocalVesselState.Clear(); Debug.LogWarning("[AICS] local telemetry: " + ex.Message); }
            string payload = Serialize(rows, vessel.id.ToString(), RpcId(vessel, "KRPC.SpaceCenter.Services.Vessel"));
            if (Interlocked.CompareExchange(ref sending, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ => {
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8765/telemetry");
                    req.Proxy = null; req.Method = "POST"; req.ContentType = "application/json";
                    req.Timeout = 1200; req.ReadWriteTimeout = 1200; req.ContentLength = bytes.Length;
                    using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                    using (req.GetResponse()) { }
                }
                catch (Exception) { /* Local rows remain usable while bridge is down. */ }
                finally { Interlocked.Exchange(ref sending, 0); }
            });
        }

        static string Number(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        }
        string Serialize(List<Row> rows, string vesselId, string rpcId)
        {
            var s = new StringBuilder("{\"version\":1,\"session\":").Append(ChatWindow.JsonStr(session))
                .Append(",\"sequence\":").Append(++sequence).Append(",\"vessel_id\":").Append(ChatWindow.JsonStr(vesselId))
                .Append(",\"rpc_vessel_id\":").Append(ChatWindow.JsonStr(rpcId)).Append(",\"rotors\":[");
            foreach (Row row in rows)
            {
                if (s[s.Length - 1] != '[') s.Append(',');
                s.Append("{\"part_id\":").Append(ChatWindow.JsonStr(row.PartId))
                    .Append(",\"rpc_part_id\":").Append(ChatWindow.JsonStr(row.RpcId))
                    .Append(",\"label\":").Append(ChatWindow.JsonStr(row.Label))
                    .Append(",\"rpm\":").Append(Number(row.Rpm)).Append(",\"rpm_limit\":").Append(Number(row.Limit))
                    .Append(",\"torque\":").Append(Number(row.Torque)).Append(",\"brake\":").Append(Number(row.Brake))
                    .Append(",\"motor_on\":").Append(row.Motor ? "true" : "false").Append('}');
            }
            return s.Append("],\"state\":").Append(LocalVesselState.Json).Append('}').ToString();
        }
        void OnDestroy() { Available = false; Rows = new List<Row>(); LocalVesselState.Clear(); }
    }
}
