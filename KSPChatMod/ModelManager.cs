using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KSPChatBridge
{
    // First-use download of Qwen2.5-3B-Instruct Q4_K_M into PluginData/models. Never runs while AI is off.
    internal sealed class ModelManager
    {
        internal const string DefaultFile = "Qwen2.5-3B-Instruct-Q4_K_M.gguf";
        internal const string DefaultSource = "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF";
        internal string Phase { get; private set; } = "idle";
        internal double Progress { get; private set; }
        internal string Error { get; private set; }
        readonly string modelsRoot;
        readonly string expectedSha256;
        readonly long minBytes;
        bool cancel;
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
        internal void Cancel() { cancel = true; }
        // Offline-testable finalize: verifies size/checksum then atomic rename.
        internal string FinalizeFromPartial(bool aiEnabled)
        {
            if (!aiEnabled) { Phase = "ai off"; Error = "Model download/load refused while AI is off."; return Error; }
            if (cancel) { Phase = "cancelled"; Error = "Download cancelled."; return Error; }
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
