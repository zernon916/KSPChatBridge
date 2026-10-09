using System;
using System.Runtime.InteropServices;

namespace KSPChatBridge
{
    // Compatibility shim for Phase 4A: LoadLibrary by absolute PluginData/native/*.bin path.
    // Does not ship or download natives; KSP live load/generate remains a release gate.
    internal static class NativeAiLoader
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32", SetLastError = true)]
        static extern bool FreeLibrary(IntPtr module);

        internal const string CandidateWrapper = "LLamaSharp 0.19.0 (Unity-known) or pinned native interop; net472/C# 7.3";
        internal const string TargetModel = ModelManager.DefaultFile;
        internal static bool TryValidatePath(string absoluteBinPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(absoluteBinPath) || !absoluteBinPath.EndsWith(NativeLibraryLayout.BinExtension, StringComparison.OrdinalIgnoreCase))
            { error = "Native library must be an absolute PluginData/native/*.bin path."; return false; }
            if (absoluteBinPath.IndexOf("GameData", StringComparison.OrdinalIgnoreCase) >= 0
                && absoluteBinPath.IndexOf("PluginData", StringComparison.OrdinalIgnoreCase) < 0)
            { error = "Refusing to LoadLibrary a path under GameData outside PluginData."; return false; }
            return true;
        }
        internal static IntPtr TryLoad(string absoluteBinPath, out string error)
        {
            error = null;
            if (!TryValidatePath(absoluteBinPath, out error)) return IntPtr.Zero;
            IntPtr handle = LoadLibrary(absoluteBinPath);
            if (handle == IntPtr.Zero) error = "LoadLibrary failed (native runtime not installed yet): " + Marshal.GetLastWin32Error();
            return handle;
        }
        internal static void Unload(IntPtr handle) { if (handle != IntPtr.Zero) FreeLibrary(handle); }
    }
}
