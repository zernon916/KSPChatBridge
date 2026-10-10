namespace KSPChatBridge
{
    internal static class ReversePolicy
    {
        internal static bool? EventDirection(string name)
        {
            string value = (name ?? "").ToLowerInvariant();
            if (value.Contains("toggle")) return null;
            if (value.Contains("forward") && value.Contains("thrust")) return false;
            if (value.Contains("revers") && (value.Contains("thrust") || value.Contains("reverser"))) return true;
            return null;
        }
        internal static bool? ForwardPrimary(string primary, string secondary)
        {
            bool a = (primary ?? "").IndexOf("revers", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool b = (secondary ?? "").IndexOf("revers", System.StringComparison.OrdinalIgnoreCase) >= 0;
            return a == b ? (bool?)null : !a;
        }
    }
    internal sealed class RecoveryGate
    {
        double? changedAt; double fumble = 3;
        static readonly System.Random Rng = new System.Random();
        /// <summary>True on the tick the change is first noticed (pilot callout moment).</summary>
        internal bool JustNoticed;
        internal bool Tick(bool changed, double now)
        {
            JustNoticed = false;
            if (!changed) { changedAt = null; return false; }
            if (!changedAt.HasValue || now < changedAt.Value) { changedAt = now; JustNoticed = true; lock (Rng) fumble = 2 + 2 * Rng.NextDouble(); }
            return now - changedAt.Value >= fumble;
        }
        internal double Fumble { get { return fumble; } }
        internal void Reset() { changedAt = null; }
    }
}
