using System;

namespace KSPChatBridge
{
    // Pure policy, separately exercised without loading Unity.
    internal sealed class RestartPolicy
    {
        int failures;
        double nextAttempt;
        internal bool CanStart(double now, bool enabled, bool quitting, bool starting, bool alive)
        { return enabled && !quitting && !starting && !alive && now >= nextAttempt; }
        internal void Failed(double now)
        { nextAttempt = now + Math.Min(60, 2 * Math.Pow(2, Math.Min(failures++, 5))); }
        internal void Stable() { failures = 0; }
    }
}
