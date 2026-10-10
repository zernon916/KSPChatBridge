// AICS core addon: AI on/off and the PluginData location. Settings: GameData/KSPChatBridge/PluginData/aics.cfg
// (ai_enabled = true|false; migrated once from the old bridge.cfg).
using System;
using System.IO;
using UnityEngine;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class AicsCore : MonoBehaviour
    {
        internal static volatile bool AiEnabled = true;
        internal static string PluginDataDirectory { get { return NativeLibraryLayout.PluginDataRoot(KSPUtil.ApplicationRootPath); } }
        /// <summary>Data folder (.env, memory, notes): KSPCHAT_DATA_DIR override, else PluginData.</summary>
        internal static string DataDirectory
        {
            get { string p = Environment.GetEnvironmentVariable("KSPCHAT_DATA_DIR"); return string.IsNullOrWhiteSpace(p) ? PluginDataDirectory : p; }
        }
        static string CfgPath { get { return Path.Combine(PluginDataDirectory, "aics.cfg"); } }

        void Awake()
        {
            DontDestroyOnLoad(this);
            AiEnabled = LoadAi();
            AiSettings.Load();
        }

        static bool LoadAi()
        {
            try
            {
                if (File.Exists(CfgPath)) return PluginDataMigration.ParseAiEnabled(File.ReadAllText(CfgPath), true);
                string old = Path.Combine(PluginDataDirectory, "bridge.cfg");
                bool ai = File.Exists(old) ? PluginDataMigration.ParseAiEnabled(File.ReadAllText(old), true) : true;
                Save(ai);
                return ai;
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] aics.cfg: " + ex.Message); return true; }
        }

        static void Save(bool ai)
        {
            try { Directory.CreateDirectory(PluginDataDirectory); File.WriteAllText(CfgPath, "# AICS settings\nai_enabled = " + (ai ? "true" : "false") + "\n"); }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] aics.cfg save: " + ex.Message); }
        }

        internal static void SetAiEnabled(bool enabled)
        {
            if (enabled == AiEnabled) return;
            if (NativeFlightController.Busy) { ChatWindow.Notice("Stop the local controller before switching AI mode."); return; }
            AiEnabled = enabled; Save(enabled);
            if (!enabled) InModAiHost.UnloadForAiOff();
            ChatWindow.Notice(enabled ? "AI enabled." : "AI off. Local controls and dashboards remain available.");
        }
    }
}
