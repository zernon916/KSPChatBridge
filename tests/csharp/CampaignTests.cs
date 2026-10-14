using System;
using System.Collections.Generic;
using System.IO;
using KSPChatBridge;

internal static class CampaignTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("CampaignTests: " + name);
        passes++;
    }

    internal static void Run()
    {
        passes = 0;
        var campaign = new Campaign { Name = "Mun program" };
        var launch = Phase("Launch", "Aster");
        var landing = Phase("Land", "Kestrel");
        campaign.AddPhase(launch);
        campaign.AddPhase(landing);
        Check(campaign.Phases.Count == 2 && campaign.Phases[0] == launch, "creates ordered phases");
        Check(!campaign.MovePhase(0, -1) && !campaign.MovePhase(1, 2), "rejects out-of-range and invalid moves");
        Check(campaign.MovePhase(1, -1) && campaign.Phases[0] == landing, "moves a phase up");
        Check(campaign.MovePhase(0, 1) && campaign.Phases[1] == landing, "moves a phase down");

        var edited = Phase("Deploy relay", "Aster II");
        edited.Status = "In progress";
        edited.VesselId = "vessel-42";
        campaign.UpdatePhase(0, edited);
        Check(campaign.Phases[0].Goal == "Deploy relay" && campaign.Phases[0].Craft == "Aster II",
            "edits phase fields");
        bool rejected = false;
        try { campaign.AddPhase(Phase("", "Invalid")); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "rejects a phase without a goal");

        Dictionary<string, object> json = campaign.ToJson();
        Campaign loaded = Campaign.FromJson(MiniJson.Deserialize(MiniJson.Serialize(json)));
        Check(loaded.Name == campaign.Name && loaded.Phases.Count == 2 &&
            loaded.Phases[0].VesselId == "vessel-42" && loaded.Phases[0].Status == "In progress",
            "round-trips campaign, phase order, and status");
        string path = Path.Combine(Path.GetTempPath(), "aics_campaign_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            CampaignStore.Save(path, campaign);
            Campaign stored = CampaignStore.Load(path);
            Check(stored.Name == campaign.Name && stored.Phases.Count == campaign.Phases.Count,
                "persists and loads a campaign");
            stored.Name = "Updated program";
            CampaignStore.Save(path, stored);
            Check(CampaignStore.Load(path).Name == "Updated program", "atomically replaces a saved campaign");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
        rejected = false;
        try { Campaign.FromJson(new Dictionary<string, object>()); }
        catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected, "rejects unsupported campaign data");
        Console.WriteLine("CampaignTests: " + passes + " behavior checks passed.");
    }

    static CampaignPhase Phase(string goal, string craft)
    {
        return new CampaignPhase { Goal = goal, Craft = craft, Status = "Planned", Alt = 80000, Inc = 0 };
    }
}
