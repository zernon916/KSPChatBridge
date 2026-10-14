using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal sealed class RoverProfile
    {
        internal double TopSpeed = double.NaN;
        internal double BrakingDistance = double.NaN;
        internal double SafeTurnSpeed = double.NaN;
        internal string Date = "";

        internal List<string> Checklist(string status)
        {
            int active = string.IsNullOrEmpty(status) ? 0
                : status.IndexOf("top speed", StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                : status.IndexOf("braking", StringComparison.OrdinalIgnoreCase) >= 0
                    || status.IndexOf("accelerating to 10", StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                : status.IndexOf("turn", StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : 0;
            var lines = new List<string>();
            if (active != 0) lines.Add("Running: " + status);
            AddLine(lines, 1, "Top speed", TopSpeed, "m/s", active);
            AddLine(lines, 2, "Braking distance from 10 m/s", BrakingDistance, "m", active);
            AddLine(lines, 3, "Safe turn speed", SafeTurnSpeed, "m/s", active);
            return lines;
        }

        static void AddLine(List<string> lines, int step, string name, double value, string unit, int active)
        {
            string state = active == step ? "[*]" : double.IsNaN(value) ? "[ ]" : "[x]";
            lines.Add(state + " " + step + " " + name + ": " + (double.IsNaN(value) ? "not measured" : value.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit));
        }

        internal Dictionary<string, object> ToDict()
        {
            var result = new Dictionary<string, object>();
            Add(result, "top_speed", TopSpeed);
            Add(result, "braking_distance", BrakingDistance);
            Add(result, "safe_turn_speed", SafeTurnSpeed);
            if (!string.IsNullOrEmpty(Date)) result["date"] = Date;
            return result;
        }

        internal static RoverProfile FromDict(Dictionary<string, object> data)
        {
            if (data == null) return null;
            double topSpeed = Number(data, "top_speed"), brakingDistance = Number(data, "braking_distance"), safeTurnSpeed = Number(data, "safe_turn_speed");
            if (topSpeed <= 0 || topSpeed > RoverTopSpeedTrial.TargetSpeed) topSpeed = double.NaN;
            if (brakingDistance < 0 || brakingDistance > 10000) brakingDistance = double.NaN;
            if (safeTurnSpeed <= 0 || safeTurnSpeed > RoverTopSpeedTrial.TargetSpeed) safeTurnSpeed = double.NaN;
            return new RoverProfile
            {
                TopSpeed = topSpeed,
                BrakingDistance = brakingDistance,
                SafeTurnSpeed = safeTurnSpeed,
                Date = data.ContainsKey("date") ? Convert.ToString(data["date"], CultureInfo.InvariantCulture) : ""
            };
        }

        static void Add(Dictionary<string, object> data, string key, double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value)) data[key] = value;
        }

        static double Number(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return double.NaN;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (FormatException) { return double.NaN; }
            catch (InvalidCastException) { return double.NaN; }
        }
    }

    internal sealed class RoverTopSpeedTrial
    {
        internal const double TargetSpeed = 25;
        const double CompleteSpeed = 24.5, IncreaseThreshold = .2, PlateauSeconds = 5, MinimumSeconds = 10, TimeoutSeconds = 90;
        readonly double startedAt;
        double lastIncreaseAt;
        double bestSpeed;
        internal bool Done { get; private set; }
        internal bool Aborted { get; private set; }
        internal string Reason { get; private set; }
        internal double BestSpeed { get { return bestSpeed; } }

        internal RoverTopSpeedTrial(double now)
        {
            startedAt = lastIncreaseAt = now;
        }

        internal bool Sample(double now, double speed, double pitch, double roll)
        {
            if (Done) return true;
            if (double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(speed) || double.IsInfinity(speed) || speed < 0)
                return Abort("invalid speed or clock sample");
            if (!KscRoverPolicy.FlatSurface(pitch, roll, 8))
                return Abort("surface stopped being level");
            double elapsed = now - startedAt;
            if (elapsed < 0) return Abort("game clock moved backwards");
            if (speed > bestSpeed)
            {
                if (speed > bestSpeed + IncreaseThreshold) lastIncreaseAt = now;
                bestSpeed = speed;
            }
            if (bestSpeed >= CompleteSpeed) return Complete("25 m/s test limit reached");
            if (elapsed >= MinimumSeconds && now - lastIncreaseAt >= PlateauSeconds)
                return bestSpeed >= 1 ? Complete("speed plateau") : Abort("rover did not move");
            if (elapsed >= TimeoutSeconds)
                return bestSpeed >= 1 ? Complete("90-second limit") : Abort("rover did not move");
            return false;
        }

        bool Complete(string reason) { Done = true; Reason = reason; return true; }
        bool Abort(string reason) { Done = Aborted = true; Reason = reason; return true; }
    }

    internal sealed class RoverBrakingTrial
    {
        const double TriggerSpeed = 9.95, StopSpeed = .25, StopSettleSeconds = .5, TestTimeoutSeconds = 60, BrakeTimeoutSeconds = 20;
        readonly double startedAt;
        double lastSampleAt, lastSpeed, brakingStartedAt = double.NaN, stoppedAt = double.NaN;
        internal bool Braking { get; private set; }
        internal bool Done { get; private set; }
        internal bool Aborted { get; private set; }
        internal string Reason { get; private set; }
        internal double Distance { get; private set; }

        internal RoverBrakingTrial(double now)
        {
            startedAt = lastSampleAt = now;
        }

        internal bool Sample(double now, double speed, double pitch, double roll)
        {
            if (Done) return true;
            if (double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(speed) || double.IsInfinity(speed) || speed < 0)
                return Abort("invalid speed or clock sample");
            if (!KscRoverPolicy.FlatSurface(pitch, roll, 8))
                return Abort("surface stopped being level");
            double elapsed = now - startedAt;
            if (elapsed < 0) return Abort("game clock moved backwards");
            if (!Braking)
            {
                if (elapsed >= TestTimeoutSeconds) return Abort("could not reach 10 m/s within 60 seconds");
                if (speed >= TriggerSpeed)
                {
                    Braking = true;
                    brakingStartedAt = now;
                    lastSampleAt = now;
                    lastSpeed = speed;
                }
                return false;
            }
            double dt = now - lastSampleAt;
            if (dt < 0 || dt > .5) return Abort("braking sample gap was too large");
            Distance += (lastSpeed + speed) * .5 * dt;
            lastSampleAt = now;
            lastSpeed = speed;
            if (double.IsNaN(stoppedAt))
            {
                if (speed <= StopSpeed) stoppedAt = now;
            }
            else if (speed > StopSpeed) stoppedAt = double.NaN;
            else if (now - stoppedAt >= StopSettleSeconds) return Complete("stopped");
            if (now - brakingStartedAt >= BrakeTimeoutSeconds)
                return Abort("brakes did not stop the rover within 20 seconds");
            return false;
        }

        bool Complete(string reason) { Done = true; Reason = reason; return true; }
        bool Abort(string reason) { Done = Aborted = true; Reason = reason; return true; }
    }

    internal sealed class RoverTurnTrial
    {
        const double StartSpeed = 2, SpeedStep = 1, TurnStepSeconds = 2, RollLimit = 15, PitchLimit = 8, TimeoutSeconds = 90;
        readonly double startedAt, maxSpeed;
        double nextStepAt;
        internal double TargetSpeed { get; private set; }
        internal double LastSafeSpeed { get; private set; }
        internal bool Done { get; private set; }
        internal bool Aborted { get; private set; }
        internal string Reason { get; private set; }

        internal RoverTurnTrial(double now, double maxSpeed)
        {
            startedAt = nextStepAt = now;
            this.maxSpeed = Math.Max(0, Math.Min(RoverTopSpeedTrial.TargetSpeed, maxSpeed));
            TargetSpeed = Math.Min(StartSpeed, this.maxSpeed);
        }

        internal bool Sample(double now, double speed, double pitch, double roll)
        {
            if (Done) return true;
            if (double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(speed) || double.IsInfinity(speed) || speed < 0
                || double.IsNaN(pitch) || double.IsInfinity(pitch) || double.IsNaN(roll) || double.IsInfinity(roll))
                return Abort("invalid speed, attitude, or clock sample");
            if (now < startedAt) return Abort("game clock moved backwards");
            if (Math.Abs(pitch) > PitchLimit) return Abort("surface stopped being level");
            if (Math.Abs(roll) > RollLimit)
                return LastSafeSpeed > 0 ? Complete("roll exceeded 15 degrees") : Abort("roll exceeded 15 degrees before a safe speed was measured");
            if (now - startedAt >= TimeoutSeconds)
                return LastSafeSpeed > 0 ? Complete("90-second limit") : Abort("could not reach a safe turn speed");
            if (now < nextStepAt || speed < TargetSpeed - .5) return false;
            LastSafeSpeed = TargetSpeed;
            if (TargetSpeed >= maxSpeed) return Complete("profile top-speed limit reached");
            TargetSpeed = Math.Min(maxSpeed, TargetSpeed + SpeedStep);
            nextStepAt = now + TurnStepSeconds;
            return false;
        }

        bool Complete(string reason) { Done = true; Reason = reason; return true; }
        bool Abort(string reason) { Done = Aborted = true; Reason = reason; return true; }
    }
}
