namespace KSPChatBridge
{
    internal static class ReversePolicy
    {
        internal static bool? ForwardPrimary(string primary, string secondary)
        {
            bool a = (primary ?? "").IndexOf("revers", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool b = (secondary ?? "").IndexOf("revers", System.StringComparison.OrdinalIgnoreCase) >= 0;
            return a == b ? (bool?)null : !a;
        }
    }
    internal sealed class RecoveryGate
    {
        double? changedAt;
        internal bool Tick(bool changed, double now)
        {
            if (!changed) { changedAt = null; return false; }
            if (!changedAt.HasValue || now < changedAt.Value) changedAt = now;
            return now - changedAt.Value >= 3;
        }
        internal void Reset() { changedAt = null; }
    }
}
