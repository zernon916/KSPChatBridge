using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    // bridge.cfg defaults for NEW installs (an existing bridge.cfg always wins). Pure C# so the offline
    // suite can pin them: native_chat ships ON so chat/tools never require the :8765 bridge once a provider
    // resolves (InModAiHost -> OpenAiBackend). Bridge fallback stays available by turning native_chat off.
    internal static class BridgeConfigDefaults
    {
        internal static Dictionary<string, string> Create()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "autostart", "false" }, { "stop_on_quit", "true" }, { "ai_enabled", "true" },
                { "native_chat", "true" }, { "bridge_dir", "" }, { "python", "python" },
            };
        }
    }
}