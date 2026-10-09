using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KSPChatBridge
{
    // HARD RULE: KSP loads every .dll under GameData as a plugin. Native llama/ggml binaries must
    // live under PluginData/native/ with a non-.dll extension (e.g. .bin) and be LoadLibrary'd by full path.
    internal static class NativeLibraryLayout
    {
        internal const string NativeFolder = "native";
        internal const string ModelsFolder = "models";
        internal const string BinExtension = ".bin";
        internal static readonly string[] ApprovedPluginDlls = { "KSPChatBridge.dll" };

        internal static string NativeRoot(string pluginData) { return Path.Combine(pluginData, NativeFolder); }
        internal static string ModelsRoot(string pluginData) { return Path.Combine(pluginData, ModelsFolder); }
        internal static string EncodedPath(string pluginData, string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) throw new ArgumentException("Native library name required.");
            string file = Path.GetFileName(logicalName);
            if (file.IndexOf("..", StringComparison.Ordinal) >= 0) throw new ArgumentException("Invalid native library path.");
            if (!file.EndsWith(BinExtension, StringComparison.OrdinalIgnoreCase))
                file = Path.GetFileNameWithoutExtension(file) + BinExtension;
            return Path.Combine(NativeRoot(pluginData), file);
        }
        // Fail packaging/install if any disallowed DLL appears under GameData/KSPChatBridge.
        internal static string[] ForbiddenDlls(IEnumerable<string> relativePaths)
        {
            var bad = new List<string>();
            foreach (string path in relativePaths ?? new string[0])
            {
                if (path == null) continue;
                string normalized = path.Replace('\\', '/');
                if (!normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileName(normalized);
                bool approved = ApprovedPluginDlls.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                    && normalized.IndexOf("/Plugins/", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!approved) bad.Add(normalized);
            }
            return bad.ToArray();
        }
        internal static bool ModelBundled(IEnumerable<string> relativePaths)
        {
            foreach (string path in relativePaths ?? new string[0])
            {
                if (path == null) continue;
                string n = path.Replace('\\', '/').ToLowerInvariant();
                if (n.Contains("/models/") && (n.EndsWith(".gguf") || n.EndsWith(".bin"))) return true;
            }
            return false;
        }
    }
}
