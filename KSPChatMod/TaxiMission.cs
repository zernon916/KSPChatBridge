using System;
using System.Collections.Generic;
using System.Globalization;

namespace KSPChatBridge
{
    internal sealed class TaxiMission
    {
        internal sealed class Point { internal string Name; internal double Lat, Lon; }
        internal static readonly Dictionary<string, Point> Builtin = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase) {
            { "Runway 09 start", new Point { Name="Runway 09 start", Lat=-.0485997, Lon=-74.724375 } },
            { "Runway 27 start", new Point { Name="Runway 27 start", Lat=-.0485997, Lon=-74.490300 } },
            { "Runway middle", new Point { Name="Runway middle", Lat=-.0494058, Lon=-74.6073375 } },
            { "KSC Pad", new Point { Name="KSC Pad", Lat=-.0972, Lon=-74.5577 } },
            { "Island 27 start", new Point { Name="Island 27 start", Lat=-1.516092, Lon=-71.856744 } },
            { "Island 09 start", new Point { Name="Island 09 start", Lat=-1.514809, Lon=-71.961815 } }
        };
        internal readonly List<Point> Points = new List<Point>();
        internal int Index;
        internal double Wheel, Yaw, Drive, Throttle, Distance;
        internal bool Brakes;
        internal string Result;
        readonly double cap;
        double deadline = double.NaN, lastThrottle;
        internal TaxiMission(string route, string body, double speed, Func<string, Point> resolve = null)
        {
            cap = FlightPolicy.Clamp(speed, 1, 25);
            foreach (string raw in (route ?? "").Split(';'))
            {
                string name = raw.Trim(); Point point;
                if (Builtin.TryGetValue(name, out point))
                { if (body != "Kerbin") throw new ArgumentException("Built-in taxi points are on Kerbin."); }
                else
                {
                    point = resolve == null ? null : resolve(name);
                    if (point != null) { Points.Add(point); if (Points.Count > 50) throw new ArgumentException("Maximum 50 taxi points."); continue; }
                    var coords = name.Split(','); double lat, lon;
                    if (coords.Length != 2 || !double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
                        || !double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
                        || double.IsNaN(lat) || double.IsNaN(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
                        throw new ArgumentException("Unknown taxi point: " + name + ". Use a built-in name or lat,lon.");
                    point = new Point { Name = name, Lat = lat, Lon = lon };
                }
                Points.Add(point); if (Points.Count > 50) throw new ArgumentException("Maximum 50 taxi points.");
            }
        }
        /// <summary>Line-following taxi on a ground chart (TaxiRoute): joins the nearest leg ahead, then pure pursuit on the centerline.</summary>
        internal readonly TaxiRoute Route; internal int Leg; internal double Cross;
        internal TaxiMission(TaxiRoute route, int leg, double speed)
        {
            cap = FlightPolicy.Clamp(speed, 1, 25); Route = route; Leg = Math.Max(0, leg);
            var end = route.Points[route.Points.Count - 1]; Points.Add(new Point { Name = end.Name, Lat = end.Lat, Lon = end.Lon });
        }
        bool stopped; double integ, lastNow = double.NaN;
        internal const double TaxiSpeed = 9, TurnSpeed = 5;
        /// <summary>Max taxi throttle for this thrust-to-weight (Luke: 20-30% at most, less for hot jets).</summary>
        internal static double MaxThrottle(double twr) { return FlightPolicy.Clamp(.25 / Math.Max(.5, double.IsNaN(twr) ? 1 : twr), .05, .3); }
        internal void Step(double now, double lat, double lon, double heading, double speed, double radius, bool grounded, bool powered, double twr = 1)
        {
            if (Result != null) return;
            if (!grounded) { Halt("Taxi stopped: not on the ground."); return; }
            double dt = double.IsNaN(lastNow) ? 0 : FlightPolicy.Clamp(now - lastNow, 0, .5); lastNow = now;
            if (!stopped) { if (speed < .5) { stopped = true; Brakes = false; } else { Wheel = Yaw = Drive = Throttle = 0; Brakes = true; return; } }   // full stop (e.g. after the rollout) before any taxi
            var point = Points[Index]; double tLat = point.Lat, tLon = point.Lon;
            if (Route != null) { int was = Leg; double ce; Route.Target(ref Leg, lat, lon, radius, out tLat, out tLon, out ce, out Distance); Cross = ce; if (Leg != was) deadline = double.NaN; }
            else Distance = NavigationMath.Distance(lat, lon, point.Lat, point.Lon, radius);
            if (double.IsNaN(deadline)) { deadline = now + Math.Min(Distance, Route != null ? 2000 : Distance) + 180; lastThrottle = now - 3; }
            if (now > deadline) { Halt("Taxi timeout: possible obstruction."); return; }
            if (Distance < (Index == Points.Count - 1 ? 12 : 25))
            {
                Index++; deadline = double.NaN;
                if (Index == Points.Count) Halt("Taxi arrived (" + point.Name + "): full stop, brakes on.");
                return;
            }
            double error = FlightPolicy.Wrap(NavigationMath.Bearing(lat, lon, tLat, tLon) - heading);
            double desired = Math.Min(Math.Min(cap, TaxiSpeed + 1), Math.Max(2, Distance / 6));
            if (Math.Abs(error) > 45) desired = Math.Min(desired, 3); else if (Math.Abs(error) > 15) desired = Math.Min(desired, TurnSpeed);
            Wheel = FlightPolicy.WheelSteering(error, 1); Yaw = FlightPolicy.Clamp(.02 * error, -.3, .3);
            Drive = powered ? FlightPolicy.Clamp(.25 * (desired - speed), -.3, 1) : 0;
            double err = desired - speed, maxT = MaxThrottle(twr);
            if (err < -1) integ = Math.Max(0, integ - .05 * dt); else integ = FlightPolicy.Clamp(integ + .01 * err * dt, 0, maxT);   // gentle PI, no windup
            Throttle = powered || err < -1 ? 0 : FlightPolicy.Clamp(.03 * err + integ, 0, maxT);
            Brakes = speed > desired + 1.5 || (Brakes && speed > desired + .3);   // wheel brakes slow us when over speed (hysteresis)
        }
        void Halt(string result) { Result = result; Wheel = Yaw = Drive = Throttle = 0; Brakes = true; }
    }
}
