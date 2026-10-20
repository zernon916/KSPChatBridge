using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>FIX: every KSPChatBridge control lock goes through here - logged '[input] lock <id> set/removed'. A watchdog drops a leaked
    /// IVA-MFD typing lock (ALLBUTCAMERAS: no keys, PAW sliders snap back) when nobody is typing, and all of ours on a scene change.</summary>
    internal static class InputLocks
    {
        internal static void Set(ControlTypes t, string id)
        {
            bool had = InputLockManager.lockStack != null && InputLockManager.lockStack.ContainsKey(id);
            InputLockManager.SetControlLock(t, id); if (!had) ChatLog.Write("input", "lock " + id + " set (" + t + ")");
        }
        internal static void Remove(string id)
        {
            bool had = InputLockManager.lockStack != null && InputLockManager.lockStack.ContainsKey(id);
            InputLockManager.RemoveControlLock(id); if (had) ChatLog.Write("input", "lock " + id + " removed");
        }
        internal static string Dump()
        {
            try { var s = InputLockManager.lockStack; return s == null || s.Count == 0 ? "none" : string.Join(", ", s.Select(kv => kv.Key + "=" + kv.Value).ToArray()); } catch (Exception ex) { return "? " + ex.Message; }
        }
        /// <summary>Lock ids that may linger after the owner is gone (AicsMenu's own locks are left to AicsMenu).</summary>
        internal static readonly string[] Ours = { AicsIvaMfd.TypingLockId, TabletInputLock.Id };
        internal static void ClearOurs(string why)
        {
            foreach (string id in Ours) if (InputLockManager.lockStack != null && InputLockManager.lockStack.ContainsKey(id)) { InputLockManager.RemoveControlLock(id); ChatLog.Write("input", "lock " + id + " removed (" + why + ")"); }
        }
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class InputLockWatchdog : MonoBehaviour
    {
        float next;
        void Awake() { DontDestroyOnLoad(this); GameEvents.onGameSceneLoadRequested.Add(OnScene); }
        void OnDestroy() { GameEvents.onGameSceneLoadRequested.Remove(OnScene); }
        void OnScene(GameScenes s) { AicsIvaMfd.ResetTyping(); InputLocks.ClearOurs("scene change -> " + s); }
        void Update()
        {
            if (Time.realtimeSinceStartup < next) return; next = Time.realtimeSinceStartup + 1;
            try
            {
                var st = InputLockManager.lockStack;
                if (st != null && st.ContainsKey(AicsIvaMfd.TypingLockId) && !AicsIvaMfd.TypingNow) { AicsIvaMfd.ResetTyping(); InputLockManager.RemoveControlLock(AicsIvaMfd.TypingLockId); ChatLog.Write("input", "lock " + AicsIvaMfd.TypingLockId + " removed (leaked: nobody typing)"); }
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] input watchdog: " + ex.Message); }
        }
    }
}
