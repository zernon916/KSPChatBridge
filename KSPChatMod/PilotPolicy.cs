using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KSPChatBridge
{
    /// <summary>Live test round 2 (Oct 9): pure policies for roll, throttle, engine watch, plan-from-chat and command preemption.</summary>
    internal static class PilotPolicy
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- commands that must preempt an active plan/mission (they used to be acked while the plan kept control) ----
        static readonly HashSet<string> Preempt = new HashSet<string> { "plane_hold", "set_heading", "turn", "fly_to", "fly_to_place",
            "circle_here", "land", "land_plane", "land_at_spot", "land_at_ksc", "go_around", "takeoff", "stop_current", "level_off", "roll", "plane_pitch",
            "hold_pattern", "make_flight_plan", "taxi_to", "touch_and_go", "heli_control", "autopilot", "set_brakes" };
        internal static bool PreemptsPlan(string tool) { return Preempt.Contains(tool ?? ""); }

        // ---- roll / bank ----
        /// <summary>Bank target (deg, + right) for roll(direction, degrees, inverted, level, override). Luke's limit (20 slow / 10 fast)
        /// applies unless explicitly overridden; inverted is an explicit override (180).</summary>
        internal static double RollTarget(string direction, double degrees, bool inverted, bool level, bool overrideLimit, double speed, out bool overridden)
        {
            overridden = false;
            if (level) return 0;
            int sign = (direction ?? "left").Trim().ToLowerInvariant().StartsWith("r") ? 1 : -1;
            if (inverted) { overridden = true; return 180; }
            double d = double.IsNaN(degrees) || degrees <= 0 ? 15 : Math.Min(Math.Abs(degrees), 180);
            double lim = FlightPolicy.BankLimit(speed);
            if (d > lim && !overrideLimit) d = lim; else if (d > lim) overridden = true;
            return sign * d;
        }
        /// <summary>Bank clamp used by the controller: Luke's rule unless an explicit override is active.</summary>
        internal static double ClampBank(double bank, double speed, bool overridden)
        {
            double lim = overridden ? 180 : FlightPolicy.BankLimit(speed);
            return FlightPolicy.Clamp(bank, -lim, lim);
        }
        /// <summary>Upside down, the elevator works the other way round relative to the horizon.</summary>
        internal static double PitchCommand(double pitchOut, double roll) { return Math.Abs(roll) > 90 ? -pitchOut : pitchOut; }

        /// <summary>Plan 'pitch N' / 'pitch up N' / 'max pitch N' sets the climb pitch limit; 'pitch down N' the descent limit.
        /// Luke's normal climb cap is 15 deg; a plan step is explicit so up to 25 is honored, never beyond.</summary>
        internal static void ApplyPitchStep(double pitch, ref double climbMax, ref double descentMin)
        {
            if (double.IsNaN(pitch) || pitch == 0) return;
            if (pitch > 0) climbMax = FlightPolicy.Clamp(pitch, 3, 25); else descentMin = -FlightPolicy.Clamp(-pitch, 1, 15);
        }
        // ---- circle bank requests ("increase bank to 25") ----
        internal static double CircleBank(string direction, double bank, double speed, bool overrideLimit)
        {
            double lim = overrideLimit ? 45 : FlightPolicy.BankLimit(speed);
            double b = FlightPolicy.Clamp(double.IsNaN(bank) || bank == 0 ? 15 : Math.Abs(bank), 5, lim);
            return (direction ?? "left").Trim().ToLowerInvariant().StartsWith("r") ? b : -b;
        }

        // ---- engine watch: pilot notices a shut-down/flamed-out engine, fumbles 2-4 s, relights ----
        /// <summary>"restart" when a running engine stopped in flight without our command; "none" otherwise.</summary>
        internal static string EngineWatch(bool wasRunning, bool running, bool flameout, bool flying, bool commandedOff, bool restartable)
        {
            if (!flying || commandedOff || !restartable) return "none";
            if (wasRunning && (!running || flameout)) return "restart";
            return "none";
        }
        internal static double FumbleSeconds(Random rng) { return 2 + 2 * (rng ?? new Random()).NextDouble(); }

        // ---- flight plan from chat text (round 3: units, bank/return, short final, runway; unsure -> ask) ----
        static readonly Regex Qty = new Regex(@"(\d+(?:\.\d+)?)\s*(km|k\b|kilomet\w*|ft|feet|foot|m\b|meters?|metres?|nm|mi(?:les?)?)?", RegexOptions.IgnoreCase);
        static readonly Regex Filler = new Regex(@"^(please|ok|okay|now|then|and|also|a|the)$");

        /// <summary>Metres for an altitude phrase ("2k", "2 km", "6500 ft", "1500 m", "1500"). NaN if none.</summary>
        internal static double AltitudeM(string p)
        {
            var m = Qty.Match(p ?? ""); if (!m.Success) return double.NaN;
            double v = double.Parse(m.Groups[1].Value, Inv); string u = m.Groups[2].Value.ToLowerInvariant();
            if (u.StartsWith("k")) return v * 1000;
            if (u.StartsWith("f")) return v * .3048;
            return v;
        }
        static string RunwayIn(string p)
        {
            var d = Regex.Match(p, @"\b(0?9|27)\b"); bool island = Regex.IsMatch(p, @"\bisland|isle\b");
            if (!d.Success && !island && !Regex.IsMatch(p, @"\b(ksc|ksp|runway|home|base)\b")) return null;
            return (island ? "Island" : "KSC") + (d.Success ? " " + (d.Groups[1].Value == "27" ? "27" : "09") : "");
        }

        /// <summary>Natural request -> native plan. unsure lists fragments it could not read with confidence (then ask, don't fly).</summary>
        internal static bool TryPlan(string request, bool grounded, out string plan, out List<string> unsure, out List<string> notes)
        {
            unsure = new List<string>(); notes = new List<string>(); plan = "";
            string t = (request ?? "").ToLowerInvariant().Replace("short final", "shortfinal").Replace("short-final", "shortfinal");
            t = Regex.Replace(t, @"^\s*(please\s+)?(make|write|create|build|do)\s+(me\s+)?(a\s+)?(flight\s+)?plan\s*(to|that|for|:|-|,)?\s*", "");
            t = Regex.Replace(t, @"^\s*(flight\s+)?plan\s*:\s*", "");
            var parts = Regex.Split(t, @"\s*(?:,|;|\band then\b|\bthen\b|\band\b|\.(?:\s|$))\s*");
            var lines = new List<string>(); double outKm = -1; bool tookOff = false; string runway = null, landRunway = null; bool shortFinal = false, landed = false;
            foreach (string raw in parts)
            {
                string p = raw.Trim().TrimEnd('.', '!'); if (p.Length == 0 || Filler.IsMatch(p)) continue;
                string rw = RunwayIn(p);
                if (Regex.IsMatch(p, @"^take ?off")) { lines.Add("takeoff"); tookOff = true; continue; }
                if (Regex.IsMatch(p, @"\b(climb|ascend|descend|altitude|level off at)\b"))
                {
                    double alt = AltitudeM(p);
                    if (double.IsNaN(alt)) { unsure.Add(p); continue; }
                    if (alt < 100 && !Regex.IsMatch(p, @"\d\s*(m\b|meters?|metres?)")) { unsure.Add(p + " (" + alt.ToString("0", Inv) + " m? say e.g. 2k or 2000 m)"); continue; }
                    if (alt < 300) notes.Add("climb " + alt.ToString("0", Inv) + " m is below the 300 m terrain safety floor; it will hold the floor");
                    lines.Add((p.Contains("descend") ? "descend " : "climb ") + alt.ToString("0", Inv) + " m " + (p.Contains("agl") ? "agl" : "msl"));
                    continue;
                }
                if (p.Contains("shortfinal") || Regex.IsMatch(p, @"\b(set up|line up|set for|approach)\b"))
                {
                    if (p.Contains("shortfinal")) { shortFinal = true; notes.Add("short final (4 km fix) if the geometry allows"); }
                    if (rw != null) runway = rw; else if (!p.Contains("shortfinal")) unsure.Add(p);
                    continue;
                }
                var bank = Regex.Match(p, @"\b(?:bank|turn|roll)\b(?:\s+at)?(?:\s+(\d{1,2})\s*(?:deg\w*)?)?(?:\s+(?:to\s+the\s+)?(left|right))?");
                bool back = Regex.IsMatch(p, @"\b(back|return|home|head(?:ing)?)\b") || Regex.IsMatch(p, @"\bto (ksc|ksp|the runway|base)\b");
                if (bank.Success && back || Regex.IsMatch(p, @"^(return|head|go|fly) (back )?(home|to (ksc|ksp|base|the runway))$|^(return|head) back$|^(fly|go|come) back$"))
                {
                    if (outKm > 0 && !bank.Success && !Regex.IsMatch(p, @"ksc|ksp|home|base|runway")) { lines.Add("cruise for " + Math.Max(5, outKm - 12).ToString("0", Inv) + " km"); continue; }
                    string side = Regex.IsMatch(p, @"\bleft\b") ? " left" : Regex.IsMatch(p, @"\bright\b") ? " right" : "";
                    double deg = bank.Success && bank.Groups[1].Success ? double.Parse(bank.Groups[1].Value, Inv) : 15;
                    lines.Add("head KSC bank " + deg.ToString("0", Inv) + side);
                    if (rw != null && Regex.IsMatch(rw, @"\d")) runway = rw;
                    continue;
                }
                var bn = Regex.Match(p, @"(\d{1,2})\s*(?:deg\w*\s+)?bank|bank(?:\s+(?:angle\s+)?(?:of|at|to))?\s+(\d{1,2})");
                string bside = Regex.IsMatch(p, @"\bleft\b") ? " left" : Regex.IsMatch(p, @"\bright\b") ? " right" : "";
                string bnum = bn.Success ? (bn.Groups[1].Success ? bn.Groups[1].Value : bn.Groups[2].Value) : null;
                if (Regex.IsMatch(p, @"\b(turn|head|come)\s+(around|back)\b|\bu-?turn\b|\breverse course\b")) { lines.Add("turn around" + (bnum != null ? " bank " + bnum + bside : "")); continue; }
                var pm = Regex.Match(p, @"\b(max(?:imum)?\s+)?pitch(?:\s+(?:angle\s+)?(?:of|at|to))?(?:\s+(up|down))?\s+(\d{1,2})|\b(\d{1,2})\s*(?:deg\w*\s+)?pitch(?:\s+(up|down))?");
                if (pm.Success)
                {
                    string dir = pm.Groups[2].Success ? pm.Groups[2].Value : pm.Groups[5].Value;
                    lines.Add((pm.Groups[1].Success ? "max " : "") + "pitch " + (dir == "down" ? "down " : dir == "up" ? "up " : "") + (pm.Groups[3].Success ? pm.Groups[3].Value : pm.Groups[4].Value));
                    continue;
                }
                if (bnum != null && !Regex.IsMatch(p, @"\bcircle|loiter")) { lines.Add((p.Contains("max") ? "max " : "") + "bank " + bnum + bside); continue; }

                if (Regex.IsMatch(p, @"\b(fly|cruise|go|head)\b") && Regex.IsMatch(p, @"\d") && Regex.IsMatch(p, @"\bmin"))
                { lines.Add("cruise for " + AltitudeM(p).ToString("0", Inv) + " min"); continue; }
                if (Regex.IsMatch(p, @"\b(fly|cruise|go|head)\b") && Regex.IsMatch(p, @"\d"))
                {
                    var q = Qty.Match(p); double v = double.Parse(q.Groups[1].Value, Inv); string u = q.Groups[2].Value.ToLowerInvariant();
                    if (u.Length == 0 && !p.Contains("out")) { unsure.Add(p); continue; }
                    outKm = u.StartsWith("k") || u.Length == 0 ? v : u == "nm" ? v * 1.852 : u.StartsWith("mi") ? v * 1.609 : v / 1000;
                    lines.Add("cruise for " + outKm.ToString("0.#", Inv) + " km"); continue;
                }
                if (Regex.IsMatch(p, @"\bcircle|loiter"))
                {
                    var n = Regex.Match(p, @"(\d+)\s*(laps?|times|circles?)"); double b = AltitudeM(Regex.Match(p, @"bank\s+\d+").Value.Replace("bank", ""));
                    lines.Add("circle " + (n.Success ? n.Groups[1].Value : "1") + " laps " + (p.Contains("right") ? "right" : "left") + " bank " + (double.IsNaN(b) ? 15 : Math.Min(20, b)).ToString("0", Inv));
                    continue;
                }
                if (Regex.IsMatch(p, @"^land\b|\bland (at|on)\b|^touch ?down"))
                {
                    string where = Regex.Replace(p, @"^.*?\bland\b\s*(at|on)?\s*(the\s+)?", "").Trim();
                    landRunway = where.Length > 0 ? RunwayIn(where) : runway;
                    if (where.Length > 0 && landRunway == null) { unsure.Add(p); continue; }
                    if (landRunway == null) { landRunway = "KSC"; notes.Add("no runway named: landing at KSC (end chosen by approach)"); }
                    lines.Add("land " + landRunway + (shortFinal || p.Contains("shortfinal") ? " short final" : "")); landed = true; continue;
                }
                if (Regex.IsMatch(p, @"^wait") && Regex.IsMatch(p, @"\d")) { lines.Add("wait " + AltitudeM(p).ToString("0", Inv) + (p.Contains("min") ? " min" : " s")); continue; }
                unsure.Add(p);
            }
            if (!landed && runway != null) { lines.Add("land " + runway + (shortFinal ? " short final" : "")); notes.Add("added the landing on " + runway); }
            if (lines.Count == 0 && unsure.Count == 0) unsure.Add(request ?? "");
            if (grounded && !tookOff && lines.Count > 0) { lines.Insert(0, "takeoff"); if (!lines.Exists(l => l.StartsWith("climb"))) lines.Insert(1, "climb 1000 m agl"); }
            plan = string.Join("\n", lines.ToArray());
            return unsure.Count == 0;
        }

        /// <summary>Old entry point: throws (with the question to ask) unless the parse is fully confident.</summary>
        internal static string PlanFromText(string request, bool grounded)
        {
            string plan; List<string> unsure, notes;
            if (!TryPlan(request, grounded, out plan, out unsure, out notes))
                throw new ArgumentException("I'm not sure about: \"" + string.Join("\", \"", unsure.ToArray()) + "\". Can you say those steps another way (e.g. climb 2000 m, head to KSC bank 25 left, land KSC 27)?");
            return plan;
        }

        // ---- round 3: speed cap (200 target / 220 cap low down) and altitude target vs terrain floor ----
        internal const double CapSpeed = 220, CapAltitude = 6000;
        /// <summary>Throttle after the over-cap rule: over 220 m/s low down, cut 15% (30% when 30+ over) at most once a second.</summary>
        internal static double SpeedCapThrottle(double throttle, double ias, double altitude, double sinceLastCut, out bool cut)
        {
            cut = false;
            if (altitude >= CapAltitude || ias <= CapSpeed || sinceLastCut < 1) return throttle;
            cut = true; return Math.Max(.05, throttle - (ias > CapSpeed + 30 ? .3 : .15));
        }
        /// <summary>Stall/approach margin: the speed below which we never cut power and never turn hard.</summary>
        internal static double SafeSpeed(double stall) { return Math.Max(60, 1.4 * stall); }
        /// <summary>Luke: never trade airspeed away. Below the target band power comes back up; below the safe speed it is
        /// at least 80% (full if near the stall). Over-cap cuts stop once inside the band.</summary>
        internal static double ThrottleFloor(double throttle, double ias, double target, double stall)
        {
            double safe = SafeSpeed(stall);
            if (ias < 1.15 * stall) return 1;
            if (ias < safe) return Math.Max(throttle, .8);
            if (ias < target - 15) return Math.Max(throttle, .35);
            return throttle;
        }
        /// <summary>No hard turns while slow or decelerating hard (that is how the tight low-speed circle happened).</summary>
        internal static double SafeBank(double bank, double ias, double stall, double decel)
        {
            double safe = SafeSpeed(stall), lim = 180;
            if (ias < safe) lim = 5; else if (ias < safe + 20 || decel > 4) lim = 10;
            return FlightPolicy.Clamp(bank, -lim, lim);
        }
        /// <summary>Level-flight max speed from measured drag (kN) and dynamic pressure (kPa) and available thrust (kN), port of
        /// kspchat/maxspeed.py (0.9 safety factor). NaN when unknown (ground, slow, thin air).</summary>
        internal static double VMax(double dragKn, double qKpa, double rho, double thrustKn, double speed)
        {
            if (!(dragKn > 0 && qKpa > .3 && rho > 0 && speed > 40)) return double.NaN;
            double cda = dragKn / qKpa; if (thrustKn <= 0) return 0;
            return Math.Max(speed * .98, .9 * Math.Sqrt(2 * thrustKn * 1000 / (rho * cda)));
        }
        /// <summary>set_speed resolution. 'max'/'full' is an explicit request for the vehicle max (cap lifted, mentioned as a note);
        /// numbers are clamped to the vehicle max and, low down, to Luke's 220 cap unless overridden. Reply states the real target.</summary>
        internal static double ResolveSpeed(object request, double vmax, double altitude, bool overrideCap, out bool capLifted, out string reply)
        {
            string s = (request == null ? "" : Convert.ToString(request, Inv)).Trim().ToLowerInvariant();
            bool max = s == "max" || s == "maximum" || s == "full" || s == "full speed" || s == "max speed";
            double asked; bool num = double.TryParse(s, NumberStyles.Float, Inv, out asked);
            bool known = !double.IsNaN(vmax) && vmax > 0; bool low = altitude < CapAltitude;
            capLifted = false;
            if (max)
            {
                double t = known ? vmax : 400; capLifted = low && t > CapSpeed;
                reply = "Target " + t.ToString("0", Inv) + " m/s (" + (known ? "estimated max level speed" : "no max estimate yet - full power, watching it") + ")"
                    + (capLifted ? "; note: above the 220 m/s low-altitude cap because you asked for max." : ".");
                return t;
            }
            if (!num) { reply = "Speed not understood: " + s + " (give m/s or say max)."; return double.NaN; }
            double target = Math.Max(25, asked); var notes = new List<string>();
            if (known && target > vmax) { target = vmax; notes.Add("best estimate max here is ~" + vmax.ToString("0", Inv) + " m/s; you asked " + asked.ToString("0", Inv)); }
            if (low && target > CapSpeed) { if (overrideCap) capLifted = true; else { target = CapSpeed; notes.Add("low-altitude cap; say override for more"); } }
            reply = "Target " + target.ToString("0", Inv) + " m/s" + (notes.Count > 0 ? " (" + string.Join("; ", notes.ToArray()) + ")." : ".");
            return target;
        }
        /// <summary>Hold the higher of the target and the terrain floor as the altitude target (no fighting VS overrides = no porpoise).</summary>
        internal static double EffectiveAltitude(double target, double terrainFloor)
        {
            return double.IsNaN(terrainFloor) ? target : Math.Max(target, terrainFloor);
        }
        /// <summary>Takeoff -> hold hand-off throttle: never leave full power on.</summary>
        internal static double HandoffThrottle(double throttle) { return Math.Min(throttle, .65); }

        /// <summary>Telemetry on command only for real orders, not UI polling (flightplan/status, get_*).</summary>
        internal static bool LogsTelemetry(string tool)
        {
            tool = tool ?? "";
            return !(tool.Contains("/") || tool.StartsWith("get_") || tool.StartsWith("list_") || tool.EndsWith("_status") || tool.EndsWith("_report") || tool == "how_far" || tool == "fuel_check");
        }

        // ---- small-model prompt hygiene ----
        static readonly Regex SpaceNote = new Regex(@"\b(orbit\w*|apoapsis|periapsis|transfer\w*|parking|rocket\w*|staging|stage|dock\w*|mun|minmus|duna|delta-?v|ascent|reentry|deorbit)\b", RegexOptions.IgnoreCase);
        /// <summary>Drop playstyle notes that don't apply to this craft kind (planes don't need parking-orbit/fuel-transfer notes).</summary>
        internal static string FilterNotes(string notesBlock, string craft)
        {
            if (string.IsNullOrEmpty(notesBlock) || craft == "rocket" || string.IsNullOrEmpty(craft)) return notesBlock ?? "";
            var keep = new List<string>();
            foreach (string line in notesBlock.Split('\n'))
                if (!line.StartsWith("- ") || !SpaceNote.IsMatch(line)) keep.Add(line);
            return keep.Count <= 1 ? "" : string.Join("\n", keep.ToArray());
        }

        /// <summary>get_status: say plainly whether we are landing (the model said "autopilot is off" while it was in hold).</summary>
        internal static string StatusLine(string mode, string craft, string landingPhase, double distance, double alt, double spd, double hdg, int planStep)
        {
            string what = mode == "idle" ? "autopilot OFF (manual)" : mode == "landing" ? "LANDING (" + landingPhase + ", " + (double.IsNaN(distance) ? "?" : (distance / 1000).ToString("0.0", Inv) + " km") + " to the runway)"
                : mode == "hold" ? "autopilot ON, holding (NOT landing)" : "autopilot " + mode;
            return craft + ": " + what + (planStep > 0 ? ", flight plan step " + planStep : "") + "; alt " + alt.ToString("0", Inv) + " m, " + spd.ToString("0", Inv) + " m/s, heading " + hdg.ToString("000", Inv) + ".";
        }

        // ---- Luke's approach rules (Oct 9): short final when near head-on, long otherwise; nearest runway + best end ----
        internal const double ShortFix = 4000, ShortHeadOn = 20, ShortForcedMax = 60;
        /// <summary>"short" (4 km fix) when the track is within 20 deg of runway heading (or short final was asked for and we're within
        /// 60 deg) and we're still far enough out; else "long" (12 km fix). distOut = metres before the threshold (-along).</summary>
        internal static string ApproachKind(double track, double course, double distOut, bool forcedShort)
        {
            if (double.IsNaN(track) || distOut < ShortFix + 1000) return "long";
            double err = Math.Abs(FlightPolicy.Wrap(track - course));
            return err <= ShortHeadOn || (forcedShort && err <= ShortForcedMax) ? "short" : "long";
        }
        /// <summary>Land on the end whose heading is closest to ours: swap when the threshold->end course is >90 deg off.</summary>
        internal static bool SwapEnd(double thresholdToEndCourse, double heading) { return Math.Abs(FlightPolicy.Wrap(heading - thresholdToEndCourse)) > 90; }
        /// <summary>Index of the nearest runway among (lat, lon) midpoints.</summary>
        internal static int Nearest(double lat, double lon, IList<double[]> runways, double radius)
        {
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < runways.Count; i++) { double d = NavigationMath.Distance(lat, lon, runways[i][0], runways[i][1], radius); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        internal const string ToolRules = "\nRULES: To change anything in the game you MUST call a tool (<tool_call>). Never say you did something unless a tool result says so; only state numbers (times, distances, speeds) that appear in a tool result or the status - never compute or invent them; if a tool fails, say what failed. If no listed tool fits, call find_tool. Answer in one short sentence.";
    }
}
