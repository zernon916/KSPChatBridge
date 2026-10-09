using System;

namespace KSPChatBridge
{
    internal sealed class ReverseRollout
    {
        bool attempted;
        internal bool Reversed { get; private set; }
        double throttle, lastStep;
        internal double Tick(bool grounded, double speed, double now, Func<bool, bool> setReverse)
        {
            if (!grounded || speed <= 30)
            { Cancel(setReverse); return 0; }
            if (!attempted && speed >= 40)
            {
                attempted = true; Reversed = setReverse(true); lastStep = now;
            }
            if (!Reversed) return 0;
            throttle = FlightPolicy.Throttle(throttle, .6, false, now, ref lastStep);
            return throttle;
        }
        internal void Cancel(Func<bool, bool> setReverse)
        {
            throttle = 0;
            if (Reversed) setReverse(false);
            Reversed = false;
            attempted = true; // a bounced landing cannot rearm reverse automatically
        }
    }
}
