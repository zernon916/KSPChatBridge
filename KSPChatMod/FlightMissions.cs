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
            double a = lat * Rad, b = lon * Rad, course = heading * Rad, arc = distance / radius;
            double q = Math.Asin(FlightPolicy.Clamp(Math.Sin(a) * Math.Cos(arc) + Math.Cos(a) * Math.Sin(arc) * Math.Cos(course), -1, 1));
            toLat = q / Rad;
            toLon = FlightPolicy.Wrap((b + Math.Atan2(Math.Sin(course) * Math.Sin(arc) * Math.Cos(a), Math.Cos(arc) - Math.Sin(a) * Math.Sin(q))) / Rad);
        }
        internal static double TerrainAhead(double lat, double lon, double heading, double speed, double radius, Func<double, double, double> height)
        {
            double highest = double.NaN;
            foreach (double seconds in new[] { 0.0, 10, 20, 35, 60 })
            {
                double la, lo; Offset(lat, lon, heading, Math.Max(50, speed) * seconds, radius, out la, out lo);
                double value = height(la, lo);
                if (!double.IsNaN(value) && !double.IsInfinity(value)) highest = double.IsNaN(highest) ? value : Math.Max(highest, value);
            }
            return highest;
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
        internal const double IfDistance = 12000, FafDistance = 5000, AlignCross = 150, AlignHeading = 8, GateDistance = 1000, GateCross = 40, GateHeading = 6;
        internal double Cross, Along;
        internal string Why = "";
        internal bool WantShort; internal string Kind = ""; internal double FixDistance = IfDistance;
        /// <summary>Round 3 approach: fly to an intercept fix 20 km out on the extended centerline, turn onto the centerline holding
        /// altitude, only descend on the glideslope once aligned (cross &lt; 150 m, track within 8 deg) before the 6 km FAF,
        /// and go around if not aligned within 40 m / 6 deg by 1 km out. track = ground track (NaN = unknown).</summary>
        internal void Step(double lat, double lon, double altitude, double agl, double speed, bool grounded, double radius, double stall, double track = double.NaN)
        {
            double course = NavigationMath.Bearing(Lat, Lon, EndLat, EndLon);
            double d = NavigationMath.Distance(Lat, Lon, lat, lon, radius);
            double theta = FlightPolicy.Wrap(NavigationMath.Bearing(Lat, Lon, lat, lon) - course) * Math.PI / 180;
            double along = d * Math.Cos(theta), cross = d * Math.Sin(theta);
            Cross = cross; Along = along;
            Distance = Math.Max(0, -along);
            double trackErr = double.IsNaN(track) ? 0 : Math.Abs(FlightPolicy.Wrap(track - course));
            if (Phase == "entry")
            {
                if (Kind.Length == 0) { Kind = PilotPolicy.ApproachKind(track, course, -along, WantShort); FixDistance = Kind == "short" ? PilotPolicy.ShortFix : IfDistance; }
                double vA = Math.Min(speed, 150), fixLat, fixLon, turnR = Math.Max(800, vA * vA / (9.81 * Math.Tan(20 * Math.PI / 180)));   // approach-speed radius: 330 m/s must not put the fix 60 km out
                NavigationMath.Offset(Lat, Lon, course + 180, FixDistance, radius, out fixLat, out fixLon);
                // Outbound (heading away from the runway) or abeam: aim for a base point 2 turn radii to our side of the fix,
                // so the 180 deg turn back rolls out on the centerline instead of overshooting it.
                bool outbound = !double.IsNaN(track) && Math.Abs(FlightPolicy.Wrap(track - course)) > 90;
                if (Kind == "long" && (outbound || -along < FixDistance - 2000)) NavigationMath.Offset(fixLat, fixLon, course + (cross >= 0 ? 90 : -90), 2 * turnR, radius, out fixLat, out fixLon);
                double fixDistance = NavigationMath.Distance(lat, lon, fixLat, fixLon, radius);
                DesiredHeading = NavigationMath.Bearing(lat, lon, fixLat, fixLon);
                DesiredAltitude = Elevation + 800; DesiredSpeed = Math.Max(1.5 * stall, Math.Min(150, speed));
                DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) * .05, -10, 15);
                // already out on the extended centerline beyond the FAF -> intercept straight away
                bool inbound = !double.IsNaN(track) && Math.Abs(FlightPolicy.Wrap(track - course)) < 60;
                double faf = Kind == "short" ? 2000 : FafDistance;
                if (fixDistance < 1500 || (-along > faf + 2000 && Math.Abs(cross) < 2500 && inbound)) Phase = "intercept";
            }
            if (Phase == "intercept")
            {
                double lead = Math.Max(2500, 1.2 * Math.Min(speed, 150) * Math.Min(speed, 150) / (9.81 * Math.Tan(20 * Math.PI / 180)));   // ~1 turn radius lead: converges without overshoot
                DesiredHeading = course - FlightPolicy.Clamp(Math.Atan2(cross, lead) * 180 / Math.PI, -40, 40);
                DesiredAltitude = Elevation + 800; DesiredSpeed = Math.Max(1.4 * stall, Math.Min(140, speed));
                DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) * .05, -6, 10);
                if (Math.Abs(cross) < AlignCross && trackErr < AlignHeading) Phase = "final";
                else if (-along < (Kind == "short" ? 2000 : FafDistance)) { Phase = "entry"; Kind = "long"; FixDistance = IfDistance; Why = "not aligned before the FAF; repositioning"; }   // never descend unaligned
            }
            if (Phase == "final" || Phase == "flare")
            {
                DesiredHeading = course - Math.Atan2(cross, 1800) * 180 / Math.PI;
                DesiredAltitude = Elevation + Math.Max(3, (350 - along) * Math.Tan(3 * Math.PI / 180));
                DesiredSpeed = (agl < 30 ? 1.15 : 1.3) * stall;
                DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) * .2, -4.5, 3);
                Gear = (Distance < 3000 && Math.Abs(cross) < AlignCross) || agl < 80;   // only on an aligned short final
                if (agl < 15) { Phase = "flare"; DesiredVs = agl > 5 ? -2 : -1; }
                if (Phase == "final" && Distance < GateDistance && Distance > 100 && (Math.Abs(cross) > GateCross || trackErr > GateHeading)) { Phase = "go around"; Why = "not lined up at 1 km (" + Math.Abs(cross).ToString("0") + " m, " + trackErr.ToString("0") + " deg)"; }
                // A missed threshold at height gets a go-around, never a dive back.
                double length = NavigationMath.Distance(Lat, Lon, EndLat, EndLon, radius);
                if (!grounded && along > length - 200 && agl > 3) Phase = "go around";
                if (grounded) Phase = "rollout";
            }
            if (Phase == "rollout") { DesiredHeading = course; DesiredSpeed = 0; Gear = Brakes = true; if (speed < 1) Phase = "stopped"; }
        }
    }
}
