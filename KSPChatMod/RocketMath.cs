using System;

namespace KSPChatBridge
{
    /// <summary>Pure rocket math for Hohmann transfer (no MechJeb needed to plan; MechJeb optional to execute).</summary>
    internal static class RocketMath
    {
        /// <summary>Hohmann transfer: semi-major axis, delta-v at r1 and r2, time of flight.</summary>
        internal static void Hohmann(double mu, double r1, double r2, out double dv1, out double dv2, out double time)
        {
            if (mu <= 0 || r1 <= 0 || r2 <= 0)
            {
                dv1 = double.NaN;
                dv2 = double.NaN;
                time = double.NaN;
                return;
            }

            double a = (r1 + r2) / 2.0;
            dv1 = Math.Sqrt(mu / r1) * (Math.Sqrt(2.0 * r2 / (r1 + r2)) - 1.0);
            dv2 = Math.Sqrt(mu / r2) * (1.0 - Math.Sqrt(2.0 * r1 / (r1 + r2)));
            time = Math.PI * Math.Sqrt(Math.Pow(a, 3.0) / mu);
        }

        /// <summary>Prograde dv at apoapsis rAp to circularize an orbit with periapsis rPe.</summary>
        internal static double CircularizeAtAp(double mu, double rAp, double rPe)
        {
            if (mu <= 0 || rAp <= 0 || rPe <= 0) return double.NaN;
            return Math.Sqrt(mu / rAp) - Math.Sqrt(mu * (2.0 / rAp - 2.0 / (rAp + rPe)));
        }

        /// <summary>Phase angle for Hohmann transfer.</summary>
        internal static double PhaseAngleDeg(double mu, double r1, double r2)
        {
            double t;
            Hohmann(mu, r1, r2, out _, out _, out t);
            double n2 = Math.Sqrt(mu / Math.Pow(r2, 3));
            double angle = 180.0 - n2 * t * 180.0 / Math.PI;
            while (angle >= 180.0) angle -= 360.0;
            while (angle < -180.0) angle += 360.0;
            return angle;
        }

        /// <summary>Capture delta-v calculation.</summary>
        internal static double CaptureDv(double mu, double rPe, double vInf, double rApTarget)
        {
            double vPe = Math.Sqrt(vInf * vInf + 2.0 * mu / rPe);
            double vOrbit = Math.Sqrt(mu * (2.0 / rPe - 2.0 / (rPe + rApTarget)));
            return vPe - vOrbit;
        }

        /// <summary>Ejection delta-v calculation.</summary>
        internal static double EjectionDv(double mu, double rPark, double vInf)
        {
            if (mu <= 0 || rPark <= 0 || vInf < 0) return double.NaN;
            return Math.Sqrt(vInf * vInf + 2.0 * mu / rPark) - Math.Sqrt(mu / rPark);
        }

        /// <summary>Ejection angle in degrees.</summary>
        internal static double EjectionAngleDeg(double mu, double rPark, double vInf)
        {
            if (mu <= 0 || rPark <= 0 || vInf < 0) return double.NaN;
            double e = 1.0 + rPark * vInf * vInf / mu;
            return 180.0 - Math.Acos(1.0 / e) * 180.0 / Math.PI;
        }

        /// <summary>Burn time (s) for dv with start mass, thrust (same units as mass*g0) and Isp (s).</summary>
        internal static double BurnTime(double dv, double mass, double thrust, double isp)
        {
            if (thrust <= 0 || isp <= 0) return double.NaN;
            const double g0 = 9.80665;
            double flow = thrust / (isp * g0);
            double m1 = mass / Math.Exp(dv / (isp * g0));
            return (mass - m1) / flow;
        }
    }
}
