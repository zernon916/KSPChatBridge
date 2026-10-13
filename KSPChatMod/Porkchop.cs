using System;

namespace KSPChatBridge
{
    /// <summary>Original universal-variable, zero-revolution transfer math. SI units.</summary>
    internal static class Porkchop
    {
        static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        static double Norm(double[] v) { return Math.Sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2]); }
        static bool Vector(double[] v)
        {
            return v != null && v.Length == 3 && Finite(v[0]) && Finite(v[1]) && Finite(v[2]);
        }

        static void Stumpff(double z, out double c, out double s)
        {
            if (Math.Abs(z) < 1e-4)
            {
                c = .5 - z/24 + z*z/720 - z*z*z/40320;
                s = 1.0/6 - z/120 + z*z/5040 - z*z*z/362880;
            }
            else if (z > 0)
            {
                double q = Math.Sqrt(z);
                c = 2*Math.Pow(Math.Sin(q/2), 2)/z;
                s = (q - Math.Sin(q))/(q*q*q);
            }
            else
            {
                double q = Math.Sqrt(-z);
                c = (Math.Cosh(q)-1)/(-z);
                s = (Math.Sinh(q)-q)/(q*q*q);
            }
        }

        static double FlightTime(double z, double radii, double a, out double y)
        {
            double c, s;
            Stumpff(z, out c, out s);
            y = radii + a*(z*s-1)/Math.Sqrt(c);
            // Below the physical branch's y=0 boundary, time is below any positive target.
            if (y < 0) return -1;
            return Math.Pow(y/c, 1.5)*s + a*Math.Sqrt(y);
        }

        /// <summary>
        /// Prograde about +Z (long way when cross(r1,r2).Z is negative).
        /// Positions and returned velocities are three-element double arrays.
        /// Returns false, with NaN velocities, for invalid input, collinear endpoints
        /// (the transfer plane is ambiguous), or failure to converge.
        /// </summary>
        internal static bool Lambert(double mu, double[] r1, double[] r2, double seconds,
            out double[] v1, out double[] v2)
        {
            v1 = new[] { double.NaN, double.NaN, double.NaN };
            v2 = new[] { double.NaN, double.NaN, double.NaN };
            if (!Finite(mu) || mu <= 0 || !Finite(seconds) || seconds <= 0 || !Vector(r1) || !Vector(r2)) return false;
            double scale = Norm(r1), end = Norm(r2);
            if (!Finite(scale) || !Finite(end) || scale <= 0 || end <= 0) return false;
            double[] p = new double[3], q = new double[3];
            for (int k = 0; k < 3; k++) { p[k] = r1[k]/scale; q[k] = r2[k]/end; }
            double cosine = Math.Max(-1, Math.Min(1, p[0]*q[0]+p[1]*q[1]+p[2]*q[2]));
            if (1-Math.Abs(cosine) < 1e-14) return false;
            double ratio = end/scale;
            double a = Math.Sqrt(ratio*(1+cosine));
            if (p[0]*q[1]-p[1]*q[0] < 0) a = -a;
            double speed = Math.Sqrt(mu/scale), target = seconds*speed/scale;
            if (!Finite(target) || target <= 0) return false;
            double lo = -1, hi = 4*Math.PI*Math.PI-1e-7, y;
            while (FlightTime(lo, 1+ratio, a, out y) > target && lo > -4096) lo *= 2;
            if (FlightTime(lo, 1+ratio, a, out y) > target ||
                FlightTime(hi, 1+ratio, a, out y) < target) return false;
            bool converged = false;
            for (int i = 0; i < 160; i++)
            {
                double z = (lo+hi)/2;
                double t = FlightTime(z, 1+ratio, a, out y);
                if (!Finite(t)) return false;
                if (Math.Abs(t-target) <= 1e-11*target) { converged = true; break; }
                if (t < target) lo = z; else hi = z;
            }
            if (!converged || y <= 0) return false;
            double f = 1-y, g = a*Math.Sqrt(y), gd = 1-y/ratio;
            for (int k = 0; k < 3; k++)
            {
                v1[k] = speed*(ratio*q[k]-f*p[k])/g;
                v2[k] = speed*(gd*ratio*q[k]-p[k])/g;
            }
            return Vector(v1) && Vector(v2);
        }

        /// <summary>
        /// Grid [departure index, travel index]. Phases are radians at t0; times are
        /// seconds relative to t0. Counts are sample counts, including both endpoints;
        /// a count of one samples the minimum. Circular coplanar orbits in XY, about +Z.
        /// Default cost is departure plus arrival v-infinity. Supply BOTH body mu and
        /// parking radius for each end to substitute ejection/circular capture cost.
        /// Unsupported/singular transfers are NaN cells. Invalid grid inputs throw.
        /// </summary>
        internal static double[,] Grid(double mu, double radius1, double phase1, double radius2, double phase2,
            double departureMin, double departureMax, int departureCount,
            double travelMin, double travelMax, int travelCount,
            double departureMu = 0, double departureParkingRadius = 0,
            double arrivalMu = 0, double arrivalParkingRadius = 0)
        {
            double[] values = { mu, radius1, phase1, radius2, phase2, departureMin, departureMax,
                travelMin, travelMax, departureMu, departureParkingRadius, arrivalMu, arrivalParkingRadius };
            foreach (double value in values) if (!Finite(value)) throw new ArgumentException("Grid inputs must be finite.");
            if (mu <= 0 || radius1 <= 0 || radius2 <= 0 || departureCount < 1 || travelCount < 1 ||
                departureMax < departureMin || travelMin <= 0 || travelMax < travelMin ||
                !Parking(departureMu, departureParkingRadius) || !Parking(arrivalMu, arrivalParkingRadius))
                throw new ArgumentException("Invalid grid range, orbit, or parking orbit.");
            double[,] result = new double[departureCount, travelCount];
            double n1 = Math.Sqrt(mu/radius1)/radius1, n2 = Math.Sqrt(mu/radius2)/radius2;
            for (int i = 0; i < departureCount; i++)
            for (int j = 0; j < travelCount; j++)
            {
                double dep = departureMin + (departureCount == 1 ? 0 : (departureMax-departureMin)*i/(departureCount-1));
                double tof = travelMin + (travelCount == 1 ? 0 : (travelMax-travelMin)*j/(travelCount-1));
                double p1 = phase1+n1*dep, p2 = phase2+n2*(dep+tof);
                double[] r1 = { radius1*Math.Cos(p1), radius1*Math.Sin(p1), 0 };
                double[] r2 = { radius2*Math.Cos(p2), radius2*Math.Sin(p2), 0 };
                double[] v1, v2;
                result[i,j] = double.NaN;
                if (!Lambert(mu, r1, r2, tof, out v1, out v2)) continue;
                v1[0] += radius1*n1*Math.Sin(p1); v1[1] -= radius1*n1*Math.Cos(p1);
                v2[0] += radius2*n2*Math.Sin(p2); v2[1] -= radius2*n2*Math.Cos(p2);
                double departure = Norm(v1), arrival = Norm(v2);
                if (departureMu > 0) departure = RocketMath.EjectionDv(departureMu, departureParkingRadius, departure);
                if (arrivalMu > 0) arrival = RocketMath.CaptureDv(arrivalMu, arrivalParkingRadius, arrival, arrivalParkingRadius);
                result[i,j] = departure+arrival;
            }
            return result;
        }

        static bool Parking(double mu, double radius)
        {
            return (mu == 0 && radius == 0) || (mu > 0 && radius > 0);
        }
    }
}
