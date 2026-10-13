using System;

namespace KSPChatBridge
{
    internal static class StagingPolicy
    {
        internal static string ShouldStage(bool flameout, bool tankEmpty, double secondsSinceEmpty)
        {
            if ((flameout || tankEmpty) && secondsSinceEmpty >= 0.5)
            {
                return flameout ? "flameout" : "tank empty";
            }
            return null;
        }
    }
}
