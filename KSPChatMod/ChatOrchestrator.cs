using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class ChatRequest
    {
        internal string Id, Text, Provider, Session;
        internal string Speaker, Persona;   // native voice: who says the reply + their system-prompt lines
        internal bool UserPriority;
        internal DateTime DeadlineUtc;
        internal Action Settled;   // exactly-once: invoked when the request runs or is dropped (releases UI pending)
    }
    internal sealed class ChatResult
    {
        internal string Id, Text, Error;
        internal List<string> ToolCalls = new List<string>();
        internal bool Cancelled;
    }
    // Provider-neutral chat queue: user chat beats crew chatter; tools must go through NativeCommands.
    // This is the real owner of in-mod chat queueing (P5-1): InModAiHost pumps requests out of here.
    internal sealed class ChatOrchestrator
    {
        internal const int MaxToolLoops = 8;   // per-request tool-execution budget (parity: config.MAX_TOOL_ROUNDS)
        readonly Queue<ChatRequest> user = new Queue<ChatRequest>();
        readonly Queue<ChatRequest> crew = new Queue<ChatRequest>();
        readonly HashSet<string> cancelled = new HashSet<string>();
        internal int Pending { get { return user.Count + crew.Count; } }
        internal void Enqueue(ChatRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.Text)) throw new ArgumentException("Chat request required.");
            if (string.IsNullOrEmpty(request.Id)) request.Id = Guid.NewGuid().ToString("N");
            if (request.DeadlineUtc == default(DateTime)) request.DeadlineUtc = DateTime.UtcNow.AddSeconds(60);
            (request.UserPriority ? user : crew).Enqueue(request);
        }
        internal void Cancel(string id) { if (!string.IsNullOrEmpty(id)) cancelled.Add(id); }
        /// <summary>Drop everything queued (chat window Clear); returns how many were dropped.</summary>
        internal int CancelAll()
        {
            int n = user.Count + crew.Count;
            foreach (var r in user) Settle(r);
            foreach (var r in crew) Settle(r);
            user.Clear();
            crew.Clear();
            return n;
        }
        internal ChatRequest DequeueNext(DateTime utcNow)
        {
            while (user.Count > 0)
            {
                var r = user.Dequeue();
                if (cancelled.Remove(r.Id) || r.DeadlineUtc < utcNow) { Settle(r); continue; }
                return r;
            }
            while (crew.Count > 0)
            {
                var r = crew.Dequeue();
                if (cancelled.Remove(r.Id) || r.DeadlineUtc < utcNow) { Settle(r); continue; }
                return r;
            }
            return null;
        }
        static void Settle(ChatRequest r)
        {
            try { if (r != null && r.Settled != null) r.Settled(); }
            catch (Exception) { }
        }
        internal ChatResult RunTools(string requestId, IEnumerable<string> toolNames, Func<string, string> execute, bool aiEnabled)
        {
            var result = new ChatResult { Id = requestId };
            if (!aiEnabled) { result.Error = "AI off."; return result; }
            if (cancelled.Remove(requestId)) { result.Cancelled = true; return result; }
            var guard = new ToolLoopGuard(MaxToolLoops);
            foreach (string name in toolNames ?? new string[0])
            {
                if (!guard.TryStep()) { result.Error = "Tool loop limit reached."; break; }
                if (!NativeCommands.IsPorted(name)) { result.Error = "Tool not on native command boundary: " + name; break; }
                string outcome = execute(name);
                result.ToolCalls.Add(name + " => " + outcome);
            }
            return result;
        }
        // Cloud providers must never receive local transcripts after a local-model failure unless chosen.
        internal static bool AllowCloudFallback(string activeProvider, bool userSelectedCloud)
        {
            if (userSelectedCloud) return true;
            return activeProvider != "local" && activeProvider != "lmstudio" && activeProvider != "ollama";
        }
    }

    /// <summary>Bounded per-request tool-execution budget (multi-turn agent loops must stay finite).</summary>
    internal sealed class ToolLoopGuard
    {
        readonly int limit;
        int used;
        internal ToolLoopGuard(int limit) { this.limit = limit < 1 ? 1 : limit; }
        internal int Used { get { return used; } }
        internal bool TryStep()
        {
            if (used >= limit) return false;
            used++;
            return true;
        }
    }
}
