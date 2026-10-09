using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class InModAiHost : MonoBehaviour
    {
        static InModAiHost instance;
        readonly object gate = new object();
        readonly Queue<Action> main = new Queue<Action>();
        readonly Dictionary<string, InModChatSession> sessions = new Dictionary<string, InModChatSession>();
        internal static ModelManager Models;
        readonly ChatOrchestrator queue = new ChatOrchestrator();
        int busy;
        void Awake()
        {
            instance = this;
            DontDestroyOnLoad(this);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneRequested);
            try { string m = PluginDataMigration.Run(BridgeLauncher.PluginDataDirectory, DateTime.UtcNow); if (m != null) Debug.Log("[KSPChatBridge] " + m); }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] PluginData migration skipped (data untouched): " + ex.Message); }
            Models = ModelManager.Instance;
            Runtime = new LlamaRuntimeManager(NativeLibraryLayout.NativeRoot(BridgeLauncher.PluginDataDirectory));
            EmbeddedLlm.ModelPath = () => Models.Ready ? Models.FinalPath : null;
            EmbeddedLlm.NativeDir = () => Runtime.NativeDir;
            EmbeddedLlm.GpuLayers = () => LlamaRuntime.GpuLayers(AiSettings.Policy.Offload, 18);   // Qwen2.5-3B has 36 layers: Hybrid = half
            EmbeddedLlm.ContextTokens = () => AiSettings.Policy.ContextTokens;
        }
        internal static LlamaRuntimeManager Runtime;
        internal static void StartRuntimeDownload()
        {
            if (instance == null || Runtime == null) return;
            if (!BridgeLauncher.AiEnabled) { ChatWindow.Notice("Turn AI on before downloading the runtime."); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = Runtime.Download(BridgeLauncher.AiEnabled);
                lock (instance.main) instance.main.Enqueue(() => ChatWindow.Notice(err == null ? "llama.cpp runtime " + LlamaRuntime.Tag + " ready." : "Runtime download: " + err));
            });
        }
        void Update()
        {
            lock (main) while (main.Count > 0) main.Dequeue()();
        }
        internal static bool UseInModChat()
        {
            if (!BridgeLauncher.AiEnabled) return false;
            if (BridgeLauncher.NativeChat) return true;
            return !BridgeHealthy();
        }

        internal static bool PreferInModChat { get { return UseInModChat(); } }
        static bool BridgeHealthy() { return BridgeLauncher.BridgeHealthy(800); }
        /// <summary>User chat: highest priority in the orchestrator queue.</summary>
        internal static void EnqueueChat(string text, string provider, string session)
        {
            if (instance == null) { ChatWindow.ReleasePendingChat(); ChatWindow.Notice("In-mod AI host not ready."); return; }
            KeyValuePair<string, string> voice;
            try { voice = NativeFlightController.Voice(); } catch (Exception) { voice = new KeyValuePair<string, string>("AICS", ""); }
            instance.Submit(text, provider, session ?? "ingame", true, voice.Key, voice.Value);
        }
        /// <summary>Crew chatter: queued behind user chat (user-before-crew).</summary>
        internal static void EnqueueCrew(string text, string provider, string session)
        {
            if (instance == null) return;   // crew chatter is best-effort, no UI pending to release
            instance.Submit(text, provider, session ?? "ingame", false);
        }
        /// <summary>Drop all queued chat (window Clear button); the running request finishes on its own.</summary>
        internal static int CancelQueued()
        {
            EmbeddedLlm.Cancel();
            return instance == null ? 0 : instance.queue.CancelAll();
        }
        void Submit(string text, string provider, string session, bool userPriority, string speaker = null, string persona = null)
        {
            var request = new ChatRequest
            {
                Speaker = speaker, Persona = persona ?? "",
                Text = text, Provider = provider, Session = session, UserPriority = userPriority,
                DeadlineUtc = DateTime.UtcNow.AddSeconds(userPriority ? 60 : 20),
                Settled = () => { if (userPriority) ChatWindow.ReleasePendingChat(); },
            };
            queue.Enqueue(request);
            Pump();
        }
        void Pump()
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;   // one model call at a time
            var request = queue.DequeueNext(DateTime.UtcNow);
            if (request == null) { Interlocked.Exchange(ref busy, 0); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string reply;
                try
                {
                    InModChatSession chat;
                    lock (gate)
                    {
                        if (!sessions.TryGetValue(request.Session ?? "ingame", out chat))
                            sessions[request.Session ?? "ingame"] = chat = new InModChatSession();
                    }
                    chat.Persona = request.Persona ?? "";
                    reply = chat.Process(request.Text, request.Provider, ExecuteTool);
                }
                catch (Exception ex) { reply = "In-mod AI failed: " + ex.Message; }
                finally { Interlocked.Exchange(ref busy, 0); }
                string line = request.UserPriority ? CrewVoice.Line(request.Speaker, reply) : reply;
                lock (main) main.Enqueue(() =>
                {
                    if (request.Settled != null) request.Settled();
                    ChatWindow.Notice(line);
                    Pump();   // next queued request (user first)
                });
            });
        }
        string ExecuteTool(string name, string argsJson)
        {
            string result = null;
            var done = new ManualResetEvent(false);
            lock (main) main.Enqueue(() =>
            {
                try
                {
                    if (NativeCommands.IsPorted(name))
                    {
                        string local = NativeFlightController.Execute(name, argsJson ?? "{}");
                        if (local != null && !local.StartsWith("Local mode is not ready")) { result = local; return; }
                    }
                    result = BridgeTool(name, argsJson);
                }
                catch (Exception ex) { result = "tool failed: " + ex.Message; }
                finally { done.Set(); }
            });
            if (!done.WaitOne(120000)) return "tool timed out";
            return result ?? "tool failed";
        }
        static string BridgeTool(string name, string argsJson)
        {
            try
            {
                string json = "{\"name\":" + ChatWindow.JsonStr(name) + ",\"args\":" + (string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) + "}";
                var req = BridgeHttp.Create("tool");
                req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 60000; req.Proxy = null;
                byte[] body = Encoding.UTF8.GetBytes(json);
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return rd.ReadToEnd();
            }
            catch (Exception ex) { return "Bridge tool unavailable (" + ex.Message + "). Enable AI-off for local tools, or start the bridge."; }
        }
        internal static void StartModelDownload()
        {
            if (instance == null || Models == null) return;
            if (!BridgeLauncher.AiEnabled) { ChatWindow.Notice("Turn AI on before downloading the model."); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = Models.Download(null, BridgeLauncher.AiEnabled);
                lock (instance.main) instance.main.Enqueue(() =>
                    ChatWindow.Notice(err == null ? "Model ready: " + Models.FinalPath : "Model download: " + err));
            });
        }

        /// <summary>Drop chat sessions and cancel model download when AI is turned off.</summary>
        void OnSceneRequested(GameScenes scene)
        {
            if (!EmbeddedLlm.UnloadOnScene(scene.ToString())) return;
            CancelQueued();                       // cancels the running reply + drops queued chat
            ChatWindow.ReleasePendingChat();
            ThreadPool.QueueUserWorkItem(_ => { try { EmbeddedLlm.Unload(); } catch (Exception) { } });   // lazy reload on next chat
            lock (gate) sessions.Clear();
            Debug.Log("[KSPChatBridge] main menu: in-mod model unloaded");
        }
        void OnDestroy() { GameEvents.onGameSceneLoadRequested.Remove(OnSceneRequested); }
        internal static void UnloadForAiOff()
        {
            if (Models != null) Models.Cancel();
            if (Runtime != null) Runtime.Cancel();
            ThreadPool.QueueUserWorkItem(_ => { try { EmbeddedLlm.Unload(); } catch (Exception) { } });   // frees model/KV off the main thread
            if (instance == null) return;
            lock (instance.gate) instance.sessions.Clear();
            Interlocked.Exchange(ref instance.busy, 0);
        }
    }
}
