using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace KSPChatBridge
{
    // Persists in-mod AI runtime choices under PluginData/native_ai.json (merged into native_settings when loaded).
    internal static class AiSettings
    {
        const string FileName = "native_ai.json";
        internal static AiRuntimePolicy Policy = new AiRuntimePolicy();

        internal static string FilePath { get { return Path.Combine(BridgeLauncher.PluginDataDirectory, FileName); } }

        internal static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    Save();
                    return;
                }
                var data = Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath));
                if (data == null) return;
                Policy.Offload = ParseOffload(Str(data, "offload", "Hybrid"));
                Policy.ContextTokens = (int)FlightPolicy.Clamp(Num(data, "context_tokens", 16384), 16384, 24576);
                Policy.AiEnabled = Bool(data, "ai_enabled", true);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[KSPChatBridge] native_ai.json: " + ex.Message);
            }
        }

        internal static void Save()
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    { "offload", Policy.Offload.ToString() },
                    { "context_tokens", Policy.ContextTokens },
                    { "ai_enabled", Policy.AiEnabled },
                    { "updated_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                };
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json().Serialize(data));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak");
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[KSPChatBridge] native_ai.json save: " + ex.Message);
            }
        }

        internal static void MergeInto(Dictionary<string, object> nativeSettings)
        {
            if (nativeSettings == null) return;
            nativeSettings["ai_offload"] = Policy.Offload.ToString();
            nativeSettings["ai_context_tokens"] = Policy.ContextTokens;
            nativeSettings["ai_native_chat"] = BridgeLauncher.NativeChatEnabled;
        }

        internal static void ApplyOffload(AiOffloadMode mode, int contextTokens)
        {
            Policy.ContextTokens = (int)FlightPolicy.Clamp(contextTokens, 16384, 24576);
            string note = Policy.Apply(mode, Policy.ContextTokens, gpuAvailable: true, freeGpuBytes: 0);
            Save();
            if (!string.IsNullOrEmpty(note)) ChatWindow.Notice("[in-mod AI] " + note);
        }

        static AiOffloadMode ParseOffload(string s)
        {
            if (string.IsNullOrEmpty(s)) return AiOffloadMode.Hybrid;
            s = s.Trim();
            if (s.Equals("Cpu", StringComparison.OrdinalIgnoreCase) || s == "0") return AiOffloadMode.Cpu;
            if (s.Equals("Gpu", StringComparison.OrdinalIgnoreCase) || s == "2") return AiOffloadMode.Gpu;
            return AiOffloadMode.Hybrid;
        }

        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
        static string Str(Dictionary<string, object> a, string key, string fallback)
        { return a.ContainsKey(key) ? Convert.ToString(a[key], CultureInfo.InvariantCulture) : fallback; }
        static double Num(Dictionary<string, object> a, string key, double fallback)
        {
            if (!a.ContainsKey(key)) return fallback;
            return Convert.ToDouble(a[key], CultureInfo.InvariantCulture);
        }
        static bool Bool(Dictionary<string, object> a, string key, bool fallback)
        { return a.ContainsKey(key) ? Convert.ToBoolean(a[key], CultureInfo.InvariantCulture) : fallback; }
    }
}
