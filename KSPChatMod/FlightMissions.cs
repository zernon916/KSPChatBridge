using System;

namespace KSPChatBridge
{
    internal static class NavigationMath
    {
        const double Rad = Math.PI / 180;
        internal static double Distance(double lat, double lon, double toLat, double toLon, double radius)
        {
            double p = lat * Rad, q = toLat * Rad, d = (toLat - lat) * Rad, l = (toLon - lon) * Rad;
            double a = Math.Pow(Math.Sin(d / 2), 2) + Math.Cos(p) * Math.Cos(q) * Math.Pow(Math.Sin(l / 2), 2);
            return 2 * radius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }
        internal static double Bearing(double lat, double lon, double toLat, double toLon)
        {
            double p = lat * Rad, q = toLat * Rad, l = (toLon - lon) * Rad;
            return (Math.Atan2(Math.Sin(l) * Math.Cos(q), Math.Cos(p) * Math.Sin(q) - Math.Sin(p) * Math.Cos(q) * Math.Cos(l)) / Rad + 360) % 360;
        }
        internal static void Offset(double lat, double lon, double heading, double distance, double radius, out double toLat, out double toLon)
        {
            toLat = lat + distance * Math.Cos(heading * Rad) / radius / Rad;
            toLon = lon + distance * Math.Sin(heading * Rad) / (radius * Math.Max(.05, Math.Cos(lat * Rad))) / Rad;
        }
    }

    internal sealed class TakeoffMission
    {
        readonly double start;
        internal string Phase = "roll";
        internal TakeoffMission(double now) { start = now; }
        internal double Step(double now, double speed, double agl, bool grounded, double stall)
        {
            if (agl >= 100 && !grounded) { Phase = "climbout complete"; return 9; }
            if (grounded && now - start > 120) { Phase = "takeoff timeout"; return 0; }
            if (grounded && speed < 1.25 * stall) { Phase = "roll"; return 0; }
            Phase = grounded ? "rotate" : "climbout";
            return !grounded && speed < 1.15 * stall ? 1 : 9;
        }
    }

    internal sealed class RunwayMission
    {
        internal double Lat, Lon, EndLat, EndLon, Elevation;
        internal string Phase = "entry";
        internal double DesiredHeading, DesiredAltitude, DesiredSpeed, DesiredVs;
        internal bool Gear, Brakes;
        internal double Distance;
        internal void Step(double lat, double lon, double altitude, double agl, double speed, bool grounded, double radius, double stall)
        {
            double course = NavigationMath.Bearing(Lat, Lon, EndLat, EndLon);
            double d = NavigationMath.Distance(Lat, Lon, lat, lon, radius);
            double theta = FlightPolicy.Wrap(NavigationMath.Bearing(Lat, Lon, lat, lon) - course) * Math.PI / 180;
            double along = d * Math.Cos(theta), cross = d * Math.Sin(theta);
            Distance = Math.Max(0, -along);
            if (Phase == "entry")
            {
                double entryLat, entryLon;
                NavigationMath.Offset(Lat, Lon, course + 180, 11000, radius, out entryLat, out entryLon);
                double entryDistance = NavigationMath.Distance(lat, lon, entryLat, entryLon, radius);
                DesiredHeading = NavigationMath.Bearing(lat, lon, entryLat, entryLon);
                DesiredAltitude = Elevation + 800; DesiredSpeed = Math.Max(1.5 * stall, Math.Min(150, speed));
                DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) * .05, -10, 15);
                if (entryDistance < 800 && Math.Abs(cross) < 800) Phase = "final";
            }
            if (Phase == "final" || Phase == "flare")
            {
                DesiredHeading = course - Math.Atan2(cross, 1800) * 180 / Math.PI;
                DesiredAltitude = Elevation + Math.Max(3, (350 - along) * Math.Tan(3 * Math.PI / 180));
                DesiredSpeed = (agl < 30 ? 1.15 : 1.3) * stall;
                DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) * .2, -4.5, 3);
                Gear = Distance < 2500 || Distance / Math.Max(1, speed) < 30 || agl < 80;
                if (agl < 15) { Phase = "flare"; DesiredVs = agl > 5 ? -2 : -1; }
                // A missed threshold at height gets a go-around, never a dive back.
                double length = NavigationMath.Distance(Lat, Lon, EndLat, EndLon, radius);
                if (!grounded && along > length - 200 && agl > 3) Phase = "go around";
                if (grounded) Phase = "rollout";
            }
            if (Phase == "rollout") { DesiredHeading = course; DesiredSpeed = 0; Gear = Brakes = true; if (speed < 1) Phase = "stopped"; }
        }
    }
}
