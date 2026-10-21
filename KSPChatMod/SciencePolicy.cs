using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    // P5-3: pure lifecycle + science decisions (parity with kspchat/science.py and ksp_actions lifecycle tools).
    internal static class SciencePolicy
    {
        internal const double MinEcPct = 10.0, MinEcTransmitPct = 25.0, PollSeconds = 3.0, MinPostGapSeconds = 60.0;
        internal static readonly string[] Modes = { "auto", "remind", "off" };

        /// <summary>Normalised watcher mode, or null when unknown (same aliases as science.set_mode).</summary>
        internal static string NormalizeMode(string mode)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            switch (m)
            {
                case "on": case "true": case "auto on": return "auto";
                case "auto off": case "false": return "remind";
            }
            return Array.IndexOf(Modes, m) >= 0 ? m : null;
        }

        internal static double EcPct(double amount, double max) { return max > 0 ? 100.0 * amount / max : 0; }

        /// <summary>Experiment requirements (ExperimentUsageReqs: 1 control, 2 crew in vessel, 4 crew in part, 8 scientist) and
        /// atmosphere; unrunnable ones are skipped silently (no "cab needs to be manned" spam).</summary>
        /// <summary>RS7n (Luke Oct 21 03:44: every transmission "R&D received ... +0.0 sci, subject now 0.0/8.0", stock "+10 data on Crew
        /// Report from Runway." with no reward): stock gatherData builds ScienceData with transmitBonus = 0 and only the results dialog page
        /// (ExperimentResultDialogPage) sets it to 1 + comm bonus. Our silent gather never opens that page, and the transmitter credits
        /// dataAmount x scienceValueRatio at xmit = baseTransmitValue x transmitBonus = 1.0 (Science-Full-Transmit) x 0 = nothing.
        /// Before transmitting: bonus 0 -> 1, base 0 -> the module's xmitDataScalar (Science-Full-Transmit makes that 1.0 = full value).</summary>
        /// <summary>RS7o: an EVA-situation experiment (stock evaScience) cannot run unless the vessel is a kerbal on EVA.</summary>
        internal static bool NeedsEva(bool requiresEvaSituation, bool isEva) { return requiresEvaSituation && !isEva; }
        internal static bool FixTransmit(ref float baseTransmit, ref float bonus, float moduleXmit)
        {
            bool changed = false;
            if (!(baseTransmit > 0) && moduleXmit > 0) { baseTransmit = moduleXmit; changed = true; }
            if (!(bonus > 0)) { bonus = 1f; changed = true; }
            return changed;
        }
        internal static bool CanRun(int usageMask, int vesselCrew, int partCrew, bool scientist, bool controllable, bool needAtmo, bool inAtmo)
        {
            if (needAtmo && !inAtmo) return false;
            if ((usageMask & 1) != 0 && !controllable) return false;
            if ((usageMask & 2) != 0 && vesselCrew <= 0) return false;
            if ((usageMask & 4) != 0 && partCrew <= 0) return false;
            if ((usageMask & 8) != 0 && !scientist) return false;
            return true;
        }
        internal static bool Eligible(bool inoperable, bool hasData, bool available, bool subjectDone, bool rerunnable, bool onlyRerunnable)
        {
            if (inoperable || hasData || !available || subjectDone) return false;
            return !onlyRerunnable || rerunnable;
        }

        /// <summary>Min recoverable science to bother running (AUTO must not waste one-shot copies for ~0 sci).</summary>
        internal const double MinSubjectValue = 0.5;

        /// <summary>Why this module must not run, or null to RUN. One module per experimentID per situation;
        /// spare copies stay sealed for the next biome/situation.</summary>
        internal static string AutoSkipReason(bool inoperable, bool hasData, bool available, bool storedOnboard, double subjectValue, bool ranThisType, bool onlyRerunnable, bool rerunnable, double minValue = double.NaN)
        {
            if (ranThisType) return "spare copy";
            if (inoperable) return "inoperable";
            if (hasData || storedOnboard) return "already have";
            if (!available) return "not available";
            if (subjectValue <= (double.IsNaN(minValue) ? MinSubjectValue : Math.Max(0, minValue))) return "0 sci";   // RS2: the rover runs anything above its own minimum (default 0)
            if (onlyRerunnable && !rerunnable) return "one-shot";
            return null;
        }

        /// <summary>One log line: [science] goo @ Kerbin/Shores/flyingLow: RUN (12.5 sci) | SKIP (…).</summary>
        internal static string FormatDecision(string expId, string body, string biome, string situation, bool run, double value, string skipWhy)
        {
            string id = string.IsNullOrEmpty(expId) ? "?" : expId;
            string where = (body ?? "?") + (string.IsNullOrEmpty(biome) ? "" : "/" + biome) + "/" + (situation ?? "?");
            if (run) return "[science] " + id + " @ " + where + ": RUN (" + value.ToString("0.0") + " sci)";
            return "[science] " + id + " @ " + where + ": SKIP (" + (skipWhy ?? "?") + ")";
        }

        /// <summary>Why experiments must not run now, or null.</summary>
        internal static string RunBlocker(double ecPct)
        {
            return ecPct < MinEcPct ? "electric charge below " + MinEcPct.ToString("0") + "%" : null;
        }

        internal static bool ShouldTransmit(bool transmit, int ran, double ecPct) { return transmit && ran > 0 && ecPct >= MinEcTransmitPct; }

        /// <summary>Watcher situation-change gate. Returns "ignore" (no change / mode off), "first" (just loaded or vessel
        /// switched: remember, stay quiet) or "act".</summary>
        internal static string SituationChange(string mode, string lastKey, string lastVessel, string key, string vessel)
        {
            if (mode == "off") return "ignore";
            if (key == lastKey) return "ignore";
            if (lastKey == null || lastVessel != vessel) return "first";
            return "act";
        }

        internal static bool ShouldPost(string mode, int ran, string blocker, int oneShotLeft, double sinceLastPost)
        {
            if (mode == "auto" && ran == 0 && blocker == null) return false;
            if (mode != "auto" && ran == 0 && blocker == null && oneShotLeft == 0) return false;
            return ran > 0 || sinceLastPost >= MinPostGapSeconds;
        }

        // ---- lifecycle gates ----
        /// <summary>deploy_parachutes refusal (python parity), or null to deploy.</summary>
        internal static string ParachuteGate(bool force, string situation, double verticalSpeed)
        {
            string s = (situation ?? "").ToUpperInvariant();
            if (force) return null;
            if (s == "PRELAUNCH" || s == "LANDED" || s == "SPLASHED" || verticalSpeed > 5)
                return "Not deploying parachutes now (situation " + (situation ?? "?").ToLowerInvariant() + ", vertical speed " + Math.Round(verticalSpeed) + " m/s). Use force=true to override.";
            return null;
        }

        /// <summary>Default launch site for an editor (VAB -> LaunchPad, SPH -> Runway).</summary>
        internal static string LaunchSite(string editor, string site)
        {
            if (!string.IsNullOrEmpty(site) && site.Trim().Length > 0) return site.Trim();
            return (editor ?? "VAB").Trim().ToUpperInvariant() == "SPH" ? "Runway" : "LaunchPad";
        }

        internal static string EditorFolder(string editor) { return (editor ?? "VAB").Trim().ToUpperInvariant() == "SPH" ? "SPH" : "VAB"; }

        /// <summary>Craft file name safety (no paths).</summary>
        internal static bool SafeCraftName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Trim().Length > 0 && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && name.IndexOf("..", StringComparison.Ordinal) < 0;
        }
    }
}
