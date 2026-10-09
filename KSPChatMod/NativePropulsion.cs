using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Expansions.Serenity;

namespace KSPChatBridge
{
    internal sealed class NativePropulsion
    {
        internal readonly List<ModuleRoboticServoRotor> Rotors = new List<ModuleRoboticServoRotor>();
        readonly List<ModuleControlSurface> blades = new List<ModuleControlSurface>();
        readonly Dictionary<ModuleControlSurface, float> bladeSign = new Dictionary<ModuleControlSurface, float>();
        readonly Dictionary<ModuleControlSurface, ModuleRoboticServoRotor> hubs = new Dictionary<ModuleControlSurface, ModuleRoboticServoRotor>();
        readonly Dictionary<ModuleRoboticServoRotor, string> roles = new Dictionary<ModuleRoboticServoRotor, string>();
        float tailBase;
        internal RotorLayoutInfo Layout { get; private set; }
        internal bool CounterLift
        {
            get { bool cw = false, ccw = false; foreach (var rotor in Rotors) if (rotor != null && roles[rotor] == "lift") { cw |= !rotor.rotateCounterClockwise; ccw |= rotor.rotateCounterClockwise; } return cw && ccw; }
        }
        internal bool Compound { get { return Layout != null ? Layout.Compound : roles.ContainsValue("left") && roles.ContainsValue("right"); } }
        internal bool Multirotor { get { return Layout != null && Layout.Multirotor; } }
        internal double? CollectiveValue
        {
            get
            {
                double total = 0; int count = 0;
                foreach (var surface in blades) if (surface != null) { total += surface.deployAngle * bladeSign[surface]; count++; }
                return count == 0 ? (double?)null : total / count;
            }
        }
        internal double Radius
        {
            get
            {
                double total = 0; int count = 0;
                foreach (var pair in hubs) if (pair.Key != null && pair.Value != null)
                { total += Vector3.Distance(pair.Key.part.transform.position, pair.Value.part.transform.position) + .5; count++; }
                return count > 0 ? Math.Max(.5, total / count) : double.NaN;
            }
        }
        internal NativePropulsion(Vessel vessel)
        {
            foreach (Part p in vessel.parts) foreach (PartModule m in p.Modules)
            {
                var rotor = m as ModuleRoboticServoRotor; if (rotor != null) Rotors.Add(rotor);
                var surface = m as ModuleControlSurface;
                if (surface != null && p.partInfo.title.IndexOf("blade", StringComparison.OrdinalIgnoreCase) >= 0)
                { blades.Add(surface); bladeSign[surface] = surface.deployAngle < 0 ? -1 : 1; }
            }
            var axisField = typeof(BaseServo).GetField("axis", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var r in Rotors)
            {
                Vector3 axis = r.part.transform.TransformDirection((Vector3)axisField.GetValue(r)).normalized;
                double up = Math.Abs(Vector3.Dot(axis, vessel.upAxis));
                double forward = Math.Abs(Vector3.Dot(axis, vessel.ReferenceTransform.up));
                double side = Vector3.Dot(r.part.transform.position - vessel.CoM, vessel.ReferenceTransform.right);
                roles[r] = HelicopterPolicy.RotorRole(up, forward, Vector3.Dot(axis, vessel.ReferenceTransform.right), side);
            }
            foreach (var blade in blades)
            {
                Part parent = blade.part.parent;
                for (int depth = 0; depth < 5 && parent != null; depth++, parent = parent.parent)
                {
                    var hub = Rotors.Find(r => r.part == parent);
                    if (hub != null) { hubs[blade] = hub; break; }
                }
            }
            foreach (var rotor in Rotors)
                if (hubs.Count > 0 && !hubs.ContainsValue(rotor)) roles[rotor] = "drive";
            bool wings = false;
            foreach (Part p in vessel.parts) if (p.FindModuleImplementing<ModuleLiftingSurface>() != null) { wings = true; break; }
            var descriptors = new RotorDescriptor[Rotors.Count];
            for (int i = 0; i < Rotors.Count; i++)
            {
                var r = Rotors[i];
                Vector3 axis = r.part.transform.TransformDirection((Vector3)axisField.GetValue(r)).normalized;
                Vector3 relative = r.part.transform.position - vessel.CoM;
                int blades = 0; foreach (var pair in hubs) if (pair.Value == r) blades++;
                descriptors[i] = new RotorDescriptor
                {
                    Up = Vector3.Dot(axis, vessel.upAxis),
                    Forward = Vector3.Dot(axis, vessel.ReferenceTransform.up),
                    Right = Vector3.Dot(axis, vessel.ReferenceTransform.right),
                    Side = Vector3.Dot(relative, vessel.ReferenceTransform.right),
                    North = Vector3.Dot(relative, vessel.north),
                    East = Vector3.Dot(relative, vessel.east),
                    Dir = r.rotateCounterClockwise ? -1 : 1,
                    Blades = blades,
                    Locked = r.lockPartOnPowerLoss || r.servoIsLocked
                };
            }
            Layout = HelicopterPolicy.Classify(descriptors, wings);
            if (Layout.Multirotor)
                foreach (int i in Layout.Lift) if (i >= 0 && i < Rotors.Count) roles[Rotors[i]] = "lift";
            double tailPitch = 0; int tails = 0;
            foreach (var pair in hubs) if (roles[pair.Value] == "tail") { tailPitch += pair.Key.deployAngle * bladeSign[pair.Key]; tails++; }
            tailBase = tails == 0 ? 0 : (float)(tailPitch / tails);
        }
        internal IEnumerable<string> Ids()
        { foreach (var r in Rotors) if (r != null) yield return r.part.flightID.ToString(); }
        internal static void Field(PartModule module, string name, object value)
        {
            var field = module.Fields[name];
            if (field == null || !field.SetValue(value, module)) throw new InvalidOperationException("Cannot set " + name);
        }
        internal void Set(float torque, float rpm = 460, bool motor = true, float brake = 0)
        {
            foreach (var r in Rotors)
            {
                if (r == null) continue;
                Field(r, "brakePercentage", brake);
                Field(r, "servoMotorLimit", torque);
                // Same axis setter as the installed kRPC API, without RPC.
                var axis = typeof(ModuleRoboticServoRotor).GetField("rpmLimitAxisField", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(r) as BaseAxisField;
                if (axis == null || !axis.SetValue(rpm, axis.module)) throw new InvalidOperationException("Rotor RPM axis unavailable");
                if (motor) r.EngageMotor(); else r.DisengageMotor();
            }
        }
        internal List<RotorSpool.Measurement> Sample()
        {
            var rows = new List<RotorSpool.Measurement>();
            foreach (var r in Rotors) if (r != null) rows.Add(new RotorSpool.Measurement { Id = r.part.flightID.ToString(), Rpm = RotorMeasurements.Rpm(r), Limit = r.rpmLimit, Brake = r.brakePercentage, Torque = r.servoMotorLimit, Motor = r.servoMotorIsEngaged });
            return rows;
        }
        internal void Collective(float degrees, string role = null)
        {
            foreach (var s in blades) if (s != null)
            {
                ModuleRoboticServoRotor hub;
                if (role != null && (!hubs.TryGetValue(s, out hub) || roles[hub] != role)) continue;
                s.deployAngle = bladeSign[s] * Mathf.Clamp(degrees, role == "tail" || role == "left" || role == "right" ? -12 : 0, role == "lift" ? 30 : 45); s.deploy = true;
            }
        }
        internal void Yaw(float demand, float forwardPitch)
        {
            demand = Mathf.Clamp(demand, -1, 1);
            if (Compound) { Collective(forwardPitch + 10 * demand, "left"); Collective(forwardPitch - 10 * demand, "right"); }
            else if (roles.ContainsValue("tail")) Collective(tailBase + 8 * demand, "tail");
            else if ((Multirotor || CounterLift) && CounterLift)
            {
                foreach (var r in Rotors) if (r != null && roles[r] == "lift")
                    Field(r, "servoMotorLimit", 90f + 10f * demand * (r.rotateCounterClockwise ? -1 : 1));
            }
        }
        internal string RotorSpeechName(ModuleRoboticServoRotor rotor)
        {
            int index = Rotors.IndexOf(rotor);
            if (index < 0 || Layout == null) return rotor != null && rotor.part != null ? rotor.part.partInfo.title : "rotor";
            Vector3 relative = rotor.part.transform.position - rotor.part.vessel.CoM;
            string code = HelicopterPolicy.LayoutLabel(Layout, index, Vector3.Dot(relative, rotor.part.vessel.ReferenceTransform.right));
            string full = code == "MR" ? "Main Rotor" : code == "TR" ? "Tail Rotor" : code.StartsWith("L") && code.Length > 1 && char.IsDigit(code[1]) ? "Left " + code.Substring(1)
                : code.StartsWith("R") && code.Length > 1 && char.IsDigit(code[1]) ? "Right " + code.Substring(1)
                : code == "LP" ? "Left Prop" : code == "RP" ? "Right Prop" : code;
            return full + " rotor";
        }
        internal bool HasLift(Vessel vessel)
        {
            return hubs.Count > 0 && roles.ContainsValue("lift");
        }
        internal bool LiftSnapshot(out double rpm, out double limit, out bool motor, out bool locked)
        {
            rpm = double.PositiveInfinity; limit = 0; motor = true; locked = false; int count = 0;
            foreach (var rotor in Rotors) if (rotor != null && roles[rotor] == "lift")
            {
                double measured = RotorMeasurements.Rpm(rotor);
                if (double.IsNaN(measured) || double.IsInfinity(measured)) { rpm = double.NaN; return false; }
                rpm = Math.Min(rpm, measured); limit = Math.Max(limit, rotor.rpmLimit);
                motor &= rotor.servoMotorIsEngaged; locked |= rotor.lockPartOnPowerLoss || rotor.servoIsLocked; count++;
            }
            return count > 0 && limit > 0;
        }
        internal static double Charge(Vessel vessel)
        {
            double amount = 0, capacity = 0;
            foreach (Part p in vessel.parts) foreach (PartResource r in p.Resources)
                if (r.resourceName == "ElectricCharge") { amount += r.amount; capacity += r.maxAmount; }
            return capacity <= 0 ? double.NaN : amount / capacity;
        }
    }
}
