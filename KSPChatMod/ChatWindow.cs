// KSPChatBridge in-game chat window.
// Draggable IMGUI window (Flight + Space Center). Toggle with the AppLauncher button (left-click) or Alt+K.
// Right-click the button (or Alt+J) for the AICS menu (AicsMenu.cs).
// Messages go to the local Python bridge (run_bridge.py serve) at http://127.0.0.1:8765/chat
// on a worker thread; replies are queued and appended on the Unity main thread in Update().
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using KSP.UI.Screens;
using UnityEngine;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public class ChatWindow : MonoBehaviour
    {
        const string BridgeUrl = "http://127.0.0.1:8765/";
        const string LockId = "KSPChatBridge_TypingLock";
        const string InputName = "kspchat_input";
        const int WindowId = 0x4B434231;
        const int LandWindowId = 0x4B434232;
        const int MaxHistory = 200;

        // AI backend: picked in AICS -> Settings (dropdown) or "/ai <name>" in the chat; persisted (by id) in window.txt.
        // Type "/model" in chat to list that backend's models, "/model <name>" to switch model.
        static readonly string[] ModelLabels = { "LM Studio", "Ollama", "ChatGPT", "Gemini", "Groq", "OpenRouter", "Hugging Face", "Custom (OpenAI-compatible)", "Claude", "Grok Bot", "Cline", "Embedded Qwen (in-mod)" };
        static readonly string[] ModelIds = { "local", "ollama", "chatgpt", "gemini", "groq", "openrouter", "huggingface", "custom", "claude", "grokbot", "cline", "embedded" };
        static readonly string[] LegacyIds = { "local", "ollama", "chatgpt", "grokbot" };   // old window.txt stored an index
        internal static volatile string BackendsReady = null;  // GET /health "backends_ready" (keyed APIs configured), null = unknown
        const float MinW = 300, MinH = 200, InputH = 24;
        const float InputMaxH = 120;   // the input box grows (word wrap) up to this, then scrolls

        // static so history/settings survive scene changes (the addon is recreated per scene)
        static readonly List<string> History = new List<string>();
        static readonly Queue<string> Incoming = new Queue<string>();
        static readonly object Sync = new object();
        static bool visible;
        static int modelIdx;
        static int pending;
        static int lastEventId;
        static volatile string lastModel = "";   // from the bridge's X-AI-Model header, shown in the title
        static volatile bool cgWaiting;           // ChatGPT (MCP mode) message queued, no reply yet
        internal static volatile string ChatGptMode = "api";   // bridge's chatgpt_mode (GET /health, AicsMenu)
        static int polling;
        static Rect winRect = new Rect(80, 120, 480, 460);
        static float inputExtra;   // extra window height while the input box is taller than one line (not saved)
        static readonly Queue<string> Commands = new Queue<string>();   // "!cmd ..." lines from /events (eject)
        static bool settingsLoaded;
        static string settingsFile;

        // Landing menu (the live landing distance / ETA moved to the STATUS window, StatusWindow.cs)
        static volatile string spotsBody = null;   // raw GET /spots reply, parsed on the main thread
        internal static bool landVisible;
        static Rect landRect = new Rect(580, 120, 360, 420);
        static int landMode;                        // 0 = V (rockets), 1 = H (planes)
        static readonly string[] LandModes = { "V  (rocket, vertical)", "H  (plane, runway)" };
        static readonly List<string[]> Spots = new List<string[]>();  // name, mode, lat, lon, builtin, runways
        static int spotSel = -1;
        static string spotName = "", latText = "", lonText = "", hdgText = "";
        Vector2 spotScroll;

        /// <summary>Thread-safe: show a line in the chat history (used by BridgeLauncher).</summary>
        static string lastNotice; static DateTime lastNoticeAt;
        public static void Notice(string line)
        {
            Debug.Log("[KSPChatBridge] " + line);
            lock (Sync) { if (line == lastNotice && (DateTime.UtcNow - lastNoticeAt).TotalSeconds < 2) return; lastNotice = line; lastNoticeAt = DateTime.UtcNow; }   // same line twice within ms = one post
            if (line != null && !line.StartsWith("[SYSTEM]") && !line.StartsWith("[INTERCOM]")) ChatLog.Write("notice", line);   // audit: every pilot notice (releases, pauses) reaches the chat log
            lock (Sync) Incoming.Enqueue(line);
        }

        string input = "";
        Vector2 scroll;
        bool scrollToEnd;
        bool locked;
        bool resizing;
        bool resizingTR;          // upper-right grip: grows up / right
        Vector2 inputScroll;
        int inputLen;
        GUIStyle inputStyle;
        ApplicationLauncherButton button;
        Texture2D icon;
        string testFile;
        GUIStyle msgStyle;

        static ChatWindow inst;          // this scene's instance (the AppLauncher button lives on it)
        static bool bringFront;          // raise + focus the window on the next OnGUI (opened from the AICS menu)

        /// <summary>Is the chat window open (AICS menu 'chat' button state).</summary>
        internal static bool IsVisible { get { return visible; } }

        /// <summary>Open / close the chat window and keep the AppLauncher button in sync. On open the window is
        /// clamped on screen and raised above the AICS menu (it used to open BEHIND it, looking like nothing happened).</summary>
        internal static void SetVisible(bool on)
        {
            visible = on;
            if (on)
            {
                winRect.x = Mathf.Clamp(winRect.x, 0, Mathf.Max(0, Screen.width - 100));
                winRect.y = Mathf.Clamp(winRect.y, 0, Mathf.Max(0, Screen.height - 60));
                bringFront = true;
            }
            else
            {
                landVisible = false;
                GUIUtility.keyboardControl = 0;
            }
            var b = inst != null ? inst.button : null;
            if (b != null) { if (on) b.SetTrue(false); else b.SetFalse(false); }
        }

        internal static void ToggleChat() { SetVisible(!visible); }

        void Start()
        {
            inst = this;
            testFile = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/test_message.txt");
            settingsFile = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/window.txt");
            if (!settingsLoaded) { LoadSettings(); settingsLoaded = true; }
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            if (ApplicationLauncher.Ready) AddButton();
            InvokeRepeating("PollTestFile", 3f, 2f);
            InvokeRepeating("PollEvents", 5f, 3f);
            Debug.Log("[KSPChatBridge] loaded in scene " + HighLogic.LoadedScene);
        }

        void OnApplicationQuit() { SaveSettings(); }

        void OnDestroy()
        {
            SaveSettings();
            if (inst == this) inst = null;
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            SetTypingLock(false);
        }

        void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            icon = MakeIcon();
            button = ApplicationLauncher.Instance.AddModApplication(
                () => { visible = true; bringFront = true; }, () => visible = false, null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.SPACECENTER,
                icon);
            button.onRightClick = AicsMenu.Toggle;   // right-click: AICS top menu (left-click stays the chat)
            if (visible) button.SetTrue(false);
        }

        void Update()
        {
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (alt && Input.GetKeyDown(KeyCode.K)) ToggleChat();
            lock (Sync)
            {
                while (Incoming.Count > 0) { History.Add(Incoming.Dequeue()); scrollToEnd = true; }
            }
            lock (Sync)
            {
                while (Commands.Count > 0) { RunCommand(Commands.Dequeue()); scrollToEnd = true; }
            }
            string sb = spotsBody;
            if (sb != null) { spotsBody = null; ParseSpots(sb); }
            if (History.Count > MaxHistory) History.RemoveRange(0, History.Count - MaxHistory);
            if (!visible) SetTypingLock(false);
        }

        void OnGUI()
        {
            if (!visible) return;
            if (msgStyle == null) msgStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
            // same look (colors, font) as the old one-line TextField, just word-wrapped
            if (inputStyle == null) inputStyle = new GUIStyle(GUI.skin.textField) { wordWrap = true };
            if (resizingTR)
            {
                if (Input.GetMouseButton(0))
                {
                    Vector2 m = Event.current.mousePosition;
                    float bottom = winRect.yMax;
                    winRect.y = Mathf.Clamp(m.y - 8, 0, bottom - MinH - inputExtra);
                    winRect.height = bottom - winRect.y;
                    winRect.width = Mathf.Clamp(m.x - winRect.x + 8, MinW, Screen.width - winRect.x);
                }
                else { resizingTR = false; SaveSettings(); }
            }
            if (Event.current.type == EventType.Layout)
            {
                // The input box grows downward with its text: the window gets taller by the same amount (history keeps its size).
                float extra = InputHeight(InputW()) - InputH;
                if (Mathf.Abs(extra - inputExtra) > 0.5f)
                {
                    winRect.height = Mathf.Max(MinH, winRect.height + extra - inputExtra);
                    inputExtra = extra;
                    if (winRect.yMax > Screen.height) winRect.y = Mathf.Max(0, Screen.height - winRect.height);
                }
            }
            if (resizing)
            {
                if (Input.GetMouseButton(0))
                {
                    Vector2 m = Event.current.mousePosition;
                    winRect.width = Mathf.Clamp(m.x - winRect.x + 6, MinW, Screen.width);
                    winRect.height = Mathf.Clamp(m.y - winRect.y + 6, MinH, Screen.height);
                }
                else { resizing = false; SaveSettings(); }
            }
            string title = "KSP Chat Bridge - " + ModelLabels[modelIdx] +
                (lastModel.Length > 0 ? " / " + lastModel : "") + "   (Alt+K; switch AI: AICS > Settings or /ai)";
            // GUI.Window (not GUILayout.Window): the rect only changes by dragging/resizing, never by content.
            winRect = GUI.Window(WindowId, winRect, DrawWindow, title);
            if (bringFront) { bringFront = false; GUI.BringWindowToFront(WindowId); GUI.FocusWindow(WindowId); }
            if (landVisible) landRect = GUI.Window(LandWindowId, landRect, DrawLandWindow, "Landing");
            // Block KSP keyboard controls (space = stage!) while typing.
            string focus = GUI.GetNameOfFocusedControl() ?? "";
            SetTypingLock(focus == InputName || focus.StartsWith("kspland_"));
        }

        float InputW() { return winRect.width - 16 - 178; }

        // Height of the input box for the current text (word-wrapped), one line .. InputMaxH.
        float InputHeight(float w)
        {
            if (inputStyle == null) return InputH;
            var gc = new GUIContent((input ?? "") + " ");
            float h = inputStyle.CalcHeight(gc, w - 4);
            if (h > InputMaxH) h = inputStyle.CalcHeight(gc, w - 20);   // room for the scrollbar
            return Mathf.Clamp(h + 2, InputH, InputMaxH);
        }

        void DrawWindow(int id)
        {
            Event e = Event.current;
            // Enter sends; Shift+Enter is a new line in the (multi-line) input box.
            if (e.type == EventType.KeyDown && !e.shift && GUI.GetNameOfFocusedControl() == InputName)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    Send(input);
                    input = "";
                    e.Use();
                }
                else if (e.character == '\n' || e.character == '\r') e.Use();   // the newline char of that Enter
            }

            float innerW = winRect.width - 16;

            if (scrollToEnd && e.type == EventType.Layout) { scroll.y = float.MaxValue; scrollToEnd = false; }
            float inW = InputW(), inH = InputH + inputExtra;
            float histH = winRect.height - 26 - inH - 22;   // title + input row + padding
            scroll = GUILayout.BeginScrollView(scroll, false, true, GUILayout.Width(innerW), GUILayout.Height(histH));
            foreach (string line in History) GUILayout.Label(line, msgStyle, GUILayout.Width(innerW - 24));
            if (pending > 0) GUILayout.Label("... thinking", msgStyle, GUILayout.Width(innerW - 24));
            else if (cgWaiting) GUILayout.Label("... waiting for ChatGPT desktop (MCP) - in ChatGPT say \"check KSP chat\"", msgStyle, GUILayout.Width(innerW - 24));
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal(GUILayout.Width(innerW), GUILayout.Height(inH));
            // Multi-line input: word wraps and grows downward up to InputMaxH, then scrolls (always inside the scroll
            // view so the control id - and the keyboard focus - stays the same when it starts scrolling).
            bool over = inputStyle.CalcHeight(new GUIContent((input ?? "") + " "), inW - 4) + 2 > InputMaxH;
            inputScroll = GUILayout.BeginScrollView(inputScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar,
                GUIStyle.none, GUILayout.Width(inW), GUILayout.Height(inH));
            GUI.SetNextControlName(InputName);
            input = GUILayout.TextArea(input ?? "", 1000, inputStyle, GUILayout.Width(inW - (over ? 20 : 4)), GUILayout.MinHeight(inH - 2));
            GUILayout.EndScrollView();
            if (input.Length != inputLen) { if (over && input.Length > inputLen) inputScroll.y = float.MaxValue; inputLen = input.Length; }
            if (GUILayout.Button("Land", GUILayout.Width(50), GUILayout.Height(InputH)))
            {
                landVisible = !landVisible;
                if (landVisible) { landRect.x = Mathf.Min(winRect.xMax + 6, Screen.width - landRect.width); landRect.y = winRect.y; RefreshSpots(); }
            }
            if (GUILayout.Button("Send", GUILayout.Width(60), GUILayout.Height(InputH))) { Send(input); input = ""; }
            if (GUILayout.Button("Clear", GUILayout.Width(50), GUILayout.Height(InputH))) { History.Clear(); cgWaiting = false; InModAiHost.CancelQueued(); Post("reset", "{\"session\":\"ingame\"}", null); }
            GUILayout.EndHorizontal();

            // Resize handle (bottom-right corner).
            Rect grip = new Rect(winRect.width - 18, winRect.height - 18, 16, 16);
            GUI.Label(grip, "//");
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition)) { resizing = true; e.Use(); }

            // Resize grip (upper-right corner): drag up / right to grow the window (min MinW x MinH).
            Rect gripTR = new Rect(winRect.width - 18, 2, 16, 16);
            GUI.Label(gripTR, "//");
            if (e.type == EventType.MouseDown && e.button == 0 && gripTR.Contains(e.mousePosition)) { resizingTR = true; e.Use(); }

            // Close (x) in the title bar, like the AICS panels; the toolbar button / Alt+K reopens it.
            if (GUI.Button(new Rect(winRect.width - 40, 2, 18, 16), "x")) SetVisible(false);

            GUI.DragWindow();
        }

        void DrawLandWindow(int id)
        {
            float w = landRect.width - 16;
            int nm = GUILayout.Toolbar(landMode, LandModes, GUILayout.Width(w));
            if (nm != landMode) { landMode = nm; spotSel = -1; }
            string mode = landMode == 0 ? "V" : "H";

            GUILayout.Label("Spots (" + (landMode == 0 ? "any spot; runways = midpoint" : "runways only") + "):");
            spotScroll = GUILayout.BeginScrollView(spotScroll, false, true, GUILayout.Width(w), GUILayout.Height(landRect.height - 270));
            for (int i = 0; i < Spots.Count; i++)
            {
                string[] s = Spots[i];
                if (landMode == 1 && s[1] != "H") continue;
                string label = s[0] + "  [" + s[1] + (s[5].Length > 0 ? " " + s[5] : "") + "]  " + s[2] + ", " + s[3];
                bool on = GUILayout.Toggle(spotSel == i, label, GUILayout.Width(w - 24));
                if (on && spotSel != i) spotSel = i;
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUI.enabled = spotSel >= 0 && spotSel < Spots.Count;
            if (GUILayout.Button("Land at spot")) Tool("land_at_spot", "{\"name\":" + JsonStr(Spots[spotSel][0]) + ",\"mode\":\"" + mode + "\"}");
            if (GUILayout.Button("Delete") && Spots[spotSel][4] == "0")
            { Tool("delete_landing_spot", "{\"name\":" + JsonStr(Spots[spotSel][0]) + "}"); spotSel = -1; Invoke("RefreshSpots", 1f); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Land at KSP target")) Tool("land_at_spot", "{\"name\":\"target\",\"mode\":\"" + mode + "\"}");
            if (GUILayout.Button("ETA")) Tool("get_landing_eta", "{}");
            if (GUILayout.Button("Abort")) Tool("abort", "{}");
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(40));
            GUI.SetNextControlName("kspland_name");
            spotName = GUILayout.TextField(spotName, 40);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Mark current position") && spotName.Trim().Length > 0)
            { Tool("save_landing_spot", "{\"name\":" + JsonStr(spotName.Trim()) + ",\"mode\":\"" + mode + "\"}"); Invoke("RefreshSpots", 1.5f); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Lat", GUILayout.Width(24));
            GUI.SetNextControlName("kspland_lat"); latText = GUILayout.TextField(latText, 12, GUILayout.Width(70));
            GUILayout.Label("Lon", GUILayout.Width(26));
            GUI.SetNextControlName("kspland_lon"); lonText = GUILayout.TextField(lonText, 12, GUILayout.Width(70));
            if (landMode == 1) { GUILayout.Label("Hdg", GUILayout.Width(28)); GUI.SetNextControlName("kspland_hdg"); hdgText = GUILayout.TextField(hdgText, 5, GUILayout.Width(40)); }
            GUILayout.EndHorizontal();
            if (GUILayout.Button(landMode == 1 ? "Save runway (threshold lat/lon + landing heading)" : "Save lat/lon spot"))
            {
                double la, lo, hd = -1;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var ns = System.Globalization.NumberStyles.Float;
                if (spotName.Trim().Length > 0 && double.TryParse(latText, ns, ci, out la) && double.TryParse(lonText, ns, ci, out lo))
                {
                    if (landMode == 1 && !double.TryParse(hdgText, ns, ci, out hd)) hd = -1;
                    Tool("save_landing_spot", string.Format(ci, "{{\"name\":{0},\"mode\":\"{1}\",\"latitude\":{2},\"longitude\":{3},\"heading\":{4}}}",
                        JsonStr(spotName.Trim()), mode, la, lo, hd));
                    Invoke("RefreshSpots", 1.5f);
                }
                else Notice("Landing: enter a name and numeric lat/lon first.");
            }
            if (GUI.Button(new Rect(landRect.width - 22, 2, 18, 16), "x")) landVisible = false;
            GUI.DragWindow();
        }

        void Tool(string name, string argsJson)
        {
            if (!BridgeLauncher.UseBridge) { Notice("Landing: " + (NativeCommands.IsPorted(name) ? NativeFlightController.Execute(name, argsJson) : BridgeHttp.Friendly(name))); return; }
            Interlocked.Increment(ref pending);
            Post("tool", "{\"name\":\"" + name + "\",\"args\":" + argsJson + "}", (n, reply) => "Landing: " + reply);
        }

        void RefreshSpots()
        {
            if (!BridgeLauncher.UseBridge) { NativeSpotsNow(); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { spotsBody = HttpGet("spots?format=text", 3000); }
                catch (Exception ex) { Notice("[bridge error] landing spots: " + ex.Message); }
            });
        }

        static void ParseSpots(string body)
        {
            Spots.Clear();
            foreach (string raw in body.Split('\n'))
            {
                string[] f = raw.TrimEnd('\r').Split('\t');
                if (f.Length >= 6) Spots.Add(f);
            }
            if (spotSel >= Spots.Count) spotSel = -1;
        }

        static string HttpGet(string path, int timeoutMs)
        {
            var req = BridgeHttp.Create(path);
            req.Timeout = timeoutMs;
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return rd.ReadToEnd();
        }

        // ---- used by the AICS menu (AicsMenu.cs) ----
        internal static void ShowChat() { SetVisible(true); }
        internal static void ShowLanding() { SetVisible(true); landVisible = true; }
        internal static string Json(string s) { return JsonStr(s); }
        internal static List<string[]> SpotsSnapshot() { return new List<string[]>(Spots); }
        static void NativeSpotsNow() { Spots.Clear(); Spots.AddRange(NativeFlightController.SpotRows()); if (spotSel >= Spots.Count) spotSel = -1; }
        internal static string LastPlayerLine() { for (int i = History.Count - 1; i >= 0; i--) if (History[i].StartsWith("You: ")) return History[i].Substring(5); return ""; }
        internal static void RequestSpots()
        {
            if (!BridgeLauncher.UseBridge) { NativeSpotsNow(); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { spotsBody = HttpGet("spots?format=text", 3000); }
                catch (Exception ex) { Notice("[bridge error] landing spots: " + ex.Message); }
            });
        }
        internal static void ToolFromMenu(string name, string argsJson)
        {
            // P5-3: menu buttons for ported tools run in-mod whenever AI is off or in-mod chat/tools are on.
            if (!BridgeLauncher.AiEnabled || (NativeFlightController.OwnsControls && NativeCommands.IsPorted(name))) { Notice(NativeFlightController.Execute(name, argsJson)); return; }
            Interlocked.Increment(ref pending);
            Post("tool", "{\"name\":\"" + name + "\",\"args\":" + argsJson + "}", (n, reply) => "AICS " + name + ": " + reply);
        }
        internal static void ChatFromMenu(string text)
        {
            if (!BridgeLauncher.AiEnabled) { Notice("AI off. Use the local control panels."); return; }
            lock (Sync) Incoming.Enqueue("You (AICS): " + text);
            if (!visible) SetVisible(true);
            MarkChatGpt(text);
            DispatchChat(text, ModelIds[modelIdx]);
        }

        void Send(string text)
        {
            if (!BridgeLauncher.AiEnabled) { Notice("AI off. Use the local control panels."); return; }
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            string low = text.ToLowerInvariant();
            if (low == "/ai" || low.StartsWith("/ai ") || low == "/backend" || low.StartsWith("/backend "))
            {
                History.Add("You: " + text);
                History.Add("Bridge: " + SwitchBackend(text.Substring(text.IndexOf(' ') < 0 ? text.Length : text.IndexOf(' ')).Trim()));
                scrollToEnd = true;
                return;
            }
            if (low == "/crew" || low.StartsWith("/crew "))
            {
                History.Add("You: " + text);
                History.Add("AICS: " + NativeFlightController.CrewCommand(text.Length > 5 ? text.Substring(5) : ""));
                scrollToEnd = true;
                return;
            }
            string model = ModelIds[modelIdx];
            History.Add("You: " + text);
            scrollToEnd = true;
            MarkChatGpt(text);
            DispatchChat(text, model);
        }

        static void DispatchChat(string text, string model)
        {
            if (UseInModChat(model))
            {
                Interlocked.Increment(ref pending);
                if (HighLogic.LoadedSceneIsFlight && NativeFlightController.TryIntercom(text)) return;   // '@Bob ...' -> Bob answers
                InModAiHost.EnqueueChat(text, model, "ingame");
                return;
            }
            string json = "{\"message\":" + JsonStr(text) + ",\"model\":" + JsonStr(model) + ",\"session\":\"ingame\"}";
            Interlocked.Increment(ref pending);
            Post("chat?format=text", json, (name, reply) => name + ": " + reply);
        }

        static bool UseInModChat(string model)
        {
            if (!InModAiHost.UseInModChat()) return false;
            if (model == "chatgpt" && ChatGptMode != "api") return false;
            return true;
        }

        internal static void ReleasePendingChat()
        {
            Interlocked.Decrement(ref pending);
        }

        // ChatGPT in MCP mode: the bridge queues the message for the ChatGPT desktop app; its reply arrives via /events.
        static void MarkChatGpt(string text)
        {
            if (ModelIds[modelIdx] == "chatgpt" && ChatGptMode != "api" && !text.StartsWith("/")) cgWaiting = true;
        }

        // "/ai <name>" (or /backend): switch backend from the chat; "/ai" alone lists them.
        static string SwitchBackend(string arg)
        {
            string a = arg.ToLowerInvariant();
            if (a.Length > 0)
                for (int i = 0; i < ModelIds.Length; i++)
                    if (ModelIds[i] == a || ModelLabels[i].ToLowerInvariant().StartsWith(a) || (a == "lmstudio" && i == 0) || (a == "openai" && i == 2) || (a == "google" && i == 3) || (a == "hf" && ModelIds[i] == "huggingface"))
                    { BackendIndex = i; return "AI backend: " + ModelLabels[i] + "."; }
            return "AI backend is " + ModelLabels[modelIdx] + ". Switch with /ai <" + string.Join("|", ModelIds) + "> or AICS > Settings.";
        }

        internal static string BackendId(int i) { return ModelIds[i]; }

        // ---- AICS Settings panel: backend dropdown + ChatGPT mode ----
        internal static string[] BackendLabels { get { return ModelLabels; } }
        internal static int BackendIndex
        {
            get { return modelIdx; }
            set { if (value != modelIdx && value >= 0 && value < ModelIds.Length) { modelIdx = value; lastModel = ""; SaveSettings(); } }
        }
        internal static string CurrentModel { get { return ModelIds[modelIdx]; } }
        internal static void SettingFromMenu(string key, string value)
        {
            Interlocked.Increment(ref pending);
            Post("setting", "{\"key\":" + JsonStr(key) + ",\"value\":" + JsonStr(value) + "}", (n, reply) => "Bridge: " + reply);
        }

        // Background HTTP POST; result text (or an error line) is queued for the main thread.
        // format(aiName, replyText); aiName comes from the bridge's X-AI-Name header (set_ai_name tool).
        static void Post(string path, string json, Func<string, string, string> format)
        {
            if (!BridgeHttp.Allowed())
            {
                if (format != null) { Notice(path.StartsWith("chat") ? "This chat backend needs the bridge (ChatGPT desktop/MCP); pick another AI or turn in-mod chat off." : BridgeHttp.Friendly(path)); Interlocked.Decrement(ref pending); }
                return;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string line;
                try
                {
                    var req = BridgeHttp.Create(path);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Timeout = 600000;
                    req.ReadWriteTimeout = 600000;
                    req.Proxy = null;
                    byte[] body = Encoding.UTF8.GetBytes(json);
                    req.ContentLength = body.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                    string aiName;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        aiName = resp.Headers["X-AI-Name"];
                        string m = resp.Headers["X-AI-Model"];
                        if (!string.IsNullOrEmpty(m)) lastModel = m;
                        line = rd.ReadToEnd();
                    }
                    if (format == null) return;
                    line = format(string.IsNullOrEmpty(aiName) ? "AI" : aiName, line);
                    Debug.Log("[KSPChatBridge] reply: " + (line.Length > 300 ? line.Substring(0, 300) : line));
                }
                catch (Exception ex)
                {
                    if (format == null) return;
                    line = "[bridge error] " + ex.Message + "  (is 'python run_bridge.py serve' running? It auto-starts with KSP unless autostart=false in PluginData/bridge.cfg)";
                    Debug.LogWarning("[KSPChatBridge] " + line);
                }
                finally
                {
                    if (format != null) Interlocked.Decrement(ref pending);
                }
                lock (Sync) Incoming.Enqueue(line);
            });
        }

        // Science-watcher notices from the bridge (GET /events, "id<TAB>text" lines), polled off the main thread.
        void PollEvents()
        {
            if (!BridgeLauncher.UseBridge) return;
            if (Interlocked.CompareExchange(ref polling, 1, 0) != 0) return;
            int since = lastEventId;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var req = BridgeHttp.Create("events?format=text&chat=1&cmd=1&since=" + since);
                    req.Timeout = 3000;
                    req.Proxy = null;
                    string body;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        body = rd.ReadToEnd();
                    foreach (string raw in body.Split('\n'))
                    {
                        int tab = raw.IndexOf('\t');
                        int id;
                        if (tab <= 0 || !int.TryParse(raw.Substring(0, tab), out id)) continue;
                        lastEventId = id;  // ascending; after a bridge restart ids start again at 1
                        string t = raw.Substring(tab + 1), line;
                        if (t.StartsWith("!cmd ")) { lock (Sync) Commands.Enqueue(t.Substring(5)); continue; }  // run in Update()
                        if (t.StartsWith("@")) { line = t.Substring(1); cgWaiting = false; }  // ChatGPT desktop (MCP) reply
                        else line = "Bridge: " + t;
                        Debug.Log("[KSPChatBridge] " + line);
                        lock (Sync) Incoming.Enqueue(line);
                    }
                }
                catch (Exception) { /* bridge not running: stay quiet */ }
                finally { Interlocked.Exchange(ref polling, 0); }
            });
        }

        // Test hook: drop a text file at GameData/KSPChatBridge/PluginData/test_message.txt and the
        // mod sends its contents as a chat message (reply is logged to KSP.log with [KSPChatBridge]).
        void PollTestFile()
        {
            try
            {
                if (!File.Exists(testFile)) return;
                string text = File.ReadAllText(testFile);
                File.Delete(testFile);
                Debug.Log("[KSPChatBridge] test message: " + text.Trim());
                Send(text);
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] test hook: " + ex.Message); }
        }

        // Window position/size + selected backend, one line: "x,y,w,h,backendId" (older files: backend index).
        static void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsFile)) return;
                string[] p = File.ReadAllText(settingsFile).Trim().Split(',');
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                float x = float.Parse(p[0], ci), y = float.Parse(p[1], ci), w = float.Parse(p[2], ci), h = float.Parse(p[3], ci);
                w = Mathf.Clamp(w, MinW, Mathf.Min(1200, Screen.width)); h = Mathf.Clamp(h, MinH, Screen.height);
                x = Mathf.Clamp(x, 0, Screen.width - 100); y = Mathf.Clamp(y, 0, Screen.height - 60);
                winRect = new Rect(x, y, w, h);
                if (p.Length > 4)
                {
                    int legacy;
                    string id = int.TryParse(p[4], out legacy) ? LegacyIds[Mathf.Clamp(legacy, 0, LegacyIds.Length - 1)] : p[4].Trim();
                    modelIdx = Math.Max(0, Array.IndexOf(ModelIds, id));
                }
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] settings: " + ex.Message); }
        }

        static void SaveSettings()
        {
            try
            {
                if (settingsFile == null) return;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                File.WriteAllText(settingsFile, string.Format(ci, "{0},{1},{2},{3},{4}",
                    (int)winRect.x, (int)winRect.y, (int)winRect.width, (int)(winRect.height - inputExtra), ModelIds[modelIdx]));
            }
            catch (Exception) { }
        }

        // ---- bridge -> mod commands ("!cmd ..." lines in /events), run on the main thread ----
        static void RunCommand(string cmd)
        {
            string[] p = (cmd ?? "").Trim().Split(new[] { ' ' }, 3);
            if (p.Length >= 2 && p[0] == "eject")
            {
                double t;
                double now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                if (!double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t)
                    || Math.Abs(now - t) > 30)
                {
                    History.Add("Bridge: ignored an old eject command.");   // e.g. replayed after a KSP restart
                    return;
                }
                History.Add("Bridge: " + Eject(p.Length > 2 ? p[2] : null));
            }
            else if (p[0] == "trim_show")
            {
                TrimWindow.ShowTrim();
                History.Add("Bridge: Trim panel opened.");
            }
            else Debug.Log("[KSPChatBridge] unknown bridge command: " + cmd);
        }

        // Real eject: EVA a crew member (by name, else the first one in a command part) out of the active vessel.
        internal static string Eject(string name)
        {
            try
            {
                if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null) return "Eject: not in flight.";
                Vessel v = FlightGlobals.ActiveVessel;
                if (v.isEVA) return "Eject: already on EVA.";
                Part from = null;
                ProtoCrewMember who = null;
                if (!string.IsNullOrEmpty(name))
                    foreach (Part p in v.parts)
                        foreach (ProtoCrewMember c in p.protoModuleCrew)
                            if (who == null && c.name == name) { who = c; from = p; }
                if (who == null)
                    foreach (Part p in v.parts)
                        if (p.protoModuleCrew.Count > 0 && p.FindModuleImplementing<ModuleCommand>() != null) { who = p.protoModuleCrew[0]; from = p; break; }
                if (who == null)
                    foreach (Part p in v.parts)
                        if (p.protoModuleCrew.Count > 0) { who = p.protoModuleCrew[0]; from = p; break; }
                if (who == null) return "Eject: nobody aboard.";
                if (from.airlock == null) return "Eject: " + who.name + "'s part (" + from.partInfo.title + ") has no hatch.";
                if (FlightEVA.fetch == null) return "Eject: KSP's EVA system isn't ready.";
                KerbalEVA k = FlightEVA.fetch.spawnEVA(who, from, from.airlock, true);
                Debug.Log("[KSPChatBridge] eject " + who.name + " from " + from.partInfo.title + ": " + (k != null ? "ok" : "refused"));
                return k != null ? who.name + " ejected!" : "Eject: KSP refused the EVA for " + who.name + " (hatch blocked?).";
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[KSPChatBridge] eject: " + ex);
                return "Eject failed: " + ex.Message;
            }
        }

        void SetTypingLock(bool on)
        {
            if (on == locked) return;
            if (on) InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, LockId);
            else InputLockManager.RemoveControlLock(LockId);
            locked = on;
        }

        internal static string JsonStr(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c); else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static Texture2D MakeIcon()
        {
            // 38x38 speech-bubble icon drawn in code (no texture file needed).
            var tex = new Texture2D(38, 38, TextureFormat.ARGB32, false);
            var clear = new Color(0, 0, 0, 0);
            var white = Color.white;
            for (int x = 0; x < 38; x++)
                for (int y = 0; y < 38; y++)
                {
                    bool bubble = x >= 4 && x <= 33 && y >= 12 && y <= 33;
                    bool tail = y >= 5 && y < 12 && x >= 8 && x <= 8 + (y - 5) * 1.2f;
                    bool dot = y >= 21 && y <= 24 && ((x >= 10 && x <= 13) || (x >= 17 && x <= 20) || (x >= 24 && x <= 27));
                    tex.SetPixel(x, y, dot ? new Color(0.1f, 0.3f, 0.6f) : (bubble || tail ? white : clear));
                }
            tex.Apply();
            return tex;
        }
    }
}
