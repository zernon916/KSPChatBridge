using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        int heliLearnStage;
        double heliLearnTime, heliStableTime, heliCollectiveSum, heliPhaseTime, heliMaxClimb, heliMaxDescent, heliMaxTilt, heliMaxForward;
        int heliCollectiveSamples;
        string heliProfileCraft;
        Guid heliLearnVessel;
        string heliLearnMessage = "No helicopter tests yet.";
        Dictionary<string, object> heliProfile = new Dictionary<string, object>();
        bool HeliLearning { get { return vessel != null && vessel.id == heliLearnVessel && mode == "helicopter" && heliLearnStage > 0 && heliLearnStage < 5; } }

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
            heliProfileCraft = vessel.vesselName;
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
            heliLearnVessel = vessel.id;
            heliLearnStage = enabled ? 1 : 0;
            heliLearnTime = heliStableTime = heliCollectiveSum = heliPhaseTime = heliMaxClimb = heliMaxDescent = heliMaxTilt = heliMaxForward = 0;
            heliCollectiveSamples = 0;
            LoadHeliProfile();
            if (!enabled)
            {
                if (HeliProfileTrusted())
                {
                    double hover = HeliLearned("hover", double.NaN);
                    if (!double.IsNaN(hover)) collective = new CollectiveController(FlightPolicy.Clamp(hover, 0, 30));
                    speed = Math.Min(speed, HeliLearned("forward", 50));
                    ChatLog.Write("heli", "using learned hover/flight envelope for " + vessel.vesselName);
                }
                return;
            }
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
                FinishHeliLearn("LEARN stopped: unstable, emergency or timeout.");
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
                    heliLearnStage = 4; heliPhaseTime = 0;
                    altitude = vessel.altitude;
                    helicopterPositionHold = false;
                    heliLearnMessage = "Measure forward speed and safe tilt, capped at 20 degrees.";
                }
                SaveHeliProfile();
                return;
            }
            if (heliLearnStage == 4)
            {
                heliPhaseTime += dt;
                bool forwardStable = Math.Abs(vessel.verticalSpeed) < 1 && Math.Abs(Roll()) < 8 && vessel.radarAltitude >= 20;
                if (forwardStable && heliPhaseTime > 3)
                {
                    heliMaxTilt = Math.Max(heliMaxTilt, Math.Min(20, Math.Abs(Pitch())));
                    heliMaxForward = Math.Max(heliMaxForward, Math.Max(0,
                        Vector3d.Dot(vessel.srf_velocity, vessel.ReferenceTransform.up)));
                }
                if (heliPhaseTime < 50 && vessel.radarAltitude >= 20 && vessel.verticalSpeed > -2) return;
                heliProfile["tilt"] = heliMaxTilt;
                heliProfile["forward"] = heliMaxForward;
                FinishHeliLearn("Forward speed and safe tilt measured; hover hold.");
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
        void FinishHeliLearn(string message)
        {
            heliLearnStage = 5;
            heliLearnMessage = message;
            speed = 0;
            altitude = vessel.altitude;
            helicopterPositionHold = true;
            hoverLat = vessel.latitude; hoverLon = vessel.longitude;
            ChatLog.Write("heli", message);
        }
        void HeliLearnForward(ref double forward, ref double right)
        {
            if (!HeliLearning || heliLearnStage != 4) return;
            forward = Math.Min(30, heliPhaseTime * .6);
            right = 0;
        }
        double HeliTiltLimit()
        {
            if (HeliLearning) return heliLearnStage == 4 ? Math.Min(20, 3 + heliPhaseTime * .4) : 12;
            return Math.Min(20, HeliLearned("tilt", 12));
        }
        double HeliLearnVerticalSpeed(double requested)
        {
            if (!HeliLearning)
                return FlightPolicy.Clamp(requested, -Math.Min(3, HeliLearned("descent", 3)), HeliLearned("climb", 3));
            if (heliLearnStage == 2) return Math.Min(8, .5 + heliPhaseTime * .3);
            if (heliLearnStage == 3) return vessel.radarAltitude <= 25 ? 0 : -Math.Min(3, .3 + heliPhaseTime * .12);
            return requested;
        }

        bool HeliProfileTrusted()
        {
            if (vessel == null || vessel.mainBody == null || heliProfileCraft != vessel.vesselName) return false;
            double mass = Num(heliProfile, "mass", double.NaN);
            return !double.IsNaN(mass) && !double.IsInfinity(mass) && mass > 0 &&
                Math.Abs(vessel.totalMass / mass - 1) <= .25 &&
                Num(heliProfile, "parts", -1) == vessel.parts.Count &&
                Str(heliProfile, "body", "") == vessel.mainBody.bodyName;
        }
        double HeliLearned(string key, double fallback)
        {
            if (!HeliProfileTrusted()) return fallback;
            double value = Num(heliProfile, key, double.NaN);
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : value;
        }
        List<string> HeliLearnLines()
        {
            if (heliProfileCraft != vessel.vesselName) LoadHeliProfile();
            var lines = new List<string> { "HELICOPTER LEARN", HeliLearning ? heliLearnMessage : "START LEARN measures hover, climb/descent, then tilt/speed." };
            foreach (string key in new[] { "mass", "hover", "climb", "descent", "tilt", "forward" })
            {
                string unit = key == "mass" ? " t" : key == "hover" || key == "tilt" ? " deg" : " m/s";
                double value = Num(heliProfile, key, double.NaN);
                lines.Add(key + ": " + (double.IsNaN(value) || double.IsInfinity(value) ? "not measured" : value.ToString("0.00", CultureInfo.InvariantCulture) + unit));
            }
            lines.Add(HeliProfileTrusted() ? "Profile matches mass/body/part count." : "No matching profile; default limits apply.");
            lines.Add("Measured envelope: climb <= 8 m/s, descent <= 3 m/s, tilt <= 20 deg.");
            return lines;
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
