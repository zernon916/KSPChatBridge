using System;

namespace KSPChatBridge
{
    internal enum AscentPhase { Climb, CoastToAp, Circularize }

    internal struct AscentCommand
    {
        internal double TargetPitch, Throttle;
        internal AscentPhase Phase;
    }

    internal static class AscentGuidance
    {
        // SI units except q/maxQ (kPa) and pitch (degrees). TWR is available
        // full-throttle thrust / local weight, not already-throttled thrust.
        // Circularize means hand off to the node; this policy never burns it.
        internal static AscentCommand Evaluate(double altitude, double verticalSpeed, double horizontalSpeed,
            double twr, double q, double targetAlt, double atmosphereHeight, double apoapsisAlt,
            double timeToAp, double maxQ = 25)
        {
            if (!Finite(altitude) || !Finite(verticalSpeed) || !Finite(horizontalSpeed) || !Finite(twr) ||
                !Finite(q) || !Finite(targetAlt) || !Finite(atmosphereHeight) || !Finite(apoapsisAlt) ||
                !Finite(timeToAp) || !Finite(maxQ) || targetAlt <= 0 || atmosphereHeight < 0 ||
                targetAlt <= atmosphereHeight || twr < 0 || q < 0 || maxQ <= 0)
                throw new ArgumentException("Invalid ascent telemetry or target.");

            if (apoapsisAlt >= targetAlt)
                return new AscentCommand { TargetPitch = 0, Throttle = 0,
                    Phase = altitude >= atmosphereHeight && timeToAp >= 0 && timeToAp <= 30
                        ? AscentPhase.Circularize : AscentPhase.CoastToAp };

            double speed = Math.Sqrt(verticalSpeed * verticalSpeed + horizontalSpeed * horizontalSpeed);
            double startSpeed = AscentProfile.TurnStartSpeed(twr);
            // Blend the early speed trigger into the altitude profile; no learned
            // launch model or stored engine parameters are needed after staging.
            double startAlt = 1000 * (1 - Math.Max(0, Math.Min(1, speed / startSpeed - 1)));
            double endAlt = Math.Max(2000, Math.Min(targetAlt, atmosphereHeight > 0 ? atmosphereHeight : targetAlt * .75));
            double pitch = AscentProfile.PitchDeg(Math.Max(0, altitude), startAlt, endAlt, twr);
            double throttle = twr > 0 ? 1 : 0;
            if (atmosphereHeight > 0 && altitude < atmosphereHeight * .5 && twr > 0)
                throttle = Math.Min(throttle, 1.9 / twr);
            // Proportional max-q relief; q has inertia, so this is a limiter,
            // not a promise that every vehicle can stay below the limit.
            if (q > maxQ) throttle *= maxQ / q;
            return new AscentCommand { TargetPitch = pitch, Throttle = Math.Max(0, Math.Min(1, throttle)), Phase = AscentPhase.Climb };
        }

        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
