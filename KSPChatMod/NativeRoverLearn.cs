using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        RoverTopSpeedTrial roverTopSpeedTrial;
        RoverBrakingTrial roverBrakingTrial;
        RoverTurnTrial roverTurnTrial;
        RoverProfile roverProfile;
        string roverProfileVessel;
        internal static bool ActiveRover
        {
            get { var me = instance; return me != null && me.HasPoweredWheels(); }
        }
        internal static bool RoverLearnRunning
        {
            get
            {
                var me = instance;
                return me != null && (me.roverTopSpeedTrial != null || me.roverBrakingTrial != null || me.roverTurnTrial != null);
            }
        }
        internal static string RoverLearnStatus
        {
            get
            {
                var me = instance;
                if (me == null) return "";
                if (me.roverTopSpeedTrial != null) return "ROVER LEARN: measuring top speed ("
                    + me.roverTopSpeedTrial.BestSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s)";
                if (me.roverBrakingTrial != null) return me.roverBrakingTrial.Braking
                    ? "ROVER LEARN: measuring braking distance (" + me.roverBrakingTrial.Distance.ToString("0.0", CultureInfo.InvariantCulture) + " m)"
                    : "ROVER LEARN: accelerating to 10 m/s for brake test";
                return me.roverTurnTrial == null ? "" : "ROVER LEARN: turn test at " + me.roverTurnTrial.TargetSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s";
            }
        }

        bool HasPoweredWheels()
        {
            if (vessel == null) return false;
            bool wheels = false, motors = false;
            foreach (Part part in vessel.parts) foreach (PartModule module in part.Modules)
            {
                wheels |= module is ModuleWheelBase;
                var motor = module as ModuleWheels.ModuleWheelMotor;
                motors |= motor != null && motor.motorEnabled;
            }
            return wheels && motors;
        }

        internal List<string> RoverLearnLines()
        {
            var profile = CurrentRoverProfile() ?? new RoverProfile();
            var lines = profile.Checklist(RoverLearnStatus);
            lines.Add(RoverLearnRunning ? "Stop current cancels the running test." : "Say \"learn rover\" to run the next incomplete test.");
            return lines;
        }

        string LearnRoverCmd()
        {
            if (vessel == null) return "No active vessel.";
            if (!vessel.Landed) return "ROVER LEARN requires a landed rover.";
            if (roverTopSpeedTrial != null || roverBrakingTrial != null || roverTurnTrial != null) return "Rover LEARN test already running.";
            if (vessel.srfSpeed > .5) return "Stop the rover before starting ROVER LEARN.";
            if (!KscRoverPolicy.FlatSurface(Pitch(), Roll(), 5)) return "ROVER LEARN requires a level surface (pitch and roll within 5 degrees).";
            if (!HasPoweredWheels()) return "ROVER LEARN requires powered wheels.";
            RoverProfile profile = CurrentRoverProfile();
            bool measureTopSpeed = profile == null || double.IsNaN(profile.TopSpeed);
            bool measureBraking = !measureTopSpeed && double.IsNaN(profile.BrakingDistance);
            bool measureTurn = !measureTopSpeed && !measureBraking && double.IsNaN(profile.SafeTurnSpeed);
            if (!measureTopSpeed && !measureBraking && !measureTurn)
                return "Rover LEARN steps 1-3 are complete. Their results are saved in this rover's profile.";
            if (mode != "rover") BeginHold();
            roverNavigating = false; roverDestination = null;
            roverTargetSpeed = measureTopSpeed ? RoverTopSpeedTrial.TargetSpeed : measureBraking ? 10 : Math.Min(2, profile.TopSpeed);
            roverHeading = FlightGlobals.ship_heading; roverHeadingEnabled = !measureTurn; roverIntegral = 0;
            if (measureTopSpeed) roverTopSpeedTrial = new RoverTopSpeedTrial(Planetarium.GetUniversalTime());
            else if (measureBraking) roverBrakingTrial = new RoverBrakingTrial(Planetarium.GetUniversalTime());
            else roverTurnTrial = new RoverTurnTrial(Planetarium.GetUniversalTime(), profile.TopSpeed);
            mode = "rover";
            vessel.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = 0;
            SetGroup(vessel, KSPActionGroup.Brakes, false);
            string test = measureTopSpeed ? "top-speed test" : measureBraking ? "10 m/s braking test" : "safe-turn-speed test";
            ChatLog.Write("rover", test + " started for " + vessel.vesselName + " on a level surface");
            ChatWindow.Notice(measureTopSpeed
                ? "[ROVER LEARN] Starting straight-line top-speed test, capped at 25 m/s. Stop current cancels it."
                : measureBraking
                    ? "[ROVER LEARN] Accelerating to 10 m/s, then braking to measure stopping distance. Stop current cancels it."
                    : "[ROVER LEARN] Starting a progressive turn test. Use a wide, level area; it stops at 15 degrees of roll. Stop current cancels it.");
            return measureTopSpeed
                ? "ROVER LEARN step 1 started: measuring top speed on level ground, capped at 25 m/s."
                : measureBraking ? "ROVER LEARN step 2 started: measuring braking distance from 10 m/s."
                    : "ROVER LEARN step 3 started: progressively turning until roll exceeds 15 degrees.";
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

        bool RoverTurnTick(double speed, double pitch, double roll)
        {
            if (roverTurnTrial == null) return false;
            var trial = roverTurnTrial;
            if (!trial.Sample(Planetarium.GetUniversalTime(), speed, pitch, roll))
            {
                roverTargetSpeed = trial.TargetSpeed;
                return false;
            }
            roverTurnTrial = null;
            if (trial.Aborted)
            {
                ChatLog.Write("rover", "turn-speed test aborted: " + trial.Reason);
                ChatWindow.Notice("[ROVER LEARN] Turn-speed test aborted: " + trial.Reason + ".");
            }
            else
            {
                var profile = LoadRoverProfile(vessel.vesselName) ?? new RoverProfile();
                profile.SafeTurnSpeed = Math.Round(trial.LastSafeSpeed, 1);
                SaveRoverProfile(profile, "safe turn speed", profile.SafeTurnSpeed, "m/s", trial.Reason);
            }
            Stop();
            return true;
        }

        void SaveRoverProfile(RoverProfile profile, string metric, double value, string unit, string reason)
        {
            profile.Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            roverProfile = profile; roverProfileVessel = vessel.vesselName;
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

        RoverProfile CurrentRoverProfile()
        {
            if (vessel == null) return null;
            if (!string.Equals(roverProfileVessel, vessel.vesselName, StringComparison.Ordinal))
            {
                roverProfileVessel = vessel.vesselName;
                roverProfile = LoadRoverProfile(roverProfileVessel);
            }
            return roverProfile;
        }

        void CancelRoverLearn()
        {
            if (roverTopSpeedTrial == null && roverBrakingTrial == null && roverTurnTrial == null) return;
            roverTopSpeedTrial = null;
            roverBrakingTrial = null;
            roverTurnTrial = null;
            ChatLog.Write("rover", "rover LEARN test cancelled");
            ChatWindow.Notice("[ROVER LEARN] Test cancelled.");
        }
    }
}
