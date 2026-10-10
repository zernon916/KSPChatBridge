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
