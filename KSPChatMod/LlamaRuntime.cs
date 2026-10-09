using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KSPChatBridge
{
    // P5-5: pinned llama.cpp runtime for the embedded provider. Downloaded on demand (never bundled), verified by
    // SHA-256, and only the needed DLLs are stored as PluginData/native/*.bin (KSP loads every *.dll under GameData).
    // For loading they are copied to a cache OUTSIDE GameData (%LOCALAPPDATA%\KSPChatBridge\native\<tag>) as .dll so
    // Windows can resolve llama.dll -> ggml.dll imports, then LoadLibraryEx'd by full path. No System.IO.Compression
    // (absent from KSP's Mono): MiniZip below reads the archive with System.dll's DeflateStream.
    internal static class LlamaRuntime
    {
        internal const string Tag = "b11538";
        internal const string ZipName = "llama-" + Tag + "-bin-win-vulkan-x64.zip";
        internal const string ZipUrl = "https://github.com/ggml-org/llama.cpp/releases/download/" + Tag + "/" + ZipName;
        internal const string ZipSha256 = "621ec0ed653ec9d40673be866558d2675eb54907ee4255ecfeb2739f2a6dccf5";
        internal const long ZipBytes = 33459184L;
        internal const string License = "llama.cpp: MIT (ggml-org/llama.cpp)";
        internal const string ManifestName = "runtime_manifest.txt";
        static readonly Regex Keep = new Regex(@"^(llama|ggml|ggml-base|ggml-vulkan|ggml-cpu-[a-z0-9]+|libomp)\.dll$", RegexOptions.IgnoreCase);

        /// <summary>Stored .bin name for a zip entry we need, or null (tools, exes, other libs are skipped).</summary>
        internal static string BinName(string zipEntry)
        {
            if (string.IsNullOrEmpty(zipEntry) || zipEntry.EndsWith("/")) return null;
            string file = zipEntry.Replace('\\', '/');
            file = file.Substring(file.LastIndexOf('/') + 1);
            if (file.IndexOf("..", StringComparison.Ordinal) >= 0 || !Keep.IsMatch(file)) return null;
            return Path.GetFileNameWithoutExtension(file) + NativeLibraryLayout.BinExtension;
        }

        internal static string DllName(string binName) { return Path.GetFileNameWithoutExtension(binName) + ".dll"; }

        /// <summary>Load cache outside GameData; refuses a GameData path.</summary>
        internal static string CacheDir(string localAppData)
        {
            if (string.IsNullOrEmpty(localAppData)) throw new ArgumentException("LOCALAPPDATA required");
            string dir = Path.Combine(Path.Combine(Path.Combine(localAppData, "KSPChatBridge"), "native"), Tag);
            if (dir.Replace('\\', '/').IndexOf("/GameData/", StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidOperationException("Native cache must be outside GameData.");
            return dir;
        }

        /// <summary>GPU layers for the offload mode: GPU = all, Hybrid = hybridLayers, CPU = 0.</summary>
        internal static int GpuLayers(AiOffloadMode mode, int hybridLayers)
        {
            switch (mode) { case AiOffloadMode.Gpu: return 999; case AiOffloadMode.Hybrid: return Math.Max(1, hybridLayers); default: return 0; }
        }

        internal static int ContextTokens(int requested) { return (int)FlightPolicy.Clamp(requested, 16384, 24576); }
        internal static int Threads(int processors) { return Math.Max(1, Math.Min(8, processors / 2)); }

        internal static string Sha256(Stream s)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder(64);
                foreach (byte b in sha.ComputeHash(s)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
        internal static string Sha256(string path) { using (var s = File.OpenRead(path)) return Sha256(s); }

        /// <summary>Extract the kept DLLs from a verified runtime zip into nativeDir as *.bin + manifest (name sha size).</summary>
        internal static int Extract(string zipPath, string nativeDir)
        {
            Directory.CreateDirectory(nativeDir);
            var manifest = new StringBuilder("tag " + Tag + "\n");
            int n = 0;
            foreach (var e in MiniZip.Entries(zipPath))
            {
                string bin = BinName(e.Name); if (bin == null) continue;
                string dest = Path.Combine(nativeDir, bin), tmp = dest + ".tmp";
                using (var outFile = File.Create(tmp)) MiniZip.CopyEntry(zipPath, e, outFile);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                manifest.Append(bin).Append(' ').Append(Sha256(dest)).Append(' ').Append(new FileInfo(dest).Length).Append('\n');
                n++;
            }
            if (n == 0) throw new InvalidOperationException("Runtime zip contained no llama/ggml libraries.");
            File.WriteAllText(Path.Combine(nativeDir, ManifestName), manifest.ToString());
            return n;
        }

        /// <summary>Manifest rows (bin -> sha) or null when the runtime is not installed for this tag.</summary>
        internal static Dictionary<string, string> Manifest(string nativeDir)
        {
            string path = Path.Combine(nativeDir, ManifestName);
            if (!File.Exists(path)) return null;
            var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != "tag " + Tag) return null;
            for (int i = 1; i < lines.Length; i++) { var f = lines[i].Split(' '); if (f.Length >= 2) rows[f[0]] = f[1]; }
            return rows.ContainsKey("llama.bin") && rows.ContainsKey("ggml.bin") && rows.ContainsKey("ggml-base.bin") ? rows : null;
        }

        /// <summary>Copy *.bin to the outside-GameData cache as *.dll (verifying each SHA); returns the cache dir.</summary>
        internal static string Materialize(string nativeDir, string cacheDir)
        {
            var rows = Manifest(nativeDir);
            if (rows == null) throw new InvalidOperationException("llama runtime not installed (use Download runtime).");
            Directory.CreateDirectory(cacheDir);
            foreach (var row in rows)
            {
                string src = Path.Combine(nativeDir, row.Key), dst = Path.Combine(cacheDir, DllName(row.Key));
                if (!File.Exists(src) || Sha256(src) != row.Value) throw new InvalidOperationException("Runtime file damaged: " + row.Key + " (download runtime again).");
                if (File.Exists(dst) && Sha256(dst) == row.Value) continue;
                File.Copy(src, dst, true);
            }
            return cacheDir;
        }
    }

    /// <summary>Minimal read-only ZIP reader (stored/deflate, no zip64) using System.dll's DeflateStream.</summary>
    internal static class MiniZip
    {
        internal sealed class Entry { internal string Name; internal int Method; internal long CompressedSize, Size, LocalOffset; }

        internal static List<Entry> Entries(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                long scan = Math.Max(0, fs.Length - 65557);
                fs.Position = scan; byte[] tail = br.ReadBytes((int)(fs.Length - scan));
                int eocd = -1;
                for (int i = tail.Length - 22; i >= 0; i--) if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 5 && tail[i + 3] == 6) { eocd = i; break; }
                if (eocd < 0) throw new InvalidDataException("Not a zip file.");
                int count = BitConverter.ToUInt16(tail, eocd + 10);
                long cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
                if (count == 0xFFFF || cdOffset == 0xFFFFFFFF) throw new InvalidDataException("zip64 not supported.");
                var list = new List<Entry>(count);
                fs.Position = cdOffset;
                for (int i = 0; i < count; i++)
                {
                    if (br.ReadUInt32() != 0x02014b50) throw new InvalidDataException("Bad central directory.");
                    fs.Position += 6; int method = br.ReadUInt16(); fs.Position += 8;
                    long csize = br.ReadUInt32(), size = br.ReadUInt32();
                    int nameLen = br.ReadUInt16(), extraLen = br.ReadUInt16(), commentLen = br.ReadUInt16();
                    fs.Position += 8; long local = br.ReadUInt32();
                    string name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                    fs.Position += extraLen + commentLen;
                    if (csize == 0xFFFFFFFF || size == 0xFFFFFFFF || local == 0xFFFFFFFF) throw new InvalidDataException("zip64 not supported.");
                    list.Add(new Entry { Name = name, Method = method, CompressedSize = csize, Size = size, LocalOffset = local });
                }
                return list;
            }
        }

        internal static void CopyEntry(string path, Entry e, Stream output)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                fs.Position = e.LocalOffset;
                if (br.ReadUInt32() != 0x04034b50) throw new InvalidDataException("Bad local header: " + e.Name);
                fs.Position = e.LocalOffset + 26;
                int nameLen = br.ReadUInt16(), extraLen = br.ReadUInt16();
                fs.Position = e.LocalOffset + 30 + nameLen + extraLen;
                var slice = new SubStream(fs, e.CompressedSize);
                Stream src = e.Method == 0 ? (Stream)slice : e.Method == 8 ? new DeflateStream(slice, CompressionMode.Decompress) : null;
                if (src == null) throw new InvalidDataException("Unsupported zip method " + e.Method + ": " + e.Name);
                byte[] buf = new byte[81920]; long total = 0; int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) { output.Write(buf, 0, n); total += n; }
                if (total != e.Size) throw new InvalidDataException("Size mismatch: " + e.Name);
            }
        }

        sealed class SubStream : Stream
        {
            readonly Stream inner; long left;
            internal SubStream(Stream inner, long length) { this.inner = inner; left = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (left <= 0) return 0;
                int n = inner.Read(buffer, offset, (int)Math.Min(count, left)); left -= n; return n;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }
    }
}
