using System.Collections.Generic;

namespace KSPChatBridge
{
    // P5-1.8: dashboard honesty. Autopilot/phase rows come from the in-mod controller unless a bridge /status answer
    // is fresh; a failed or stale bridge poll never leaves old bridge AP/phase rows on screen looking live.
    internal static class DashboardRows
    {
        internal const double BridgeStaleSeconds = 5;

        internal static List<string[]> Local(string phase, string plan)
        {
            return new List<string[]> { new[] { "source", "local (in-mod)" }, new[] { "autopilot", "Local " + (phase ?? "idle") }, new[] { "plan", plan ?? "" } };
        }

        /// <summary>Bridge rows only when AI is on, the bridge answered, and that answer is fresh; otherwise local rows.</summary>
        internal static List<string[]> Choose(bool aiEnabled, List<string[]> bridgeRows, double bridgeAgeSeconds, string phase, string plan)
        {
            if (!aiEnabled || bridgeRows == null || bridgeRows.Count == 0 || bridgeAgeSeconds < 0 || bridgeAgeSeconds > BridgeStaleSeconds)
                return Local(phase, plan);
            var r = new List<string[]> { new[] { "source", "bridge" } };
            r.AddRange(bridgeRows);
            return r;
        }
    }
}
