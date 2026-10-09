using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace KSPChatBridge
{
    internal static class OpenAiBackend
    {
        internal const string ClineUrl = "https://api.cline.bot/api/v1";
        internal const string ClineDefaultModel = "minimax/minimax-m2.5";   // Cline's free model; set CLINE_MODEL for e.g. anthropic/claude-sonnet-4-6
        internal const int DefaultTimeoutMs = 60000;
        internal const int CloudRetryMaxWaitSec = 30;
        internal delegate string ChatPostOverride(Endpoint ep, string bodyJson, int timeoutMs);
        internal static ChatPostOverride PostOverride;
        internal const string EmbeddedUrl = "embedded://llama";

        internal struct Endpoint
        {
            internal string Url, Key, Model, Error;
            internal bool Ok { get { return string.IsNullOrEmpty(Error); } }
        }
        internal static Endpoint Resolve(string provider)
        {
            var ep = new Endpoint();
            provider = (provider ?? "local").Trim().ToLowerInvariant();
            if (provider == "lmstudio" || provider == "lm") provider = "local";
            if (provider == "openai" || provider == "gpt") provider = "chatgpt";
            if (provider == "google") provider = "gemini";
            if (provider == "hf") provider = "huggingface";
            if (provider == "claude")
            {
                // Anthropic's OpenAI-SDK compatibility layer: POST /chat/completions with Bearer key.
                ep.Url = TrimUrl(SecretsStore.Get("CLAUDE_BASE_URL")); if (ep.Url.Length == 0) ep.Url = "https://api.anthropic.com/v1";
                ep.Key = SecretsStore.Get("ANTHROPIC_API_KEY");
                ep.Model = First(SecretsStore.Get("CLAUDE_MODEL"), "claude-sonnet-5-5");
                if (ep.Key.Length == 0) ep.Error = "Claude needs ANTHROPIC_API_KEY in AICS > Settings.";
                return ep;
            }
            if (provider == "grokbot")
            {
                // xAI Grok: OpenAI-compatible /v1/chat/completions with Bearer key.
                ep.Url = TrimUrl(SecretsStore.Get("XAI_BASE_URL")); if (ep.Url.Length == 0) ep.Url = "https://api.x.ai/v1";
                ep.Key = First(SecretsStore.Get("XAI_API_KEY"), SecretsStore.Get("GROK_API_KEY"));
                ep.Model = First(SecretsStore.Get("XAI_MODEL"), "grok-4.7");
                if (ep.Key.Length == 0) ep.Error = "Grok Bot needs XAI_API_KEY in AICS > Settings.";
                return ep;
            }
            if (provider == "local")
            {
                ep.Url = TrimUrl(SecretsStore.Get("LMSTUDIO_URL")); if (ep.Url.Length == 0) ep.Url = "http://localhost:1234/v1";
                ep.Model = First(SecretsStore.Get("LMSTUDIO_MODEL"), SecretsStore.Get("LMSTUDIO_FALLBACK_MODEL"), "local-model");
                return ep;
            }
            if (provider == "ollama")
            {
                ep.Url = TrimUrl(SecretsStore.Get("OLLAMA_URL")); if (ep.Url.Length == 0) ep.Url = "http://localhost:11434/v1";
                ep.Model = First(SecretsStore.Get("OLLAMA_MODEL"), "llama3.2");
                return ep;
            }
            if (provider == "chatgpt")
            {
                ep.Url = TrimUrl(SecretsStore.Get("OPENAI_BASE_URL")); if (ep.Url.Length == 0) ep.Url = "https://api.openai.com/v1";
                ep.Key = SecretsStore.Get("OPENAI_API_KEY");
                ep.Model = First(SecretsStore.Get("OPENAI_MODEL"), "gpt-4.1-mini");
                if (ep.Key.Length == 0) ep.Error = "ChatGPT needs OPENAI_API_KEY in AICS > Settings.";
                return ep;
            }
            if (provider == "gemini")
            {
                ep.Url = TrimUrl(SecretsStore.Get("GEMINI_BASE_URL"));
                if (ep.Url.Length == 0) ep.Url = "https://generativelanguage.googleapis.com/v1beta/openai";
                ep.Key = First(SecretsStore.Get("GEMINI_API_KEY"), SecretsStore.Get("GOOGLE_API_KEY"));
                ep.Model = First(SecretsStore.Get("GEMINI_MODEL"), "gemini-flash-latest");
                if (ep.Key.Length == 0) ep.Error = "Gemini needs GEMINI_API_KEY in AICS > Settings.";
                return ep;
            }
            if (provider == "cline")   // Cline API: OpenAI-compatible, provider/model ids (OpenRouter style); streams unless stream=false
            {
                ep.Url = TrimUrl(SecretsStore.Get("CLINE_BASE_URL")); if (ep.Url.Length == 0) ep.Url = ClineUrl;
                ep.Key = SecretsStore.Get("CLINE_API_KEY");
                ep.Model = First(SecretsStore.Get("CLINE_MODEL"), ClineDefaultModel);
                if (ep.Key.Length == 0) ep.Error = "Cline needs CLINE_API_KEY in AICS > Settings (app.cline.bot).";
                return ep;
            }
            if (provider == "groq")
            {
                ep.Url = TrimUrl(SecretsStore.Get("GROQ_BASE_URL")); if (ep.Url.Length == 0) ep.Url = "https://api.groq.com/openai/v1";
                ep.Key = SecretsStore.Get("GROQ_API_KEY");
                ep.Model = First(SecretsStore.Get("GROQ_MODEL"), "openai/gpt-oss-120b");
                if (ep.Key.Length == 0) ep.Error = "Groq needs GROQ_API_KEY in AICS > Settings.";
                return ep;
            }
            if (provider == "openrouter")
            {
                ep.Url = TrimUrl(SecretsStore.Get("OPENROUTER_BASE_URL")); if (ep.Url.Length == 0) ep.Url = "https://openrouter.ai/api/v1";
                ep.Key = SecretsStore.Get("OPENROUTER_API_KEY");
                ep.Model = First(SecretsStore.Get("OPENROUTER_MODEL"), "openrouter/free");
                if (ep.Key.Length == 0) ep.Error = "OpenRouter needs OPENROUTER_API_KEY in AICS > Settings.";
                return ep;
            }
            if (provider == "huggingface")
            {
                ep.Url = TrimUrl(SecretsStore.Get("HF_BASE_URL")); if (ep.Url.Length == 0) ep.Url = "https://router.huggingface.co/v1";
                ep.Key = First(SecretsStore.Get("HF_TOKEN"), SecretsStore.Get("HUGGINGFACE_API_KEY"));
                ep.Model = First(SecretsStore.Get("HF_MODEL"), "openai/gpt-oss-120b:fastest");
                if (ep.Key.Length == 0) ep.Error = "Hugging Face needs HF_TOKEN in AICS > Settings.";
                return ep;
            }
            if (provider == "custom")
            {
                ep.Url = TrimUrl(SecretsStore.Get("CUSTOM_AI_URL"));
                ep.Key = SecretsStore.Get("CUSTOM_AI_KEY");
                ep.Model = SecretsStore.Get("CUSTOM_AI_MODEL");
                if (ep.Url.Length == 0 || ep.Model.Length == 0) ep.Error = "Custom needs CUSTOM_AI_URL and CUSTOM_AI_MODEL.";
                return ep;
            }
            if (provider == "embedded")
            {
                // P5-5: in-process llama.cpp; ready once runtime + model are downloaded.
                ep.Url = EmbeddedUrl; ep.Model = ModelManager.DefaultFile;
                ep.Error = EmbeddedLlm.Readiness();
                return ep;
            }
            ep.Error = "Unknown AI backend: " + provider;
            return ep;
        }
        static string TrimUrl(string u) { return (u ?? "").Trim().TrimEnd('/'); }
        static string First(params string[] values)
        {
            foreach (string v in values) if (!string.IsNullOrEmpty(v)) return v.Trim();
            return "";
        }
        internal static string ChatCompletions(Endpoint ep, string bodyJson, int timeoutMs = 120000)
        {
            if (!ep.Ok) throw new InvalidOperationException(ep.Error);
            if (PostOverride != null) return PostOverride(ep, bodyJson, timeoutMs);
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(ep.Url + "/chat/completions");
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Accept = "application/json";
                    req.Timeout = timeoutMs;
                    req.ReadWriteTimeout = timeoutMs;
                    req.Proxy = null;
                    req.UserAgent = "KSPChatBridge/1.0";
                    if (!string.IsNullOrEmpty(ep.Key)) req.Headers["Authorization"] = "Bearer " + ep.Key;
                    byte[] bytes = Encoding.UTF8.GetBytes(bodyJson);
                    req.ContentLength = bytes.Length;
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return rd.ReadToEnd();
                }
                catch (WebException ex)
                {
                    last = ex;
                    var http = ex.Response as HttpWebResponse;
                    if (http != null && (int)http.StatusCode == 429 && attempt < 2)
                    {
                        int wait = ParseRetryAfter(http.Headers["Retry-After"]);
                        if (wait > CloudRetryMaxWaitSec)
                            throw new InvalidOperationException("Rate limit (429). Wait or switch AI in Settings.");
                        Thread.Sleep(Math.Max(1000, wait * 1000));
                        continue;
                    }
                    string detail = "";
                    try
                    {
                        if (ex.Response != null)
                            using (var rd = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                                detail = rd.ReadToEnd();
                    }
                    catch (Exception) { }
                    throw new InvalidOperationException((http != null ? ((int)http.StatusCode) + " " : "") + ex.Message
                        + (detail.Length > 0 ? " — " + (detail.Length > 240 ? detail.Substring(0, 240) : detail) : ""));
                }
            }
            throw new InvalidOperationException(last != null ? last.Message : "chat failed");
        }

        static int ParseRetryAfter(string header)
        {
            int sec;
            if (int.TryParse((header ?? "").Trim(), out sec)) return sec;
            return 10;
        }
    }
}
