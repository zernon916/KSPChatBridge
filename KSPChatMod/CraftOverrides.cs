// GROK TAB-CRAFT1: per-craft speed overrides (native_settings.json craft_overrides.<craft name>) - pure model shared by the
// autopilot (NativeFlightController.ApplyCraftOverrides) and the tablet AUTOPILOT / CRAFT / DETAILS page. Tested offline.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KSPChatBridge
{
    internal static class CraftOverrides
    {
        internal const string Table = "craft_overrides", Approach = "approach_speed", Threshold = "threshold_speed", Stall = "stall_override";
        internal const string AutoGa = "auto_go_around", Vr = "vr", RotatePitch = "rotate_pitch_deg", FlareStart = "flare_start_m", FlareThrottle = "flare_throttle_pct", FlareSink = "flare_sink", FlareMaxPitch = "flare_max_pitch_deg", TouchdownAim = "touchdown_aim_m", RolloutPitch = "rollout_pitch_deg", FlapDeg = "flap_deg", FlareSpoolLead = "flare_spool_lead_s";   /* FLAP2 */
        internal sealed class Def
        {
            internal string Key, Label, Group, Unit; internal double Small = 1, Large = 5, Min = 15, Max = 400, Default = double.NaN; internal bool Bool;
        }
        static Def D(string g, string k, string l, string u, double sm, double lg, double mn, double mx, double def = double.NaN) { return new Def { Group = g, Key = k, Label = l, Unit = u, Small = sm, Large = lg, Min = mn, Max = mx, Default = def }; }
        static Def B(string g, string k, string l, bool def) { return new Def { Group = g, Key = k, Label = l, Unit = "", Bool = true, Min = 0, Max = 1, Default = def ? 1 : 0 }; }
        /// <summary>TAB-CRAFT1 per-craft tuning (blank = auto). Groups SPEEDS / APPROACH / TOUCHDOWN / ROLLOUT / TAKEOFF.</summary>
        internal static readonly List<Def> Defs = new List<Def>
        {
            D("SPEEDS", Approach, "Approach speed", "m/s", 1, 5, 30, 400), D("SPEEDS", Threshold, "Landing speed (at runway)", "m/s", 1, 5, 20, 300),
            D("SPEEDS", Stall, "Stall override", "m/s", 1, 5, 15, 250), D("SPEEDS", "decel_plan", "Decel for planning", "m/s2", .1, .5, .2, 5),
            D("SPEEDS", "flap_limit_speed", "Flap limit speed", "m/s", 1, 5, 20, 400),   /* ROLL5: brake_full_speed replaced by the ROLLOUT brake bands */
            B("APPROACH", AutoGa, "Auto go-around", false), D("APPROACH", "max_approach_bank", "Max approach bank", "deg", 1, 5, 5, 45), B("APPROACH", "airbrakes", "Airbrakes allowed", true),
            D("TOUCHDOWN", "flare_start_m", "Flare start height", "m", 1, 5, 3, 60, 15), D("TOUCHDOWN", "touchdown_sink", "Touchdown sink target", "m/s", .5, 1, .5, 5, 3),
            D("TOUCHDOWN", FlareThrottle, "Flare throttle", "%", 1, 5, 0, 100), D("TOUCHDOWN", FlareSpoolLead, "Flare spool-up lead", "s", .5, 1, 0, 6, 2), D("TOUCHDOWN", FlareSink, "Flare sink rate", "m/s", .1, .5, 0, 5), D("TOUCHDOWN", FlareMaxPitch, "Max flare pitch", "deg", .5, 1, 0, 20), D("TOUCHDOWN", RolloutPitch, "Rollout nose-down", "deg", .5, 1, 0, 10), D("TOUCHDOWN", "touchdown_aim_m", "Touchdown aim distance", "m", 10, 50, -300, 1500, 350), D("TOUCHDOWN", FlapDeg, "Flap degree", "deg", .5, 5, -30, 30),
            B("ROLLOUT", "reverser", "Reverser on rollout", true), D("ROLLOUT", "brake_high_pct", "Brakes % at high speed", "%", 5, 10, 0, 100, 60), D("ROLLOUT", "brake_mid_pct", "Brakes % at mid speed", "%", 5, 10, 0, 100, 80), D("ROLLOUT", "brake_low_pct", "Brakes % at low speed", "%", 5, 10, 0, 100, 100), D("ROLLOUT", "brake_high_speed", "High speed above (m/s)", "m/s", 1, 5, 5, 250), D("ROLLOUT", "brake_low_speed", "Low speed below (m/s)", "m/s", 1, 5, 0, 150, 40),   /* ROLL5: live brake bands */ D("ROLLOUT", "reverse_pct", "Reverse thrust %", "%", 1, 10, 0, 100, 70), D("ROLLOUT", "reverse_off_speed", "Reverse thrust off speed", "m/s", 1, 5, 0, 150, 25),   /* REV1 */
            D("TAKEOFF", "vr", "Rotation speed (Vr)", "m/s", 1, 5, 15, 300), D("TAKEOFF", RotatePitch, "Rotation angle", "deg", .5, 1, TakeoffMission.RotPitchMin, TakeoffMission.RotPitchMax), D("TAKEOFF", "climb_speed", "Climb speed", "m/s", 1, 5, 20, 400),
        };
        internal static readonly string[] Fields = Defs.ConvertAll(x => x.Key).ToArray();
        internal static readonly string[] Labels = Defs.ConvertAll(x => x.Label).ToArray();
        internal const double Min = 15, Max = 400;
        internal static Def Find(string key) { return Defs.Find(x => x.Key == key); }
        static double Clamp(string key, double v) { var d = Find(key); return d == null ? Clamp(v) : Math.Max(d.Min, Math.Min(d.Max, d.Bool ? Math.Round(v) : v)); }
        /// <summary>Live values for the active craft (Apply); NaN = auto.</summary>
        static readonly Dictionary<string, double> live = new Dictionary<string, double>();
        internal static double L(string key) { double v; return live.TryGetValue(key, out v) ? v : auto.TryGetValue(key, out v) ? v : double.NaN; }
        /// <summary>AUTO1: computed defaults (AutoLanding) for blank landing settings - L() falls back to them; the saved overrides are untouched.</summary>
        static readonly Dictionary<string, double> auto = new Dictionary<string, double>();
        internal static bool IsUser(string key) { return live.ContainsKey(key); }
        internal static double AutoValue(string key) { double v; return auto.TryGetValue(key, out v) ? v : double.NaN; }
        internal static void ClearAuto() { auto.Clear(); }
        internal static void SetAuto(AutoLanding a)
        {
            auto.Clear(); if (a == null || double.IsNaN(a.Landing)) return;
            auto[Threshold] = a.Landing; auto[Approach] = a.Approach; auto[FlareStart] = a.FlareStart; auto[FlareSink] = a.FlareSink; auto[FlareMaxPitch] = a.FlareMaxPitch; auto[TouchdownAim] = a.Aim;
            if (a.FlareThrottlePct > 0) auto[FlareThrottle] = a.FlareThrottlePct;
        }
        /// <summary>AUTO1: one line per setting, shown under its label (CRAFT > DETAILS).</summary>
        static readonly Dictionary<string, string> desc = new Dictionary<string, string>
        {
            { Approach, "Speed held on the approach until the final slow-down. Blank = 1.4 x landing speed, capped by measured thrust." },
            { Threshold, "Speed crossing the runway threshold. Blank = level flight at 5 deg AoA (measured lift), never below 1.15 x stall." },
            { Stall, "Stall speed for this craft. Blank = learned in flight / wing-loading estimate." },
            { "decel_plan", "Slow-down rate used to plan the approach (m/s2). Blank = learned idle decel." },
            { "flap_limit_speed", "Highest speed for full flaps. Blank = from the flap parts." },
            { AutoGa, "Go around by itself when the approach is unsafe (manual GO AROUND always works)." },
            { "max_approach_bank", "Steepest bank in approach turns." },
            { "airbrakes", "Let the autopilot use airbrakes to slow down." },
            { FlareStart, "Height above the runway where the flare starts. Blank = 1.5 s of glidepath sink (5-15 m)." },
            { "touchdown_sink", "Sink rate aimed for at touchdown when no flare sink is set." },
            { FlareThrottle, "Throttle held through the flare. Blank = idle, or a little for slow-spooling jets." },
            { FlareSpoolLead, "Seconds before the flare to set the flare throttle, so the engines are spooled." },
            { FlareSink, "Sink rate held in the flare. Blank = 0.5 m/s, more if the plane floats over 5 s." },
            { FlareMaxPitch, "Highest nose-up pitch in the flare (tail-strike guard). Blank = 6.5 deg." },
            { RolloutPitch, "Nose-down pitch after touchdown to keep the nose wheel on the runway." },
            { TouchdownAim, "Touchdown point past the threshold. Blank = 200 m, shorter when the stop distance is tight." },
            { FlapDeg, "Full-flap deflection (negative = trailing edge up). Blank = the part's setting." },
            { "reverser", "Use thrust reversers on the rollout." },
            { "brake_high_pct", "Wheel brake strength above the high speed." },
            { "brake_mid_pct", "Wheel brake strength between the low and high speeds." },
            { "brake_low_pct", "Wheel brake strength below the low speed." },
            { "brake_high_speed", "Speed above which the high-speed brake strength is used. Blank = max(stall, 90)." },
            { "brake_low_speed", "Speed below which the low-speed brake strength is used." },
            { "reverse_pct", "Reverse throttle on the rollout." },
            { "reverse_off_speed", "Speed where reverse thrust is cut and stowed." },
            { Vr, "Speed where the takeoff rotation starts." },
            { RotatePitch, "Nose-up pitch held during the takeoff rotation." },
            { "climb_speed", "Speed held in the climb after takeoff." },
        };
        internal static string Desc(string key) { string s; return key != null && desc.TryGetValue(key, out s) ? s : ""; }
        /// <summary>Bool fields: an override, else the global/default.</summary>
        internal static bool On(string key, bool auto) { double v = L(key); return double.IsNaN(v) ? auto : v >= .5; }
        static double Num(object o)
        {
            if (o == null) return double.NaN;
            try { double v = Convert.ToDouble(o, CultureInfo.InvariantCulture); return double.IsInfinity(v) ? double.NaN : v; } catch (Exception) { return double.NaN; }
        }
        internal static Dictionary<string, object> Entry(Dictionary<string, object> s, string craft, bool create)
        {
            if (s == null || string.IsNullOrEmpty(craft)) return null;
            object o; var t = s.TryGetValue(Table, out o) ? o as Dictionary<string, object> : null;
            if (t == null) { if (!create) return null; s[Table] = t = new Dictionary<string, object>(); }
            object e; var d = t.TryGetValue(craft, out e) ? e as Dictionary<string, object> : null;
            if (d == null && create) t[craft] = d = new Dictionary<string, object>();
            return d;
        }
        internal static double Get(Dictionary<string, object> s, string craft, string field)
        {
            var d = Entry(s, craft, false); object o; return d != null && d.TryGetValue(field, out o) ? Num(o) : double.NaN;
        }
        /// <summary>NaN = remove the field (use learned); an entry with no fields left is removed.</summary>
        internal static void Set(Dictionary<string, object> s, string craft, string field, double v)
        {
            var d = Entry(s, craft, !double.IsNaN(v)); if (d == null) return;
            if (double.IsNaN(v)) d.Remove(field); else d[field] = Math.Round(Clamp(field, v), 2);
            if (d.Count == 0) Clear(s, craft);
        }
        internal static void Clear(Dictionary<string, object> s, string craft)
        {
            object o; var t = s != null && s.TryGetValue(Table, out o) ? o as Dictionary<string, object> : null; if (t != null && craft != null) t.Remove(craft);
        }
        static double Clamp(double v) { return Math.Max(Min, Math.Min(Max, v)); }
        /// <summary>Text field: blank / "off" / "-" = no override (NaN); a number is clamped; anything else = invalid (null).</summary>
        internal static double? Parse(string text)
        {
            string t = (text ?? "").Trim().ToLowerInvariant(); if (t.Length == 0 || t == "off" || t == "-") return double.NaN;
            if (t == "on" || t == "yes") return 1; if (t == "no") return 0;
            double v; if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsNaN(v) || double.IsInfinity(v)) return null;
            return v;
        }
        /// <summary>-/+ buttons: an unset field starts from the value in use (fallback), rounded to whole m/s.</summary>
        internal static double Bump(double cur, double delta, double fallback, string key = null)
        {
            var df = key == null ? null : Find(key); double fb = double.IsNaN(fallback) && df != null ? df.Default : fallback;
            double b = double.IsNaN(cur) ? (double.IsNaN(fb) ? (df != null ? Math.Max(df.Min, Math.Min(df.Max, 50)) : 50) : (df != null && df.Small < 1 ? fb : Math.Round(fb))) : cur;
            return key == null ? Clamp(b + delta) : Clamp(key, Math.Round((b + delta) * 100) / 100);
        }
        /// <summary>Text field per field: a number clamped to the field's range (on/off/yes/no for switches).</summary>
        internal static double? Parse(string key, string text) { double? p = Parse(text); return !p.HasValue || double.IsNaN(p.Value) ? p : Clamp(key, p.Value); }
        /// <summary>'[craft] override <craft>: approach=<n> landing=<n>' (+ stall when set; '-' = learned).</summary>
        internal static string LogLine(string craft, double approach, double landing, double stall)
        {
            return "override " + craft + ": approach=" + (double.IsNaN(approach) ? "-" : Text(approach)) + " landing=" + (double.IsNaN(landing) ? "-" : Text(landing)) + (double.IsNaN(stall) ? "" : " stall=" + Text(stall));
        }
        /// <summary>TAB-TAKEOFF2: '[craft] override <craft>: vr=<n> rotate=<n>' ('-' = auto).</summary>
        internal static string TakeoffLogLine(string craft, double vr, double rotate)
        {
            return "override " + craft + ": vr=" + (double.IsNaN(vr) ? "-" : Text(vr)) + " rotate=" + (double.IsNaN(rotate) ? "-" : Text(rotate));
        }
        /// <summary>LAND5m: flare throttle 0-1 (NaN = idle).</summary>
        /// <summary>THR9: seconds before the flare to command the flare throttle (blank = 2, 0-6).</summary>
        internal static double FlareSpoolLeadS() { double v = L(FlareSpoolLead); return double.IsNaN(v) ? 2 : Math.Max(0, Math.Min(6, v)); }
        internal static double FlareThrottleFrac() { double p = L(FlareThrottle); return double.IsNaN(p) ? double.NaN : Math.Max(0, Math.Min(100, p)) / 100; }
        internal static string Text(double v) { return double.IsNaN(v) ? "" : v.ToString("0.#", CultureInfo.InvariantCulture); }
        /// <summary>AUTO2: what a number field shows. An override shows its value. A blank (auto) field is filled with the current
        /// auto value (autoShown remembers what was put there) and keeps following it while the text is untouched; once Luke types
        /// something different it is his pending edit (isAuto false) until SET saves it or AUTO resets it (caller nulls autoShown).</summary>
        internal static string FieldText(double user, double auto, string buffer, ref string autoShown, out bool isAuto)
        {
            if (!double.IsNaN(user)) { isAuto = false; if (autoShown != null || buffer == null) { autoShown = null; return Text(user); } return buffer; }
            if (autoShown == null || buffer == null || buffer == autoShown) { autoShown = Text(auto); isAuto = true; return autoShown; }
            isAuto = false; return buffer;
        }
        /// <summary>Apply a craft's overrides to the approach schedule (live): sets ApproachProfile.OverrideApproach / OverrideThreshold and
        /// returns the stall override (stall_override, else threshold_speed / 1.15; NaN = use learned).</summary>
        internal static double Apply(Dictionary<string, object> s, string craft, out double approach, out double threshold)
        {
            double so = Get(s, craft, Stall); approach = Get(s, craft, Approach); threshold = Get(s, craft, Threshold);
            if (double.IsNaN(approach)) approach = AutoValue(Approach); if (double.IsNaN(threshold)) threshold = AutoValue(Threshold);   // AUTO1: blank = computed default
            if (!(approach > 30)) approach = double.NaN; if (!(threshold > 20)) threshold = double.NaN;
            ApproachProfile.OverrideApproach = approach; ApproachProfile.OverrideThreshold = threshold;
            live.Clear(); foreach (var df in Defs) { double v = Get(s, craft, df.Key); if (!double.IsNaN(v)) live[df.Key] = v; }
            return StallLearner.OverrideStall(so, threshold);
        }
    }
}