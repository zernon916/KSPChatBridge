using System;

namespace KSPChatBridge
{
    internal struct AtmoLandCommand
    {
        internal bool ArmDrogue, DeployDrogue, ArmMain, DeployMain, StartBurn;
        internal double BurnAltitude, Throttle;
    }

    internal static class AtmoLandMath
    {
        // SI units. Drag is upward vertical aerodynamic acceleration; thrust is
        // available upward acceleration. Surface speed includes horizontal motion.
        internal static AtmoLandCommand Evaluate(double agl, double verticalSpeed, double dragDecel,
            double thrustAccel, double gravity, double surfaceSpeed, bool droguesAvailable, bool mainsAvailable,
            bool burnActive = false, double drogueSafeSpeed = 250, double mainSafeSpeed = 150,
            double drogueAltitude = 5000, double mainAltitude = 1500, double armAltitude = 10000,
            double touchdownSpeed = 2, double burnMargin = .2)
        {
            if (!Finite(agl) || !Finite(verticalSpeed) || !Finite(dragDecel) || !Finite(thrustAccel) ||
                !Finite(gravity) || !Finite(surfaceSpeed) || !Finite(drogueSafeSpeed) || !Finite(mainSafeSpeed) ||
                !Finite(drogueAltitude) || !Finite(mainAltitude) || !Finite(armAltitude) || !Finite(touchdownSpeed) ||
                !Finite(burnMargin) || dragDecel < 0 || thrustAccel < 0 || gravity <= 0 || surfaceSpeed < 0 ||
                drogueSafeSpeed <= 0 || mainSafeSpeed <= 0 || drogueAltitude <= 0 || mainAltitude <= 0 ||
                armAltitude < Math.Max(drogueAltitude, mainAltitude) || touchdownSpeed <= 0 || burnMargin < 0)
                throw new ArgumentException("Invalid atmospheric landing telemetry or limits.");

            var result = new AtmoLandCommand();
            double down = Math.Max(0, -verticalSpeed);
            // Never mistake a slow vertical descent with large lateral speed for
            // a safe chute opening. Arming readiness is also speed-gated.
            double speed = Math.Max(surfaceSpeed, Math.Abs(verticalSpeed));
            bool descending = verticalSpeed < 0 && agl > 0;
            result.ArmDrogue = descending && droguesAvailable && agl <= armAltitude && speed <= drogueSafeSpeed;
            result.ArmMain = descending && mainsAvailable && agl <= armAltitude && speed <= mainSafeSpeed;
            result.DeployDrogue = result.ArmDrogue && agl <= drogueAltitude;
            result.DeployMain = result.ArmMain && agl <= mainAltitude;

            // Do not assume today's drag persists to touchdown. Use half the
            // measured drag, capped at half gravity, for the stopping estimate.
            double credit = Math.Min(gravity * .5, dragDecel * .5);
            double effectiveGravity = gravity - credit;
            double braking = thrustAccel - effectiveGravity;
            double stopSpeed = Math.Sqrt(Math.Max(0, down * down - touchdownSpeed * touchdownSpeed));
            result.BurnAltitude = SuicideBurn.StartAlt(stopSpeed, effectiveGravity, thrustAccel, 1, burnMargin) + 2;
            result.StartBurn = thrustAccel > 0 && descending && down > touchdownSpeed && agl <= result.BurnAltitude;
            if (agl <= 0 || thrustAccel <= 0 || (!burnActive && !result.StartBurn)) return result;

            // Follow a decreasing descent-speed envelope, then hold ~2 m/s.
            // The remaining thrust margin permits error correction as drag changes.
            double targetDown = Math.Sqrt(touchdownSpeed * touchdownSpeed +
                2 * Math.Max(0, braking) * .65 * Math.Max(0, agl - 2));
            double request = (gravity - dragDecel + (down - targetDown)) / thrustAccel;
            result.Throttle = Math.Max(0, Math.Min(1, request));
            return result;
        }

        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
