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

    /// <summary>Takeoff (Luke 5:15 PM: a new plane ran to the runway end before rotating). Rotate speed Vr = 1.15 x stall, or just
    /// under this craft's measured liftoff speed once known. Progressive back-pressure from 0.85 Vr (2 -> 9 deg pitch target at
    /// Vr), more (12 deg) if it is still on the ground at 1.3 Vr; never waits for the approach speed or a fixed m/s. Pure; tested.</summary>
    /// <summary>Ground roll after brake release (Luke 5:36 PM: nose jumped and the plane yawed 18 deg / 13 g within 2 s of release).
    /// Throttle ramps 25%/s, elevator slightly nose-down below rotate, wheel steering and rudder gains low at low speed with rate limits,
    /// heading = takeoff line + gentle cross-track correction (0.3 deg/m, max 5 deg). Pure; tested.</summary>
    /// <summary>Craft class from mass / wingspan / part count: heavy transports fly an airliner profile (30 deg bank, matching
    /// 1.15 g level-turn joins, 6 deg/s roll, gentle pitch); light/fighter keep the full 4 g rating. Override: auto / gentle / fighter.</summary>
    internal static class CraftClass
    {
        internal static string Classify(double massT, double spanM, int parts) { return massT >= 40 || PlausibleSpan(spanM, massT) >= 30 || parts >= 150 ? "heavy" : "light"; }
        /// <summary>Span sanity: a sub-40 t craft cannot have a 100+ m wingspan; that is a stale-transform measurement (live 17:51: 5.5 t Aeris 3A
        /// measured "span 503 m" -> HEAVY -> heavy floor recovery on takeoff). Ignored (0) so mass/parts decide.</summary>
        internal static double PlausibleSpan(double spanM, double massT) { return double.IsNaN(spanM) || spanM < 0 || (spanM > 100 && massT < 40) ? 0 : spanM; }
        internal static string Effective(string auto, string overrideMode) { return overrideMode == "gentle" ? "heavy" : overrideMode == "fighter" ? "light" : auto; }
        internal static bool Gentle(string cls) { return cls == "heavy"; }
        /// <summary>Stall estimate never below what liftoff proved: a craft that lifted off at Vlo has Vs >= Vlo/1.25.</summary>
        internal static double ConservativeStall(double est, double liftoff) { return double.IsNaN(liftoff) || liftoff <= 0 ? est : Math.Max(est, liftoff / 1.25); }
        internal const double HeavyBank = 25, HeavyRollRate = 6, HeavyG = 1.15, HeavyPitchG = 1.4;
        internal static double MaxBank(string cls, double limit) { return Gentle(cls) ? Math.Min(limit, HeavyBank) : limit; }
        internal const double HeavyVsAccel = 1.0;
        /// <summary>Oct 12: heavy altitude hold porpoised around 5.0-5.2 km; the commanded vertical speed now changes at most 1 m/s per s (light: unchanged).</summary>
        internal static double SlewVs(ref double prev, double want, double dt, string cls)
        {
            if (!Gentle(cls) || double.IsNaN(prev) || dt <= 0) { prev = want; return want; }
            prev += FlightPolicy.Clamp(want - prev, -HeavyVsAccel * dt, HeavyVsAccel * dt); return prev;
        }
        internal static double JoinG(string cls, double g) { return Gentle(cls) ? Math.Min(g, HeavyG) : g; }
        /// <summary>Roll toward want. LEARN roll rate (when known) drives the step for every class; else heavies use HeavyRollRate and light/fighter snap (AUTOPILOT_REVIEW #7/#10).</summary>
        internal static double RollStep(string cls, double cmd, double want, double dt, double learnedRollDegS = double.NaN)
        {
            double rate = !double.IsNaN(learnedRollDegS) && learnedRollDegS > 5 ? FlightPolicy.Clamp(learnedRollDegS, 6, 60) : Gentle(cls) ? HeavyRollRate : 0;
            if (rate <= 0) return want;
            double d = rate * dt; return Math.Abs(want - cmd) <= d ? want : cmd + Math.Sign(want - cmd) * d;
        }
        /// <summary>Heavy: extra nose-up lead in a bank (lift lost to bank, anticipated not chased).</summary>
        internal static double PitchLead(string cls, double bankDeg) { if (!Gentle(cls)) return 0; double c = Math.Cos(Math.Min(60, Math.Abs(bankDeg)) * Math.PI / 180); return 8 * (1 / c - 1); }
        /// <summary>Heavy: start recovering (wings level + climb) when AGL is below max(150 m, 10 s of sink).</summary>
        internal static bool FloorRecover(string cls, double agl, double vs) { return Gentle(cls) && agl < Math.Max(150, 10 * Math.Max(0, -vs)); }
        /// <summary>Heavy: slower, smaller pitch corrections (inertia).</summary>
        internal static double GainScale(string cls) { return Gentle(cls) ? .7 : 1; }
        /// <summary>Heavy: softer altitude-to-VS correction (a 0.15/s loop oscillates with airliner pitch inertia; 0.08/s does not).</summary>
        internal static double VsScale(string cls) { return Gentle(cls) ? .55 : 1; }
        internal static string Label(string cls, string overrideMode) { return (Gentle(cls) ? "HEAVY (gentle)" : "LIGHT/FIGHTER") + (overrideMode == "auto" ? "" : " [" + overrideMode + "]"); }
    }

    /// <summary>Luke 5:44 PM: one localizer-style lateral law for every route leg and the LOC intercept. Course = leg bearing,
    /// deviation = cross-track (m, + = right). Asymptotic capture: the intercept angle follows the tangent-arc geometry
    /// xte = R(1 - cos a), i.e. a = acos(1 - |xte|/R), capped at 45 deg, with R = 1.25 x the turn radius, so the turn onto the
    /// line starts early by the lead R(1 - cos a) and rolls out ON the line instead of crossing it. Pure; tested.</summary>
    internal static class LocalizerCourse
    {
        internal const double MaxIntercept = 45, Margin = 1.0;
        internal static double InterceptAngle(double xte, double turnRadius)
        {
            double r = Math.Max(50, Margin * turnRadius), x = Math.Abs(xte);
            // angle = clamp(k * xte, 0, 45 deg) with k = 1 / (2 R): the demanded turn rate k v sin(a) stays below the craft's v / R,
            // so the decay onto the line is exponential (asymptotic) and the turn starts early by about the lead R(1 - cos a).
            return Math.Min(MaxIntercept, x / (2 * r) * 180 / Math.PI);
        }
        internal const double DefaultRollS = 3, HoldGain = .5;
        /// <summary>Oct 12 lineup: lead for the roll-out. The cross-track closed while the wings roll level (closing speed x learned roll-in s)
        /// is taken off the error, so the turn onto the line starts early and merges without crossing it and coming back.</summary>
        internal static double Heading(double course, double xte, double turnRadius, double track, double speed, double rollS)
        {
            if (double.IsNaN(track) || speed <= 0) return Heading(course, xte, turnRadius);
            double rs = rollS > 0 ? rollS : DefaultRollS;
            double xdot = speed * Math.Sin(FlightPolicy.Wrap(track - course) * Math.PI / 180);   // + = moving right of the line
            double tLead = rs + speed / (HoldGain * 9.81);   // roll-in + the heading hold's own time constant (bank = HoldGain x heading error)
            return Heading(course, xte + xdot * tLead, turnRadius);   // steer on the predicted cross-track: the turn onto the line starts early
        }
        internal static double Heading(double course, double xte, double turnRadius) { double h = (course - Math.Sign(xte) * InterceptAngle(xte, turnRadius)) % 360; return h < 0 ? h + 360 : h; }
    }

    /// <summary>GROK LAND3: centreline capture that actually closes the cross-track (the 03:09 intercept flew parallel 1.0-1.5 km off:
    /// heading-only steering through a bank = 0.5 x heading-error hold turned ~1 deg/s, too late to roll out, then a ~15 deg cut that never closed).
    /// Desired track = course - atan(xte / L) (L = LookFrac x the turn radius, capped MaxIntercept), and the BANK is commanded directly:
    /// turn rate = track error / TrackTau, bank = atan(v x rate / g) - heading-rate damped, no overshoot. xte + = right of the course line.</summary>
    internal static class LateralCapture
    {
        internal const double MaxIntercept = 30, TrackTau = 5, LookFrac = .4, MinLook = 600;
        internal static double Look(double speed, double maxBank) { return Math.Max(MinLook, LookFrac * speed * speed / (9.81 * Math.Tan(Math.Max(5, maxBank) * Math.PI / 180))); }
        internal static double TrackOffset(double xte, double look) { return -FlightPolicy.Clamp(Math.Atan(xte / Math.Max(1, look)) * 180 / Math.PI, -MaxIntercept, MaxIntercept); }
        /// <summary>GROK LAND4: hand over to the course capture (any phase) once the line is within the roll-out lead: converging at up to 100 deg
        /// and |xte| <= r (1 - cos angle) + 300 m, or within 30 deg and inside the look-ahead. (03:31 crossed the final at 90 deg in entry.)</summary>
        internal static bool ShouldCapture(double xte, double trackErr, double r, double speed, double maxBank)
        {
            if (double.IsNaN(trackErr)) return false; double th = Math.Abs(FlightPolicy.Wrap(trackErr));
            bool toward = xte * Math.Sin(FlightPolicy.Wrap(trackErr) * Math.PI / 180) < 0 || Math.Abs(xte) < 300;
            if (th < 30 && Math.Abs(xte) < Look(speed, maxBank)) return true;
            return toward && th <= 100 && Math.Abs(xte) <= r * (1 - Math.Cos(Math.Min(90, th) * Math.PI / 180)) + 300;
        }
        internal static double Bank(double course, double xte, double track, double speed, double maxBank, out double heading)
        {
            double h = (course + TrackOffset(xte, Look(speed, maxBank))) % 360; heading = h < 0 ? h + 360 : h;
            if (double.IsNaN(track) || speed <= 1) return 0;
            double rate = FlightPolicy.Wrap(heading - track) / TrackTau * Math.PI / 180;
            return FlightPolicy.Clamp(Math.Atan(speed * rate / 9.81) * 180 / Math.PI, -Math.Max(5, maxBank), Math.Max(5, maxBank));
        }
    }
    /// <summary>Luke 5:46 PM: arcs were flown on the outside edge (pure cross-track law, no curvature). Feed-forward bank for the
    /// path curvature at the upcoming vertex: bank = atan(v^2 kappa / g), kappa = turn / mean segment length, sign = turn
    /// direction; looked up ~1.5 s ahead so the roll starts before arc entry. Cross-track / integral stay as trims. Pure; tested.</summary>
    internal static class PathCurvature
    {
        internal const double LookAheadS = 1.5, MaxArcSeg = 1500;
        /// <summary>Signed curvature (rad/m, + = right turn) at a vertex between an inbound and outbound segment.</summary>
        internal static double AtVertex(double brgIn, double brgOut, double lenIn, double lenOut)
        {
            if (lenIn > MaxArcSeg || lenOut > MaxArcSeg || lenIn + lenOut < 1) return 0;   // a real corner between long legs: no arc
            return FlightPolicy.Wrap(brgOut - brgIn) * Math.PI / 180 / ((lenIn + lenOut) / 2);
        }
        internal static double Bank(double speed, double kappa, double maxBank) { return FlightPolicy.Clamp(Math.Atan(speed * speed * kappa / 9.81) * 180 / Math.PI, -maxBank, maxBank); }
        /// <summary>Curvature to use now: the current leg's (vertex behind) until within the look-ahead of the next vertex.</summary>
        internal static double Ahead(double kBehind, double kNext, double remaining, double speed) { return remaining <= LookAheadS * speed ? kNext : kBehind; }
    }

    /// <summary>Luke 5:59 PM: learn the stall speed in flight without stalling. Steady level-ish samples give the lift
    /// coefficient CL = n m g / (q S) against AoA; a least-squares line gives the CL slope; CLmax = CL at a conservative max-lift
    /// AoA (13 deg for stock wings); Vs = sqrt(2 m g / (rho0 S CLmax)). S is the same reference area (sum of the wings' lift
    /// coefficients) in both steps, so it cancels. Sufficient statistics persist per craft. Until confidence is high the higher of
    /// the measured and weight-based estimates is used (conservative). Pure; tested.</summary>
    internal sealed class StallLearner
    {
        internal const double AoaMax = 13, Rho0 = 1.225, G0 = 9.81, ConfidentAt = .7;
        internal double N, Sx, Sy, Sxx, Sxy, Xmin = double.MaxValue, Xmax = double.MinValue;
        /// <summary>Accept one sample; false = not steady/valid.</summary>
        internal bool Add(double aoaDeg, double qPa, double massKg, double nz, double area, double bankDeg, double vs)
        {
            if (qPa < 300 || area <= 0 || massKg <= 0 || Math.Abs(bankDeg) > 15 || Math.Abs(vs) > 8 || nz < .7 || nz > 1.4 || Math.Abs(aoaDeg) > 14) return false;
            double cl = nz * massKg * G0 / (qPa * area);
            N++; Sx += aoaDeg; Sy += cl; Sxx += aoaDeg * aoaDeg; Sxy += aoaDeg * cl; Xmin = Math.Min(Xmin, aoaDeg); Xmax = Math.Max(Xmax, aoaDeg);
            return true;
        }
        internal double Spread { get { return N < 2 ? 0 : Xmax - Xmin; } }
        internal double Confidence { get { return FlightPolicy.Clamp(N / 200, 0, 1) * FlightPolicy.Clamp(Spread / 4, 0, 1); } }
        /// <summary>CL = Slope * aoa + Intercept (aoa sign normalised so lift rises with AoA).</summary>
        internal bool Fit(out double slope, out double intercept)
        {
            slope = intercept = double.NaN; double d = N * Sxx - Sx * Sx;
            if (N < 10 || Math.Abs(d) < 1e-9) return false;
            slope = (N * Sxy - Sx * Sy) / d; intercept = (Sy - slope * Sx) / N;
            return !double.IsNaN(slope) && Math.Abs(slope) > 1e-4;
        }
        internal double ClMax { get { double a, b; if (!Fit(out a, out b)) return double.NaN; if (a < 0) a = -a; double c = a * AoaMax + b; return c > 0 ? c : double.NaN; } }
        internal double MeasuredStall(double massKg, double area)
        {
            double c = ClMax; return double.IsNaN(c) || area <= 0 ? double.NaN : Math.Sqrt(2 * massKg * G0 / (Rho0 * area * c));
        }
        /// <summary>Speed to fly by: the higher (conservative) until confident, then the measured value.</summary>
        internal static double Effective(double guess, double measured, double confidence)
        {
            if (double.IsNaN(measured)) return guess;
            if (confidence < ConfidentAt) return Math.Max(guess, measured);
            return measured;
        }
        internal static string Label(double guess, double measured, double confidence)
        {
            return "Vs " + Math.Round(Effective(guess, measured, confidence)) + " (est " + Math.Round(guess) + (double.IsNaN(measured) ? "" : ", meas " + Math.Round(measured)) + ", conf " + Math.Round(100 * confidence) + "%)";
        }
        internal Dictionary<string, object> Save() { return new Dictionary<string, object> { { "n", N }, { "sx", Sx }, { "sy", Sy }, { "sxx", Sxx }, { "sxy", Sxy }, { "xmin", Xmin }, { "xmax", Xmax } }; }
        internal static StallLearner Load(Dictionary<string, object> d)
        {
            var l = new StallLearner(); if (d == null) return l;
            Func<string, double, double> g = (k, def) => { object o; return d.TryGetValue(k, out o) && o != null ? Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture) : def; };
            l.N = g("n", 0); l.Sx = g("sx", 0); l.Sy = g("sy", 0); l.Sxx = g("sxx", 0); l.Sxy = g("sxy", 0); l.Xmin = g("xmin", double.MaxValue); l.Xmax = g("xmax", double.MinValue); return l;
        }
    }

    internal sealed class TakeoffGround
    {
        internal double Lat0, Lon0, Hdg0, Wheel, Yaw; double released = double.NaN;
        internal TakeoffGround(double lat, double lon, double hdg) { Lat0 = lat; Lon0 = lon; Hdg0 = hdg; }
        internal bool Released { get { return !double.IsNaN(released); } }
        internal bool Release(double t) { if (Released) return false; released = t; Wheel = Yaw = 0; return true; }
        internal double Since(double t) { return Released ? t - released : -1; }
        internal double Throttle(double t) { return Released ? FlightPolicy.Clamp(.25 * (t - released), 0, 1) : 0; }
        internal static double Elevator(double speed, double vr) { return speed < .85 * vr ? -.05 : double.NaN; }
        /// <summary>Metres right of the takeoff line (flat approximation, fine over a runway).</summary>
        internal double CrossTrack(double lat, double lon, double radius)
        {
            double k = Math.PI / 180 * radius, dn = (lat - Lat0) * k, de = (lon - Lon0) * k * Math.Cos(Lat0 * Math.PI / 180), h = Hdg0 * Math.PI / 180;
            return de * Math.Cos(h) - dn * Math.Sin(h);
        }
        /// <summary>Heading error (deg, + = turn right) toward the takeoff line.</summary>
        internal double HeadingError(double hdg, double xte) { return FlightPolicy.Wrap(Hdg0 - hdg) - FlightPolicy.Clamp(.3 * xte, -5, 5); }
        /// <summary>GROK TO1: rate is the heading rate (deg/s, + = turning right); it damps the correction so the roll doesn't weave.</summary>
        internal void Steer(double err, double v, double dt, double rate = 0)
        {
            double e = err - .6 * FlightPolicy.Clamp(rate, -20, 20);
            double gain = .05 * FlightPolicy.Clamp(v / 15, .25, 1), lim = FlightPolicy.Clamp(.5 * 25 / Math.Max(v, 25), .12, .5);
            Wheel = MoveTowards(Wheel, FlightPolicy.Clamp(-gain * e, -lim, lim), 1 * dt);
            Yaw = MoveTowards(Yaw, FlightPolicy.Clamp(.03 * e * FlightPolicy.Clamp(v / 30, 0, 1), -.3, .3), .5 * dt);
        }
        static double MoveTowards(double a, double b, double d) { return Math.Abs(b - a) <= d ? b : a + Math.Sign(b - a) * d; }
    }

    /// <summary>Batch 1: one log line per decision naming the learned curve or default it reads (only when the source changes).</summary>
    internal static class DecisionLog
    {
        static readonly Dictionary<string, string> seen = new Dictionary<string, string>();
        internal static Action<string> Sink;
        internal static bool Use(string decision, string source, double value)
        {
            string s; if (seen.TryGetValue(decision, out s) && s == source) return false;
            seen[decision] = source; if (Sink != null) Sink(decision + ": " + source + " (" + (double.IsNaN(value) ? "-" : value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + ")"); return true;
        }
        internal static void Reset() { seen.Clear(); }
    }
    internal sealed class TakeoffMission
    {
        readonly double start;
        internal string Phase = "roll";
        internal double LiftoffSpeed = double.NaN, Vr;
        internal TakeoffMission(double now) { start = now; }
        /// <summary>Oct 12: gear up after a positive climb (VS > 1 m/s, clear of the ground).</summary>
        internal static bool GearUp(double agl, double vs) { return agl > 10 && vs > 1; }
        internal const double RotPitch = 10;   // nose-up the rotation has to reach before the wheels unstick
        /// <summary>Vr: 1.15x stall, or the measured liftoff speed (bounded). Batch 1: with a learned pitch rate + accel, start rotating early by
        /// the speed gained while the nose comes up (accel x RotPitch / pitch rate) - the Aeris lifted at 164 m/s because rotation started at Vr.</summary>
        internal static double RotateSpeed(double stall, double liftoff, double pitchRate = double.NaN, double accel = double.NaN)
        {
            double vr = 1.15 * FlightPolicy.Clamp(stall, 15, 200);
            if (double.IsNaN(liftoff) || liftoff < 10) return vr;
            double lead = !double.IsNaN(pitchRate) && pitchRate > .5 && !double.IsNaN(accel) && accel > 0 ? FlightPolicy.Clamp(accel, 0, 15) * FlightPolicy.Clamp(RotPitch / pitchRate, .5, 6) : 0;
            return FlightPolicy.Clamp(.97 * liftoff - lead, .8 * vr, 1.2 * vr);
        }
        /// <summary>GROK TO1 (Luke): minimum safe rotation speed = the lift-scan stall estimate (mass, wing area, CLmax) + 12% margin.
        /// Rotating as soon as this is reached keeps the ground roll short and slow - high runway speed is what made the roll wobbly.</summary>
        internal const double VrMargin = 1.12;
        internal static double MinRotateSpeed(double liftStall) { return !double.IsNaN(liftStall) && liftStall >= 15 && liftStall <= 200 ? VrMargin * liftStall : double.NaN; }
        internal double RotStart, V2, MaxPitch = 30; internal string VrSource = "";
        /// <summary>GROK TO1: safe climb speed (V2) = 1.3 x the stall estimate (lift scan, else Vr/1.15).</summary>
        internal static double ClimbSpeed(double liftStall, double vr) { double s = !double.IsNaN(liftStall) && liftStall >= 15 && liftStall <= 200 ? liftStall : vr / 1.15; return 1.3 * s; }
        internal const double ShallowPitch = 5;
        /// <summary>GROK TO3 (live 03:45): the nose never came up (pitch < 3 deg) at 1.3x Vr on the ground = rotation is authority-limited (e.g. trailing-edge flaps behind the CoM, pitch surfaces re-roled).</summary>
        internal static bool RotationStalled(bool grounded, string phase, double speed, double vr, double pitch) { return grounded && phase == "rotate" && vr > 0 && speed > 1.3 * vr && pitch < 3; }
        /// <summary>GROK TO3: a liftoff over 2.5x Vr (live 190 m/s vs Vr 51; normal 105) was rotation-limited, not lift-limited - it must not raise the stall estimate (was 152).</summary>
        internal static bool LiftoffUsable(double liftoff, double vr) { return !double.IsNaN(liftoff) && liftoff > 0 && (double.IsNaN(vr) || vr <= 0 || liftoff <= 2.5 * vr); }
        /// <summary>GROK TO2: takeoff settings - climb-rate cap (1..50 m/s, NaN = auto), heading after the climbout (NaN = runway heading), max climb pitch (5..45).</summary>
        internal static double ClimbRateCap(double asked) { return double.IsNaN(asked) || asked <= 0 ? double.NaN : FlightPolicy.Clamp(asked, 1, 50); }
        internal static double HeadingAfter(double asked, double runwayHdg) { return double.IsNaN(asked) || asked < -360 || asked > 720 ? runwayHdg : ((asked % 360) + 360) % 360; }
        internal static double MaxPitchSetting(double asked) { return double.IsNaN(asked) || asked <= 0 ? 30 : FlightPolicy.Clamp(asked, ShallowPitch, 45); }
        /// <summary>GROK TO1: accelerate in a shallow climb to V2, then pitch up toward the max climb pitch (user setting, default 30) over the next
        /// 25 m/s; speed protection: below V2 the pitch comes down (0.5 deg per m/s short, never under 1 deg).</summary>
        internal static double ClimbPitch(double speed, double v2, double maxPitch)
        {
            double mp = FlightPolicy.Clamp(double.IsNaN(maxPitch) ? 30 : maxPitch, ShallowPitch, 45);
            if (speed < v2) return FlightPolicy.Clamp(ShallowPitch - .5 * (v2 - speed), 1, ShallowPitch);
            return ShallowPitch + (mp - ShallowPitch) * FlightPolicy.Clamp((speed - v2) / 25, 0, 1);
        }
        internal double Step(double now, double speed, double agl, bool grounded, double stall, double liftoff = double.NaN, double pitchRate = double.NaN, double accel = double.NaN, double liftStall = double.NaN)
        {
            double vmin = MinRotateSpeed(liftStall);
            if (!double.IsNaN(vmin)) { Vr = vmin; RotStart = Vr; VrSource = "lift scan +12%"; }
            else { Vr = RotateSpeed(stall, liftoff, pitchRate, accel); RotStart = .85 * Vr; VrSource = "stall/liftoff fallback"; }
            V2 = ClimbSpeed(liftStall, Vr);
            if (!grounded && double.IsNaN(LiftoffSpeed) && agl > 3) LiftoffSpeed = speed;
            if (!grounded && (agl >= 100 && speed >= V2 || agl >= 400)) { Phase = "climbout complete"; return ClimbPitch(speed, V2, MaxPitch); }
            if (grounded && now - start > 120) { Phase = "takeoff timeout"; return 0; }
            if (grounded && speed < RotStart) { Phase = "roll"; return 0; }
            if (grounded) { Phase = "rotate"; double span = Math.Max(1, Vr - RotStart) > 1 ? Vr - RotStart : .1 * Vr; return speed > 1.3 * Vr ? 12 : 2 + 7 * FlightPolicy.Clamp((speed - RotStart) / span, 0, 1); }
            Phase = "climbout";
            return ClimbPitch(speed, V2, MaxPitch);
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
        internal double FlareStartM = 15, FlareSinkMs = 1, TouchdownM = 350, TchM = 15, DaM = double.NaN;   // GROK LAND2: plate DA (da_m, in alt_ref)
        /// <summary>GROK LAND2: decision altitude as height above the threshold (NaN = none on the chart).</summary>
        internal double DaHat(double elevation) { return double.IsNaN(DaM) ? double.NaN : AltRef == "msl" ? DaM - elevation : DaM; }
        internal static readonly string[] Roles = { "faf", "sf", "dw_left", "dwend_left", "base_left", "dw_right", "dwend_right", "base_right", "td_left", "tdend_left", "td_right", "tdend_right", "wp" };
        internal const int MaxFixes = 16;
        static double N(Dictionary<string, object> d, string k, double def) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : def; }
        /// <summary>Parse one end. Fixes must sit within 60 km of the threshold, 0-6000 m above the runway; flare/touchdown in sane ranges.</summary>
        /// alt_ref "agl" (editor default): alt is height above the terrain under the fix, converted here with in-game terrain (runway elevation if unavailable); "msl" = legacy.
        internal static ApproachOverride Parse(object raw, double thrLat, double thrLon, double elevation, double radius, out string why, Func<double, double, double> terrain = null)
        {
            why = ""; var d = raw as Dictionary<string, object>; if (d == null) { why = "not an object"; return null; }
            try
            {
                var o = new ApproachOverride { FlareStartM = N(d, "flare_start_m", 15), FlareSinkMs = N(d, "flare_sink_ms", ApproachProfile.TouchdownSink), TouchdownM = N(d, "touchdown_m", 350), TchM = N(d, "tch_m", 15) };
                if (o.FlareStartM < 3 || o.FlareStartM > 60 || o.FlareSinkMs < .2 || o.FlareSinkMs > 5 || o.TouchdownM < 0 || o.TouchdownM > 1500 || o.TchM < 0 || o.TchM > 100) { why = "flare/touchdown/TCH out of range"; return null; }
                object ar; string altRef = d.TryGetValue("alt_ref", out ar) && ar != null ? Convert.ToString(ar) : "agl";
                if (altRef != "agl" && altRef != "msl") { why = "alt_ref must be agl or msl"; return null; }
                o.AltRef = altRef; o.DaM = N(d, "da_m", double.NaN);
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
        /// <summary>True when the active craft has a LEARN'd stall (wider SaneStall / join floor — AUTOPILOT_REVIEW #4/#11).</summary>
        internal static bool TrustedStall;
        /// <summary>Stall at current mass from a reference stall (Vs ~ sqrt(mass)).</summary>
        internal static double StallAt(double stallRef, double massRef, double massNow) { return massRef > 0 && massNow > 0 ? stallRef * Math.Sqrt(massNow / massRef) : stallRef; }
        /// <summary>Wing-loading estimate when the craft has no measured stall: Vs = sqrt(2 m g / (rho0 * K * sum(lift coeff))), K calibrated to stock (6 t, 6 lift -> ~45 m/s).</summary>
        /// <summary>Vs = sqrt(2 m g / (rho S CLmax)). Stock: 1 deflectionLiftCoeff = 3.52 m^2 of wing. Luke 6:31 PM: the old 7.9 factor meant
        /// CLmax 2.25 (far too optimistic: a 48.8 t A300 got Vs 45 and lifted off at 94). Now CLmax 1.6 (stock wings ~1.5-1.8 at max-lift AoA), sea-level rho; heavies also get a sqrt(mass) floor and the liftoff bound.</summary>
        internal const double AreaPerCoeff = 3.52, ClMaxConservative = 1.6, RhoSea = 1.225;
        internal static double EstimateStall(double massKg, double liftCoeffSum) { return liftCoeffSum <= 0 ? UnmeasuredStallFloor : FlightPolicy.Clamp(Math.Sqrt(2 * massKg * 9.81 / (RhoSea * AreaPerCoeff * liftCoeffSum * ClMaxConservative)), UnmeasuredStallFloor, 200); }
        /// <summary>Heavy-class floor that scales with sqrt(mass): 10 m/s * sqrt(t) (48.8 t -> 70 m/s).</summary>
        internal static double HeavyStallFloor(double massT) { return 10 * Math.Sqrt(Math.Max(0, massT)); }   // Luke 13:33: unfloored estimate gave 31 m/s -> 46 m/s joins, 586 m arcs flown at 70-260 m/s
        internal static double Speed(double stall)
        {
            double s = SaneStall(stall), lo = TrustedStall ? Math.Max(40, 1.3 * s) : MinSpeed;
            return FlightPolicy.Clamp(JoinFactor * s, lo, MaxSpeed);
        }
        /// <summary>Stall sanity for the approach schedule. No LEARN: 40-120 (live 15:4x). With LEARN TrustedStall: 15-180 so light fighters and fast jets keep real Vs (AUTOPILOT_REVIEW #4).</summary>
        internal static double SaneStall(double stall)
        {
            if (double.IsNaN(stall)) return UnmeasuredStallFloor;
            return TrustedStall ? FlightPolicy.Clamp(stall, 15, 180) : FlightPolicy.Clamp(stall, 40, 120);
        }
        /// <summary>Oct 12 speed plan: landing speed 1.35x the flaps-down stall when the craft has flaps (FlapRatio = learned flap stall / clean stall),
        /// no flaps: a faster clean speed (heavy 1.75x stall, else 1.45x).</summary>
        internal static bool HasFlaps = true, Heavy; internal static double FlapRatio = double.NaN;
        internal const double NoFlapHeavy = 1.75, NoFlapLight = 1.45;
        /// <summary>Oct 12: learned econ / cruise speed (NaN = none). Entry and join fly it; the final schedule holds it to the long-final fix.</summary>
        internal static double CruiseSpeed = double.NaN;
        /// <summary>Oct 12 predictive speed gate: learned decel boosted by the flap steps still to come and the airbrakes.</summary>
        internal const double FlapStepGain = .15, BrakeGain = 1.3;
        internal static double PredictSpeed(double v, double distM, double decelA, int flapStepsLeft, bool airbrakes, double floor)
        {
            if (double.IsNaN(decelA) || decelA <= 0 || distM <= 0) return v;
            double a = decelA * (1 + FlapStepGain * Math.Max(0, flapStepsLeft)) * (airbrakes ? BrakeGain : 1);
            return Math.Max(floor, Math.Sqrt(Math.Max(0, v * v - 2 * a * distM)));
        }
        /// <summary>Too fast only when the learned-decel prediction to touchdown misses landing speed + margin. No learned decel: never (keep landing).</summary>
        internal static bool PredictTooFast(double v, double distThrM, double stall, double decelA, int flapStepsLeft, bool airbrakes, double margin, out double predicted)
        {
            predicted = double.NaN; if (double.IsNaN(decelA) || decelA <= 0) return false;
            double app = AppSpeed(stall); predicted = PredictSpeed(v, distThrM, decelA, flapStepsLeft, airbrakes, app);
            return predicted - app > margin;
        }
        /// <summary>Oct 12: a join route may be at most 2.5x the direct distance + 20 km (live: 74 km for an 11 km direct, 4 min wandering at 700 m).</summary>
        /// <summary>Oct 12 (A300 overshoot 03:29): approach energy from the learned profile. Sink limit = learned descent sink (idle dives), fallback the old fixed cap.</summary>
        /// <summary>GROK LAND5c (Luke: 20 over is fine): predicted touchdown excess over landing speed: <= +10 land, +10..+20 airbrakes + idle
        /// and continue, > +20 (or the stop estimate past the runway left) go around. 0 = land, 1 = brake/idle continue, 2 = go around.</summary>
        internal const double ContinueOver = 10, GoAroundOver = 20;
        /// <summary>GROK LAND5e: inside 2 km the speed cannot come down much on a 3 deg glide - classify on max(predicted, current).</summary>
        internal static double GateSpeed(double predicted, double speed, double distM) { return distM <= DecelEnd || double.IsNaN(predicted) ? Math.Max(double.IsNaN(predicted) ? speed : predicted, speed) : predicted; }
        internal static int SpeedGate(double predicted, double app, double stopM, double runwayLeftM)
        {
            if (double.IsNaN(predicted) || double.IsNaN(app)) return 0;
            double over = predicted - app;
            if (over > GoAroundOver || !double.IsNaN(stopM) && !double.IsNaN(runwayLeftM) && over > ContinueOver && stopM > runwayLeftM) return 2;
            return over > ContinueOver ? 1 : 0;
        }
        /// <summary>GROK LAND5c item 5: speed floor shrinks toward the threshold - 1.30x stall at/outside the FAF, 1.15x at 2 km, 1.04x at the
        /// threshold. Slow above it never goes around (path position first); below it a gentle power-up; go around only if it cannot recover.</summary>
        /// GROK LAND5g (Luke: 4% over stall is fine; 94 = 1.12x went around at 4 km): 1.10x at/outside the FAF, linear to 1.04x at the threshold.
        internal const double FloorAtFaf = 1.10, FloorAtThr = 1.04, FloorRecoverS = 10;
        internal static double FloorK(double distM, double faf)
        {
            double f = Math.Max(faf, 1000);
            if (distM >= f) return FloorAtFaf;
            return FloorAtThr + (FloorAtFaf - FloorAtThr) * Math.Max(0, distM) / f;
        }
        /// <summary>0 = fine, 1 = below the floor: gentle power, 2 = below the floor for FloorRecoverS and still not gaining = go around.</summary>
        internal static int SlowGate(double speed, double stall, double distM, double faf, double belowS, double accel)
        {
            if (double.IsNaN(stall) || stall <= 0 || speed >= FloorK(distM, faf) * stall) return 0;
            return belowS >= FloorRecoverS && accel <= 0 ? 2 : 1;
        }
        /// <summary>GROK LAND5c item 6: touchdown sink class - 1-5 normal, 5-8 firm, > 8 hard.</summary>
        internal const double TouchdownSink = 3, FlareFastOver = 10;
        internal static string TouchdownClass(double sink) { double s = Math.Abs(sink); return s <= 5 ? "normal" : s <= 8 ? "firm" : "hard"; }
        internal static bool FlareFast(double speed, double app) { return !double.IsNaN(app) && speed > app + FlareFastOver; }
        internal static double LandingPitchMin(double desiredVs) { return desiredVs < -6 ? -12 : -5; }
        internal static double SinkLimit(double learnedSink, double fallback) { return double.IsNaN(learnedSink) || learnedSink <= 1 ? fallback : learnedSink; }
        internal const double HighM = 50, FastMs = 10;
        /// <summary>High (above the planned path) or fast (over the planned speed): idle + speedbrakes, steeper descent.</summary>
        /// <summary>GROK LAND3: LOW (> LowM below the path) is never HIGH/FAST - 309 m low and 11 m/s fast got idle + speedbrakes + steeper (03:09:47).</summary>
        internal static bool EnergyHigh(double aboveM, double speed, double plannedSpeed) { return !EnergyLow(aboveM) && (aboveM > HighM || speed > plannedSpeed + FastMs); }
        internal const double LowM = 50;
        internal static bool EnergyLow(double aboveM) { return aboveM < -LowM; }
        /// <summary>GROK LAND3: what the energy state commands, for the log (matches EnergyHigh / EnergyLow).</summary>
        internal static string EnergyLabel(double aboveM, double speed, double plannedSpeed)
        {
            bool fast = speed > plannedSpeed + FastMs;
            if (EnergyLow(aboveM)) return fast ? " -> LOW/FAST: no added power, no speedbrakes, trade speed for height" : " -> LOW: power on, no descent until back on the path";
            if (aboveM > HighM) return " -> HIGH" + (fast ? "/FAST" : "") + ": idle + speedbrakes, steeper";
            return fast ? " -> FAST: idle + speedbrakes" : "";
        }
        /// <summary>High on final: sink to rejoin the path - path sink plus 5% of the height excess per s, at most the learned sink (fallback 15).</summary>
        internal static double RecoverSink(double speed, double slope, double aboveM, double learnedSink)
        {
            double path = Math.Max(4.5, speed * Math.Max(slope, .052));
            return Math.Max(path, Math.Min(SinkLimit(learnedSink, 15), path + .1 * Math.Max(0, aboveM)));   // Oct 12: full learned sink by ~100-150 m high
        }
        /// <summary>Decel used for the plan: learned idle decel, boosted by the speedbrakes when the craft has them (flaps only below their limit, so not counted).</summary>
        internal static double PlanDecel(double decelA, bool airbrakes) { return double.IsNaN(decelA) || decelA <= 0 ? decelA : decelA * (airbrakes ? BrakeGain : 1); }
        /// <summary>Oct 12 19:14 (A300 564 m low at the intercept): entry-leg altitude never below the next leg's plan (fix alt / 3 deg path);
        /// already below it -> hold the current altitude, never keep descending.</summary>
        internal static double EntryAlt(double wpAlt, double pathAlt, double altitude)
        {
            if (double.IsNaN(pathAlt)) return wpAlt;
            return altitude <= pathAlt ? Math.Max(wpAlt, altitude) : Math.Max(wpAlt, pathAlt);
        }
        /// <summary>Oct 12 19:13: speedbrakes only above the planned speed (retract below it); HIGH alone never holds them out.</summary>
        internal static bool BrakesWanted(bool speedHoldWants, bool high, double speed, double plan) { return speed > plan + (speedHoldWants || high ? 0 : 2); }   // Luke Oct 12: airbrakes whenever above plan
        /// <summary>Idle when high only while not slow: below plan - 5 the speed hold flies the throttle.</summary>
        internal static bool IdleWanted(bool high, double speed, double plan, bool nearShort = false) { return (high && speed > plan - 5) || (nearShort && speed > plan + AggressiveFastM); }
        /// <summary>Luke Oct 12: well over plan (> 15 m/s) inside 2x the short-final fix = idle + airbrakes + flaps together.</summary>
        internal const double AggressiveFastM = 15;
        internal static bool NearShort(string phase, double distM) { return (phase == "final" || phase == "intercept") && distM < 2 * ApproachChart.ShortFix; }
        internal static double MaxJoinPath(double direct) { return 3 * direct + 30000; }
        internal static double EntrySpeed(double stall) { double j = Speed(stall); return double.IsNaN(CruiseSpeed) || CruiseSpeed <= 0 ? j : Math.Min(FlightPolicy.Clamp(CruiseSpeed, j, MaxSpeed), StallCap(stall)); }
        /// <summary>GROK LAND2: approach speeds are stall-based - cruise may only LOWER them, never lift the join above the
        /// stall schedule's top (2.3x stall, cap 150 / LEARN'd 1.1x join): cruise 200 m/s kept the plane fast + high all approach.</summary>
        internal static double StallCap(double stall)
        {
            double s = SaneStall(stall), cap = TrustedStall ? Math.Max(150, Math.Min(MaxSpeed, 1.1 * JoinFactor * s)) : 150;
            return Math.Max(Math.Max(Speed(stall), AppSpeed(stall)), Math.Min(cap, 2.3 * s));
        }
        /// <summary>GROK LAND2: vertical speed that TRACKS a descending path instead of lagging ~sink/0.05 = 200 m behind it:
        /// feed-forward of the path sink (speed x slope, phased in over the first 30 m high) + 0.05/s height correction,
        /// bounded by maxSink; when fast (> plan + 10, gone by + 25) the extra dive beyond the path rate is cut (speed protection).</summary>
        internal static double PathVs(double altErr, double speed, double slope, double maxSink, double maxClimb, double overSpeed)
        {
            double ff = altErr < 0 && slope > 0 ? -Math.Max(0, speed) * slope * Math.Min(1, -altErr / 30) : 0, vs = ff + altErr * .05;
            if (vs < ff && overSpeed > 10) vs = ff + (vs - ff) * Math.Max(0, 1 - (overSpeed - 10) / 15);
            return FlightPolicy.Clamp(vs, -Math.Max(1, maxSink), maxClimb);
        }
        /// <summary>Oct 12: badly off the line -> hold altitude (no glidepath descent) until re-established. Final: > 400 m or > 30 deg; legs: > 1.5 km or > 60 deg.</summary>
        internal static bool BadlyOff(double xteM, double hdgErrDeg, bool onFinal) { double x = Math.Abs(xteM), h = Math.Abs(hdgErrDeg); return onFinal ? x > 400 || h > 30 : x > 1500 || h > 60; }
        internal static double AppSpeed(double stall)
        {
            double s = SaneStall(stall);
            if (!HasFlaps) return (Heavy ? NoFlapHeavy : NoFlapLight) * s;
            return 1.35 * s * (!double.IsNaN(FlapRatio) && FlapRatio > .5 && FlapRatio < 1 ? FlapRatio : 1);
        }
        /// <summary>Final speed schedule: 12->4 km ~2.3x stall (cap 150, or 1.1x join when LEARN'd fast), linear decel to 1.35x stall by 2 km.</summary>
        internal static double HiSpeed(double stall)
        {
            double s = SaneStall(stall), app = AppSpeed(stall), hi = 2.3 * s;
            double cap = TrustedStall ? Math.Max(150, Math.Min(MaxSpeed, 1.1 * JoinFactor * s)) : 150;
            double stallHi = Math.Max(app, Math.Min(cap, hi));   // GROK LAND2: stall-based top; cruise below it only
            if (!double.IsNaN(CruiseSpeed) && CruiseSpeed > 0) return Math.Max(app, Math.Min(stallHi, CruiseSpeed));   // Oct 12: cruise to long final (now only ever lowers it)
            return stallHi;
        }
        internal const double DefaultDecel = 1.0, DecelMargin = 500, DecelEnd = 2000;
        /// <summary>Distance needed to slow from v1 to v2 at a m/s^2: (v1^2 - v2^2) / 2a, plus margin.</summary>
        internal static double DecelStartM(double v1, double v2, double a) { return Math.Max(0, v1 * v1 - v2 * v2) / (2 * Math.Max(.3, double.IsNaN(a) ? DefaultDecel : a)) + DecelMargin; }
        /// <summary>Distance to the threshold where the decel from the long-final speed starts (ends at 2 km).</summary>
        /// <summary>No measured profile: decel scales down with mass (1.0 m/s^2 at 10 t, 0.45 at 50 t, floor 0.3).</summary>
        internal static double MassDecel(double massT) { return FlightPolicy.Clamp(1.0 * Math.Sqrt(10 / Math.Max(1, massT)), .3, 1.0); }
        /// <summary>Hold the long-final speed to the long-final fix (12 km), then bleed; earlier only when the decel needs more room.</summary>
        /// <summary>GROK LAND5e (Luke: MUCH slower, decelerate early and level): hi -> flap limit - 10 by D14 (the decel point is before D14),
        /// -> landing + 15 by the FAF, -> landing speed by 2 km. FlapLimitSpeed is set by the flaps code (NaN = unknown: hi kept to D14).</summary>
        internal static double FlapLimitSpeed = double.NaN; internal const double FlapUnderM = 10, FafOver = 15, D14 = 14000, DefaultFaf = 6000;
        /// <summary>GROK LAND5f: the schedule's landing speed = the flare's (1.15x stall), reached by 2 km.</summary>
        internal const double VrefK = 1.15;
        internal static double Vref(double stall) { return VrefK * SaneStall(stall); }
        internal static double D14Speed(double stall) { double hi = HiSpeed(stall), app = Vref(stall); return FlightPolicy.Clamp(double.IsNaN(FlapLimitSpeed) ? app + 2 * FafOver : FlapLimitSpeed - FlapUnderM, app + FafOver, hi); }   // unknown flap limit: landing + 30 at D14
        internal static double DecelStartDist(double stall, double a) { return D14 + DecelStartM(HiSpeed(stall), D14Speed(stall), a); }
        internal static double FinalSchedule(double distToThreshold, double stall, double decelA = double.NaN, double faf = double.NaN)
        {
            double hi = HiSpeed(stall), app = Vref(stall), s1 = D14Speed(stall), s2 = Math.Min(s1, app + FafOver), start = DecelStartDist(stall, decelA), d = distToThreshold;
            double fa = FlightPolicy.Clamp(double.IsNaN(faf) ? DefaultFaf : faf, DecelEnd + 1000, D14 - 1000);
            Func<double, double, double, double, double> blend = (v0, v1, d0, d1) => Math.Sqrt(v1 * v1 + (v0 * v0 - v1 * v1) * FlightPolicy.Clamp((d - d1) / (d0 - d1), 0, 1));   // constant decel
            if (d >= start) return hi; if (d >= D14) return blend(hi, s1, start, D14);
            if (d >= fa) return blend(s1, s2, D14, fa); if (d >= DecelEnd) return blend(s2, app, fa, DecelEnd);
            return app;
        }
        internal static double Bank(double chartBank, double speed) { return chartBank > 20 ? chartBank : Math.Min(Math.Max(5, chartBank), FlightPolicy.BankLimit(speed)); }   // explicit "bank 60" wins
        internal static double Radius(double speed, double bank) { return speed * speed / (9.81 * Math.Tan(bank * Math.PI / 180)); }
        /// <summary>Luke: G-based turn rating (joins ~2 g, final corrections ~1.5 g; settable), never above the 3 g cap and
        /// kept 20% inside the stall load factor (v/Vs)^2. Bank = acos(1/n), radius = v^2/(g sqrt(n^2-1)).</summary>
        /// Fighter/NASA style (Luke): allowed g = min(rating, crew tolerance, structure, stall margin), with stress back-off.
        /// <summary>Learned safe turn envelope (LEARN step 7): max sustained bank at a speed; null = defaults. RollIn = seconds to roll in.</summary>
        internal static Func<double, double> SafeBank; internal static double RollIn;
        internal const double EnvelopeUse = .85;
        /// <summary>Phase bank: the G rating (joins JoinG, final FinalG); joins/intercepts use up to 85% of the learned max safe bank when known.</summary>
        internal static double PhaseBank(string phase, double speed, double stall)
        {
            double def = BankForG(LoadFactor(phase == "entry" ? JoinG : FinalG, speed, stall));
            if (SafeBank != null && phase != "entry" && phase != "intercept") { double sb2 = SafeBank(speed); if (!double.IsNaN(sb2) && sb2 > 0) return Math.Min(def, Math.Min(25, EnvelopeUse * sb2)); }   // final/flare: gentle, never above 25 deg or the envelope
            if (SafeBank != null && (phase == "entry" || phase == "intercept")) { double sb = SafeBank(speed); if (!double.IsNaN(sb) && sb > 0) return EnvelopeUse * sb; }   // the learned envelope replaces the G rating / heavy comfort cap
            return def;
        }
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
        /// <summary>Heading-hold bank step: g-onset ramp (or straight to want when snap), then the class/learned roll-rate step. Stores the
        /// result in bankCmd itself so the next tick continues from it (Oct 12: the caller's assignment was lost and every turn stalled at ~1 deg).</summary>
        internal static double HoldBank(ref double bankCmd, string cls, double want, double dt, double onsetRoll, bool snap, double learnedRoll)
        {
            bankCmd = CraftClass.RollStep(cls, bankCmd, snap ? want : Bank(bankCmd, want, dt, GRate, onsetRoll), dt, learnedRoll);
            return bankCmd;
        }
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

    /// <summary>Per-craft deceleration at idle (and idle + airbrakes) vs speed, learned in flight, persisted (Luke). Pure; tested.</summary>
    /// <summary>GROK JOIN1 (Luke R1.5: the plane flew straight at JOIN and crossed it): explicit Dubins CSC path (turn - tangent - turn)
    /// from the current position/track to a fix ARRIVING ON the fix's required course, at the radius of the bank the controller will
    /// actually fly at the current speed. Local metres x east / y north, compass headings. Re-planned every step (receding horizon);
    /// Command gives the heading to fly now. Pure; tested.</summary>
    /// <summary>GROK LAND5 (Luke): an ACCEPTED join path is LOCKED in world space (frame: metres east/north of a fixed origin) - arc 1, straight,
    /// arc 2, final course line - and FOLLOWED (cross-track + track error per segment, arc curvature as feed-forward bank, small intercept),
    /// never re-fitted (14:22-14:25: the per-tick re-fit drifted to hdg 037, 10 km off, never turned right, then circled left).</summary>
    /// <summary>GROK LAND5c item 8: go-around traffic pattern - after the straight climb: 180 deg turn (one side) onto a downwind 2r off the
    /// centreline, downwind past the threshold to abeam the join, 180 deg back onto final at max(FAF, 8 km): one locked path, ~360 deg total.</summary>
    internal static class GoAroundPattern
    {
        internal const double MinFinalM = ApproachChart.LongJoinM; internal const int Side = -1;   // GROK LAND5d: base turn ends at D14   // left-hand pattern
        internal static double JoinDistance(double faf) { return Math.Max(faf, MinFinalM); }
        /// <summary>(x, y) = plane in metres from the threshold (x east, y north); course = runway inbound course.</summary>
        internal static LockedPath Build(double x, double y, double course, double r, double faf, int side = Side)
        {
            double hu = DubinsPath.Norm(course + 180), cr = course * Math.PI / 180, ux = Math.Sin(cr), uy = Math.Cos(cr), rx = Math.Cos(cr), ry = -Math.Sin(cr);
            double e = x * rx + y * ry, a = x * ux + y * uy, jd = JoinDistance(faf);   // lateral offset (+ right) and along (+ past the threshold)
            double[] p = { x, y }, c1 = { x + side * r * rx, y + side * r * ry }, t1 = { x + 2 * side * r * rx, y + 2 * side * r * ry };
            double s = Math.Max(0, a + jd);
            double[] t2 = { t1[0] - s * ux, t1[1] - s * uy };
            double[] fix = { t2[0] - 2 * side * r * rx, t2[1] - 2 * side * r * ry }, c2 = { t2[0] - side * r * rx, t2[1] - side * r * ry };
            var l = new LockedPath { Type = side < 0 ? "LSL" : "RSR", R = r, A1 = 180, A2 = 180, S = s, Hu = hu, H0 = DubinsPath.Norm(course), H1 = DubinsPath.Norm(course), D1 = side, D2 = side, C1 = c1, C2 = c2, T1 = t1, T2 = t2, Stub = fix, Fix = fix, Seg = 0 };
            return l;
        }
    }
    /// <summary>GROK LAND5c item 7: radar-altitude callouts on short final, once each per approach.</summary>
    internal sealed class AltCallouts
    {
        internal static readonly int[] Marks = { 100, 50, 30, 20, 10 }; int next;
        internal void Reset() { next = 0; }
        internal string Step(double agl)
        {
            string say = null; while (next < Marks.Length && agl <= Marks[next]) { say = Marks[next] + " m"; next++; }
            return say;
        }
    }
    internal sealed class LockedPath
    {
        internal string Type; internal double R, A1, A2, S, Hu, H0, H1; internal int D1, D2;
        internal double[] C1, C2, T1, T2, Stub, Fix;   // world frame
        internal int Seg; internal double SegLeft, Xtrk, TrkErr, BankAsk, DesiredTrack, swept, lastBrg = double.NaN; internal const int N = 4;
        internal const double UnlockDist = 10000, MaxIntercept = 30;
        static double Brg(double dx, double dy) { return DubinsPath.Norm(Math.Atan2(dx, dy) * 180 / Math.PI); }
        static double[] Right(double h) { h *= Math.PI / 180; return new[] { Math.Cos(h), -Math.Sin(h) }; }
        static double[] Add(double[] a, double ox, double oy) { return a == null ? null : new[] { a[0] + ox, a[1] + oy }; }
        /// <summary>Lock a plane-relative Dubins plan; (ox, oy) = the plane's world position when planned.</summary>
        internal static LockedPath From(DubinsPath p, double ox, double oy)
        {
            if (p == null || p.Type == "LINE" || p.C1 == null) return null;
            var l = new LockedPath { Type = p.Type, R = p.R, A1 = p.A1, A2 = p.A2, S = p.S, Hu = p.Hu, H0 = p.H0, H1 = p.H1, D1 = p.D1, D2 = p.D2,
                C1 = Add(p.C1, ox, oy), C2 = Add(p.C2, ox, oy), T1 = Add(p.T1, ox, oy), T2 = Add(p.T2, ox, oy), Stub = Add(p.Stub ?? new[] { p.X1, p.Y1 }, ox, oy), Fix = new[] { p.X1 + ox, p.Y1 + oy } };
            l.Seg = l.A1 > 2 ? 0 : l.S > 50 ? 1 : 2; return l;
        }
        internal string Describe() { return Type + " " + Math.Round(A1) + "/" + Math.Round(A2) + " deg, straight " + (S / 1000).ToString("0.0") + " km, r " + Math.Round(R) + " m, segments arc1 / straight / arc2 / final"; }
        /// <summary>One follow step at world (x, y), ground track trk. Returns false when the path must be unlocked (> UnlockDist off).</summary>
        internal bool Step(double x, double y, double trk, double v, double planBank, double maxBank)
        {
            double segTrk, e, ff = 0;
            for (int guard = 0; guard < 3; guard++)
            {   // sequencing by swept angle / along-segment progress (cannot skip the second turn)
                if (Seg == 0 || Seg == 2)
                {
                    double[] c = Seg == 0 ? C1 : C2; int d = Seg == 0 ? D1 : D2; double a = Seg == 0 ? A1 : A2;
                    double b = Brg(x - c[0], y - c[1]); if (!double.IsNaN(lastBrg)) swept += d * FlightPolicy.Wrap(b - lastBrg); lastBrg = b;
                    if (swept >= a - 1) { Seg++; swept = 0; lastBrg = double.NaN; if (Seg == 1 && S <= 50) Seg = 2; continue; }
                }
                else if (Seg == 1)
                {
                    double ux = Math.Sin(Hu * Math.PI / 180), uy = Math.Cos(Hu * Math.PI / 180), al = (x - T1[0]) * ux + (y - T1[1]) * uy;
                    SegLeft = S - al; if (al >= S) { Seg = 2; swept = 0; lastBrg = double.NaN; SegLeft = 0; continue; }
                }
                break;
            }
            if (Seg == 0 || Seg == 2)
            {
                double[] c = Seg == 0 ? C1 : C2; int d = Seg == 0 ? D1 : D2;
                double dist = Math.Sqrt((x - c[0]) * (x - c[0]) + (y - c[1]) * (y - c[1])), b = Brg(x - c[0], y - c[1]);
                segTrk = DubinsPath.Norm(b + 90 * d); e = d * (R - dist);   // + = right of the path direction
                ff = d * Math.Atan(v * v / (9.81 * R)) * 180 / Math.PI;   // curvature feed-forward bank
            }
            else
            {
                double h = Seg == 1 ? Hu : H1; double[] o = Seg == 1 ? T1 : Stub; var rt = Right(h);
                segTrk = h; e = (x - o[0]) * rt[0] + (y - o[1]) * rt[1];
            }
            Xtrk = e;
            if (Math.Abs(e) > UnlockDist) return false;
            double cut = FlightPolicy.Clamp(e / Math.Max(1500, .3 * R) * MaxIntercept, -MaxIntercept, MaxIntercept);
            DesiredTrack = DubinsPath.Norm(segTrk - cut); TrkErr = FlightPolicy.Wrap(DesiredTrack - trk);
            BankAsk = FlightPolicy.Clamp(ff + 1.5 * TrkErr, -maxBank, maxBank);
            return true;
        }
        /// <summary>The locked geometry for the map (world frame), through the fix.</summary>
        internal List<double[]> Points()
        {
            var pts = new List<double[]>();
            Action<double[], int, double, double> arc = (c, d, h0, a) => { for (int i = 0; i <= 24; i++) { double h = h0 + d * a * i / 24.0, b = (h - 90 * d) * Math.PI / 180; pts.Add(new[] { c[0] + R * Math.Sin(b), c[1] + R * Math.Cos(b) }); } };
            arc(C1, D1, H0, A1); pts.Add(T2); arc(C2, D2, Hu, A2); pts.Add(Fix); return pts;
        }
    }
    internal sealed class DubinsPath
    {
        internal string Type = ""; internal double Length = double.MaxValue, R, A1, A2, S, Hu, X0, Y0, H0, X1, Y1, H1; internal int D1, D2;
        internal double[] C1, C2, T1, T2;
        internal static double M360(double a) { a = Norm(a); return a > 359.5 ? 0 : a; }   // arc amounts: ~360 = no turn
        internal static double Norm(double a) { a %= 360; return a < 0 ? a + 360 : a; }   // headings
        static double[] Right(double hdg) { double h = hdg * Math.PI / 180; return new[] { Math.Cos(h), -Math.Sin(h) }; }
        static double Brg(double dx, double dy) { return Norm(Math.Atan2(dx, dy) * 180 / Math.PI); }
        internal static DubinsPath Plan(double x0, double y0, double h0, double x1, double y1, double h1, double r, string prefer = null, double hysteresis = 0, double dirHysteresis = double.NaN)
        {
            DubinsPath best = null, bestTight = null, pref = null, same = null; var r0 = Right(h0); var r1 = Right(h1);   // GROK LAND3: same = same first turn as the committed plan
            foreach (int d1 in new[] { -1, 1 })
                foreach (int d2 in new[] { -1, 1 })
                {
                    double[] c1 = { x0 + d1 * r * r0[0], y0 + d1 * r * r0[1] }, c2 = { x1 + d2 * r * r1[0], y1 + d2 * r * r1[1] };
                    double vx = c2[0] - c1[0], vy = c2[1] - c1[1], L = Math.Sqrt(vx * vx + vy * vy), s, hu = Brg(vx, vy);
                    double slack = 0;
                    if (d1 != d2) { if (L < 2 * r) { if (L < .97 * 2 * r) continue; slack = 2 * r - L; L = 2 * r; } s = Math.Sqrt(Math.Max(0, L * L - 4 * r * r)); }   // GROK LAND4f: at the end of the straight an LSR/RSL sits on L = 2r; noise made it vanish (14:06:34 LSL 246/246)
                    else s = L;
                    double[] t1 = null, t2 = null; bool ok = false;
                    foreach (double off in d1 == d2 ? new[] { 0.0 } : new[] { Math.Atan2(2 * r, s) * 180 / Math.PI, -Math.Atan2(2 * r, s) * 180 / Math.PI })
                    {
                        double h = Norm(hu + off); var ru = Right(h);
                        t1 = new[] { c1[0] - d1 * r * ru[0], c1[1] - d1 * r * ru[1] }; t2 = new[] { c2[0] - d2 * r * ru[0], c2[1] - d2 * r * ru[1] };
                        double ux = Math.Sin(h * Math.PI / 180), uy = Math.Cos(h * Math.PI / 180), ex = t2[0] - t1[0] - s * ux, ey = t2[1] - t1[1] - s * uy;
                        if (Math.Sqrt(ex * ex + ey * ey) <= 1e-3 * Math.Max(s, 1) + 1e-6 + 2.5 * slack) { hu = h; ok = true; break; }
                    }
                    if (!ok) continue;
                    double a1 = d1 > 0 ? M360(hu - h0) : M360(h0 - hu), a2 = d2 > 0 ? M360(h1 - hu) : M360(hu - h1);
                    if (a1 > 340) a1 = 0; if (a2 > 340) a2 = 0;   // a slight overshoot of the tangent heading is not another full circle (receding horizon)
                    var p = new DubinsPath { Type = (d1 > 0 ? "R" : "L") + "S" + (d2 > 0 ? "R" : "L"), R = r, A1 = a1, A2 = a2, S = s, Hu = hu, D1 = d1, D2 = d2, C1 = c1, C2 = c2, T1 = t1, T2 = t2, X0 = x0, Y0 = y0, H0 = h0, X1 = x1, Y1 = y1, H1 = h1 };
                    p.Length = r * (a1 + a2) * Math.PI / 180 + s;
                    if (best == null || p.Length < best.Length) best = p;
                    if (TurnDeg(p) <= 270 && (bestTight == null || p.Length < bestTight.Length)) bestTight = p;   // GROK LAND4e: teardrop class
                    if (prefer != null && p.Type == prefer) pref = p;
                    if (prefer != null && prefer.Length == 3 && prefer != "LINE" && p.Type[0] == prefer[0] && (same == null || p.Length < same.Length)) same = p;
                }
            if (bestTight != null) best = bestTight;   // GROK LAND4e: never pick a figure-8 when a <=270 deg CSC exists
            if (pref != null && best != null && pref.Length <= best.Length + hysteresis) return pref;
            if (pref != null && best != null && TurnDeg(pref) <= 270 && TurnDeg(best) > TurnDeg(pref) + 1 && pref.Length <= best.Length + 4 * hysteresis) return pref;   // GROK LAND4f: never switch to MORE turning while the committed one is flyable
            double dh = double.IsNaN(dirHysteresis) ? hysteresis : dirHysteresis;   // GROK LAND4d: established in the first turn = never reverse it (dirHysteresis +inf)
            return same != null && best != null && same.Length <= best.Length + dh ? same : best;
        }
        /// <summary>Heading to fly now: keep turning on the first circle (up to 60 deg ahead, the bank saturates at the planned bank),
        /// then the tangent line toward T2, then the final circle onto the required course.</summary>
        internal double Command()
        {
            if (A1 > 2) return Norm(H0 + D1 * Math.Min(A1, 60));
            if (S > Math.Max(50, .05 * R)) return Brg(T2[0] - X0, T2[1] - Y0);
            return Norm(H0 + D2 * Math.Min(Math.Max(A2, A1), 60));
        }
        /// <summary>GROK JOIN1 guidance: the Dubins path ends one turn radius BEFORE the fix on its course line (a short straight
        /// to settle the roll-out), then the course line is tracked through the fix (cross-track law, max 30 deg cut). Returns the
        /// heading to fly; plan = the Dubins path (with the course-line stub appended in Points) or null while on the line.</summary>
        internal static double JoinSteer(double x, double y, double h, double fx, double fy, double crs, double r, string prefer, out DubinsPath plan, bool lockDir = false, bool hold = false)
        {
            double ux = Math.Sin(crs * Math.PI / 180), uy = Math.Cos(crs * Math.PI / 180), dx = x - fx, dy = y - fy;
            double along = dx * ux + dy * uy, cross = dx * uy - dy * ux, ext = r;
            bool wasLine = prefer == "LINE";   // GROK LAND3: hysteresis on the line capture (enter .35 r / 35 deg, leave .5 r / 45 deg)
            if (along > -1.3 * ext && along < ext && Math.Abs(cross) < (wasLine ? .5 : .35) * r && Math.Abs(FlightPolicy.Wrap(h - crs)) < (wasLine ? 45 : 35))
            {   // on the course line before the fix: track it
                plan = new DubinsPath { Type = "LINE", R = r, X0 = x, Y0 = y, H0 = h, X1 = fx, Y1 = fy, H1 = crs, T1 = new[] { x, y }, T2 = new[] { x, y }, C1 = new[] { x, y }, C2 = new[] { x, y }, Hu = crs, Length = -along };
                return Norm(crs - FlightPolicy.Clamp(cross / (.35 * r) * 30, -30, 30));
            }
            plan = Plan(x, y, h, fx - ext * ux, fy - ext * uy, crs, r, prefer, hold ? double.PositiveInfinity : CommitHysteresis(r), lockDir || hold ? double.PositiveInfinity : double.NaN);   // GROK LAND4f: hold = warp/jump: keep the committed type
            if (plan == null) return h;
            plan.X1 = fx; plan.Y1 = fy; plan.Stub = new[] { fx - ext * ux, fy - ext * uy };
            return plan.Command();
        }
        /// <summary>GROK LAND3: a committed join path is only swapped when another is shorter by half a turn radius (min 1 km) -
        /// was 0.1 r, so 1-3 deg of pitch-bob heading/speed noise flipped RSR/LSL/LSR/RSL every 1-15 s (03:08:31-03:09:22).</summary>
        /// <summary>GROK LAND4: shortest Dubins join onto the extended final - a point P at d (dMin..dMax) before the threshold, arriving on
        /// the course one turn radius before P (as JoinSteer flies it). total = path + r + d to the threshold. ok(d, pathLen) = feasibility (descent).</summary>
        internal static DubinsPath FinalJoin(double x, double y, double h, double tx, double ty, double course, double r, double dMin, double dMax, Func<double, double, bool> ok, out double bestD, out double total, bool expand = true)
        {
            double ux = Math.Sin(course * Math.PI / 180), uy = Math.Cos(course * Math.PI / 180);
            // GROK LAND4e: plane along-track from the threshold (negative = before it). Candidates must reach past the plane by ~2.5 r for a teardrop turnaround (live 04:06: plane at -35 km, dMax was 40 km with r 12 km - no room to turn).
            double planeAlong = -(tx * ux + ty * uy); if (planeAlong > 0) planeAlong = 0;
            if (expand) dMax = Math.Max(dMax, -planeAlong + 2.5 * Math.Max(r, 1000));
            DubinsPath best = null, bestTight = null; bestD = double.NaN; total = double.PositiveInfinity; double totalTight = double.PositiveInfinity, bestDTight = double.NaN;
            for (double d = dMin; d <= dMax + 1; d += 500)
            {
                double px = tx - d * ux, py = ty - d * uy; var p = Plan(x, y, h, px - r * ux, py - r * uy, course, r); if (p == null) continue;
                if (ok != null && !ok(d, p.Length + r)) continue;
                double len = p.Length + r + d;
                if (TurnDeg(p) <= 270 && len < totalTight) { totalTight = len; bestTight = p; bestDTight = d; }   // GROK LAND4e: teardrop class first
                if (len < total) { total = len; best = p; bestD = d; }
            }
            if (bestTight != null) { total = totalTight; bestD = bestDTight; return bestTight; }
            return best;
        }
        internal static double CommitHysteresis(double r) { return Math.Max(.5 * r, 1000); }
        /// <summary>GROK LAND4f: a speed change keeps a committed join when its radius is still flyable at the max bank (14:06:01 threw away LSR for a 492 deg LSL).</summary>
        internal static bool KeepCommitted(bool committed, double committedR, double speed, double maxBank) { return committed && !double.IsNaN(committedR) && committedR > 0 && PredictedPath.Radius(speed, maxBank) <= committedR; }
        /// <summary>GROK LAND4f: time warp / position jump (> 3 s of flight in one update) = hold the committed plan.</summary>
        internal static bool Jumped(double moved, double speed, double warp) { return warp > 1.01 || moved > 3 * Math.Max(30, speed); }
        /// <summary>GROK LAND4d/e (Luke): joins are planned at a MEDIAN bank (55% of the safe bank, 20 deg floor) so the controller keeps reserve bank
        /// (up to the safe max) to pull back onto a committed path instead of replanning. Floor was 10: at 189 m/s that made r 12.4 km (live 04:06 figure-8).</summary>
        internal const double PlanBankFrac = .55, PlanBankMin = 20;
        internal static double MedianBank(double safeMax) { double m = double.IsNaN(safeMax) || safeMax <= 0 ? 25 : safeMax; return Math.Min(m, Math.Max(PlanBankMin, PlanBankFrac * m)); }
        /// <summary>GROK LAND4e: join radius uses the approach plan speed (entry), never the current high speed while still slowing - live 04:06 planned at 189.</summary>
        internal static double JoinPlanSpeed(double entry, double speed) { double e = !double.IsNaN(entry) && entry > 40 ? entry : 150; return FlightPolicy.Clamp(e, 60, !double.IsNaN(speed) && speed > 0 ? Math.Min(speed, ApproachProfile.MaxSpeed) : ApproachProfile.MaxSpeed); }
        /// <summary>GROK LAND4e: total turning on a CSC path; a figure-8 (live LSL 240/300) is > 270 deg and must lose to a teardrop.</summary>
        internal static double TurnDeg(DubinsPath p) { return p == null || p.Type == "LINE" ? 0 : p.A1 + p.A2; }
        internal static bool FigureEight(DubinsPath p) { return p != null && p.Type != null && p.Type.Length == 3 && p.Type != "LINE" && p.Type[0] == p.Type[2] && TurnDeg(p) > 270; }
        /// <summary>GROK LAND4d: bank on a committed Dubins circle = the plan bank (feed-forward) plus up to the reserve (safe max - plan) to pull back
        /// onto the circle: outside it by 10% r = full reserve, inside = less bank (never reversed).</summary>
        internal static double ArcBank(double dir, double distCenter, double r, double planBank, double maxBank)
        { double e = FlightPolicy.Clamp((distCenter - r) / Math.Max(100, .1 * r), -1, 1); double b = e >= 0 ? planBank + e * Math.Max(0, maxBank - planBank) : planBank * (1 + e); return Math.Sign(dir) * FlightPolicy.Clamp(b, 0, maxBank); }
        /// <summary>GROK LAND4d: established in the committed first turn = already turned 10 deg its way since the commit (live 03:46-03:48: LSL/LSR/RSL/LSR/RSL/LSR every 10-30 s).</summary>
        internal static bool Established(string type, double commitHdg, double trk)
        { return type != null && type.Length == 3 && type != "LINE" && !double.IsNaN(commitHdg) && !double.IsNaN(trk) && (type[0] == 'R' ? 1 : -1) * FlightPolicy.Wrap(trk - commitHdg) > 10; }
        /// <summary>GROK LAND3: planning state filters - radius only re-sized on a real (12%) speed change; track low-passed (angle-safe).</summary>
        internal static double CommitRadius(double committed, double now) { return double.IsNaN(committed) || Math.Abs(now - committed) > .12 * committed ? now : committed; }
        internal static double FilterTrack(double filt, double track, double k) { if (double.IsNaN(track)) return filt; if (double.IsNaN(filt)) return track; return Norm(filt + FlightPolicy.Wrap(track - filt) * k); }
        internal double[] Stub;
        /// <summary>Points along the path (5 deg arc steps), start ... fix.</summary>
        internal List<double[]> Points()
        {
            var pts = new List<double[]> { new[] { X0, Y0 } };
            for (double a = 5; a < A1; a += 5) { var ru = Right(H0 + D1 * a); pts.Add(new[] { C1[0] - D1 * R * ru[0], C1[1] - D1 * R * ru[1] }); }
            pts.Add(T1); pts.Add(T2);
            if (Type != "LINE") for (double a = 5; a < A2; a += 5) { var ru = Right(Hu + D2 * a); pts.Add(new[] { C2[0] - D2 * R * ru[0], C2[1] - D2 * R * ru[1] }); }
            if (Stub != null) pts.Add(Stub);
            pts.Add(new[] { X1, Y1 }); return pts;
        }
        /// <summary>GROK JOIN1: can a route (join point first, threshold last) be flown at turn radius r? Every corner needs
        /// r x tan(turn/2) of straight leg on both sides; a leg shorter than its two corners need = not flyable (the TIGHT 175 deg join).
        /// The first point has no corner (the Dubins path arrives aligned). Slack: arcs shrunk up to Slack x tighter are still flown (as the
        /// chart smoother does); worse than that, or a corner over 150 deg, is rejected.</summary>
        internal const double Slack = 3;
        internal static bool Flyable(IList<double[]> xy, double r, out string why) { return Flyable(xy, r, Slack, out why); }
        /// <summary>GROK LAND3: slack &lt; 1 = margin (0.85: every corner needs 15% more straight than its arcs use - "needs 3972 m, has 3955 m" is NOT flyable).</summary>
        internal static bool Flyable(IList<double[]> xy, double r, double slack, out string why)
        {
            why = ""; int n = xy.Count; if (n < 3) return true;
            var need = new double[n];
            for (int i = 1; i < n - 1; i++)
            {
                double bin = Brg(xy[i][0] - xy[i - 1][0], xy[i][1] - xy[i - 1][1]), bout = Brg(xy[i + 1][0] - xy[i][0], xy[i + 1][1] - xy[i][1]);
                double th = Math.Abs(FlightPolicy.Wrap(bout - bin)); need[i] = th > 150 ? double.MaxValue / 4 : r * Math.Tan(th * Math.PI / 360);
            }
            for (int i = 0; i + 1 < n; i++)
            {
                double dx = xy[i + 1][0] - xy[i][0], dy = xy[i + 1][1] - xy[i][1], len = Math.Sqrt(dx * dx + dy * dy);
                if (need[i] + need[i + 1] > slack * len + 1) { why = "leg " + (i + 1) + " needs " + (need[i] + need[i + 1] >= double.MaxValue / 8 ? "a U-turn" : Math.Round(need[i] + need[i + 1]) + " m") + ", has " + Math.Round(len) + " m at r " + Math.Round(r) + " m"; return false; }
            }
            return true;
        }
    }
    /// <summary>GROK MAP1: the path the plane will actually fly to a fix - a constant-bank turn (radius from the current speed and
    /// the bank the controller will use) until the fix is ahead, then a straight line. A fix inside the turn circle shows as a full
    /// circle (the Oct 19 orbit). Points are lat/lon pairs; 5 deg steps, at most 360 deg of turn.</summary>
    internal static class PredictedPath
    {
        internal static double Radius(double speed, double bankDeg) { return speed * speed / (9.81 * Math.Tan(Math.Max(5, Math.Min(60, bankDeg)) * Math.PI / 180)); }
        internal static List<double[]> Points(double lat, double lon, double track, double speed, double bankDeg, double tLat, double tLon, double radius)
        {
            var pts = new List<double[]> { new[] { lat, lon } };
            if (!double.IsNaN(track) && speed > 1)
            {
                double r = Radius(speed, bankDeg), hdg = track, la = lat, lo = lon; const double step = 5;
                double chord = 2 * r * Math.Sin(step * Math.PI / 360);
                for (int k = 0; k < 72; k++)
                {
                    double err = FlightPolicy.Wrap(NavigationMath.Bearing(la, lo, tLat, tLon) - hdg);
                    if (Math.Abs(err) <= step || NavigationMath.Distance(la, lo, tLat, tLon, radius) < chord) break;
                    double turn = Math.Sign(err) * step;
                    NavigationMath.Offset(la, lo, hdg + turn / 2, chord, radius, out la, out lo); hdg = (hdg + turn + 360) % 360;
                    pts.Add(new[] { la, lo });
                }
            }
            pts.Add(new[] { tLat, tLon }); return pts;
        }
    }
    /// <summary>GROK FIX3 (Oct 19 airbrake chatter on approach): stateful airbrake hysteresis - out above plan + 8 m/s, in below
    /// plan + 2, at least DwellS between changes. Not allowed (ground / below 30 m) retracts at once. A pilot toggle is adopted
    /// as the current state. Step returns true when the airbrakes should change; Why says why (for the [brakes] log).</summary>
    internal sealed class AirbrakeGate
    {
        internal const double OnAbove = 8, OffBelow = 2, DwellS = 8;   // GROK LAND4: 8 s minimum hold (was 3: OUT/IN every 3-4 s on the porpoise)
        internal bool On; internal string Why = ""; double changedAt = double.NegativeInfinity;
        /// <summary>deployOk = soft block (GROK LAND4: low, or sinking faster than the path): no NEW deploy; allowed = hard block: retract at once.</summary>
        /// <summary>GROK LAND5e: airbrakes allowed all the way down to the flare whenever over plan (no 'not allowed here' toggle on short final).</summary>
        internal static bool Allowed(bool landed, double agl, double speed, double plan, bool moveControls, bool powerFloor) { return !landed && !moveControls && !powerFloor && (agl > 30 || speed > plan + OffBelow); }
        internal bool Step(double now, bool actual, double speed, double plan, bool allowed, bool deployOk = true)
        {
            On = actual;
            bool want = allowed && (On ? speed > plan + OffBelow : deployOk && speed > plan + OnAbove);
            if (want == On) return false;
            if (allowed && now - changedAt < DwellS) return false;
            Why = string.Format(System.Globalization.CultureInfo.InvariantCulture, "airbrakes {0}: {1:0} m/s vs plan {2:0} ({3})", want ? "OUT" : "IN", speed, plan,
                !allowed ? "not allowed here" : want ? "> plan + " + OnAbove : "< plan + " + OffBelow);
            On = want; changedAt = now; return true;
        }
    }
    internal sealed class DecelLearner
    {
        internal const double Bin = 20; const int Bins = 16;
        readonly Dictionary<string, double[][]> craft = new Dictionary<string, double[][]>();
        internal bool Dirty;
        double[][] Of(string name) { double[][] t; if (!craft.TryGetValue(name ?? "", out t)) { t = new[] { new double[Bins], new double[Bins] }; for (int k = 0; k < 2; k++) for (int i = 0; i < Bins; i++) t[k][i] = double.NaN; craft[name ?? ""] = t; } return t; }
        internal void Learn(string name, double speed, double decel, bool brakes)
        {
            if (double.IsNaN(decel) || decel <= .05 || decel > 15 || speed < 20) return;
            var t = Of(name)[brakes ? 1 : 0]; int i = Math.Min(Bins - 1, (int)(speed / Bin));
            t[i] = double.IsNaN(t[i]) ? decel : t[i] + .03 * (decel - t[i]); Dirty = true;
        }
        /// <summary>Mean decel over the speed range (brakes table preferred when available); NaN = unmeasured.</summary>
        internal double Estimate(string name, double vHi, double vLo, bool brakes)
        {
            double[][] t; if (!craft.TryGetValue(name ?? "", out t)) return double.NaN;
            for (int pass = 0; pass < 2; pass++)
            {
                var row = t[pass == 0 && brakes ? 1 : 0]; double s = 0; int n = 0;
                for (int i = Math.Max(0, (int)(vLo / Bin)); i <= Math.Min(Bins - 1, (int)(vHi / Bin)); i++) if (!double.IsNaN(row[i])) { s += row[i]; n++; }
                if (n > 0) return s / n;
                if (!brakes) break;
            }
            return double.NaN;
        }
        internal string ToJson()
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in craft) { var rows = new List<object>(); foreach (var r in kv.Value) { var l = new List<object>(); foreach (var x in r) l.Add(double.IsNaN(x) ? -1 : Math.Round(x, 3)); rows.Add(l); } d[kv.Key] = new Dictionary<string, object> { { "bin_ms", Bin }, { "idle", rows[0] }, { "airbrakes", rows[1] } }; }
            return MiniJson.Serialize(d);
        }
        internal static DecelLearner FromJson(string json)
        {
            var o = new DecelLearner(); Dictionary<string, object> d = null; try { d = MiniJson.Deserialize(json); } catch (Exception) { }
            if (d == null) return o;
            foreach (var kv in d)
            {
                var e = kv.Value as Dictionary<string, object>; if (e == null) continue; var t = o.Of(kv.Key); int k = 0;
                foreach (var key in new[] { "idle", "airbrakes" }) { object v; var l = e.TryGetValue(key, out v) ? v as System.Collections.IList : null; if (l != null) for (int i = 0; i < Math.Min(Bins, l.Count); i++) { double x = Convert.ToDouble(l[i]); t[k][i] = x < 0 ? double.NaN : x; } k++; }
            }
            return o;
        }
    }

    /// <summary>Derotation after main-gear contact (Luke: the nose gear bounced). Hold the touchdown attitude briefly, then lower the
    /// nose at a limited rate (2.5 deg/s) to nose-gear contact with no forward-stick spike (elevator floor -0.1); wheel brakes only
    /// once the nose wheel is down. Pure; tested.</summary>
    internal sealed class Derotation
    {
        internal const double Rate = 2.5, HoldS = .8, MinElevator = -.1;
        double cmd = double.NaN, held, contactSpeed, contactPitch; internal bool NoseDown;
        internal double Command { get { return cmd; } }
        internal void Reset() { cmd = double.NaN; held = 0; NoseDown = false; }
        /// <summary>Pitch command (deg) for this frame; noseGrounded from the nose wheel.</summary>
        internal double Step(double pitch, double dt, bool noseGrounded, double speed = double.NaN)
        {
            if (double.IsNaN(cmd)) { cmd = pitch; contactPitch = pitch; contactSpeed = speed; }
            if (noseGrounded && held > HoldS) NoseDown = true;
            held += dt;
            double target = contactSpeed > 10 && speed >= 0 ? -1 + (contactPitch + 1) * FlightPolicy.Clamp(speed / contactSpeed, 0, 1) : -1;
            if (noseGrounded) target = -1;
            if (held > HoldS) cmd = Math.Min(cmd, Math.Max(target, cmd - Rate * dt));
            return cmd;
        }
        /// <summary>Elevator: track the command, damp pitch rate, never push hard forward (no nose slam / bounce).</summary>
        internal double Elevator(double pitch, double q) { return FlightPolicy.Clamp(.03 * (cmd - pitch) - .04 * q, NoseDown ? -.2 : MinElevator, .5); }
        internal bool BrakesAllowed { get { return NoseDown; } }
    }

    /// <summary>GROK LAND3 (Luke: in banked turns the nose porpoised above/below the level line; log 03:09:20-40 vs +13/-18 m/s, pitch -7/+8
    /// at 11-17 deg bank): fly turns like a pilot - hold a pitch ATTITUDE = trim angle (nose pitch - flight-path angle, low-passed; in a bank the
    /// extra AoA is the elevator feed-forward, its vertical projection stays ~ the level-flight trim) + the target flight-path angle; the vs error
    /// is only a gentle (x0.4) correction on a filtered vs, and the altitude error stays the slow outer loop (the mission's target vs).</summary>
    /// <summary>GROK LAND5b/5c: approach throttle = PREDICTIVE step-and-wait. Speed 5 s ahead = v + trend*5 + the thrust still spooling
    /// (engine lag); hold while that is inside plan -5/+15; outside it one small step (3% within 10 m/s, else 4%), then 3 s wait, and no
    /// further step while the trend is already toward plan (or improved > 0.5 m/s2 since the last step). When the prediction crosses
    /// plan, half the accumulated steps come back off at once (15:16 overshoot: 6..30% while already recovering, then +21).</summary>
    /// <summary>GROK LAND5f: AUTOWARP - physics warp on long straight wings-level legs only; 1x before turns, lineup, D14/FAF, inside 10 km, config, go-around.</summary>
    internal static class AutoWarp
    {
        internal const double MaxBank = 3, MaxXtrk = 300, MinClearS = 30, FourXClearS = 90, NoWarpM = 10000;
        internal static int Want(double bank, double xtrkM, double secsClear, double distToThrM, bool goAround, bool configSoon)
        {
            if (goAround || configSoon || Math.Abs(bank) >= MaxBank || Math.Abs(xtrkM) > MaxXtrk || double.IsNaN(secsClear) || secsClear < MinClearS || distToThrM < NoWarpM) return 1;
            return secsClear >= FourXClearS ? 4 : 2;
        }
        internal static int Index(int rate) { return rate >= 4 ? 3 : rate >= 2 ? 1 : 0; }   // physics warp rates 1/2/3/4
    }
    /// <summary>GROK LAND5f: rollout wheel brakes - none until the wheels have been down 1 s, then a duty cycle ramping to full over 3 s.</summary>
    internal static class RolloutBrake
    {
        internal const double SettleS = 1, RampS = 3;
        internal static double Duty(double landedS, double speed) { if (speed < .5 || landedS < SettleS) return 0; return FlightPolicy.Clamp((landedS - SettleS) / RampS, .25, 1); }
        internal static bool On(double duty, double now) { return duty >= 1 || duty > 0 && (now * 2 - Math.Floor(now * 2)) < duty; }
    }
    /// <summary>GROK LAND5g: autowarp ownership - a change is the player's only if the rate moved off what the game settled on after our last
    /// request (the game's own clamp is not a player change); we only ever undo warp we set.</summary>
    internal sealed class AutoWarpState
    {
        internal int Req = -1, Got = -1; internal bool Settling, PlayerOwns;
        internal bool Ours { get { return !PlayerOwns && Req > 0; } }
        /// <summary>Returns the index to request, or -1 for no change.</summary>
        internal int Step(int cur, int want, bool enabled)
        {
            if (PlayerOwns) return -1;
            if (Settling) { Got = cur; Settling = false; }
            else if (Req < 0) { if (cur != 0) { PlayerOwns = true; return -1; } Req = Got = 0; }
            else if (cur != Got) { PlayerOwns = true; return -1; }
            if (!enabled) want = 0;
            if (want == Req) return -1;
            Req = want; Settling = true; return want;
        }
        internal void Reset() { Req = Got = -1; Settling = PlayerOwns = false; }
    }
    internal sealed class StepThrottle
    {
        internal const double IdleOver = 3, MaxStep = .05, Step = .04, NearStep = .03, NearMs = 10, WaitS = 3, Below = 5, Above = 15, LeadStep = .03, LeadS = 4, PostConfigS = 5, ConfigRate = .10, AheadS = 5;
        internal double Thr = double.NaN, Accel, Eff = double.NaN, Acc, Predicted = double.NaN;
        internal double SpoolS = 4, AccelPerThr = 6;   // spool lag (s) and m/s2 per unit throttle (defaults; LAND5c)
        double lastStep = double.NegativeInfinity, prevV = double.NaN, lastLead = double.NegativeInfinity, accelAtStep = double.NaN;
        internal string Why = "";
        internal void Reset(double thr) { Thr = thr; Eff = thr; Acc = 0; prevV = double.NaN; Accel = 0; lastStep = double.NegativeInfinity; accelAtStep = double.NaN; }
        /// <summary>Speed AheadS from now: trend plus the part of (command - spooled thrust) that arrives inside the window.</summary>
        internal static double Predict(double v, double accel, double thr, double eff, double spool, double perThr)
        { double tau = Math.Max(.1, spool); return v + accel * AheadS + perThr * (thr - eff) * (AheadS - tau * (1 - Math.Exp(-AheadS / tau))); }
        /// <summary>leadIn = seconds to the next flap/gear step (NaN = none).</summary>
        internal const double LastM = 2000, ShortM = 6000, DecayPerS = .01, HoldOverFloor = 8;
        internal double PlanRate; double prevPlan = double.NaN;
        internal double Update(double now, double speed, double plan, double dt, double leadIn, double floor = double.NaN, double distM = double.NaN)
        {
            if (double.IsNaN(Thr)) Thr = .3; if (double.IsNaN(Eff)) Eff = Thr;
            if (dt > 0) { if (!double.IsNaN(prevV)) Accel += ((speed - prevV) / dt - Accel) * Math.Min(1, dt / 1.5); Eff += (Thr - Eff) * Math.Min(1, dt / Math.Max(.1, SpoolS)); }
            prevV = speed;
            if (dt > 0 && !double.IsNaN(prevPlan)) PlanRate += ((plan - prevPlan) / dt - PlanRate) * Math.Min(1, dt / 3); prevPlan = plan;
            Predicted = Predict(speed, Accel, Thr, Eff, SpoolS, AccelPerThr);
            double e = Predicted - plan, sz = Math.Abs(speed - plan) <= NearMs ? NearStep : Step; Why = "";
            if (speed > plan + IdleOver) { if (Thr > 0) { Why = "fast +" + Math.Round(speed - plan) + " m/s: idle"; lastStep = now; } Thr = 0; Acc = 0; accelAtStep = Accel; return Thr; }   // GROK LAND5e: > plan + 3 = idle
            if (Math.Abs(Accel) > .5 && Math.Sign(Accel) == Math.Sign(e)) sz = FlightPolicy.Clamp(.8 * Math.Abs(Accel + e / 10) / Math.Max(1, AccelPerThr), sz, MaxStep);   // a real drag/thrust step (flaps FULL + gear): half the thrust that would stop the trend, capped
            if (Acc < -1e-9 && e <= 0 && speed > plan) Acc = 0;   // GROK LAND5e: 'back' never adds power while over plan
            if (!double.IsNaN(distM) && distM <= ShortM && e >= 0 && Thr > 0 && Acc > 1e-9) { Thr = 0; Acc = 0; lastStep = now; accelAtStep = Accel; Why = "predicted " + Math.Round(Predicted) + " crosses plan on short final: idle"; return Thr; }   // GROK LAND5f: straight to idle (spool lag)
            if (!double.IsNaN(distM) && distM <= LastM && e < 0)
            {   // GROK LAND5f (Luke: slow is fine down to the floor; distance matters more): last 2 km - power only below the floor, small steps
                bool low = !double.IsNaN(floor) && (speed < floor || Predicted < floor);
                if (low && now - lastStep >= WaitS) { double t0 = Thr; Thr = FlightPolicy.Clamp(Thr + NearStep, 0, 1); Acc += Thr - t0; lastStep = now; accelAtStep = Accel; Why = "below the floor " + Math.Round(floor) + ": +3%"; }
                else if (!low && Thr > 0) Thr = Math.Max(0, Thr - DecayPerS * Math.Max(0, dt));   // between floor and plan: decay toward idle
                return Thr;
            }
            if (Acc > 1e-9 && e >= 0 || Acc < -1e-9 && e <= 0)
            {   // predicted crossing: take half of what we stepped in back out, now
                double back = Acc / 2; Thr = FlightPolicy.Clamp(Thr - back, 0, 1); Acc = 0; lastStep = now; accelAtStep = Accel;
                Why = "predicted " + Math.Round(Predicted) + " crosses plan: back " + (back > 0 ? "-" : "+") + Math.Round(100 * Math.Abs(back)) + "%"; return Thr;
            }
            // GROK LAND5g (Luke: 'throttle increases even though it's supposed to be slowing down'): on final, power up only near the floor (floor + 8);
            // elsewhere never while the plan itself is decelerating.
            bool upOk = !double.IsNaN(distM) && !double.IsNaN(floor) ? Predicted < floor + HoldOverFloor || speed < floor + HoldOverFloor : PlanRate > -.05;
            if (!upOk && e < 0) { Why = ""; return Thr; }   // hold
            if (!double.IsNaN(leadIn) && leadIn > 0 && leadIn <= LeadS && now - lastLead > 2 * LeadS && speed <= plan) {   // GROK LAND5e: config lead only at/below plan (LAND5g: and near the floor)
                 Thr = FlightPolicy.Clamp(Thr + LeadStep, 0, 1); Acc += LeadStep; lastLead = lastStep = now; accelAtStep = Accel; Why = "config lead +3%"; return Thr; }
            if (now - lastStep < WaitS) return Thr;
            bool improving = !double.IsNaN(accelAtStep) && now - lastStep < 3 * WaitS;
            if (e < -Below && Accel <= .2 && !(improving && Accel - accelAtStep > .5)) { double t0 = Thr; Thr = FlightPolicy.Clamp(Thr + sz, 0, 1); Acc += Thr - t0; lastStep = now; accelAtStep = Accel; Why = "pred slow " + Math.Round(e) + " m/s: +" + Math.Round(100 * sz) + "%"; }
            else if (e > Above && Accel >= -.2 && !(improving && accelAtStep - Accel > .5)) { double t0 = Thr; Thr = FlightPolicy.Clamp(Thr - sz, 0, 1); Acc += Thr - t0; lastStep = now; accelAtStep = Accel; Why = "pred fast +" + Math.Round(e) + " m/s: -" + Math.Round(100 * sz) + "%"; }
            return Thr;
        }
        /// <summary>Bang-bang (idle / full) only for genuine emergencies: more than 30 m/s off the plan.</summary>
        internal static bool Emergency(double speed, double plan) { return Math.Abs(speed - plan) > 30; }
        /// <summary>During and 5 s after a config change the throttle moves at most 10%/s.</summary>
        internal static double ConfigLimit(double prev, double want, double dt, double sinceConfig) { if (sinceConfig > PostConfigS || double.IsNaN(prev)) return want; double s = ConfigRate * Math.Max(0, dt); return Math.Min(want, prev + s); }   // GROK LAND5g: limits power INCREASES only (100% after the go-around could not come off while flaps retracted -> 188 m/s)
    }
    /// <summary>GROK LAND5b: learned elevator trim per configuration (flap setting + gear); on a config change the integrator is pre-loaded by the delta.</summary>
    internal sealed class ConfigTrim
    {
        readonly Dictionary<int, double> ema = new Dictionary<int, double>();
        internal static int Key(int flaps, bool gear) { return flaps + (gear ? 10 : 0); }
        internal void Learn(int key, double elev, double dt, bool steady) { if (!steady || double.IsNaN(elev)) return; double v; ema[key] = ema.TryGetValue(key, out v) ? v + (elev - v) * Math.Min(1, dt / 10) : elev; }
        internal double Delta(int from, int to) { double a, b; return ema.TryGetValue(from, out a) && ema.TryGetValue(to, out b) ? FlightPolicy.Clamp(b - a, -.3, .3) : 0; }
    }
    internal static class PitchAttitude
    {
        internal const double TrimTau = 12, VsTau = 1.5, VsGain = .4, BankOn = 5, BankFull = 10, LandGain = .75, SlewDegS = 2, SteadyVs = 3;   // LandGain x VsGain = 0.3
        /// <summary>GROK LAND4: nose command slew, at most SlewDegS deg/s.</summary>
        internal static bool Steady(double vsPrev, double vsNow, double dt) { return !double.IsNaN(vsPrev) && dt > 0 && Math.Abs(vsNow - vsPrev) / dt < SteadyRate; }
        internal const double SteadyRate = .5;   // m/s per s
        internal static double Slew(double cur, double target, double dt) { double s = SlewDegS * Math.Max(0, dt); return FlightPolicy.Clamp(target, cur - s, cur + s); }
        internal static double TrimStep(double trim, double pitch, double fpa, double rollDeg, double dt)
        {
            double a = pitch - fpa; if (double.IsNaN(a) || double.IsInfinity(a) || Math.Abs(rollDeg) > 45) return trim;
            if (double.IsNaN(trim)) return FlightPolicy.Clamp(a, 0, 6);   // GROK LAND4: a sane seed, then learned in steady flight only
            a = FlightPolicy.Clamp(a, -5, 15);
            return trim + (a - trim) * Math.Min(1, Math.Max(0, dt) / TrimTau);
        }
        internal static double VsStep(double filt, double vs, double dt) { return double.IsNaN(filt) ? vs : filt + (vs - filt) * Math.Min(1, Math.Max(0, dt) / VsTau); }
        internal static double Target(double trim, double targetVs, double vsFilt, double tas, double gainScale)
        {
            double v = Math.Max(30, tas), g = double.IsNaN(gainScale) ? 1 : gainScale;
            return trim + Math.Atan2(targetVs, v) * 180 / Math.PI + VsGain * g * Math.Atan2(targetVs - vsFilt, v) * 180 / Math.PI;
        }
        /// <summary>GROK LAND4g (Luke: pitch bobs 1-3 deg in banks, nose target 6.9/0.7/0.3/2.3/1.0 while vs swung -3.3..+4.3): pilot law -
        /// nose = trim + path angle, held through the turn; vs is only a SLOW outer loop (4 s filter, +-1.5 m/s deadband, x0.08), nose slew 0.5 deg/s in banks.</summary>
        internal const double OuterTau = 4, OuterGain = .08, OuterDeadband = 1.5, BankSlewDegS = .5;
        internal static double OuterStep(double slow, double vs, double dt) { return double.IsNaN(slow) ? vs : slow + (vs - slow) * Math.Min(1, Math.Max(0, dt) / OuterTau); }
        internal static double Deadband(double e, double db) { return Math.Abs(e) <= db ? 0 : e - Math.Sign(e) * db; }
        internal static double OuterTarget(double trim, double targetVs, double vsSlow, double tas)
        {
            double v = Math.Max(30, tas);
            return trim + Math.Atan2(targetVs, v) * 180 / Math.PI + OuterGain * Math.Atan2(Deadband(targetVs - vsSlow, OuterDeadband), v) * 180 / Math.PI;   // x0.08 (was x0.3 on a 1.5 s filter)
        }
        internal static double BankSlew(double cur, double target, double dt, double bankDeg, double pitchErr = 0) { double r = Math.Abs(bankDeg) > BankOn ? BankSlewDegS : Math.Abs(pitchErr) > 2 ? 4 : SlewDegS, s = r * Math.Max(0, dt); return FlightPolicy.Clamp(target, cur - s, cur + s); }   // GROK LAND5b: wings level + nose > 2 deg off = 4 deg/s
        /// <summary>GROK LAND5b: wings level, a vs error from the path target over 5 m/s is corrected FAST (1.5 s vs, gain 1 beyond 3 m/s), not by the slow banked loop.</summary>
        internal static double FinalTarget(double trim, double targetVs, double vsSlow, double vsFast, double tas, double bankDeg)
        {
            double t = OuterTarget(trim, targetVs, vsSlow, tas), e = targetVs - vsFast;
            if (Math.Abs(bankDeg) <= BankOn && Math.Abs(e) > 5) t += Math.Atan2(Deadband(e, 3), Math.Max(30, tas)) * 180 / Math.PI;
            return t;
        }
        /// <summary>GROK LAND5b: sink guard inside 5 km / below 500 m AGL - sink capped at the path sink + 3 m/s: nose up straight away.</summary>
        internal static double SinkGuard(double att, double pitch, double vs, double pathVs, double tas)
        { double lim = Math.Min(pathVs, 0) - 3; return vs >= lim ? att : Math.Max(att, pitch + Math.Atan2(lim - vs, Math.Max(30, tas)) * 180 / Math.PI * 1.5); }
        /// <summary>GROK LAND5b: inner attitude gain - wings level and > 2 deg off the target = x2 (it sat 4-5 deg low at 14:46:39).</summary>
        internal static double InnerGain(double err, double bankDeg) { return Math.Abs(bankDeg) <= BankOn && Math.Abs(err) > 2 ? 2 : 1.3; }
        /// <summary>GROK LAND5b: go-around climb-out nose (deg): 8 deg, more with excess speed, less near the stall.</summary>
        internal static double GoAroundPitch(double speed, double stall, double target = double.NaN) { double s = Math.Max(30, stall), t = double.IsNaN(target) ? 1.5 * s : target; return FlightPolicy.Clamp(8 + .25 * (speed - t), 4, 15); }   // GROK LAND5d: excess speed -> more nose (climb it off)
        /// <summary>GROK LAND4g: load-factor feed-forward (deg of extra AoA): the 1 g trim AoA x (n - 1), n = 1/cos(bank) - applied as the bank builds.</summary>
        internal static double LoadFF(double trimAoa, double bankDeg) { double a = double.IsNaN(trimAoa) ? 2 : FlightPolicy.Clamp(trimAoa, 0, 8), b = Math.Min(60, Math.Abs(bankDeg)) * Math.PI / 180; return a * (1 / Math.Cos(b) - 1); }
        /// <summary>Blend weight of the attitude hold: 0 wings level, 1 from BankFull deg of bank (no switch jump).</summary>
        internal static double Weight(double bankDeg) { return FlightPolicy.Clamp((Math.Abs(bankDeg) - BankOn) / (BankFull - BankOn), 0, 1); }
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

    /// <summary>Luke Oct 12 21:22: per-plane approach chart DESIGN values from the learned profile - a middle ground of what the plane can do
    /// (55% of the learned max safe bank, 55% of the learned max sink, 0.8x learned decel) so the chart never makes it struggle.
    /// Key changes when the profile or the active vessel changes -> charts / screens rebuild. No profile = the old fixed charts.</summary>
    internal static class ChartDesign
    {
        internal const double BankUse = .55, SinkUse = .55, DecelUse = .8, MinFaf = 6000, MaxFaf = 20000, MinBank = 15, MaxBank = 35;
        internal static string Key = ""; internal static int Version; static Func<double, double> sinkAt; static double decel = double.NaN; static string lastLog = "";
        internal static void Set(string key, Func<double, double> sink, double learnedDecel)
        { if (key == Key && Version > 0) return; Key = key ?? ""; sinkAt = sink; decel = learnedDecel; Version++; }
        internal static bool Known { get { return Key.Length > 0; } }
        /// <summary>Design bank at a speed: 55% of the learned max safe bank (15..35 deg); NaN = no envelope.</summary>
        internal static double Bank(double speed)
        {
            if (!Known || ApproachProfile.SafeBank == null) return double.NaN; double sb = ApproachProfile.SafeBank(speed);
            return double.IsNaN(sb) || sb <= 0 ? double.NaN : FlightPolicy.Clamp(BankUse * sb, MinBank, MaxBank);
        }
        internal static double Sink(double speed) { if (!Known || sinkAt == null) return double.NaN; double s = sinkAt(speed); return double.IsNaN(s) || s <= 1 ? double.NaN : Math.Max(3, SinkUse * s); }
        internal static double Decel { get { return !Known || double.IsNaN(decel) || decel <= 0 ? double.NaN : DecelUse * decel; } }
        /// <summary>Final approach fix distance: room to slow from the long-final speed to approach speed at the design decel,
        /// and to descend the glide height at the design sink; 6..20 km. NaN = no learned data (use the fixed 12 km).</summary>
        internal static double FafDistance(double hiSpeed, double appSpeed)
        {
            double a = Decel, sk = Sink(hiSpeed); if (double.IsNaN(a) && double.IsNaN(sk)) return double.NaN;
            double d = double.IsNaN(a) ? 0 : ApproachProfile.DecelStartM(hiSpeed, appSpeed, a) + ApproachProfile.DecelEnd;
            return FlightPolicy.Clamp(Math.Max(d, MinFaf), MinFaf, MaxFaf);
        }
        internal static void Log(string what) { if (what == lastLog) return; lastLog = what; ChatLog.Write("approach", "chart design " + what); }
    }

    internal sealed class ApproachChart    {
        internal sealed class Wp { internal string Name; internal double Lat, Lon, Alt; }
        internal const double LongLineM = 30000;   // GROK LAND4f: the chart's long final line
        internal const double RearPast = 3000; internal const double LongFix = 12000, ShortFix = 4000, Clearance = 250, Gs = 9.81;
        internal double FafD = LongFix, ThrLat, ThrLon, Course, Elevation, TurnRadius, LongAlt, ShortAlt, MinAlt, RouteFixAlt = double.NaN;
        internal Wp Long, Short, DownLeftA, DownLeftB, ApexLeft, DownRightA, DownRightB, ApexRight, RearLeftA, RearLeftB, RearRightA, RearRightB;
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
            double db = ChartDesign.Bank(speed), dv = speed;
            if (bankDeg <= 20 && !double.IsNaN(db)) bankDeg = db;   // Oct 12 21:22: middle-ground design bank (55% of learned max)
            else if (bankDeg <= 20 && ApproachProfile.SafeBank != null) bankDeg = ApproachProfile.PhaseBank("entry", speed, speed / 1.5);   // sized from the learned turn envelope
            if (bankDeg > FinalApproachPolicy.LandingBankCap) bankDeg = FinalApproachPolicy.LandingBankCap;   // GROK FIX2: chart turns at the bank the command can fly (+ the usual 30% margin)
            double r = c.TurnRadius = Radius(speed, bankDeg) / (!double.IsNaN(db) && bankDeg == db ? Margin : 1);   // design bank already IS the margin (no extra 30%)
            double fd = ChartDesign.FafDistance(Math.Max(speed, 60), Math.Max(50, .7 * speed)); double LongFix = double.IsNaN(fd) ? ApproachChart.LongFix : Math.Round(fd / 500) * 500; c.FafD = LongFix;
            if (ChartDesign.Known) ChartDesign.Log("[" + ChartDesign.Key + "]: bank " + Math.Round(bankDeg) + " deg (" + (double.IsNaN(db) ? "no envelope" : "55% learned") + ") @ " + Math.Round(dv) + " m/s -> r " + Math.Round(r) + " m; sink " + (double.IsNaN(ChartDesign.Sink(dv)) ? "-" : ChartDesign.Sink(dv).ToString("0.0")) + ", decel " + (double.IsNaN(ChartDesign.Decel) ? "-" : ChartDesign.Decel.ToString("0.00")) + " -> FAF " + (LongFix / 1000).ToString("0.0") + " km (fixed 12), downwind offset " + (2 * r / 1000).ToString("0.0") + " km, base " + ((LongFix + r) / 1000).ToString("0.0") + " km out, teardrop end " + ((LongFix + 2 * r) / 1000).ToString("0.0") + " km");
            c.Long = c.At("long final " + (LongFix / 1000).ToString("0.#") + " km", LongFix, 0); c.Short = c.At("short final 4 km", ShortFix, 0);
            c.DownLeftA = c.At("left downwind", 0, -2 * r); c.DownLeftB = c.At("left downwind end", LongFix, -2 * r); c.ApexLeft = c.At("left base", LongFix + r, -r);
            c.DownRightA = c.At("right downwind", 0, 2 * r); c.DownRightB = c.At("right downwind end", LongFix, 2 * r); c.ApexRight = c.At("right base", LongFix + r, r);
            // rear / teardrop entry (Luke): overfly offset past the far end, outbound parallel to final, 180 back onto the long fix
            c.RearLeftA = c.At("TD-L overhead", -RearPast, -r); c.RearLeftB = c.At("TD-L outbound end", LongFix + 2 * r, -2 * r);
            c.RearRightA = c.At("TD-R overhead", -RearPast, r); c.RearRightB = c.At("TD-R outbound end", LongFix + 2 * r, 2 * r);
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
            foreach (var w in new[] { c.DownLeftA, c.DownLeftB, c.ApexLeft, c.DownRightA, c.DownRightB, c.ApexRight, c.RearLeftA, c.RearLeftB, c.RearRightA, c.RearRightB }) w.Alt = Math.Max(c.Long.Alt, c.T(w.Lat, w.Lon) + Clearance);
            c.MinAlt = elevation + 150;
            return c;
        }
        /// <summary>Glide-path altitude at distance d (m) before the threshold, through the long fix.</summary>
        internal double GlideAlt(double d)
        {
            double fixAlt = double.IsNaN(RouteFixAlt) ? LongAlt : RouteFixAlt, fixD = RouteFixD > 0 ? RouteFixD : FafD;
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
                    if (f.Role == "faf" || f.Role == "sf" || f.Role.StartsWith("td")) continue;
                    var w = new Wp { Name = string.IsNullOrEmpty(f.Name) ? f.Role : f.Name, Lat = f.Lat, Lon = f.Lon, Alt = f.Alt };
                    if (f.Side != "right") CustomLeft.Add(w);
                    if (f.Side != "left") CustomRight.Add(w);
                }
            }
            foreach (var f in o.Fixes)
            {
                Wp w = f.Role == "faf" ? Long : f.Role == "sf" ? Short : f.Role == "dw_left" ? DownLeftA : f.Role == "dwend_left" ? DownLeftB : f.Role == "base_left" ? ApexLeft
                    : f.Role == "dw_right" ? DownRightA : f.Role == "dwend_right" ? DownRightB : f.Role == "base_right" ? ApexRight
                    : f.Role == "td_left" ? RearLeftA : f.Role == "tdend_left" ? RearLeftB : f.Role == "td_right" ? RearRightA : f.Role == "tdend_right" ? RearRightB : null;
                if (w == null) continue;
                w.Lat = f.Lat; w.Lon = f.Lon; w.Alt = f.Alt; if (!string.IsNullOrEmpty(f.Name)) w.Name = f.Name;
            }
            LongAlt = Long.Alt; ShortAlt = Short.Alt; Tch = o.TchM;
        }
        internal double Slope { get { double fixAlt = double.IsNaN(RouteFixAlt) ? LongAlt : RouteFixAlt, fixD = RouteFixD > 0 ? RouteFixD : FafD; return Math.Max(0, (fixAlt - Elevation) / fixD); } }
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
        /// <summary>Rear / teardrop entry for one side, ending at the long-final fix.</summary>
        internal List<Wp> Rear(bool right) { return new List<Wp> { right ? RearRightA : RearLeftA, right ? RearRightB : RearLeftB, Long }; }
        /// <summary>Candidate join sequences for an entry chart (EntryPolicy): straight = the final line only, left/right = that
        /// side's downwind/base, rear_* = the teardrop; null/"" = every side (old behaviour).</summary>
        internal List<KeyValuePair<bool, List<Wp>>> Seqs(string entry)
        {
            var o = new List<KeyValuePair<bool, List<Wp>>>();
            if (entry == "straight") { var far = At("final line 30 km", 30000, 0); far.Alt = Math.Max(Long.Alt, GlideAlt(30000)); o.Add(new KeyValuePair<bool, List<Wp>>(false, new List<Wp> { far, Long })); }
            else if (entry == "left" || entry == "right") o.Add(new KeyValuePair<bool, List<Wp>>(entry == "right", Side(entry == "right")));
            else if (entry == "rear_left" || entry == "rear_right") o.Add(new KeyValuePair<bool, List<Wp>>(entry == "rear_right", Rear(entry == "rear_right")));
            else { o.Add(new KeyValuePair<bool, List<Wp>>(false, Side(false))); o.Add(new KeyValuePair<bool, List<Wp>>(true, Side(true))); }
            return o;
        }

        /// <summary>Luke: join the chart at the nearest fix or leg point (1/3, 2/3) ahead of the FAF this plane can safely align with:
        /// Dubins turn-straight-turn reach at its radius, arriving on the leg track; descent/climb within 10/15 m/s at its speed;
        /// terrain 150 m below the path. Picks the shortest total path to touchdown; null = none (fall back to the long final).</summary>
        internal const double JoinStep = 300, MaxIntercept = 45, MaxSink = 25; internal Wp JoinPoint;
        /// <summary>Oct 12 19:12 (A300, 48 km out, 297/330 "unreachable"): the 45 deg bearing-vs-leg limit only applies near the point (< 3 turn radii);
        /// farther out the Dubins path already turns onto the leg track, only points well behind (> 120 deg) are refused. With ample distance
        /// (> 35 km direct) each skipped chart fix costs 8 km, so it flies entry -> downwind/base -> long final instead of short-cutting to the FAF.</summary>
        internal static double PlanSink = double.NaN, PlanDecelA = double.NaN, PlanApp = double.NaN; internal static string PlanLearned = "";   // Luke Oct 12 19:57: learned sink/decel/app speed for SHORT routes
        internal const double FarJoinRadii = 3, FarMaxIntercept = 120, AmpleJoinM = 35000, SkipCostM = 8000;
        internal static bool JoinAngleOk(double icpt, bool onLeg, double toJ, double turnR) { return onLeg || toJ <= turnR || icpt <= MaxIntercept || (toJ >= FarJoinRadii * turnR && icpt <= FarMaxIntercept); }
        internal static double SkipCost(double direct, double skipped) { return direct > AmpleJoinM ? Math.Max(0, skipped) * SkipCostM : 0; }
        /// <summary>T7 approach cone: more than ConeDeg (30) off the final course -> an intercept point on the extended centerline, sized from the
        /// learned turn radius: the capture covers the cross-track at a ConeDeg-or-less angle plus one turn radius to settle on that heading.
        /// along/cross = metres from the threshold along the course (+ = past it) and across it (+ = right). Distance behind the threshold,
        /// clamped to [finalMin, ConeMaxD]; NaN when already inside the cone (heading within ConeDeg of the course).</summary>
        internal const double ConeDeg = 30, ConeMaxD = 45000;
        internal static double ConeJoinD(double along, double cross, double heading, double course, double turnR, double finalMin)
        {
            if (double.IsNaN(along) || double.IsNaN(cross) || double.IsNaN(heading) || double.IsNaN(course)) return double.NaN;
            if (Math.Abs(FlightPolicy.Wrap(heading - course)) <= ConeDeg) return double.NaN;
            double fNeed = Math.Abs(cross) / Math.Tan(ConeDeg * Math.PI / 180) + Math.Max(0, turnR);
            return FlightPolicy.Clamp(Math.Max(0, -along) - fNeed, finalMin, ConeMaxD);
        }
        string flyOutWhy = "";
        internal static double FlyOutDistance(double turnR) { return FlightPolicy.Clamp(1.75 * turnR, 5000, 10000); }
        /// <summary>Fly-out-and-turn: a point FlyOutDistance upstream of the start fix on its inbound track; path = Dubins to it (arriving on the inbound track) + back to the fix + rest of the chart.</summary>
        KeyValuePair<double, List<Wp>> FlyOut(double lat, double lon, double heading, double altitude, double speed, double turnR, double radius, List<Wp> seq, double legTrack, double direct, double[] me, Func<double, double, double[]> xy, bool right)
        {
            var f = seq[0]; double k = FlyOutDistance(turnR), la, lo; NavigationMath.Offset(f.Lat, f.Lon, legTrack + 180, k, radius, out la, out lo);
            var jp = xy(la, lo); double reach = Dubins(me[0], me[1], heading, jp[0], jp[1], legTrack, turnR);
            if (double.IsInfinity(reach) || reach >= double.MaxValue / 2) return new KeyValuePair<double, List<Wp>>(double.MaxValue, null);
            var o = new Wp { Name = "fly-out " + (k / 1000).ToString("0.0") + " km past " + f.Name.Split('(')[0].Trim(), Lat = la, Lon = lo, Alt = Math.Max(f.Alt, altitude > f.Alt ? Math.Min(altitude, f.Alt + 1000) : f.Alt) };
            if (o.Alt - altitude > reach * 15 / Math.Max(30, speed)) { ChatLog.Write("approach", "fly-out rejected: climb to " + f.Name + " exceeds 15 m/s"); return new KeyValuePair<double, List<Wp>>(double.MaxValue, null); }
            for (int s = 1; s <= 8; s++) { double g = T(lat + (la - lat) * s / 8, lon + (lo - lon) * s / 8); if (!double.IsNaN(g) && g + 150 > altitude + (o.Alt - altitude) * s / 8) { ChatLog.Write("approach", "fly-out rejected: terrain intersects climb path to " + f.Name); return new KeyValuePair<double, List<Wp>>(double.MaxValue, null); } }
            var route = new List<Wp> { o }; double rest = k; Wp prev = f; route.Add(f);
            for (int q = 1; q < seq.Count; q++) { rest += NavigationMath.Distance(prev.Lat, prev.Lon, seq[q].Lat, seq[q].Lon, radius); route.Add(seq[q]); prev = seq[q]; }
            rest += NavigationMath.Distance(prev.Lat, prev.Lon, ThrLat, ThrLon, radius);
            double total = reach + rest;
            flyOutWhy = "start path " + f.Name.Split('(')[0].Trim() + " (" + (right ? "right" : "left") + "), FLY-OUT-AND-TURN: bad angle at the start fix, out " + (k / 1000).ToString("0.0") + " km past it, turn, inbound " + Math.Round((legTrack + 360) % 360) + " deg; reach " + (reach / 1000).ToString("0.0") + " km (r " + Math.Round(turnR) + " m), total " + (total / 1000).ToString("0.0") + " km to touchdown (direct " + (direct / 1000).ToString("0.0") + ")";
            return new KeyValuePair<double, List<Wp>>(total + Math.Max(0, total - (1.3 * direct + 8000)) * 4, route);
        }
        /// <summary>GROK LAND5d (Luke): the plane's track already lines up with a published leg (start p, course crs, length len; metres):
        /// track within LegMatchTrkDeg AND (the straight-ahead path passes within LegMatchFixM of the leg start, OR the plane is within
        /// LegMatchXtkM cross-track of the leg and not past its end). Then join that leg and let the follower converge.</summary>
        internal const double LegMatchTrkDeg = 30, LegMatchFixM = 2500, LegMatchXtkM = 2000, LongJoinM = 14000;
        internal static bool LegMatch(double x, double y, double heading, double px, double py, double crs, double len)
        {
            if (double.IsNaN(heading) || Math.Abs(FlightPolicy.Wrap(heading - crs)) > LegMatchTrkDeg) return false;
            double h = heading * Math.PI / 180, ux = Math.Sin(h), uy = Math.Cos(h), dx = px - x, dy = py - y;
            double ahead = dx * ux + dy * uy, pass = Math.Abs(dx * uy - dy * ux);
            if (ahead > 0 && pass <= LegMatchFixM) return true;
            double c = crs * Math.PI / 180, cx = Math.Sin(c), cy = Math.Cos(c), al = -(dx * cx + dy * cy), xt = Math.Abs(-dx * cy + dy * cx);
            return xt <= LegMatchXtkM && al >= -LegMatchFixM && al <= Math.Max(0, len);
        }
        internal bool JoinOnLeg;   // GROK LAND5d: the last BestJoin picked a leg the plane already lines up with
        internal List<Wp> BestJoin(double lat, double lon, double heading, double altitude, double speed, double turnR, double radius, out string why, string entry = null)
        {
            JoinOnLeg = false; bool bestOnLeg = false;
            why = ""; if (double.IsNaN(heading)) { why = "no track yet"; return null; }
            double k = Math.PI / 180 * radius, cl = Math.Cos(ThrLat * Math.PI / 180);
            Func<double, double, double[]> xy = (la, lo) => new[] { (lo - ThrLon) * k * cl, (la - ThrLat) * k };
            var me = xy(lat, lon); List<Wp> best = null; double bestCost = double.MaxValue; string bestWhy = ""; double bestTotal = double.MaxValue; int nReach = 0, nAlt = 0, nTerr = 0, nTight = 0, n = 0; string tightWhy = "";
            // Luke: the route LINE is continuous mini-waypoints (every ~300 m). Join at any point ahead (toward the FAF, chart order only)
            // that is intercepted at <= 45 deg; then track the line by cross-track. Prefer the leg we are already flying along (< 30 deg).
            double direct = NavigationMath.Distance(lat, lon, ThrLat, ThrLon, radius);
            foreach (var sq in Seqs(entry))
            {
                bool right = sq.Key; var seq = sq.Value;
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
                        if (!onLeg && kk == (a == null ? steps : 1)) { var ps = xy((a ?? f).Lat, (a ?? f).Lon); onLeg = LegMatch(me[0], me[1], heading, ps[0], ps[1], legTrack, legLen); }   // GROK LAND5d: near-miss of the leg start / within 2 km of the leg
                        if (!JoinAngleOk(icpt, onLeg, toJ, turnR))
                        {   // Luke Oct 12 21:22: bad angle onto the START path -> fly out 1.5-2 turn radii past the start fix (upstream), turn around, line up inbound
                            if (i == 0 && a == null) { var fo = FlyOut(lat, lon, heading, altitude, speed, turnR, radius, seq, legTrack, direct, me, xy, right); if (fo.Value != null && fo.Key < bestCost) { bestCost = fo.Key; bestTotal = fo.Key; best = fo.Value; JoinPoint = fo.Value[0]; bestWhy = flyOutWhy; } }
                            nReach++; continue;
                        }   // never a point behind / against the line direction (near it: 45 deg)
                        var jp = xy(j.Lat, j.Lon);
                        double reach = Dubins(me[0], me[1], heading, jp[0], jp[1], legTrack, turnR);
                        if (double.IsInfinity(reach) || reach >= double.MaxValue / 2) { nReach++; continue; }
                        double sinkR = ApproachProfile.SinkLimit(PlanSink, MaxSink), dz = j.Alt - altitude, sink = reach * sinkR / Math.Max(30, speed);
                        if (dz > reach * 15 / Math.Max(30, speed)) { nAlt++; continue; }   // can't climb that much
                        double lose = -dz > sink ? (-dz - sink) * Math.Max(30, speed) / sinkR : 0;   // too high: S-turns/slowing, flown as extra distance
                        if (lose > 30000) { nAlt++; continue; }
                        bool clear = true;
                        for (int s = 1; s <= 8 && clear; s++) { double la = lat + (j.Lat - lat) * s / 8, lo = lon + (j.Lon - lon) * s / 8, g = T(la, lo), path = altitude + (j.Alt - altitude) * s / 8; if (!double.IsNaN(g) && g + 150 > path) clear = false; }   // clearance vs the PATH flown
                        if (!clear) { nTerr++; continue; }
                        double rest = 0; Wp prev = j;
                        var route = new List<Wp> { j };
                        for (int q = kk == steps || a == null ? i + 1 : i; q < seq.Count; q++) { rest += NavigationMath.Distance(prev.Lat, prev.Lon, seq[q].Lat, seq[q].Lon, radius); route.Add(seq[q]); prev = seq[q]; }
                        rest += NavigationMath.Distance(prev.Lat, prev.Lon, ThrLat, ThrLon, radius);
                        {   // GROK JOIN1: the rest of the route must be flyable at this plane's turn radius (no TIGHT shrunk arcs)
                            var rxy = new List<double[]>(); foreach (var w in route) rxy.Add(xy(w.Lat, w.Lon)); rxy.Add(xy(ThrLat, ThrLon)); string tw;
                            if (!DubinsPath.Flyable(rxy, turnR, out tw)) { nTight++; if (tightWhy.Length == 0) tightWhy = j.Name + ": " + tw; continue; }
                        }
                        double total = reach + lose + rest, cost = total + Math.Max(0, total - (1.3 * direct + 8000)) * 4 + Math.Max(0, total - ApproachProfile.MaxJoinPath(direct)) * 8;   // Oct 12: a long detour only if nothing else works   // penalize a path much longer than direct
                        cost += SkipCost(direct, i - 1 + fr);   // Oct 12: ample distance -> fly the chart from its first fix, no FAF short-cut
                        if (onLeg) cost *= .4 + .05 * i;   // prefer the leg we are already heading along - the EARLIEST matching leg (GROK LAND5d)
                        if (cost < bestCost) { bestCost = cost; bestTotal = total; best = route; JoinPoint = j; bestOnLeg = onLeg; bestWhy = "joined start path " + seq[0].Name.Split('(')[0].Trim() + " (" + (right ? "right" : "left") + "), no fly-out: joined " + (right ? "right" : "left") + " at " + j.Name + (onLeg ? " (on the leg ahead)" : "") + ": intercept " + Math.Round(icpt) + " deg, reach " + (reach / 1000).ToString("0.0") + " km (r " + Math.Round(turnR) + " m)" + (lose > 0 ? ", +" + (lose / 1000).ToString("0.0") + " km to lose height" : "") + ", total " + (total / 1000).ToString("0.0") + " km to touchdown (direct " + (direct / 1000).ToString("0.0") + ")"; }
                    }
                }
            }
            if (best == null && !string.IsNullOrEmpty(entry))
            {   // the chosen entry chart has no safe join for this plane: try every chart before the long-final fallback
                string w2; var b2 = BestJoin(lat, lon, heading, altitude, speed, turnR, radius, out w2); why = w2 + " (" + EntryPolicy.Label(entry) + " chart had no safe join)"; return b2;
            }
            int nFallback = 0;
            // Luke Oct 12 19:54: close in (A300 r~6 km, 8-16 km direct) the chart join came out as 69-96 km of loops, so it circled the field.
            // When the best chart join is a big detour, fly to a positioning fix out on the extended centerline (straight out, one turn in).
            bool detour = best != null && direct < AmpleJoinM && bestTotal > 2 * direct + 20000;
            // Preserve the selected chart and its on-leg/skip costs; use direct final only when no chart fits or an unrestricted join is a detour.
            bool useFallback = best == null || (string.IsNullOrEmpty(entry) && detour);
            bestWhy += useFallback ? " [evaluating direct-final fallback]" : " [retaining chart route and join preferences]";
            double fSink = ApproachProfile.SinkLimit(PlanSink, MaxSink), fApp = double.IsNaN(PlanApp) || PlanApp <= 0 ? .75 * speed : PlanApp;
            double finalMin = Math.Max(RunwayMission.FafDistance, Math.Min(15000, ApproachProfile.DecelStartM(speed, fApp, PlanDecelA)));   // shortest final the learned decel allows
            // T7 approach cone: more than ConeDeg (30) off the final course -> the long-final INTERCEPT point is capped below
            // (sized from the cross-track at a <= 30 deg angle + the learned turn radius) so the join onto the centerline is <= 30 deg.
            double coneOff = Math.Abs(FlightPolicy.Wrap(heading - Course)), coneD = double.NaN; bool coneCap = false;
            if (coneOff > ConeDeg)
            {
                double cE = Math.Sin(Course * Math.PI / 180), cN = Math.Cos(Course * Math.PI / 180);
                coneD = ConeJoinD(me[0] * cE + me[1] * cN, me[0] * cN - me[1] * cE, heading, Course, turnR, finalMin);
            }
            
            {   // Luke 5:56 PM (heavy craft, 330/330 unreachable): fall back to the nearest reachable point on the extended
                // centerline (long final line), far enough out to roll out aligned with this plane's turn radius; no intercept limit.
                double minD = finalMin + turnR;   // leave one turn radius before the shortest final
                // T7: >30 deg off course -> cap the intercept at the cone-sized point so the join is <= 30 deg; the cap is inside
                // the shortest final (no room for a <=30 deg capture) -> keep the full sweep, the cone cannot fit before the FAF.
                coneCap = !double.IsNaN(coneD) && coneD >= minD;
                double dMax = coneCap ? Math.Max(minD, Math.Min(coneD, ConeMaxD)) : Math.Max(minD, ConeMaxD);
                for (double d = minD; useFallback && d <= dMax; d += 1000)
                {
                    double la, lo; NavigationMath.Offset(ThrLat, ThrLon, Course + 180, d, radius, out la, out lo); var jp = xy(la, lo);
                    double reach = Dubins(me[0], me[1], heading, jp[0], jp[1], Course, turnR); if (double.IsInfinity(reach) || reach >= double.MaxValue / 2) continue;
                    double jAlt = Math.Max(LongAlt, GlideAlt(d)); bool clr = true;
                    for (int s = 1; s <= 8 && clr; s++) { double sla = lat + (la - lat) * s / 8, slo = lon + (lo - lon) * s / 8, g = T(sla, slo), path = altitude + (jAlt - altitude) * s / 8; if (!double.IsNaN(g) && g + 150 > path) clr = false; }
                    if (!clr) { nTerr++; continue; }   // terrain still rules the fallback out
                    if (jAlt - altitude > reach * 15 / Math.Max(30, speed)) { nAlt++; continue; }   // nor a climb it cannot make
                    double fz = altitude - jAlt - reach * fSink / Math.Max(30, speed), flose = fz > 0 ? fz * Math.Max(30, speed) / fSink : 0;   // too high: extra distance at the learned sink
                    if (flose > 30000) { nAlt++; continue; }
                    nFallback++; double cost = reach + flose + d;
                    if (cost < bestCost) { bestCost = cost; bestOnLeg = false; var j = new Wp { Name = "long final -" + (d / 1000).ToString("0") + " km", Lat = la, Lon = lo, Alt = jAlt };
                        best = new List<Wp> { j }; JoinPoint = j; bestWhy = "direct to the long final line at " + j.Name + ", reach " + Math.Round(reach / 1000, 1) + " km (r=" + Math.Round(turnR) + " m), total " + (cost / 1000).ToString("0.0") + " km to touchdown (direct " + (direct / 1000).ToString("0.0") + ", final min " + (finalMin / 1000).ToString("0.0") + " km)" + (coneOff > ConeDeg && !double.IsNaN(coneD) && d <= coneD ? ", approach cone: " + Math.Round(coneOff) + " deg off the final course -> intercept at " + j.Name + " sized from r " + Math.Round(turnR) + " m, join <= " + Math.Round(ConeDeg) + " deg" : "") + (detour ? " [chart join was a detour]" : ""); }
                }
            }
            JoinOnLeg = best != null && bestOnLeg; if (JoinOnLeg) bestWhy += " [ON PUBLISHED LEG: following the chart]";
            why = (best != null ? bestWhy : "no safe join point") + (PlanLearned.Length > 0 ? " {" + PlanLearned + "}" : "") + " [" + n + " candidates; rejected " + nAlt + " altitude, " + nTerr + " terrain, " + nReach + " unreachable, " + nTight + " too tight for r " + Math.Round(turnR) + " m" + (tightWhy.Length > 0 ? " (" + tightWhy + ")" : "") + (nFallback > 0 ? "; " + nFallback + " long-final fallbacks" : "") + "]" + (coneCap ? " [approach cone: " + Math.Round(coneOff) + " deg off the final course -> intercept capped at " + (coneD / 1000).ToString("0.0") + " km, join <= " + Math.Round(ConeDeg) + " deg]" : coneOff > ConeDeg && !double.IsNaN(coneD) ? " [approach cone: " + Math.Round(coneOff) + " deg off the final course -> cap " + (coneD / 1000).ToString("0.0") + " km is inside the shortest final, sweep unchanged]" : "");
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

    /// <summary>Luke: pick the runway-end chart by the inbound bearing relative to the runway heading. Within 45 deg = HEAD-ON
    /// (straight-in), 45-135 = SIDE (left/right base/downwind, by which side of the centerline we are on), beyond = REAR teardrop.</summary>
    internal static class EntryPolicy
    {
        internal const double StraightDeg = 45, SideDeg = 135;
        internal static double Rel(double inbound, double course) { return Math.Abs(FlightPolicy.Wrap(inbound - course)); }
        /// <param name="cross">our offset from the extended centerline, + = right of the landing direction</param>
        internal static string Pick(double inbound, double course, double cross)
        {
            double rel = Rel(inbound, course); string side = cross >= 0 ? "right" : "left";
            return rel <= StraightDeg ? "straight" : rel <= SideDeg ? side : "rear_" + side;
        }
        internal static string Label(string e)
        {
            return e == "straight" ? "HEAD-ON" : e == "left" ? "SIDE-L" : e == "right" ? "SIDE-R" : e == "rear_left" ? "REAR-L (teardrop)" : e == "rear_right" ? "REAR-R (teardrop)" : "ANY";
        }
        /// <summary>Chart file text for one runway end (all entries), sized for this plane; written as KEY_generated.json.</summary>
        internal static string ChartJson(ApproachChart c, string note)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture; var sb = new System.Text.StringBuilder();
            sb.Append("{\n \"alt_ref\": \"msl\",\n \"variant\": \"generated\",\n \"note\": \"").Append(note.Replace("\"", "'")).Append("\",\n \"fixes\": [\n");
            var rows = new List<string>();
            Action<string, string, ApproachChart.Wp> add = (role, side, w) => rows.Add("  { \"role\": \"" + role + "\", \"side\": \"" + side + "\", \"alt_ref\": \"msl\", \"name\": \"" + w.Name + "\", \"lat\": " + w.Lat.ToString("0.000000", ic) + ", \"lon\": " + w.Lon.ToString("0.000000", ic) + ", \"alt\": " + w.Alt.ToString("0", ic) + " }");
            add("faf", "both", c.Long); add("sf", "both", c.Short);
            add("dw_left", "left", c.DownLeftA); add("dwend_left", "left", c.DownLeftB); add("base_left", "left", c.ApexLeft);
            add("dw_right", "right", c.DownRightA); add("dwend_right", "right", c.DownRightB); add("base_right", "right", c.ApexRight);
            add("td_left", "left", c.RearLeftA); add("tdend_left", "left", c.RearLeftB); add("td_right", "right", c.RearRightA); add("tdend_right", "right", c.RearRightB);
            sb.Append(string.Join(",\n", rows.ToArray())).Append("\n ]\n}\n"); return sb.ToString();
        }
    }

    /// <summary>Oct 12: waypoints the user placed on the MAP/CHART editor this session (saved or not), per runway key.</summary>
    internal static class UserWaypoints
    {
        static readonly Dictionary<string, List<ApproachChart.Wp>> byKey = new Dictionary<string, List<ApproachChart.Wp>>(); internal static int Version;
        internal static void Put(string key, List<ApproachChart.Wp> wps) { byKey[key ?? ""] = wps ?? new List<ApproachChart.Wp>(); Version++; }
        internal static List<ApproachChart.Wp> Get(string key) { List<ApproachChart.Wp> l; return byKey.TryGetValue(key ?? "", out l) ? l : new List<ApproachChart.Wp>(); }
        internal static void Clear() { byKey.Clear(); Version++; }
        /// <summary>The first user WP ahead (within 75 deg of the track), at least minM away and nearer than the runway; null + why otherwise.</summary>
        internal static ApproachChart.Wp Pick(List<ApproachChart.Wp> wps, double lat, double lon, double track, double thrLat, double thrLon, double radius, double minM, out string why)
        {
            why = ""; ApproachChart.Wp best = null; double bd = double.MaxValue; double direct = NavigationMath.Distance(lat, lon, thrLat, thrLon, radius); var skips = new List<string>();
            foreach (var w in wps)
            {
                double d = NavigationMath.Distance(lat, lon, w.Lat, w.Lon, radius), off = Math.Abs(FlightPolicy.Wrap(NavigationMath.Bearing(lat, lon, w.Lat, w.Lon) - track));
                if (off > 75) { skips.Add(w.Name + " behind (" + Math.Round(off) + " deg off the track)"); continue; }
                if (d < minM) { skips.Add(w.Name + " too close (" + Math.Round(d) + " m < " + Math.Round(minM) + " m)"); continue; }
                if (d > direct + 5000) { skips.Add(w.Name + " beyond the runway"); continue; }
                if (d < bd) { bd = d; best = w; }
            }
            why = (best != null ? "user WP " + best.Name + " used (" + (bd / 1000).ToString("0.0") + " km ahead)" : "") + (skips.Count > 0 ? (best != null ? "; " : "") + "user WP skipped: " + string.Join(", ", skips.ToArray()) : "");
            return best;
        }
    }

    internal sealed class RunwayMission
    {
        /// <summary>Cross-track error predicted tau s ahead (+ = right of course): damps the centerline intercept.</summary>
        internal const double Kp = 1, Ki = .001, Tau = 2, Lv = 3, Lmin = 50, IntBand = 3, CrabTau = 5;
        /// <summary>Luke: centerline within ~1 m, no swinging. Desired TRACK from a cross-track PID (P on cross, D via the track
        /// angle Tau s ahead, I per metre flown inside +-3 m), then crab-compensated: heading = track - (measured track - heading),
        /// low-passed over CrabTau s, so wind/sideslip/trim offsets are cancelled instead of integrated (no PI limit cycle).</summary>
        internal static double CenterlineHeading(double cross, double track, double course, double speed, ref double crossI, double ds, double heading, ref double crab, bool tight = false, double lookAhead = 0)
        {
            if (!double.IsNaN(track) && !double.IsNaN(heading)) crab += FlightPolicy.Clamp(ds / (Math.Max(1, speed) * CrabTau), 0, 1) * (FlightPolicy.Wrap(track - heading) - crab);
            crab = FlightPolicy.Clamp(crab, -20, 20);
            if (Math.Abs(cross) < IntBand || lookAhead > 0 && Math.Abs(cross) < 150 && !double.IsNaN(track) && Math.Abs(FlightPolicy.Wrap(track - course)) < 1) crossI = FlightPolicy.Clamp(crossI + cross * ds, -5000, 5000);
            double u = Kp * PredictCross(cross, speed, track, course, Tau) + (tight ? 1.5 : 1) * Ki * crossI;   // short final: harder integral toward +-1 m
            return course - FlightPolicy.Clamp(Math.Atan2(u, lookAhead > 0 ? lookAhead : Math.Max(Lmin, Lv * speed)) * 180 / Math.PI, tight ? -8 : -15, tight ? 8 : 15) - crab;
        }
        internal static double PredictCross(double cross, double speed, double track, double course, double tau)
        { return double.IsNaN(track) ? cross : cross + tau * speed * Math.Sin(FlightPolicy.Wrap(track - course) * Math.PI / 180); }
        internal double FinalSlope { get { double s = Chart == null ? double.NaN : Chart.Slope; return FinalApproachPolicy.Slope(s); } }
        internal double Lat, Lon, EndLat, EndLon, Elevation;
        internal string Phase = "entry";
        internal double DesiredHeading, DesiredAltitude, DesiredSpeed, DesiredVs;
        internal bool Gear, Brakes; int replanHold, sinceReplan = 100000;
        internal double Distance;
        internal const double FastAtShort = 15, FastAtGate = 10;
        /// <summary>Oct 12: speed-gate inputs (set by the controller): learned decel (NaN = none -> no speed go-around), flap steps still to come, airbrakes.</summary>
        internal double LearnedDecel = double.NaN; internal int FlapStepsLeft; internal bool HasAirbrakes;
        /// <summary>Oct 12: learned descent sink (m/s, from LEARN idle dives; NaN = fixed fallbacks), energy state and the per-leg energy log line.</summary>
        internal double LearnedSink = double.NaN; internal bool EnergyHigh; internal bool EnergyLow; internal string EnergyLog = ""; double energyBucket = double.NaN; string energyKey = "";
        double EffDecel { get { return ApproachProfile.PlanDecel(DecelA, HasAirbrakes); } }
        void EnergyNote(double altitude, double speed, double plannedAlt, int leg)
        {   // one line per leg / phase and every 2 km: planned vs actual altitude and speed, distance to the threshold
            double b = Math.Floor(Distance / 2000); string key = Phase + "/" + leg;
            if (key == energyKey && b == energyBucket) return; energyKey = key; energyBucket = b;
            EnergyLog = "energy " + Phase + " leg " + leg + ": " + (Distance / 1000).ToString("0.0") + " km to thr, alt plan " + plannedAlt.ToString("0") + " act " + altitude.ToString("0") + " (" + (altitude - plannedAlt).ToString("+0;-0") + " m), spd plan " + DesiredSpeed.ToString("0") + " act " + speed.ToString("0")
                + ", sink limit " + ApproachProfile.SinkLimit(LearnedSink, 10).ToString("0") + (double.IsNaN(LearnedSink) ? " (default)" : " (learned)") + ", decel " + (double.IsNaN(EffDecel) ? "-" : EffDecel.ToString("0.00")) + ApproachProfile.EnergyLabel(altitude - plannedAlt, speed, DesiredSpeed)
                + (Phase == "final" ? "; steering to moving centerline point 1500 m ahead on 3 deg glide path" + (OffLine ? "; off line: descent held" : "") : "");
        }
        /// <summary>Predicted touchdown speed / landing speed for the ILS speed cue (NaN = no prediction).</summary>
        internal static double PredictedNow = double.NaN, AppNow = double.NaN;
        /// <summary>Oct 12: after a go-around no gate re-runs for ~30 s (live: 3 go-arounds in 0.5 s).</summary>
        internal double RollS = double.NaN;   // learned roll-in time, s (lineup lead)
        internal int GateCooldown; internal const int GateCooldownSteps = 1500;
        internal double DecelA = double.NaN, LastStallEst = 45;   // measured idle(+airbrake) decel for this craft; NaN = conservative default
        /// <summary>LEARN sink-map hint for flare VS (m/s positive down); NaN = default 1 m/s (AUTOPILOT_REVIEW #6).</summary>
        internal double FlareSinkHint = double.NaN;
        internal double DecelStartDist { get { return ApproachProfile.DecelStartDist(LastStallEst, DecelA); } }
        internal const double IfDistance = 12000, FafDistance = 5000, AlignCross = 150, AlignHeading = 8, GateDistance = 1000, GateCross = 40, GateHeading = 6, LineupGateM = 100;   // GROK LAND5d (Luke): lineup corrections run until 100 m before the touchdown point; the go-around decision is there
        internal double Cross, Along;
        internal string Why = "";
        internal Func<double, double, double> Terrain; internal double BankDeg = 20, LongAgl = -1, ShortAgl = -1;
        internal ApproachOverride Override; internal string Key = ""; int seenUserWp = -1;
        internal ApproachChart Chart; internal List<ApproachChart.Wp> Route; internal int RouteIndex; internal string RouteLog = "";
        internal string SmoothLog = "", JoinLog = "", EntryChart = "", EntryLog = "";
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
        /// <summary>GROK LAND2: after a go-around, climb straight ahead on the runway heading to MissedAgl before any turn, then re-plan from there.</summary>
        bool finalJoinRoute;   // GROK LAND4
        /// <summary>GROK LAND5d: go-around climb / downwind / base speed = approach entry speed (never the 250 m/s of 15:54).</summary>
        internal static double PatternSpeed(double stall) { return Math.Max(ApproachProfile.EntrySpeed(stall), 1.3 * Math.Max(1, stall)); }
        internal bool PatternActive { get { return MissedClimb || Pattern != null; } }
        /// <summary>GROK LAND5f (16:41 rollout swung -13/+17/-29 m): rollout heading correction capped and damped by the lateral drift rate.</summary>
        internal const double RolloutMaxCorr = 4, RolloutDampK = 1.0;
        internal static double RolloutCorr(double corrDeg, double crossRateMs, double speed) { return FlightPolicy.Clamp(corrDeg - RolloutDampK * Math.Atan2(crossRateMs, Math.Max(5, speed)) * 180 / Math.PI, -RolloutMaxCorr, RolloutMaxCorr); }
        double lastCrossR = double.NaN;
        internal int missedSteps, belowFloorSteps; bool goAroundLow, fastLogged; internal bool FastContinue, FlareFast, BelowFloor; internal double SpeedFloor = double.NaN; double slowPrevV = double.NaN, slowAcc; internal LockedPath Pattern; internal string PatternLog = ""; internal double BankCap = double.NaN;   // GROK LAND5b/5c
        internal bool MissedClimb; internal const double MissedAgl = 300, RejoinSlack = .85;   // GROK LAND3: rejoin corners need a 15% margin, never "shrunk"
        /// <summary>GROK LAND2: the route as the smoother flies it - from the plane's position, through the fixes, to the threshold - must be flyable
        /// at radius r (the 2 km "TIGHT 166 deg long final 6 km" rejoin turned into a figure-8 and a crash).</summary>
        internal static bool RouteFlyable(double lat, double lon, List<ApproachChart.Wp> route, double thrLat, double thrLon, double r, double radius, out string why)
        {
            double k = Math.PI / 180 * radius, cl = Math.Cos(lat * Math.PI / 180); var xy = new List<double[]> { new[] { 0.0, 0.0 } };
            foreach (var w in route) xy.Add(new[] { (w.Lon - lon) * k * cl, (w.Lat - lat) * k });
            xy.Add(new[] { (thrLon - lon) * k * cl, (thrLat - lat) * k });
            return DubinsPath.Flyable(xy, r, RejoinSlack, out why);
        }
        /// <summary>GROK LAND2: fly-out for a re-join (a racetrack on the plane's side, 2.5 turn radii off the centreline): abeam the plane, outbound
        /// parallel to the final to 3 r beyond the last fix, turn back ~140 deg, ~40 deg onto the final. At or above the glidepath / 500 m above the field.</summary>
        internal static List<ApproachChart.Wp> FlyOut(double thrLat, double thrLon, double course, ApproachChart.Wp last, double lat, double lon, double r, double radius, double alt)
        {
            double d = NavigationMath.Distance(thrLat, thrLon, last.Lat, last.Lon, radius) + 3 * r;
            double dp = NavigationMath.Distance(thrLat, thrLon, lat, lon, radius), th = FlightPolicy.Wrap(NavigationMath.Bearing(thrLat, thrLon, lat, lon) - (course + 180)) * Math.PI / 180;
            double behind = dp * Math.Cos(th), side = dp * Math.Sin(th) >= 0 ? 90 : -90, off = Math.Max(2.5 * r, Math.Abs(dp * Math.Sin(th)) + 1.5 * r);   // + = right of the outbound line; GROK LAND3: abeam leg >= 1.5 r from the plane (was TIGHT 90 deg)
            Func<double, string, ApproachChart.Wp> at = (dist, name) => { double ca, co, la, lo; NavigationMath.Offset(thrLat, thrLon, course + 180, dist, radius, out ca, out co); NavigationMath.Offset(ca, co, course + 180 + side, off, radius, out la, out lo); return new ApproachChart.Wp { Name = name, Lat = la, Lon = lo, Alt = alt }; };
            var o = new List<ApproachChart.Wp>();
            if (behind < d - 3 * r) o.Add(at(Math.Max(0, behind), "fly-out abeam"));
            o.Add(at(d, "fly-out " + Math.Round(d / 1000) + " km (rejoin)"));
            return o;
        }
        internal void GoAroundReset() { UnlockPath("go-around"); missedSteps = 0; goAroundLow = true; fastLogged = false; FastContinue = false; FlareFast = false; BelowFloor = false; belowFloorSteps = 0; Pattern = null; PatternLog = "go-around pattern: climb (straight ahead, wings level)"; MissedClimb = true; GateCooldown = GateCooldownSteps; PredictedNow = double.NaN; LocCoupled = GsCoupled = false; Phase = "entry"; Kind = ForceKind.Length > 0 ? ForceKind : "long"; FixDistance = IfDistance; Route = null; CrossI = 0; Crab = 0; lastAlong = double.NaN; }
        internal double PlanBankRaw, RouteToGo = double.NaN; double wpFromLat = double.NaN, wpFromLon;
        /// <summary>GROK FIX2: distance shown to the pilot - along the route to touchdown during entry, else to the threshold.</summary>
        internal double ToGo { get { return Phase == "entry" && !double.IsNaN(RouteToGo) ? RouteToGo : Distance; } }
        internal double PlanSpeed, PlanBank, PlanRadius, PlanG, TurnBank, LastStall = 45; internal List<ApproachChart.Wp> RawRoute;

        /// <summary>Fly-by leg steering: track the line prev->fix (cross-track, max 30 deg cut) and start the turn onto the next leg
        /// r*tan(dHdg/2) before the fix, r from the actual speed and bank. Returns the desired heading; advance = time to switch legs.</summary>
        /// <summary>(Re)build the flown route from the raw chart fixes for a join speed; keeps the current target when replanning.</summary>
        internal void Plan(double course, double radius, double speed, int keepIndex, double hereLat = double.NaN, double hereLon = double.NaN, string why = null)
        {
            PlanSpeed = speed; PlanG = ApproachProfile.LoadFactor(ApproachProfile.JoinG, speed, LastStall);
            PlanBankRaw = BankDeg > 20 ? BankDeg : ApproachProfile.SafeBank != null ? ApproachProfile.PhaseBank("entry", speed, LastStall) : ApproachProfile.BankForG(PlanG);   // explicit "bank 60" still wins
            PlanBank = FinalApproachPolicy.PlanningBank(PlanBankRaw);   // GROK FIX2: ...but never above the landing bank cap the command is clamped to
            PlanRadius = ApproachProfile.Radius(speed, PlanBank);
            ApproachChart.Wp target = keepIndex >= 0 && Route != null && keepIndex < Route.Count ? Route[keepIndex] : null;
            // smooth from the plane's own position so the first turn (onto the run-in) gets a lead/arc too, then drop that start point
            bool here = !double.IsNaN(hereLat) && keepIndex < 0;
            var src = here ? new List<ApproachChart.Wp>(RawRoute) : RawRoute; if (here) src.Insert(0, new ApproachChart.Wp { Name = "here", Lat = hereLat, Lon = hereLon, Alt = RawRoute[0].Alt });
            string sm; Route = ApproachChart.Smooth(src, course, Lat, Lon, speed, PlanBank, radius, out sm);
            if (here && Route.Count > 1) { RouteStartLat = hereLat; RouteStartLon = hereLon; Route.RemoveAt(0); }
            SmoothLog = (keepIndex >= 0 ? "replanned (" + (string.IsNullOrEmpty(why) ? "mass/speed change" : why) + "): " : "") + (string.IsNullOrEmpty(sm) && keepIndex >= 0 ? "route re-smoothed for r " + Math.Round(PlanRadius) + " m" : sm);   // GROK LAND4f: reason was empty
            if (target != null)
            {
                int best = 0; double bd = double.MaxValue;
                for (int i = 0; i < Route.Count; i++) { double d = NavigationMath.Distance(target.Lat, target.Lon, Route[i].Lat, Route[i].Lon, radius); if (d < bd) { bd = d; best = i; } }
                RouteIndex = best;
            }
        }

        /// <summary>Route rebuilds at most every ~20 s (Luke 6:34 PM: turned away from the runway it rebuilt 4x/s; the fresh
        /// route finished at once, the intercept bounced it back to entry, repeat). Inside the cooldown it keeps the route and
        /// flies out to the join point itself.</summary>
        internal const int RebuildCooldownSteps = 1000;
        internal static bool RebuildDue(int stepsSinceRebuild) { return stepsSinceRebuild >= RebuildCooldownSteps; }
        int stepsSinceRebuild = RebuildCooldownSteps; bool holdLast; internal int Rebuilds;
        internal double Warp = 1; double jumpLat = double.NaN, jumpLon; int warpHoldSteps;   // GROK LAND4f
        internal LockedPath Locked; string lockFix; int followLogN; internal string LockLog = "", FollowLog = "";   // GROK LAND5
        internal void UnlockPath(string why) { if (Locked == null) return; Locked = null; lockFix = null; dubType = null; LockLog = "path UNLOCK: " + why; }
        string dubType, dubFix; double dubR = double.NaN, dubTrk = double.NaN, dubCommitHdg = double.NaN, dubPlanBank; internal List<double[]> DubinsPts; internal double DubinsR; internal string DubinsLog = "";   // GROK JOIN1
        /// <summary>Distance flown since the fix became current before an 'overflown' skip (frame-rate independent): half the spacing, >= 150 m.</summary>
        internal static double BehindMinMove(double spacing) { return Math.Max(150, double.IsNaN(spacing) ? 300 : .5 * spacing); }
        /// <summary>GROK FIX2 (Oct 19: legs 1-12 of a 593 m-spaced arc consumed in 0.23 s): capture radius max(700, .3 r) but never more
        /// than 75% of the spacing to the previous route point; the overflown ('behind') skip only within 1 turn radius of the tracked
        /// fix and only after flying BehindMinMove since it became current (no frame-by-frame cascade). The caller advances at most one fix per update.</summary>
        /// <summary>GROK FIX6 (Oct 19 01:27-01:44: 17 min orbit 16 km west of KSC): after "not aligned before the FAF" the route
        /// holds its LAST point and only a 700 m pass captures it; at bank 15 (r ~16 km at 205 m/s) a pursuit never gets that
        /// close. Replan (fresh join from here) when the point is inside ~2 turn radii and more than 60 deg off our track.</summary>
        internal static bool HoldLastReplan(double dw, double bearingErr, double turnRadius) { return dw < 2 * turnRadius && Math.Abs(bearingErr) > 60; }
        internal static bool AdvanceWaypoint(bool leadTurn, double dw, double turnRadius, double spacing, bool behindUs, double movedM)
        {
            double capture = Math.Max(700, .3 * turnRadius);
            if (!double.IsNaN(spacing) && spacing > 0) capture = Math.Min(capture, .75 * spacing);
            return leadTurn || dw < capture || (behindUs && dw < turnRadius && movedM >= BehindMinMove(spacing));
        }
        internal static double LegSteer(double lat, double lon, double pLat, double pLon, double fLat, double fLon, double nextBrg, double speed, double bankDeg, double radius, out bool advance, out double xte)
        {
            double leg = NavigationMath.Bearing(pLat, pLon, fLat, fLon), len = NavigationMath.Distance(pLat, pLon, fLat, fLon, radius);
            double d = NavigationMath.Distance(pLat, pLon, lat, lon, radius), th = FlightPolicy.Wrap(NavigationMath.Bearing(pLat, pLon, lat, lon) - leg) * Math.PI / 180;
            xte = d * Math.Sin(th); double remaining = len - d * Math.Cos(th);
            double r = speed * speed / (9.81 * Math.Tan(Math.Max(5, bankDeg) * Math.PI / 180));
            double dh = double.IsNaN(nextBrg) ? 0 : Math.Abs(FlightPolicy.Wrap(nextBrg - leg));
            double lead = Math.Min(len, r * Math.Tan(Math.Min(120, dh) / 2 * Math.PI / 180) + (dh > 5 ? .5 * speed * ApproachProfile.RollIn : 0));   // + roll-in lag (learned)   // reversal (176 deg) no longer completes a leg instantly
            advance = remaining <= Math.Max(250, lead);
            return LocalizerCourse.Heading(leg, xte, r);   // every leg is a localizer course (same capture law as the LOC intercept)
        }


        /// <summary>Metres below the glideslope; NaN before the ILS is computed (Ils is null outside final: the 6:09 PM NRE).</summary>
        internal bool OffLine;
        internal static double BelowGsM(Ils.Reading r) { return r == null ? double.NaN : -r.AboveGsM; }
        internal double LatBank = double.NaN;   // GROK LAND3: direct bank command from the centreline capture (NaN = heading hold)
        internal double ArcBank; internal bool Heavy; internal const double GsMargin = 30;   // feed-forward bank on chart arcs (deg, + right)
        internal string ChartPin = "", ForceKind = "";   // GROK LAND2: picked chart file / picked final ("" = auto)
        /// <summary>GROK LAND2: final kind to fly: the picked LONG/SHORT; a picked chart without a final pick = long (its own join fixes).</summary>
        internal static string PickedKind(string final, string chartPin) { final = (final ?? "").Trim().ToLowerInvariant(); return final == "long" || final == "short" ? final : !string.IsNullOrEmpty(chartPin) ? "long" : ""; }
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
            LastStall = stall; LastStallEst = stall;
            // live bank limit for the autopilot = this phase's G rating at the speed we have now (joins 2 g, final 1.5 g)
            TurnBank = FinalApproachPolicy.PlanningBank(BankDeg > 20 ? BankDeg : ApproachProfile.PhaseBank(Phase, speed, stall));   // GROK FIX2: capped like the command
            stepsSinceRebuild++; if (GateCooldown > 0) GateCooldown--; LatBank = double.NaN;
            if (Phase == "entry")
            {
                if (Route != null && holdLast && RouteIndex >= Route.Count) RouteIndex = Route.Count - 1;
                if (Kind.Length == 0) { Kind = ForceKind.Length > 0 ? ForceKind : PilotPolicy.ApproachKind(track, course, -along, WantShort); FixDistance = Kind == "short" ? PilotPolicy.ShortFix : IfDistance; }
                if (Route != null && seenUserWp >= 0 && seenUserWp != UserWaypoints.Version) Route = null;   // user waypoints changed: replan the join (logged via JoinLog)
                if (Route == null)
                {
                    stepsSinceRebuild = 0; holdLast = false; Rebuilds++; finalJoinRoute = false;
                    Chart = ApproachChart.Build(Lat, Lon, course, Elevation, speed, BankDeg, radius, Terrain, LongAgl, ShortAgl); if (Override != null) Chart.Apply(Override);
                    Route = Chart.Route(lat, lon, track, Kind, radius, altitude); RouteIndex = 0; RouteStartLat = lat; RouteStartLon = lon; Chart.RouteFixD = Kind == "short" ? ApproachChart.ShortFix : Chart.FafD;
                    if (Kind != "short")
                    {   // join at the nearest safely-alignable fix/leg point for THIS plane; fall back to the computed long-final entry
                        double jv = Math.Max(ApproachProfile.EntrySpeed(stall), Math.Min(speed, ApproachProfile.MaxSpeed)), jr = BankDeg > 20 ? ApproachProfile.Radius(jv, BankDeg) : ApproachProfile.RadiusForG(jv, ApproachProfile.LoadFactor(ApproachProfile.JoinG, jv, stall)); string jw;
                        // Luke Oct 12 19:57: plan with the LEARNED numbers - turn radius at the bank the autopilot actually flies (learned envelope), learned sink, decel, app speed
                        // GROK JOIN1: ONE source of truth - the join is planned with the bank the controller will fly (learned envelope x0.85, capped 30)
                        // at the speed we have now (was the 30 deg chart design bank while the controller flew ~15)
                        double jvNow = NavigationMath.Distance(lat, lon, Lat, Lon, radius) > 30000 ? jv : Math.Max(jv, Math.Min(speed, ApproachProfile.MaxSpeed));   // far out: we slow to the entry speed before the join
                        double lb = BankDeg > 20 ? double.NaN : FinalApproachPolicy.PlanningBank(ApproachProfile.SafeBank != null ? ApproachProfile.PhaseBank("entry", jvNow, stall) : ChartDesign.Bank(jvNow));
                        if (!double.IsNaN(lb) && lb > 5) { jv = jvNow; jr = ApproachProfile.Radius(jv, lb); }
                        ApproachChart.PlanSink = LearnedSink; ApproachChart.PlanDecelA = EffDecel; ApproachChart.PlanApp = ApproachProfile.AppSpeed(stall);
                        ApproachChart.PlanLearned = "learned: r " + Math.Round(jr) + " m @ " + Math.Round(jv) + " m/s" + (double.IsNaN(lb) ? " (G rating)" : " (flown bank " + Math.Round(lb) + ")") + ", sink " + ApproachProfile.SinkLimit(LearnedSink, ApproachChart.MaxSink).ToString("0") + (double.IsNaN(LearnedSink) ? " (default)" : "") + ", decel " + EffDecel.ToString("0.00") + ", app " + Math.Round(ApproachChart.PlanApp);
                        double inb = NavigationMath.Bearing(lat, lon, Lat, Lon), cth = FlightPolicy.Wrap(NavigationMath.Bearing(Lat, Lon, lat, lon) - course) * Math.PI / 180;
                        double crs = NavigationMath.Distance(Lat, Lon, lat, lon, radius) * Math.Sin(cth);
                        EntryChart = EntryPolicy.Pick(inb, course, crs);
                        EntryLog = "chart " + Key + " " + EntryPolicy.Label(EntryChart) + " (inbound " + Math.Round((inb + 360) % 360) + " deg, " + Math.Round(EntryPolicy.Rel(inb, course)) + " deg off the runway heading; r " + Math.Round(Chart.TurnRadius) + " m)";
                        var join = Chart.BestJoin(lat, lon, track, altitude, jv, jr, radius, out jw, EntryChart);
                        jw = jw + " [" + EntryLog + "; localizer prediction uses roll-in lag only]";
                        if (join != null) { Route = join; Chart.RouteFixAlt = Chart.LongAlt; }
                        JoinLog = join != null ? jw : jw + "; long final entry";
                        bool userFirst = false; var uw = UserWaypoints.Get(Key); seenUserWp = UserWaypoints.Version;
                        if (uw.Count > 0)
                        {   // Oct 12 (2nd attempt): a WP Luke placed ahead on the MAP/CHART is flown first, then the chart join from there
                            string uwhy; var u = UserWaypoints.Pick(uw, lat, lon, track, Lat, Lon, radius, Math.Max(1500, .5 * jr), out uwhy);
                            if (u != null)
                            {
                                if (double.IsNaN(u.Alt) || u.Alt <= 0) u.Alt = Math.Max(Chart.LongAlt, Math.Min(altitude, Chart.LongAlt + 3000));
                                string jw2; var j2 = Chart.BestJoin(u.Lat, u.Lon, NavigationMath.Bearing(lat, lon, u.Lat, u.Lon), u.Alt, jv, jr, radius, out jw2, EntryChart);
                                Route = new List<ApproachChart.Wp> { u }; Route.AddRange(j2 ?? new List<ApproachChart.Wp> { Chart.Long }); Chart.RouteFixAlt = Chart.LongAlt; userFirst = true;
                                JoinLog = uwhy + ", then " + jw2;
                            }
                            else JoinLog = jw + "; " + uwhy;
                        }
                        if (!userFirst && !double.IsNaN(track))
                        {   // GROK LAND4: the shortest Dubins join onto the extended final (>= FAF) wins unless the chart route is about as short (03:28: 86.8 km vs 20.7 direct)
                            double k0 = Math.PI / 180 * radius, cl0 = Math.Cos(lat * Math.PI / 180), tx = (Lon - lon) * k0 * cl0, ty = (Lat - lat) * k0;
                            double fjR = PredictedPath.Radius(DubinsPath.JoinPlanSpeed(ApproachProfile.EntrySpeed(stall), speed), DubinsPath.MedianBank(TurnBank > 0 ? TurnBank : 25)), sinkOk = .7 * ApproachProfile.SinkLimit(LearnedSink, 15) / Math.Max(50, speed), fd, ft;
                            Func<double, double, bool> fok = (fdd, pl) => altitude - Chart.GlideAlt(fdd) <= pl * sinkOk; double dLo = Math.Max(Chart.FafD, FafDistance + 2000);
                            var fj = DubinsPath.FinalJoin(0, 0, track, tx, ty, course, fjR, Math.Max(dLo, ApproachChart.LongJoinM), Math.Max(dLo, ApproachChart.LongLineM), fok, out fd, out ft, false);   // GROK LAND5d: join at D14 or beyond (arc2 ends outside D14: long wings-level final)
                            if (fj == null || DubinsPath.TurnDeg(fj) > 270) fj = DubinsPath.FinalJoin(0, 0, track, tx, ty, course, fjR, Math.Max(dLo, ApproachChart.LongJoinM), 40000, fok, out fd, out ft);
                            if (fj == null) fj = DubinsPath.FinalJoin(0, 0, track, tx, ty, course, fjR, Math.Max(dLo, ApproachChart.LongJoinM), 40000, (a1, b1) => true, out fd, out ft);   // GROK LAND5f: D14 without the energy filter (the descent plan copes) before any FAF join
                            if (fj == null) fj = DubinsPath.FinalJoin(0, 0, track, tx, ty, course, fjR, dLo, Math.Max(dLo, ApproachChart.LongLineM), fok, out fd, out ft, false);   // fallback: the FAF-side join only when D14 is impossible
                            if (fj == null || DubinsPath.TurnDeg(fj) > 270) fj = DubinsPath.FinalJoin(0, 0, track, tx, ty, course, fjR, dLo, 40000, fok, out fd, out ft);
                            double cur = 0, pla = lat, plo = lon; if (Route != null) foreach (var w in Route) { cur += NavigationMath.Distance(pla, plo, w.Lat, w.Lon, radius); pla = w.Lat; plo = w.Lon; }
                            cur += NavigationMath.Distance(pla, plo, Lat, Lon, radius);
                            // GROK LAND4e: outbound past the FAF on the extended final = always take the Dubins teardrop join (chart join to the FAF behind is a figure-8)
                            bool outboundPast = Math.Abs(FlightPolicy.Wrap(track - (course + 180))) < 45 && -along > Math.Max(Chart.FafD, FafDistance) + 2000;
                            bool legMatch = Chart.JoinOnLeg && Route != null && Route.Count > 0;   // GROK LAND5d: a published leg lines up -> follow the chart; else the D14 centreline join
                            if (fj != null && !legMatch && (Route == null || Route.Count == 0 || ft < .9 * cur || fd >= ApproachChart.LongJoinM - 1 || outboundPast && !DubinsPath.FigureEight(fj) && DubinsPath.TurnDeg(fj) <= 270))
                            {
                                double pLa, pLo; NavigationMath.Offset(Lat, Lon, course + 180, fd, radius, out pLa, out pLo);
                                Route = new List<ApproachChart.Wp> { new ApproachChart.Wp { Name = "final join " + (fd / 1000).ToString("0.0") + " km", Lat = pLa, Lon = pLo, Alt = Math.Min(Math.Max(Chart.GlideAlt(fd), Elevation + 300), Math.Max(altitude, Chart.GlideAlt(ApproachChart.LongLineM))) } };   // GROK LAND4f: no 2.3 km climb to a 41 km join
                                Chart.RouteFixAlt = Chart.LongAlt; finalJoinRoute = true;
                                JoinLog = "DUBINS FINAL JOIN: " + fj.Type + " " + Math.Round(fj.A1) + "/" + Math.Round(fj.A2) + " deg onto the final at " + (fd / 1000).ToString("0.0") + " km, total " + (ft / 1000).ToString("0.0") + " km (chart route " + (cur / 1000).ToString("0.0") + " km, r " + Math.Round(fjR) + " m, plan bank " + Math.Round(DubinsPath.MedianBank(TurnBank > 0 ? TurnBank : 25)) + ")" + (outboundPast ? " [teardrop turnaround]" : "");
                            }
                            else JoinLog += fj == null ? " [no feasible Dubins final join]" : " [Dubins final join " + (ft / 1000).ToString("0.0") + " km not 10% shorter than the chart route]";
                        }
                    }
                    else JoinLog = "short final (head-on)";
                    if (!finalJoinRoute && Route != null && Route.Count > 0)
                    {   // GROK LAND2: tight-join rejection on EVERY (re)join - from the plane's own position, as the smoother will fly it
                        double fr = PredictedPath.Radius(Math.Max(ApproachProfile.EntrySpeed(stall), Math.Min(speed, ApproachProfile.MaxSpeed)), TurnBank > 0 ? TurnBank : 25); string fw, fw2;
                        if (!RouteFlyable(lat, lon, Route, Lat, Lon, fr, radius, out fw))
                        {
                            var proc = Kind == "short" ? null : Chart.Route(lat, lon, track, "long", radius, altitude);
                            if (proc != null && proc.Count > 0 && RouteFlyable(lat, lon, proc, Lat, Lon, fr, radius, out fw2) && proc[0].Name != Route[0].Name) { Route = proc; JoinLog += "; join not flyable from here (" + fw + ") - chart procedure from " + proc[0].Name; }
                            else
                            {
                                var last = Route[Route.Count - 1]; double dOut = NavigationMath.Distance(Lat, Lon, last.Lat, last.Lon, radius) + 3 * fr;
                                var fo = FlyOut(Lat, Lon, course, last, lat, lon, fr, radius, Math.Max(Math.Max(last.Alt, Chart.GlideAlt(dOut)), Elevation + 500));
                                Route = new List<ApproachChart.Wp>(fo); Route.Add(last); JoinLog += "; join not flyable from here (" + fw + ") - flying out to " + fo[fo.Count - 1].Name + ", then " + last.Name;
                            }
                        }
                    }
                    RawRoute = Route; Plan(course, radius, Math.Max(ApproachProfile.EntrySpeed(stall), Math.Min(speed, ApproachProfile.MaxSpeed)), -1, finalJoinRoute ? double.NaN : lat, finalJoinRoute ? double.NaN : lon);   // GROK LAND4: a Dubins final join is flown by the Dubins guidance alone (no smoother arc from here)
                    Why = Why.Length > 0 ? Why : ""; RouteLog = Chart.Describe(Route);
                }
                // fuel burn / mass change moved the craft's join speed: recompute arcs + lead points for THIS plane
                double nowV = Math.Max(ApproachProfile.EntrySpeed(stall), Math.Min(speed, ApproachProfile.MaxSpeed));   // turns are flown at the speed we actually have
                replanHold = RawRoute != null && ApproachProfile.NeedsReplan(PlanSpeed, nowV) ? replanHold + 1 : 0;   // no replans on transient speed: must persist ~20 s
                sinceReplan++;
                if (warpHoldSteps > 0) replanHold = 0;   // GROK LAND4f: no speed replans during warp / position jumps
                if (replanHold > 1000 && sinceReplan > 3000)
                {
                    replanHold = 0; sinceReplan = 0; double oldV = PlanSpeed;
                    if (DubinsPath.KeepCommitted(finalJoinRoute || dubType != null && dubType.Length == 3 && dubType != "LINE", dubR, speed, TurnBank > 0 ? TurnBank : 25))
                    { PlanSpeed = nowV; SmoothLog = "speed " + Math.Round(oldV) + " -> " + Math.Round(nowV) + " m/s: committed join " + (dubType ?? "route") + " kept (r " + Math.Round(dubR) + " m still flyable at bank " + Math.Round(TurnBank > 0 ? TurnBank : 25) + "), re-fit from here"; }
                    else Plan(course, radius, nowV, RouteIndex, double.NaN, double.NaN, "speed " + Math.Round(oldV) + " -> " + Math.Round(nowV) + " m/s");
                }   // and at most one replan per ~60 s (no churn)
                // join speed from the IAF onward = this craft's 1.5 x stall (Luke), so the planned arcs are what it flies
                bool joined = RouteIndex > 0 || (Route.Count > 0 && NavigationMath.Distance(lat, lon, Route[0].Lat, Route[0].Lon, radius) < 2 * speed * speed / (9.81 * Math.Tan(BankDeg * Math.PI / 180)));
                DesiredSpeed = Math.Min(ApproachProfile.EntrySpeed(stall), ApproachProfile.FinalSchedule(double.IsNaN(RouteToGo) ? double.MaxValue : RouteToGo, stall, EffDecel, Chart != null ? Chart.FafD : double.NaN));   // Oct 12: cruise speed on entry / join; GROK LAND5e: never above the early-decel schedule
                if (RouteIndex < Route.Count)
                {
                    var wp = Route[RouteIndex];
                    double dw = NavigationMath.Distance(lat, lon, wp.Lat, wp.Lon, radius);
                    { double togo = dw; for (int k = RouteIndex; k + 1 < Route.Count; k++) togo += NavigationMath.Distance(Route[k].Lat, Route[k].Lon, Route[k + 1].Lat, Route[k + 1].Lon, radius);
                      var lw = Route[Route.Count - 1]; RouteToGo = togo + NavigationMath.Distance(lw.Lat, lw.Lon, Lat, Lon, radius); }   // GROK FIX2: along-route distance to touchdown
                    if (double.IsNaN(wpFromLat)) { wpFromLat = lat; wpFromLon = lon; }
                    double pLat = RouteIndex > 0 ? Route[RouteIndex - 1].Lat : RouteStartLat, pLon = RouteIndex > 0 ? Route[RouteIndex - 1].Lon : RouteStartLon;
                    double nextBrg = RouteIndex + 1 < Route.Count ? NavigationMath.Bearing(wp.Lat, wp.Lon, Route[RouteIndex + 1].Lat, Route[RouteIndex + 1].Lon) : course;
                    bool leadTurn = false;
                    DesiredHeading = NavigationMath.Distance(pLat, pLon, wp.Lat, wp.Lon, radius) < 300 ? NavigationMath.Bearing(lat, lon, wp.Lat, wp.Lon)
                        : LegSteer(lat, lon, pLat, pLon, wp.Lat, wp.Lon, nextBrg, speed, TurnBank, radius, out leadTurn, out LegXte);
                    {   // arc feed-forward bank (curvature of the densified chart line), 1.5 s look-ahead
                        double legBrg = NavigationMath.Bearing(pLat, pLon, wp.Lat, wp.Lon), legLen = NavigationMath.Distance(pLat, pLon, wp.Lat, wp.Lon, radius);
                        double kB = 0, kN = 0;
                        if (RouteIndex > 1) { var a = Route[RouteIndex - 2]; kB = PathCurvature.AtVertex(NavigationMath.Bearing(a.Lat, a.Lon, pLat, pLon), legBrg, NavigationMath.Distance(a.Lat, a.Lon, pLat, pLon, radius), legLen); }
                        if (RouteIndex + 1 < Route.Count) { var n = Route[RouteIndex + 1]; kN = PathCurvature.AtVertex(legBrg, nextBrg, legLen, NavigationMath.Distance(wp.Lat, wp.Lon, n.Lat, n.Lon, radius)); }
                        ArcBank = PathCurvature.Bank(speed, PathCurvature.Ahead(kB, kN, dw, speed), TurnBank > 0 ? TurnBank : 30);
                    }
                    if (NavigationMath.Distance(pLat, pLon, wp.Lat, wp.Lon, radius) < 300) leadTurn = false;
                    DesiredAltitude = Chart != null ? ApproachProfile.EntryAlt(wp.Alt, Math.Max(double.IsNaN(Chart.RouteFixAlt) ? Chart.LongAlt : Chart.RouteFixAlt, Chart.GlideAlt(-along)), altitude) : wp.Alt;   // Oct 12: consistent with the intercept plan
                    DesiredVs = ApproachProfile.PathVs(DesiredAltitude - altitude, speed, Chart != null ? Chart.Slope : 0, ApproachProfile.SinkLimit(LearnedSink, 15), 15, speed - DesiredSpeed);   // GROK LAND2: path feed-forward (was P-only: +200 m lag)   // Oct 12: learned sink, not a fixed -10 (A300 600 m high at 10 km)
                    EnergyHigh = ApproachProfile.EnergyHigh(altitude - DesiredAltitude, speed, DesiredSpeed); EnergyLow = ApproachProfile.EnergyLow(altitude - DesiredAltitude); EnergyNote(altitude, speed, DesiredAltitude, RouteIndex);
                    OffLine = ApproachProfile.BadlyOff(LegXte, double.IsNaN(track) ? 0 : FlightPolicy.Wrap(DesiredHeading - track), false);
                    if (finalJoinRoute && Chart != null && Phase == "entry") DesiredAltitude = Math.Min(DesiredAltitude, Math.Max(altitude, Chart.GlideAlt(ApproachChart.LongLineM)));   // GROK LAND4f: never climb hugely for a far join
                    if (OffLine && DesiredAltitude < altitude) { DesiredAltitude = altitude; DesiredVs = 0; }   // Oct 12: no descent while badly off the route
                    bool last = RouteIndex == Route.Count - 1;
                    bool dubLeg = Pattern == null && !MissedClimb && (RouteIndex == 0 || holdLast && last) && !double.IsNaN(track), dubAligned = true;   // GROK LAND5c: no Dubins search while in the go-around pattern
                    if (dubLeg)
                    {   // GROK JOIN1: fly a Dubins turn-tangent-turn path that ARRIVES at the fix on its required course (the next leg / the final)
                        if (wp.Name != dubFix || double.IsNaN(dubR)) { dubPlanBank = DubinsPath.MedianBank(TurnBank > 0 ? TurnBank : 25); dubR = PredictedPath.Radius(DubinsPath.JoinPlanSpeed(ApproachProfile.EntrySpeed(LastStall), speed), dubPlanBank); }   // GROK LAND4d: radius locked per fix (plan speed, median bank)
                        double moved = double.IsNaN(jumpLat) ? 0 : NavigationMath.Distance(jumpLat, jumpLon, lat, lon, radius); jumpLat = lat; jumpLon = lon;
                        if (DubinsPath.Jumped(moved, speed, Warp)) { warpHoldSteps = 150; if (moved > 3 * Math.Max(30, speed)) dubTrk = track; } else if (warpHoldSteps > 0) warpHoldSteps--;
                        dubTrk = DubinsPath.FilterTrack(dubTrk, track, .03);   // GROK LAND3: plan from filtered state
                        double r = dubR, k = Math.PI / 180 * radius, cl = Math.Cos(lat * Math.PI / 180);
                        double x1 = (wp.Lon - lon) * k * cl, y1 = (wp.Lat - lat) * k;
                        if (wp.Name != dubFix) { dubFix = wp.Name; dubType = null; dubCommitHdg = double.NaN; }   // a new fix = a fresh choice
                        double ox = (lon - Lon) * k * cl, oy = (lat - Lat) * k;   // GROK LAND5: world frame = metres from the threshold
                        if (Locked != null && wp.Name != lockFix) UnlockPath("new join fix " + wp.Name);
                        if (Locked != null)
                        {
                            if (!Locked.Step(ox, oy, track, speed, dubPlanBank, TurnBank > 0 ? TurnBank : 25)) UnlockPath(Math.Round(Math.Abs(Locked.Xtrk)) + " m from the locked path");
                            else
                            {
                                if (Locked.Seg >= 3) { double lh; LatBank = LateralCapture.Bank(nextBrg, -x1 * Math.Cos(nextBrg * Math.PI / 180) + y1 * Math.Sin(nextBrg * Math.PI / 180), track, speed, TurnBank > 0 ? TurnBank : 25, out lh); DesiredHeading = lh; }
                                else { LatBank = Locked.BankAsk; DesiredHeading = Locked.DesiredTrack; }
                                ArcBank = 0; leadTurn = false; DubinsR = Locked.R;
                                if (++followLogN >= 500) { followLogN = 0; FollowLog = "path follow: seg " + (Locked.Seg + 1) + "/" + LockedPath.N + ", xtrk " + Math.Round(Locked.Xtrk) + " m, trk err " + Locked.TrkErr.ToString("0") + " deg, bank ask " + LatBank.ToString("0"); }
                                var lp = new List<double[]>(); foreach (var q in Locked.Points()) lp.Add(new[] { Lat + q[1] / k, Lon + q[0] / (k * cl) }); if (RouteIndex == Route.Count - 1) lp.Add(new[] { Lat, Lon }); DubinsPts = lp;
                            }
                        }
                        if (Locked == null)
                        {
                        DubinsPath dp; double cmd = DubinsPath.JoinSteer(0, 0, dubTrk, x1, y1, nextBrg, r, dubType, out dp, DubinsPath.Established(dubType, dubCommitHdg, dubTrk), warpHoldSteps > 0 && dubType != null);
                        if (dp != null)
                        {
                            if (dp.Type != dubType) { if (dubType == null || dubType.Length != 3 || dp.Type.Length != 3 || dubType[0] != dp.Type[0]) dubCommitHdg = dubTrk; dubType = dp.Type; DubinsLog = "join path to " + wp.Name + ": " + dp.Type + " " + Math.Round(dp.A1) + "/" + Math.Round(dp.A2) + " deg, straight " + (dp.S / 1000).ToString("0.0") + " km, r " + Math.Round(r) + " m at plan bank " + Math.Round(dubPlanBank) + " (max " + Math.Round(TurnBank) + "), arrives on " + Math.Round((nextBrg + 360) % 360) + " deg"; }
                            DesiredHeading = cmd; ArcBank = 0; leadTurn = false; DubinsR = r;
                            if (dp.Type != "LINE")
                            {   // GROK LAND4d: fly the committed circle with plan bank + reserve, not a saturated heading error
                                bool c1 = dp.A1 > 2, c2 = !c1 && dp.S <= Math.Max(50, .05 * dp.R) && dp.A2 > 2; double[] cc = c1 ? dp.C1 : c2 ? dp.C2 : null;
                                if (cc != null) LatBank = DubinsPath.ArcBank(c1 ? dp.D1 : dp.D2, Math.Sqrt(cc[0] * cc[0] + cc[1] * cc[1]), r, dubPlanBank, TurnBank > 0 ? TurnBank : 25);
                            }
                            if (dp.Type == "LINE") { double ux = Math.Sin(nextBrg * Math.PI / 180), uy = Math.Cos(nextBrg * Math.PI / 180), lh; LatBank = LateralCapture.Bank(nextBrg, -x1 * uy + y1 * ux, track, speed, TurnBank > 0 ? TurnBank : 25, out lh); DesiredHeading = lh; }   // GROK LAND3: on the course line into the fix - same capture law (overshot 1 km at 03:09:30)
                            var pts = new List<double[]>(); foreach (var q in dp.Points()) pts.Add(new[] { lat + q[1] / k, lon + q[0] / (k * cl) }); if (RouteIndex == Route.Count - 1) { pts.Add(new[] { wp.Lat, wp.Lon }); pts.Add(new[] { Lat, Lon }); } DubinsPts = pts;   // GROK LAND4f: the line continues along final to the threshold
                            if (dp.Type != "LINE" && warpHoldSteps == 0 && !MissedClimb) { Locked = LockedPath.From(dp, ox, oy); if (Locked != null) { lockFix = wp.Name; followLogN = 0; LockLog = "path LOCKED: " + Locked.Describe() + " to " + wp.Name; } }   // GROK LAND5: accept = lock
                        }
                        }
                        dubAligned = Math.Abs(FlightPolicy.Wrap(track - nextBrg)) < 30;   // never "capture" the join while crossing it on the wrong course
                    }
                    else { DubinsPts = null; dubType = null; dubR = dubTrk = dubCommitHdg = double.NaN; dubFix = null; if (Locked != null) UnlockPath("join leg done"); }
                    bool behindUs = !dubLeg && !double.IsNaN(track) && Math.Abs(FlightPolicy.Wrap(DesiredHeading - track)) > 100;   // overflown: never orbit a fix
                    if (holdLast && last)
                    {   // fly all the way out to the join point
                        if (!dubLeg) DesiredHeading = NavigationMath.Bearing(lat, lon, wp.Lat, wp.Lon);
                        if (dw < 700 && dubAligned) { holdLast = false; RouteIndex++; }
                        else if (!dubLeg && RebuildDue(stepsSinceRebuild) && HoldLastReplan(dw, double.IsNaN(track) ? 0 : FlightPolicy.Wrap(DesiredHeading - track), speed * speed / (9.81 * Math.Tan(Math.Max(5, TurnBank) * Math.PI / 180))))
                        { Route = null; holdLast = false; Kind = ""; JoinLog = "join point " + Math.Round(dw) + " m away is inside our turn circle (bank " + Math.Round(TurnBank) + "): replanning the join from here"; }   // GROK FIX6
                    }
                    else if (dubAligned && AdvanceWaypoint(leadTurn, dw, Chart.TurnRadius, RouteIndex > 0 ? NavigationMath.Distance(pLat, pLon, wp.Lat, wp.Lon, radius) : double.NaN, behindUs, NavigationMath.Distance(wpFromLat, wpFromLon, lat, lon, radius))) { RouteIndex++; wpFromLat = lat; wpFromLon = lon; }   // GROK FIX2: one fix per update
                }
                if (Math.Abs(LegXte) > 500 || (!double.IsNaN(track) && Math.Abs(FlightPolicy.Wrap(DesiredHeading - track)) > 30)) ArcBank = 0;   // GROK LAND4: arc feed-forward only ON the arc (kept banking right past the ask at 03:30)
                if (!MissedClimb && (Pattern == null || Pattern.Seg >= 2) && Route != null && -along > FafDistance + 1000 && LateralCapture.ShouldCapture(cross, double.IsNaN(track) ? double.NaN : FlightPolicy.Wrap(track - course), PredictedPath.Radius(speed, TurnBank > 0 ? TurnBank : 25), speed, TurnBank > 0 ? TurnBank : 25))
                { Phase = "intercept"; ArcBank = 0; Why = "course capture " + (-along / 1000).ToString("0.0") + " km out (" + Math.Round(cross) + " m, " + Math.Round(trackErr) + " deg)"; EnergyLog = "lateral: " + Why; }   // GROK LAND4: hand over to the LAND3a capture in any phase
                if (MissedClimb)
                {   // GROK LAND2/5b: missed approach - straight ahead WINGS LEVEL, climbing, no turns and no join plan for 10 s and until MissedAgl
                    missedSteps++;
                    if (altitude - Elevation >= MissedAgl && missedSteps >= 500)
                    {   // GROK LAND5c item 8: traffic pattern instead of a Dubins/chart re-join
                        MissedClimb = false; missedSteps = 0; double kP = Math.PI / 180 * radius, clP = Math.Cos(lat * Math.PI / 180);
                        double rP = PredictedPath.Radius(PatternSpeed(stall), DubinsPath.MedianBank(TurnBank > 0 ? TurnBank : 25));   // GROK LAND5d: radius at the pattern TARGET speed
                        Pattern = GoAroundPattern.Build((lon - Lon) * kP * clP, (lat - Lat) * kP, course, rP, Chart != null ? Chart.FafD : FafDistance);
                        Why = "missed approach climb done; pattern"; PatternLog = "go-around pattern: downwind - " + Pattern.Describe() + ", join final " + (GoAroundPattern.JoinDistance(Chart != null ? Chart.FafD : FafDistance) / 1000).ToString("0.0") + " km";
                    }
                    else { DesiredHeading = double.IsNaN(track) ? course : track; ArcBank = 0; LatBank = 0; Locked = null; DubinsPts = null; DesiredAltitude = Math.Max(DesiredAltitude, Elevation + MissedAgl + 100); DesiredVs = 10; DesiredSpeed = PatternSpeed(stall); }   // GROK LAND5d: capped climb speed = approach entry speed
                }
                else if (Pattern != null)
                {   // GROK LAND5c item 8: fly the locked pattern (climb -> 180 -> downwind -> 180 -> final); hold the pattern altitude
                    double kP = Math.PI / 180 * radius, clP = Math.Cos(lat * Math.PI / 180); int segB = Pattern.Seg;
                    bool ok = !double.IsNaN(track) && Pattern.Step((lon - Lon) * kP * clP, (lat - Lat) * kP, track, speed, DubinsPath.MedianBank(TurnBank > 0 ? TurnBank : 25), altitude - Elevation < 500 ? 20 : (TurnBank > 0 ? TurnBank : 25));
                    if (Pattern.Seg != segB && Pattern.Seg == 2) PatternLog = "go-around pattern: base-final (180 back onto final)";
                    if (!ok || Pattern.Seg >= 3) { PatternLog = ok ? "go-around pattern: on final - glidepath (hold altitude until it meets us)" : "go-around pattern: off the pattern by " + Math.Round(Math.Abs(Pattern.Xtrk)) + " m - capturing final"; Pattern = null; Phase = "intercept"; ArcBank = 0; }
                    else
                    {
                        LatBank = Pattern.BankAsk; DesiredHeading = Pattern.DesiredTrack; ArcBank = 0; DubinsR = Pattern.R; DesiredSpeed = PatternSpeed(stall);
                        DesiredAltitude = Math.Max(Elevation + MissedAgl + 100, Math.Min(altitude, Chart != null ? Chart.GlideAlt(GoAroundPattern.JoinDistance(Chart.FafD)) : altitude)); DesiredVs = FlightPolicy.Clamp((DesiredAltitude - altitude) / 20, -5, 5);
                        var lp = new List<double[]>(); foreach (var q in Pattern.Points()) lp.Add(new[] { Lat + q[1] / kP, Lon + q[0] / (kP * clP) }); lp.Add(new[] { Lat, Lon }); DubinsPts = lp;
                    }
                }
                BankCap = goAroundLow && altitude - Elevation < 500 ? 20 : double.NaN; if (goAroundLow && altitude - Elevation >= 500) goAroundLow = false;   // GROK LAND5b: <= 20 deg bank below 500 m after a go-around
                if (Route != null && RouteIndex >= Route.Count) { Phase = "intercept"; ArcBank = 0; }   // Route == null: replan requested (GROK FIX6), rebuilt next step
            }
            if (Phase == "intercept")
            {
                double lead = Math.Max(1500, .6 * Math.Min(speed, 150) * Math.Min(speed, 150) / (9.81 * Math.Tan(20 * Math.PI / 180)));   // ~1 turn radius lead: converges without overshoot
                double icR = speed * speed / (9.81 * Math.Tan(Math.Max(5, TurnBank > 0 ? TurnBank : 25) * Math.PI / 180)), icTrk = double.IsNaN(track) ? heading : track;
                bool merging = !double.IsNaN(icTrk) && Math.Abs(FlightPolicy.Wrap(icTrk - course)) < 45 && Math.Abs(cross) < 2 * icR;   // Oct 12: lead-turn merge (learned roll-in) once closing at an intercept angle; steep side entries keep the plain law (90 deg replay)
                { double ch; LatBank = LateralCapture.Bank(course, cross, icTrk, speed, TurnBank > 0 ? TurnBank : 25, out ch); DesiredHeading = ch; }   // GROK LAND3: bank-commanded capture onto the runway centreline (was heading-only: flew parallel 1.4 km off)
                DesiredAltitude = Chart != null ? Math.Max(double.IsNaN(Chart.RouteFixAlt) ? Chart.LongAlt : Chart.RouteFixAlt, Chart.GlideAlt(-along)) : Elevation + 800;   // hold the fix altitude until aligned
                DesiredSpeed = Math.Min(ApproachProfile.EntrySpeed(stall), ApproachProfile.FinalSchedule(Distance, stall, EffDecel, Chart != null ? Chart.FafD : double.NaN));   // intercept: join speed, never above the final schedule
                DesiredVs = ApproachProfile.PathVs(DesiredAltitude - altitude, speed, Chart != null ? Chart.Slope : 0, ApproachProfile.SinkLimit(LearnedSink, 15), 10, speed - DesiredSpeed);   // GROK LAND2
                EnergyHigh = ApproachProfile.EnergyHigh(altitude - DesiredAltitude, speed, DesiredSpeed); EnergyLow = ApproachProfile.EnergyLow(altitude - DesiredAltitude); EnergyNote(altitude, speed, DesiredAltitude, -1);
                if (Math.Abs(cross) < AlignCross && trackErr < AlignHeading) Phase = "final";
                else if (-along < (Kind == "short" ? 2000 : FafDistance) && (Math.Abs(cross) > 1000 || trackErr > 30 || -along < GateDistance + 500)) { Phase = "go around"; Why = "not aligned " + (-along / 1000).ToString("0.0") + " km out (" + Math.Abs(cross).ToString("0") + " m, " + trackErr.ToString("0") + " deg off)"; }   // GROK LAND2: close-in failed intercept = go around (climb, fly out, re-join), never a tight re-join   // never descend unaligned
            }
            if (Phase == "final" || Phase == "flare")
            {
                // Proportional + integral (per metre flown) centerline tracking: removes steady offsets (sideslip/wind/trim) to < 5 m
                double ds = double.IsNaN(lastAlong) ? 0 : Math.Min(50, Math.Abs(along - lastAlong)); lastAlong = along;
                // Luke: coupled ILS. The SAME localizer/glideslope deviations the ILS tab shows feed the controllers: capture LOC, then GS.
                Ils = KSPChatBridge.Ils.Compute(lat, lon, altitude, Lat, Lon, EndLat, EndLon, Elevation, radius, TouchdownM, FinalSlope);
                if (!LocCoupled && Math.Abs(Ils.LocDeg) < KSPChatBridge.Ils.LocFullScale && Ils.Front && -along < LocRange) LocCoupled = true;
                if (LocCoupled && !GsCoupled && Math.Abs(Ils.GsDeg) < KSPChatBridge.Ils.GsFullScale) GsCoupled = true;
                double latDev = LocCoupled ? Ils.CrossM : cross;
                DesiredHeading = CenterlineHeading(latDev, track, course, speed, ref CrossI, ds, heading, ref Crab, Distance < ApproachChart.ShortFix, FinalApproachPolicy.LookAheadM);
                DesiredAltitude = GsCoupled ? altitude - Ils.AboveGsM : Chart != null ? Math.Max(Chart.GlideAlt(-along + TouchdownM), altitude - Ils.AboveGsM) : Elevation + Math.Max(3, (TouchdownM - along) * Math.Tan(3 * Math.PI / 180));   // before GS capture: never below the ILS path
                double hat = altitude - Elevation, low = Math.Min(hat, agl);
                OffLine = hat > 60 && ApproachProfile.BadlyOff(latDev, trackErr, true);
                if (OffLine) DesiredAltitude = Math.Max(DesiredAltitude, altitude);   // Oct 12: hold altitude until re-established on the line (A300 nearly hit the water off the line)   // Luke: glide path/flare vs the RUNWAY THRESHOLD, terrain under the plane only for clearance
                DesiredSpeed = hat < 30 ? ApproachProfile.Vref(stall) : ApproachProfile.FinalSchedule(Distance, stall, EffDecel, Chart != null ? Chart.FafD : double.NaN);   // Luke: ~2.3x stall 12->4 km, smooth decel to 1.35x by 2 km
                double maxSink = Math.Max(4.5, 2 * speed * Math.Max(Chart != null ? Chart.Slope : .052, .052));   // shallow GS capture: at most ~2x path sink, no diving
                if (speed > DesiredSpeed + 10) maxSink = Math.Min(maxSink, Math.Max(4.5, speed * Math.Max(Chart != null ? Chart.Slope : .052, .052)));   // fast: never trade height for more speed
                if (Heavy) maxSink = Math.Min(maxSink, Math.Max(4.5, 1.3 * speed * Math.Max(Chart != null ? Chart.Slope : .052, .052)));   // heavy: gentler GS capture
                double above = altitude - DesiredAltitude;
                if (above > 30 && hat > 60) maxSink = Math.Max(maxSink, ApproachProfile.SinkLimit(LearnedSink, 15));   // Luke Oct 12: ALTITUDE over speed - high = full learned sink even if fast (idle+brakes/flaps bleed speed; overspeed protection unchanged)   // Oct 12: high on final -> steeper within the learned sink (was capped at the path rate while fast: never got down)
                EnergyHigh = hat > 60 && ApproachProfile.EnergyHigh(above, speed, DesiredSpeed); EnergyLow = ApproachProfile.EnergyLow(above); EnergyNote(altitude, speed, DesiredAltitude, -2);
                bool belowGs = Ils.AboveGsM < -GsMargin && Distance > ApproachChart.ShortFix;   // never below the GS minus a margin before short final
                double targetAlt = FinalApproachPolicy.TargetAltitude(Elevation, along, TouchdownM, FinalSlope);
                double pointVs = FinalApproachPolicy.VerticalSpeed(targetAlt, altitude, speed, latDev);
                bool outsideFaf = Distance > (Chart != null ? Chart.FafD : FafDistance); if (outsideFaf && targetAlt > altitude) belowGs = true;   // GROK LAND5b: below the path outside the FAF = hold altitude until it meets us
                DesiredVs = FlightPolicy.Clamp(OffLine ? Math.Max(0, pointVs) : pointVs, belowGs ? 0 : -maxSink, belowGs ? (outsideFaf ? 0 : 10) : 3);
                Gear = Distance <= FlapsPolicy.GearDistance(Chart != null ? Chart.FafD : FafDistance) || (Distance < 3000 && Math.Abs(cross) < AlignCross) || low < 80;   // GROK LAND5b: gear at/before the FAF (was 3 km)
                double fs = Override != null ? Override.FlareStartM : 15;
                
                if (low < fs) { if (Phase != "flare") FlareFast = ApproachProfile.FlareFast(speed, AppNow); Phase = "flare"; double ts = FlightPolicy.Clamp(Override != null ? Override.FlareSinkMs : ApproachProfile.TouchdownSink, 1, 5); DesiredVs = -ts; }   // GROK LAND5c: touchdown sink 3 m/s (was 1.5-1.9); fast = no flare pitch-up
                // Oct 12: predictive soft gate - learned decel + flap steps still to come + airbrakes, to touchdown; no learned decel -> never a speed go-around
                double predSpd; bool predFast = ApproachProfile.PredictTooFast(speed, Distance, stall, LearnedDecel, FlapStepsLeft, HasAirbrakes, FastAtGate, out predSpd);
                PredictedNow = predSpd; AppNow = ApproachProfile.AppSpeed(stall);
                int sg = ApproachProfile.SpeedGate(ApproachProfile.GateSpeed(predSpd, speed, Distance), AppNow, double.NaN, double.NaN); predFast = sg == 2; FastContinue = Phase == "final" && sg == 1 && Distance < ApproachChart.ShortFix;   // GROK LAND5c: no stopping estimate yet -> plain +20
                if (!FastContinue) fastLogged = false; if (FastContinue && !fastLogged) { fastLogged = true; Why = "fast: " + predSpd.ToString("0") + " m/s predicted at touchdown (landing " + AppNow.ToString("0") + ", <= +20): airbrakes + idle, continuing"; }
                {   // GROK LAND5c item 5: shrinking speed floor; slow above it is fine; below it power, and go around only if it cannot recover
                    bool overRwy = along >= 0 && along <= NavigationMath.Distance(Lat, Lon, EndLat, EndLon, radius) && hat < 10; double fa = double.IsNaN(slowPrevV) ? 0 : (speed - slowPrevV) * 50; slowPrevV = speed; slowAcc += (fa - slowAcc) * .02;
                    int sl = overRwy ? 0 : ApproachProfile.SlowGate(speed, stall, Distance, Chart != null ? Chart.FafD : FafDistance, belowFloorSteps / 50.0, slowAcc);
                    BelowFloor = sl > 0; belowFloorSteps = sl > 0 ? belowFloorSteps + 1 : 0; SpeedFloor = ApproachProfile.FloorK(Distance, Chart != null ? Chart.FafD : FafDistance) * stall;
                    if (sl == 2 && GateCooldown <= 0 && Phase == "final" && hat > 10) { Phase = "go around"; Why = "too slow: " + speed.ToString("0") + " m/s below the floor " + SpeedFloor.ToString("0") + " for " + ApproachProfile.FloorRecoverS + " s, not recovering"; }
                }
                if (GateCooldown <= 0 && Phase == "final" && Distance < ApproachChart.ShortFix && Distance > 100 && predFast) { Phase = "go around"; Why = "too fast: " + predSpd.ToString("0") + " m/s predicted at touchdown (landing " + AppNow.ToString("0") + ")"; }
                if ((GateCooldown <= 0 || hat < 60) && (Phase == "final" || Phase == "flare") && TouchdownM - along <= LineupGateM && TouchdownM - along > -500 && (Math.Abs(cross) > GateCross || trackErr > GateHeading)) { Phase = "go around"; Why = "not lined up " + LineupGateM + " m before touchdown (" + Math.Abs(cross).ToString("0") + " m, " + trackErr.ToString("0") + " deg)"; }
                double daHat = Override != null ? Override.DaHat(Elevation) : double.NaN;   // GROK LAND2: plate DA - still well high there = go around
                if (Phase == "final" && GateCooldown <= 0 && daHat > 20 && hat < daHat && hat > daHat - 40 && above > 60) { Phase = "go around"; Why = "high at DA " + daHat.ToString("0") + " m (" + above.ToString("0") + " m above the glidepath)"; }
                // A missed threshold at height gets a go-around, never a dive back.
                double length = NavigationMath.Distance(Lat, Lon, EndLat, EndLon, radius);
                if (!grounded && along > length - 200 && hat > 3) { Phase = "go around"; Why = "overflew the runway (" + hat.ToString("0") + " m above threshold, " + speed.ToString("0") + " m/s)"; }
                if (grounded) Phase = "rollout";
            }
            if (Phase == "rollout") { double dsr = double.IsNaN(lastAlong) ? 0 : Math.Min(50, Math.Abs(along - lastAlong)); lastAlong = along; DesiredHeading = CenterlineHeading(cross, track, course, Math.Max(5, speed), ref CrossI, dsr, heading, ref Crab); { double dtR = dsr / Math.Max(1, speed), cr = double.IsNaN(lastCrossR) || dtR <= 0 ? 0 : (cross - lastCrossR) / dtR; lastCrossR = cross; DesiredHeading = DubinsPath.Norm(course + RolloutCorr(FlightPolicy.Wrap(DesiredHeading - course), cr, speed)); }   // GROK LAND5f: damped rollout steering
                    DesiredSpeed = 0; Gear = Brakes = true; if (speed < 1) Phase = "stopped"; }
        }
    }
}
