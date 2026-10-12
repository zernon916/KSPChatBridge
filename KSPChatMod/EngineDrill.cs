using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Oct 12 (Luke): HIDDEN Learn drill "mid-flight engine restart". Once per Learn run, at a random moment while a test runs at
    /// >= 5 km (never takeoff / climb / fly-home / landing / the stall break or recovery / low energy): engines off quietly, crew panics at 2 s,
    /// pilot restarts at 4 s. Records restart time (to >= 50% thrust), altitude and speed lost. Backup: no thrust in 30 s or below 2500 m AGL
    /// -> force restart, drill aborted. Afterwards it appears as a lettered sub-item of the test it happened in ("9c Engine restart (surprise)").
    /// The crew always FAILS the panic test (Luke's joke). Pure: the controller does the engines and the chat.</summary>
    internal sealed class EngineDrill
    {
        internal const double MinAlt = 5000, MinAgl = 2500, PanicAt = 2, RestartAt = 4, ForceS = 30, ArmMinS = 20, ArmMaxS = 240, ThrustOk = .5;
        internal enum Act { None, Shutdown, Panic, Restart, ForceRestart, Done }
        static readonly HashSet<string> NoPhase = new HashSet<string> { "start", "takeoff", "climb", "climbback", "wait", "flyhome", "finish", "stall", "stallsink", "stallrec" };
        /// <summary>Allowed now: a real test (3..11) is running, high enough, not low energy, not one of the excluded phases.</summary>
        internal static bool Eligible(string phase, int test, double alt, double agl, bool lowEnergy, bool done)
        {
            int b = BaseTest(test);
            return !done && b >= 3 && b <= 11 && !NoPhase.Contains(phase ?? "") && alt >= MinAlt && agl >= MinAgl && !lowEnergy;
        }
        internal static int BaseTest(int code) { return code == 91 ? 9 : code == 101 ? 10 : code; }
        /// <summary>Next free letter under a test: 9 and 10 already have a 'b' (flaps-full) -> 'c'; every other test -> 'b'.</summary>
        internal static string Letter(int test) { int b = BaseTest(test); return b == 9 || b == 10 ? "c" : "b"; }
        internal static string Label(int test) { return BaseTest(test) + Letter(test) + " Engine restart (surprise)"; }
        /// <summary>Checklist row for a recorded drill (crew always FAILED).</summary>
        internal static string Row(int test, double s, double altLoss, double spdLoss, bool ok)
        {
            return "  [x] " + Label(test) + ": crew FAILED" + (ok ? ", restart " + s.ToString("0.0") + " s" : ", forced restart") + ", -" + Math.Max(0, altLoss).ToString("0") + " m, -" + Math.Max(0, spdLoss).ToString("0") + " m/s";
        }
        internal static readonly string[] PilotCalm = { "Hmm. Quiet in here, isn't it?", "Nobody touch anything. Totally under control.", "Huh, would you look at that. Engines are... resting.", "Stay calm. I've done this a hundred times. On purpose, even." };
        internal static readonly string[] PilotLaugh = { "HA! You should see your faces, {names}! Planned test, you sissies - and you ALL failed.", "Engines back. That was a drill, {names}. Crew panic test: failed. Spectacularly.",
            "Hahaha! Surprise engine drill! Grade for {names}: F. Big fat F.", "Relax, I switched them off myself. {name}, I heard that scream. Crew panic test... FAILED. Every one of you.", "Did somebody scream? {name} screamed. Test over - {names}, you all failed.", "{name}, you screamed louder than the engines GROANING back to life!" };
        internal const string FailedLine = "Crew panic test: FAILED";
        /// <summary>First names, "A", "A and B", "A, B and C"; "you lot" with nobody else aboard.</summary>
        internal static string Names(IList<string> names)
        {
            var f = new List<string>(); foreach (var n in names) if (!string.IsNullOrEmpty(n)) f.Add(IntercomTalk.First(n));
            if (f.Count == 0) return "you lot"; if (f.Count == 1) return f[0];
            return string.Join(", ", f.GetRange(0, f.Count - 1).ToArray()) + " and " + f[f.Count - 1];
        }
        internal static string Laugh(string template, IList<string> names) { return template.Replace("{names}", Names(names)).Replace("{name}", names.Count > 0 ? IntercomTalk.First(names[0]) : "somebody"); }
        internal static string LaughPrompt(string pilot, IList<string> names, double s, double altLoss)
        {
            return "You are " + pilot + ", the pilot. You secretly switched the engines off mid-flight as a surprise drill and restarted them after " + s.ToString("0") + " s (lost " + Math.Max(0, altLoss).ToString("0")
                + " m). The crew - " + Names(names) + " - panicked. Laugh, tease each of them BY NAME, tell them it was a planned test and that they all FAILED the crew panic test. One or two short lines, no quotes, no name prefix.";
        }

        readonly Random rng;
        internal EngineDrill(Random rng = null) { this.rng = rng ?? new Random(); }
        internal bool Used, Running, Ok; internal int Test; internal double Seconds = double.NaN, AltLoss = double.NaN, SpdLoss = double.NaN;
        double armAt = double.NaN, t0, alt0, spd0, minAlt, minSpd; bool panicked, restarted;

        /// <summary>One tick. eligible from Eligible(); thrust = current / max thrust (0..1).</summary>
        internal Act Step(double now, bool eligible, int test, double alt, double agl, double speed, double thrust)
        {
            if (Used && !Running) return Act.None;
            if (!Running)
            {
                if (!eligible) return Act.None;
                if (double.IsNaN(armAt)) { armAt = now + ArmMinS + rng.NextDouble() * (ArmMaxS - ArmMinS); return Act.None; }
                if (now < armAt) return Act.None;
                Used = Running = true; Test = test; t0 = now; alt0 = minAlt = alt; spd0 = minSpd = speed; return Act.Shutdown;
            }
            minAlt = Math.Min(minAlt, alt); minSpd = Math.Min(minSpd, speed); double dt = now - t0;
            if (agl < MinAgl || dt > ForceS) { Finish(now, false); return Act.ForceRestart; }
            if (!panicked && dt >= PanicAt) { panicked = true; return Act.Panic; }
            if (!restarted && dt >= RestartAt) { restarted = true; return Act.Restart; }
            if (restarted && thrust >= ThrustOk) { Finish(now, true); return Act.Done; }
            return Act.None;
        }
        void Finish(double now, bool ok) { Running = false; Ok = ok; Seconds = now - t0; AltLoss = alt0 - minAlt; SpdLoss = spd0 - minSpd; }
        internal string Pick(string[] pool) { return pool[rng.Next(pool.Length)]; }
    }
}