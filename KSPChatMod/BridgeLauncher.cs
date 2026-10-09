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
        readonly Dictionary<string, string> cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "autostart", "true" }, { "stop_on_quit", "true" },
            { "bridge_dir", "" }, { "python", "python" },
        };

        void Awake()
        {
            DontDestroyOnLoad(this);
            LoadCfg();
            if (!Bool("autostart")) { Debug.Log("[KSPChatBridge] bridge autostart off (bridge.cfg)"); return; }
            string dir = cfg["bridge_dir"], py = cfg["python"];
            string exe = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/Bridge/AICSBridge.exe");
            ThreadPool.QueueUserWorkItem(_ => StartBridge(dir, py, exe));
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
                proc = Process.Start(psi);
                startedByUs = true;
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
            }
            catch (Exception ex)
            {
                startedByUs = false;
                ChatWindow.Notice("[bridge] auto-start failed: " + ex.Message + " (is '" + py + "' on PATH? set python= in PluginData/bridge.cfg)");
            }
        }

        void OnApplicationQuit()
        {
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
