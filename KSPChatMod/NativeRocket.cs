using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        string rocketMode = "off";

        void RocketSteer(FlightCtrlState c, Vector3d dir)
        {
            if (c == null) return;
            if (vessel == null || vessel.transform == null || vessel.ReferenceTransform == null)
            {
                c.pitch = c.yaw = c.roll = 0;
                return;
            }

            double magnitude = dir.magnitude;
            if (double.IsNaN(magnitude) || double.IsInfinity(magnitude) || magnitude <= 0)
            {
                c.pitch = c.yaw = c.roll = 0;
                return;
            }

            Vector3d errorAxis = Vector3d.Cross(vessel.transform.up, dir / magnitude);
            Transform frame = vessel.ReferenceTransform;
            Vector3 error = frame.InverseTransformDirection((Vector3)errorAxis);
            Vector3 angularVelocity = frame.InverseTransformDirection((Vector3)vessel.angularVelocity);
            c.pitch = RocketSteerCommand(error.x, angularVelocity.x);
            c.roll = RocketSteerCommand(error.y, angularVelocity.y);
            c.yaw = RocketSteerCommand(error.z, angularVelocity.z);
        }

        static float RocketSteerCommand(double error, double angularVelocity)
        {
            double command = 2 * error - .8 * angularVelocity;
            if (double.IsNaN(command) || double.IsInfinity(command)) return 0;
            return (float)Math.Max(-.5, Math.Min(.5, command));
        }

        internal string RocketCommand(string name)
        {
            return RocketCommand(name, null);
        }

        internal string RocketCommand(string name, Dictionary<string, object> args)
        {
            if (name == "rocket_stop")
            {
                rocketMode = "off";
                ChatLog.Write("rocket", "stop");
                return "Rocket autopilot off.";
            }

            if (name == "rocket_point")
            {
                object modeValue;
                string mode = args != null && args.TryGetValue("mode", out modeValue) ? modeValue as string : null;
                if (string.IsNullOrWhiteSpace(mode)) return "Usage: rocket_point requires a mode.";
                mode = mode.Trim();
                rocketMode = "point:" + mode;
                ChatLog.Write("rocket", "point " + mode);
                return "Pointing " + mode + ".";
            }

            return null;
        }

        internal bool RocketFly(FlightCtrlState c)
        {
            const string prefix = "point:";
            if (rocketMode == null || !rocketMode.StartsWith(prefix, StringComparison.Ordinal)) return false;
            RocketSteer(c, KSPChatBridge.PointAt.Dir(vessel, rocketMode.Substring(prefix.Length)));
            return true;
        }
    }
}
