using System;

namespace KSPChatBridge
{
    internal static class FlightPolicy
    {
        internal static double Clamp(double value, double lo, double hi) { return Math.Max(lo, Math.Min(hi, value)); }
        internal static double Wrap(double degrees) { return (degrees % 360 + 540) % 360 - 180; }
        internal static double BankLimit(double speed) { return speed > 250 ? 10 : 20; }
        internal static double WheelSteering(double headingError, double limit)
        { return Clamp(-.04 * Wrap(headingError), -limit, limit); }
        internal static bool RotorPowerReady(double chargeFraction)
        { return !double.IsNaN(chargeFraction) && !double.IsInfinity(chargeFraction) && chargeFraction >= .25; }
        internal static bool SafeSolar(bool atmosphericFlight, double speed, double pressurePa)
        { return !atmosphericFlight || (speed <= 220 && pressurePa <= 12000); }
        internal static double Trim(double current, double input)
        { return Math.Abs(input) < .04 ? current : Clamp(current + Math.Sign(input) * Math.Min(.015, Math.Abs(input) * .05), -1, 1); }
        internal static double SurfaceSign(double forward, bool inverted, bool deployInverted)
        { return (forward > .05 ? -1 : 1) * (inverted ? -1 : 1) * (deployInverted ? -1 : 1); }
        // Port of alt_hold.vertical_speed_target; state is captured on band entry.
        internal static double VerticalSpeed(double altitude, double target, double up, double down, double kin, double band, ref double? capture)
        {
            band = Clamp(band, 10, 500);
            double error = target - altitude;
            if (Math.Abs(error) <= band)
            {
                if (!capture.HasValue) capture = altitude;
                return Math.Abs(error) <= band * .9 ? 0 : Clamp(.08 * kin * (capture.Value - altitude), -3, 3);
            }
            capture = null;
            double soften = Clamp((Math.Abs(error) - band) / band, 0, 1);
            return Clamp(.08 * kin * error, -Math.Abs(down), Math.Abs(up)) * soften;
        }
        internal static double Throttle(double current, double desired, bool flying, double now, ref double lastStep)
        {
            if (now - lastStep < 3) return current;
            double delta = desired - current;
            if (Math.Abs(delta) < .005) return current;
            lastStep = now;
            return Clamp(current + Clamp(delta, -.05, .05), flying ? .05 : 0, 1);
        }
    }

    internal sealed class ControlLease
    {
        internal string VesselId { get; private set; }
        internal string Owner { get; private set; }
        internal bool Acquire(string vessel, string owner)
        {
            if (string.IsNullOrEmpty(vessel) || string.IsNullOrEmpty(owner)) return false;
            if (Owner != null && (VesselId != vessel || Owner != owner)) return false;
            VesselId = vessel; Owner = owner; return true;
        }
        internal void Release() { Owner = VesselId = null; }
        internal void VesselChanged(string vessel) { if (VesselId != vessel) Release(); }
    }
}
