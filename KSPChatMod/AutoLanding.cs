// AUTO1: automatic landing defaults for any plane (Luke: land well WITHOUT hand fine-tuning). Pure math, tested offline against the
// five hand-tuned planes (Aeris 3A / 4A, Albatross 3, Thunderbird, Stearwing A300) and the all-default Stratolauncher. Used only for
// craft settings that are blank; every value carries a one-line reason for the log.
using System;
using System.Globalization;

namespace KSPChatBridge
{
    internal sealed class AutoLanding
    {
        internal const double G = 9.81, Rho = 1.225, AreaPerCoeff = 3.52, ClMax = 1.6;
        /// <summary>Landing speed = the speed where the plane holds level flight at TargetAoa (the flare can only add ~6 deg of pitch), never
        /// below VrefK x stall. Without an in-flight lift measurement: FallbackK x the wing-loading stall estimate.</summary>
        internal const double TargetAoa = 5, VrefK = 1.15, FallbackK = 1.4, ClPerDegMin = .03, ClPerDegMax = .6;
        /// <summary>Approach = AppK x landing (Luke's planes 1.13-1.69, median 1.4), at least landing + AppMinOver, capped where drag at the
        /// approach speed stays under ThrustUse x the available thrust on a GlideDeg glidepath (Thunderbird could not hold 175).</summary>
        internal const double AppK = 1.4, AppMinOver = 10, ThrustUse = .8, GlideDeg = 3;
        /// <summary>Flare start = FlareTauS x glidepath sink (landing x tan 3 deg), 5-15 m; flare sink 0.5 (4 of 5 tuned); max flare pitch
        /// 6.5 (tuned 6-7.5); aim 200 m, shorter (>= 100) when the stop distance at BrakeDecel leaves < StopMargin of runway.</summary>
        internal const double FlareTauS = 1.5, FlareMin = 5, FlareMax = 15, FlareSinkDef = .5, MaxPitchDef = 6.5, AimDef = 200, AimMin = 100, BrakeDecel = 2.5, StopMargin = 300;
        /// <summary>Flare throttle: FlareThrK x the learned final-approach throttle when the engines spool slower than SpoolSlowS, else idle.</summary>
        internal const double SpoolSlowS = 2, FlareThrK = .25;
        /// <summary>Float detection: a flare longer than FloatAfterS adds FloatRate m/s of sink per second (max FloatMax) - Stratolauncher floated 9 s.</summary>
        internal const double FloatAfterS = 5, FloatRate = .5, FloatMax = 3;

        internal sealed class In
        {
            internal double MassT, LiftCoeff, Stall = double.NaN, ClPerDeg = double.NaN, ThrustKN = double.NaN, DragK = double.NaN, SpoolS = double.NaN, FinalTrim = double.NaN, RunwayM = double.NaN;
        }
        internal double Landing = double.NaN, Approach = double.NaN, FlareStart = double.NaN, FlareSink = FlareSinkDef, FlareMaxPitch = MaxPitchDef, Aim = AimDef, FlareThrottlePct = 0, VsEst = double.NaN, V5 = double.NaN, ThrustCapMs = double.NaN;
        internal string LandingWhy = "", ApproachWhy = "", FlareWhy = "", AimWhy = "", ThrWhy = "";

