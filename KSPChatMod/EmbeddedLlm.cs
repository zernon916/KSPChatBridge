using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace KSPChatBridge
{
    // P5-5: "Download runtime" (pinned llama.cpp zip -> PluginData/native/*.bin) and the embedded provider
    // (load / generate / cancel / unload). No Unity types: the C# suite exercises the extract path offline.
    internal sealed class LlamaRuntimeManager
    {
        internal string Phase { get; private set; } = "idle";
        internal double Progress { get; private set; }
        internal string Error { get; private set; }
        readonly string nativeDir; readonly object sync = new object(); bool cancel;
        internal LlamaRuntimeManager(string nativeDir) { this.nativeDir = nativeDir; }
        internal string NativeDir { get { return nativeDir; } }
        internal bool Ready { get { return LlamaRuntime.Manifest(nativeDir) != null; } }
        internal void Cancel() { lock (sync) cancel = true; }
        bool Cancelled() { lock (sync) return cancel; }

        /// <summary>Verify + extract an already-downloaded zip (also the offline-test entry point).</summary>
        internal string Install(string zipPath, string expectedSha)
        {
            Phase = "verifying";
            if (expectedSha != null && LlamaRuntime.Sha256(zipPath) != expectedSha) { Phase = "checksum"; Error = "Runtime checksum mismatch."; return Error; }
            Phase = "extracting";
            try { LlamaRuntime.Extract(zipPath, nativeDir); }
            catch (Exception ex) { Phase = "error"; Error = "Extract failed: " + ex.Message; return Error; }
            Phase = "ready"; Progress = 1; Error = null; return null;
        }

        internal string Download(bool aiEnabled)
        {
            if (!aiEnabled) { Phase = "ai off"; Error = "Runtime download refused while AI is off."; return Error; }
            lock (sync) cancel = false;
            Phase = "downloading"; Progress = 0; Error = null;
            string partial = Path.Combine(nativeDir, LlamaRuntime.ZipName + ".partial");
            try
            {
                Directory.CreateDirectory(nativeDir);
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }   // TLS 1.2
                var req = (HttpWebRequest)WebRequest.Create(LlamaRuntime.ZipUrl);
                req.UserAgent = "KSPChatBridge"; req.Timeout = 600000; req.ReadWriteTimeout = 600000; req.Proxy = null; req.AllowAutoRedirect = true;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var input = resp.GetResponseStream())
                using (var output = File.Create(partial))
                {
                    long total = resp.ContentLength > 0 ? resp.ContentLength : LlamaRuntime.ZipBytes, read = 0; byte[] buf = new byte[81920]; int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (Cancelled()) { Phase = "cancelled"; Error = "Download cancelled."; break; }
                        output.Write(buf, 0, n); read += n; Progress = Math.Min(.99, (double)read / total);
                    }
                }
                if (Phase == "cancelled") { File.Delete(partial); return Error; }
                return Install(partial, LlamaRuntime.ZipSha256);
            }
            catch (Exception ex) { Phase = "error"; Error = ex.Message; return Error; }
            finally { try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception) { } }
        }
    }

    internal static class EmbeddedLlm
    {
        static readonly object Gate = new object();
        static LlamaNative llm; static string loadedKey;
        internal static Func<string> ModelPath = () => null;
        internal static Func<string> NativeDir = () => null;
        internal static Func<int> GpuLayers = () => 0;
        internal static Func<int> ContextTokens = () => 16384;
        internal const int MaxReplyTokens = 512;

        /// <summary>Null when embedded chat can run, else what to download.</summary>
        internal static string Readiness()
        {
            string nd = NativeDir(), mp = ModelPath();
            bool rt = nd != null && LlamaRuntime.Manifest(nd) != null, model = mp != null && File.Exists(mp);
            if (!rt && !model) return "Embedded Qwen: press Download runtime and Download model (AICS > Settings).";
            if (!rt) return "Embedded Qwen: press Download runtime (AICS > Settings).";
            if (!model) return "Embedded Qwen: press Download model (AICS > Settings).";
            return null;
        }

        static LlamaNative Ensure()
        {
            string why = Readiness(); if (why != null) throw new InvalidOperationException(why);
            int gpu = GpuLayers(), ctx = LlamaRuntime.ContextTokens(ContextTokens());
            string key = ModelPath() + "|" + gpu + "|" + ctx;
            if (llm != null && loadedKey == key) return llm;
            Unload();
            string cache = LlamaRuntime.Materialize(NativeDir(), LlamaRuntime.CacheDir(Environment.GetEnvironmentVariable("LOCALAPPDATA")));
            LlamaNative.EnsureBackend(cache);
            llm = new LlamaNative(ModelPath(), gpu, ctx, LlamaRuntime.Threads(Environment.ProcessorCount));
            loadedKey = key;
            return llm;
        }

        /// <summary>InModChatSession completion hook: OpenAI payload in, OpenAI response out.</summary>
        internal static string Complete(OpenAiBackend.Endpoint ep, string payload)
        {
            var body = MiniJson.Deserialize(payload);
            object m, t, temp; body.TryGetValue("messages", out m); body.TryGetValue("tools", out t); body.TryGetValue("temperature", out temp);
            string prompt = EmbeddedPrompt.Render(m as IList ?? new ArrayList(), t as IList);
            lock (Gate)
            {
                var model = Ensure();
                string text = model.Generate(prompt, MaxReplyTokens, temp == null ? 0.3f : Convert.ToSingle(temp, System.Globalization.CultureInfo.InvariantCulture), null);
                return EmbeddedPrompt.ToOpenAi(text);
            }
        }

        internal static void Cancel() { var l = llm; if (l != null) l.Cancel(); }

        /// <summary>Free the model + context (AI off, settings change). Cancels a running generation first.</summary>
        internal static void Unload()
        {
            Cancel();
            lock (Gate) { if (llm != null) { llm.Dispose(); llm = null; loadedKey = null; } }
        }
        internal static bool Loaded { get { return llm != null; } }
    }
}
