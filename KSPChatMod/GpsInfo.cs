using System;
using System.Globalization;

namespace KSPChatBridge
{
    internal static class GpsInfo
    {
        const double Deg = Math.PI / 180.0;
        static bool Valid(params double[] values) { foreach (double v in values) if (double.IsNaN(v) || double.IsInfinity(v)) return false; return true; }
        internal static double DistanceM(double lat1, double lon1, double lat2, double lon2, double r)
        {
            if (!Valid(lat1, lon1, lat2, lon2, r) || r <= 0) return double.NaN;
            double p1 = lat1 * Deg, p2 = lat2 * Deg, dp = (lat2 - lat1) * Deg, dl = (lon2 - lon1) * Deg;
            double a = Math.Pow(Math.Sin(dp / 2), 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Pow(Math.Sin(dl / 2), 2);
            return 2 * r * Math.Asin(Math.Sqrt(Math.Max(0, Math.Min(1, a))));
        }
        internal static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
        {
            if (!Valid(lat1, lon1, lat2, lon2)) return double.NaN;
            double p1 = lat1 * Deg, p2 = lat2 * Deg, dl = (lon2 - lon1) * Deg;
            double y = Math.Sin(dl) * Math.Cos(p2), x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            double b = Math.Atan2(y, x) / Deg; return (b + 360) % 360;
        }
        internal static double CenterlineOffsetM(double lat, double lon, double r)
        {
            if (!Valid(lat, lon, r) || r <= 0) return double.NaN;
            // Local east/north coordinates centered on the western end.
            double midLat = (KscRunway.Lat09 + KscRunway.Lat27) * .5 * Deg;
            double dx = (lon - KscRunway.Lon09) * Deg * Math.Cos(midLat) * r;
            double dy = (lat - KscRunway.Lat09) * Deg * r;
            double ex = (KscRunway.Lon27 - KscRunway.Lon09) * Deg * Math.Cos(midLat) * r;
            double ny = (KscRunway.Lat27 - KscRunway.Lat09) * Deg * r;
            return (ex * dy - ny * dx) / Math.Sqrt(ex * ex + ny * ny);
        }
        /// <summary>Compass degrees as 000-359 (359.6 rounds to 000, not 360); n/a for NaN.</summary>
        internal static string Compass(double d) { return Valid(d) ? (((Math.Round(d) % 360) + 360) % 360).ToString("000", CultureInfo.InvariantCulture) : "n/a"; }
        static string F(double n, string format) { return Valid(n) ? n.ToString(format, CultureInfo.InvariantCulture) : "n/a"; }
        internal static string Format(string vessel, string body, double bodyRadius, double lat, double lon, double altAsl, double radarAlt, double heading, double track, double groundSpeed, bool landed, string situation, double ut)
        {
            string s = "GPS " + (vessel ?? "?") + " @ " + (body ?? "?") + ": lat " + F(lat, "0.00000") + " lon " + F(lon, "0.00000")
                + " | ASL " + F(altAsl, "0") + " m, radar " + (Valid(radarAlt) && radarAlt >= 0 ? F(radarAlt, "0") + " m" : "n/a")
                + " | hdg " + Compass(heading) + " trk " + Compass(track) + " gs " + F(groundSpeed, "0.0") + " m/s"
                + " | " + (string.IsNullOrEmpty(situation) ? (landed ? "LANDED" : "UNKNOWN") : situation.ToUpperInvariant());
            if (string.Equals(body, "Kerbin", StringComparison.Ordinal) && Valid(lat, lon, bodyRadius) && bodyRadius > 0)
            {
                double d09 = DistanceM(lat, lon, KscRunway.Lat09, KscRunway.Lon09, bodyRadius), d27 = DistanceM(lat, lon, KscRunway.Lat27, KscRunway.Lon27, bodyRadius);
                bool west = d09 <= d27; double d = west ? d09 : d27;
                s += " | rwy " + (west ? "09" : "27") + " end " + F(d / 1000, "0.0") + " km brg " + Compass(BearingDeg(lat, lon, west ? KscRunway.Lat09 : KscRunway.Lat27, west ? KscRunway.Lon09 : KscRunway.Lon27));
                if (d <= 50000) { double off = CenterlineOffsetM(lat, lon, bodyRadius); if (Valid(off)) s += " off " + F(Math.Abs(off), "0") + " m " + (off >= 0 ? "N" : "S") + " of centerline"; }
            }
            return s + " | UT " + F(ut, "0");
        }
    }
}
