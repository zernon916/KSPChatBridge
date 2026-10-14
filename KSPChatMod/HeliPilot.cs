using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        int heliLearnStage;
        double heliLearnTime, heliStableTime, heliCollectiveSum, heliPhaseTime, heliMaxClimb, heliMaxDescent;
        int heliCollectiveSamples;
        string heliLearnMessage = "No helicopter tests yet.";
        Dictionary<string, object> heliProfile = new Dictionary<string, object>();
        bool HeliLearning { get { return mode == "helicopter" && heliLearnStage > 0 && heliLearnStage < 5; } }

        string HeliProfilePath()
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(vessel.vesselName));
                return System.IO.Path.Combine(AicsCore.PluginDataDirectory,
                    "heli_" + BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + ".json");
            }
        }
        void LoadHeliProfile()
        {
            heliProfile = new Dictionary<string, object>();
            try
            {
                string path = HeliProfilePath();
                if (System.IO.File.Exists(path))
                    heliProfile = MiniJson.Deserialize(System.IO.File.ReadAllText(path)) as Dictionary<string, object>
                        ?? new Dictionary<string, object>();
            }
            catch (Exception ex) { ChatLog.Write("heli", "profile load failed: " + ex.Message); }
        }
        void SaveHeliProfile()
        {
            try
            {
                System.IO.Directory.CreateDirectory(AicsCore.PluginDataDirectory);
                AtomicFile.Write(HeliProfilePath(), MiniJson.Serialize(heliProfile));
                ChatLog.Write("heli", "learn profile saved: " + heliLearnMessage);
            }
            catch (Exception ex) { heliLearnMessage = "Profile save failed: " + ex.Message; ChatLog.Write("heli", heliLearnMessage); }
        }
        void BeginHeliLearn(bool enabled)
        {
            heliLearnStage = enabled ? 1 : 0;
            heliLearnTime = heliStableTime = heliCollectiveSum = heliPhaseTime = heliMaxClimb = heliMaxDescent = 0;
            heliCollectiveSamples = 0;
            LoadHeliProfile();
            if (!enabled) return;
            heliProfile = new Dictionary<string, object>();
            heliProfile["mass"] = vessel.totalMass;
            heliProfile["body"] = vessel.mainBody.bodyName;
            heliProfile["parts"] = vessel.parts.Count;
            heliLearnMessage = "Hover: hold steady for 8 seconds.";
            ChatLog.Write("heli", "learn start: " + vessel.vesselName + ", mass " + vessel.totalMass.ToString("0.000", CultureInfo.InvariantCulture));
        }
        void HeliLearnSample(double dt, double collectiveOutput, string emergency)
        {
            if (!HeliLearning) return;
            heliLearnTime += dt;
            if (emergency != "normal" || Math.Abs(Pitch()) > 30 || Math.Abs(Roll()) > 30 || heliLearnTime > 180)
            {
                heliLearnStage = 0;
                heliLearnMessage = "LEARN stopped: unstable, emergency or timeout.";
                ChatLog.Write("heli", heliLearnMessage);
                return;
            }
            if (heliLearnStage == 2 || heliLearnStage == 3)
            {
                heliPhaseTime += dt;
                bool controlled = !vessel.LandedOrSplashed && Math.Abs(Pitch()) < 8 && Math.Abs(Roll()) < 8 && vessel.horizontalSrfSpeed < 2;
                if (controlled && heliPhaseTime > 5)
                {
                    if (heliLearnStage == 2) heliMaxClimb = Math.Max(heliMaxClimb, vessel.verticalSpeed);
                    else if (vessel.radarAltitude > 25) heliMaxDescent = Math.Max(heliMaxDescent, -vessel.verticalSpeed);
                }
                if (heliPhaseTime < 30 && (heliLearnStage != 3 || vessel.radarAltitude > 25)) return;
                if (heliLearnStage == 2)
                {
                    heliProfile["climb"] = Math.Max(0, heliMaxClimb);
                    heliLearnStage = 3; heliPhaseTime = 0;
                    heliLearnMessage = "Measure controlled descent, capped at 3 m/s.";
                }
                else
                {
                    heliProfile["descent"] = Math.Max(0, heliMaxDescent);
                    heliLearnStage = 5;
                    altitude = vessel.altitude;
                    heliLearnMessage = "Climb and safe descent measured.";
                }
                SaveHeliProfile();
                return;
            }
            if (heliLearnStage != 1) return;
            bool stable = !vessel.LandedOrSplashed && vessel.radarAltitude >= 10 &&
                Math.Abs(vessel.verticalSpeed) < .4 && vessel.horizontalSrfSpeed < .8 &&
                Math.Abs(Pitch()) < 5 && Math.Abs(Roll()) < 5;
            if (!stable) { heliStableTime = heliCollectiveSum = 0; heliCollectiveSamples = 0; return; }
            heliStableTime += dt;
            heliCollectiveSum += collectiveOutput;
            heliCollectiveSamples++;
            if (heliStableTime < 8) return;
            heliProfile["hover"] = heliCollectiveSum / heliCollectiveSamples;
            heliProfile["mass"] = vessel.totalMass;
            heliLearnMessage = "Hover collective measured at current mass.";
            SaveHeliProfile();
            heliLearnStage = 2; heliPhaseTime = 0;
            heliLearnMessage = "Measure climb, increasing demand up to 8 m/s.";
            ChatLog.Write("heli", heliLearnMessage);
        }
        double HeliLearnVerticalSpeed(double requested)
        {
            if (!HeliLearning) return requested;
            if (heliLearnStage == 2) return Math.Min(8, .5 + heliPhaseTime * .3);
            if (heliLearnStage == 3) return vessel.radarAltitude <= 25 ? 0 : -Math.Min(3, .3 + heliPhaseTime * .12);
            return requested;
        }

        TaxiMission.Point heliWaypoint;
        bool heliWaypointArrived;

        void HeliWaypointVelocity(ref double forward, ref double right)
        {
            if (heliWaypoint == null || vessel == null || vessel.mainBody == null || helicopterLanding) return;
            double distance = NavigationMath.Distance(vessel.latitude, vessel.longitude,
                heliWaypoint.Lat, heliWaypoint.Lon, vessel.mainBody.Radius);
            double bearing = NavigationMath.Bearing(vessel.latitude, vessel.longitude,
                heliWaypoint.Lat, heliWaypoint.Lon);
            if (distance < 5 && vessel.horizontalSrfSpeed < 1 && !heliWaypointArrived)
            {
                heliWaypointArrived = true;
                ChatLog.Write("heli", "arrived at " + heliWaypoint.Name + "; position hold");
            }
            if (heliWaypointArrived)
            {
                HelicopterPolicy.HoldVelocity(distance, bearing, FlightGlobals.ship_heading, out forward, out right);
                return;
            }
            double target = Math.Min(speed, Math.Max(0, distance) * speed / 200);
            double angle = FlightPolicy.Wrap(bearing - FlightGlobals.ship_heading) * Math.PI / 180;
            forward = target * Math.Cos(angle);
            right = target * Math.Sin(angle);
            if (distance > 10) heading = bearing;
        }
    }
}
