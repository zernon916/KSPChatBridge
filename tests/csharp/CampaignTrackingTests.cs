using System;
using KSPChatBridge;

internal static class CampaignTrackingTests
{
    static int checks;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("CampaignTrackingTests: " + name);
        checks++;
    }

    internal static void Run()
    {
        checks = 0;
        var phase = new CampaignPhase { Goal = "Deploy relay", Craft = "Relay" };
        Check(CampaignTracking.VesselLabel(phase, null, false) == "Tracked vessel: none",
            "reports an unassigned phase");

        CampaignTracking.Track(phase, " vessel-42 ");
        Check(phase.VesselId == "vessel-42", "trims and stores the stable vessel ID");
        Check(CampaignTracking.IsAssignedTo(phase.VesselId, "vessel-42"),
            "matches the assigned vessel ID");
        Check(!CampaignTracking.IsAssignedTo(phase.VesselId, "Vessel-42") &&
            !CampaignTracking.IsAssignedTo(phase.VesselId, null), "does not match different or missing IDs");
        Check(CampaignTracking.VesselLabel(phase, "Relay One", true) ==
            "Tracked vessel: Relay One (active)", "labels an active tracked vessel");
        Check(CampaignTracking.VesselLabel(phase, "Relay One", false) ==
            "Tracked vessel: Relay One (loaded)", "labels a loaded non-active vessel");
        Check(CampaignTracking.VesselLabel(phase, null, false) ==
            "Tracked vessel ID: vessel-42 (not loaded)", "retains the ID label when the vessel is absent");

        bool rejected = false;
        try { CampaignTracking.Track(phase, " "); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "rejects an empty vessel ID");
        Console.WriteLine("CampaignTrackingTests: " + checks + " behavior checks passed.");
    }
}
