using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        RoverTopSpeedTrial roverTopSpeedTrial;
        internal static string RoverLearnStatus
        {
            get
            {
                var me = instance;
                return me == null || me.roverTopSpeedTrial == null ? "" : "ROVER LEARN: measuring top speed ("
                    + me.roverTopSpeedTrial.BestSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s)";
            }
        }

        string LearnRoverCmd()
        {
            if (vessel == null) return "No active vessel.";
            if (!vessel.Landed) return "ROVER LEARN requires a landed rover.";
            if (roverTopSpeedTrial != null) return "Rover top-speed test already running.";
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
            if (mode != "rover") BeginHold();
            roverNavigating = false; roverDestination = null;
            roverTargetSpeed = RoverTopSpeedTrial.TargetSpeed;
            roverHeading = FlightGlobals.ship_heading; roverHeadingEnabled = true; roverIntegral = 0;
            roverTopSpeedTrial = new RoverTopSpeedTrial(Planetarium.GetUniversalTime());
            mode = "rover";
            vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0;
            SetGroup(vessel, KSPActionGroup.Brakes, false);
            ChatLog.Write("rover", "top-speed test started for " + vessel.vesselName + " on a level surface");
            ChatWindow.Notice("[ROVER LEARN] Starting straight-line top-speed test, capped at 25 m/s. Stop current cancels it.");
            return "ROVER LEARN started: measuring top speed on level ground, capped at 25 m/s.";
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
                profile.Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                Bucket("rover_profiles")[vessel.vesselName] = profile.ToDict();
                try
                {
                    Save();
                    ChatLog.Write("rover", "top speed " + profile.TopSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s saved for " + vessel.vesselName + " (" + trial.Reason + ")");
                    ChatWindow.Notice("[ROVER LEARN] Top speed " + profile.TopSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s saved to the rover profile.");
                }
                catch (Exception ex)
                {
                    ChatLog.Write("rover", "top-speed profile save failed: " + ex.Message);
                    ChatWindow.Notice("[ROVER LEARN] Measured " + profile.TopSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s, but saving the rover profile failed: " + ex.Message);
                }
            }
            Stop();
            return true;
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
            if (roverTopSpeedTrial == null) return;
            roverTopSpeedTrial = null;
            ChatLog.Write("rover", "top-speed test cancelled");
            ChatWindow.Notice("[ROVER LEARN] Top-speed test cancelled.");
        }
    }
}
