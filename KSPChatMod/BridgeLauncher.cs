// Auto-starts the bridge when KSP starts, and stops it when KSP quits.
//   Release zip: GameData/KSPChatBridge/Bridge/AICSBridge.exe (no Python needed) - used when bridge_dir is unset.
//   From source: python run_bridge.py serve in bridge_dir.
// Settings: GameData/KSPChatBridge/PluginData/bridge.cfg (created with defaults on first run):
//   autostart = true            start the bridge if GET /health doesn't answer
//   stop_on_quit = true         POST /shutdown on quit (only if this mod started it)
//   bridge_dir =                empty / C:\path\to\KSPChatBridge = use Bridge/AICSBridge.exe; else the repo folder
//   python = python             python.exe for a source bridge_dir (window is hidden)
// Failures are reported in the chat window, never thrown.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class BridgeLauncher : MonoBehaviour
    {
        const string BridgeUrl = "http://127.0.0.1:8765/";
        const string PlaceholderDir = @"C:\path\to\KSPChatBridge";
        static Process proc;
        static bool startedByUs;
        static volatile bool quitting;
        static readonly object Lifecycle = new object();
        static BridgeLauncher instance;
        internal static volatile bool AiEnabled = true, NativeReady, Switching;
        int starting;
        int healthFailStreak;
        float nextWatch;
        float aliveSince;
        readonly RestartPolicy restart = new RestartPolicy();
        const int HealthFailLimit = 3;
        string launchDir, launchPython, launchExe;
        readonly Dictionary<string, string> cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "autostart", "true" }, { "stop_on_quit", "true" }, { "ai_enabled", "true" },
            { "bridge_dir", "" }, { "python", "python" },
        };
        internal static string DataDirectory
        {
            get
            {
                string path = Environment.GetEnvironmentVariable("KSPCHAT_DATA_DIR");
                if (!string.IsNullOrWhiteSpace(path)) return path;
                if (instance != null && !string.IsNullOrWhiteSpace(instance.cfg["bridge_dir"])) return instance.cfg["bridge_dir"];
                return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData");
            }
        }

        void Awake()
        {
            instance = this;
            DontDestroyOnLoad(this);
            LoadCfg();
            AiEnabled = Bool("ai_enabled");
            if (!AiEnabled) NativeReady = true;
            if (!Bool("autostart")) { Debug.Log("[KSPChatBridge] bridge autostart off (bridge.cfg)"); return; }
            launchDir = cfg["bridge_dir"]; launchPython = cfg["python"];
            launchExe = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/Bridge/AICSBridge.exe");
        }

        void Update()
        {
            if (!AiEnabled || Switching) return;
            if (Time.realtimeSinceStartup < nextWatch || Volatile.Read(ref starting) != 0) return;
            nextWatch = Time.realtimeSinceStartup + 2;
            bool alive = false;
            if (proc != null)
            {
                try
                {
                    alive = !proc.HasExited;
                    if (alive)
                    {
                        if (aliveSince == 0) aliveSince = Time.realtimeSinceStartup;
                        if (startedByUs)
                        {
                            if (Healthy(1000)) healthFailStreak = 0;
                            else if (++healthFailStreak >= HealthFailLimit)
                            {
                                ChatWindow.Notice("[bridge] process alive but not responding; restarting with backoff");
                                TerminateOwnedProcess();
                                alive = false;
                                healthFailStreak = 0;
                                restart.Failed(Time.realtimeSinceStartup);
                            }
                        }
                        else healthFailStreak = 0;
                        if (alive && Time.realtimeSinceStartup - aliveSince >= 60) restart.Stable();
                    }
                    if (!alive && proc != null)
                    {
                        aliveSince = 0;
                        healthFailStreak = 0;
                        int code = 0;
                        try { code = proc.ExitCode; } catch (Exception) { }
                        ChatWindow.Notice("[bridge] exited with code " + code + "; retrying with backoff");
                        proc.Dispose(); proc = null; startedByUs = false;
                        restart.Failed(Time.realtimeSinceStartup);
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[bridge] process status: " + ex.Message); return; }
            }
            if (!restart.CanStart(Time.realtimeSinceStartup, Bool("autostart"), quitting, false, alive)) return;
            if (Interlocked.CompareExchange(ref starting, 1, 0) != 0) return;
            restart.Failed(Time.realtimeSinceStartup);
            ThreadPool.QueueUserWorkItem(_ => {
                try { if (!quitting) StartBridge(launchDir, launchPython, launchExe); }
                finally { Interlocked.Exchange(ref starting, 0); }
            });
        }

        static bool Healthy(int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(BridgeUrl + "health");
                req.Timeout = timeoutMs; req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse()) return resp.StatusCode == HttpStatusCode.OK;
            }
            catch (Exception) { return false; }
        }

        static void StartBridge(string dir, string py, string exe)
        {
            try
            {
                if (Healthy(1500)) { Debug.Log("[KSPChatBridge] bridge already running"); return; }
                dir = (dir ?? "").Trim().Trim('"');
                bool dirUnset = dir.Length == 0 || string.Equals(dir.TrimEnd('\\', '/'), PlaceholderDir, StringComparison.OrdinalIgnoreCase);
                bool haveSource = !dirUnset && File.Exists(Path.Combine(dir, "run_bridge.py"));
                bool useExe = File.Exists(exe) && !haveSource;   // source users keep python + bridge_dir
                if (!useExe && !haveSource)
                {
                    ChatWindow.Notice(dirUnset
                        ? "[bridge] auto-start failed: Bridge/AICSBridge.exe is missing (reinstall the mod, or set bridge_dir in PluginData/bridge.cfg)"
                        : "[bridge] auto-start failed: run_bridge.py not found in '" + dir + "' (fix bridge_dir in PluginData/bridge.cfg)");
                    return;
                }
                ProcessStartInfo psi;
                if (useExe)
                {
                    dir = Path.GetDirectoryName(exe); py = exe;
                    psi = new ProcessStartInfo(exe, "serve") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true };
                }
                else
                {
                    psi = new ProcessStartInfo(py, "run_bridge.py serve") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true };
                }
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                lock (Lifecycle)
                {
                    if (quitting || !AiEnabled || Switching) return;
                    proc = Process.Start(psi);
                    startedByUs = true;
                }
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(500);
                    if (Healthy(1000)) { ChatWindow.Notice("[bridge] started automatically (" + dir + ")"); return; }
                    if (proc.HasExited)
                    {
                        ChatWindow.Notice("[bridge] auto-start failed: '" + (useExe ? "AICSBridge.exe serve" : py + " run_bridge.py serve") + "' exited with code " + proc.ExitCode
                            + (useExe ? " (see PluginData/logs/bridge.log)" : " (see logs/ in the bridge folder)"));
                        startedByUs = false;
                        return;
                    }
                }
                ChatWindow.Notice("[bridge] started but not answering on " + BridgeUrl + " yet");
                TerminateOwnedProcess();
                if (instance != null) instance.restart.Failed(Time.realtimeSinceStartup);
            }
            catch (Exception ex)
            {
                startedByUs = false;
                ChatWindow.Notice("[bridge] auto-start failed: " + ex.Message + " (is '" + py + "' on PATH? set python= in PluginData/bridge.cfg)");
            }
        }

        static void TerminateOwnedProcess()
        {
            lock (Lifecycle)
            {
                if (!startedByUs || proc == null) return;
                try { if (!proc.HasExited) proc.Kill(); }
                catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] bridge kill: " + ex.Message); }
                try { proc.Dispose(); }
                catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] bridge dispose: " + ex.Message); }
                proc = null;
                startedByUs = false;
            }
        }

        void OnApplicationQuit()
        {
            lock (Lifecycle) quitting = true;
            if (!startedByUs || !Bool("stop_on_quit")) return;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(BridgeUrl + "shutdown");
                req.Method = "POST"; req.Timeout = 2000; req.Proxy = null; req.ContentLength = 0;
                using (req.GetResponse()) { }
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] bridge shutdown request: " + ex.Message); }
            try
            {
                if (proc != null && !proc.WaitForExit(6000)) proc.Kill();
                Debug.Log("[KSPChatBridge] bridge stopped");
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] bridge stop: " + ex.Message); }
        }

        bool Bool(string k) { string v; return cfg.TryGetValue(k, out v) && v.Trim().ToLowerInvariant() == "true"; }

        static void NativePost(string path)
        {
            var req = (HttpWebRequest)WebRequest.Create(BridgeUrl + path);
            req.Proxy = null; req.Method = "POST"; req.ContentType = "application/json";
            req.ContentLength = 2; req.Timeout = 4000; req.ReadWriteTimeout = 4000;
            using (var stream = req.GetRequestStream()) { stream.WriteByte(123); stream.WriteByte(125); }
            using (req.GetResponse()) { }
        }
        internal static void SetAiEnabled(bool enabled)
        {
            if (instance == null || Switching || enabled == AiEnabled) return;
            if (NativeFlightController.Busy)
            { ChatWindow.Notice("Stop the local controller before switching AI mode."); return; }
            if (Volatile.Read(ref instance.starting) != 0)
            { ChatWindow.Notice("Bridge startup is in progress; switch once it is ready."); return; }
            Switching = true;
            string configPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/bridge.cfg");
            ThreadPool.QueueUserWorkItem(_ => {
                bool prepared = false;
                try
                {
                    if (!enabled && Healthy(1500))
                    {
                        if (!startedByUs || proc == null) throw new InvalidOperationException("An externally started bridge is running; stop it before selecting AI off.");
                        NativePost("native/prepare-off"); prepared = true;
                        NativePost("shutdown");
                        if (!proc.WaitForExit(6000)) throw new InvalidOperationException("Bridge has not stopped yet; mode unchanged.");
                    }
                    lock (Lifecycle)
                    {
                        if (!enabled && proc != null && !proc.HasExited) throw new InvalidOperationException("Bridge still running; mode unchanged.");
                        if (!enabled && proc != null) { proc.Dispose(); proc = null; startedByUs = false; }
                        instance.cfg["ai_enabled"] = enabled ? "true" : "false";
                        var lines = new StringBuilder("# AICS bridge settings\n");
                        foreach (var kv in instance.cfg) lines.Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
                        File.WriteAllText(configPath, lines.ToString());
                        AiEnabled = enabled; NativeReady = !enabled;
                    }
                    ChatWindow.Notice(enabled ? "AI & bridge enabled." : "AI off. Local controls and dashboards remain available.");
                }
                catch (Exception ex)
                {
                    if (prepared) { try { NativePost("native/cancel-off"); } catch (Exception) { } }
                    ChatWindow.Notice("AI mode: " + ex.Message);
                }
                finally { Switching = false; }
            });
        }

        void LoadCfg()
        {
            string file = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/bridge.cfg");
            try
            {
                if (File.Exists(file))
                {
                    foreach (string raw in File.ReadAllLines(file))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                        int eq = line.IndexOf('=');
                        if (eq > 0) cfg[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    var sb = new StringBuilder("# KSPChatBridge: auto-start the bridge with KSP. bridge_dir empty = use Bridge/AICSBridge.exe;\n# set it to a source checkout (run_bridge.py) to run python instead.\n");
                    foreach (var kv in cfg) sb.Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
                    File.WriteAllText(file, sb.ToString());
                }
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] bridge.cfg: " + ex.Message); }
        }
    }
}
