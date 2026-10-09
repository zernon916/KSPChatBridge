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
            { "Runway 27 start", new Point { Name="Runway 27 start", Lat=-.0502119, Lon=-74.490300 } },
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
        internal void Step(double now, double lat, double lon, double heading, double speed, double radius, bool grounded, bool powered)
        {
            if (Result != null) return;
            if (!grounded) { Halt("Taxi stopped: not on the ground."); return; }
            var point = Points[Index]; Distance = NavigationMath.Distance(lat, lon, point.Lat, point.Lon, radius);
            if (double.IsNaN(deadline)) { deadline = now + Distance + 180; lastThrottle = now - 3; }
            if (now > deadline) { Halt("Taxi timeout: possible obstruction."); return; }
            if (Distance < (Index == Points.Count - 1 ? 12 : 25))
            {
                Index++; deadline = double.NaN;
                if (Index == Points.Count) Halt("Taxi arrived; brakes on.");
                return;
            }
            double error = FlightPolicy.Wrap(NavigationMath.Bearing(lat, lon, point.Lat, point.Lon) - heading);
            double desired = Math.Min(cap, Math.Max(2, Distance / 6));
            if (Math.Abs(error) > 30) desired = Math.Min(desired, 3);
            Wheel = FlightPolicy.WheelSteering(error, 1); Yaw = FlightPolicy.Clamp(.02 * error, -.3, .3);
            Drive = powered ? FlightPolicy.Clamp(.25 * (desired - speed), -.3, 1) : 0;
            Throttle = powered ? 0 : FlightPolicy.Throttle(Throttle, FlightPolicy.Clamp(Throttle + .05 * Math.Sign(desired - speed), 0, .4), false, now, ref lastThrottle);
            Brakes = speed > desired + (powered ? 3 : 2);
        }
        void Halt(string result) { Result = result; Wheel = Yaw = Drive = Throttle = 0; Brakes = true; }
    }
}
