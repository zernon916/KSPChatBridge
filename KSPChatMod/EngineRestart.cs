using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class EngineRestart<T>
    {
        readonly List<T> pending = new List<T>();
        double due;
        internal static bool Eligible(bool ignited, bool restartable, int stage, int currentStage)
        { return !ignited && restartable && (stage == -1 || stage >= currentStage); }
        internal bool Schedule(IEnumerable<T> engines, double now)
        {
            if (pending.Count > 0) return false;
            pending.AddRange(engines); due = now + 4;
            return pending.Count > 0;
        }
        internal void Cancel() { pending.Clear(); }
        internal int Tick(double now, Func<T, bool> eligible, Action<T> activate)
        {
            if (now < due || pending.Count == 0) return 0;
            var ready = pending.ToArray(); pending.Clear(); int count = 0;
            foreach (var engine in ready) if (eligible(engine)) { activate(engine); count++; }
            return count;
        }
    }
}
