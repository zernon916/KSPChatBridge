using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal static class LegBudget
    {
        internal sealed class Leg
        {
            public string Name;
            public double Dv;
        }

        internal static double LaunchDv(double mu, double radius, double orbitAlt, double atmoDepth)
        {
            double v = Math.Sqrt(mu / (radius + orbitAlt));
            // Rough estimate for atmospheric and launch losses.
            double loss = atmoDepth > 0 ? 1.15 + atmoDepth / 70000 * 0.35 : 1.05;
            return v * loss;
        }

        internal static List<Leg> Legs(double launch, double transfer, double capture, double land, double ret)
        {
            List<Leg> legs = new List<Leg>();
            if (launch > 0) legs.Add(new Leg { Name = "launch", Dv = launch });
            if (transfer > 0) legs.Add(new Leg { Name = "transfer", Dv = transfer });
            if (capture > 0) legs.Add(new Leg { Name = "capture", Dv = capture });
            if (land > 0) legs.Add(new Leg { Name = "land", Dv = land });
            if (ret > 0) legs.Add(new Leg { Name = "return", Dv = ret });
            return legs;
        }
    }
}
