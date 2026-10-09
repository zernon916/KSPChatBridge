namespace KSPChatBridge
{
    internal static class KSPUtil
    {
        internal static string ApplicationRootPath { get { return System.IO.Path.GetTempPath(); } }
    }
    internal static class BridgeLauncher
    {
        internal static string PluginDataDirectory { get { return System.IO.Path.GetTempPath(); } }
        internal static string DataDirectory
        {
            get
            {
                if (SecretsStore.DataDirectoryOverride != null) return SecretsStore.DataDirectoryOverride();
                return System.IO.Path.GetTempPath();
            }
        }
    }
}
