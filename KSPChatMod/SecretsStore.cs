using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KSPChatBridge
{
    /// <summary>PluginData/.env — same keys as kspchat/config.py (AicsCore.DataDirectory or GameData fallback).</summary>
    internal static class SecretsStore
    {
        static readonly object Gate = new object();
        internal static Func<string> DataDirectoryOverride;

        static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "LMSTUDIO_URL", "http://localhost:1234/v1" },
            { "OLLAMA_URL", "http://localhost:11434/v1" },
            { "OPENAI_BASE_URL", "https://api.openai.com/v1" },
            { "OPENAI_MODEL", "gpt-4.1-mini" },
            { "GEMINI_BASE_URL", "https://generativelanguage.googleapis.com/v1beta/openai" },
            { "GEMINI_MODEL", "gemini-flash-latest" },
            { "GROQ_BASE_URL", "https://api.groq.com/openai/v1" },
            { "GROQ_MODEL", "openai/gpt-oss-120b" },
            { "OPENROUTER_BASE_URL", "https://openrouter.ai/api/v1" },
            { "OPENROUTER_MODEL", "openrouter/free" },
            { "HF_BASE_URL", "https://router.huggingface.co/v1" },
            { "HF_MODEL", "openai/gpt-oss-120b:fastest" },
            { "LMSTUDIO_FALLBACK_MODEL", "qwen/qwen3.5-9b" },
        };

        internal static string Path
        {
            get
            {
                string data = DataDirectoryOverride != null ? DataDirectoryOverride() : AicsCore.PluginDataDirectory;
                return System.IO.Path.Combine(data, ".env");
            }
        }

        static readonly Dictionary<string, string> BackendKeyVar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chatgpt", "OPENAI_API_KEY" },
            { "gemini", "GEMINI_API_KEY" },
            { "groq", "GROQ_API_KEY" },
            { "cline", "CLINE_API_KEY" },
            { "openrouter", "OPENROUTER_API_KEY" },
            { "huggingface", "HF_TOKEN" },
            { "custom", "CUSTOM_AI_KEY" },
            { "claude", "ANTHROPIC_API_KEY" },
            { "grokbot", "XAI_API_KEY" },
        };

        internal static string StatusText()
        {
            var lines = new List<string>();
            foreach (var kv in BackendKeyVar)
            {
                string v = Get(kv.Value);
                lines.Add(kv.Key + "\t" + (string.IsNullOrEmpty(v) ? "0" : "1") + "\t" + MaskValue(v));
            }
            lines.Add("custom_url\t" + Get("CUSTOM_AI_URL"));
            lines.Add("custom_model\t" + Get("CUSTOM_AI_MODEL"));
            return string.Join("\n", lines.ToArray()) + "\n";
        }

        internal static string SaveBackend(string backend, string key, string customUrl, string customModel, bool clear)
        {
            backend = (backend ?? "").Trim().ToLowerInvariant();
            string varName;
            if (!BackendKeyVar.TryGetValue(backend, out varName)) return "Unknown backend.";
            if (clear)
            {
                Set(varName, "");
                if (backend == "custom") { Set("CUSTOM_AI_URL", ""); Set("CUSTOM_AI_MODEL", ""); }
                return "Key cleared in PluginData/.env.";
            }
            if (!string.IsNullOrWhiteSpace(key)) Set(varName, key.Trim());
            if (backend == "custom")
            {
                if (!string.IsNullOrWhiteSpace(customUrl)) Set("CUSTOM_AI_URL", customUrl.Trim().TrimEnd('/'));
                if (!string.IsNullOrWhiteSpace(customModel)) Set("CUSTOM_AI_MODEL", customModel.Trim());
            }
            return "Saved to PluginData/.env.";
        }

        static string MaskValue(string value)
        {
            value = value ?? "";
            return value.Length >= 8 ? "••••" + value.Substring(value.Length - 4) : (value.Length > 0 ? "••••" : "");
        }

        internal static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(env)) return env.Trim();
            foreach (string alt in AltKeys(key))
            {
                env = Environment.GetEnvironmentVariable(alt);
                if (!string.IsNullOrEmpty(env)) return env.Trim();
            }
            lock (Gate)
            {
                string v;
                if (ReadAll().TryGetValue(key, out v) && !string.IsNullOrEmpty(v)) return v;
                foreach (string alt in AltKeys(key))
                    if (ReadAll().TryGetValue(alt, out v) && !string.IsNullOrEmpty(v)) return v;
            }
            string d;
            return Defaults.TryGetValue(key, out d) ? d : "";
        }

        internal static void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                var map = ReadAll();
                if (string.IsNullOrEmpty(value)) map.Remove(key);
                else map[key] = value.Trim();
                WriteAll(map);
            }
        }

        internal static string Mask(string key)
        {
            string v = Get(key);
            if (v.Length == 0) return "(not set)";
            if (v.Length <= 8) return "****";
            return v.Substring(0, 4) + "…" + v.Substring(v.Length - 4);
        }

        static IEnumerable<string> AltKeys(string key)
        {
            if (key == "GEMINI_API_KEY") yield return "GOOGLE_API_KEY";
            if (key == "HF_TOKEN") yield return "HUGGINGFACE_API_KEY";
            if (key == "XAI_API_KEY") yield return "GROK_API_KEY";
        }

        static Dictionary<string, string> ReadAll()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path;
            if (!File.Exists(path)) return map;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.IndexOf('=') < 0) continue;
                if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase)) line = line.Substring(7).Trim();
                int eq = line.IndexOf('=');
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim().Trim('"').Trim('\'');
                if (k.Length > 0) map[k] = v;
            }
            return map;
        }

        static void WriteAll(Dictionary<string, string> map)
        {
            string path = Path, dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder("# KSPChatBridge secrets (git-ignore this file)\n");
            foreach (var pair in map) sb.Append(pair.Key).Append('=').Append(Escape(pair.Value)).Append('\n');
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static string Escape(string s)
        {
            if (s.IndexOfAny(new[] { ' ', '#', '=', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
