using System;

namespace KSPChatBridge
{
    internal static class FlarePolicy
    {
        internal static bool ValidAoa(double aoa) { return aoa >= 0 && aoa <= 13; }
        internal static double PathAngle(double speed, double vs)
        { return speed > 10 && !double.IsInfinity(speed) && !double.IsNaN(vs) && !double.IsInfinity(vs) ? Math.Asin(FlightPolicy.Clamp(vs / speed, -1, 1)) * 180 / Math.PI : double.NaN; }
        internal static double Pitch(double aoa, double speed, double vs)
        { return ValidAoa(aoa) ? aoa + PathAngle(speed, vs) : double.NaN; }
        /// <summary>LAND5n: flare sink target (m/s, always <= 0): flare_sink (0-5), else touchdown_sink / chart / 3 (0.5-5). The flare never asks for a climb.</summary>
        internal static double SinkTarget(double flareSink, double touchdownSink, double chart)
        {
            if (!double.IsNaN(flareSink)) return -FlightPolicy.Clamp(flareSink, 0, 5);
            double s = !double.IsNaN(touchdownSink) ? touchdownSink : DefaultSink;   // LAND5n: blank = 1.5 (chart flare sinks defaulted to 3 -> ignored)
            return -FlightPolicy.Clamp(s, .5, 5);
        }
        internal const double TrackK = 1, TrackMax = 5, DefaultSink = 1.5;
        /// <summary>LAND5n: flare pitch flies the TARGET path (aoa + target path angle) and corrects the vs error (1 deg per m/s, +-5),
        /// so above the target sink the nose comes down instead of holding a level / climbing path (23:43: +1.4 m/s, 1.1 km float).</summary>
        internal static double Pitch(double aoa, double speed, double vs, double targetVs)
        {
            if (double.IsNaN(targetVs)) return Pitch(aoa, speed, vs);
            double t = Math.Min(0, targetVs), p = PathAngle(speed, t);
            if (!ValidAoa(aoa) || double.IsNaN(p) || double.IsNaN(vs)) return double.NaN;
            return aoa + p + FlightPolicy.Clamp(TrackK * (t - vs), -TrackMax, TrackMax);
        }
        /// <summary>LAND5n: flare_max_pitch_deg cap (NaN = no cap, 0-20).</summary>
        internal static double CapPitch(double pitch, double maxPitch)
        { return double.IsNaN(maxPitch) || double.IsNaN(pitch) ? pitch : Math.Min(pitch, FlightPolicy.Clamp(maxPitch, 0, 20)); }
    }

    internal static class FinalApproachPolicy
    {
        internal const double LookAheadM = 1500;
        internal const double LandingBankCap = 30;
        internal static double BankCommand(double bank, bool landing) { return landing ? double.IsNaN(bank) ? 0 : FlightPolicy.Clamp(bank, -LandingBankCap, LandingBankCap) : bank; }
        /// <summary>GROK FIX2 (Oct 19: planned 55 deg, flown 30 -> real radius 7.1 km vs 2.8 planned): every route / turn / lead / feed-forward
        /// computation uses the bank the landing command can actually reach.</summary>
        internal static double PlanningBank(double bank) { return double.IsNaN(bank) || bank <= 0 ? bank : Math.Min(bank, LandingBankCap); }
        // A virtual point on the extended 3-degree path; flare takes over before touchdown.
        internal static double Slope(double slope) { return double.IsNaN(slope) || double.IsInfinity(slope) || slope <= 0 ? Math.Tan(3 * Math.PI / 180) : slope; }
        internal static double TargetAltitude(double elevation, double along, double touchdown, double slope = double.NaN)
        { return elevation + (touchdown - along - LookAheadM) * Slope(slope); }
        internal static double VerticalSpeed(double targetAltitude, double altitude, double speed, double cross)
        { return (targetAltitude - altitude) * Math.Max(0, speed) / Math.Sqrt(LookAheadM * LookAheadM + cross * cross); }
    }

    internal sealed class FlapSchedule
    {
        static readonly double[] angles = { 0, 1, 5, 15, 25, 30 };
        static readonly double[] factors = { double.PositiveInfinity, 2.4, 2, 1.7, 1.45, 1.35 };
        internal double Degrees { get; private set; }
        double nextChange;
        internal double Step(double now, double speed, double stall, double wanted)
        {
            double limit = 0;
            for (int i = 1; i < angles.Length; i++) if (speed <= factors[i] * stall) limit = angles[i];
            double target = Math.Min(wanted, limit);
            if (Degrees > limit) Degrees = limit;
            else if (now >= nextChange)
            {
                if (target > Degrees)
                { foreach (double angle in angles) if (angle > Degrees) { if (angle <= target) Degrees = angle; break; } }
                else if (target < Degrees)
                    for (int i = angles.Length - 1; i >= 0; i--) if (angles[i] < Degrees) { Degrees = Math.Max(target, angles[i]); break; }
                nextChange = now + 2;
            }
            return Degrees;
        }
    }
    internal sealed class StallStudy
    {
        readonly double started;
        internal double Altitude, Heading;
        internal double? Measured;
        internal bool Finished;
        internal string Result;
        internal StallStudy(double now, double altitude, double heading) { started = now; Altitude = altitude; Heading = heading; }
        internal void Step(double now, double speed, double aoa, double vs, double agl, double roll)
        {
            if (Finished) return;
            if (agl < 250 || Math.Abs(roll) > 30 || vs < -8 || now - started > 90 || speed < 20)
            { Finished = true; Result = "Stall measurement stopped; no new speed cached."; return; }
            if (aoa >= 15 || (aoa > 8 && vs < -4))
            { Finished = true; Measured = speed; Result = "Stall speed measured."; }
        }
    }
}
