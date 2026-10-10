using System.Collections.Generic;
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

    /// <summary>Computed approach chart for one runway end: long (12 km) and short (4 km) final fixes on the extended centerline,
    /// left/right downwind + base-apex joins spaced for a bank-limited turn (r = v^2/(g tan bank)) that rolls out on the centerline,
    /// fix altitudes AGL (Luke: settings approach_long_agl / approach_short_agl) and every leg raised over sampled terrain.</summary>
    /// <summary>One runway end's edited chart from PluginData/approaches.json (validated; invalid -> null = computed defaults).</summary>
    internal sealed class ApproachOverride
    {
        internal sealed class Fix { internal string Role, Name, Side; internal double Lat, Lon, Alt, Agl = double.NaN; internal string AltRef = "agl"; }
        internal string AltRef = "msl";
        internal readonly List<Fix> Fixes = new List<Fix>();
        internal double FlareStartM = 15, FlareSinkMs = 1, TouchdownM = 350, TchM = 15;
        internal static readonly string[] Roles = { "faf", "sf", "dw_left", "dwend_left", "base_left", "dw_right", "dwend_right", "base_right", "wp" };
        internal const int MaxFixes = 16;
        static double N(Dictionary<string, object> d, string k, double def) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : def; }
        /// <summary>Parse one end. Fixes must sit within 60 km of the threshold, 0-6000 m above the runway; flare/touchdown in sane ranges.</summary>
        /// alt_ref "agl" (editor default): alt is height above the terrain under the fix, converted here with in-game terrain (runway elevation if unavailable); "msl" = legacy.
        internal static ApproachOverride Parse(object raw, double thrLat, double thrLon, double elevation, double radius, out string why, Func<double, double, double> terrain = null)
        {
            why = ""; var d = raw as Dictionary<string, object>; if (d == null) { why = "not an object"; return null; }
            try
            {
                var o = new ApproachOverride { FlareStartM = N(d, "flare_start_m", 15), FlareSinkMs = N(d, "flare_sink_ms", 1), TouchdownM = N(d, "touchdown_m", 350), TchM = N(d, "tch_m", 15) };
                if (o.FlareStartM < 3 || o.FlareStartM > 60 || o.FlareSinkMs < .2 || o.FlareSinkMs > 5 || o.TouchdownM < 0 || o.TouchdownM > 1500 || o.TchM < 0 || o.TchM > 100) { why = "flare/touchdown/TCH out of range"; return null; }
                object ar; string altRef = d.TryGetValue("alt_ref", out ar) && ar != null ? Convert.ToString(ar) : "agl";
                if (altRef != "agl" && altRef != "msl") { why = "alt_ref must be agl or msl"; return null; }
                o.AltRef = altRef;
                object fl; if (d.TryGetValue("fixes", out fl) && fl is System.Collections.IList)
                    foreach (object x in (System.Collections.IList)fl)
                    {
                        var fd = x as Dictionary<string, object>; if (fd == null) { why = "bad fix"; return null; }
                        object r; string role = fd.TryGetValue("role", out r) ? Convert.ToString(r) : "";
                        if (Array.IndexOf(Roles, role) < 0) { why = "unknown fix role " + role; return null; }
                        object sd; string side = fd.TryGetValue("side", out sd) ? Convert.ToString(sd) : role.EndsWith("_left") ? "left" : role.EndsWith("_right") ? "right" : "both";
                        if (side != "left" && side != "right" && side != "both") { why = role + ": side must be left/right/both"; return null; }
                        if (o.Fixes.Count >= MaxFixes) { why = "more than " + MaxFixes + " fixes"; return null; }
                        object nm; var f = new Fix { Role = role, Side = side, Name = fd.TryGetValue("name", out nm) ? Convert.ToString(nm) : role, Lat = N(fd, "lat", double.NaN), Lon = N(fd, "lon", double.NaN), Alt = N(fd, "alt", double.NaN) };
                        if (double.IsNaN(f.Lat) || double.IsNaN(f.Lon) || double.IsNaN(f.Alt)) { why = role + ": lat/lon/alt missing"; return null; }
                        object far; string fRef = fd.TryGetValue("alt_ref", out far) && far != null ? Convert.ToString(far) : altRef;
                        if (fRef != "agl" && fRef != "msl") { why = role + ": alt_ref must be agl or msl"; return null; }
                        f.AltRef = fRef;
                        if (fRef == "agl")
                        {
                            if (f.Alt < 0 || f.Alt > 6000) { why = role + ": AGL altitude outside 0..6000 m"; return null; }
                            double g = terrain != null ? terrain(f.Lat, f.Lon) : double.NaN;
                            f.Agl = f.Alt; f.Alt = (double.IsNaN(g) ? elevation : g) + f.Alt;
                        }
                        if (NavigationMath.Distance(thrLat, thrLon, f.Lat, f.Lon, radius) > 60000) { why = role + ": more than 60 km from the threshold"; return null; }
                        if (fRef == "msl" && (f.Alt < 0 || f.Alt > elevation + 6000)) { why = role + ": altitude outside runway..+6000 m"; return null; }
                        o.Fixes.Add(f);
                    }
                return o;
            }
            catch (Exception ex) { why = ex.Message; return null; }
        }
    }

    internal sealed class ApproachChart
    {
        internal sealed class Wp { internal string Name; internal double Lat, Lon, Alt; }
        internal const double LongFix = 12000, ShortFix = 4000, Clearance = 250, Gs = 9.81;
        internal double ThrLat, ThrLon, Course, Elevation, TurnRadius, LongAlt, ShortAlt, MinAlt, RouteFixAlt = double.NaN;
        internal Wp Long, Short, DownLeftA, DownLeftB, ApexLeft, DownRightA, DownRightB, ApexRight;
        Func<double, double, double> terrain; double radius;
        internal static double Radius(double speed, double bankDeg) { double v = Math.Max(60, Math.Min(speed, 200)); return Margin * v * v / (Gs * Math.Tan(FlightPolicy.Clamp(bankDeg, 10, 60) * Math.PI / 180)); }
        internal const double Margin = 1.3, GlideDeg = 3;   // join turns: 20 deg bank at actual speed + 30% margin
        double T(double lat, double lon) { if (terrain == null) return Elevation; double h = terrain(lat, lon); return double.IsNaN(h) ? Elevation : h; }
        Wp At(string name, double behind, double side)
        {
            double la, lo; NavigationMath.Offset(ThrLat, ThrLon, Course + 180, behind, radius, out la, out lo);
            if (side != 0) NavigationMath.Offset(la, lo, Course + (side > 0 ? 90 : -90), Math.Abs(side), radius, out la, out lo);
            return new Wp { Name = name, Lat = la, Lon = lo };
        }
        internal static ApproachChart Build(double thrLat, double thrLon, double course, double elevation, double speed, double bankDeg, double radius,
            Func<double, double, double> terrain, double longAgl = -1, double shortAgl = -1)
        {
            var c = new ApproachChart { ThrLat = thrLat, ThrLon = thrLon, Course = course, Elevation = elevation, terrain = terrain, radius = radius };
            double r = c.TurnRadius = Radius(speed, bankDeg);
            c.Long = c.At("long final 12 km", LongFix, 0); c.Short = c.At("short final 4 km", ShortFix, 0);
            c.DownLeftA = c.At("left downwind", 0, -2 * r); c.DownLeftB = c.At("left downwind end", LongFix, -2 * r); c.ApexLeft = c.At("left base", LongFix + r, -r);
            c.DownRightA = c.At("right downwind", 0, 2 * r); c.DownRightB = c.At("right downwind end", LongFix, 2 * r); c.ApexRight = c.At("right base", LongFix + r, r);
            double tg = Math.Tan(GlideDeg * Math.PI / 180);   // continuous 3 deg glideslope: ~630 m at 12 km, ~210 m at 4 km above the runway
            c.Long.Alt = longAgl > 0 ? c.T(c.Long.Lat, c.Long.Lon) + longAgl : elevation + LongFix * tg;
            c.Short.Alt = shortAgl > 0 ? c.T(c.Short.Lat, c.Short.Lon) + shortAgl : elevation + ShortFix * tg;
            // glide path from each fix to the threshold must clear terrain along the centerline (+60 m); raise the fix if not
            for (double d = 1500; d < LongFix; d += 300)   // inside 1.5 km the path meets the runway itself
            {
                var p = c.At("", d, 0); double need = c.T(p.Lat, p.Lon) + 60;
                double viaLong = elevation + (c.Long.Alt - elevation) * d / LongFix;
                if (viaLong < need) c.Long.Alt += (need - viaLong) * LongFix / d;
                if (d < ShortFix) { double viaShort = elevation + (c.Short.Alt - elevation) * d / ShortFix; if (viaShort < need) c.Short.Alt += (need - viaShort) * ShortFix / d; }
            }
            c.LongAlt = c.Long.Alt; c.ShortAlt = c.Short.Alt;
            foreach (var w in new[] { c.DownLeftA, c.DownLeftB, c.ApexLeft, c.DownRightA, c.DownRightB, c.ApexRight }) w.Alt = Math.Max(c.Long.Alt, c.T(w.Lat, w.Lon) + Clearance);
            c.MinAlt = elevation + 150;
            return c;
        }
        /// <summary>Glide-path altitude at distance d (m) before the threshold, through the long fix.</summary>
        internal double GlideAlt(double d)
        {
            double fixAlt = double.IsNaN(RouteFixAlt) ? LongAlt : RouteFixAlt, fixD = RouteFixD > 0 ? RouteFixD : LongFix;
            return Elevation + Math.Max(Tch, (fixAlt - Elevation) * Math.Max(0, d) / fixD);
        }
        internal double RouteFixD, Tch = 3;
        internal List<Wp> CustomLeft, CustomRight;
        /// <summary>Luke's edited chart (approaches.json): moves named fixes by role and sets threshold crossing height.</summary>
        internal void Apply(ApproachOverride o)
        {
            // Waypoints added/removed in the editor -> fly the listed join sequence per side (in file order) instead of the computed one.
            bool custom = false;
            foreach (var role in new[] { "dw_left", "dwend_left", "base_left", "dw_right", "dwend_right", "base_right", "wp" })
                if (o.Fixes.Exists(f => f.Role == role) == (role == "wp")) custom = true;
            if (custom && o.Fixes.Count > 0)
            {
                CustomLeft = new List<Wp>(); CustomRight = new List<Wp>();
                foreach (var f in o.Fixes)
                {
                    if (f.Role == "faf" || f.Role == "sf") continue;
                    var w = new Wp { Name = string.IsNullOrEmpty(f.Name) ? f.Role : f.Name, Lat = f.Lat, Lon = f.Lon, Alt = f.Alt };
                    if (f.Side != "right") CustomLeft.Add(w);
                    if (f.Side != "left") CustomRight.Add(w);
                }
            }
            foreach (var f in o.Fixes)
            {
                Wp w = f.Role == "faf" ? Long : f.Role == "sf" ? Short : f.Role == "dw_left" ? DownLeftA : f.Role == "dwend_left" ? DownLeftB : f.Role == "base_left" ? ApexLeft
                    : f.Role == "dw_right" ? DownRightA : f.Role == "dwend_right" ? DownRightB : f.Role == "base_right" ? ApexRight : null;
                if (w == null) continue;
                w.Lat = f.Lat; w.Lon = f.Lon; w.Alt = f.Alt; if (!string.IsNullOrEmpty(f.Name)) w.Name = f.Name;
            }
            LongAlt = Long.Alt; ShortAlt = Short.Alt; Tch = o.TchM;
        }
        internal double Slope { get { double fixAlt = double.IsNaN(RouteFixAlt) ? LongAlt : RouteFixAlt, fixD = RouteFixD > 0 ? RouteFixD : LongFix; return Math.Max(0, (fixAlt - Elevation) / fixD); } }
        /// <summary>Raise a leg's altitude over sampled terrain between two points.</summary>
        double LegClear(double la0, double lo0, Wp b)
        {
            double top = double.MinValue;
            for (int k = 1; k <= 12; k++) { double f = k / 12.0; top = Math.Max(top, T(la0 + (b.Lat - la0) * f, lo0 + (b.Lon - lo0) * f)); }
            return Math.Max(b.Alt, top + Clearance);
        }
        /// <summary>Waypoint sequence for the entry: short final if (near) head-on, straight to the long fix if already out beyond it
        /// and inbound, else downwind (our side) -> base apex -> long final. Never crosses the runway.</summary>
        internal List<Wp> Route(double lat, double lon, double track, string kind, double radius, double altitude = double.NaN)
        {
            double d = NavigationMath.Distance(ThrLat, ThrLon, lat, lon, radius);
            double th = FlightPolicy.Wrap(NavigationMath.Bearing(ThrLat, ThrLon, lat, lon) - Course) * Math.PI / 180;
            double behind = -d * Math.Cos(th), cross = d * Math.Sin(th);
            bool inbound = !double.IsNaN(track) && Math.Abs(FlightPolicy.Wrap(track - Course)) < 60;
            var r = new List<Wp>();
            if (kind == "short") r.Add(Short);
            else if (behind > LongFix && inbound && Math.Abs(cross) < 3 * TurnRadius) r.Add(Long);
            else
            {
                bool right = cross >= 0;
                var cl = right ? CustomRight : CustomLeft;
                if (cl != null) { r.AddRange(cl); r.Add(Long); }
                else if (behind < LongFix - TurnRadius) { if (behind < 0) r.Add(right ? DownRightA : DownLeftA); r.Add(right ? DownRightB : DownLeftB); }
                if (cl == null) { r.Add(right ? ApexRight : ApexLeft); r.Add(Long); }
            }
            var outp = new List<Wp>(); double la0 = lat, lo0 = lon;
            foreach (var w in r)
            {
                // Fix altitudes are "at or below": never climb to them if we're already lower, but stay above the 3 deg path and terrain.
                double dist = NavigationMath.Distance(ThrLat, ThrLon, w.Lat, w.Lon, radius), floor = Elevation + dist * Math.Tan(3 * Math.PI / 180);
                var c = new Wp { Name = w.Name, Lat = w.Lat, Lon = w.Lon, Alt = double.IsNaN(altitude) ? w.Alt : Math.Min(w.Alt, Math.Max(altitude, floor)) };
                c.Alt = LegClear(la0, lo0, c); outp.Add(c); la0 = w.Lat; lo0 = w.Lon;
            }
            if (outp.Count > 0) RouteFixAlt = outp[outp.Count - 1].Alt;
            return outp;
        }
        internal string Describe(List<Wp> route)
        {
            var parts = new List<string>(); foreach (var w in route) parts.Add(w.Name + " " + w.Alt.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m");
            return "r=" + TurnRadius.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " m: " + string.Join(" > ", parts.ToArray());
        }
        /// <summary>Launch pads: a simple vertical-approach point above the pad.</summary>
        internal static Wp Pad(double lat, double lon, double elevation) { return new Wp { Name = "pad vertical", Lat = lat, Lon = lon, Alt = elevation + 300 }; }
    }

    internal sealed class RunwayMission
    {
        /// <summary>Cross-track error predicted tau s ahead (+ = right of course): damps the centerline intercept.</summary>
        internal static double PredictCross(double cross, double speed, double track, double course, double tau)
        { return double.IsNaN(track) ? cross : cross + tau * speed * Math.Sin(FlightPolicy.Wrap(track - course) * Math.PI / 180); }
        internal double Lat, Lon, EndLat, EndLon, Elevation;
        internal string Phase = "entry";
        internal double DesiredHeading, DesiredAltitude, DesiredSpeed, DesiredVs;
        internal bool Gear, Brakes;
        internal double Distance;
        internal const double IfDistance = 12000, FafDistance = 5000, AlignCross = 150, AlignHeading = 8, GateDistance = 1000, GateCross = 40, GateHeading = 6;
        internal double Cross, Along;
        internal string Why = "";
        internal Func<double, double, double> Terrain; internal double BankDeg = 20, LongAgl = -1, ShortAgl = -1;
        internal ApproachOverride Override; internal string Key = "";
        internal ApproachChart Chart; internal List<ApproachChart.Wp> Route; internal int RouteIndex; internal string RouteLog = "";
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
                if (Route == null)
                {
                    Chart = ApproachChart.Build(Lat, Lon, course, Elevation, speed, BankDeg, radius, Terrain, LongAgl, ShortAgl); if (Override != null) Chart.Apply(Override);
                    Route = Chart.Route(lat, lon, track, Kind, radius, altitude); RouteIndex = 0; Chart.RouteFixD = Kind == "short" ? ApproachChart.ShortFix : ApproachChart.LongFix;
                    Why = Why.Length > 0 ? Why : ""; RouteLog = Chart.Describe(Route);
                }
                DesiredSpeed = Math.Max(1.5 * stall, Math.Min(150, speed));
                if (RouteIndex < Route.Count)
                {
                    var wp = Route[RouteIndex];
                    double dw = NavigationMath.Distance(lat, lon, wp.Lat, wp.Lon, radius);
                    DesiredHeading = NavigationMath.Bearing(lat, lon, wp.Lat, wp.Lon); DesiredAltitude = wp.Alt;
                    DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) * .05, -10, 15);
                    bool behindUs = !double.IsNaN(track) && Math.Abs(FlightPolicy.Wrap(DesiredHeading - track)) > 100;   // overflown: never orbit a fix
                    if (dw < Math.Max(700, .3 * Chart.TurnRadius) || (behindUs && dw < 2.2 * Chart.TurnRadius)) RouteIndex++;
                }
                if (RouteIndex >= Route.Count) Phase = "intercept";
            }
            if (Phase == "intercept")
            {
                double lead = Math.Max(1500, .6 * Math.Min(speed, 150) * Math.Min(speed, 150) / (9.81 * Math.Tan(20 * Math.PI / 180)));   // ~1 turn radius lead: converges without overshoot
                DesiredHeading = course - FlightPolicy.Clamp(Math.Atan2(PredictCross(cross, speed, track, course, 10), Math.Max(2000, 12 * speed)) * 180 / Math.PI, -30, 30);   // damped intercept (was overshooting S->N)
                DesiredAltitude = Chart != null ? Math.Max(double.IsNaN(Chart.RouteFixAlt) ? Chart.LongAlt : Chart.RouteFixAlt, Chart.GlideAlt(-along)) : Elevation + 800;   // hold the fix altitude until aligned DesiredSpeed = Math.Max(1.4 * stall, Math.Min(140, speed));
                DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) * .05, -12, 10);
                if (Math.Abs(cross) < AlignCross && trackErr < AlignHeading) Phase = "final";
                else if (-along < (Kind == "short" ? 2000 : FafDistance) && (Math.Abs(cross) > 1000 || trackErr > 30 || -along < GateDistance + 500)) { Phase = "entry"; Kind = "long"; FixDistance = IfDistance; Route = null; Why = "not aligned before the FAF; repositioning"; }   // never descend unaligned
            }
            if (Phase == "final" || Phase == "flare")
            {
                DesiredHeading = course - FlightPolicy.Clamp(Math.Atan2(PredictCross(cross, speed, track, course, 6), Math.Max(1200, 8 * speed)) * 180 / Math.PI, -15, 15);   // cross-track tracking all the way down
                DesiredAltitude = Chart != null ? Chart.GlideAlt(-along + (Override != null ? Override.TouchdownM : 350)) : Elevation + Math.Max(3, (350 - along) * Math.Tan(3 * Math.PI / 180));
                DesiredSpeed = (agl < 30 ? 1.15 : 1.3) * stall;
                double ff = Chart != null && agl > 30 ? -speed * Chart.Slope : 0;   // path feed-forward (steep AGL fixes)
                DesiredVs = FlightPolicy.Clamp(ff + (DesiredAltitude - altitude) * .2, Distance > 1500 ? -25 : Distance > 400 ? -15 : -4.5, 3);   // 2500 m AGL long fix = ~12 deg path: steep descent allowed far out only
                Gear = (Distance < 3000 && Math.Abs(cross) < AlignCross) || agl < 80;   // only on an aligned short final
                double fs = Override != null ? Override.FlareStartM : 15, fv = Override != null ? Override.FlareSinkMs : 1;
                if (agl < fs) { Phase = "flare"; DesiredVs = agl > fs / 3 ? -Math.Max(fv, 2) : -fv; }
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
