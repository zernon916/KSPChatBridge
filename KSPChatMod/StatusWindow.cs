// KSPChatBridge STATUS and SYSTEMS windows (MechJeb-style info windows), fed by the Python bridge:
//   GET /status?format=text   "key<TAB>value" lines, polled ~1 s: live autopilot state (also drives the AICS menu's
//                             active-mode indicators) - replaces the periodic status line that used to sit in the chat
//   GET /systems?format=text  master caution/warning + one light per system (engines, intakes, control surfaces,
//                             reaction wheels, gear, brakes, chutes, fuel, temperature, G-load, pilot), polled ~1 s in flight
// Toggle both from the AICS menu ("status" / "systems"); visibility and window rects persist in
// GameData/KSPChatBridge/PluginData/status_window.txt. The master warning / caution flashes while an emergency is active
// and not acknowledged - click it to acknowledge (silences the flashing and the MAYDAY light blinking). During an
// unacknowledged warning the craft's Light action group blinks (setting in the Systems window, default on). Existing text colors are untouched; only the new lights have their own colors.
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

        static bool maydayLights = true;
        static bool loaded;
        static string cfgFile;

        // written by the poll threads (whole objects swapped, never mutated), read on the main thread
        static volatile Dictionary<string, string> data = new Dictionary<string, string>();
        static volatile List<string[]> rows = new List<string[]>();
        static volatile List<string[]> sysRows = new List<string[]>();
        static volatile string[] master = { "", "0", "" };   // level, seq, text
        static volatile bool statusOk, systemsOk;
        static int ackSeq;

        static readonly Dictionary<string, string> Labels = new Dictionary<string, string> {
            { "autopilot", "Autopilot" }, { "phase", "Phase" }, { "alt", "Altitude" }, { "speed", "Speed" },
            { "heading", "Heading" }, { "bank", "Bank" }, { "pitch", "Pitch" }, { "throttle", "Throttle" },
            { "orders", "Live orders" }, { "overrides", "Overrides" }, { "pilot", "Pilot" }, { "protect", "Protect" },
            { "eta", "Distance / ETA" }, { "plan", "Flight Plan" }, { "alert", "ALERT" } };

        float nextPoll;
        bool blinking, lightOrig;
        Vessel blinkVessel;
        float nextBlink;

        /// <summary>AICS menu indicators: true if the in-mod controller is in that mode.</summary>
        internal static bool Active(string key)
        {
                string phase = NativeFlightController.Phase;
                return key == "ind_holds" ? phase == "hold" || phase == "takeoff" || phase == "spool" || phase == "helicopter"
                    : key == "ind_autoland" ? phase == "landing"
                    : key == "ind_taxi" ? phase == "taxi"
                    : key == "ind_flightplan" && NativeFlightController.PlanRunning;
        }

        internal static void ToggleStatus() { AicsMenu.ShowMfdItem("status"); }
        internal static void ToggleSystems() { AicsMenu.ShowMfdItem("systems"); }

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
            PollStatus();
            if (HighLogic.LoadedSceneIsFlight)
            {
                sysRows = LocalVesselState.Systems;
                master = new[] { LocalVesselState.Level, LocalVesselState.AlarmSequence.ToString(), LocalVesselState.Alarm };
                systemsOk = LocalVesselState.Available;
            }
            else if (master[0] != "") master = new[] { "", "0", "" };
        }


        static void PollStatus()
        { rows = DashboardRows.Local(NativeFlightController.Phase, NativeFlightController.PlanStatus); statusOk = true; }


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
            bool want = maydayLights && v != null && master[0] == "warning" && Unacked();
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
                    NativeFlightController.SetGroup(v, KSPActionGroup.Light, !v.ActionGroups[KSPActionGroup.Light]);
                }
            }
            else RestoreLights();
        }

        void RestoreLights()
        {
            if (!blinking) return;
            blinking = false;
            try { if (blinkVessel != null) NativeFlightController.SetGroup(blinkVessel, KSPActionGroup.Light, lightOrig); }
            catch (Exception) { }
            blinkVessel = null;
        }

        // ---------------------------------------------------------------- drawing
        // ---------------------------------------------------------------- MFD pages (SYS > MSTR ALARM / SYSTEMS / STATUS; no pop-up windows)
        internal static string AlarmLevel { get { return master[0]; } }
        internal static string AlarmText { get { return master[2]; } }
        internal static bool AlarmUnacked { get { return Unacked(); } }
        internal static void Ack() { if (master[0] != "") ackSeq = Seq(); }
        internal static bool MaydayLights { get { return maydayLights; } set { maydayLights = value; Save(); } }
        internal static bool RotorsTab;
        internal static List<string> AlarmLines()
        {
            var o = new List<string>(); string l = master[0];
            o.Add(l == "warning" ? "MASTER WARNING" : l == "caution" ? "MASTER CAUTION" : "NO ALARM");
            if (l != "") { o.Add(master[2]); o.Add(Unacked() ? "ACK: acknowledge" : "(acknowledged)"); }
            foreach (var r in sysRows) if (r[0] != "ok") o.Add((r[0] == "fail" ? "FAIL " : "CAUT ") + r[1] + ": " + r[2]);
            o.Add(""); o.Add("MAYDAY lights " + (maydayLights ? "ON" : "OFF") + " (MAYDAY LT)");
            return o;
        }
        /// <summary>[light, name, value] rows: overview or robotic rotors.</summary>
        internal static List<string[]> SystemRows()
        {
            var o = new List<string[]>();
            if (!RotorsTab) { if (!systemsOk) o.Add(new[] { "caution", "Systems", "waiting for vessel state" }); o.AddRange(sysRows); return o; }
            var vessel = FlightGlobals.ActiveVessel;
            if (RotorTelemetry.Rows.Count == 0) o.Add(new[] { "ok", "Rotors", "none on this vessel" });
            foreach (var r in RotorTelemetry.Rows)
                o.Add(new[] { RotorPlacement.Status(r.Rpm, r.Limit, r.Motor, r.Brake, vessel != null && !vessel.LandedOrSplashed), r.Label + " " + r.Direction,
                    (float.IsNaN(r.Rpm) ? "?" : r.Rpm.ToString("F0")) + "/" + r.Limit.ToString("F0") + " RPM  T " + r.Torque.ToString("F0") + "%  B " + r.Brake.ToString("F0") + "%  " + (r.Motor ? "ON" : "OFF") });
            return o;
        }
        internal static List<string[]> StatusRows()
        {
            var o = new List<string[]>(); foreach (string[] kv in LocalVesselState.Flight) o.Add(new[] { kv[0], kv[1] });
            if (statusOk) foreach (string[] kv in rows)
            {
                if (LocalVesselState.Available && (kv[0] == "alt" || kv[0] == "speed" || kv[0] == "throttle" || kv[0] == "pilot")) continue;
                string label; if (!Labels.TryGetValue(kv[0], out label)) label = kv[0]; o.Add(new[] { label, kv[1] });
            }
            return o;
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
                        case "lights": maydayLights = kv[1].Trim() != "0"; break;
                    }
                }
            }
            catch (Exception ex) { Debug.Log("[KSPChatBridge] status window load: " + ex.Message); }
        }

        static void Save()
        {
            try
            {
                if (cfgFile == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(cfgFile));
                File.WriteAllText(cfgFile, "lights=" + (maydayLights ? 1 : 0) + "\n");
            }
            catch (Exception ex) { Debug.Log("[KSPChatBridge] status window save: " + ex.Message); }
        }
    }
}
