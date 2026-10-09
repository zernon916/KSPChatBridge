using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class ChatRequest
    {
        internal string Id, Text, Provider;
        internal bool UserPriority;
        internal DateTime DeadlineUtc;
    }
    internal sealed class ChatResult
    {
        internal string Id, Text, Error;
        internal List<string> ToolCalls = new List<string>();
        internal bool Cancelled;
    }
    // Provider-neutral chat queue: user chat beats crew chatter; tools must go through NativeCommands.
    internal sealed class ChatOrchestrator
    {
        internal const int MaxToolLoops = 6;
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
        internal ChatRequest DequeueNext(DateTime utcNow)
        {
            while (user.Count > 0)
            {
                var r = user.Dequeue();
                if (cancelled.Contains(r.Id) || r.DeadlineUtc < utcNow) continue;
                return r;
            }
            while (crew.Count > 0)
            {
                var r = crew.Dequeue();
                if (cancelled.Contains(r.Id) || r.DeadlineUtc < utcNow) continue;
                return r;
            }
            return null;
        }
        internal ChatResult RunTools(string requestId, IEnumerable<string> toolNames, Func<string, string> execute, bool aiEnabled)
        {
            var result = new ChatResult { Id = requestId };
            if (!aiEnabled) { result.Error = "AI off."; return result; }
            if (cancelled.Contains(requestId)) { result.Cancelled = true; return result; }
            int loops = 0;
            foreach (string name in toolNames ?? new string[0])
            {
                if (++loops > MaxToolLoops) { result.Error = "Tool loop limit reached."; break; }
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
}
