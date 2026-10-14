using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        RoverTopSpeedTrial roverTopSpeedTrial;
        RoverBrakingTrial roverBrakingTrial;
        internal static string RoverLearnStatus
        {
            get
            {
                var me = instance;
                if (me == null) return "";
                if (me.roverTopSpeedTrial != null) return "ROVER LEARN: measuring top speed ("
                    + me.roverTopSpeedTrial.BestSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s)";
                return me.roverBrakingTrial == null ? "" : me.roverBrakingTrial.Braking
                    ? "ROVER LEARN: measuring braking distance (" + me.roverBrakingTrial.Distance.ToString("0.0", CultureInfo.InvariantCulture) + " m)"
                    : "ROVER LEARN: accelerating to 10 m/s for brake test";
            }
        }

        string LearnRoverCmd()
        {
            if (vessel == null) return "No active vessel.";
            if (!vessel.Landed) return "ROVER LEARN requires a landed rover.";
            if (roverTopSpeedTrial != null || roverBrakingTrial != null) return "Rover LEARN test already running.";
            if (vessel.srfSpeed > .5) return "Stop the rover before starting ROVER LEARN.";
            if (!KscRoverPolicy.FlatSurface(Pitch(), Roll(), 5)) return "ROVER LEARN requires a level surface (pitch and roll within 5 degrees).";
            bool wheels = false, motors = false;
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            {
                wheels |= module is ModuleWheelBase;
                var motor = module as ModuleWheels.ModuleWheelMotor;
                motors |= motor != null && motor.motorEnabled;
            }
            if (!wheels || !motors) return "ROVER LEARN requires powered wheels.";
            RoverProfile profile = LoadRoverProfile(vessel.vesselName);
            bool measureTopSpeed = profile == null || double.IsNaN(profile.TopSpeed);
            if (mode != "rover") BeginHold();
            roverNavigating = false; roverDestination = null;
            roverTargetSpeed = measureTopSpeed ? RoverTopSpeedTrial.TargetSpeed : 10;
            roverHeading = FlightGlobals.ship_heading; roverHeadingEnabled = true; roverIntegral = 0;
            if (measureTopSpeed) roverTopSpeedTrial = new RoverTopSpeedTrial(Planetarium.GetUniversalTime());
            else roverBrakingTrial = new RoverBrakingTrial(Planetarium.GetUniversalTime());
            mode = "rover";
            vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0;
            SetGroup(vessel, KSPActionGroup.Brakes, false);
            string test = measureTopSpeed ? "top-speed test" : "10 m/s braking test";
            ChatLog.Write("rover", test + " started for " + vessel.vesselName + " on a level surface");
            ChatWindow.Notice(measureTopSpeed
                ? "[ROVER LEARN] Starting straight-line top-speed test, capped at 25 m/s. Stop current cancels it."
                : "[ROVER LEARN] Accelerating to 10 m/s, then braking to measure stopping distance. Stop current cancels it.");
            return measureTopSpeed
                ? "ROVER LEARN step 1 started: measuring top speed on level ground, capped at 25 m/s."
                : "ROVER LEARN step 2 started: measuring braking distance from 10 m/s.";
        }

        bool RoverTopSpeedTick(double speed, double pitch, double roll)
        {
            if (roverTopSpeedTrial == null) return false;
            var trial = roverTopSpeedTrial;
            if (!trial.Sample(Planetarium.GetUniversalTime(), speed, pitch, roll)) return false;
            roverTopSpeedTrial = null;
            if (trial.Aborted)
            {
                ChatLog.Write("rover", "top-speed test aborted: " + trial.Reason);
                ChatWindow.Notice("[ROVER LEARN] Test aborted: " + trial.Reason + ".");
            }
            else
            {
                var profile = LoadRoverProfile(vessel.vesselName) ?? new RoverProfile();
                profile.TopSpeed = Math.Round(trial.BestSpeed, 1);
                SaveRoverProfile(profile, "top speed", profile.TopSpeed, "m/s", trial.Reason);
            }
            Stop();
            return true;
        }

        bool RoverBrakingTick(double speed, double pitch, double roll)
        {
            if (roverBrakingTrial == null) return false;
            var trial = roverBrakingTrial;
            if (!trial.Sample(Planetarium.GetUniversalTime(), speed, pitch, roll)) return false;
            roverBrakingTrial = null;
            if (trial.Aborted)
            {
                ChatLog.Write("rover", "braking test aborted: " + trial.Reason);
                ChatWindow.Notice("[ROVER LEARN] Braking test aborted: " + trial.Reason + ".");
            }
            else
            {
                var profile = LoadRoverProfile(vessel.vesselName) ?? new RoverProfile();
                profile.BrakingDistance = Math.Round(trial.Distance, 1);
                SaveRoverProfile(profile, "braking distance", profile.BrakingDistance, "m", trial.Reason);
            }
            Stop();
            return true;
        }

        void SaveRoverProfile(RoverProfile profile, string metric, double value, string unit, string reason)
        {
            profile.Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Bucket("rover_profiles")[vessel.vesselName] = profile.ToDict();
            try
            {
                Save();
                ChatLog.Write("rover", metric + " " + value.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit + " saved for " + vessel.vesselName + " (" + reason + ")");
                ChatWindow.Notice("[ROVER LEARN] " + metric + " " + value.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit + " saved to the rover profile.");
            }
            catch (Exception ex)
            {
                ChatLog.Write("rover", metric + " profile save failed: " + ex.Message);
                ChatWindow.Notice("[ROVER LEARN] Measured " + value.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit + ", but saving the rover profile failed: " + ex.Message);
            }
        }

        RoverProfile LoadRoverProfile(string name)
        {
            object value;
            var profiles = settingsData.TryGetValue("rover_profiles", out value) ? value as Dictionary<string, object> : null;
            object data;
            return profiles != null && profiles.TryGetValue(name, out data)
                ? RoverProfile.FromDict(data as Dictionary<string, object>) : null;
        }

        void CancelRoverLearn()
        {
            if (roverTopSpeedTrial == null && roverBrakingTrial == null) return;
            roverTopSpeedTrial = null;
            roverBrakingTrial = null;
            ChatLog.Write("rover", "rover LEARN test cancelled");
            ChatWindow.Notice("[ROVER LEARN] Test cancelled.");
        }
    }
}
