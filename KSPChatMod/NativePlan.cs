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
        }
        internal List<Step> Steps = new List<Step>();
        internal int Index;
        internal bool Paused, Running;
        internal double Elapsed;
        internal bool Entered;
        internal string Text;
        static double Read(string text, string pattern, double fallback)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return fallback;
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        internal static NativePlan Parse(string text)
        {
            if (text == null || text.Length > 16000) throw new ArgumentException("Plan is empty or too long");
            var plan = new NativePlan { Text = text };
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = Regex.Replace(raw.Split('#')[0].Trim(), @"^(?:[-*]|\d+[.)])\s*", "").Trim();
                if (line.Length == 0) continue;
                string low = line.ToLowerInvariant();
                var step = new Step { Text = line, Reference = low.Contains("msl") ? "msl" : "agl" };
                if (low == "takeoff" || low == "take off") step.Op = "takeoff";
                else if (Regex.IsMatch(low, @"^(climb|descend)\b")) step.Op = "climb";
                else if (Regex.IsMatch(low, @"^cruise\b")) step.Op = "cruise";
                else if (Regex.IsMatch(low, @"^circle\b")) step.Op = "circle";
                else if (Regex.IsMatch(low, @"^wait\b")) step.Op = "wait";
                else if (Regex.IsMatch(low, @"^land\b")) { step.Op = "land"; step.Destination = line.Substring(4).Trim(); }
                else throw new ArgumentException("Local plan step not yet supported: " + line);
                const string number = @"\d+(?:\.\d+)?";
                string altitudeOption = @"(?:alt(?:itude)?\s+)?" + number + @"\s*(?:km|m|ft|feet)?(?:\s+(?:agl|msl))?";
                string holdOptions = @"(?:\s+(?:(?:hdg|heading|speed)\s+" + number + @"|alt(?:itude)?\s+" + number + @"\s*(?:km|m|ft|feet)?(?:\s+(?:agl|msl))?))*";
                string duration = number + @"\s*(?:s|sec(?:ond)?s?|min(?:ute)?s?)?";
                string grammar = step.Op == "takeoff" ? @"take\s?off"
                    : step.Op == "climb" ? @"(?:climb|descend)\s+" + altitudeOption + holdOptions
                    : step.Op == "cruise" ? "cruise" + holdOptions + @"(?:\s+for\s+" + duration + ")?"
                    : step.Op == "circle" ? @"circle(?:\s+" + number + @"(?:\s+laps?)?)?(?:\s+(?:left|right))?(?:\s+bank\s+" + number + ")?" + holdOptions
                    : step.Op == "wait" ? @"wait\s+" + duration
                    : @"land(?:\s+(?:KSC\s+)?Runway\s+(?:09|27)|\s+Island(?:\s+Runway(?:\s+(?:09|27))?)?)?";
                if (!Regex.IsMatch(low, "^(?:" + grammar + ")$", RegexOptions.IgnoreCase))
                    throw new ArgumentException("Unsupported local plan syntax: " + line);
                step.Altitude = Read(low, @"(?:alt(?:itude)?\s+|^(?:climb|descend)\s+)(\d+(?:\.\d+)?)", -1);
                if (Regex.IsMatch(low, @"\d\s*km\b")) step.Altitude *= 1000;
                if (Regex.IsMatch(low, @"\d\s*(?:ft|feet)\b")) step.Altitude *= .3048;
                step.Heading = Read(low, @"(?:hdg|heading)\s+(\d+(?:\.\d+)?)", -1);
                step.Speed = Read(low, @"speed\s+(\d+(?:\.\d+)?)", -1);
                step.Seconds = Read(low, @"(?:for|wait)\s+(\d+(?:\.\d+)?)", 120);
                if (Regex.IsMatch(low, @"\bmin(?:ute)?s?\b")) step.Seconds *= 60;
                step.Laps = Read(low, @"circle\s+(\d+(?:\.\d+)?)", 1);
                step.Bank = FlightPolicy.Clamp(Read(low, @"bank\s+(\d+(?:\.\d+)?)", 15), 5, 20) * (low.Contains("left") ? -1 : 1);
                if (step.Op == "climb" && step.Altitude < 0) throw new ArgumentException("Climb/descend requires altitude");
                if (step.Seconds <= 0 || step.Seconds > 86400 || step.Laps <= 0 || step.Laps > 100 || step.Heading > 360 || step.Speed > 200)
                    throw new ArgumentException("Local plan value outside supported limits: " + line);
                plan.Steps.Add(step);
                if (plan.Steps.Count > 100) throw new ArgumentException("Maximum 100 steps");
            }
            if (plan.Steps.Count == 0) throw new ArgumentException("Plan has no steps");
            return plan;
        }
        internal void Start() { if (Index >= Steps.Count) { Index = 0; Elapsed = 0; Entered = false; } Running = true; Paused = false; }
        internal void Pause() { Paused = Index < Steps.Count; Running = false; }
        internal void Interrupt() { Pause(); Entered = false; }
        internal void Advance() { Index++; Entered = false; Elapsed = 0; if (Index >= Steps.Count) Running = false; }
        internal string Status { get { return (Paused ? "Paused" : Running ? "Running" : Index >= Steps.Count ? "Complete" : "Ready") + " " + Math.Min(Index + 1, Steps.Count) + "/" + Steps.Count; } }
    }
}
