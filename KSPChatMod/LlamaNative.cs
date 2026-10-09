using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KSPChatBridge
{
    // P5-5: in-process llama.cpp (pinned LlamaRuntime.Tag) via LoadLibraryEx by full path + GetProcAddress delegates.
    // No managed wrapper (LLamaSharp needs System.Memory & co, absent from KSP's Mono). Windows x64 ABI: structs > 8
    // bytes are returned through a hidden first pointer and passed by pointer to a caller copy, so the large param
    // structs stay opaque buffers; only fields at offsets pinned for this tag are touched, after an ABI sanity check
    // of the defaults (refuses to run on a mismatched runtime instead of corrupting memory).
    internal sealed class LlamaNative : IDisposable
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

        // ---- pinned ABI (llama.h @ b11538) ----
        internal const int ModelParamsBytes = 512, ContextParamsBytes = 1024, BatchBytes = 128;
        internal const int OffNGpuLayers = 16, OffSplitMode = 20;
        internal const int OffNCtx = 0, OffNBatch = 4, OffNUbatch = 8, OffNSeqMax = 12, OffNThreads = 28, OffNThreadsBatch = 32;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StrFn(IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr RetStructFn(IntPtr ret);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PtrPtrFn(IntPtr a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PtrFn(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeFn(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TokenizeFn(IntPtr vocab, IntPtr text, int len, IntPtr tokens, int max, [MarshalAs(UnmanagedType.I1)] bool addSpecial, [MarshalAs(UnmanagedType.I1)] bool parseSpecial);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr BatchOneFn(IntPtr ret, IntPtr tokens, int n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DecodeFn(IntPtr ctx, IntPtr batch);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void MemClearFn(IntPtr mem, [MarshalAs(UnmanagedType.I1)] bool data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ChainInitFn(byte noPerf);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FloatFn(float f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FloatSizeFn(float f, UIntPtr minKeep);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr UIntFn(uint seed);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ChainAddFn(IntPtr chain, IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SampleFn(IntPtr smpl, IntPtr ctx, int idx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool IsEogFn(IntPtr vocab, int token);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PieceFn(IntPtr vocab, int token, IntPtr buf, int len, int lstrip, [MarshalAs(UnmanagedType.I1)] bool special);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal delegate bool AbortFn(IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetAbortFn(IntPtr ctx, AbortFn cb, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void LogFn(int level, IntPtr text, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetLogFn(LogFn cb, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint NCtxFn(IntPtr ctx);

        static bool backendReady; static readonly object BackendLock = new object();
        static IntPtr llama, ggml;
        static LogFn quietLog;   // kept alive for the process lifetime
        static RetStructFn modelDefaults, contextDefaults; static PtrPtrFn loadModel, initContext; static PtrFn getVocab, getMemory;
        static FreeFn modelFree, ctxFree, samplerFree; static TokenizeFn tokenize; static BatchOneFn batchOne; static DecodeFn decode;
        static MemClearFn memClear; static ChainInitFn chainInit; static FloatFn initTemp; static FloatSizeFn initTopP, initMinP;
        static UIntFn initDist; static ChainAddFn chainAdd; static SampleFn sample; static IsEogFn isEog; static PieceFn piece;
        static SetAbortFn setAbort; static NCtxFn nCtx;

        IntPtr model, ctx, vocab, sampler;
        AbortFn abortCb; volatile bool cancel;
        internal int ContextTokens { get; private set; }
        internal string LoadedPath { get; private set; }

        static T Fn<T>(IntPtr module, string name) where T : class
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException("llama runtime missing " + name + " (wrong version? expected " + LlamaRuntime.Tag + ")");
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        /// <summary>Load the runtime from the outside-GameData cache (once per process).</summary>
        internal static void EnsureBackend(string cacheDir)
        {
            lock (BackendLock)
            {
                if (backendReady) return;
                if (IntPtr.Size != 8 || Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Embedded llama needs 64-bit Windows.");
                if (cacheDir.Replace('\\', '/').IndexOf("/GameData/", StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidOperationException("Refusing to load natives from GameData.");
                SetDllDirectoryW(cacheDir);
                try
                {
                    ggml = LoadLibraryExW(Path.Combine(cacheDir, "ggml.dll"), IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                    llama = LoadLibraryExW(Path.Combine(cacheDir, "llama.dll"), IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                }
                finally { SetDllDirectoryW(null); }
                if (ggml == IntPtr.Zero || llama == IntPtr.Zero)
                    throw new DllNotFoundException("LoadLibrary failed (" + Marshal.GetLastWin32Error() + "). The Microsoft Visual C++ 2015-2022 x64 runtime may be missing.");
                modelDefaults = Fn<RetStructFn>(llama, "llama_model_default_params"); contextDefaults = Fn<RetStructFn>(llama, "llama_context_default_params");
                loadModel = Fn<PtrPtrFn>(llama, "llama_model_load_from_file"); initContext = Fn<PtrPtrFn>(llama, "llama_init_from_model");
                getVocab = Fn<PtrFn>(llama, "llama_model_get_vocab"); getMemory = Fn<PtrFn>(llama, "llama_get_memory");
                modelFree = Fn<FreeFn>(llama, "llama_model_free"); ctxFree = Fn<FreeFn>(llama, "llama_free"); samplerFree = Fn<FreeFn>(llama, "llama_sampler_free");
                tokenize = Fn<TokenizeFn>(llama, "llama_tokenize"); batchOne = Fn<BatchOneFn>(llama, "llama_batch_get_one"); decode = Fn<DecodeFn>(llama, "llama_decode");
                memClear = Fn<MemClearFn>(llama, "llama_memory_clear"); chainInit = Fn<ChainInitFn>(llama, "llama_sampler_chain_init");
                initTemp = Fn<FloatFn>(llama, "llama_sampler_init_temp"); initTopP = Fn<FloatSizeFn>(llama, "llama_sampler_init_top_p");
                initMinP = Fn<FloatSizeFn>(llama, "llama_sampler_init_min_p"); initDist = Fn<UIntFn>(llama, "llama_sampler_init_dist");
                chainAdd = Fn<ChainAddFn>(llama, "llama_sampler_chain_add"); sample = Fn<SampleFn>(llama, "llama_sampler_sample");
                isEog = Fn<IsEogFn>(llama, "llama_vocab_is_eog"); piece = Fn<PieceFn>(llama, "llama_token_to_piece");
                setAbort = Fn<SetAbortFn>(llama, "llama_set_abort_callback"); nCtx = Fn<NCtxFn>(llama, "llama_n_ctx");
                quietLog = (level, text, user) => { };
                Fn<SetLogFn>(llama, "llama_log_set")(quietLog, IntPtr.Zero);
                Fn<VoidFn>(llama, "llama_backend_init")();
                IntPtr dir = Utf8(cacheDir);
                try { Fn<StrFn>(ggml, "ggml_backend_load_all_from_path")(dir); } finally { Marshal.FreeHGlobal(dir); }
                backendReady = true;
            }
        }

        static IntPtr Utf8(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? ""); IntPtr p = Marshal.AllocHGlobal(b.Length + 1);
            Marshal.Copy(b, 0, p, b.Length); Marshal.WriteByte(p, b.Length, 0); return p;
        }

        /// <summary>Pure ABI check of default param buffers (testable with fake bytes).</summary>
        internal static string CheckDefaults(int nCtx, int nBatch, int nUbatch, int nSeqMax, int splitMode)
        {
            if (nBatch <= 0 || nUbatch <= 0 || nSeqMax != 1 || nCtx < 0 || nCtx > 1 << 20 || splitMode < 0 || splitMode > 3)
                return "llama runtime ABI mismatch (defaults n_ctx=" + nCtx + " n_batch=" + nBatch + " n_ubatch=" + nUbatch + " n_seq_max=" + nSeqMax + " split=" + splitMode + "); expected " + LlamaRuntime.Tag + ".";
            return null;
        }

        internal LlamaNative(string modelPath, int gpuLayers, int contextTokens, int threads)
        {
            if (!backendReady) throw new InvalidOperationException("llama backend not loaded");
            IntPtr mp = Marshal.AllocHGlobal(ModelParamsBytes), cp = Marshal.AllocHGlobal(ContextParamsBytes), path = Utf8(modelPath);
            try
            {
                for (int i = 0; i < ModelParamsBytes; i += 8) Marshal.WriteInt64(mp, i, 0);
                for (int i = 0; i < ContextParamsBytes; i += 8) Marshal.WriteInt64(cp, i, 0);
                modelDefaults(mp); contextDefaults(cp);
                string abi = CheckDefaults(Marshal.ReadInt32(cp, OffNCtx), Marshal.ReadInt32(cp, OffNBatch), Marshal.ReadInt32(cp, OffNUbatch), Marshal.ReadInt32(cp, OffNSeqMax), Marshal.ReadInt32(mp, OffSplitMode));
                if (abi != null) throw new InvalidOperationException(abi);
                Marshal.WriteInt32(mp, OffNGpuLayers, gpuLayers);
                model = loadModel(path, mp);
                if (model == IntPtr.Zero) throw new InvalidOperationException("llama could not load the model (corrupt file or out of memory).");
                Marshal.WriteInt32(cp, OffNCtx, contextTokens);
                Marshal.WriteInt32(cp, OffNBatch, 2048); Marshal.WriteInt32(cp, OffNUbatch, 512);
                Marshal.WriteInt32(cp, OffNThreads, threads); Marshal.WriteInt32(cp, OffNThreadsBatch, threads);
                ctx = initContext(model, cp);
                if (ctx == IntPtr.Zero) { modelFree(model); model = IntPtr.Zero; throw new InvalidOperationException("llama could not create a " + contextTokens + "-token context (out of memory? try 16k or CPU)."); }
                vocab = getVocab(model);
                abortCb = d => cancel;
                setAbort(ctx, abortCb, IntPtr.Zero);
                ContextTokens = (int)nCtx(ctx); LoadedPath = modelPath;
            }
            finally { Marshal.FreeHGlobal(mp); Marshal.FreeHGlobal(cp); Marshal.FreeHGlobal(path); }
        }

        internal void Cancel() { cancel = true; }

        int[] Tokenize(string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text); IntPtr t = Utf8(text);
            try
            {
                int max = b.Length + 16; IntPtr buf = Marshal.AllocHGlobal(max * 4);
                try
                {
                    int n = tokenize(vocab, t, b.Length, buf, max, true, true);
                    if (n < 0) throw new InvalidOperationException("tokenize overflow");
                    var r = new int[n]; Marshal.Copy(buf, r, 0, n); return r;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Marshal.FreeHGlobal(t); }
        }

        void Decode(int[] tokens, int start, int count)
        {
            IntPtr tok = Marshal.AllocHGlobal(count * 4), batch = Marshal.AllocHGlobal(BatchBytes);
            try
            {
                Marshal.Copy(tokens, start, tok, count);
                batchOne(batch, tok, count);
                int rc = decode(ctx, batch);
                if (cancel) throw new OperationCanceledException("Generation cancelled.");
                if (rc != 0) throw new InvalidOperationException("llama_decode failed (" + rc + ")" + (rc == 1 ? ": context full" : ""));
            }
            finally { Marshal.FreeHGlobal(tok); Marshal.FreeHGlobal(batch); }
        }

        /// <summary>Stateless completion: clears the KV memory, evaluates the prompt in 2048-token chunks, samples.</summary>
        internal string Generate(string prompt, int maxTokens, float temperature, Func<bool> cancelled)
        {
            cancel = false;
            memClear(getMemory(ctx), true);
            int[] tokens = Tokenize(prompt);
            if (tokens.Length + maxTokens > ContextTokens) throw new InvalidOperationException("Prompt too long for the " + ContextTokens + "-token context (" + tokens.Length + " tokens); clear the chat or raise context.");
            for (int i = 0; i < tokens.Length; i += 2048) { if (cancelled != null && cancelled()) cancel = true; Decode(tokens, i, Math.Min(2048, tokens.Length - i)); }
            if (sampler == IntPtr.Zero)
            {
                sampler = chainInit(1);
                chainAdd(sampler, initMinP(0.05f, (UIntPtr)1)); chainAdd(sampler, initTopP(0.9f, (UIntPtr)1));
                chainAdd(sampler, initTemp(temperature)); chainAdd(sampler, initDist(unchecked((uint)Environment.TickCount)));
            }
            var output = new List<byte>(); IntPtr pbuf = Marshal.AllocHGlobal(256);
            try
            {
                var one = new int[1];
                for (int n = 0; n < maxTokens; n++)
                {
                    if (cancel || (cancelled != null && cancelled())) { cancel = true; break; }
                    int t = sample(sampler, ctx, -1);
                    if (isEog(vocab, t)) break;
                    int len = piece(vocab, t, pbuf, 256, 0, true);
                    if (len > 0) { var bytes = new byte[len]; Marshal.Copy(pbuf, bytes, 0, len); output.AddRange(bytes); }
                    one[0] = t; Decode(one, 0, 1);
                }
            }
            finally { Marshal.FreeHGlobal(pbuf); }
            if (cancel) throw new OperationCanceledException("Generation cancelled.");
            return Encoding.UTF8.GetString(output.ToArray());
        }

        public void Dispose()
        {
            cancel = true;
            if (sampler != IntPtr.Zero) { samplerFree(sampler); sampler = IntPtr.Zero; }
            if (ctx != IntPtr.Zero) { ctxFree(ctx); ctx = IntPtr.Zero; }
            if (model != IntPtr.Zero) { modelFree(model); model = IntPtr.Zero; }
        }
    }
}
