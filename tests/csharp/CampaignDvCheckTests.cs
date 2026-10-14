using System;
using System.Collections.Generic;
using KSPChatBridge;

internal static class CampaignDvCheckTests
{
    static int checks;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("CampaignDvCheckTests: " + name);
        checks++;
    }

    internal static void Run()
    {
        checks = 0;
        double required;
        Check(CampaignDvCheck.TryRequiredDeltaV(new List<MissionLeg>
        {
            new MissionLeg { PlannedDv = 800 },
            new MissionLeg { PlannedDv = 225 }
        }, out required) && required == 1025, "sums planned legs");
        Check(!CampaignDvCheck.TryRequiredDeltaV(null, out required), "rejects missing plan");
        Check(!CampaignDvCheck.TryRequiredDeltaV(new List<MissionLeg>(), out required), "rejects empty plan");
        Check(!CampaignDvCheck.TryRequiredDeltaV(new List<MissionLeg>
        {
            new MissionLeg { PlannedDv = 100 },
            null
        }, out required), "rejects missing leg");
        Check(!CampaignDvCheck.TryRequiredDeltaV(new List<MissionLeg>
        {
            new MissionLeg { PlannedDv = -1 }
        }, out required), "rejects negative planned delta-v");
        Check(!CampaignDvCheck.TryRequiredDeltaV(new List<MissionLeg>
        {
            new MissionLeg { PlannedDv = double.NaN }
        }, out required), "rejects non-finite planned delta-v");
        Check(CampaignDvCheck.HasEnoughDeltaV(1000, 1000), "accepts exact stage budget");
        Check(CampaignDvCheck.HasEnoughDeltaV(900, 1000), "accepts surplus stage budget");
        Check(!CampaignDvCheck.HasEnoughDeltaV(1001, 1000), "rejects insufficient stage budget");
        Check(!CampaignDvCheck.HasEnoughDeltaV(double.NaN, 1000), "rejects invalid comparison values");
        Console.WriteLine("CampaignDvCheckTests: " + checks + " behavior checks passed.");
    }
}
