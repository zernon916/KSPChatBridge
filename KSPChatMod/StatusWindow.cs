// KSPChatBridge STATUS and SYSTEMS windows (MechJeb-style info windows), fed by the Python bridge:
//   GET /status?format=text   "key<TAB>value" lines, polled ~1 s: live autopilot state (also drives the AICS menu's
//                             active-mode indicators) - replaces the periodic status line that used to sit in the chat
//   GET /systems?format=text  master caution/warning + one light per system (engines, intakes, control surfaces,
//                             reaction wheels, gear, brakes, chutes, fuel, temperature, G-load, pilot), polled ~1 s in flight
// Toggle both from the AICS menu ("status" / "systems"); visibility and window rects persist in
// GameData/KSPChatBridge/PluginData/status_window.txt. The master warning / caution flashes while an emergency is active
// and not acknowledged - click it to acknowledge (silences the flashing and the MAYDAY light blinking). During an
// unacknowledged warning the craft's Light action group blinks (setting in the Systems window, default on; the bridge
// can veto it with mayday_lights=false). Existing text colors are untouched; only the new lights have their own colors.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public class StatusWindow : MonoBehaviour
    {
        const string BridgeUrl = "http://127.0.0.1:8765/";
        const int StatusId = 0x4B434233, SystemsId = 0x4B434234;
        const float MinW = 260, MinH = 140;

        internal static bool StatusVisible, SystemsVisible;
        static bool maydayLights = true;
        static Rect statusRect = new Rect(100, 320, 400, 300);
        static Rect sysRect = new Rect(520, 320, 440, 440);
        static bool loaded;
        static string cfgFile;

        // written by the poll threads (whole objects swapped, never mutated), read on the main thread
        static volatile Dictionary<string, string> data = new Dictionary<string, string>();
        static volatile List<string[]> rows = new List<string[]>();
        static volatile List<string[]> sysRows = new List<string[]>();
        static volatile string[] master = { "", "0", "" };   // level, seq, text
        static volatile bool bridgeLights = true;
        static volatile bool statusOk, systemsOk;
        static int polling, sysPolling;
        static int ackSeq;

        static readonly Dictionary<string, string> Labels = new Dictionary<string, string> {
            { "autopilot", "Autopilot" }, { "phase", "Phase" }, { "alt", "Altitude" }, { "speed", "Speed" },
            { "heading", "Heading" }, { "bank", "Bank" }, { "pitch", "Pitch" }, { "throttle", "Throttle" },
            { "orders", "Live orders" }, { "overrides", "Overrides" }, { "pilot", "Pilot" }, { "protect", "Protect" },
            { "eta", "Distance / ETA" }, { "plan", "Flight Plan" }, { "alert", "ALERT" } };

        float nextPoll;
        bool resizingStatus, resizingSys;
        Vector2 statusScroll, sysScroll;
        GUIStyle valStyle, keyStyle, okLight, cauLight, failLight, mWarnOn, mCauOn, mDark, mAcked;
        bool blinking, lightOrig;
        Vessel blinkVessel;
        float nextBlink;

        /// <summary>AICS menu indicators: true if the bridge reports this "ind_..." key as active.</summary>
        internal static bool Active(string key)
        {
            var d = data;
            string v;
            return d != null && d.TryGetValue(key, out v) && v == "1";
        }

        internal static void ToggleStatus() { StatusVisible = !StatusVisible; Save(); }
        internal static void ToggleSystems() { SystemsVisible = !SystemsVisible; Save(); }

        void Start()
        {
            cfgFile = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/status_window.txt");
            if (!loaded) { Load(); loaded = true; }
        }

        void OnApplicationQuit() { Save(); RestoreLights(); }

        void OnDestroy() { Save(); RestoreLights(); }

        void Update()
        {
            Blink();
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + 1f;
            if (StatusVisible || AicsMenu.Expanded) PollStatus();
            if (HighLogic.LoadedSceneIsFlight) PollSystems();
            else if (master[0] != "") master = new[] { "", "0", "" };
        }

        // ---------------------------------------------------------------- polling (worker threads)
        static string HttpGet(string path)
        {
            var req = (HttpWebRequest)WebRequest.Create(BridgeUrl + path);
            req.Timeout = 2500;
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return rd.ReadToEnd();
        }

        static void PollStatus()
        {
            if (Interlocked.CompareExchange(ref polling, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var d = new Dictionary<string, string>();
                    var r = new List<string[]>();
                    foreach (string line in HttpGet("status?format=text").Split('\n'))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        string k = line.Substring(0, tab), v = line.Substring(tab + 1).TrimEnd('\r');
                        d[k] = v;
                        if (!k.StartsWith("ind_")) r.Add(new[] { k, v });
                    }
                    data = d;
                    rows = r;
                    statusOk = true;
                }
                catch (Exception)
                {
                    data = new Dictionary<string, string>();
                    statusOk = false;
                }
                finally { Interlocked.Exchange(ref polling, 0); }
            });
        }

        static void PollSystems()
        {
            if (Interlocked.CompareExchange(ref sysPolling, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var r = new List<string[]>();
                    string[] m = { "", "0", "" };
                    bool lights = true;
                    foreach (string raw in HttpGet("systems?format=text").Split('\n'))
                    {
                        string[] f = raw.TrimEnd('\r').Split('\t');
                        if (f[0] == "master" && f.Length >= 4) m = new[] { f[1], f[2], f[3] };
                        else if (f[0] == "sys" && f.Length >= 4) r.Add(new[] { f[1], f[2], f[3] });
                        else if (f[0] == "lights" && f.Length >= 2) lights = f[1].Trim() != "0";
                    }
                    sysRows = r;
                    master = m;
                    bridgeLights = lights;
                    systemsOk = true;
                }
                catch (Exception)
                {
                    systemsOk = false;   // keep the last master state: a busy bridge must not silence an alert
                }
                finally { Interlocked.Exchange(ref sysPolling, 0); }
            });
        }

        static int Seq()
        {
            int s;
            int.TryParse(master[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out s);
            return s;
        }

        static bool Unacked() { return master[0] != "" && Seq() > ackSeq; }

        // ---------------------------------------------------------------- MAYDAY light blinking
        void Blink()
        {
            Vessel v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            bool want = maydayLights && bridgeLights && v != null && master[0] == "warning" && Unacked();
            if (want)
            {
                if (!blinking || blinkVessel != v)
                {
                    RestoreLights();
                    blinking = true;
                    blinkVessel = v;
                    lightOrig = v.ActionGroups[KSPActionGroup.Light];
                    nextBlink = 0f;
                }
                if (Time.realtimeSinceStartup >= nextBlink)
                {
                    nextBlink = Time.realtimeSinceStartup + 0.5f;
                    v.ActionGroups.ToggleGroup(KSPActionGroup.Light);
                }
            }
            else RestoreLights();
        }

        void RestoreLights()
        {
            if (!blinking) return;
            blinking = false;
            try { if (blinkVessel != null) blinkVessel.ActionGroups.SetGroup(KSPActionGroup.Light, lightOrig); }
            catch (Exception) { }
            blinkVessel = null;
        }

        // ---------------------------------------------------------------- drawing
        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        static GUIStyle Light(Color c)
        {
            var s = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            s.normal.textColor = c;
            return s;
        }

        static GUIStyle Lamp(Color bg, Color fg)
        {
            var s = new GUIStyle(GUI.skin.box) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, fontSize = 12 };
            s.normal.background = s.hover.background = s.active.background = Tex(bg);
            s.normal.textColor = s.hover.textColor = s.active.textColor = fg;
            return s;
        }

        void Styles()
        {
            if (valStyle != null) return;
            valStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            keyStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            okLight = Light(new Color(0.35f, 0.95f, 0.35f));
            cauLight = Light(new Color(1f, 0.72f, 0.1f));
            failLight = Light(new Color(1f, 0.25f, 0.2f));
            mWarnOn = Lamp(new Color(0.85f, 0.08f, 0.05f), Color.white);
            mCauOn = Lamp(new Color(0.95f, 0.65f, 0.05f), Color.black);
            mDark = Lamp(new Color(0.15f, 0.15f, 0.16f), new Color(0.45f, 0.45f, 0.47f));
            mAcked = Lamp(new Color(0.45f, 0.12f, 0.1f), new Color(0.95f, 0.85f, 0.85f));
        }

        void OnGUI()
        {
            if (!StatusVisible && !SystemsVisible) return;
            Styles();
            Resize(ref statusRect, ref resizingStatus);
            Resize(ref sysRect, ref resizingSys);
            if (StatusVisible) statusRect = GUI.Window(StatusId, statusRect, DrawStatus, "AICS Status");
            if (SystemsVisible && HighLogic.LoadedSceneIsFlight) sysRect = GUI.Window(SystemsId, sysRect, DrawSystems, "AICS Systems");
        }

        static void Resize(ref Rect r, ref bool resizing)
        {
            if (!resizing) return;
            if (Input.GetMouseButton(0))
            {
                Vector2 m = Event.current.mousePosition;
                r.width = Mathf.Clamp(m.x - r.x + 6, MinW, Screen.width);
                r.height = Mathf.Clamp(m.y - r.y + 6, MinH, Screen.height);
            }
            else { resizing = false; Save(); }
        }

        void Chrome(Rect r, ref bool resizing, bool status)
        {
            Event e = Event.current;
            Rect grip = new Rect(r.width - 18, r.height - 18, 16, 16);
            GUI.Label(grip, "//");
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition)) { resizing = true; e.Use(); }
            if (GUI.Button(new Rect(r.width - 22, 2, 18, 16), "x"))
            {
                if (status) StatusVisible = false; else SystemsVisible = false;
                Save();
            }
            GUI.DragWindow();
        }

        // Master caution / warning lamps: flash while unacknowledged; click = acknowledge / silence.
        void DrawMaster(float w, bool big)
        {
            string lvl = master[0];
            bool flash = (Time.realtimeSinceStartup % 1f) < 0.5f;
            bool unacked = Unacked();
            GUILayout.BeginHorizontal(GUILayout.Width(w));
            GUIStyle warn = lvl == "warning" ? (unacked ? (flash ? mWarnOn : mDark) : mAcked) : mDark;
            GUIStyle cau = lvl == "caution" ? (unacked ? (flash ? mCauOn : mDark) : mCauOn) : mDark;
            float h = big ? 34 : 22;
            string sep = big ? "\n" : " ";
            if (GUILayout.Button("MASTER" + sep + "WARNING", warn, GUILayout.Width(w / 2 - 4), GUILayout.Height(h)) && lvl != "") ackSeq = Seq();
            if (GUILayout.Button("MASTER" + sep + "CAUTION", cau, GUILayout.Width(w / 2 - 4), GUILayout.Height(h)) && lvl != "") ackSeq = Seq();
            GUILayout.EndHorizontal();
            if (lvl != "") GUILayout.Label(master[2] + (unacked ? "   (click a lamp to acknowledge)" : "   (acknowledged)"), valStyle, GUILayout.Width(w));
        }

        void DrawStatus(int id)
        {
            float w = statusRect.width - 16;
            if (HighLogic.LoadedSceneIsFlight && master[0] != "") DrawMaster(w, false);
            if (!statusOk) GUILayout.Label("Bridge not responding on 127.0.0.1:8765 (run_bridge.py serve).", valStyle, GUILayout.Width(w));
            statusScroll = GUILayout.BeginScrollView(statusScroll, false, false, GUILayout.Width(w), GUILayout.ExpandHeight(true));
            foreach (string[] kv in rows)
            {
                string label;
                if (!Labels.TryGetValue(kv[0], out label)) label = kv[0];
                GUILayout.BeginHorizontal();
                GUILayout.Label(label, keyStyle, GUILayout.Width(100));
                GUILayout.Label(kv[1], valStyle, GUILayout.Width(w - 130));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.Space(14);
            Chrome(statusRect, ref resizingStatus, true);
        }

        void DrawSystems(int id)
        {
            float w = sysRect.width - 16;
            DrawMaster(w, true);
            if (!systemsOk) GUILayout.Label("Bridge not responding on 127.0.0.1:8765 (run_bridge.py serve).", valStyle, GUILayout.Width(w));
            sysScroll = GUILayout.BeginScrollView(sysScroll, false, false, GUILayout.Width(w), GUILayout.ExpandHeight(true));
            foreach (string[] r in sysRows)
            {
                GUIStyle light = r[0] == "fail" ? failLight : r[0] == "caution" ? cauLight : okLight;
                GUILayout.BeginHorizontal();
                GUILayout.Label("\u25CF", light, GUILayout.Width(16));
                GUILayout.Label(r[1], keyStyle, GUILayout.Width(Mathf.Min(190, w * 0.45f)));
                GUILayout.Label(r[2], valStyle, GUILayout.Width(w - Mathf.Min(190, w * 0.45f) - 46));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            bool ml = GUILayout.Toggle(maydayLights, " Blink the craft's lights during a MAYDAY (Light action group)");
            if (ml != maydayLights) { maydayLights = ml; Save(); }
            GUILayout.Space(14);
            Chrome(sysRect, ref resizingSys, false);
        }

        // ---------------------------------------------------------------- settings
        static void Load()
        {
            try
            {
                if (cfgFile == null || !File.Exists(cfgFile)) return;
                foreach (string line in File.ReadAllLines(cfgFile))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    float f;
                    float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                    switch (kv[0].Trim())
                    {
                        case "status": StatusVisible = kv[1].Trim() == "1"; break;
                        case "systems": SystemsVisible = kv[1].Trim() == "1"; break;
                        case "lights": maydayLights = kv[1].Trim() != "0"; break;
                        case "sx": statusRect.x = f; break;
                        case "sy": statusRect.y = f; break;
                        case "sw": statusRect.width = Mathf.Max(MinW, f); break;
                        case "sh": statusRect.height = Mathf.Max(MinH, f); break;
                        case "yx": sysRect.x = f; break;
                        case "yy": sysRect.y = f; break;
                        case "yw": sysRect.width = Mathf.Max(MinW, f); break;
                        case "yh": sysRect.height = Mathf.Max(MinH, f); break;
                    }
                }
                statusRect.x = Mathf.Clamp(statusRect.x, 0, Mathf.Max(0, Screen.width - 60));
                statusRect.y = Mathf.Clamp(statusRect.y, 0, Mathf.Max(0, Screen.height - 40));
                sysRect.x = Mathf.Clamp(sysRect.x, 0, Mathf.Max(0, Screen.width - 60));
                sysRect.y = Mathf.Clamp(sysRect.y, 0, Mathf.Max(0, Screen.height - 40));
            }
            catch (Exception ex) { Debug.Log("[KSPChatBridge] status window load: " + ex.Message); }
        }

        static void Save()
        {
            try
            {
                if (cfgFile == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(cfgFile));
                File.WriteAllText(cfgFile, string.Format(CultureInfo.InvariantCulture,
                    "status={0}\nsystems={1}\nlights={2}\nsx={3}\nsy={4}\nsw={5}\nsh={6}\nyx={7}\nyy={8}\nyw={9}\nyh={10}\n",
                    StatusVisible ? 1 : 0, SystemsVisible ? 1 : 0, maydayLights ? 1 : 0,
                    statusRect.x, statusRect.y, statusRect.width, statusRect.height,
                    sysRect.x, sysRect.y, sysRect.width, sysRect.height));
            }
            catch (Exception ex) { Debug.Log("[KSPChatBridge] status window save: " + ex.Message); }
        }
    }
}