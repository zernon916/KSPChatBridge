using System;

namespace KSPChatBridge
{
    internal enum AiOffloadMode { Cpu = 0, Hybrid = 1, Gpu = 2 }
    internal sealed class AiRuntimePolicy
    {
        internal AiOffloadMode Offload = AiOffloadMode.Hybrid;
        internal int ContextTokens = 16384;
        internal int GpuLayers = -1; // -1 = runtime decide for Hybrid (~1 GB target is a budget, not a guarantee)
        internal bool AiEnabled = true;
        internal string Apply(AiOffloadMode requestedOffload, int requestedContext, bool gpuAvailable, long freeGpuBytes)
        {
            if (!AiEnabled) return "AI off: runtime not loaded.";
            ContextTokens = (int)FlightPolicy.Clamp(requestedContext, 16384, 24576);
            Offload = requestedOffload;
            if (requestedOffload == AiOffloadMode.Gpu && !gpuAvailable)
            {
                Offload = AiOffloadMode.Cpu;
                GpuLayers = 0;
                return "GPU unavailable; fell back to CPU-only.";
            }
            if (requestedOffload == AiOffloadMode.Hybrid || requestedOffload == AiOffloadMode.Gpu)
            {
                // Budget ~1 GB VRAM for weights/KV; if free memory is lower, reduce layers and report it.
                if (freeGpuBytes > 0 && freeGpuBytes < 1200L * 1024 * 1024)
                {
                    Offload = AiOffloadMode.Cpu;
                    GpuLayers = 0;
                    return "Insufficient GPU memory for Hybrid/GPU; fell back to CPU-only.";
                }
                GpuLayers = requestedOffload == AiOffloadMode.Gpu ? int.MaxValue : 20;
            }
            else GpuLayers = 0;
            return null;
        }
        internal static string ProviderId(string name)
        {
            if (string.IsNullOrEmpty(name)) return "local";
            name = name.Trim().ToLowerInvariant();
            if (name == "groq" || name == "openai" || name == "gemini" || name == "local" || name == "lmstudio" || name == "ollama") return name;
            throw new ArgumentException("Unknown AI provider: " + name);
        }
    }
}
