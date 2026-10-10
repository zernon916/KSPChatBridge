using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KSPChatBridge
{
    internal sealed class NativePlan
    {
        internal sealed class Step
        {
            internal string Op, Text, Reference = "agl", Destination = "";
            internal double Altitude = -1, Heading = -1, Speed = -1, Seconds = 120, Laps = 1, Bank = 15;
            internal double VerticalSpeed = -999, Distance = -1, Pitch = double.NaN;
        }
        internal List<Step> Steps = new List<Step>();
        internal int Index;
        internal bool Paused, Running;
        internal double Elapsed, DistanceElapsed;
        internal bool Entered;
        internal string Text;
        static double Read(string text, string pattern, double fallback)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return fallback;
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        internal static NativePlan Parse(string text, Func<string, bool> savedRunway = null, Func<string, bool> taxiRoute = null)
        {
            if (text == null || text.Length > 16000) throw new ArgumentException("Plan is empty or too long");
            var plan = new NativePlan { Text = text };
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = Regex.Replace(raw.Split('#')[0].Trim(), @"^(?:[-*]|\d+[.)])\s*", "").Trim();
                if (line.Length == 0) continue;
                string low = line.ToLowerInvariant();
                var step = new Step { Text = line, Reference = low.Contains("msl") ? "msl" : "agl" };
                if (Regex.IsMatch(low, @"\b(ascent|orbit|deorbit|transfer|rendezvous|dock|warp|stage|science|chutes?)\b")
                    || Regex.IsMatch(low, @"^fly\s+to\b"))
                    throw new ArgumentException("Local plan rejects unsupported orbital/rocket step: " + line);
                if (Regex.IsMatch(low, @"\btg\b|\btouch\s*and\s*go\b"))
                    throw new ArgumentException("Local plan does not yet support touch-and-go: " + line);
                if (low == "takeoff" || low == "take off") step.Op = "takeoff";
                else if (Regex.IsMatch(low, @"^(climb|descend)\b")) step.Op = "climb";
                else if (Regex.IsMatch(low, @"^cruise\b")) step.Op = "cruise";
                else if (Regex.IsMatch(low, @"^circle\b")) step.Op = "circle";
                else if (Regex.IsMatch(low, @"^wait\b")) step.Op = "wait";
                else if (Regex.IsMatch(low, @"^turn\s+(around|back)\b")) step.Op = "turnaround";
                else if (Regex.IsMatch(low, @"^(?:max\s+)?pitch\b")) step.Op = "pitch";
                else if (Regex.IsMatch(low, @"^(?:max\s+)?bank\b")) step.Op = "bank";
                else if (Regex.IsMatch(low, @"^head\b")) { step.Op = "head"; step.Destination = Regex.Match(low, @"^head\s+(?:to\s+)?([a-z0-9]+)").Groups[1].Value; }
                else if (Regex.IsMatch(low, @"^taxi\s+to\s+")) { step.Op = "taxi"; step.Destination = line.Substring(8).Trim(); }
                else if (Regex.IsMatch(low, @"^land\b")) { step.Op = "land"; step.Destination = line.Substring(4).Trim(); }
                else throw new ArgumentException("Local plan step not yet supported: " + line);
                const string number = @"\d+(?:\.\d+)?";
                string altitudeOption = @"(?:alt(?:itude)?\s+)?" + number + @"\s*(?:km|m|ft|feet)?(?:\s+(?:agl|msl))?";
                string holdOptions = @"(?:\s+(?:(?:hdg|heading|speed|vs)\s+" + number + @"|alt(?:itude)?\s+" + number + @"\s*(?:km|m|ft|feet)?(?:\s+(?:agl|msl))?))*";
                string duration = number + @"\s*(?:s|sec(?:ond)?s?|min(?:ute)?s?)?";
                string grammar = step.Op == "takeoff" ? @"take\s?off"
                    : step.Op == "turnaround" ? @"turn\s+(?:around|back)(?:\s+(?:max\s+)?bank\s+" + number + @"(?:\s*deg\w*)?)?(?:\s+(?:to\s+the\s+)?(?:left|right))?"
                    : step.Op == "pitch" ? @"(?:max\s+)?pitch(?:\s+(?:up|down))?\s+" + number + @"(?:\s*deg\w*)?"
                    : step.Op == "bank" ? @"(?:max\s+)?bank\s+" + number + @"(?:\s*deg\w*)?(?:\s+(?:to\s+the\s+)?(?:left|right))?"
                    : step.Op == "head" ? @"head\s+(?:to\s+)?(?:ksc|ksp|home|base|island)(?:\s+bank\s+" + number + @")?(?:\s+(?:left|right))?"
                    : step.Op == "climb" ? @"(?:climb|descend)\s+" + altitudeOption + holdOptions
                    : step.Op == "cruise" ? "cruise" + holdOptions + @"(?:\s+for\s+(?:" + duration + "|" + number + @"\s*km))?"
                    : step.Op == "circle" ? @"circle(?:\s+" + number + @"(?:\s+laps?)?)?(?:\s+(?:left|right))?(?:\s+bank\s+" + number + ")?" + holdOptions
                    : step.Op == "wait" ? @"wait\s+" + duration
                    : step.Op == "taxi" && taxiRoute != null && taxiRoute(step.Destination) ? Regex.Escape(line)
                    : step.Op == "land" && ((savedRunway != null && savedRunway(step.Destination)) || FlightResidualPolicy.BuiltInRunway(step.Destination, "") != null) ? Regex.Escape(line)
                    : @"land(?:\s+(?:KSC\s+)?Runway\s+(?:09|27)|\s+Island(?:\s+Runway(?:\s+(?:09|27))?)?)?";
                if (!Regex.IsMatch(low, "^(?:" + grammar + ")$", RegexOptions.IgnoreCase))
                    throw new ArgumentException("Unsupported local plan syntax: " + line);
                var altitude = Regex.Match(low, @"(?:alt(?:itude)?\s+|^(?:climb|descend)\s+)(\d+(?:\.\d+)?)\s*(km|m|ft|feet)?");
                if (altitude.Success)
                {
                    step.Altitude = double.Parse(altitude.Groups[1].Value, CultureInfo.InvariantCulture);
                    string unit = altitude.Groups[2].Value;
                    step.Altitude *= unit == "km" ? 1000 : unit == "ft" || unit == "feet" ? .3048 : 1;
                }
                step.Heading = Read(low, @"(?:hdg|heading)\s+(\d+(?:\.\d+)?)", -1);
                step.Speed = Read(low, @"speed\s+(\d+(?:\.\d+)?)", -1);
                step.Seconds = Read(low, @"(?:for|wait)\s+(\d+(?:\.\d+)?)", 120);
                if (Regex.IsMatch(low, @"\bmin(?:ute)?s?\b")) step.Seconds *= 60;
                step.VerticalSpeed = Read(low, @"\bvs\s+(\d+(?:\.\d+)?)", -999);
                if (step.VerticalSpeed != -999 && low.StartsWith("descend")) step.VerticalSpeed = -step.VerticalSpeed;
                double km = Read(low, @"\bfor\s+(\d+(?:\.\d+)?)\s*km\b", -1);
                if (km >= 0) step.Distance = km * 1000;
                step.Laps = Read(low, @"circle\s+(\d+(?:\.\d+)?)", 1);
                step.Bank = FlightPolicy.Clamp(Read(low, @"bank\s+(\d+(?:\.\d+)?)", 15), 5, step.Op == "head" ? 45 : 20) * (low.Contains("left") ? -1 : 1);
                if (step.Op == "head" && !low.Contains("left") && !low.Contains("right")) step.Bank = 0;   // 0 = normal heading turn
                if (step.Op == "turnaround" || step.Op == "bank")   // explicit plan bank: up to 45, carried by the step
                {
                    double b = Read(low, @"bank\s+(\d+(?:\.\d+)?)", 0);
                    step.Bank = b <= 0 ? 0 : FlightPolicy.Clamp(b, 5, 45) * (Regex.IsMatch(low, @"\bleft\b") ? -1 : 1);
                }
                if (step.Op == "pitch")
                {
                    double v = Read(low, @"(\d+(?:\.\d+)?)", 0);
                    if (v <= 0 || v > 30) throw new ArgumentException("Pitch must be 1-30 degrees: " + line);
                    step.Pitch = low.Contains("down") ? -v : v;
                }
                if (step.Op == "climb" && step.Altitude < 0) throw new ArgumentException("Climb/descend requires altitude");
                if (step.Seconds <= 0 || step.Seconds > 86400 || step.Laps <= 0 || step.Laps > 100 || step.Heading > 360 || step.Speed > 200
                    || (step.VerticalSpeed != -999 && Math.Abs(step.VerticalSpeed) > 90) || step.Distance == 0)
                    throw new ArgumentException("Local plan value outside supported limits: " + line);
                plan.Steps.Add(step);
                if (plan.Steps.Count > 100) throw new ArgumentException("Maximum 100 steps");
            }
            if (plan.Steps.Count == 0) throw new ArgumentException("Plan has no steps");
            return plan;
        }
        internal void Start() { if (Index >= Steps.Count) { Index = 0; Elapsed = DistanceElapsed = 0; Entered = false; } Running = true; Paused = false; }
        internal void Pause() { Paused = Index < Steps.Count; Running = false; }
        internal void Interrupt() { Pause(); Entered = false; }
        internal void Advance() { Index++; Entered = false; Elapsed = DistanceElapsed = 0; if (Index >= Steps.Count) Running = false; }
        internal string Status { get { return (Paused ? "Paused" : Running ? "Running" : Index >= Steps.Count ? "Complete" : "Ready") + " " + Math.Min(Index + 1, Steps.Count) + "/" + Steps.Count; } }
    }
}
