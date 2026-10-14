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
            return new RoverProfile
            {
                TopSpeed = Number(data, "top_speed"),
                BrakingDistance = Number(data, "braking_distance"),
                SafeTurnSpeed = Number(data, "safe_turn_speed"),
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
}
