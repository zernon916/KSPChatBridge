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
            ChatLog.Dir = System.IO.Path.Combine(BridgeLauncher.PluginDataDirectory, "logs");
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
            string craft = "";
            try { craft = NativeFlightController.CraftKind(); } catch (Exception) { }
            var direct = ToolRouter.Direct(text, craft);
            ChatLog.Write("router", direct == null ? "model (" + text + ")" : "direct " + direct.Value.Key + " " + direct.Value.Value);
            if (direct != null && NativeFlightController.OwnsControls)   // clear command: no model round-trip
            {
                string res;
                try { res = NativeFlightController.Execute(direct.Value.Key, direct.Value.Value); } catch (Exception ex) { res = "Couldn't: " + ex.Message; }
                ChatWindow.ReleasePendingChat();
                ChatLog.Write("result", res);
                ChatWindow.Notice(CrewVoice.Line(voice.Key, res));
                return;
            }
            instance.Submit(text, provider, session ?? "ingame", true, voice.Key, voice.Value, null, craft);
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
        static int crewRunning;
        const int CrewTimeoutMs = 6000;

        /// <summary>Model-written crew line: only when the model is idle and no player chat is waiting (never starves
        /// chat; a player message preempts a running crew line). false = say the canned line instead.
        /// done(text or null) runs on the main thread.</summary>
        internal static bool TryCrewLine(string system, string prompt, Action<string> done)
        {
            if (instance == null || !BridgeLauncher.AiEnabled || BridgeLauncher.UseBridge) return false;
            if (!ChatterPolicy.ModelFree(Volatile.Read(ref instance.busy) != 0, instance.queue.Pending)) return false;
            string provider = ChatWindow.CurrentModel;
            if (!ChatterPolicy.ProviderReady(provider == "embedded", EmbeddedLlm.Loaded)) return false;   // never load a model just for chatter
            Interlocked.Exchange(ref crewRunning, 1);
            instance.queue.Enqueue(new ChatRequest
            {
                Text = prompt, Provider = provider, Session = "crew", UserPriority = false, DeadlineUtc = DateTime.UtcNow.AddSeconds(3),
                Run = () => { try { return CrewComplete(provider, system, prompt, CrewTimeoutMs, false); } finally { Interlocked.Exchange(ref crewRunning, 0); } },
                Done = done, Settled = () => Interlocked.Exchange(ref crewRunning, 0),
            });
            instance.Pump();
            return true;
        }

        /// <summary>One short line from the current provider, no tools. null on any problem / timeout.</summary>
        internal static string CrewComplete(string provider, string system, string prompt, int timeoutMs, bool allowLoad)
        {
            var ep = OpenAiBackend.Resolve(provider);
            if (!ep.Ok) return null;
            bool emb = ep.Url == OpenAiBackend.EmbeddedUrl;
            if (emb && !allowLoad && !EmbeddedLlm.Loaded) return null;
            var body = new Dictionary<string, object> {
                { "model", ep.Model }, { "temperature", 0.9 }, { "stream", false }, { "max_tokens", CrewPrompts.MaxTokens },
                { "messages", new List<object> {
                    new Dictionary<string, object> { { "role", "system" }, { "content", system ?? "" } },
                    new Dictionary<string, object> { { "role", "user" }, { "content", prompt ?? "" } } } } };
            string raw;
            try
            {
                string payload = MiniJson.Serialize(body);
                if (emb) using (new Timer(_ => EmbeddedLlm.Cancel(), null, timeoutMs, Timeout.Infinite)) raw = EmbeddedLlm.Complete(ep, payload);
                else raw = OpenAiBackend.ChatCompletions(ep, payload, timeoutMs);
            }
            catch (Exception) { return null; }
            return ChatterPolicy.Content(raw);
        }

        /// <summary>'@Bob how are you?' - the crew member answers in character (player chat priority, canned on timeout).</summary>
        internal static void EnqueueIntercom(KeyValuePair<string, string> member, string said, string facts, string pilot, string system)
        {
            if (instance == null) { ChatWindow.ReleasePendingChat(); return; }
            string provider = ChatWindow.CurrentModel;
            instance.Submit(said, provider, "intercom", true, null, null,
                () => IntercomTalk.Reply(member, said, facts, p => CrewComplete(provider, system, p, 20000, true), pilot, 25000));
        }

        void Submit(string text, string provider, string session, bool userPriority, string speaker = null, string persona = null, Func<string> run = null, string craft = "")
        {
            if (userPriority && Volatile.Read(ref crewRunning) != 0) EmbeddedLlm.Cancel();   // player chat preempts a crew line
            var request = new ChatRequest
            {
                Speaker = speaker, Persona = persona ?? "", Run = run, Craft = craft,
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
                    if (request.Run != null) reply = request.Run();
                    else {
                    InModChatSession chat;
                    lock (gate)
                    {
                        if (!sessions.TryGetValue(request.Session ?? "ingame", out chat))
                            sessions[request.Session ?? "ingame"] = chat = new InModChatSession();
                    }
                    chat.Persona = request.Persona ?? ""; chat.Craft = request.Craft ?? "";
                    reply = chat.Process(request.Text, request.Provider, ExecuteTool);
                    }
                }
                catch (Exception ex) { reply = "In-mod AI failed: " + ex.Message; }
                finally { Interlocked.Exchange(ref busy, 0); }
                string line = request.Run != null ? reply : request.UserPriority ? CrewVoice.Line(request.Speaker, reply) : reply;
                lock (main) main.Enqueue(() =>
                {
                    if (request.Settled != null) request.Settled();
                    if (request.Done != null) request.Done(reply); else ChatWindow.Notice(line);
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
