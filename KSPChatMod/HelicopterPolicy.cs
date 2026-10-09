using System;

namespace KSPChatBridge
{
    internal sealed class HelicopterYaw
    {
        double integral, start = double.NaN, firstDemand, firstRate;
        bool consistent;
        int badWindows;
        internal double Sign = 1;
        internal double Step(double error, double rate, double dt)
        {
            integral = FlightPolicy.Clamp(integral + .01 * error * dt, -.6, .6);
            return FlightPolicy.Clamp(.03 * error - .05 * rate + integral, -1, 1);
        }
        internal bool Observe(double now, double demand, double rate)
        {
            if (double.IsNaN(start) || now < start) { start = now; firstDemand = demand; firstRate = rate; consistent = Math.Abs(demand) >= .3; return false; }
            consistent &= Math.Abs(demand) >= .3 && Math.Sign(demand) == Math.Sign(firstDemand);
            if (now - start < 1) return false;
            double response = (rate - firstRate) / (now - start) * firstDemand;
            if (consistent && response < -2) badWindows++;
            else if (consistent && response > 2) badWindows = 0;
            start = now; firstDemand = demand; firstRate = rate; consistent = Math.Abs(demand) >= .3;
            if (badWindows < 3) return false;
            Sign = -Sign; badWindows = 0; integral = 0; return true;
        }
    }
    internal static class HelicopterPolicy
    {
        internal static string RotorRole(double up, double forward, double right, double side)
        {
            if (Math.Abs(up) >= .8) return "lift";
            if (Math.Abs(forward) >= .7) return side < -.25 ? "left" : side > .25 ? "right" : "forward";
            return Math.Abs(right) >= .7 ? "tail" : "canted";
        }
        internal static void HoldVelocity(double distance, double bearing, double heading, out double forward, out double right)
        {
            double speed = Math.Min(3, Math.Max(0, distance) * .1), angle = FlightPolicy.Wrap(bearing - heading) * Math.PI / 180;
            forward = speed * Math.Cos(angle); right = speed * Math.Sin(angle);
        }
        internal static double Autorotation(double agl, double vs, double rpm, double reference)
        { return agl < 6 && vs < -1 ? 21 : FlightPolicy.Clamp(1 + .02 * (rpm - .9 * reference), 0, 6); }
    }
    internal sealed class RotorEmergency
    {
        double? lostAt;
        internal string Mode = "normal";
        internal string Step(double now, bool known, double rpm, double limit, bool motor, bool locked, bool ground)
        {
            if (ground) { lostAt = null; Mode = "normal"; return Mode; }
            if (!known) { lostAt = null; return Mode; }
            bool lost = !motor || rpm < .45 * limit;
            if (!lost) { lostAt = null; Mode = "normal"; return Mode; }
            if (!lostAt.HasValue || now < lostAt.Value) lostAt = now;
            if (now - lostAt.Value >= 1) Mode = locked ? "locked rotor: controlled descent" : "autorotation";
            return Mode;
        }
    }
    internal sealed class TouchdownGate
    {
        double? since;
        internal bool Step(double now, bool grounded, double verticalSpeed)
        {
            if (!grounded || Math.Abs(verticalSpeed) >= .5) { since = null; return false; }
            if (!since.HasValue || now < since.Value) since = now;
            return now - since.Value >= 3;
        }
    }
}
