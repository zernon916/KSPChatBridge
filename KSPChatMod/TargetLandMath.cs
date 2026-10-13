using System;

namespace KSPChatBridge
{
    internal sealed class TargetLandImpact
    {
        internal double Latitude, Longitude, Time, EastError, NorthError, DownrangeError, CrossrangeError;
        internal double Error { get { return Math.Sqrt(EastError * EastError + NorthError * NorthError); } }
    }

    internal sealed class TargetLandSolution
    {
        internal TargetLandImpact Impact, CorrectedImpact;
        internal double DvEast, DvNorth, DvUp;
    }

    /// <summary>Original spherical ballistic predictor. No terrain, lift, or learned vehicle model.</summary>
    internal static class TargetLandMath
    {
        const double Rad = Math.PI / 180;
        struct V
        {
            internal double X, Y, Z;
            internal V(double x, double y, double z) { X = x; Y = y; Z = z; }
            internal double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
            public static V operator +(V a, V b) { return new V(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
            public static V operator -(V a, V b) { return new V(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
            public static V operator *(V a, double s) { return new V(a.X * s, a.Y * s, a.Z * s); }
        }

        internal static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        internal static double Wrap(double degrees) { return (degrees % 360 + 540) % 360 - 180; }

        // Lat/lon in degrees; rotation in rad/s, positive eastward; velocity in
        // m/s in the INERTIAL local east/north/up basis at the initial position.
        // Drag is optional linear damping (1/s) relative to the rotating air.
        // Returns null if no surface crossing occurs within the bounded horizon.
        internal static TargetLandImpact Predict(double radius, double mu, double rotation, double lat, double lon,
            double altitude, double east, double north, double up, double targetLat, double targetLon, double drag = 0)
        {
            foreach (double x in new[] { radius, mu, rotation, lat, lon, altitude, east, north, up, targetLat, targetLon, drag })
                if (!Finite(x)) throw new ArgumentException("Nonfinite landing input.");
            if (radius <= 0 || mu <= 0 || altitude < 0 || Math.Abs(lat) > 90 || Math.Abs(targetLat) > 90 || drag < 0)
                throw new ArgumentException("Invalid landing geometry.");
            double la = lat * Rad, lo = Wrap(lon) * Rad, r0 = radius + altitude;
            V vertical = new V(Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la));
            V e = new V(-Math.Sin(lo), Math.Cos(lo), 0);
            V n = new V(-Math.Sin(la) * Math.Cos(lo), -Math.Sin(la) * Math.Sin(lo), Math.Cos(la));
            V p = vertical * r0, v = e * east + n * north + vertical * up;
            if (altitude == 0) return Impact(lat, lon, 0, radius, targetLat, targetLon, east, north);
            // Analytic periapsis rejection avoids integrating stable vacuum orbits.
            double energy = (east * east + north * north + up * up) / 2 - mu / r0;
            double h2 = r0 * r0 * (east * east + north * north);
            double eccentricity = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h2 / (mu * mu)));
            if (drag == 0 && (h2 / (mu * (1 + eccentricity)) > radius || (energy >= 0 && up >= 0))) return null;
            double horizon = Math.Min(86400, 4 * Math.PI * Math.Sqrt(r0 * r0 * r0 / mu));
            double t = 0;
            for (int step = 0; step < 24000 && t < horizon; step++)
            {
                double r = p.Length;
                double dt = Math.Min(4, Math.Min(horizon - t, Math.Max(.02, .02 * r / Math.Max(1, v.Length))));
                if (drag > 0) dt = Math.Min(dt, .1 / drag);
                // Midpoint integration handles velocity-dependent drag as well as gravity.
                V a = Accel(p, v, mu, rotation, drag);
                V midP = p + v * (dt / 2), midV = v + a * (dt / 2);
                V nextP = p + midV * dt, nextV = v + Accel(midP, midV, mu, rotation, drag) * dt;
                double nextR = nextP.Length;
                if (nextR <= radius)
                {
                    double f = (r - radius) / (r - nextR);
                    V hit = p + (nextP - p) * f;
                    double time = t + dt * f;
                    return Impact(Math.Asin(Math.Max(-1, Math.Min(1, hit.Z / hit.Length))) / Rad,
                        Math.Atan2(hit.Y, hit.X) / Rad - rotation * time / Rad, time, radius, targetLat, targetLon, east, north);
                }
                p = nextP; v = nextV; t += dt;
            }
            return null;
        }

        static V Accel(V p, V v, double mu, double rotation, double drag)
        {
            double r = p.Length;
            V air = new V(-rotation * p.Y, rotation * p.X, 0);
            return p * (-mu / (r * r * r)) - (v - air) * drag;
        }

