// RS7h: rover auto-EVA science (pure, tested). At a tour stop with EVA-only science left (surface sample / EVA report worth > 0 here),
// one crew member (scientist first) steps out of their hatch, runs the EVA experiments where they stand and boards the same part again.
// The state machine owns the timing and the fallback: no settle / drift / boarding failure = keep the rover parked and ask the player
// to board by hand (the tour never drives off with a kerbal outside). KSP glue: NativeRoverEva.cs.
using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal enum EvaPhase { Idle, Settling, Science, Boarding, Manual, Done }
    internal enum EvaAction { None, RunScience, TryBoard, Alert, Finish }

    internal static class RoverEvaPolicy
    {
        /// <summary>EVA when enabled, someone can step out, and the sample (if the R&D building allows it) or the EVA report is worth anything here.</summary>
        internal static bool ShouldEva(bool enabled, int cabinCrew, double sampleGain, double reportGain, bool sampleAllowed, double evaSciGain = 0, bool kitAboard = false)
        {
            if (!enabled || cabinCrew <= 0) return false;
            return (sampleAllowed && sampleGain > 0) || reportGain > 0 || (kitAboard && evaSciGain > 0);   // RS7p: EVA Science alone is worth a trip out
        }
        /// <summary>RS7p: when EVA Science is worth anything, send a kit carrier (a scientist among them first); else PickCrew.</summary>
        internal static int PickCrew(IList<string> traits, IList<bool> hasKit, bool wantKit)
        {
            if (wantKit && hasKit != null && traits != null)
            {
                for (int i = 0; i < traits.Count && i < hasKit.Count; i++) if (hasKit[i] && traits[i] == "Scientist") return i;
                for (int i = 0; i < traits.Count && i < hasKit.Count; i++) if (hasKit[i]) return i;
            }
            return PickCrew(traits);
        }
        /// <summary>Index of the crew member to send: the first scientist, else the first one; -1 = nobody.</summary>
        internal static int PickCrew(IList<string> traits)
        {
            if (traits == null || traits.Count == 0) return -1;
            for (int i = 0; i < traits.Count; i++) if (traits[i] == "Scientist") return i;
            return 0;
        }
    }

    internal sealed class RoverEvaRun
    {
        internal const double SettleMin = 1, SettleTimeout = 10, BoardInterval = 1.5, MaxDriftM = 10, LostAfter = 3;
        internal const int BoardTries = 3;
        internal EvaPhase Phase = EvaPhase.Idle;
        internal string Why = "";
        internal int BoardAttempts;
        double since, lastBoard;
        internal bool Busy { get { return Phase == EvaPhase.Settling || Phase == EvaPhase.Science || Phase == EvaPhase.Boarding || Phase == EvaPhase.Manual; } }

        /// <summary>The spawn call returned: ok = wait for the kerbal to settle; failed = nobody left the rover, nothing to wait for.</summary>
        internal void Spawned(double now, bool ok, string why)
        {
            since = now; BoardAttempts = 0;
            if (ok) Phase = EvaPhase.Settling; else { Phase = EvaPhase.Done; Why = string.IsNullOrEmpty(why) ? "spawn failed" : why; }
        }
        /// <summary>holdS: RS7p - EVA Science plays an animation before its data exists; hold the first boarding try up to holdS
        /// (DataReady ends the hold early).</summary>
        internal void ScienceDone(double now, double holdS = 0) { if (Phase == EvaPhase.Science) { Phase = EvaPhase.Boarding; since = now; lastBoard = now + Math.Max(0, holdS); } }
        internal bool Holding(double now) { return Phase == EvaPhase.Boarding && BoardAttempts == 0 && now < lastBoard; }
        internal void DataReady(double now) { if (Holding(now)) lastBoard = now - BoardInterval; }
        /// <summary>Player pressed Resume: only when the kerbal is no longer outside (aboard, or gone).</summary>
        internal bool Resume(bool kerbalOutside)
        {
            if (Phase != EvaPhase.Manual || kerbalOutside) return false;
            Phase = EvaPhase.Done; return true;
        }

        /// <summary>One tick. exists = the EVA kerbal vessel is loaded; settled = on the ground and still; driftM = from the hatch part;
        /// aboard = back in the rover's crew.</summary>
        internal EvaAction Tick(double now, bool exists, bool settled, double driftM, bool aboard)
        {
            switch (Phase)
            {
                case EvaPhase.Settling:
                    if (aboard) { Phase = EvaPhase.Done; return EvaAction.Finish; }
                    if (!exists) return now - since > LostAfter ? Fail("kerbal not found after the EVA") : EvaAction.None;
                    if (driftM > MaxDriftM) return Fail("kerbal drifted " + driftM.ToString("0") + " m");
                    if ((settled && now - since >= SettleMin) || now - since >= SettleTimeout) { Phase = EvaPhase.Science; return EvaAction.RunScience; }
                    return EvaAction.None;
                case EvaPhase.Science:
                    return EvaAction.None;   // the caller runs the science and calls ScienceDone
                case EvaPhase.Boarding:
                    if (aboard) { Phase = EvaPhase.Done; return EvaAction.Finish; }
                    if (!exists && now - lastBoard > LostAfter) return Fail("kerbal lost while boarding");
                    if (exists && driftM > MaxDriftM) return Fail("kerbal drifted " + driftM.ToString("0") + " m");
                    if (now - lastBoard < BoardInterval) return EvaAction.None;
                    if (BoardAttempts >= BoardTries) return Fail("boarding failed " + BoardTries + " times");
                    BoardAttempts++; lastBoard = now; return EvaAction.TryBoard;
                case EvaPhase.Manual:
                    if (aboard) { Phase = EvaPhase.Done; return EvaAction.Finish; }
                    return EvaAction.None;
                default:
                    return EvaAction.None;
            }
        }
        EvaAction Fail(string why) { Phase = EvaPhase.Manual; Why = why; return EvaAction.Alert; }
    }
}