using System;

namespace KSPChatBridge
{
    internal static class CampaignPhaseChatter
    {
        internal static bool TryStart(CampaignPhase phase, string vesselId)
        {
            if (phase == null || !MatchesVessel(phase, vesselId) ||
                (!string.IsNullOrWhiteSpace(phase.Status) &&
                 !string.Equals(phase.Status, "Planned", StringComparison.OrdinalIgnoreCase)))
                return false;

            phase.Status = "Started";
            return true;
        }

        internal static bool TryComplete(CampaignPhase phase, string vesselId)
        {
            if (phase == null || !MatchesVessel(phase, vesselId) ||
                !string.Equals(phase.Status, "Started", StringComparison.OrdinalIgnoreCase))
                return false;

            phase.Status = "Complete";
            return true;
        }

        internal static bool TryCompleteAtTargetOrbit(CampaignPhase phase, string vesselId,
            double apoapsisAltitude, double periapsisAltitude, double inclinationDeg)
        {
            return TargetOrbitReached(apoapsisAltitude, periapsisAltitude, inclinationDeg, phase) &&
                TryComplete(phase, vesselId);
        }

        internal static bool TargetOrbitReached(double apoapsisAltitude, double periapsisAltitude,
            double inclinationDeg, CampaignPhase phase)
        {
            if (phase == null || !Finite(apoapsisAltitude) || !Finite(periapsisAltitude) ||
                !Finite(inclinationDeg) || !Finite(phase.Alt) || !Finite(phase.Inc) ||
                phase.Alt < 0 || phase.Inc < 0 || phase.Inc > 180)
                return false;

            double altitudeTolerance = Math.Max(1000, phase.Alt * .05);
            return Math.Abs(apoapsisAltitude - phase.Alt) <= altitudeTolerance &&
                Math.Abs(periapsisAltitude - phase.Alt) <= altitudeTolerance &&
                Math.Abs(inclinationDeg - phase.Inc) <= 2;
        }

        static bool MatchesVessel(CampaignPhase phase, string vesselId)
        {
            return phase != null && !string.IsNullOrWhiteSpace(phase.VesselId) &&
                !string.IsNullOrWhiteSpace(vesselId) &&
                string.Equals(phase.VesselId, vesselId, StringComparison.Ordinal);
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
