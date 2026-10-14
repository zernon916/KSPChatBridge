using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        string rocketMode = "off";
        double rocketEmptyT = -1;
        bool rocketFairingsDone;
        Vessel rocketFairingsVessel;

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
            RocketAutoStage();
            if (!rocketFairingsDone || rocketFairingsVessel != vessel)
                RocketFairings(LiveRocket.Read(vessel));
            const string prefix = "point:";
            if (rocketMode == null || !rocketMode.StartsWith(prefix, StringComparison.Ordinal)) return false;
            RocketSteer(c, KSPChatBridge.PointAt.Dir(vessel, rocketMode.Substring(prefix.Length)));
            return true;
        }

        void RocketFairings(LiveRocket s)
        {
            if (vessel == null || vessel != FlightGlobals.ActiveVessel || vessel.packed || vessel.parts == null)
                return;
            if (rocketFairingsVessel != vessel)
            {
                rocketFairingsVessel = vessel;
                rocketFairingsDone = false;
            }
            if (rocketFairingsDone || s == null) return;

            string reason = FairingPolicy.CanDeploy(s.QKpa, s.Alt, s.MaxTempRatio);
            if (reason == null) return;

            rocketFairingsDone = true;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    ModuleProceduralFairing fairing = module as ModuleProceduralFairing;
                    if (fairing != null) fairing.DeployFairing();
                }
            }
            ChatLog.Write("rocket", "fairings: " + reason);
        }

        void RocketAutoStage()
        {
            if (vessel == null || vessel != FlightGlobals.ActiveVessel || vessel.packed || vessel.parts == null)
            {
                rocketEmptyT = -1;
                return;
            }

            bool flameout = false;
            bool hasIgnitedEngine = false;
            bool tankEmpty = true;
            PartResourceLibrary resourceLibrary = PartResourceLibrary.Instance;
            foreach (Part part in vessel.parts)
            {
                if (part == null || part.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    ModuleEngines engine = module as ModuleEngines;
                    if (engine == null || !engine.EngineIgnited) continue;
                    hasIgnitedEngine = true;
                    if (engine.flameout) flameout = true;
                    if (engine.propellants == null || engine.propellants.Count == 0 || resourceLibrary == null)
                    {
                        tankEmpty = false;
                        continue;
                    }

                    foreach (Propellant propellant in engine.propellants)
                    {
                        if (propellant == null || string.IsNullOrEmpty(propellant.name))
                        {
                            tankEmpty = false;
                            continue;
                        }
                        PartResourceDefinition definition = resourceLibrary.GetDefinition(propellant.name);
                        if (definition == null)
                        {
                            tankEmpty = false;
                            continue;
                        }
                        double amount, capacity;
                        vessel.GetConnectedResourceTotals(definition.id, out amount, out capacity);
                        if (amount > 0.0001) tankEmpty = false;
                    }
                }
            }
            tankEmpty = hasIgnitedEngine && tankEmpty;

            bool empty = flameout || tankEmpty;
            double now = Time.realtimeSinceStartup;
            if (empty)
            {
                if (rocketEmptyT < 0) rocketEmptyT = now;
            }
            else rocketEmptyT = -1;

            string reason = StagingPolicy.ShouldStage(flameout, tankEmpty,
                rocketEmptyT < 0 ? 0 : now - rocketEmptyT);
            if (reason == null) return;

            rocketEmptyT = -1;
            KSP.UI.Screens.StageManager.ActivateNextStage();
            ChatWindow.Notice("Stage " + vessel.currentStage + " empty, staging.");
            ChatLog.Write("rocket", "stage: " + reason);
        }
    }
}
