using System;

namespace KSPChatBridge
{
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
