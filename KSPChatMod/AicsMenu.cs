// AICS: the KSPChatBridge main menu (design: docs/AICS_MENU.md).
// Right-click the toolbar button (or Alt+J) toggles a sticky, expandable top menu; left-click stays the chat.
// Dark MechJeb-like IMGUI skin. Each item opens its own window. Every panel is wired: bridge /tool calls (MechJeb via
// kRPC.MechJeb where it has an autopilot), the Flight Plan runner, or local KSP data/actions (Power, Crew, map pick).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public class AicsMenu : MonoBehaviour
    {
        const int MenuId = 0x4B434241;
        static readonly string[] Items = {
            "Aircraft Autopilot", "Approach & Autoland", "Landing Guidance", "Flight Plan", "Orbit Plan",
            "Capture Assist", "Docking", "Orbital Autopilot", "Sun Lock", "Power", "Science",
            "Crew / Station / EVA", "Taxi / Base Run", "Abort / Status", "Settings" };
        static readonly string[] Tags = { "W", "W", "W", "W", "W", "W", "W", "W", "W", "W", "W", "W", "W", "W", "W" };
        static readonly bool[] Open = new bool[Items.Length];
        // which bridge status flag lights the indicator next to each item (null = not an autopilot / mode)
        static readonly string[] IndKeys = { "ind_holds", "ind_autoland", "ind_lander", "ind_flightplan", "ind_mechjeb", null,
            "ind_docking", "ind_mechjeb", null, null, null, null, "ind_taxi", null, null };

        // MechJeb-style: a sticky collapsed tab at the top of the screen ("▲ AICS ▲"); right-click (or click) it to
        // expand the two-column menu, right-click the menu (or the tab) to collapse. Each item opens its own window.
        internal static bool Expanded;
        static bool showTab = true;
        static float tabX = -1f;
        static Rect menuRect = new Rect(-1, 24, 420, 60);
        static readonly Rect[] panelRects = new Rect[Items.Length];
        static bool loaded;
        static string cfgFile;
        static GUISkin skin;
        static GUIStyle hdr, small, green, grey, tagW, tagP, tagS, tabStyle, itemOn, itemOff, warnStyle, tabWarn;
        static bool? mj;
        const float TabW = 132f, TabH = 22f;
        static bool dragging, dragMoved;
        static float dragOff;

        // Required dependencies: MechJeb2, kRPC (+ kRPC.MechJeb) in GameData and the Python bridge on :8765.
        static string missingMods;          // null = not checked yet, "" = all present
        static volatile bool bridgeOk;
        static volatile bool bridgeChecked;
        static int bridgePolling;
        static int bridgeFailStreak;
        static float nextBridgeCheck;
        static bool warned;
        // modules that work without MechJeb / the bridge (local KSP data only)
        static readonly bool[] LocalOnly = { false, false, false, false, false, false, false, false, false, true, false, true, false, false, true };

        static void CheckMods()
        {
            var names = new HashSet<string>(AssemblyLoader.loadedAssemblies.Select(a => a.name));
            var miss = new List<string>();
            if (!names.Any(n => n.StartsWith("MechJeb2"))) miss.Add("MechJeb2");
            if (!names.Contains("KRPC")) miss.Add("kRPC");
            if (!names.Contains("KRPC.MechJeb")) miss.Add("kRPC.MechJeb");
            missingMods = string.Join(", ", miss.ToArray());
            mj = !miss.Contains("MechJeb2");
        }

        static void PollBridge()
        {
            if (!BridgeLauncher.AiEnabled) return;
            if (Interlocked.CompareExchange(ref bridgePolling, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8765/health");
                    req.Timeout = 2500; req.Proxy = null;
                    using (var resp = req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream()))
                    {
                        string body = rd.ReadToEnd();
                        const string key = "\"chatgpt_mode\": \"";
                        int k = body.IndexOf(key);
                        int end = k >= 0 ? body.IndexOf('"', k + key.Length) : -1;
                        if (end > 0) ChatWindow.ChatGptMode = body.Substring(k + key.Length, end - k - key.Length);
                        const string rk = "\"backends_ready\": \"";
                        int r = body.IndexOf(rk);
                        int rend = r >= 0 ? body.IndexOf('"', r + rk.Length) : -1;
                        ChatWindow.BackendsReady = rend >= 0 ? body.Substring(r + rk.Length, rend - r - rk.Length) : null;
                    }
                    Interlocked.Exchange(ref bridgeFailStreak, 0);
                    bridgeOk = true;
                }
                catch (Exception)
                {
                    if (Interlocked.Increment(ref bridgeFailStreak) >= 3)
                        bridgeOk = false;
                }
                finally { bridgeChecked = true; Interlocked.Exchange(ref bridgePolling, 0); }
            });
        }

        static bool DepsOk { get { return missingMods == "" && (!BridgeLauncher.AiEnabled || bridgeOk); } }

        void DepsWarning()
        {
            if (!string.IsNullOrEmpty(missingMods))
                GUILayout.Label("⚠ AICS needs " + missingMods + " installed in GameData (CKAN). Autopilot modules are disabled.", warnStyle);
            if (BridgeLauncher.AiEnabled && bridgeChecked && !bridgeOk)
                GUILayout.Label("⚠ Bridge not responding on 127.0.0.1:8765 - start run_bridge.py serve (it auto-starts with KSP unless autostart=false in PluginData/bridge.cfg).", warnStyle);
        }

        // panel state (static: survives scene changes)
        static bool apAlt, apVs, apHdg, apRoll, apSpd, apAgl = true;
        static string apAltT = "7200", apVsT = "50", apHdgT = "270", apRollT = "0", apSpdT = "200";
        static int rwySel = -1, tgCount;
        static bool shortFinal, vertical, chuteCheck = true;
        static string apprHdgT = "", tgT = "0";
        static string planText = "", planAsk = "";
        // Flight Plan: bridge replies arrive on worker threads; the editor text is swapped in on the GUI thread.
        static volatile string planIncoming;            // plan text to load into the editor (AI fill / template / chat)
        static volatile string planMsg = "";            // last bridge answer (check / fly / stop / errors)
        static volatile string planStatus = "";         // live runner status (GET /flightplan)
        static volatile bool planBusy;                  // AI fill in progress
        static int planRev, planPolling;
        static float nextTaxiLoad;
        static string orbApT = "80000", orbPeT = "80000", orbIncT = "0", xferBodyT = "Mun", capPeT = "", skLonT = "";
        // Landing Guidance map pick (green 4-prong cursor in map view)
        static bool picking, havePick;
        static double pickLat, pickLon;
        static string pickBody = "", pickNameT = "Map pick";
        // Crew transfer / Taxi
        static string crewPick = "", crewMsg = "", taxiSpdT = "8", taxiRouteT = "", taxiMarkT = "";
        static volatile string taxiBody;
        static readonly List<string[]> taxiPts = new List<string[]>();
        static int sciWatch = 1;
        static readonly string[] SciModes = { "off", "remind", "auto" };
        static bool preferMj = true;
        static readonly string[] CgModes = { "OpenAI API key", "ChatGPT desktop (MCP) - heavy token use" };  // 0 = api (default)
        static bool backendDrop;            // AI backend dropdown open
        // In-game API key entry (Settings): POST /api_key saves to the bridge's git-ignored .env and applies it live.
        static string keyInput = "", keyFor = "", cuUrlT, cuModelT;
        static volatile string keyMsg = "", keysBody;
        static int keysPolling;
        static float nextKeysPoll;
        static bool clearArmed, typeLocked;
        static int modelDlBusy;
        static volatile string modelDlMsg = "";
        static readonly Dictionary<string, string> keyInfo = new Dictionary<string, string>();  // "gemini" -> "1\t••••abcd"
        static float opacity = 0.92f;       // window background alpha (slider)
        static float skinOpacity = -1f;

        // live data
        static double ecAmt, ecMax, ecRate, ecPrevAmt = -1, ecPrevT;
        static volatile string landing = "";
        static int polling;
        float nextSample;

        public static void Toggle() { Expanded = !Expanded; showTab = true; Save(); }

        void Start()
        {
            cfgFile = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/PluginData/aics.txt");
            if (!loaded) { Load(); loaded = true; }
        }

        void OnDestroy() { Save(); SetCamLock(false); SetTypeLock(false); }

        // Block KSP keyboard controls (space = stage!) while typing in an AICS key/URL field.
        static void SetTypeLock(bool on)
        {
            if (on == typeLocked) return;
            typeLocked = on;
            if (on) InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, "AICS_TypingLock");
            else InputLockManager.RemoveControlLock("AICS_TypingLock");
        }

        // Block KSP camera orbit/zoom (and KSC building clicks) while the mouse is on AICS or a right-drag started there.
        const string LockId = "AICS_UiLock";
        static bool camLocked, rmbOnUi;
        static Rect lastTab;
        static void SetCamLock(bool on)
        {
            if (on == camLocked) return;
            camLocked = on;
            if (on) InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS | ControlTypes.KSC_FACILITIES, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }
        static bool MouseOverUi()
        {
            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if ((showTab || Expanded) && lastTab.Contains(m)) return true;
            if (Expanded && menuRect.Contains(m)) return true;
            for (int i = 0; i < Open.Length; i++) if (Open[i] && panelRects[i].Contains(m)) return true;
            if (TrimWindow.ContainsPoint(m)) return true;
            return false;
        }

        void Update()
        {
            bool over = MouseOverUi();
            if (Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(0)) rmbOnUi = over;
            if (!Input.GetMouseButton(1) && !Input.GetMouseButton(0)) rmbOnUi = false;
            SetCamLock(over || dragging || rmbOnUi);
            if (!Open.Any(o => o)) SetTypeLock(false);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (alt && Input.GetKeyDown(KeyCode.J)) Toggle();
            if (missingMods == null) CheckMods();
            if (Time.realtimeSinceStartup > nextBridgeCheck)
            {
                nextBridgeCheck = Time.realtimeSinceStartup + (bridgeOk ? 10f : 4f);
                PollBridge();
            }
            if (!warned && missingMods != null && bridgeChecked && Time.timeSinceLevelLoad > 20f)
            {
                warned = true;  // once per session, in chat
                if (missingMods != "") ChatWindow.Notice("AICS: missing required mods: " + missingMods + ". Install them (CKAN) - autopilot modules are disabled until then.");
                if (BridgeLauncher.AiEnabled && !bridgeOk) ChatWindow.Notice("AICS: the Python bridge isn't responding on 127.0.0.1:8765 (run_bridge.py serve). Autopilot modules are disabled until it is up.");
            }
            if (Time.realtimeSinceStartup < nextSample) return;
            nextSample = Time.realtimeSinceStartup + 1f;
            if (!Open.Any(o => o)) return;
            SamplePower();
            if (Open[13] || Open[1] || Open[2] || Open[0]) PollLanding();
            if (Open[3]) PollPlan();
            if (Open[12] && taxiPts.Count == 0 && Time.realtimeSinceStartup > nextTaxiLoad) { nextTaxiLoad = Time.realtimeSinceStartup + 10f; LoadTaxi(); }
        }

        void OnGUI()
        {
            if (!showTab && !Expanded && !Open.Any(o => o) && !picking) return;
            if (skin == null || Mathf.Abs(skinOpacity - opacity) > 0.01f) BuildSkin();
            GUI.skin = skin;
            if (tabX < 0) tabX = Screen.width / 2f - TabW / 2f;
            Rect tab = new Rect(Mathf.Clamp(tabX, 0, Screen.width - TabW), 0, TabW, TabH);
            lastTab = tab;
            GUI.depth = -1000;  // draw above other mods' windows
            Event e = Event.current;
            // left-click: toggle. right-click: toggle on release; right-click-HOLD + move: drag the tab (menu follows)
            if (e.type == EventType.MouseDown && tab.Contains(e.mousePosition))
            {
                if (e.button == 0) { Expanded = !Expanded; Save(); e.Use(); }
                else if (e.button == 1) { dragging = true; dragMoved = false; dragOff = e.mousePosition.x - tab.x; e.Use(); }
            }
            if (dragging)
            {
                if (e.type == EventType.MouseDrag || (e.type == EventType.Repaint && Input.GetMouseButton(1)))
                {
                    float nx = Mathf.Clamp(e.mousePosition.x - dragOff, 0, Screen.width - TabW);
                    if (Mathf.Abs(nx - tabX) > 3f || dragMoved) { dragMoved = true; tabX = nx; }
                }
                if (e.type == EventType.MouseUp || !Input.GetMouseButton(1))
                {
                    dragging = false;
                    if (!dragMoved) Expanded = !Expanded;
                    Save();
                }
            }
            bool depsBad = missingMods != null && (missingMods != "" || (BridgeLauncher.AiEnabled && bridgeChecked && !bridgeOk));
            GUI.Box(tab, (Expanded ? "▼ AICS" : "▲ AICS") + (depsBad ? " ⚠ " : " ") + (Expanded ? "▼" : "▲"), depsBad ? tabWarn : tabStyle);
            if (Expanded)
            {
                // sticky: the menu hangs right under the tab and follows it
                menuRect.x = Mathf.Clamp(tab.x + TabW / 2f - menuRect.width / 2f, 0, Screen.width - menuRect.width);
                menuRect.y = TabH;
                menuRect = GUILayout.Window(MenuId, menuRect, DrawMenu, "AICS  (right-click to collapse)", GUILayout.Width(menuRect.width));
                GUI.BringWindowToFront(MenuId);  // always on top
            }
            for (int i = 0; i < Items.Length; i++)
            {
                if (!Open[i]) continue;
                if (panelRects[i].width < 10)
                    panelRects[i] = new Rect(Mathf.Min(Screen.width - 380, menuRect.x + menuRect.width + 8 + 24 * (i % 5)),
                                             menuRect.y + 30 * (i % 8), 360, 80);
                int idx = i;
                panelRects[i] = GUILayout.Window(MenuId + 1 + i, panelRects[i], id => DrawPanelWindow(idx), Items[i],
                                                 GUILayout.Width(panelRects[i].width));
            }
            if (picking) MapPick(e);
            if (e.type == EventType.Repaint) SetTypeLock((GUI.GetNameOfFocusedControl() ?? "").StartsWith("aics_"));
            GUI.skin = null;
        }

        void DrawMenu(int id)
        {
            GUI.color = GUI.contentColor = Color.white;
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 1) { Expanded = false; Save(); e.Use(); return; }
            GUILayout.BeginHorizontal();
            if (mj == null) mj = AssemblyLoader.loadedAssemblies.Any(a => a.name.StartsWith("MechJeb2"));
            GUILayout.Label(mj.Value ? "MJ" : "no MJ", mj.Value ? green : grey, GUILayout.Width(44));
            GUILayout.Label("Opacity", small, GUILayout.Width(48));
            float no = GUILayout.HorizontalSlider(opacity, 0.2f, 1f);
            opacity = Mathf.Round(no * 20f) / 20f;  // 5 % steps (bounded texture cache)
            // toggle buttons that show their window's state (pressed = open); 'chat' used to only ever "show" (never
            // closed it, and the window could open behind this menu)
            bool chatOn = ChatWindow.IsVisible, stOn = StatusWindow.StatusVisible, syOn = StatusWindow.SystemsVisible, trOn = TrimWindow.TrimVisible;
            if (GUILayout.Toggle(chatOn, "chat", GUI.skin.button, GUILayout.Width(40)) != chatOn) ChatWindow.ToggleChat();
            if (GUILayout.Toggle(stOn, "status", GUI.skin.button, GUILayout.Width(50)) != stOn) StatusWindow.ToggleStatus();     // live autopilot state window
            if (GUILayout.Toggle(syOn, "systems", GUI.skin.button, GUILayout.Width(60)) != syOn) StatusWindow.ToggleSystems();   // parts / emergency dashboard
            if (GUILayout.Toggle(trOn, "trim", GUI.skin.button, GUILayout.Width(44)) != trOn) TrimWindow.ToggleTrim();
            GUILayout.EndHorizontal();
            DepsWarning();
            // two columns of module toggles (MechJeb style); each opens its own window
            int half = (Items.Length + 1) / 2;
            GUILayout.BeginHorizontal();
            for (int col = 0; col < 2; col++)
            {
                GUILayout.BeginVertical(GUILayout.Width(menuRect.width / 2 - 10));
                for (int i = col * half; i < Math.Min(Items.Length, (col + 1) * half); i++)
                {
                    GUILayout.BeginHorizontal();
                    // active-mode indicator (bridge GET /status ind_* keys): green filled = engaged, grey empty = not
                    if (IndKeys[i] != null)
                    {
                        bool act = StatusWindow.Active(IndKeys[i]);
                        GUILayout.Label(act ? "\u25CF" : "\u25CB", act ? green : grey, GUILayout.Width(14));
                    }
                    else GUILayout.Space(18);
                    GUI.enabled = DepsOk || LocalOnly[i] || Open[i];
                    bool on = GUILayout.Toggle(Open[i], Items[i], Open[i] ? itemOn : itemOff);
                    GUI.enabled = true;
                    if (on != Open[i]) { Open[i] = on; Save(); }
                    GUILayout.Label(Tags[i], Tags[i] == "W" ? tagW : Tags[i] == "P" ? tagP : tagS, GUILayout.Width(14));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("W = working, P = partial, S = stub.  Right-click here or the tab to collapse; right-click-hold the tab and drag to move it; Alt+J.", small);
        }

        void DrawPanelWindow(int i)
        {
            GUI.color = GUI.contentColor = Color.white;
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("x", GUILayout.Width(20))) { Open[i] = false; Save(); }
            GUILayout.EndHorizontal();
            if (!LocalOnly[i] && !DepsOk)
            {
                DepsWarning();
                GUI.enabled = false;
            }
            try { DrawPanel(i); }
            catch (Exception ex) { GUILayout.Label("panel error: " + ex.Message, small); }
            GUI.enabled = true;
            GUI.DragWindow();
        }

        void DrawPanel(int i)
        {
            switch (i)
            {
                case 0: Aircraft(); break;
                case 1: Approach(); break;
                case 2: Guidance(); break;
                case 3: FlightPlan(); break;
                case 4: OrbitPlan(); break;
                case 5: Capture(); break;
                case 6: Docking(); break;
                case 7: OrbitalAp(); break;
                case 8: SunLock(); break;
                case 9: Power(); break;
                case 10: Science(); break;
                case 11: Crew(); break;
                case 12: Taxi(); break;
                case 13: Status(); break;
                case 14: Settings(); break;
            }
        }

        // ---------- panels ----------
        void Aircraft()
        {
            Hold(ref apAlt, "Altitude", ref apAltT, "m");
            GUILayout.BeginHorizontal(); GUILayout.Space(20); apAgl = GUILayout.Toggle(apAgl, apAgl ? "AGL (above ground / runway)" : "MSL (sea level)"); GUILayout.EndHorizontal();
            Hold(ref apVs, "Vertical speed", ref apVsT, "m/s");
            Hold(ref apHdg, "Heading", ref apHdgT, "°");
            Hold(ref apRoll, "Roll", ref apRollT, "°");
            Hold(ref apSpd, "Speed (actual)", ref apSpdT, "m/s");
            GUILayout.Label("Throttle drives V/S and speed; pitch never trades altitude for speed. Bank cap 20° / 10° fast.", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(apEngaged ? "Update holds" : "Engage")) { EngageHolds(); apEngaged = true; }
            if (GUILayout.Button("Disengage")) { ChatWindow.ToolFromMenu("plane_hold", "{\"engage\":false}"); apEngaged = false; }
            if (GUILayout.Button("Status", GUILayout.Width(52))) ChatWindow.ToolFromMenu("autopilot_status", "{}");
            GUILayout.EndHorizontal();
        }

        static bool apEngaged;
        static string Num(string t, string fallback)
        {
            double d;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d.ToString(CultureInfo.InvariantCulture) : fallback;
        }

        void EngageHolds()
        {
            var off = new List<string>();
            if (!apAlt) off.Add("altitude");
            if (!apVs) off.Add("vertical_speed");
            if (!apHdg) off.Add("heading");
            if (!apRoll) off.Add("roll");
            if (!apSpd) off.Add("speed");
            string json = "{\"altitude_m\":" + (apAlt ? Num(apAltT, "-1") : "-1") +
                ",\"altitude_ref\":\"" + (apAgl ? "agl" : "msl") + "\"" +
                ",\"vertical_speed\":" + (apVs ? Num(apVsT, "-999") : "-999") +
                ",\"heading\":" + (apHdg ? Num(apHdgT, "-1") : "-1") +
                ",\"roll\":" + (apRoll ? Num(apRollT, "-999") : "-999") +
                ",\"speed\":" + (apSpd ? Num(apSpdT, "-1") : "-1") +
                ",\"off\":" + ChatWindow.Json(string.Join(",", off.ToArray())) + ",\"engage\":true}";
            ChatWindow.ToolFromMenu("plane_hold", json);
        }

        void Approach()
        {
            var rw = ChatWindow.SpotsSnapshot().Where(s => s[1] == "H").ToList();
            if (rw.Count == 0 && GUILayout.Button("Load runways")) ChatWindow.RequestSpots();
            for (int k = 0; k < rw.Count; k++)
                if (GUILayout.Toggle(rwySel == k, rw[k][0] + (rw[k][5].Length > 0 ? "  [" + rw[k][5] + "]" : "")) && rwySel != k) rwySel = k;
            GUILayout.BeginHorizontal();
            shortFinal = GUILayout.Toggle(shortFinal, shortFinal ? "Short final (5 km)" : "Long final (11 km)");
            vertical = GUILayout.Toggle(vertical, vertical ? "V (rocket)" : "Plane (runway)");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Approach hdg", GUILayout.Width(90)); GUI.SetNextControlName("aics_apprhdg"); apprHdgT = GUILayout.TextField(apprHdgT, 5, GUILayout.Width(50));
            GUILayout.Label("Touch&go", GUILayout.Width(62)); GUI.SetNextControlName("aics_tg"); tgT = GUILayout.TextField(tgT, 3, GUILayout.Width(36));
            GUILayout.EndHorizontal();
            chuteCheck = GUILayout.Toggle(chuteCheck, "Chute / dV check before a V landing");
            GUILayout.BeginHorizontal();
            bool was = GUI.enabled;
            GUI.enabled = was && rwySel >= 0 && rwySel < rw.Count;
            if (GUILayout.Button("Land"))
            {
                string name = rw[rwySel][0];
                int.TryParse(tgT, out tgCount);
                string ah = apprHdgT.Trim().Length > 0 ? Num(apprHdgT, "-1") : "-1";   // heading to land on -> runway end
                string opts = ",\"final_km\":" + (shortFinal ? "5" : "0") + ",\"approach_heading\":" + ah;
                if (!vertical && name.StartsWith("Runway"))   // KSC: land_plane (touch-and-go, final length, heading)
                    ChatWindow.ToolFromMenu("land_plane", "{\"runway\":" + ChatWindow.Json(ah != "-1" ? "" : name.Substring(7)) +
                                                          ",\"touch_and_go\":" + tgCount + opts + "}");
                else
                {
                    ChatWindow.ToolFromMenu("land_at_spot", "{\"name\":" + ChatWindow.Json(name) + ",\"mode\":\"" + (vertical ? "V" : "H") + "\"" + opts +
                                                            ",\"chute_check\":" + (vertical && chuteCheck ? "true" : "false") + "}");
                    if (tgCount != 0) ChatWindow.Notice("AICS: touch-and-goes work on the KSC runways only; this is a full-stop landing.");
                }
            }
            GUI.enabled = was && vertical;
            if (GUILayout.Button("Chute / dV check", GUILayout.Width(110))) ChatWindow.ToolFromMenu("landing_check", "{}");
            GUI.enabled = was;
            if (!BridgeLauncher.AiEnabled && GUILayout.Button("Powered descent here")) ChatWindow.ToolFromMenu("land_here", "{}");
            if (GUILayout.Button("Abort")) ChatWindow.ToolFromMenu("abort", "{}");
            GUILayout.EndHorizontal();
            LandingLine();
        }

        void Guidance()
        {
            GUILayout.Label("Saved spots, Mark position, lat/lon entry: Landing window.", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Landing window")) { ChatWindow.ShowLanding(); ChatWindow.RequestSpots(); }
            if (GUILayout.Button("Land at Target")) ChatWindow.ToolFromMenu("land_at_spot", "{\"name\":\"target\",\"mode\":\"" + (vertical ? "V" : "") + "\"}");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(picking ? "Picking... (click the map, Esc = cancel)" : "Map pick (green 4-prong cursor)"))
            {
                picking = !picking;
                if (picking && HighLogic.LoadedSceneIsFlight && !MapView.MapIsEnabled) MapView.EnterMapView();
            }
            GUILayout.EndHorizontal();
            if (havePick)
            {
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "Picked on {0}: {1:F4}, {2:F4}", pickBody, pickLat, pickLon), small);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Land at pick (V)"))
                    ChatWindow.ToolFromMenu("land_at", string.Format(CultureInfo.InvariantCulture, "{{\"latitude\":{0},\"longitude\":{1}}}", pickLat, pickLon));
                GUILayout.Label("Name", small, GUILayout.Width(36));
                GUI.SetNextControlName("aics_pickname");
                pickNameT = GUILayout.TextField(pickNameT, 40, GUILayout.Width(90));
                if (GUILayout.Button("Save spot", GUILayout.Width(70)) && pickNameT.Trim().Length > 0)
                {
                    ChatWindow.ToolFromMenu("save_landing_spot", string.Format(CultureInfo.InvariantCulture,
                        "{{\"name\":{0},\"mode\":\"V\",\"latitude\":{1},\"longitude\":{2}}}", ChatWindow.Json(pickNameT.Trim()), pickLat, pickLon));
                    ChatWindow.RequestSpots();
                }
                GUILayout.EndHorizontal();
            }
            LandingLine();
        }

        void FlightPlan()
        {
            string inc = planIncoming;
            if (inc != null) { planIncoming = null; planText = inc; GUIUtility.keyboardControl = 0; }
            GUILayout.Label("One step per line: takeoff / climb 3000 m agl vs 50 / cruise hdg 090 speed 180 for 3 min / " +
                            "circle 3 laps around field / land  (rockets: ascent 80 km / circularize / transfer Mun). " +
                            "AI fill or a template writes it here; edit; Fly runs it.", small);
            bool was = GUI.enabled;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cruise + circle")) PlanTemplate("circle");
            if (GUILayout.Button("Out & back")) PlanTemplate("cruise");
            if (GUILayout.Button("T&G circuit")) PlanTemplate("circuit");
            if (GUILayout.Button("Orbit (MJ)")) PlanTemplate("orbit");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Ask AI", small, GUILayout.Width(44));
            GUI.SetNextControlName("aics_planask");
            planAsk = GUILayout.TextField(planAsk, 300);
            GUILayout.EndHorizontal();
            GUI.SetNextControlName("aics_plan");
            planText = GUILayout.TextArea(planText, GUILayout.MinHeight(110));
            GUILayout.BeginHorizontal();
            GUI.enabled = was && !planBusy;
            if (GUILayout.Button(planBusy ? "AI drafting..." : "AI fill")) PlanDraft(false);
            if (GUILayout.Button("From chat")) PlanDraft(true);
            GUI.enabled = was;
            if (GUILayout.Button("Check", GUILayout.Width(50))) PlanPost("flightplan/check", "{\"plan\":" + ChatWindow.JsonStr(planText) + "}", false);
            if (GUILayout.Button("Fly", GUILayout.Width(44))) PlanPost("flightplan/fly", "{\"plan\":" + ChatWindow.JsonStr(planText) + "}", true);
            if (GUILayout.Button("Stop", GUILayout.Width(44))) PlanPost("flightplan/stop", "{}", true);
            GUILayout.EndHorizontal();
            string st = planStatus, msg = planMsg;
            if (st.Length > 0) GUILayout.Label(st, small);
            if (msg.Length > 0) GUILayout.Label(msg, small);
            GUILayout.Label("AI fill uses the AI picked in Settings (" + ChatWindow.BackendLabels[ChatWindow.BackendIndex] +
                            "); 'Ask AI' is optional (e.g. climb to 8 km at 40 m/s, circle the field 5 laps, land). " +
                            "Plans the chat AI writes land here too. Planes: bridge autopilot; rocket steps: MechJeb.", small);
        }

        // AI fill / From chat: POST /flightplan/draft -> the normalized plan text replaces the editor content.
        static void PlanDraft(bool fromChat)
        {
            if (!BridgeLauncher.AiEnabled) { planMsg = "AI off: edit the plan or use a local template."; return; }
            planBusy = true;
            planMsg = fromChat ? "Converting the chat's plan..." : "AI is drafting the plan...";
            string json = "{\"model\":" + ChatWindow.JsonStr(ChatWindow.BackendId(ChatWindow.BackendIndex)) +
                          ",\"session\":\"ingame\",\"from_chat\":" + (fromChat ? "true" : "false") +
                          ",\"request\":" + ChatWindow.JsonStr(planAsk.Trim()) + "}";
            BridgeText("flightplan/draft", json, 300000, (ok, body) =>
            {
                planBusy = false;
                if (ok && body.Trim().Length > 0)
                {
                    planIncoming = body.TrimEnd() + "\n";
                    planMsg = "Plan loaded into the editor - check the numbers, then Fly.";
                    ChatWindow.Notice("AICS Flight Plan: " + (fromChat ? "chat plan" : "AI draft") + " loaded into the editor.");
                }
                else planMsg = body;
            });
        }

        static void PlanTemplate(string kind)
        {
            if (!BridgeLauncher.AiEnabled)
            {
                if (kind == "orbit" || kind == "circuit") { planMsg = "This template is not yet supported in local mode."; return; }
                planIncoming = "takeoff\nclimb 1500 m agl\ncruise hdg 090 speed 150 for 2 min\ncircle 1 laps left bank 15\nland Runway 27";
                planMsg = "Local aircraft template loaded."; return;
            }
            BridgeText("flightplan/template?kind=" + kind, null, 5000, (ok, body) =>
            {
                if (ok) { planIncoming = body; planMsg = "Template loaded - edit the numbers, then Fly."; }
                else planMsg = body;
            });
        }

        static void PlanPost(string path, string json, bool toChat)
        {
            if (!BridgeLauncher.AiEnabled) { planMsg = NativeFlightController.Execute(path, json); return; }
            planMsg = "...";
            BridgeText(path, json, 30000, (ok, body) =>
            {
                planMsg = body;
                if (toChat) ChatWindow.Notice("AICS Flight Plan: " + body);
            });
        }

        // Live runner status + plans pushed by the chat AI (GET /flightplan?since=rev, about once a second).
        static void PollPlan()
        {
            if (!BridgeLauncher.AiEnabled) { planStatus = NativeFlightController.PlanStatus; return; }
            if (Interlocked.CompareExchange(ref planPolling, 1, 0) != 0) return;
            int since = planRev;
            BridgeText("flightplan?format=text&since=" + since, null, 3000, (ok, body) =>
            {
                try
                {
                    if (!ok) { planStatus = "Controller unavailable"; return; }
                    int nl = body.IndexOf('\n');
                    string head = nl >= 0 ? body.Substring(0, nl) : body, rest = nl >= 0 ? body.Substring(nl + 1) : "";
                    int tab = head.IndexOf('\t');
                    int rev;
                    if (tab < 0 || !int.TryParse(head.Substring(0, tab), out rev)) return;
                    planStatus = head.Substring(tab + 1).Trim();
                    if (rev < planRev) planRev = rev;               // bridge restarted
                    else if (rev > planRev)
                    {
                        planRev = rev;
                        if (rest.Trim().Length > 0)
                        {
                            planIncoming = rest;
                            planMsg = "The chat AI wrote a plan - loaded into the editor. Check it, then Fly.";
                        }
                    }
                }
                finally { Interlocked.Exchange(ref planPolling, 0); }
            });
        }

        // GET (json == null) or POST JSON to the bridge on a worker thread; done(ok, text) runs on that thread.
        static void BridgeText(string path, string json, int timeoutMs, Action<bool, string> done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = false;
                string text;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8765/" + path);
                    req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.Proxy = null;
                    if (json != null)
                    {
                        req.Method = "POST"; req.ContentType = "application/json";
                        byte[] b = Encoding.UTF8.GetBytes(json);
                        req.ContentLength = b.Length;
                        using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                    }
                    using (var resp = req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        text = rd.ReadToEnd();
                    ok = true;
                }
                catch (WebException ex) when (ex.Response != null)
                {
                    using (var rd = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) text = rd.ReadToEnd().Trim();
                }
                catch (Exception ex) { text = "Couldn't reach the bridge: " + ex.Message; }
                try { done(ok, text); } catch (Exception) { }
            });
        }

        void OrbitPlan()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Ap", GUILayout.Width(20)); GUI.SetNextControlName("aics_ap"); orbApT = GUILayout.TextField(orbApT, 9, GUILayout.Width(70));
            GUILayout.Label("Pe", GUILayout.Width(20)); GUI.SetNextControlName("aics_pe"); orbPeT = GUILayout.TextField(orbPeT, 9, GUILayout.Width(70));
            GUILayout.Label("Inc", GUILayout.Width(26)); GUI.SetNextControlName("aics_inc"); orbIncT = GUILayout.TextField(orbIncT, 5, GUILayout.Width(40));
            GUILayout.EndHorizontal();
            GUILayout.Label((mj == true && preferMj ? "MechJeb Ascent Guidance + maneuver planner / node executor." : "MechJeb not found: these need MechJeb2 + kRPC.MechJeb.") + " Each button burns right away (no extra confirm).", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Ascent to Ap"))
                ChatWindow.ToolFromMenu("mechjeb_ascent", "{\"target_altitude_km\":" + Km(orbApT, 80) + ",\"inclination_deg\":" + Num(orbIncT, "0") + "}");
            if (GUILayout.Button("Launch to target plane")) ChatWindow.ToolFromMenu("launch_to_target_plane", "{\"target_altitude_km\":" + Km(orbApT, 80) + "}");
            if (GUILayout.Button("Circularize")) ChatWindow.ToolFromMenu("circularize", "{}");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Set Ap")) ChatWindow.ToolFromMenu("change_apoapsis", "{\"altitude_km\":" + Km(orbApT, 80) + "}");
            if (GUILayout.Button("Set Pe")) ChatWindow.ToolFromMenu("change_periapsis", "{\"altitude_km\":" + Km(orbPeT, 80) + "}");
            if (GUILayout.Button("Set Inc")) ChatWindow.ToolFromMenu("change_inclination", "{\"inclination_deg\":" + Num(orbIncT, "0") + "}");
            if (GUILayout.Button("Match target plane")) ChatWindow.ToolFromMenu("match_target_plane", "{}");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Transfer to", GUILayout.Width(72));
            GUI.SetNextControlName("aics_xfer");
            xferBodyT = GUILayout.TextField(xferBodyT, 12, GUILayout.Width(70));
            if (GUILayout.Button("Transfer (MJ Hohmann)")) ChatWindow.ToolFromMenu("transfer_to", "{\"body\":" + ChatWindow.Json(xferBodyT.Trim()) + "}");
            if (GUILayout.Button("Ask AI", GUILayout.Width(56))) ChatWindow.ChatFromMenu("Plan a transfer from my current orbit to " + xferBodyT.Trim() + ": dV needed vs what I have, and the steps. Don't burn without my OK.");
            GUILayout.EndHorizontal();
        }

        static string Km(string metres, double fbKm)
        {
            return (ParseD(metres, fbKm * 1000.0) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        }

        static double ParseD(string t, double fb)
        {
            double d;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fb;
        }

        void Capture()
        {
            GUILayout.Label("Arriving at a body: numbers first, then capture with a MechJeb burn at periapsis, or set an aerocapture periapsis.", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Capture numbers")) ChatWindow.ToolFromMenu("capture_plan", "{}");
            if (GUILayout.Button("Capture (circularize)")) ChatWindow.ToolFromMenu("circularize", "{}");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Aerocapture Pe", GUILayout.Width(92));
            GUI.SetNextControlName("aics_cappe");
            capPeT = GUILayout.TextField(capPeT, 6, GUILayout.Width(50));
            GUILayout.Label("km", small, GUILayout.Width(22));
            bool w0 = GUI.enabled;
            GUI.enabled = w0 && ParseD(capPeT, -1) >= 0;
            if (GUILayout.Button("Set Pe (MJ burn)")) ChatWindow.ToolFromMenu("change_periapsis", "{\"altitude_km\":" + Num(capPeT, "30") + "}");
            GUI.enabled = w0;
            GUILayout.EndHorizontal();
            GUILayout.Label("Suggested aerocapture Pe: Kerbin 32, Duna 15, Eve 80, Laythe 30, Jool 140 km ('Capture numbers' shows it for this body).", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Ask AI: capture plan")) ChatWindow.ChatFromMenu("I'm arriving at a body: tell me my periapsis, the capture burn dV needed vs my available dV, and whether aerocapture is possible. Don't burn yet.");
            GUILayout.EndHorizontal();
        }

        void OrbitalAp()
        {
            GUILayout.Label("Antenna Lock: nose to the ground (nadir) for fixed dishes. Station-keep: MechJeb raises Ap to synchronous altitude, puts Ap over the longitude and circularizes there (runs as a Flight Plan).", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Antenna Lock")) ChatWindow.ToolFromMenu("antenna_lock", "{\"on\":true}");
            if (GUILayout.Button("Release")) ChatWindow.ToolFromMenu("antenna_lock", "{\"on\":false}");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Lon", GUILayout.Width(28));
            GUI.SetNextControlName("aics_sklon");
            skLonT = GUILayout.TextField(skLonT, 9, GUILayout.Width(70));
            if (GUILayout.Button(skLonT.Trim().Length > 0 ? "Station-keep over lon" : "Station-keep over target / KSC"))
                ChatWindow.ToolFromMenu("station_keep", "{\"longitude\":" + (skLonT.Trim().Length > 0 ? Num(skLonT, "999") : "999") + "}");
            if (GUILayout.Button("Sync alt?", GUILayout.Width(66))) ChatWindow.ToolFromMenu("sync_orbit_altitude", "{}");
            GUILayout.EndHorizontal();
        }

        void SunLock()
        {
            GUILayout.Label("Space only: nose to the sun and hold (kRPC autopilot). Best-face (side panels) is queued.", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Sun Lock")) ChatWindow.ToolFromMenu("sun_lock", "{\"on\":true}");
            if (GUILayout.Button("Release")) ChatWindow.ToolFromMenu("sun_lock", "{\"on\":false}");
            GUILayout.EndHorizontal();
        }

        void Docking()
        {
            GUILayout.Label("Auto-rendezvous + dock at a free compatible port on the KSP target (size match, nearest free port, fuel check).", small);
            if (GUILayout.Button("Dock with target")) ChatWindow.ToolFromMenu("dock_with", "{\"name\":\"target\"}");
        }

        void Power()
        {
            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null) { GUILayout.Label("flight scene only", small); return; }
            float frac = ecMax > 0 ? (float)(ecAmt / ecMax) : 0f;
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "Electric charge  {0:F0} / {1:F0}  ({2:P0})", ecAmt, ecMax, frac));
            Rect r = GUILayoutUtility.GetRect(100, 10, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(r, Tex(new Color(0.15f, 0.15f, 0.15f)));
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * frac, r.height), Tex(frac > 0.2f ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.9f, 0.3f, 0.2f)));
            string eta = Math.Abs(ecRate) < 1e-4 ? "steady" :
                ecRate < 0 ? "empty in " + Dur(ecAmt / -ecRate) : (ecMax - ecAmt < 0.5 ? "full" : "full in " + Dur((ecMax - ecAmt) / ecRate));
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "Net rate {0:+0.00;-0.00} EC/s   {1}", ecRate, eta), small);
        }

        void Science()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Collect all")) ChatWindow.ToolFromMenu("run_science", "{\"transmit\":false}");
            if (GUILayout.Button("Collect + transmit")) ChatWindow.ToolFromMenu("run_science", "{\"transmit\":true}");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Watcher", GUILayout.Width(60));
            int n = GUILayout.Toolbar(sciWatch, SciModes);
            if (n != sciWatch) { sciWatch = n; ChatWindow.ToolFromMenu("set_science_watcher", "{\"mode\":\"" + SciModes[n] + "\"}"); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset inoperable (scientist)")) ChatWindow.ToolFromMenu("reset_experiments", "{\"discard_data\":false}");
            if (GUILayout.Button("Reset + discard data")) ChatWindow.ToolFromMenu("reset_experiments", "{\"discard_data\":true}");
            GUILayout.EndHorizontal();
        }

        void Crew()
        {
            Vessel v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (v == null) { GUILayout.Label("flight scene only", small); return; }
            if (v.isEVA)
            {
                GUILayout.Label("On EVA: board the nearest free seat with a hatch (within 50 m).", small);
                if (GUILayout.Button("EVA: return to ship")) crewMsg = BoardNearest(v);
                if (crewMsg.Length > 0) GUILayout.Label(crewMsg, small);
                return;
            }
            int total = 0;
            ProtoCrewMember selK = null;
            Part selP = null;
            foreach (Part p in v.parts)
            {
                if (p.CrewCapacity <= 0) continue;
                GUILayout.Label(p.partInfo.title + "  (" + p.protoModuleCrew.Count + "/" + p.CrewCapacity + ")", small);
                foreach (ProtoCrewMember k in p.protoModuleCrew.ToList())
                {
                    total++;
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(12);
                    GUILayout.Label(k.name + " (" + k.experienceTrait.Title + ")", small);
                    if (GUILayout.Button(crewPick == k.name ? "selected" : "move", GUILayout.Width(64))) crewPick = crewPick == k.name ? "" : k.name;
                    GUILayout.EndHorizontal();
                    if (k.name == crewPick) { selK = k; selP = p; }
                }
            }
            if (total == 0) GUILayout.Label("no crew aboard", small);
            if (selK != null)
            {
                GUILayout.Label("Move " + selK.name + " to (free seats; docked ships count as one vessel):", small);
                foreach (Part p in v.parts)
                    if (p != selP && p.CrewCapacity > p.protoModuleCrew.Count &&
                        GUILayout.Button(p.partInfo.title + "  (" + p.protoModuleCrew.Count + "/" + p.CrewCapacity + ")"))
                    { crewMsg = MoveCrew(selK, selP, p); crewPick = ""; }
            }
            if (crewMsg.Length > 0) GUILayout.Label(crewMsg, small);
        }

        static string MoveCrew(ProtoCrewMember k, Part from, Part to)
        {
            try
            {
                if (to.protoModuleCrew.Count >= to.CrewCapacity) return to.partInfo.title + " is full.";
                from.RemoveCrewmember(k);
                to.AddCrewmember(k);
                Vessel v = to.vessel;
                v.SpawnCrew();
                GameEvents.onVesselCrewWasModified.Fire(v);
                return k.name + " moved to " + to.partInfo.title + ".";
            }
            catch (Exception ex) { return "Crew transfer failed: " + ex.Message; }
        }

        static string BoardNearest(Vessel ev)
        {
            KerbalEVA eva = ev.FindPartModuleImplementing<KerbalEVA>();
            if (eva == null) return "Not a kerbal on EVA.";
            Part best = null;
            double bd = double.MaxValue;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == ev || v.isEVA) continue;
                foreach (Part p in v.parts)
                {
                    if (p.CrewCapacity <= p.protoModuleCrew.Count || p.airlock == null) continue;
                    double d = (p.airlock.position - ev.transform.position).magnitude;
                    if (d < bd) { bd = d; best = p; }
                }
            }
            if (best == null) return "No free seat with a hatch nearby.";
            if (bd > 50) return string.Format(CultureInfo.InvariantCulture, "Nearest free hatch ({0} on {1}) is {2:F0} m away: get within 50 m.", best.partInfo.title, best.vessel.vesselName, bd);
            try { eva.BoardPart(best); }
            catch (Exception ex) { return "Boarding failed: " + ex.Message; }
            return "Boarding " + best.partInfo.title + " on " + best.vessel.vesselName + ".";
        }

        // Taxi / Base Run: waypoint list (GET /taxi) with distance/bearing from here; Go = taxi_to (bridge controller).
        void Taxi()
        {
            string tb = taxiBody;
            if (tb != null)
            {
                taxiBody = null;
                taxiPts.Clear();
                foreach (string line in tb.Split('\n'))
                {
                    string[] f = line.TrimEnd('\r').Split('\t');
                    if (f.Length >= 3) taxiPts.Add(f);
                }
            }
            if (taxiPts.Count == 0 && GUILayout.Button("Load taxi points")) LoadTaxi();
            Vessel v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Speed cap", GUILayout.Width(66));
            GUI.SetNextControlName("aics_taxispd");
            taxiSpdT = GUILayout.TextField(taxiSpdT, 4, GUILayout.Width(40));
            GUILayout.Label("m/s", small, GUILayout.Width(28));
            if (GUILayout.Button("Stop (abort)")) ChatWindow.ToolFromMenu("abort", "{}");
            if (GUILayout.Button("Refresh", GUILayout.Width(60))) LoadTaxi();
            GUILayout.EndHorizontal();
            foreach (string[] p in taxiPts)
            {
                double la = ParseD(p[1], 0), lo = ParseD(p[2], 0);
                string where = "";
                if (v != null && v.mainBody != null)
                {
                    double d = GcDist(v.latitude, v.longitude, la, lo, v.mainBody.Radius);
                    where = d < 10000 ? string.Format(CultureInfo.InvariantCulture, "{0:F0} m, brg {1:000}", d, Brg(v.latitude, v.longitude, la, lo))
                                      : string.Format(CultureInfo.InvariantCulture, "{0:F1} km", d / 1000);
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label(p[0] + "   " + where, small);
                if (GUILayout.Button("Go", GUILayout.Width(36)))
                    ChatWindow.ToolFromMenu("taxi_to", "{\"name\":" + ChatWindow.Json(p[0]) + ",\"speed\":" + Num(taxiSpdT, "8") + "}");
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Route", GUILayout.Width(40));
            GUI.SetNextControlName("aics_taxiroute");
            taxiRouteT = GUILayout.TextField(taxiRouteT, 200);
            if (GUILayout.Button("Go", GUILayout.Width(36)) && taxiRouteT.Trim().Length > 0)
                ChatWindow.ToolFromMenu("taxi_to", "{\"name\":" + ChatWindow.Json(taxiRouteT.Trim()) + ",\"speed\":" + Num(taxiSpdT, "8") + "}");
            GUILayout.EndHorizontal();
            GUILayout.Label("Route = point names separated by ';' (or lat,lon). Straight lines - no obstacle avoidance; rovers use wheel motors, planes engines + brakes.", small);
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("aics_taximark");
            taxiMarkT = GUILayout.TextField(taxiMarkT, 40);
            if (GUILayout.Button("Mark here as point", GUILayout.Width(130)) && taxiMarkT.Trim().Length > 0)
            {
                ChatWindow.ToolFromMenu("save_landing_spot", "{\"name\":" + ChatWindow.Json(taxiMarkT.Trim()) + ",\"mode\":\"V\"}");
                taxiMarkT = "";
                LoadTaxi();
            }
            GUILayout.EndHorizontal();
        }

        static void LoadTaxi()
        {
            if (!BridgeLauncher.AiEnabled) { taxiBody = NativeFlightController.Execute("taxi/list", "{}"); return; }
            BridgeText("taxi", null, 4000, (ok, body) => { if (ok) taxiBody = body; else ChatWindow.Notice("AICS Taxi: " + body); });
        }

        static double GcDist(double la1, double lo1, double la2, double lo2, double r)
        {
            double p1 = la1 * Math.PI / 180, p2 = la2 * Math.PI / 180, dp = p2 - p1, dl = (lo2 - lo1) * Math.PI / 180;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        static double Brg(double la1, double lo1, double la2, double lo2)
        {
            double p1 = la1 * Math.PI / 180, p2 = la2 * Math.PI / 180, dl = (lo2 - lo1) * Math.PI / 180;
            double y = Math.Sin(dl) * Math.Cos(p2), x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        }

        // Map pick: green 4-prong cursor (MechJeb's is a yellow 3-prong); left-click on the body = target lat/lon.
        static void MapPick(Event e)
        {
            if (!HighLogic.LoadedSceneIsFlight) { picking = false; return; }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { picking = false; e.Use(); return; }
            if (!MapView.MapIsEnabled) return;
            Vector2 m = e.mousePosition;
            if (e.type == EventType.Repaint)
            {
                Texture2D g = Tex(new Color(0.3f, 1f, 0.3f, 0.95f));
                const float gap = 5f, len = 11f, th = 2f;
                GUI.DrawTexture(new Rect(m.x - gap - len, m.y - th / 2, len, th), g);
                GUI.DrawTexture(new Rect(m.x + gap, m.y - th / 2, len, th), g);
                GUI.DrawTexture(new Rect(m.x - th / 2, m.y - gap - len, th, len), g);
                GUI.DrawTexture(new Rect(m.x - th / 2, m.y + gap, th, len), g);
            }
            if (e.type == EventType.MouseDown && e.button == 0 && !MouseOverUi())
            {
                double lat, lon;
                CelestialBody body;
                if (MouseLatLon(out lat, out lon, out body))
                {
                    pickLat = lat; pickLon = lon; pickBody = body.bodyName; havePick = true; picking = false;
                    ChatWindow.Notice(string.Format(CultureInfo.InvariantCulture, "AICS map pick: {0} {1:F4}, {2:F4}", body.bodyName, lat, lon));
                    Vessel v = FlightGlobals.ActiveVessel;
                    if (v != null && v.mainBody != body) ChatWindow.Notice("AICS: that point is on " + body.bodyName + " but the craft is at " + v.mainBody.bodyName + ".");
                }
                e.Use();
            }
        }

        static bool MouseLatLon(out double lat, out double lon, out CelestialBody body)
        {
            lat = lon = 0;
            body = null;
            MapObject t = PlanetariumCamera.fetch != null ? PlanetariumCamera.fetch.target : null;
            if (t != null) body = t.celestialBody != null ? t.celestialBody : (t.vessel != null ? t.vessel.mainBody : null);
            if (body == null) body = FlightGlobals.currentMainBody;
            if (body == null || PlanetariumCamera.Camera == null) return false;
            Ray ray = PlanetariumCamera.Camera.ScreenPointToRay(Input.mousePosition);
            Vector3d o = ScaledSpace.ScaledToLocalSpace(ray.origin);
            Vector3d d = ((Vector3d)ray.direction).normalized;
            Vector3d rel = o - body.position;
            double b = Vector3d.Dot(rel, d), c = rel.sqrMagnitude - body.Radius * body.Radius, disc = b * b - c;
            if (disc < 0) return false;
            double tt = -b - Math.Sqrt(disc);
            if (tt < 0) return false;
            Vector3d hit = o + d * tt;
            lat = body.GetLatitude(hit);
            lon = (body.GetLongitude(hit) % 360 + 540) % 360 - 180;
            return true;
        }

        void Status()
        {
            if (GUILayout.Button("ABORT autopilot", GUILayout.Height(26))) ChatWindow.ToolFromMenu("abort", "{}");
            Vessel v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (v != null)
            {
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "Radar alt {0:N0} m   MSL {1:N0} m", v.radarAltitude, v.altitude));
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "Speed {0:F0} m/s   V/S {1:+0.0;-0.0} m/s   Hdg {2:000}°", v.srfSpeed, v.verticalSpeed, FlightGlobals.ship_heading));
            }
            LandingLine();
            if (GUILayout.Button("Autopilot status (heading error +right / -left)")) ChatWindow.ToolFromMenu("autopilot_status", "{}");
        }

        void Settings()
        {
            bool ai = GUILayout.Toggle(BridgeLauncher.AiEnabled, "AI & Bridge");
            if (ai != BridgeLauncher.AiEnabled) BridgeLauncher.SetAiEnabled(ai);
            if (BridgeLauncher.Switching) GUILayout.Label("Switching mode after controller handoff...");
            if (!BridgeLauncher.AiEnabled) GUILayout.Label("AI off: local controls available; unported commands are disabled.");
            DrawInModAiSettings();
            if (!BridgeLauncher.AiEnabled) return;
            // AI backend dropdown (IMGUI has none: a button that unfolds the option list inline).
            string[] labels = ChatWindow.BackendLabels;
            int cur = ChatWindow.BackendIndex;
            GUILayout.BeginHorizontal();
            GUILayout.Label("AI backend", small, GUILayout.Width(70));
            if (GUILayout.Button(labels[cur] + (backendDrop ? "   ^" : "   v"))) backendDrop = !backendDrop;
            GUILayout.EndHorizontal();
            if (backendDrop)
            {
                for (int i = 0; i < labels.Length; i++)
                {
                    if (GUILayout.Button((i == cur ? "> " : "   ") + labels[i] + "   " + BackendHint(i), GUI.skin.label)) { ChatWindow.BackendIndex = i; backendDrop = false; }
                }
            }
            string id = ChatWindow.BackendId(cur);
            if (id == "chatgpt")
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("ChatGPT via", small, GUILayout.Width(70));
                int cg = ChatWindow.ChatGptMode == "mcp" ? 1 : 0;
                int ncg = GUILayout.Toolbar(cg, CgModes);
                GUILayout.EndHorizontal();
                if (ncg != cg) { ChatWindow.ChatGptMode = ncg == 1 ? "mcp" : "api"; ChatWindow.SettingFromMenu("chatgpt_mode", ChatWindow.ChatGptMode); }
                GUILayout.Label(ncg == 1 ? "MCP (opt-in): no API key, but HEAVY token use - the ChatGPT desktop thread keeps polling. Keep a thread with the ksp_bridge tools open and say \"check KSP chat\"."
                                         : "API: paste your OpenAI API key (platform.openai.com) below and Save.", ncg == 1 ? warnStyle : small);
            }
            else if (id == "gemini") GUILayout.Label("Gemini (free tier): free key from aistudio.google.com. Paste it below and Save.", small);
            else if (id == "groq") GUILayout.Label("Groq (free tier, fast): free key from console.groq.com. Free = 8K tokens/min, so tool steps may pause ~10-30 s.", small);
            else if (id == "openrouter") GUILayout.Label("OpenRouter free models: key from openrouter.ai. Free = 50 requests/day (~15-25 chat messages).", small);
            else if (id == "huggingface") GUILayout.Label("Hugging Face router: token (huggingface.co/settings/tokens, Inference Providers permission). Free credits are tiny.", small);
            else if (id == "custom") GUILayout.Label("Custom: any OpenAI-compatible API. Base URL (e.g. https://api.mistral.ai/v1) + model id, key if the API needs one.", small);
            else if (id == "local") GUILayout.Label("LM Studio on this PC (no key). /model lists/switches models.", small);
            else if (id == "ollama") GUILayout.Label("Ollama on this PC (ollama serve, no key). /model lists/switches models.", small);
            if (NeedsKey(id)) KeyBox(id);
            GUILayout.Label("Also: type /ai <name> in the chat (e.g. /ai gemini).", small);
            GUILayout.Label("Required: MechJeb2, kRPC, kRPC.MechJeb, bridge.  Missing mods: " + (string.IsNullOrEmpty(missingMods) ? "none" : missingMods) +
                "   Bridge: " + (bridgeOk ? "OK" : (bridgeChecked ? "NOT responding" : "checking...")), small);
            if (GUILayout.Button("Re-check dependencies")) { missingMods = null; nextBridgeCheck = 0; }
            preferMj = GUILayout.Toggle(preferMj, "Prefer MechJeb when available");
            GUILayout.Label("Bridge autostart: PluginData/bridge.cfg (autostart=true).", small);
            showTab = GUILayout.Toggle(showTab, "Show the collapsed ▲ AICS ▲ tab at the top");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Opacity", small, GUILayout.Width(52));
            opacity = Mathf.Round(GUILayout.HorizontalSlider(opacity, 0.2f, 1f) * 20f) / 20f;
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Reset menu / tab position")) { menuRect.x = -1; menuRect.y = 24; tabX = -1; }
        }

        void DrawInModAiSettings()
        {
            GUILayout.Label("In-mod AI (Phase 4)", hdr);
            bool nc = GUILayout.Toggle(BridgeLauncher.NativeChatEnabled, "Chat in-mod (no bridge)");
            if (nc != BridgeLauncher.NativeChatEnabled) BridgeLauncher.SetNativeChat(nc);
            var policy = AiSettings.Policy;
            GUILayout.Label("Runtime offload (embedded llama still gated until 4A passes)", small);
            GUILayout.BeginHorizontal();
            int off = (int)policy.Offload;
            int noff = GUILayout.Toolbar(off, new[] { "CPU", "Hybrid", "GPU" });
            if (noff != off) AiSettings.ApplyOffload((AiOffloadMode)noff, policy.ContextTokens);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Context", small, GUILayout.Width(52));
            int[] ctxOpts = { 16384, 20480, 24576 };
            string[] ctxLbl = { "16k", "20k", "24k" };
            int ctxIdx = 0;
            for (int i = 0; i < ctxOpts.Length; i++) if (policy.ContextTokens == ctxOpts[i]) ctxIdx = i;
            int nctx = GUILayout.Toolbar(ctxIdx, ctxLbl);
            if (nctx != ctxIdx) AiSettings.ApplyOffload(policy.Offload, ctxOpts[nctx]);
            GUILayout.EndHorizontal();
            var mm = InModAiHost.Models ?? ModelManager.Instance;
            string prog = mm.Phase == "idle" ? (mm.Ready ? "Model on disk." : "Model not downloaded.") :
                string.Format(CultureInfo.InvariantCulture, "{0} {1:P0}", mm.Phase, mm.Progress);
            GUILayout.Label(prog, small);
            if (mm.Error != null && mm.Phase != "ready") GUILayout.Label(mm.Error, warnStyle);
            else if (!string.IsNullOrEmpty(mm.Warning)) GUILayout.Label(mm.Warning, small);
            GUILayout.BeginHorizontal();
            GUI.enabled = BridgeLauncher.AiEnabled && Interlocked.CompareExchange(ref modelDlBusy, 0, 0) == 0 && !mm.Ready;
            if (GUILayout.Button("Download Qwen2.5-3B Q4_K_M"))
            {
                if (!BridgeLauncher.AiEnabled)
                    ChatWindow.Notice("Turn on AI & Bridge (or enable AI in bridge.cfg) to download the local model.");
                else if (Interlocked.CompareExchange(ref modelDlBusy, 1, 0) == 0)
                {
                    modelDlMsg = "Starting download...";
                    ModelManager.Instance.Cancel();
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            string err = ModelManager.Instance.Download(null, BridgeLauncher.AiEnabled);
                            modelDlMsg = err ?? (ModelManager.Instance.Warning ?? "Download complete.");
                        }
                        catch (Exception ex) { modelDlMsg = ex.Message; }
                        finally { Interlocked.Exchange(ref modelDlBusy, 0); }
                    });
                }
            }
            GUI.enabled = mm.Phase == "downloading" || modelDlBusy == 1;
            if (GUILayout.Button("Cancel download")) ModelManager.Instance.Cancel();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (modelDlMsg.Length > 0) GUILayout.Label(modelDlMsg, small);
            string inModStatus = InModChatStatus(mm);
            GUILayout.Label("Status: " + inModStatus, inModStatus.StartsWith("In-mod chat ready") ? tagW : (inModStatus.Contains("error") || inModStatus.Contains("not") ? warnStyle : small));
            if (BridgeLauncher.NativeChatEnabled)
            {
                string backend = ChatWindow.BackendId(ChatWindow.BackendIndex);
                if (NeedsKey(backend) || backend == "local" || backend == "ollama")
                {
                    GUILayout.Label("Keys / endpoints (PluginData/.env — in-mod chat + bridge on restart)", small);
                    if (NeedsKey(backend)) KeyBox(backend);
                    else GUILayout.Label(backend == "local"
                        ? "LM Studio: set LMSTUDIO_URL / LMSTUDIO_MODEL in .env if not localhost:1234."
                        : "Ollama: set OLLAMA_URL / OLLAMA_MODEL in .env if not localhost:11434.", small);
                }
            }
        }

        static string InModChatStatus(ModelManager mm)
        {
            if (!BridgeLauncher.NativeChatEnabled) return "In-mod chat disabled (bridge chat when AI & Bridge is on).";
            if (!BridgeLauncher.AiEnabled) return "AI off — enable AI & Bridge to use in-mod HTTP chat.";
            if (mm.Phase == "downloading") return "Downloading model...";
            if (!string.IsNullOrEmpty(mm.Error) && mm.Phase != "ready") return "Model error: " + mm.Error;
            return "In-mod chat ready (HTTP providers / LM Studio / Ollama). Embedded llama still gated until PluginData/native/*.bin load passes.";
        }

        static bool NeedsKey(string id)
        {
            return id == "gemini" || id == "groq" || id == "openrouter" || id == "huggingface" || id == "custom"
                || id == "claude" || id == "grokbot"
                || (id == "chatgpt" && ChatWindow.ChatGptMode == "api");
        }

        // API key field (password-style) + Save/Clear + status, and URL/model for Custom. The key is sent once to the
        // local bridge (127.0.0.1) and the field is emptied; the bridge only ever shows it masked (••••last4).
        void KeyBox(string id)
        {
            if (keyFor != id) { keyFor = id; keyInput = ""; keyMsg = ""; clearArmed = false; }
            if (Time.realtimeSinceStartup > nextKeysPoll) { nextKeysPoll = Time.realtimeSinceStartup + 5f; PollKeys(); }
            string body = keysBody;
            if (body != null)
            {
                keysBody = null;
                foreach (string line in body.Split('\n'))
                {
                    int t = line.IndexOf('\t');
                    if (t > 0) keyInfo[line.Substring(0, t)] = line.Substring(t + 1);
                }
                string v;
                if (cuUrlT == null && keyInfo.TryGetValue("custom_url", out v)) cuUrlT = v;
                if (cuModelT == null && keyInfo.TryGetValue("custom_model", out v)) cuModelT = v;
            }
            Event e = Event.current;
            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                         && (GUI.GetNameOfFocusedControl() ?? "").StartsWith("aics_");
            GUILayout.BeginHorizontal();
            GUILayout.Label(id == "huggingface" ? "Token" : "API key", small, GUILayout.Width(70));
            GUI.SetNextControlName("aics_key");
            keyInput = GUILayout.PasswordField(keyInput, '•', 400);
            if (GUILayout.Button("Paste", GUILayout.Width(50))) keyInput = (GUIUtility.systemCopyBuffer ?? "").Trim();
            GUILayout.EndHorizontal();
            if (id == "custom")
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Base URL", small, GUILayout.Width(70));
                GUI.SetNextControlName("aics_url");
                cuUrlT = GUILayout.TextField(cuUrlT ?? "", 300);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Model", small, GUILayout.Width(70));
                GUI.SetNextControlName("aics_model");
                cuModelT = GUILayout.TextField(cuModelT ?? "", 200);
                GUILayout.EndHorizontal();
            }
            string info;
            keyInfo.TryGetValue(id, out info);
            string[] st = (info ?? "").Split('\t');
            string masked = st.Length > 1 ? st[1] : "";
            bool keysLocal = BridgeLauncher.NativeChatEnabled;
            string status;
            if (!bridgeOk && !keysLocal) status = bridgeChecked ? "bridge not responding" : "checking...";
            else if (info == null) status = "checking...";
            else if (id == "custom") status = st[0] == "1" ? "configured (key " + (masked.Length > 0 ? masked : "none - fine if the API needs none") + ")" : "missing URL and/or model";
            else status = masked.Length > 0 ? "configured (" + masked + ")" : "missing - paste a key and Save";
            GUILayout.Label("Status: " + status, status.StartsWith("configured") ? tagW : (status.StartsWith("missing") ? warnStyle : small));
            GUILayout.BeginHorizontal();
            bool changed = keyInput.Trim().Length > 0 || (id == "custom" &&
                ((cuUrlT != null && cuUrlT != Val("custom_url")) || (cuModelT != null && cuModelT != Val("custom_model"))));
            GUI.enabled = changed && (bridgeOk || keysLocal);
            if (GUILayout.Button("Save") || (enter && GUI.enabled))
            {
                string trimmedKey = keyInput.Trim();
                string json = "{\"backend\":" + ChatWindow.JsonStr(id) + ",\"key\":" + ChatWindow.JsonStr(trimmedKey)
                    + (id == "custom" && cuUrlT != null ? ",\"url\":" + ChatWindow.JsonStr(cuUrlT) : "")
                    + (id == "custom" && cuModelT != null ? ",\"model\":" + ChatWindow.JsonStr(cuModelT) : "") + "}";
                keyInput = ""; clearArmed = false; keyMsg = "Saving...";
                KeyPost(json, id, trimmedKey, cuUrlT, cuModelT, false);
                if (enter) e.Use();
            }
            GUI.enabled = (bridgeOk || keysLocal) && masked.Length > 0;
            if (GUILayout.Button(clearArmed ? "Really clear?" : "Clear key"))
            {
                if (!clearArmed) clearArmed = true;
                else { clearArmed = false; keyMsg = "Clearing..."; KeyPost("{\"backend\":" + ChatWindow.JsonStr(id) + ",\"clear\":true}", id, "", cuUrlT, cuModelT, true); }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            string msg = keyMsg;
            if (msg.Length > 0) GUILayout.Label(msg, small);
            GUILayout.Label("Saved to PluginData/.env; in-mod chat reads it immediately; bridge picks up on restart.", small);
        }

        static string Val(string k) { string v; return keyInfo.TryGetValue(k, out v) ? v : ""; }

        static void PollKeys()
        {
            if (Interlocked.CompareExchange(ref keysPolling, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8765/api_keys");
                    req.Timeout = 3000; req.Proxy = null;
                    using (var resp = req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        keysBody = rd.ReadToEnd();
                }
                catch (Exception)
                {
                    try { keysBody = SecretsStore.StatusText(); }
                    catch (Exception) { }
                }
                finally { Interlocked.Exchange(ref keysPolling, 0); }
            });
        }

        static void KeyPost(string json, string backend, string key, string customUrl, string customModel, bool clear)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { keyMsg = SecretsStore.SaveBackend(backend, key, customUrl, customModel, clear); }
                catch (Exception ex) { keyMsg = ".env: " + ex.Message; }
                if (bridgeOk)
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8765/api_key");
                        req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 30000; req.Proxy = null;
                        byte[] b = Encoding.UTF8.GetBytes(json);
                        req.ContentLength = b.Length;
                        using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                        using (var resp = req.GetResponse())
                        using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            keyMsg = rd.ReadToEnd().Trim();
                    }
                    catch (WebException ex) when (ex.Response != null)
                    {
                        using (var rd = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                            keyMsg = keyMsg + " Bridge: " + rd.ReadToEnd().Trim();
                    }
                    catch (Exception ex) { keyMsg = keyMsg + " Bridge unreachable: " + ex.Message; }
                }
                cuUrlT = cuModelT = null;
                nextKeysPoll = 0; nextBridgeCheck = 0;
            });
        }

        // Readiness note per backend in the dropdown (keyed APIs from /health "backends_ready"; unknown on an old bridge).
        static string BackendHint(int i)
        {
            string id = ChatWindow.BackendId(i), ready = ChatWindow.BackendsReady;
            if (id == "local" || id == "ollama") return "(local, no key)";
            if (id == "chatgpt") return ChatWindow.ChatGptMode == "api" ? (ready == null ? "(API)" : ("," + ready + ",").Contains(",chatgpt,") ? "(API, key set)" : "(API, no key!)") : "(desktop MCP, heavy token use)";
            string free = id == "gemini" || id == "groq" || id == "openrouter" ? "free tier, " : "";
            if (ready == null) return free.Length > 0 ? "(free tier, needs key)" : "";
            return "(" + free + (("," + ready + ",").Contains("," + id + ",") ? "configured)" : "not configured)");
        }

        // ---------- helpers ----------
        void Hold(ref bool on, string label, ref string val, string unit)
        {
            GUILayout.BeginHorizontal();
            on = GUILayout.Toggle(on, label, GUILayout.Width(130));
            GUI.SetNextControlName("aics_hold_" + label);
            val = GUILayout.TextField(val, 7, GUILayout.Width(60));
            GUILayout.Label(unit, small, GUILayout.Width(30));
            GUILayout.EndHorizontal();
        }

        void Stub(string what, params string[] buttons)
        {
            GUILayout.Label(what, small);
            GUILayout.BeginHorizontal();
            foreach (string b in buttons) if (GUILayout.Button(b)) Queued(b);
            GUILayout.EndHorizontal();
        }

        static void Queued(string what) { ChatWindow.Notice("AICS: '" + what + "' is a stub for now (queued in docs/AICS_MENU.md)."); }

        void LandingLine()
        {
            string l = landing;
            GUILayout.Label(l.Length > 0 ? l : "(no active landing / ETA)", small);
        }

        static void PollLanding()
        {
            if (!BridgeLauncher.AiEnabled) return;
            if (!HighLogic.LoadedSceneIsFlight || Interlocked.CompareExchange(ref polling, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8765/landing?format=text");
                    req.Timeout = 3000; req.Proxy = null;
                    using (var resp = req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) landing = rd.ReadToEnd().Trim();
                }
                catch (Exception) { landing = ""; }
                finally { Interlocked.Exchange(ref polling, 0); }
            });
        }

        static void SamplePower()
        {
            Vessel v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (v == null) { ecPrevAmt = -1; return; }
            v.GetConnectedResourceTotals(PartResourceLibrary.ElectricityHashcode, out ecAmt, out ecMax);
            double t = Planetarium.GetUniversalTime();
            if (ecPrevAmt >= 0 && t > ecPrevT + 1e-3)
                ecRate = 0.7 * ecRate + 0.3 * (ecAmt - ecPrevAmt) / (t - ecPrevT);
            ecPrevAmt = ecAmt; ecPrevT = t;
        }

        static string Dur(double s)
        {
            if (double.IsInfinity(s) || double.IsNaN(s)) return "-";
            if (s < 120) return s.ToString("F0", CultureInfo.InvariantCulture) + " s";
            if (s < 7200) return (s / 60).ToString("F0", CultureInfo.InvariantCulture) + " min";
            return (s / 3600).ToString("F1", CultureInfo.InvariantCulture) + " h";
        }

        static readonly Dictionary<Color, Texture2D> texCache = new Dictionary<Color, Texture2D>();
        static Texture2D Tex(Color c)
        {
            Texture2D t;
            if (texCache.TryGetValue(c, out t) && t != null) return t;
            t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); texCache[c] = t;
            return t;
        }

        static void SetText(GUIStyle s, Color c)
        {
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = c;
            s.onNormal.textColor = s.onHover.textColor = s.onActive.textColor = s.onFocused.textColor = c;
        }

        internal static GUISkin EnsureSkin()
        {
            if (skin == null || Mathf.Abs(skinOpacity - opacity) > 0.01f) BuildSkin();
            return skin;
        }

        static void BuildSkin()
        {
            // MechJeb-like: dark grey, light text, green accents, compact.
            skinOpacity = opacity;
            if (skin != null) Destroy(skin);
            skin = Instantiate(GUI.skin);
            Color bg = new Color(0.11f, 0.12f, 0.13f, opacity), bg2 = new Color(0.18f, 0.19f, 0.21f, 1f), hi = new Color(0.25f, 0.27f, 0.3f, 1f);
            Color txt = new Color(0.86f, 0.88f, 0.9f);
            skin.window.normal.background = skin.window.onNormal.background = Tex(bg);
            skin.window.normal.textColor = skin.window.onNormal.textColor = new Color(0.55f, 0.9f, 0.55f);
            skin.window.fontStyle = FontStyle.Bold; skin.window.padding = new RectOffset(6, 6, 20, 6);
            skin.box.normal.background = Tex(new Color(0.07f, 0.08f, 0.09f, Mathf.Min(1f, opacity * 0.95f)));
            // Light text in EVERY style state. Previously focused/onFocused/onActive were left at the base skin's colours, so an
            // edited TextArea/TextField (Flight Plan, Orbit Plan, Settings, ...) drew dark text on our dark field background.
            foreach (GUIStyle s in new[] { skin.button, skin.toggle, skin.textField, skin.textArea, skin.label })
            { s.fontSize = 12; SetText(s, txt); }
            SetText(skin.box, txt);
            Color fieldTxt = new Color(0.96f, 0.97f, 0.98f);   // editable text: near-white
            SetText(skin.textField, fieldTxt); SetText(skin.textArea, fieldTxt);
            skin.button.normal.background = Tex(bg2); skin.button.hover.background = Tex(hi); skin.button.active.background = Tex(new Color(0.2f, 0.45f, 0.25f));
            // Dark field background in every state too (a light base-skin hover/onFocused background would hide the light text).
            Texture2D fieldBg = Tex(new Color(0.05f, 0.05f, 0.06f)), fieldFocus = Tex(new Color(0.08f, 0.1f, 0.08f));
            foreach (GUIStyle s in new[] { skin.textField, skin.textArea })
            {
                s.normal.background = s.hover.background = s.onNormal.background = s.onHover.background = fieldBg;
                s.focused.background = s.active.background = s.onFocused.background = s.onActive.background = fieldFocus;
            }
            skin.settings.cursorColor = new Color(0.6f, 1f, 0.6f);
            skin.settings.selectionColor = new Color(0.25f, 0.55f, 0.3f, 0.7f);
            skin.toggle.onNormal.textColor = new Color(0.55f, 0.95f, 0.55f);
            hdr = new GUIStyle(skin.button) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            small = new GUIStyle(skin.label) { fontSize = 11, wordWrap = true };
            small.normal.textColor = new Color(0.7f, 0.72f, 0.75f);
            green = new GUIStyle(skin.label) { fontStyle = FontStyle.Bold }; green.normal.textColor = new Color(0.4f, 0.95f, 0.4f);
            grey = new GUIStyle(skin.label); grey.normal.textColor = new Color(0.6f, 0.6f, 0.62f);
            tagW = new GUIStyle(small) { fontStyle = FontStyle.Bold }; tagW.normal.textColor = new Color(0.4f, 0.95f, 0.4f);
            tagP = new GUIStyle(tagW); tagP.normal.textColor = new Color(0.95f, 0.8f, 0.3f);
            tagS = new GUIStyle(tagW); tagS.normal.textColor = new Color(0.55f, 0.55f, 0.6f);
            tabStyle = new GUIStyle(skin.box) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 12 };
            tabStyle.normal.background = Tex(new Color(0.11f, 0.12f, 0.13f, Mathf.Max(opacity, 0.6f)));
            tabStyle.normal.textColor = new Color(0.55f, 0.95f, 0.55f);
            warnStyle = new GUIStyle(small) { fontStyle = FontStyle.Bold };
            warnStyle.normal.textColor = new Color(1f, 0.6f, 0.2f);
            tabWarn = new GUIStyle(tabStyle);
            tabWarn.normal.textColor = new Color(1f, 0.6f, 0.2f);
            itemOff = new GUIStyle(skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = 12 };
            itemOn = new GUIStyle(itemOff);
            itemOn.normal.background = itemOn.hover.background = Tex(new Color(0.2f, 0.42f, 0.24f, 1f));
            itemOn.onNormal.background = itemOn.onHover.background = itemOn.onActive.background = itemOn.normal.background;
            SetText(itemOn, Color.white);
        }

        static void Load()
        {
            try
            {
                if (!File.Exists(cfgFile)) return;
                foreach (string line in File.ReadAllLines(cfgFile))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    float f; float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                    switch (kv[0].Trim())
                    {
                        case "x": menuRect.x = f; break;
                        case "y": menuRect.y = f; break;
                        case "w": menuRect.width = Mathf.Max(300, f); break;
                        case "visible": Expanded = kv[1].Trim() == "1"; break;
                        case "tab": showTab = kv[1].Trim() != "0"; break;
                        case "tabx": tabX = f; break;
                        case "open":
                            for (int i = 0; i < Items.Length && i < kv[1].Trim().Length; i++) Open[i] = kv[1].Trim()[i] == '1';
                            break;
                        case "preferMj": preferMj = kv[1].Trim() != "0"; break;
                        case "opacity": opacity = Mathf.Clamp(f, 0.2f, 1f); break;

                    }
                }
            }
            catch (Exception ex) { Debug.Log("[KSPChatBridge] aics load: " + ex.Message); }
        }

        static void Save()
        {
            try
            {
                if (cfgFile == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(cfgFile));
                File.WriteAllText(cfgFile, string.Format(CultureInfo.InvariantCulture,
                    "x={0}\ny={1}\nw={2}\nvisible={3}\npreferMj={4}\nopacity={5}\ntab={6}\ntabx={7}\nopen={8}\n", menuRect.x, menuRect.y,
                    menuRect.width, Expanded ? 1 : 0, preferMj ? 1 : 0, opacity, showTab ? 1 : 0, tabX,
                    new string(Open.Select(o => o ? '1' : '0').ToArray())));
            }
            catch (Exception ex) { Debug.Log("[KSPChatBridge] aics save: " + ex.Message); }
        }
    }
}
