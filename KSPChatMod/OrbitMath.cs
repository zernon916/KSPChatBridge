using System;

namespace KSPChatBridge
{
    // P5-4: pure orbital math for native maneuver nodes (no MechJeb needed to plan; MechJeb optional to execute).
    // Node delta-v uses KSP's node frame: x = radial, y = normal, z = prograde.
    internal static class OrbitMath
    {
        internal static double VisViva(double mu, double r, double a)
        {
            if (mu <= 0 || r <= 0) return double.NaN;
            double s = mu * (2 / r - 1 / a);
            return s > 0 ? Math.Sqrt(s) : double.NaN;
        }

        /// <summary>Prograde dv at radius r to circularize from semi-major axis a.</summary>
        internal static double CircularizeDv(double mu, double r, double a) { return Math.Sqrt(mu / r) - VisViva(mu, r, a); }

        /// <summary>Prograde dv at burn radius r (an apsis) so the opposite apsis becomes rOtherNew.</summary>
        internal static double ApsisChangeDv(double mu, double r, double a, double rOtherNew)
        {
            if (rOtherNew <= 0) return double.NaN;
            return VisViva(mu, r, (r + rOtherNew) / 2) - VisViva(mu, r, a);
        }

        /// <summary>Plane change by delta degrees at a node: (normal, prograde) components.</summary>
        internal static void InclinationDv(double speed, double deltaDeg, bool ascendingNode, out double normal, out double prograde)
        {
            double d = deltaDeg * Math.PI / 180;
            normal = speed * Math.Sin(d) * (ascendingNode ? 1 : -1);
            prograde = speed * (Math.Cos(d) - 1);
        }

        /// <summary>Synchronous orbit semi-major axis -> altitude (m) above radius.</summary>
        internal static double SyncAltitude(double mu, double rotationPeriod, double radius)
        {
            double a = Math.Pow(mu * rotationPeriod * rotationPeriod / (4 * Math.PI * Math.PI), 1.0 / 3);
            return a - radius;
        }

        /// <summary>Burn at apoapsis when it comes first (or periapsis is below ground/atmosphere-ish negative), like circularize.py.</summary>
        internal static bool UseApoapsis(double timeToAp, double timeToPe, double periapsisAltitude)
        {
            return timeToAp < timeToPe || periapsisAltitude < 0;
        }

        /// <summary>Warp target UT with lead, never in the past.</summary>
        internal static double WarpUt(double now, double eventUt, double leadSeconds)
        {
            return Math.Max(now, eventUt - Math.Max(0, leadSeconds));
        }

        /// <summary>Refusal for a requested apsis altitude (km) or null.</summary>
        internal static string ApsisGate(string which, double altitudeKm, double otherApsisM, bool isApoapsis, double atmosphereTopM)
        {
            if (double.IsNaN(altitudeKm) || altitudeKm < 0) return which + " altitude must be >= 0 km.";
            double m = altitudeKm * 1000;
            if (isApoapsis && m < otherApsisM) return "New apoapsis would be below the current periapsis; change the periapsis instead.";
            if (!isApoapsis && m > otherApsisM) return "New periapsis would be above the current apoapsis; change the apoapsis instead.";
            return null;
        }
    }
}
