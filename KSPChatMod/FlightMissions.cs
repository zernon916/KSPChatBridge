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

    /// <summary>Per-craft approach profile (Luke: arcs depend on the plane). Join speed 1.5 x the stall speed at current mass,
    /// usable bank = chart bank capped by the speed bank rule, radius = v^2/(g tan bank).</summary>
    internal static class ApproachProfile
    {
        internal const double JoinFactor = 1.5, MinSpeed = 80, MaxSpeed = 200, Replan = .08, UnmeasuredStallFloor = 45;
        /// <summary>Stall at current mass from a reference stall (Vs ~ sqrt(mass)).</summary>
        internal static double StallAt(double stallRef, double massRef, double massNow) { return massRef > 0 && massNow > 0 ? stallRef * Math.Sqrt(massNow / massRef) : stallRef; }
        /// <summary>Wing-loading estimate when the craft has no measured stall: Vs = sqrt(2 m g / (rho0 * K * sum(lift coeff))), K calibrated to stock (6 t, 6 lift -> ~45 m/s).</summary>
        internal static double EstimateStall(double massKg, double liftCoeffSum) { return liftCoeffSum <= 0 ? UnmeasuredStallFloor : FlightPolicy.Clamp(Math.Sqrt(2 * massKg * 9.81 / (1.225 * 7.9 * liftCoeffSum)), UnmeasuredStallFloor, 200); }   // Luke 13:33: unfloored estimate gave 31 m/s -> 46 m/s joins, 586 m arcs flown at 70-260 m/s
        internal static double Speed(double stall) { return FlightPolicy.Clamp(JoinFactor * stall, MinSpeed, MaxSpeed); }
        /// <summary>Stall estimate sanity floor for the approach schedule (live 15:4x: ~33 m/s gave a 43 m/s final from 20 km).</summary>
        internal static double SaneStall(double stall) { return FlightPolicy.Clamp(double.IsNaN(stall) ? UnmeasuredStallFloor : stall, 40, 120); }
        internal static double AppSpeed(double stall) { return 1.35 * SaneStall(stall); }
        /// <summary>Final speed schedule: 12->4 km ~2.3x stall (cap 150), linear decel to 1.35x stall by 2 km, then held to the flare.</summary>
        internal static double FinalSchedule(double distToThreshold, double stall)
        {
            double hi = Math.Max(AppSpeed(stall), Math.Min(150, 2.3 * SaneStall(stall))), app = AppSpeed(stall);
            if (distToThreshold >= 4000) return hi; if (distToThreshold <= 2000) return app;
            return app + (hi - app) * (distToThreshold - 2000) / 2000;
        }
        internal static double Bank(double chartBank, double speed) { return chartBank > 20 ? chartBank : Math.Min(Math.Max(5, chartBank), FlightPolicy.BankLimit(speed)); }   // explicit "bank 60" wins
        internal static double Radius(double speed, double bank) { return speed * speed / (9.81 * Math.Tan(bank * Math.PI / 180)); }
        /// <summary>Luke: G-based turn rating (joins ~2 g, final corrections ~1.5 g; settable), never above the 3 g cap and
        /// kept 20% inside the stall load factor (v/Vs)^2. Bank = acos(1/n), radius = v^2/(g sqrt(n^2-1)).</summary>
        /// Fighter/NASA style (Luke): allowed g = min(rating, crew tolerance, structure, stall margin), with stress back-off.
        internal static double JoinG = 4.0, FinalG = 1.5, CrewG = 6, StructG = 9, Backoff = 1;
        internal const double GCap = 12, StructCap = 9;   // absolute sanity ceiling; real limits come from crew/structure/stall
        internal static string Limit = "rating";
        internal static double LoadFactor(double wantG, double speed, double stall)
        {
            double stallN = stall > 0 ? .8 * (speed / stall) * (speed / stall) : GCap, rating = wantG * Backoff, n = rating; Limit = Backoff < .999 ? "stress back-off" : "rating";
            if (CrewG < n) { n = CrewG; Limit = "crew g"; }
            if (StructG < n) { n = StructG; Limit = "structure"; }
            if (stallN < n) { n = stallN; Limit = "stall"; }
            return FlightPolicy.Clamp(n, 1.05, GCap);
        }
        /// <summary>Structural g limit from the weakest part's g tolerance (80% margin); 0/garbage tolerances ignored.</summary>
        internal static double StructuralG(IEnumerable<double> partTolerances)
        { double m = double.MaxValue; foreach (var t in partTolerances) if (t > 1 && t < 1e4) m = Math.Min(m, .8 * t); return m == double.MaxValue ? StructCap : FlightPolicy.Clamp(m, 1.5, StructCap); }   // part gTolerance is not a measured airframe limit: cap ~9 g
        /// <summary>Stress back-off: above 80% of the structural limit shrink the rating quickly, recover slowly below 60%.</summary>
        internal static double StressStep(double backoff, double g, double structG, double dt)
        { double st = structG > 0 ? g / structG : 0; return st > .8 ? Math.Max(.4, backoff * (1 - .5 * dt)) : st < .6 ? Math.Min(1, backoff + .05 * dt) : backoff; }
        internal static double BankForG(double n) { return Math.Acos(1 / Math.Max(1.0001, n)) * 180 / Math.PI; }
        internal static double RadiusForG(double speed, double n) { return speed * speed / (9.81 * Math.Sqrt(Math.Max(1e-6, n * n - 1))); }
        /// <summary>Back-pressure: extra pitch (deg) to hold altitude in a bank, scaled by the load factor 1/cos(bank).</summary>
        internal static double TurnPitch(double rollDeg) { double a = Math.Abs(rollDeg); return a >= 80 ? 0 : FlightPolicy.Clamp(4 * (1 / Math.Cos(a * Math.PI / 180) - 1), 0, 8); }
        internal static bool NeedsReplan(double planned, double now) { return planned > 0 && Math.Abs(now - planned) / planned > Replan; }
    }

    /// <summary>Turn-onset smoothing (Luke: choppy g at turn entry). Bank command ramps with a roll-rate limit AND a g-onset
    /// limit (~1.25 g/s on n = 1/cos(bank)); reversals ramp through wings-level. Pitch commands go through a critically
    /// damped 2nd-order filter (S-curve) so the PID never sees a step; elevator gets feed-forward from the commanded g.</summary>
    internal static class TurnOnset
    {
        internal const double GRate = 1.25, RollRate = 30;
        static double N(double bankDeg) { double c = Math.Cos(Math.Min(85, Math.Abs(bankDeg)) * Math.PI / 180); return 1 / c; }
        internal static double Bank(double prev, double target, double dt, double gRate = GRate, double rollRate = RollRate)
        {
            if (dt <= 0) return prev;
            double tgt = prev * target < 0 && Math.Abs(prev) > .5 ? 0 : target;   // reversal: through wings-level first
            double b = prev + FlightPolicy.Clamp(tgt - prev, -rollRate * dt, rollRate * dt);
            double n0 = N(prev), n1 = N(b), dn = gRate * dt;
            if (Math.Abs(n1 - n0) > dn) { double n = n0 + Math.Sign(n1 - n0) * dn; double mag = Math.Acos(Math.Min(1, 1 / Math.Max(1, n))) * 180 / Math.PI; b = (b == 0 ? Math.Sign(tgt) : Math.Sign(b)) * mag; }
            return b;
        }
        /// <summary>Elevator feed-forward for the commanded load factor (0..0.3).</summary>
        internal static double ElevatorFF(double bankCmdDeg) { return Math.Abs(bankCmdDeg) >= 80 ? 0 : FlightPolicy.Clamp(.04 * (N(bankCmdDeg) - 1), 0, .3); }
    }

    /// <summary>Pitch-command shaping (crash 047208f: 9.5 g at the takeoff->climb handoff). S-curve + pitch-rate limit; high g only
    /// in commanded turns/joins (|bank| > 15), otherwise ClimbG; turn back-pressure only when banked; Reset() from the current
    /// state at every mode change so no stale filter/rate state produces a jump.</summary>
    internal sealed class PitchShaper
    {
        internal const double ClimbG = 1.8, TurnBankMin = 15, FFBankMin = 5;
        readonly SCurve s = new SCurve(); double last = double.NaN;
        internal void Reset(double pitch) { s.Reset(); s.Step(pitch, 0); last = pitch; }
        internal static double Limit(double gLim, double bankCmd) { return Math.Abs(bankCmd) > TurnBankMin ? gLim : Math.Min(gLim, ClimbG); }
        internal static double BackPressure(double bankCmd) { return Math.Abs(bankCmd) > FFBankMin ? ApproachProfile.TurnPitch(bankCmd) : 0; }
        internal static double ElevatorFF(double bankCmd) { return Math.Abs(bankCmd) > FFBankMin ? TurnOnset.ElevatorFF(bankCmd) : 0; }
        internal double Step(double desired, double pitch, double speed, double gLim, double bankCmd, double dt)
        {
            if (double.IsNaN(last)) Reset(pitch);
            double d = s.Step(desired + BackPressure(bankCmd), dt);
            double pr = PilotPolicy.MaxPitchRate(speed, Limit(gLim, bankCmd)) * dt;
            last = FlightPolicy.Clamp(d, last - pr, last + pr); return last;
        }
        /// <summary>Elevator slew: normal .35/s; unloading when g is near/over the limit is fast (3/s) so the g limiter can act.</summary>
        internal static double ElevatorRate(double g, double gLim, double cmd, double elevator) { return g > .9 * gLim && cmd < elevator ? 3 : .35; }
    }

    /// <summary>Critically damped second-order low-pass (S-curve response, no overshoot).</summary>
    internal sealed class SCurve
    {
        double x = double.NaN, v; internal double Omega = 2.5;
        internal void Reset() { x = double.NaN; v = 0; }
        internal double Step(double target, double dt)
        {
            if (double.IsNaN(x) || dt <= 0) { if (double.IsNaN(x)) { x = target; v = 0; } return x; }
            int n = Math.Max(1, (int)Math.Ceiling(dt / .02)); double h = dt / n;
            for (int i = 0; i < n; i++) { double a = Omega * Omega * (target - x) - 2 * Omega * v; v += a * h; x += v * h; }
            return x;
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
        /// <summary>Mod-side waypoint maths (Luke's fixes untouched): every turn after the first fix is replaced by a fly-by arc of
        /// radius r = v^2/(g tan bank) tangent to both legs (entry/exit tangent points + arc points, the fix keeps its name at the arc
        /// midpoint). If two neighbouring turns need more leg than exists, both leads shrink proportionally and the turn is logged tight.</summary>
        internal static List<Wp> Smooth(List<Wp> route, double course, double thrLat, double thrLon, double speed, double bankDeg, double radius, out string log)
        {
            log = ""; if (route == null || route.Count < 2) return route;
            double r = speed * speed / (9.81 * Math.Tan(Math.Max(5, bankDeg) * Math.PI / 180));
            int n = route.Count; var brgIn = new double[n]; var lenIn = new double[n + 1]; var lead = new double[n]; var dth = new double[n];
            for (int i = 1; i < n; i++) { brgIn[i] = NavigationMath.Bearing(route[i - 1].Lat, route[i - 1].Lon, route[i].Lat, route[i].Lon); lenIn[i] = NavigationMath.Distance(route[i - 1].Lat, route[i - 1].Lon, route[i].Lat, route[i].Lon, radius); }
            lenIn[n] = NavigationMath.Distance(route[n - 1].Lat, route[n - 1].Lon, thrLat, thrLon, radius);
            for (int i = 1; i < n; i++)
            {
                double outB = i + 1 < n ? brgIn[i + 1] : course;
                dth[i] = FlightPolicy.Wrap(outB - brgIn[i]);
                lead[i] = Math.Abs(dth[i]) < 2 ? 0 : r * Math.Tan(Math.Min(170, Math.Abs(dth[i])) / 2 * Math.PI / 180);
            }
            var tight = new List<string>();
            for (int i = 1; i < n; i++)
            {
                double before = (i == 1 ? lenIn[i] : lenIn[i] - lead[i - 1]), after = lenIn[i + 1] - (i + 1 < n ? lead[i + 1] : 0);
                double room = Math.Max(0, Math.Min(before, after) - 100);
                if (lead[i] > room) { tight.Add(route[i].Name + " (" + Math.Round(Math.Abs(dth[i])) + " deg, needs " + Math.Round(lead[i]) + " m, has " + Math.Round(room) + " m)"); lead[i] = room; }
            }
            var outp = new List<Wp> { route[0] }; int arcs = 0;
            for (int i = 1; i < n; i++)
            {
                var f = route[i];
                if (lead[i] < 50) { outp.Add(f); continue; }
                double outB = i + 1 < n ? brgIn[i + 1] : course, ea, eo, xa, xo;
                NavigationMath.Offset(f.Lat, f.Lon, brgIn[i] + 180, lead[i], radius, out ea, out eo);
                NavigationMath.Offset(f.Lat, f.Lon, outB, lead[i], radius, out xa, out xo);
                double rr = lead[i] / Math.Tan(Math.Abs(dth[i]) / 2 * Math.PI / 180), side = dth[i] > 0 ? 90 : -90, ca, co;
                NavigationMath.Offset(ea, eo, brgIn[i] + side, rr, radius, out ca, out co);   // arc centre
                int k = Math.Max(2, (int)Math.Ceiling(Math.Abs(dth[i]) / 15));
                outp.Add(new Wp { Name = f.Name + " lead", Lat = ea, Lon = eo, Alt = f.Alt });
                for (int j = 1; j < k; j++)
                {
                    double a = brgIn[i] - side + dth[i] * j / k, pa, po; NavigationMath.Offset(ca, co, a, rr, radius, out pa, out po);
                    outp.Add(new Wp { Name = j == k / 2 ? f.Name : f.Name + " arc", Lat = pa, Lon = po, Alt = f.Alt });
                }
                if (k / 2 == 0 || k == 1) outp.Add(new Wp { Name = f.Name, Lat = f.Lat, Lon = f.Lon, Alt = f.Alt });
                outp.Add(new Wp { Name = f.Name + " exit", Lat = xa, Lon = xo, Alt = f.Alt }); arcs++;
            }
            log = "smoothed " + arcs + " turn(s) at " + Math.Round(speed) + " m/s, r=" + Math.Round(r) + " m" + (tight.Count > 0 ? "; TIGHT (shrunk): " + string.Join(", ", tight.ToArray()) : "");
            return outp;
        }

        /// <summary>Shortest Dubins CSC path (LSL/RSR/LSR/RSL) from (x0,y0) heading h0 to (x1,y1) arriving on heading h1
        /// (compass degrees, metres east/north), turn radius r. Returns metres.</summary>
        internal static double Dubins(double x0, double y0, double h0, double x1, double y1, double h1, double r)
        {
            Func<double, double> m = z => { z %= 2 * Math.PI; return z < 0 ? z + 2 * Math.PI : z; };
            double t0 = (90 - h0) * Math.PI / 180, t1 = (90 - h1) * Math.PI / 180, dx = x1 - x0, dy = y1 - y0, D = Math.Sqrt(dx * dx + dy * dy), d = D / r;
            double phi = Math.Atan2(dy, dx), a = m(t0 - phi), b = m(t1 - phi), sa = Math.Sin(a), sb = Math.Sin(b), ca = Math.Cos(a), cb = Math.Cos(b), best = double.MaxValue;
            double p2 = 2 + d * d - 2 * Math.Cos(a - b) + 2 * d * (sa - sb);   // LSL
            if (p2 >= 0) { double tmp = Math.Atan2(cb - ca, d + sa - sb); best = Math.Min(best, m(-a + tmp) + Math.Sqrt(p2) + m(b - tmp)); }
            p2 = 2 + d * d - 2 * Math.Cos(a - b) + 2 * d * (sb - sa);          // RSR
            if (p2 >= 0) { double tmp = Math.Atan2(ca - cb, d - sa + sb); best = Math.Min(best, m(a - tmp) + Math.Sqrt(p2) + m(-b + tmp)); }
            p2 = -2 + d * d + 2 * Math.Cos(a - b) + 2 * d * (sa + sb);         // LSR
            if (p2 >= 0) { double p = Math.Sqrt(p2), tmp = Math.Atan2(-ca - cb, d + sa + sb) - Math.Atan2(-2, p); best = Math.Min(best, m(-a + tmp) + p + m(-m(b) + tmp)); }
            p2 = -2 + d * d + 2 * Math.Cos(a - b) - 2 * d * (sa + sb);         // RSL
            if (p2 >= 0) { double p = Math.Sqrt(p2), tmp = Math.Atan2(ca + cb, d - sa - sb) - Math.Atan2(2, p); best = Math.Min(best, m(a - tmp) + p + m(b - tmp)); }
            return best * r;
        }

        /// <summary>One side's full join sequence ending at the long-final fix (custom editor list or computed downwind/base).</summary>
        internal List<Wp> Side(bool right)
        {
            var seq = new List<Wp>(right ? (CustomRight ?? new List<Wp> { DownRightA, DownRightB, ApexRight }) : (CustomLeft ?? new List<Wp> { DownLeftA, DownLeftB, ApexLeft }));
            seq.Add(Long); return seq;
        }

        /// <summary>Luke: join the chart at the nearest fix or leg point (1/3, 2/3) ahead of the FAF this plane can safely align with:
        /// Dubins turn-straight-turn reach at its radius, arriving on the leg track; descent/climb within 10/15 m/s at its speed;
        /// terrain 150 m below the path. Picks the shortest total path to touchdown; null = none (fall back to the long final).</summary>
        internal const double JoinStep = 300, MaxIntercept = 45, MaxSink = 25; internal Wp JoinPoint;
        internal List<Wp> BestJoin(double lat, double lon, double heading, double altitude, double speed, double turnR, double radius, out string why)
        {
            why = ""; if (double.IsNaN(heading)) { why = "no track yet"; return null; }
            double k = Math.PI / 180 * radius, cl = Math.Cos(ThrLat * Math.PI / 180);
            Func<double, double, double[]> xy = (la, lo) => new[] { (lo - ThrLon) * k * cl, (la - ThrLat) * k };
            var me = xy(lat, lon); List<Wp> best = null; double bestCost = double.MaxValue; string bestWhy = ""; int nReach = 0, nAlt = 0, nTerr = 0, n = 0;
            // Luke: the route LINE is continuous mini-waypoints (every ~300 m). Join at any point ahead (toward the FAF, chart order only)
            // that is intercepted at <= 45 deg; then track the line by cross-track. Prefer the leg we are already flying along (< 30 deg).
            double direct = NavigationMath.Distance(lat, lon, ThrLat, ThrLon, radius);
            foreach (bool right in new[] { false, true })
            {
                var seq = Side(right);
                for (int i = 0; i < seq.Count; i++)
                {
                    Wp a = i > 0 ? seq[i - 1] : null, f = seq[i];
                    double legLen = a == null ? 0 : NavigationMath.Distance(a.Lat, a.Lon, f.Lat, f.Lon, radius);
                    int steps = a == null ? 1 : Math.Max(1, (int)Math.Ceiling(legLen / JoinStep));
                    double legTrack = a != null ? NavigationMath.Bearing(a.Lat, a.Lon, f.Lat, f.Lon) : i + 1 < seq.Count ? NavigationMath.Bearing(f.Lat, f.Lon, seq[i + 1].Lat, seq[i + 1].Lon) : Course;
                    if (i == seq.Count - 1 && a == null) legTrack = Course;
                    for (int kk = a == null ? steps : 1; kk <= steps; kk++)
                    {
                        n++;
                        double fr = (double)kk / steps;
                        var j = a == null || kk == steps ? f : new Wp { Name = "line " + f.Name.Split('(')[0].Trim() + " -" + ((1 - fr) * legLen / 1000).ToString("0.0") + " km", Lat = a.Lat + (f.Lat - a.Lat) * fr, Lon = a.Lon + (f.Lon - a.Lon) * fr, Alt = a.Alt + (f.Alt - a.Alt) * fr };
                        double toJ = NavigationMath.Distance(lat, lon, j.Lat, j.Lon, radius), brg = NavigationMath.Bearing(lat, lon, j.Lat, j.Lon);
                        double icpt = Math.Abs(FlightPolicy.Wrap(legTrack - brg)), hdgErr = Math.Abs(FlightPolicy.Wrap(heading - legTrack));
                        bool onLeg = hdgErr < 30 && toJ < 6000 && icpt < 60;   // already flying this leg: the point ahead on it
                        if (icpt > MaxIntercept && !onLeg && toJ > turnR) { nReach++; continue; }   // never a point behind / against the line direction
                        var jp = xy(j.Lat, j.Lon);
                        double reach = Dubins(me[0], me[1], heading, jp[0], jp[1], legTrack, turnR);
                        if (double.IsInfinity(reach) || reach >= double.MaxValue / 2) { nReach++; continue; }
                        double dz = j.Alt - altitude, sink = reach * MaxSink / Math.Max(30, speed);
                        if (dz > reach * 15 / Math.Max(30, speed)) { nAlt++; continue; }   // can't climb that much
                        double lose = -dz > sink ? (-dz - sink) * Math.Max(30, speed) / MaxSink : 0;   // too high: S-turns/slowing, flown as extra distance
                        if (lose > 30000) { nAlt++; continue; }
                        bool clear = true;
                        for (int s = 1; s <= 8 && clear; s++) { double la = lat + (j.Lat - lat) * s / 8, lo = lon + (j.Lon - lon) * s / 8, g = T(la, lo), path = altitude + (j.Alt - altitude) * s / 8; if (!double.IsNaN(g) && g + 150 > path) clear = false; }   // clearance vs the PATH flown
                        if (!clear) { nTerr++; continue; }
                        double rest = 0; Wp prev = j;
                        var route = new List<Wp> { j };
                        for (int q = kk == steps || a == null ? i + 1 : i; q < seq.Count; q++) { rest += NavigationMath.Distance(prev.Lat, prev.Lon, seq[q].Lat, seq[q].Lon, radius); route.Add(seq[q]); prev = seq[q]; }
                        rest += NavigationMath.Distance(prev.Lat, prev.Lon, ThrLat, ThrLon, radius);
                        double total = reach + lose + rest, cost = total + Math.Max(0, total - (1.5 * direct + 15000)) * 2;   // penalize a path much longer than direct
                        if (onLeg) cost *= .6;   // prefer the leg we are already heading along
                        if (cost < bestCost) { bestCost = cost; best = route; JoinPoint = j; bestWhy = "joined " + (right ? "right" : "left") + " at " + j.Name + (onLeg ? " (on the leg ahead)" : "") + ": intercept " + Math.Round(icpt) + " deg, reach " + (reach / 1000).ToString("0.0") + " km (r " + Math.Round(turnR) + " m)" + (lose > 0 ? ", +" + (lose / 1000).ToString("0.0") + " km to lose height" : "") + ", total " + (total / 1000).ToString("0.0") + " km to touchdown (direct " + (direct / 1000).ToString("0.0") + ")"; }
                    }
                }
            }
            why = (best != null ? bestWhy : "no safe join point") + " [" + n + " candidates; rejected " + nAlt + " altitude, " + nTerr + " terrain, " + nReach + " unreachable]";
            return best;
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
        internal const double Kp = 1, Ki = .001, Tau = 2, Lv = 3, Lmin = 50, IntBand = 3, CrabTau = 5;
        /// <summary>Luke: centerline within ~1 m, no swinging. Desired TRACK from a cross-track PID (P on cross, D via the track
        /// angle Tau s ahead, I per metre flown inside +-3 m), then crab-compensated: heading = track - (measured track - heading),
        /// low-passed over CrabTau s, so wind/sideslip/trim offsets are cancelled instead of integrated (no PI limit cycle).</summary>
        internal static double CenterlineHeading(double cross, double track, double course, double speed, ref double crossI, double ds, double heading, ref double crab, bool tight = false)
        {
            if (!double.IsNaN(track) && !double.IsNaN(heading)) crab += FlightPolicy.Clamp(ds / (Math.Max(1, speed) * CrabTau), 0, 1) * (FlightPolicy.Wrap(track - heading) - crab);
            crab = FlightPolicy.Clamp(crab, -20, 20);
            if (Math.Abs(cross) < IntBand) crossI = FlightPolicy.Clamp(crossI + cross * ds, -5000, 5000);
            double u = Kp * PredictCross(cross, speed, track, course, Tau) + (tight ? 1.5 : 1) * Ki * crossI;   // short final: harder integral toward +-1 m
            return course - FlightPolicy.Clamp(Math.Atan2(u, Math.Max(Lmin, Lv * speed)) * 180 / Math.PI, tight ? -8 : -15, tight ? 8 : 15) - crab;
        }
        internal static double PredictCross(double cross, double speed, double track, double course, double tau)
        { return double.IsNaN(track) ? cross : cross + tau * speed * Math.Sin(FlightPolicy.Wrap(track - course) * Math.PI / 180); }
        internal double Lat, Lon, EndLat, EndLon, Elevation;
        internal string Phase = "entry";
        internal double DesiredHeading, DesiredAltitude, DesiredSpeed, DesiredVs;
        internal bool Gear, Brakes; int replanHold;
        internal double Distance;
        internal const double FastAtShort = 15, FastAtGate = 10;
        internal const double IfDistance = 12000, FafDistance = 5000, AlignCross = 150, AlignHeading = 8, GateDistance = 1000, GateCross = 40, GateHeading = 6;
        internal double Cross, Along;
        internal string Why = "";
        internal Func<double, double, double> Terrain; internal double BankDeg = 20, LongAgl = -1, ShortAgl = -1;
        internal ApproachOverride Override; internal string Key = "";
        internal ApproachChart Chart; internal List<ApproachChart.Wp> Route; internal int RouteIndex; internal string RouteLog = "";
        internal string SmoothLog = "", JoinLog = "";
        internal double Crab, RouteStartLat, RouteStartLon, LegXte, CrossI, lastAlong = double.NaN;
        /// <summary>After a go-around: fly a fresh long-final pattern from here (old route is spent), reset the centerline integral.</summary>
        /// <summary>Hot-swapped chart: rebuild while joining (route from here); established on final/flare/rollout keeps flying the centerline.</summary>
        internal void ReloadChart()
        {
            if (Phase == "entry") Route = null;   // rebuilt next step from the current position (BestJoin picks the nearest still-valid fix)
            else if (Chart != null && Override != null) Chart.Apply(Override);
        }
        internal Ils.Reading Ils; internal bool LocCoupled, GsCoupled; internal const double LocRange = 18000;
        internal double TouchdownM { get { return Override != null ? Override.TouchdownM : 350; } }
        internal bool Coupled { get { return LocCoupled && (Phase == "final" || Phase == "flare"); } }
        internal void GoAroundReset() { LocCoupled = GsCoupled = false; Phase = "entry"; Kind = "long"; FixDistance = IfDistance; Route = null; CrossI = 0; Crab = 0; lastAlong = double.NaN; }
        internal double PlanSpeed, PlanBank, PlanRadius, PlanG, TurnBank, LastStall = 45; internal List<ApproachChart.Wp> RawRoute;

        /// <summary>Fly-by leg steering: track the line prev->fix (cross-track, max 30 deg cut) and start the turn onto the next leg
        /// r*tan(dHdg/2) before the fix, r from the actual speed and bank. Returns the desired heading; advance = time to switch legs.</summary>
        /// <summary>(Re)build the flown route from the raw chart fixes for a join speed; keeps the current target when replanning.</summary>
        internal void Plan(double course, double radius, double speed, int keepIndex, double hereLat = double.NaN, double hereLon = double.NaN)
        {
            PlanSpeed = speed; PlanG = ApproachProfile.LoadFactor(ApproachProfile.JoinG, speed, LastStall);
            PlanBank = BankDeg > 20 ? BankDeg : ApproachProfile.BankForG(PlanG);   // explicit "bank 60" still wins
            PlanRadius = ApproachProfile.Radius(speed, PlanBank);
            ApproachChart.Wp target = keepIndex >= 0 && Route != null && keepIndex < Route.Count ? Route[keepIndex] : null;
            // smooth from the plane's own position so the first turn (onto the run-in) gets a lead/arc too, then drop that start point
            bool here = !double.IsNaN(hereLat) && keepIndex < 0;
            var src = here ? new List<ApproachChart.Wp>(RawRoute) : RawRoute; if (here) src.Insert(0, new ApproachChart.Wp { Name = "here", Lat = hereLat, Lon = hereLon, Alt = RawRoute[0].Alt });
            string sm; Route = ApproachChart.Smooth(src, course, Lat, Lon, speed, PlanBank, radius, out sm);
            if (here && Route.Count > 1) { RouteStartLat = hereLat; RouteStartLon = hereLon; Route.RemoveAt(0); }
            SmoothLog = (keepIndex >= 0 ? "replanned (mass/speed change): " : "") + sm;
            if (target != null)
            {
                int best = 0; double bd = double.MaxValue;
                for (int i = 0; i < Route.Count; i++) { double d = NavigationMath.Distance(target.Lat, target.Lon, Route[i].Lat, Route[i].Lon, radius); if (d < bd) { bd = d; best = i; } }
                RouteIndex = best;
            }
        }

        internal static double LegSteer(double lat, double lon, double pLat, double pLon, double fLat, double fLon, double nextBrg, double speed, double bankDeg, double radius, out bool advance, out double xte)
        {
            double leg = NavigationMath.Bearing(pLat, pLon, fLat, fLon), len = NavigationMath.Distance(pLat, pLon, fLat, fLon, radius);
            double d = NavigationMath.Distance(pLat, pLon, lat, lon, radius), th = FlightPolicy.Wrap(NavigationMath.Bearing(pLat, pLon, lat, lon) - leg) * Math.PI / 180;
            xte = d * Math.Sin(th); double remaining = len - d * Math.Cos(th);
            double r = speed * speed / (9.81 * Math.Tan(Math.Max(5, bankDeg) * Math.PI / 180));
            double dh = double.IsNaN(nextBrg) ? 0 : Math.Abs(FlightPolicy.Wrap(nextBrg - leg));
            double lead = Math.Min(len, r * Math.Tan(Math.Min(150, dh) / 2 * Math.PI / 180));
            advance = remaining <= Math.Max(250, lead);
            return leg - FlightPolicy.Clamp(Math.Atan2(xte, Math.Max(1500, 10 * speed)) * 180 / Math.PI, -30, 30);
        }


        internal bool WantShort; internal string Kind = ""; internal double FixDistance = IfDistance;
        /// <summary>Round 3 approach: fly to an intercept fix 20 km out on the extended centerline, turn onto the centerline holding
        /// altitude, only descend on the glideslope once aligned (cross &lt; 150 m, track within 8 deg) before the 6 km FAF,
        /// and go around if not aligned within 40 m / 6 deg by 1 km out. track = ground track (NaN = unknown).</summary>
        internal void Step(double lat, double lon, double altitude, double agl, double speed, bool grounded, double radius, double stall, double track = double.NaN, double heading = double.NaN)
        {
            double course = NavigationMath.Bearing(Lat, Lon, EndLat, EndLon);
            double d = NavigationMath.Distance(Lat, Lon, lat, lon, radius);
            double theta = FlightPolicy.Wrap(NavigationMath.Bearing(Lat, Lon, lat, lon) - course) * Math.PI / 180;
            double along = d * Math.Cos(theta), cross = d * Math.Sin(theta);
            Cross = cross; Along = along;
            Distance = Math.Max(0, -along);
            double trackErr = double.IsNaN(track) ? 0 : Math.Abs(FlightPolicy.Wrap(track - course));
            LastStall = stall;
            // live bank limit for the autopilot = this phase's G rating at the speed we have now (joins 2 g, final 1.5 g)
            TurnBank = BankDeg > 20 ? BankDeg : ApproachProfile.BankForG(ApproachProfile.LoadFactor(Phase == "entry" ? ApproachProfile.JoinG : ApproachProfile.FinalG, speed, stall));
            if (Phase == "entry")
            {
                if (Kind.Length == 0) { Kind = PilotPolicy.ApproachKind(track, course, -along, WantShort); FixDistance = Kind == "short" ? PilotPolicy.ShortFix : IfDistance; }
                if (Route == null)
                {
                    Chart = ApproachChart.Build(Lat, Lon, course, Elevation, speed, BankDeg, radius, Terrain, LongAgl, ShortAgl); if (Override != null) Chart.Apply(Override);
                    Route = Chart.Route(lat, lon, track, Kind, radius, altitude); RouteIndex = 0; RouteStartLat = lat; RouteStartLon = lon; Chart.RouteFixD = Kind == "short" ? ApproachChart.ShortFix : ApproachChart.LongFix;
                    if (Kind != "short")
                    {   // join at the nearest safely-alignable fix/leg point for THIS plane; fall back to the computed long-final entry
                        double jv = Math.Max(ApproachProfile.Speed(stall), Math.Min(speed, ApproachProfile.MaxSpeed)), jr = BankDeg > 20 ? ApproachProfile.Radius(jv, BankDeg) : ApproachProfile.RadiusForG(jv, ApproachProfile.LoadFactor(ApproachProfile.JoinG, jv, stall)); string jw;
                        var join = Chart.BestJoin(lat, lon, track, altitude, jv, jr, radius, out jw);
                        if (join != null) { Route = join; Chart.RouteFixAlt = Chart.LongAlt; }
                        JoinLog = join != null ? jw : jw + "; long final entry";
                    }
                    else JoinLog = "short final (head-on)";
                    RawRoute = Route; Plan(course, radius, Math.Max(ApproachProfile.Speed(stall), Math.Min(speed, ApproachProfile.MaxSpeed)), -1, lat, lon);
                    Why = Why.Length > 0 ? Why : ""; RouteLog = Chart.Describe(Route);
                }
                // fuel burn / mass change moved the craft's join speed: recompute arcs + lead points for THIS plane
                double nowV = Math.Max(ApproachProfile.Speed(stall), Math.Min(speed, ApproachProfile.MaxSpeed));   // turns are flown at the speed we actually have
                replanHold = RawRoute != null && ApproachProfile.NeedsReplan(PlanSpeed, nowV) ? replanHold + 1 : 0;   // no replans on transient speed: must persist ~10 s
                if (replanHold > 500) { replanHold = 0; Plan(course, radius, nowV, RouteIndex); }
                // join speed from the IAF onward = this craft's 1.5 x stall (Luke), so the planned arcs are what it flies
                bool joined = RouteIndex > 0 || (Route.Count > 0 && NavigationMath.Distance(lat, lon, Route[0].Lat, Route[0].Lon, radius) < 2 * speed * speed / (9.81 * Math.Tan(BankDeg * Math.PI / 180)));
                DesiredSpeed = ApproachProfile.Speed(stall);
                if (RouteIndex < Route.Count)
                {
                    var wp = Route[RouteIndex];
                    double dw = NavigationMath.Distance(lat, lon, wp.Lat, wp.Lon, radius);
                    double pLat = RouteIndex > 0 ? Route[RouteIndex - 1].Lat : RouteStartLat, pLon = RouteIndex > 0 ? Route[RouteIndex - 1].Lon : RouteStartLon;
                    double nextBrg = RouteIndex + 1 < Route.Count ? NavigationMath.Bearing(wp.Lat, wp.Lon, Route[RouteIndex + 1].Lat, Route[RouteIndex + 1].Lon) : course;
                    bool leadTurn = false;
                    DesiredHeading = NavigationMath.Distance(pLat, pLon, wp.Lat, wp.Lon, radius) < 300 ? NavigationMath.Bearing(lat, lon, wp.Lat, wp.Lon)
                        : LegSteer(lat, lon, pLat, pLon, wp.Lat, wp.Lon, nextBrg, speed, TurnBank, radius, out leadTurn, out LegXte);
                    if (NavigationMath.Distance(pLat, pLon, wp.Lat, wp.Lon, radius) < 300) leadTurn = false;
                    DesiredAltitude = wp.Alt;
                    DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) * .05, -10, 15);
                    bool behindUs = !double.IsNaN(track) && Math.Abs(FlightPolicy.Wrap(DesiredHeading - track)) > 100;   // overflown: never orbit a fix
                    if (leadTurn || dw < Math.Max(700, .3 * Chart.TurnRadius) || (behindUs && dw < 2.2 * Chart.TurnRadius)) RouteIndex++;
                }
                if (RouteIndex >= Route.Count) Phase = "intercept";
            }
            if (Phase == "intercept")
            {
                double lead = Math.Max(1500, .6 * Math.Min(speed, 150) * Math.Min(speed, 150) / (9.81 * Math.Tan(20 * Math.PI / 180)));   // ~1 turn radius lead: converges without overshoot
                DesiredHeading = course - FlightPolicy.Clamp(Math.Atan2(PredictCross(cross, speed, track, course, 10), Math.Max(2000, 12 * speed)) * 180 / Math.PI, -30, 30);   // damped intercept (was overshooting S->N)
                DesiredAltitude = Chart != null ? Math.Max(double.IsNaN(Chart.RouteFixAlt) ? Chart.LongAlt : Chart.RouteFixAlt, Chart.GlideAlt(-along)) : Elevation + 800;   // hold the fix altitude until aligned
                DesiredSpeed = Math.Min(ApproachProfile.Speed(stall), ApproachProfile.FinalSchedule(Distance, stall));   // intercept: join speed, never above the final schedule
                DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) * .05, -12, 10);
                if (Math.Abs(cross) < AlignCross && trackErr < AlignHeading) Phase = "final";
                else if (-along < (Kind == "short" ? 2000 : FafDistance) && (Math.Abs(cross) > 1000 || trackErr > 30 || -along < GateDistance + 500)) { Phase = "entry"; Kind = "long"; FixDistance = IfDistance; Route = null; Why = "not aligned before the FAF; repositioning"; }   // never descend unaligned
            }
            if (Phase == "final" || Phase == "flare")
            {
                // Proportional + integral (per metre flown) centerline tracking: removes steady offsets (sideslip/wind/trim) to < 5 m
                double ds = double.IsNaN(lastAlong) ? 0 : Math.Min(50, Math.Abs(along - lastAlong)); lastAlong = along;
                // Luke: coupled ILS. The SAME localizer/glideslope deviations the ILS tab shows feed the controllers: capture LOC, then GS.
                Ils = KSPChatBridge.Ils.Compute(lat, lon, altitude, Lat, Lon, EndLat, EndLon, Elevation, radius, TouchdownM);
                if (!LocCoupled && Math.Abs(Ils.LocDeg) < KSPChatBridge.Ils.LocFullScale && Ils.Front && -along < LocRange) LocCoupled = true;
                if (LocCoupled && !GsCoupled && Math.Abs(Ils.GsDeg) < KSPChatBridge.Ils.GsFullScale) GsCoupled = true;
                double latDev = LocCoupled ? Ils.CrossM : cross;
                DesiredHeading = CenterlineHeading(latDev, track, course, speed, ref CrossI, ds, heading, ref Crab, Distance < ApproachChart.ShortFix);
                DesiredAltitude = GsCoupled ? altitude - Ils.AboveGsM : Chart != null ? Math.Max(Chart.GlideAlt(-along + TouchdownM), altitude - Ils.AboveGsM) : Elevation + Math.Max(3, (TouchdownM - along) * Math.Tan(3 * Math.PI / 180));   // before GS capture: never below the ILS path
                double hat = altitude - Elevation, low = Math.Min(hat, agl);   // Luke: glide path/flare vs the RUNWAY THRESHOLD, terrain under the plane only for clearance
                DesiredSpeed = hat < 30 ? 1.15 * ApproachProfile.SaneStall(stall) : ApproachProfile.FinalSchedule(Distance, stall);   // Luke: ~2.3x stall 12->4 km, smooth decel to 1.35x by 2 km
                double ff = Chart != null && hat > 30 ? -speed * Chart.Slope : 0;   // path feed-forward (steep AGL fixes)
                double maxSink = Math.Max(4.5, 2 * speed * Math.Max(Chart != null ? Chart.Slope : .052, .052));   // shallow GS capture: at most ~2x path sink, no diving
                if (speed > DesiredSpeed + 10) maxSink = Math.Min(maxSink, Math.Max(4.5, speed * Math.Max(Chart != null ? Chart.Slope : .052, .052)));   // fast: never trade height for more speed
                DesiredVs = FlightPolicy.Clamp(ff + (DesiredAltitude - altitude) * .1, -maxSink, 3);   // 2500 m AGL long fix = ~12 deg path: steep descent allowed far out only
                Gear = (Distance < 3000 && Math.Abs(cross) < AlignCross) || low < 80;   // only on an aligned short final
                double fs = Override != null ? Override.FlareStartM : 15, fv = Override != null ? Override.FlareSinkMs : 1;
                if (low < fs) { Phase = "flare"; DesiredVs = low > fs / 3 ? -Math.Min(1.9, Math.Max(fv, 1.5)) : -Math.Min(fv, 1.5); }   // touchdown sink < 2 m/s
                double fastBy = speed - ApproachProfile.FinalSchedule(Distance + 1500, stall);   // decel lag allowance   // gates follow the schedule, only inside the short final
                if (Phase == "final" && Distance < ApproachChart.ShortFix && Distance > GateDistance && fastBy > FastAtShort) { Phase = "go around"; Why = "too fast at the short final (+" + fastBy.ToString("0") + " m/s)"; }
                if (Phase == "final" && Distance <= GateDistance && Distance > 100 && speed - ApproachProfile.AppSpeed(stall) > FastAtGate) { Phase = "go around"; Why = "too fast at 1 km (+" + (speed - ApproachProfile.AppSpeed(stall)).ToString("0") + " m/s)"; }
                if (Phase == "final" && Distance < GateDistance && Distance > 100 && (Math.Abs(cross) > GateCross || trackErr > GateHeading)) { Phase = "go around"; Why = "not lined up at 1 km (" + Math.Abs(cross).ToString("0") + " m, " + trackErr.ToString("0") + " deg)"; }
                // A missed threshold at height gets a go-around, never a dive back.
                double length = NavigationMath.Distance(Lat, Lon, EndLat, EndLon, radius);
                if (!grounded && along > length - 200 && hat > 3) { Phase = "go around"; Why = "overflew the runway (" + hat.ToString("0") + " m above threshold, " + speed.ToString("0") + " m/s)"; }
                if (grounded) Phase = "rollout";
            }
            if (Phase == "rollout") { double dsr = double.IsNaN(lastAlong) ? 0 : Math.Min(50, Math.Abs(along - lastAlong)); lastAlong = along; DesiredHeading = CenterlineHeading(cross, track, course, Math.Max(5, speed), ref CrossI, dsr, heading, ref Crab); DesiredSpeed = 0; Gear = Brakes = true; if (speed < 1) Phase = "stopped"; }
        }
    }
}
