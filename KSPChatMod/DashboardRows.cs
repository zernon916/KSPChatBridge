using System.Collections.Generic;

namespace KSPChatBridge
{
    // Autopilot/phase rows from the in-mod controller.
    internal static class DashboardRows
    {

        internal static List<string[]> Local(string phase, string plan)
        {
            return new List<string[]> { new[] { "source", "local (in-mod)" }, new[] { "autopilot", "Local " + (phase ?? "idle") }, new[] { "plan", plan ?? "" } };
        }
    }
}
