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
        /// <summary>REV1: rollout reverse thrust - deploy once the wheels are down (nose down) above reverse_off_speed + 5, reverse throttle
        /// reverse_pct ramped in over 1.5 s, idle while |yaw rate| > 3 deg/s (until < 1.5), stowed below reverse_off_speed or when airborne.
        /// NoReversers = no engine could reverse (setReverse(true) failed).</summary>
        internal const double DefaultPct = 70, DefaultOffSpeed = 25, ArmMarginMs = 5, RampS = 1.5, YawBack = 3, YawOk = 1.5;
        internal bool NoReversers, YawBackoff; internal double Cmd; double deployedAt = double.NaN;
        internal static double Pct(double ov) { return double.IsNaN(ov) ? DefaultPct : FlightPolicy.Clamp(ov, 0, 100); }
        internal static double OffSpeed(double ov) { return double.IsNaN(ov) ? DefaultOffSpeed : FlightPolicy.Clamp(ov, 0, 150); }
        internal double Step(bool grounded, bool noseDown, double speed, double now, double yawRate, double pct, double offSpeed, Func<bool, bool> setReverse)
        {
            if (!grounded) { if (Reversed) Cancel(setReverse); return Cmd = 0; }   // a bounce stows (no rearm)
            if (speed <= offSpeed) { if (Reversed) Cancel(setReverse); attempted = true; return Cmd = 0; }
            if (!noseDown) return Cmd = 0;
            if (!attempted && speed > offSpeed + ArmMarginMs) { attempted = true; Reversed = setReverse(true); NoReversers = !Reversed; deployedAt = now; }
            if (!Reversed) return Cmd = 0;
            double yr = double.IsNaN(yawRate) ? 0 : Math.Abs(yawRate); YawBackoff = YawBackoff ? yr >= YawOk : yr > YawBack;
            return Cmd = YawBackoff ? 0 : pct / 100 * FlightPolicy.Clamp((now - deployedAt) / RampS, 0, 1);
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
