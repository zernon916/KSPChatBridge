using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal static class CampaignDvCheck
    {
        internal static bool TryRequiredDeltaV(IList<MissionLeg> legs, out double required)
        {
            required = 0;
            if (legs == null || legs.Count == 0) return false;

            for (int i = 0; i < legs.Count; i++)
            {
                MissionLeg leg = legs[i];
                if (leg == null || !Finite(leg.PlannedDv) || leg.PlannedDv < 0) return false;
                required += leg.PlannedDv;
                if (!Finite(required)) return false;
            }
            return true;
        }

        internal static bool HasEnoughDeltaV(double required, double available)
        {
            return Finite(required) && required >= 0 &&
                Finite(available) && available >= 0 && available >= required;
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