        static TargetLandImpact Impact(double lat, double lon, double time, double radius, double targetLat, double targetLon, double east, double north)
        {
            double a = lat * Rad, b = targetLat * Rad, dl = Wrap(targetLon - lon) * Rad;
            double hav = Math.Pow(Math.Sin((b - a) / 2), 2) + Math.Cos(a) * Math.Cos(b) * Math.Pow(Math.Sin(dl / 2), 2);
            double distance = 2 * radius * Math.Asin(Math.Sqrt(Math.Max(0, Math.Min(1, hav))));
            double bearing = Math.Atan2(Math.Sin(dl) * Math.Cos(b), Math.Cos(a) * Math.Sin(b) - Math.Sin(a) * Math.Cos(b) * Math.Cos(dl));
            double ee = distance * Math.Sin(bearing), ne = distance * Math.Cos(bearing);
            double heading = Math.Atan2(east, north);
            return new TargetLandImpact { Latitude = lat, Longitude = Wrap(lon), Time = time, EastError = ee, NorthError = ne,
                DownrangeError = ee * Math.Sin(heading) + ne * Math.Cos(heading), CrossrangeError = ee * Math.Cos(heading) - ne * Math.Sin(heading) };
        }

        internal static TargetLandSolution Solve(double radius, double mu, double rotation, double lat, double lon,
            double altitude, double east, double north, double up, double targetLat, double targetLon, double drag = 0, double maxDv = 500)
        {
            if (!Finite(maxDv) || maxDv <= 0) throw new ArgumentException("Invalid correction budget.");
            Func<double, double, double, TargetLandImpact> predict = (de, dn, du) =>
                Predict(radius, mu, rotation, lat, lon, altitude, east + de, north + dn, up + du, targetLat, targetLon, drag);
            var solution = new TargetLandSolution { Impact = predict(0, 0, 0) };
            var best = solution.Impact;
            double[] dv = { 0, 0, 0 };
            // When still in orbit, seed several retrograde deorbits; never
            // fabricate an impact for a trajectory that misses the surface.
            if (best == null)
            {
                double speed = Math.Sqrt(east * east + north * north + up * up);
                if (speed <= 0) return solution;
                for (int i = 1; i <= 12; i++)
                {
                    double amount = Math.Min(maxDv, speed * .95) * i / 12;
                    var trial = predict(-east / speed * amount, -north / speed * amount, -up / speed * amount);
                    if (trial != null && (best == null || trial.Error < best.Error))
                    { best = trial; dv[0] = -east / speed * amount; dv[1] = -north / speed * amount; dv[2] = -up / speed * amount; }
                }
            }
            for (int iteration = 0; best != null && best.Time > .01 && best.Error > .1 && iteration < 6; iteration++)
            {
                double[,] j = new double[2, 3];
                for (int axis = 0; axis < 3; axis++)
                {
                    var probe = (double[])dv.Clone(); probe[axis] += 1;
                    var hit = predict(probe[0], probe[1], probe[2]);
                    if (hit == null) continue;
                    j[0, axis] = hit.EastError - best.EastError;
                    j[1, axis] = hit.NorthError - best.NorthError;
                }
                // Damped least-norm Newton correction: -J^T (J J^T)^-1 error.
                double aa = .01, bb = 0, cc = .01;
                for (int k = 0; k < 3; k++) { aa += j[0, k] * j[0, k]; bb += j[0, k] * j[1, k]; cc += j[1, k] * j[1, k]; }
                double det = aa * cc - bb * bb;
                double x = (cc * best.EastError - bb * best.NorthError) / det;
                double y = (aa * best.NorthError - bb * best.EastError) / det;
                bool improved = false;
                for (double scale = 1; scale >= .0625; scale *= .5)
                {
                    double[] trialDv = new double[3]; double length = 0;
                    for (int k = 0; k < 3; k++) { trialDv[k] = dv[k] - scale * (j[0, k] * x + j[1, k] * y); length += trialDv[k] * trialDv[k]; }
                    length = Math.Sqrt(length);
                    if (length > maxDv) for (int k = 0; k < 3; k++) trialDv[k] *= maxDv / length;
                    var hit = predict(trialDv[0], trialDv[1], trialDv[2]);
                    if (hit == null || hit.Error >= best.Error) continue;
                    dv = trialDv; best = hit; improved = true; break;
                }
                if (!improved) break;
            }
            solution.CorrectedImpact = best;
            solution.DvEast = dv[0]; solution.DvNorth = dv[1]; solution.DvUp = dv[2];
            return solution;
        }
    }
}