        static bool Ok(double v) { return !double.IsNaN(v) && !double.IsInfinity(v) && v > 0; }
        static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }
        static string F(double v, string f = "0") { return v.ToString(f, CultureInfo.InvariantCulture); }
        internal static double Area(double liftCoeff) { return liftCoeff * AreaPerCoeff; }
        /// <summary>Stall from wing loading: sqrt(2 m g / (rho S CLmax)), S = 3.52 x summed deflectionLiftCoeff.</summary>
        internal static double StallEstimate(double massT, double liftCoeff) { double s = Area(liftCoeff); return Ok(s) && Ok(massT) ? Math.Sqrt(2 * massT * 1000 * G / (Rho * s * ClMax)) : double.NaN; }
        /// <summary>Lift slope (CL per deg AoA) from one steady sample: lift = W cos(gamma) / cos(bank).</summary>
        internal static double ClPerDegFrom(double massT, double speed, double rho, double aoaDeg, double gammaDeg, double bankDeg, double liftCoeff)
        {
            double s = Area(liftCoeff); if (!Ok(s) || !Ok(massT) || !Ok(speed) || !Ok(rho) || !(aoaDeg >= 1.5) || Math.Abs(bankDeg) > 10) return double.NaN;
            double lift = massT * 1000 * G * Math.Cos(gammaDeg * Math.PI / 180) / Math.Cos(bankDeg * Math.PI / 180);
            return lift / (.5 * rho * speed * speed * s) / aoaDeg;
        }
        /// <summary>Sea-level speed for level flight at aoaDeg with the given lift slope.</summary>
        internal static double SpeedAtAoa(double massT, double liftCoeff, double clPerDeg, double aoaDeg) { double s = Area(liftCoeff); return Ok(s) && Ok(massT) && Ok(clPerDeg) && Ok(aoaDeg) ? Math.Sqrt(2 * massT * 1000 * G / (Rho * s * clPerDeg * aoaDeg)) : double.NaN; }
        /// <summary>Highest speed whose drag (dragK x v^2, kN) the engines hold on the glidepath at ThrustUse of the available thrust.</summary>
        internal static double ThrustCap(double massT, double thrustKN, double dragK)
        {
            if (!Ok(thrustKN) || !Ok(dragK) || !Ok(massT)) return double.NaN;
            return Math.Sqrt((ThrustUse * thrustKN + massT * G * Math.Sin(GlideDeg * Math.PI / 180)) / dragK);
        }
        internal static double FloatSink(double baseSink, double flareS) { double b = double.IsNaN(baseSink) ? FlareSinkDef : baseSink; return double.IsNaN(flareS) ? b : Clamp(b + Math.Max(0, flareS - FloatAfterS) * FloatRate, b, Math.Max(b, FloatMax)); }

