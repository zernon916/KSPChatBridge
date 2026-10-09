// AICS Trim panel: pitch / roll / yaw trim (+ collective on rotorcraft), live via POST /tool on the Python bridge.
// Expected JSON from get_trim_state:
//   {"pitch":-1..1,"roll":-1..1,"yaw":-1..1,"collective"?:deg,"heli":bool,"craft":"name","notes_saved":bool,
//    "auto_master":bool,"auto_pitch":bool,"auto_roll":bool,"auto_yaw":bool,"auto_rotor":bool}
// set_trim args: {"axis":"pitch"|"roll"|"yaw"|"collective","value":-1..1 or degrees for collective}
// auto_trim_now / save_craft_notes -> plain-text report; reset -> tool "trim" {"direction":"reset","percent":0}
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public class TrimWindow : MonoBehaviour
    {
        const string BridgeUrl = "http://127.0.0.1:8765/";
        const int WindowId = 0x4B434235;
        const float MinW = 280, MinH = 300, Nudge = 0.05f, CollMax = 12f, PollInterval = 2.5f;

        internal static bool TrimVisible;
        static Rect trimRect = new Rect(140, 360, 320, 300);
        static bool loaded, resizing;
        static string cfgFile;

        static volatile string stateJson = "";
        static volatile bool stateOk;
        static bool everStateOk;
        static float lastGoodState;
        static int polling;
        static float nextPoll;

        static float pitch, roll, yaw, collective = 4f;
        static bool heli, notesSaved;
        static bool autoMaster = true, autoPitch = true, autoRoll = true, autoYaw = true, autoRotor = true;
        static string craftName = "";
        static bool dragPitch, dragRoll, dragYaw, dragColl;
        GUIStyle valStyle, keyStyle, small;

        internal static void ToggleTrim()
        {
            TrimVisible = !TrimVisible;
            if (TrimVisible) PollState();
            Save();
        }

        internal static void ShowTrim()
        {
            if (!TrimVisible) { TrimVisible = true; PollState(); Save(); }
        }

        internal static bool ContainsPoint(Vector2 guiPos)
        {
            return TrimVisible && trimRect.Contains(guiPos);
        }

        void Start()
        {
            cfgFile = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/trim_window.txt");
            if (!loaded) { Load(); loaded = true; }
        }

        void OnApplicationQuit() { Save(); }
        void OnDestroy() { Save(); }

        void Update()
        {
            if (!TrimVisible) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel != null)
            {
                if (!dragPitch) pitch = vessel.ctrlState.pitchTrim;
                if (!dragRoll) roll = vessel.ctrlState.rollTrim;
                if (!dragYaw) yaw = vessel.ctrlState.yawTrim;
                craftName = vessel.vesselName;
            }
            if (Time.realtimeSinceStartup >= nextPoll)
            {
                nextPoll = Time.realtimeSinceStartup + PollInterval;
                if (LocalTrim)
                {
                    string local = NativeFlightController.Execute("get_trim_state", "{}");
                    stateOk = local.StartsWith("{");
                    if (stateOk) { ApplyJson(local); everStateOk = true; lastGoodState = Time.realtimeSinceStartup; }
                    return;
                }
                PollState();
            }
        }

        // P5-1.8: trim panel is fully local whenever AI is off or in-mod chat/tools are on (native set_trim/get_trim_state).
        static bool LocalTrim { get { return !BridgeLauncher.AiEnabled || BridgeLauncher.NativeChat; } }

        static void PollState()
        {
            if (LocalTrim) return;
            if (Interlocked.CompareExchange(ref polling, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string body = PostTool("get_trim_state", "{}");
                    stateJson = body ?? "";
                    stateOk = body != null && body.TrimStart().StartsWith("{");
                    if (stateOk)
                    {
                        ApplyJson(body);
                        everStateOk = true;
                        lastGoodState = Time.realtimeSinceStartup;
                    }
                }
                catch (Exception)
                {
                    stateOk = false;
                }
                finally { Interlocked.Exchange(ref polling, 0); }
            });
        }

        static void ApplyJson(string json)
        {
            // Physical trim is sampled from the vessel on the main thread.
            if (!dragColl) collective = JsonFloat(json, "collective", collective);
            heli = JsonBool(json, "heli");
            notesSaved = JsonBool(json, "notes_saved");
            autoMaster = JsonBool(json, "auto_master", autoMaster);
            autoPitch = JsonBool(json, "auto_pitch", autoPitch);
            autoRoll = JsonBool(json, "auto_roll", autoRoll);
            autoYaw = JsonBool(json, "auto_yaw", autoYaw);
            autoRotor = JsonBool(json, "auto_rotor", autoRotor);
            string c = JsonString(json, "craft");
            if (c.Length > 0) craftName = c;
        }

        void Styles()
        {
            if (valStyle != null) return;
            valStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            keyStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            small = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        }

        void OnGUI()
        {
            if (!TrimVisible) return;
            var skin = AicsMenu.EnsureSkin();
            if (skin != null) GUI.skin = skin;
            Styles();
            Resize();
            trimRect = GUI.Window(WindowId, trimRect, DrawTrim, "AICS Trim");
        }

        void DrawTrim(int id)
        {
            float w = trimRect.width - 16;
            if (craftName.Length > 0)
                GUILayout.Label("Craft: " + craftName + (notesSaved ? "  (notes saved)" : ""), small, GUILayout.Width(w));
            if (!stateOk && !everStateOk)
                GUILayout.Label("Controller unavailable. Physical trim values are local.", valStyle, GUILayout.Width(w));
            else if (!stateOk && Time.realtimeSinceStartup - lastGoodState > PollInterval * 3f)
                GUILayout.Label("Trim state stale — retrying…", small, GUILayout.Width(w));

            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && stateOk;
            AutoTrimHeader(w);
            AutoTrimRow(w, "Pitch", autoPitch, "auto_pitch");
            AutoTrimRow(w, "Roll", autoRoll, "auto_roll");
            AutoTrimRow(w, "Yaw", autoYaw, "auto_yaw");
            if (heli)
                AutoTrimRow(w, "Rotor", autoRotor, "auto_rotor");
            GUILayout.Space(4);

            AxisRow(w, "Pitch", ref pitch, ref dragPitch, "pitch");
            AxisRow(w, "Roll", ref roll, ref dragRoll, "roll");
            AxisRow(w, "Yaw", ref yaw, ref dragYaw, "yaw");
            if (heli)
                CollectiveRow(w);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto-trim now")) ChatWindow.ToolFromMenu("auto_trim_now", "{}");
            if (GUILayout.Button("Save to craft notes")) ChatWindow.ToolFromMenu("save_craft_notes", "{}");
            if (GUILayout.Button("Reset")) ChatWindow.ToolFromMenu("trim", "{\"direction\":\"reset\",\"percent\":0}");
            GUILayout.EndHorizontal();

            GUI.enabled = wasEnabled;

            GUILayout.Space(14);
            Event e = Event.current;
            Rect grip = new Rect(trimRect.width - 18, trimRect.height - 18, 16, 16);
            GUI.Label(grip, "//");
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition)) { resizing = true; e.Use(); }
            if (GUI.Button(new Rect(trimRect.width - 22, 2, 18, 16), "x")) { TrimVisible = false; Save(); }
            GUI.DragWindow();
        }

        void AutoTrimHeader(float w)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(w));
            AutoDot(autoMaster);
            GUILayout.Label("Auto-trim", keyStyle, GUILayout.Width(72));
            if (GUILayout.Button(autoMaster ? "Master ON" : "Master OFF", GUILayout.Width(88)))
                ToggleAuto("auto_master", !autoMaster);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        void AutoTrimRow(float w, string label, bool on, string axis)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(w));
            AutoDot(on && autoMaster);
            GUILayout.Label(label, small, GUILayout.Width(44));
            if (GUILayout.Button(on ? "ON" : "OFF", GUILayout.Width(44)))
                ToggleAuto(axis, !on);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        static void AutoDot(bool on)
        {
            Color prev = GUI.color;
            GUI.color = on ? new Color(0.2f, 0.85f, 0.25f) : new Color(0.45f, 0.45f, 0.45f);
            GUILayout.Label("●", GUILayout.Width(14));
            GUI.color = prev;
        }

        static void ToggleAuto(string axis, bool on)
        {
            if (axis == "auto_master") autoMaster = on;
            else if (axis == "auto_pitch") autoPitch = on;
            else if (axis == "auto_roll") autoRoll = on;
            else if (axis == "auto_yaw") autoYaw = on;
            else if (axis == "auto_rotor") autoRotor = on;
            string json = string.Format(CultureInfo.InvariantCulture, "{{\"axis\":\"{0}\",\"value\":{1}}}", axis, on ? 1 : 0);
            if (LocalTrim) { ChatWindow.Notice(NativeFlightController.Execute("set_trim", json)); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { PostTool("set_trim", json); PollState(); }
                catch (Exception ex) { ChatWindow.Notice("Trim auto: " + ex.Message); }
            });
        }

        void AxisRow(float w, string label, ref float val, ref bool dragging, string axis)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(w));
            GUILayout.Label(label, keyStyle, GUILayout.Width(44));
            if (GUILayout.Button("-", GUILayout.Width(22))) { val = ClampTrim(val - Nudge); SetTrim(axis, val); dragging = false; }
            float n = GUILayout.HorizontalSlider(val, -1f, 1f, GUILayout.Width(w - 130));
            if (Mathf.Abs(n - val) > 0.0001f)
            {
                val = n;
                dragging = true;
                SetTrim(axis, val);
            }
            else if (Event.current.type == EventType.MouseUp) dragging = false;
            if (GUILayout.Button("+", GUILayout.Width(22))) { val = ClampTrim(val + Nudge); SetTrim(axis, val); dragging = false; }
            GUILayout.Label(TrimPct(val), valStyle, GUILayout.Width(44));
            GUILayout.EndHorizontal();
        }

        void CollectiveRow(float w)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(w));
            GUILayout.Label("Coll.", keyStyle, GUILayout.Width(44));
            if (GUILayout.Button("-", GUILayout.Width(22))) { collective = Mathf.Clamp(collective - 0.5f, 0f, CollMax); SetTrim("collective", collective); dragColl = false; }
            float n = GUILayout.HorizontalSlider(collective, 0f, CollMax, GUILayout.Width(w - 130));
            if (Mathf.Abs(n - collective) > 0.01f)
            {
                collective = n;
                dragColl = true;
                SetTrim("collective", collective);
            }
            else if (Event.current.type == EventType.MouseUp) dragColl = false;
            if (GUILayout.Button("+", GUILayout.Width(22))) { collective = Mathf.Clamp(collective + 0.5f, 0f, CollMax); SetTrim("collective", collective); dragColl = false; }
            GUILayout.Label(collective.ToString("0.0", CultureInfo.InvariantCulture) + "°", valStyle, GUILayout.Width(44));
            GUILayout.EndHorizontal();
        }

        static float ClampTrim(float v) { return Mathf.Clamp(v, -1f, 1f); }

        static string TrimPct(float v) { return (100f * v).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%"; }

        static void SetTrim(string axis, float value)
        {
            string json = string.Format(CultureInfo.InvariantCulture, "{{\"axis\":\"{0}\",\"value\":{1}}}", axis, value);
            if (LocalTrim) { NativeFlightController.Execute("set_trim", json); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { PostTool("set_trim", json); }
                catch (Exception ex) { ChatWindow.Notice("Trim set_trim: " + ex.Message); }
            });
        }

        static string PostTool(string name, string argsJson)
        {
            var req = (HttpWebRequest)WebRequest.Create(BridgeUrl + "tool");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.Proxy = null;
            string payload = "{\"name\":\"" + name + "\",\"args\":" + argsJson + "}";
            byte[] body = Encoding.UTF8.GetBytes(payload);
            req.ContentLength = body.Length;
            using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return rd.ReadToEnd();
        }

        static void Resize()
        {
            if (!resizing) return;
            if (Input.GetMouseButton(0))
            {
                Vector2 m = Event.current.mousePosition;
                trimRect.width = Mathf.Clamp(m.x - trimRect.x + 6, MinW, Screen.width);
                trimRect.height = Mathf.Clamp(m.y - trimRect.y + 6, MinH, Screen.height);
            }
            else { resizing = false; Save(); }
        }

        static float JsonFloat(string json, string key, float def)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)");
            if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                return f;
            return def;
        }

        static bool JsonBool(string json, string key, bool def = false)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (!m.Success) return def;
            return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

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
                        case "visible": TrimVisible = kv[1].Trim() == "1"; break;
                        case "x": trimRect.x = f; break;
                        case "y": trimRect.y = f; break;
                        case "w": trimRect.width = Mathf.Max(MinW, f); break;
                        case "h": trimRect.height = Mathf.Max(MinH, f); break;
                    }
                }
                trimRect.x = Mathf.Clamp(trimRect.x, 0, Mathf.Max(0, Screen.width - 60));
                trimRect.y = Mathf.Clamp(trimRect.y, 0, Mathf.Max(0, Screen.height - 40));
            }
            catch (Exception ex) { Debug.Log("[KSPChatBridge] trim window load: " + ex.Message); }
        }

        static void Save()
        {
            try
            {
                if (cfgFile == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(cfgFile));
                File.WriteAllText(cfgFile, string.Format(CultureInfo.InvariantCulture,
                    "visible={0}\nx={1}\ny={2}\nw={3}\nh={4}\n",
                    TrimVisible ? 1 : 0, trimRect.x, trimRect.y, trimRect.width, trimRect.height));
            }
            catch (Exception ex) { Debug.Log("[KSPChatBridge] trim window save: " + ex.Message); }
        }
    }
}
