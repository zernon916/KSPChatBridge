using System;
using KSPChatBridge;

internal static class CampaignPhaseChatterTests
{
    static int checks;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("CampaignPhaseChatterTests: " + name);
        checks++;
    }

    internal static void Run()
    {
        checks = 0;
        var phase = new CampaignPhase
        {
            Goal = "Deploy relay",
            Craft = "Relay",
            VesselId = "relay-1",
            Status = "Planned",
            Alt = 80000,
            Inc = 5
        };
        Check(!CampaignPhaseChatter.TryStart(phase, "other"), "does not start for a different vessel");
        Check(CampaignPhaseChatter.TryStart(phase, "relay-1") && phase.Status == "Started",
            "launch starts the tracked planned phase");
        Check(!CampaignPhaseChatter.TryStart(phase, "relay-1"), "duplicate launch does not repeat phase start");
        Check(!CampaignPhaseChatter.TryComplete(phase, "other"), "does not complete for a different vessel");
        Check(CampaignPhaseChatter.TargetOrbitReached(82000, 79000, 6, phase),
            "accepts target apoapsis, periapsis, and inclination within tolerance");
        Check(!CampaignPhaseChatter.TargetOrbitReached(90000, 80000, 5, phase),
            "rejects orbit outside target altitude tolerance");
        Check(!CampaignPhaseChatter.TargetOrbitReached(80000, 80000, 9, phase),
            "rejects orbit outside target inclination tolerance");
        Check(CampaignPhaseChatter.TryComplete(phase, "relay-1") && phase.Status == "Complete",
            "target orbit completes the started phase");
        Check(!CampaignPhaseChatter.TryComplete(phase, "relay-1"), "duplicate orbit event does not repeat completion");
        Check(!CampaignPhaseChatter.TargetOrbitReached(double.NaN, 80000, 5, phase),
            "rejects unavailable orbit data");
        Console.WriteLine("CampaignPhaseChatterTests: " + checks + " behavior checks passed.");
    }
}
