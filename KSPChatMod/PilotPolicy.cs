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
        static readonly HashSet<string> Preempt = new HashSet<string> { "plane_hold", "set_heading", "set_altitude", "set_speed", "turn", "fly_to", "fly_to_place",
            "circle_here", "land", "land_plane", "land_at_spot", "land_at_ksc", "go_around", "takeoff", "stop_current", "level_off", "roll", "plane_pitch",
            "set_throttle", "hold_pattern", "make_flight_plan", "taxi_to", "touch_and_go", "heli_control", "autopilot", "set_brakes" };
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

        // ---- flight plan from chat text ----
        static readonly Regex Num = new Regex(@"(\d+(?:\.\d+)?)\s*(km|kilomet\w*|m|meters?|metres?|ft|feet)?", RegexOptions.IgnoreCase);
        /// <summary>Natural request ("fly 100km out, turn around, fly back, land at 27 ksp") -> native plan text. Throws on nothing usable.</summary>
        internal static string PlanFromText(string request, bool grounded)
        {
            string t = (request ?? "").ToLowerInvariant();
            t = Regex.Replace(t, @"^(please\s+)?(make|write|create|build|do)\s+(me\s+)?(a\s+)?(flight\s+)?plan\s*(to|that|:|-)?\s*", "");
            var parts = Regex.Split(t, @"\s*(?:,|;|\bthen\b|\band then\b|\band\b|\.\s)\s*");
            var lines = new List<string>(); double outKm = -1; bool tookOff = false;
            foreach (string raw in parts)
            {
                string p = raw.Trim(); if (p.Length == 0) continue;
                Match n = Num.Match(p);
                double val = n.Success ? double.Parse(n.Groups[1].Value, Inv) : -1; string unit = n.Success ? n.Groups[2].Value : "";
                if (Regex.IsMatch(p, @"^take ?off")) { lines.Add("takeoff"); tookOff = true; }
                else if (Regex.IsMatch(p, @"\b(turn|head|come)\s+(around|back)\b|\bu-?turn\b|\breverse course\b")) lines.Add("turn around");
                else if (Regex.IsMatch(p, @"\b(fly|go|head|come|return)\s+(back|home)\b|\breturn\b"))
                    lines.Add("cruise for " + (outKm > 0 ? Math.Max(5, outKm - 12) : 30).ToString("0", Inv) + " km");
                else if (Regex.IsMatch(p, @"\b(fly|cruise|go|head)\b") && val > 0 && (unit.StartsWith("k") || p.Contains("out")))
                { outKm = unit.StartsWith("k") ? val : val / 1000; lines.Add("cruise for " + outKm.ToString("0.#", Inv) + " km"); }
                else if (Regex.IsMatch(p, @"\b(fly|cruise)\b") && val > 0 && Regex.IsMatch(p, @"\bmin"))
                    lines.Add("cruise for " + val.ToString("0", Inv) + " min");
                else if (Regex.IsMatch(p, @"\b(climb|ascend|descend|altitude)\b") && val > 0)
                    lines.Add((p.Contains("descend") ? "descend " : "climb ") + (unit.StartsWith("k") ? val * 1000 : unit.StartsWith("f") ? val * .3048 : val).ToString("0", Inv) + " m msl");
                else if (Regex.IsMatch(p, @"\bcircle|orbit the|loiter"))
                    lines.Add("circle " + (val > 0 && val < 20 ? val.ToString("0", Inv) : "1") + " laps " + (p.Contains("right") ? "right" : "left") + " bank 15");
                else if (Regex.IsMatch(p, @"^land\b|\bland (at|on)\b"))
                {
                    string where = Regex.Replace(p, @"^.*?\bland\b\s*(at|on)?\s*(the\s+)?", "").Trim();
                    lines.Add("land " + (where.Length > 0 ? where : "KSC"));
                }
                else if (Regex.IsMatch(p, @"^wait") && val > 0) lines.Add("wait " + val.ToString("0", Inv) + (p.Contains("min") ? " min" : " s"));
            }
            if (lines.Count == 0) throw new ArgumentException("I couldn't turn that into plan steps (try: takeoff, climb 1000 m, fly 100 km out, turn around, fly back, circle, land at KSC 27).");
            if (grounded && !tookOff) { lines.Insert(0, "takeoff"); if (!lines.Exists(l => l.StartsWith("climb"))) lines.Insert(1, "climb 1000 m agl"); }
            return string.Join("\n", lines.ToArray());
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

        internal const string ToolRules = "\nRULES: To change anything in the game you MUST call a tool (<tool_call>). Never say you did something unless a tool result says so; if a tool fails, say what failed. If no listed tool fits, call find_tool. Answer in one short sentence.";
    }
}