        internal static AutoLanding Compute(In x)
        {
            var a = new AutoLanding(); a.VsEst = StallEstimate(x.MassT, x.LiftCoeff);
            double vStall = Ok(x.Stall) ? VrefK * x.Stall : double.NaN;
            bool cl = Ok(x.ClPerDeg) && x.ClPerDeg >= ClPerDegMin && x.ClPerDeg <= ClPerDegMax;
            if (cl) a.V5 = SpeedAtAoa(x.MassT, x.LiftCoeff, x.ClPerDeg, TargetAoa);
            double vLift = cl ? a.V5 : Ok(a.VsEst) ? FallbackK * a.VsEst : double.NaN;
            string liftWhy = cl ? "level at " + F(TargetAoa) + " deg AoA (lift measured " + F(x.ClPerDeg, "0.000") + "/deg)" : "1.4 x wing-loading stall " + F(a.VsEst) + " (no lift measurement yet)";
            if (Ok(vLift) && (!Ok(vStall) || vLift >= vStall)) { a.Landing = vLift; a.LandingWhy = liftWhy; }
            else if (Ok(vStall)) { a.Landing = vStall; a.LandingWhy = "1.15 x stall " + F(x.Stall); }
            if (!Ok(a.Landing)) { a.LandingWhy = "no wings / no stall - autopilot defaults"; return a; }
            a.Approach = Math.Max(AppK * a.Landing, a.Landing + AppMinOver); a.ApproachWhy = "1.4 x landing";
            a.ThrustCapMs = ThrustCap(x.MassT, x.ThrustKN, x.DragK);
            if (Ok(a.ThrustCapMs) && a.ThrustCapMs < a.Approach)
            {
                a.Approach = Math.Max(a.Landing + AppMinOver, a.ThrustCapMs);
                a.ApproachWhy = a.ThrustCapMs < a.Landing + AppMinOver ? "thrust holds only " + F(a.ThrustCapMs) + " m/s - landing + 10 (marginal!)" : "thrust cap: drag at " + F(a.ThrustCapMs) + " m/s = 80% of " + F(x.ThrustKN) + " kN";
            }
            double gs = a.Landing * Math.Tan(GlideDeg * Math.PI / 180);
            a.FlareStart = Clamp(FlareTauS * gs, FlareMin, FlareMax); a.FlareWhy = "1.5 s x glidepath sink " + F(gs, "0.0") + " m/s";
            if (Ok(x.RunwayM)) { double stop = a.Landing * a.Landing / (2 * BrakeDecel); a.Aim = Clamp(x.RunwayM - stop - StopMargin, AimMin, AimDef); a.AimWhy = "stop " + F(stop) + " m on " + F(x.RunwayM) + " m runway"; }
            else a.AimWhy = "default (runway length unknown)";
            if (Ok(x.SpoolS) && x.SpoolS > SpoolSlowS && Ok(x.FinalTrim)) { a.FlareThrottlePct = Math.Round(FlareThrK * Clamp(x.FinalTrim, 0, 1) * 100); a.ThrWhy = "slow spool " + F(x.SpoolS, "0.0") + " s: 25% of final trim " + F(100 * x.FinalTrim) + "%"; }
            else a.ThrWhy = "idle";
            return a;
        }
        internal string Summary(string craft)
        {
            return "auto " + craft + ": landing " + F(Landing) + " m/s (" + LandingWhy + "), approach " + F(Approach) + " (" + ApproachWhy + "), flare at " + F(FlareStart, "0.0") + " m (" + FlareWhy + "), flare sink " + F(FlareSink, "0.0") + ", max flare pitch " + F(FlareMaxPitch, "0.0") + ", aim " + F(Aim) + " m (" + AimWhy + "), flare throttle " + F(FlareThrottlePct) + "% (" + ThrWhy + ")";
        }
    }

    /// <summary>AUTO1: in-flight lift slope - steady, wings-level samples on the way in (EMA); valid after 4 s of samples.</summary>
    internal sealed class LiftMeter
    {
        internal double ClPerDeg = double.NaN, SampleS; internal const double ValidS = 4, Tau = 3;
        internal bool Valid { get { return SampleS >= ValidS && !double.IsNaN(ClPerDeg); } }
        internal void Add(double clPerDeg, double dt)
        {
            if (double.IsNaN(clPerDeg) || clPerDeg < AutoLanding.ClPerDegMin || clPerDeg > AutoLanding.ClPerDegMax || !(dt > 0)) return;
            ClPerDeg = double.IsNaN(ClPerDeg) ? clPerDeg : ClPerDeg + Math.Min(1, dt / Tau) * (clPerDeg - ClPerDeg); SampleS += dt;
        }
    }
    /// <summary>AUTO1: drag (kN per (m/s)^2) from thrust now - m (a + g sin gamma), and available thrust = thrust now / throttle (throttle >= 0.3).</summary>
    internal sealed class DragThrustMeter
    {
        internal double DragK = double.NaN, ThrustKN = double.NaN, DragS, ThrustS; internal const double ValidS = 4, Tau = 3;
        internal bool DragValid { get { return DragS >= ValidS && !double.IsNaN(DragK); } }
        internal bool ThrustValid { get { return ThrustS >= ValidS && !double.IsNaN(ThrustKN); } }
        internal static double Drag(double thrustNowKN, double massT, double accel, double gammaDeg) { return thrustNowKN - massT * (accel + AutoLanding.G * Math.Sin(gammaDeg * Math.PI / 180)); }
        internal void Add(double thrustNowKN, double throttle, double massT, double accel, double gammaDeg, double speed, double dt)
        {
            if (!(dt > 0) || !(speed > 30) || double.IsNaN(thrustNowKN) || double.IsNaN(accel)) return;
            double d = Drag(thrustNowKN, massT, accel, gammaDeg) / (speed * speed);
            if (d > 0) { DragK = double.IsNaN(DragK) ? d : DragK + Math.Min(1, dt / Tau) * (d - DragK); DragS += dt; }
            if (throttle >= .3 && thrustNowKN > 0) { double t = thrustNowKN / throttle; ThrustKN = double.IsNaN(ThrustKN) ? t : ThrustKN + Math.Min(1, dt / Tau) * (t - ThrustKN); ThrustS += dt; }
        }
    }
}