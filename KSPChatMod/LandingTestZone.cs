using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KSPChatBridge
{
    /// <summary>GROK TEST1: state shared between the main-menu picker and the in-flight placer (survives the scene change; static).</summary>
    internal static class LandingTestRun
    {
        internal const string SaveName = "KSPChatBridge_LandingTest";
        internal static string CraftPath, CraftName; internal static TestPreset Preset; internal static int PresetIndex, Run;
        internal static bool PlacePending;
        internal static List<TestPreset> Presets;
        internal static string PluginData { get { return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), "KSPChatBridge"), "PluginData"); } }
        internal static List<TestPreset> Load()
        {
            string f = Path.Combine(PluginData, "landing_tests.json"), why = "";
            try
            {
                if (File.Exists(f)) { var l = LandingTestPresets.Parse(File.ReadAllText(f), out why); if (why.Length > 0) Debug.LogWarning("[KSPChatBridge] landing_tests.json: " + why); if (l != null) return Presets = l; }
                else { Directory.CreateDirectory(PluginData); File.WriteAllText(f, LandingTestPresets.ToJson(LandingTestPresets.Defaults())); }   // seed an editable copy
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] landing tests: " + ex.Message); }
            return Presets = LandingTestPresets.Defaults();
        }
        /// <summary>Temporary sandbox (created once, reused; never touches other saves) then a normal runway launch.</summary>
        internal static void Launch()
        {
            string root = Path.Combine(KSPUtil.ApplicationRootPath, "saves"), dir = Path.Combine(root, SaveName);
            Game g = null;
            if (File.Exists(Path.Combine(dir, "persistent.sfs"))) g = GamePersistence.LoadGame("persistent", SaveName, true, false);
            if (g == null) g = GamePersistence.CreateNewGame(SaveName, Game.Modes.SANDBOX, new GameParameters(), "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.SPH);
            HighLogic.CurrentGame = g; HighLogic.SaveFolder = SaveName;
            GamePersistence.SaveGame(g, "persistent", SaveName, SaveMode.OVERWRITE);
            string ships = Path.Combine(Path.Combine(dir, "Ships"), "SPH"); Directory.CreateDirectory(ships);
            string local = Path.Combine(ships, Path.GetFileName(CraftPath));
            if (!string.Equals(Path.GetFullPath(local), Path.GetFullPath(CraftPath), StringComparison.OrdinalIgnoreCase)) File.Copy(CraftPath, local, true);
            var node = ConfigNode.Load(local);
            var manifest = g.CrewRoster.DefaultCrewForVessel(node, VesselCrewManifest.FromConfigNode(node));
            Run++; PlacePending = true; LaunchPending = local;
            ChatLog.Write("TEST", LandingTestPresets.RunLine(Run, Preset, CraftName));
            // GROK TEST1b: launching straight from the main menu NREs in FlightDriver.setStartupNewVessel ('Cannot find a transform named
            // Facility/End09/SpawnPoint on Runway' - the KSC facilities are not built yet) and KSP falls back to the Space Center.
            // Start the game into the Space Center first (stock flow); LandingTestKsc launches once the KSC scene is up.
            g.startScene = GameScenes.SPACECENTER; g.Start();
        }
        internal static string LaunchPending;
        static void ScanDir(string dir, List<string> into) { try { if (Directory.Exists(dir)) into.AddRange(Directory.GetFiles(dir, "*.craft")); } catch { } }
        /// <summary>Every SPH / VAB craft in every save + stock SPH, sorted by name (menu picker and the in-flight Menu picker).</summary>
        internal static List<string> ScanCrafts()
        {
            var crafts = new List<string>(); string root = KSPUtil.ApplicationRootPath;
            try { foreach (string save in Directory.GetDirectories(Path.Combine(root, "saves"))) { ScanDir(Path.Combine(Path.Combine(save, "Ships"), "SPH"), crafts); ScanDir(Path.Combine(Path.Combine(save, "Ships"), "VAB"), crafts); } } catch { }
            ScanDir(Path.Combine(Path.Combine(root, "Ships"), "SPH"), crafts);
            crafts.Sort((a, b) => string.Compare(Path.GetFileNameWithoutExtension(a), Path.GetFileNameWithoutExtension(b), StringComparison.OrdinalIgnoreCase));
            return crafts;
        }
        internal static void LaunchNow()
        {
            string local = LaunchPending; LaunchPending = null; var g = HighLogic.CurrentGame;
            var node = ConfigNode.Load(local);
            var manifest = g.CrewRoster.DefaultCrewForVessel(node, VesselCrewManifest.FromConfigNode(node));
            ChatLog.Write("TEST", "run " + Run + " launching from the Space Center: " + Path.GetFileName(local));
            FlightDriver.StartWithNewLaunch(local, g.flagURL, "Runway", manifest);
        }
    }

    /// <summary>GROK TEST1b: once the test sandbox's Space Center scene is up, launch the picked craft onto the runway.</summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class LandingTestKsc : MonoBehaviour
    {
        IEnumerator Start()
        {
            if (LandingTestRun.LaunchPending == null || HighLogic.SaveFolder != LandingTestRun.SaveName) yield break;
            for (int i = 0; i < 120; i++) yield return null;   // let the KSC facilities and spawn points finish building
            try { LandingTestRun.LaunchNow(); } catch (Exception ex) { Debug.LogException(ex); ChatLog.Write("TEST", "run " + LandingTestRun.Run + " launch failed: " + ex.Message); }
        }
    }

    /// <summary>GROK TEST1: main-menu 'KSPChatBridge Landing Tests' picker.</summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public class LandingTestMenu : MonoBehaviour
    {
        bool open; Rect win = new Rect(40, 80, 560, 520); Vector2 s1, s2; List<string> crafts = new List<string>(); int craft = -1, preset = 0; string err = "";
        bool leaving;
        void Start() { LandingTestRun.Load(); GameEvents.onGameSceneLoadRequested.Add(OnLeave); }
        void OnDestroy() { GameEvents.onGameSceneLoadRequested.Remove(OnLeave); }
        void OnLeave(GameScenes s) { leaving = true; open = false; }   // GROK TEST1b: hide on the loading screen
        static void Scan(string dir, List<string> into) { try { if (Directory.Exists(dir)) into.AddRange(Directory.GetFiles(dir, "*.craft")); } catch { } }
        void Refresh()
        {
            crafts = LandingTestRun.ScanCrafts();
        }
        void OnGUI()
        {
            if (leaving) return;
            if (!open) { if (GUI.Button(new Rect(20, 20, 230, 30), "KSPChatBridge Landing Tests")) { open = true; Refresh(); LandingTestRun.Load(); } return; }
            win = GUILayout.Window(0x4C54, win, Draw, "KSPChatBridge Landing Tests");
        }
        void Draw(int id)
        {
            string root = Path.Combine(KSPUtil.ApplicationRootPath, "saves");
            GUILayout.Label("Craft (" + crafts.Count + "):");
            s1 = GUILayout.BeginScrollView(s1, GUILayout.Height(200));
            for (int i = 0; i < crafts.Count; i++)
            {
                string label = Path.GetFileNameWithoutExtension(crafts[i]) + "   [" + crafts[i].Replace(KSPUtil.ApplicationRootPath, "").Replace('\\', '/') + "]";
                if (GUILayout.Toggle(craft == i, label)) craft = i;
            }
            GUILayout.EndScrollView();
            GUILayout.Label("Start point (PluginData/landing_tests.json):");
            s2 = GUILayout.BeginScrollView(s2, GUILayout.Height(190));
            var ps = LandingTestRun.Presets;
            for (int i = 0; i < ps.Count; i++) if (GUILayout.Toggle(preset == i, ps[i].Name + "  (" + Math.Round(ps[i].AltM) + " m, " + Math.Round(ps[i].Speed) + " m/s, hdg " + Math.Round(ps[i].Heading) + ")")) preset = i;
            GUILayout.EndScrollView();
            if (err.Length > 0) GUILayout.Label(err);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Fly test") && craft >= 0)
            {
                try { LandingTestRun.CraftPath = crafts[craft]; LandingTestRun.CraftName = Path.GetFileNameWithoutExtension(crafts[craft]); LandingTestRun.PresetIndex = preset; LandingTestRun.Preset = ps[preset]; leaving = true; open = false; LandingTestRun.Launch(); }
                catch (Exception ex) { leaving = false; open = true; err = "Launch failed: " + ex.Message; Debug.LogException(ex); }
            }
            if (GUILayout.Button("Close")) open = false;
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }
    }

    /// <summary>GROK TEST1: in flight - place the launched craft in the air at the preset, engage autoland, log the outcome; RETRY / NEXT / MENU.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class LandingTestFlight : MonoBehaviour
    {
        Rect win = new Rect(Screen.width - 330, 120, 310, 110), pickWin = new Rect(Screen.width / 2 - 280, 100, 560, 500); bool picker; Vector2 ps1, ps2; List<string> pickCrafts = new List<string>(); int pickCraft = -1, pickPreset; string pickErr = "";
        bool placed, wasFlying, done, wasActive, engagedSeen, wasMissed; int interventions; int goArounds; string lastPhase = "", status = ""; double lastVs = double.NaN;
        bool InTest { get { return HighLogic.SaveFolder == LandingTestRun.SaveName && LandingTestRun.Preset != null; } }
        void Start() { if (InTest) { GameEvents.onCrash.Add(OnCrash); GameEvents.onCrashSplashdown.Add(OnCrash); if (LandingTestRun.PlacePending) StartCoroutine(Place()); } }
        void OnDestroy() { GameEvents.onCrash.Remove(OnCrash); GameEvents.onCrashSplashdown.Remove(OnCrash); }
        void OnCrash(EventReport r) { if (placed && !done) End("crash (" + (r != null && r.origin != null ? r.origin.partInfo.title : "part") + ")"); }
        void End(string what)
        {
            done = true; var v = FlightGlobals.ActiveVessel;
            string line = LandingTestPresets.Outcome(LandingTestRun.Run, what + (interventions > 0 ? " (pilot intervened x" + interventions + ")" : ""), lastVs, v != null ? v.srfSpeed : double.NaN, goArounds);
            ChatLog.Write("TEST", line); status = line;
        }
        IEnumerator Place()
        {
            LandingTestRun.PlacePending = false;
            Vessel v = null;
            for (int i = 0; i < 600; i++) { v = FlightGlobals.ActiveVessel; if (v != null && v.loaded && !v.packed && FlightGlobals.ready) break; yield return new WaitForFixedUpdate(); }
            if (v == null) { status = "no vessel"; yield break; }
            for (int i = 0; i < 25; i++) yield return new WaitForFixedUpdate();   // let it settle on the runway
            var p = LandingTestRun.Preset; var body = v.mainBody; double lat, lon, la2, lo2;
            LandingTestPresets.Position(p, out lat, out lon, body.Radius);
            NavigationMath.Offset(lat, lon, p.Heading, 50, body.Radius, out la2, out lo2);
            if (v.currentStage > 0 && v.FindPartModulesImplementing<ModuleEngines>().TrueForAll(e => !e.EngineIgnited)) KSP.UI.Screens.StageManager.ActivateNextStage();
            NativeFlightController.SetGroup(v, KSPActionGroup.Brakes, false);
            v.ctrlState.mainThrottle = FlightInputHandler.state.mainThrottle = .6f;
            for (int k = 0; k < 10; k++)   // hold the state a few physics frames so the ground contact and packing settle
            {
                Vector3d pos = body.GetWorldSurfacePosition(lat, lon, p.AltM), ahead = body.GetWorldSurfacePosition(la2, lo2, p.AltM);
                Vector3d up = (pos - body.position).normalized, nose = (ahead - pos).normalized;
                v.Landed = false; v.Splashed = false; v.landedAt = ""; v.situation = Vessel.Situations.FLYING;
                v.IgnoreGForces(20);
                v.SetPosition(pos, true);
                v.SetRotation(Quaternion.LookRotation(-(Vector3)up, (Vector3)nose));   // plane: transform.up = nose, transform.forward = belly
                v.SetWorldVelocity(nose * p.Speed - Krakensbane.GetFrameVelocity());
                v.angularVelocity = Vector3.zero;
                yield return new WaitForFixedUpdate();
            }
            NativeFlightController.SetGroup(v, KSPActionGroup.Gear, false);
            placed = true; wasFlying = true;
            ChatLog.Write("TEST", "run " + LandingTestRun.Run + " placed at " + lat.ToString("0.0000") + "," + lon.ToString("0.0000") + " alt " + Math.Round(p.AltM) + " m, " + Math.Round(p.Speed) + " m/s, hdg " + Math.Round(p.Heading));
            yield return new WaitForSeconds(1f);
            ChatWindow.ToolFromMenu("land", "{\"name\":\"KSC\",\"runway\":\"" + p.Runway + "\"}");
            status = "run " + LandingTestRun.Run + ": " + p.Name;
        }
        void Update()
        {
            if (!InTest || !placed || done) return;
            var v = FlightGlobals.ActiveVessel; if (v == null) return;
            bool act = NativeFlightController.Active;   // GROK LAND5k: pilot disengage / re-engage = an intervention in the run summary
            if (engagedSeen && wasActive && !act) { interventions++; ChatLog.Write("TEST", "run " + LandingTestRun.Run + " pilot intervened: autopilot disengaged"); }
            if (engagedSeen && !wasActive && act) ChatLog.Write("TEST", "run " + LandingTestRun.Run + " autopilot re-engaged");
            if (act) engagedSeen = true; wasActive = act;
            string ph = NativeFlightController.RunwayPhase ?? "";
            bool missed = NativeFlightController.RunwayMissed; if (ph == "go around" && lastPhase != "go around" || missed && !wasMissed) goArounds++; wasMissed = missed;   // GROK LAND5k: the go-around sets MissedClimb, not a phase
            lastPhase = ph;
            if (!v.LandedOrSplashed) { lastVs = -v.verticalSpeed; wasFlying = true; }
            else if (wasFlying) { wasFlying = false; ChatLog.Write("TEST", "run " + LandingTestRun.Run + " touchdown sink=" + lastVs.ToString("0.0") + " m/s speed=" + v.srfSpeed.ToString("0") + " m/s"); }
            if (v.LandedOrSplashed && v.srfSpeed < 2 && (ph == "stopped" || ph == "")) End("landed");
            if (goArounds >= 4) End("gave up after " + goArounds + " go-arounds");
        }
        void OnGUI()
        {
            if (!InTest) return;
            win = GUILayout.Window(0x4C55, win, id =>
            {
                GUILayout.Label(status.Length > 0 ? status : "placing...");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("RETRY")) Again(LandingTestRun.PresetIndex);
                if (GUILayout.Button("Next preset")) Again((LandingTestRun.PresetIndex + 1) % LandingTestRun.Presets.Count);
                if (GUILayout.Button("Menu")) { picker = !picker; if (picker) OpenPicker(); }   // TEST2: plane + start point picker in flight (Main menu inside)
                GUILayout.EndHorizontal(); GUI.DragWindow();
            }, "Landing test");
            if (picker) pickWin = GUILayout.Window(0x4C56, pickWin, DrawPicker, "Landing test - plane / start point");
        }
        void OpenPicker()
        {
            pickCrafts = LandingTestRun.ScanCrafts(); pickPreset = LandingTestRun.PresetIndex; pickErr = ""; pickCraft = -1;
            for (int i = 0; i < pickCrafts.Count; i++) if (string.Equals(Path.GetFileNameWithoutExtension(pickCrafts[i]), LandingTestRun.CraftName, StringComparison.OrdinalIgnoreCase)) { pickCraft = i; break; }
        }
        void DrawPicker(int id)
        {
            GUILayout.Label("Plane (" + pickCrafts.Count + ", now " + LandingTestRun.CraftName + "):");
            ps1 = GUILayout.BeginScrollView(ps1, GUILayout.Height(190));
            for (int i = 0; i < pickCrafts.Count; i++) if (GUILayout.Toggle(pickCraft == i, Path.GetFileNameWithoutExtension(pickCrafts[i]) + "   [" + pickCrafts[i].Replace(KSPUtil.ApplicationRootPath, "").Replace('\\', '/') + "]")) pickCraft = i;
            GUILayout.EndScrollView();
            GUILayout.Label("Start point:");
            ps2 = GUILayout.BeginScrollView(ps2, GUILayout.Height(170));
            var ps = LandingTestRun.Presets;
            for (int i = 0; i < ps.Count; i++) if (GUILayout.Toggle(pickPreset == i, ps[i].Name + "  (" + Math.Round(ps[i].AltM) + " m, " + Math.Round(ps[i].Speed) + " m/s, hdg " + Math.Round(ps[i].Heading) + ")")) pickPreset = i;
            GUILayout.EndScrollView();
            if (pickErr.Length > 0) GUILayout.Label(pickErr);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Fly here") && pickCraft >= 0 && pickPreset >= 0 && pickPreset < ps.Count)
            {
                try
                {
                    string path = pickCrafts[pickCraft]; bool same = string.Equals(Path.GetFileNameWithoutExtension(path), LandingTestRun.CraftName, StringComparison.OrdinalIgnoreCase);
                    if (!same) { LandingTestRun.CraftPath = path; LandingTestRun.CraftName = Path.GetFileNameWithoutExtension(path); ChatLog.Write("TEST", "change plane -> " + LandingTestRun.CraftName); }
                    picker = false; Again(pickPreset, !same);
                }
                catch (Exception ex) { pickErr = "Respawn failed: " + ex.Message; Debug.LogException(ex); }
            }
            if (GUILayout.Button("Close")) picker = false;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Main menu", GUILayout.Width(110))) { picker = false; if (placed && !done) End("aborted"); HighLogic.LoadScene(GameScenes.MAINMENU); }
            GUILayout.EndHorizontal(); GUI.DragWindow();
        }
        void Again(int idx) { Again(idx, false); }
        void Again(int idx, bool newCraft)
        {
            if (placed && !done) End("aborted (retry)");
            try { ChatWindow.ToolFromMenu("plane_hold", "{\"engage\":false}"); } catch { }
            LandingTestRun.PresetIndex = idx; LandingTestRun.Preset = LandingTestRun.Presets[idx]; LandingTestRun.Run++; LandingTestRun.PlacePending = true;
            ChatLog.Write("TEST", LandingTestPresets.RunLine(LandingTestRun.Run, LandingTestRun.Preset, LandingTestRun.CraftName));
            if (FlightDriver.CanRevertToPostInit && !newCraft) FlightDriver.RevertToLaunch();   // TEST2: a new plane = new launch (revert would bring back the old one)
            else { var g = HighLogic.CurrentGame; var node = ConfigNode.Load(LandingTestRun.CraftPath); FlightDriver.StartWithNewLaunch(LandingTestRun.CraftPath, g.flagURL, "Runway", g.CrewRoster.DefaultCrewForVessel(node, VesselCrewManifest.FromConfigNode(node))); }
        }
    }
}
