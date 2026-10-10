using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    // Live test round 2: roll, autopilot on/off, plans from chat, engine watch, telemetry line, built-in runway choice, native spots list.
    public partial class NativeFlightController
    {
        readonly Dictionary<ModuleEngines, bool> engineRunning = new Dictionary<ModuleEngines, bool>();
        readonly List<KeyValuePair<ModuleEngines, float>> relight = new List<KeyValuePair<ModuleEngines, float>>();
        readonly System.Random fumbleRng = new System.Random();
        internal static float EnginesCommandedAt = -100;

        internal string Telemetry()
        {
            if (vessel == null) return "no vessel";
            string ap = mode + (patternOn ? "/pattern" : "") + (directBank.HasValue ? "/bank" : "") + (plan != null && plan.Running ? "/plan" + (plan.Index + 1) : "");
            return ChatTelemetry.Line(vessel.altitude, vessel.radarAltitude, vessel.srfSpeed, FlightGlobals.ship_heading, Roll(), Pitch(), vessel.ctrlState.mainThrottle,
                vessel.verticalSpeed, vessel.ActionGroups[KSPActionGroup.Brakes], vessel.ActionGroups[KSPActionGroup.Gear], ap);
        }

        static string NearestBuiltIn(double lat, double lon)
        {
            double ksc = NavigationMath.Distance(lat, lon, -.0494058, -74.6073375, 600000), isl = NavigationMath.Distance(lat, lon, -1.5154, -71.9093, 600000);
            return isl < ksc ? "island" : "ksc";
        }

        void EngineWatchTick(float now, bool flying)
        {
            bool owns = OwnsControls;
            foreach (Part p in vessel.parts)
                foreach (var e in p.FindModulesImplementing<ModuleEngines>())
                {
                    bool running = e.EngineIgnited && !e.flameout, was;
                    engineRunning.TryGetValue(e, out was);
                    string act = PilotPolicy.EngineWatch(was, running, e.flameout, flying && owns, now - EnginesCommandedAt < 5, e.allowRestart);
                    engineRunning[e] = running;
                    if (act != "restart") continue;
                    string title = e.part.partInfo != null ? e.part.partInfo.title : "engine";
                    CrewEmergency("flameout", title);
                    if (!e.EngineIgnited) { relight.Add(new KeyValuePair<ModuleEngines, float>(e, now + (float)PilotPolicy.FumbleSeconds(fumbleRng))); ChatLog.Write("engine", title + " shut down in flight; relight queued"); }
                    else ChatLog.Write("engine", title + " flameout (fuel/air?)");
                }
            for (int i = relight.Count - 1; i >= 0; i--)
            {
                if (now < relight[i].Value) continue;
                var e = relight[i].Key; relight.RemoveAt(i);
                if (e == null || e.vessel != vessel || e.EngineIgnited) continue;
                e.Activate();
                ChatWindow.Notice(Voice().Key + ": Got it - relit the " + (e.part.partInfo != null ? e.part.partInfo.title : "engine") + "!");
            }
        }

        string PilotCommand(string name, Dictionary<string, object> a)
        {
            switch (name)
            {
                case "roll":
                {
                    if (vessel.LandedOrSplashed) return "Roll is for flight.";
                    if (Bool(a, "level", false)) { directBank = null; bankOverride = false; holdHeading = true; heading = FlightGlobals.ship_heading; return "Wings level, holding heading " + heading.ToString("0", Inv) + "."; }
                    if (mode == "idle") { string r = Command("plane_hold", Args("altitude_m", vessel.altitude, "altitude_ref", "msl", "heading", FlightGlobals.ship_heading, "speed", -1)); if (mode == "idle") return r; }
                    bool ov; double t = PilotPolicy.RollTarget(Str(a, "direction", "left"), Num(a, "degrees", 15), Bool(a, "inverted", false), false, Bool(a, "override", false), vessel.srfSpeed, out ov);
                    flying2 = false; patternOn = false; directBank = t; bankOverride = ov;
                    if (Math.Abs(t) >= 179) return "Rolling inverted (explicit override). Say roll level to come back.";
                    return "Banking " + (t > 0 ? "right " : "left ") + Math.Abs(t).ToString("0", Inv) + " deg" + (ov ? " (override)." : Math.Abs(t) < Num(a, "degrees", 15) ? " (capped by the bank rule; say override to go further)." : ".");
                }
                case "autopilot":
                    if (!Bool(a, "on", true)) { if (plan != null) plan.Pause(); Stop(); return "Autopilot off; you have control."; }
                    if (vessel.LandedOrSplashed) return "On the ground: say takeoff.";
                    return Command("plane_hold", Args("altitude_m", vessel.altitude, "altitude_ref", "msl", "heading", FlightGlobals.ship_heading, "speed", vessel.srfSpeed)) + " Holding " + vessel.altitude.ToString("0", Inv) + " m, heading " + FlightGlobals.ship_heading.ToString("0", Inv) + ".";
                case "make_flight_plan":
                {
                    string text = Str(a, "plan", "");
                    if (text.Trim().Length == 0)
                    {
                        try { text = PilotPolicy.PlanFromText(Str(a, "request", ""), vessel.LandedOrSplashed); }
                        catch (ArgumentException ex) { return ex.Message; }
                    }
                    try { ParsePlan(text); } catch (ArgumentException ex) { return "Plan not valid: " + ex.Message + "\n" + text; }
                    AicsMenu.LoadPlan(text);
                    if (!Bool(a, "fly", true)) return "Plan written (in the Flight Plan editor):\n" + text;
                    return Command("flightplan/fly", Args("plan", text)) + "\n" + text;
                }
            }
            return null;
        }

        /// <summary>Landing panel rows (name, mode, lat, lon, builtin, runways) without the bridge.</summary>
        internal static List<string[]> SpotRows()
        {
            var rows = new List<string[]>
            {
                new[] { "KSC Runway", "H", "-0.0486", "-74.7244", "1", "09/27" },
                new[] { "Island Runway", "H", "-1.5161", "-71.8567", "1", "09/27" },
            };
            if (instance == null || instance.vessel == null) return rows;
            foreach (var p in instance.spots.Points(instance.vessel.mainBody.bodyName))
                { string md = "V"; try { var row = instance.spots.Find(p.Name, instance.vessel.mainBody.bodyName); object mv; if (row != null && row.TryGetValue("mode", out mv) && mv != null) md = mv.ToString(); } catch (ArgumentException) { }
                rows.Add(new[] { p.Name, md, p.Lat.ToString("0.0000", CultureInfo.InvariantCulture), p.Lon.ToString("0.0000", CultureInfo.InvariantCulture), "0", "" }); }
            return rows;
        }
    }

}
