using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>One window's OnGUI exception must never hide the others: logged once per window+message, then swallowed.</summary>
    internal static class GuiGuard
    {
        static readonly HashSet<string> Seen = new HashSet<string>();
        internal static void Log(string who, Exception ex)
        {
            string k = who + ":" + ex.GetType().Name + ":" + ex.Message;
            lock (Seen) { if (Seen.Count > 200 || !Seen.Add(k)) return; }
            UnityEngine.Debug.LogError("[AICS] " + who + " GUI error (window skipped this frame): " + ex);
        }
    }
}
