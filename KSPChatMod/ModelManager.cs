using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
namespace KSPChatBridge
{
    // First-use download of Qwen2.5-3B-Instruct Q4_K_M into PluginData/models. Never runs while AI is off.
    internal sealed class ModelManager
    {
        internal const string DefaultFile = "Qwen2.5-3B-Instruct-Q4_K_M.gguf";
        // Bartowski repack; filename matches DefaultFile (see also Qwen/Qwen2.5-3B-Instruct-GGUF on HuggingFace).
        internal const string DefaultDownloadUrl =
            "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf";
        internal const long DefaultMinBytes = 1500000000L;
        internal string Phase { get; private set; } = "idle";
        internal double Progress { get; private set; }
        internal string Error { get; private set; }
        internal string Warning { get; private set; }
        readonly string modelsRoot;
        readonly string expectedSha256;
        readonly long minBytes;
        readonly object sync = new object();
        bool cancel;
        static ModelManager instance;
        static readonly object InstanceLock = new object();

        internal static ModelManager Instance
        {
            get
            {
                lock (InstanceLock)
                {
                    if (instance == null)
                    {
                        string root = NativeLibraryLayout.ModelsRoot(BridgeLauncher.PluginDataDirectory);
                        instance = new ModelManager(root, null, DefaultMinBytes);
                    }
                    return instance;
                }
            }
        }

        internal ModelManager(string modelsRoot, string expectedSha256 = null, long minBytes = 1)
        {
            if (string.IsNullOrEmpty(modelsRoot)) throw new ArgumentException("models root required");
            this.modelsRoot = modelsRoot;
            this.expectedSha256 = string.IsNullOrEmpty(expectedSha256) ? null : expectedSha256.ToLowerInvariant();
            this.minBytes = Math.Max(1, minBytes);
        }

        internal string FinalPath { get { return Path.Combine(modelsRoot, DefaultFile); } }
        internal string PartialPath { get { return FinalPath + ".partial"; } }
        internal bool Ready { get { return File.Exists(FinalPath) && new FileInfo(FinalPath).Length >= minBytes; } }

        internal void Cancel() { lock (sync) { cancel = true; } }

        bool Cancelled()
        {
            lock (sync) { return cancel; }
        }

        // HttpWebRequest download to PartialPath, then FinalizeFromPartial. Never runs when aiEnabled is false.
        internal string Download(string url, bool aiEnabled)
        {
            if (!aiEnabled)
            {
                Phase = "ai off";
                Error = "Model download refused while AI is off.";
                return Error;
            }
            if (string.IsNullOrWhiteSpace(url)) url = DefaultDownloadUrl;
            lock (sync) { cancel = false; }
            Phase = "downloading";
            Progress = 0;
            Error = null;
            Warning = null;
            try
            {
                Directory.CreateDirectory(modelsRoot);
                if (File.Exists(PartialPath))
                {
                    try { File.Delete(PartialPath); }
                    catch (Exception ex) { Error = "Could not reset partial file: " + ex.Message; Phase = "error"; return Error; }
                }
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "KSPChatBridge";
                req.Timeout = 600000;
                req.ReadWriteTimeout = 600000;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var input = resp.GetResponseStream())
                using (var output = File.Create(PartialPath))
                {
                    long total = resp.ContentLength;
                    byte[] buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (Cancelled())
                        {
                            Phase = "cancelled";
                            Error = "Download cancelled.";
                            try { output.Close(); File.Delete(PartialPath); } catch (Exception) { }
                            return Error;
                        }
                        output.Write(buffer, 0, n);
                        read += n;
                        if (total > 0) MarkProgress((double)read / total);
                        else MarkProgress(Math.Min(0.99, (double)read / DefaultMinBytes));
                    }
                }
                MarkProgress(1);
                string finalize = FinalizeFromPartial(true);
                if (finalize != null) return finalize;
                if (expectedSha256 == null)
                {
                    long len = new FileInfo(FinalPath).Length;
                    if (len < DefaultMinBytes)
                    {
                        Warning = "File is smaller than ~1.5 GB; verify the download on HuggingFace.";
                        Phase = "warning";
                        Error = Warning;
                        return Error;
                    }
                    Warning = "No pinned SHA256; size check only (~1.5 GB minimum).";
                }
                return null;
            }
            catch (Exception ex)
            {
                Phase = "error";
                Error = ex.Message;
                try { if (File.Exists(PartialPath)) File.Delete(PartialPath); } catch (Exception) { }
                return Error;
            }
        }

        // Offline-testable finalize: verifies size/checksum then atomic rename.
        internal string FinalizeFromPartial(bool aiEnabled)
        {
            if (!aiEnabled) { Phase = "ai off"; Error = "Model download/load refused while AI is off."; return Error; }
            if (Cancelled()) { Phase = "cancelled"; Error = "Download cancelled."; return Error; }
            if (!File.Exists(PartialPath)) { Phase = "missing"; Error = "Partial download missing."; return Error; }
            var info = new FileInfo(PartialPath);
            if (info.Length < minBytes) { Phase = "corrupt"; Error = "Downloaded file too small."; return Error; }
            if (expectedSha256 != null)
            {
                string hash = Sha256(PartialPath);
                if (hash != expectedSha256) { Phase = "checksum"; Error = "Checksum mismatch."; return Error; }
            }
            Directory.CreateDirectory(modelsRoot);
            string temporary = FinalPath + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
            File.Copy(PartialPath, temporary, true);
            if (File.Exists(FinalPath)) File.Delete(FinalPath);
            File.Move(temporary, FinalPath);
            try { File.Delete(PartialPath); } catch (Exception) { }
            Phase = "ready"; Progress = 1; Error = null;
            return null;
        }

        internal static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        internal void MarkProgress(double fraction)
        {
            Progress = FlightPolicy.Clamp(fraction, 0, 1);
            Phase = Progress >= 1 ? "verifying" : "downloading";
        }
    }
}
