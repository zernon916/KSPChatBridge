using System;

namespace KSPChatBridge
{
    internal static class CampaignTracking
    {
        internal static void Track(CampaignPhase phase, string vesselId)
        {
            if (phase == null) throw new ArgumentNullException("phase");
            if (string.IsNullOrWhiteSpace(vesselId))
                throw new ArgumentException("A vessel ID is required.", "vesselId");
            phase.VesselId = vesselId.Trim();
        }

        internal static bool IsAssignedTo(string trackedVesselId, string vesselId)
        {
            return !string.IsNullOrWhiteSpace(trackedVesselId) &&
                !string.IsNullOrWhiteSpace(vesselId) &&
                string.Equals(trackedVesselId, vesselId, StringComparison.Ordinal);
        }

        internal static string VesselLabel(CampaignPhase phase, string vesselName, bool isActive)
        {
            if (phase == null || string.IsNullOrWhiteSpace(phase.VesselId))
                return "Tracked vessel: none";
            if (string.IsNullOrWhiteSpace(vesselName))
                return "Tracked vessel ID: " + phase.VesselId + " (not loaded)";
            return "Tracked vessel: " + vesselName + (isActive ? " (active)" : " (loaded)");
        }
    }
}
