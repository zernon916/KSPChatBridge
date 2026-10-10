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
                vessel.verticalSpeed, vessel.ActionGroups[KSPActionGroup.Brakes], vessel.ActionGroups[KSPActionGroup.Gear], ap,
                TelemetryPos.Part(vessel.latitude, vessel.longitude, runway == null ? double.NaN : NavigationMath.Distance(vessel.latitude, vessel.longitude, runway.Lat, runway.Lon, vessel.mainBody.Radius) / 1000,
                    runway == null ? 0 : NavigationMath.Bearing(vessel.latitude, vessel.longitude, runway.Lat, runway.Lon)) + " g=" + vessel.geeForce.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " name=\"" + vessel.vesselName + "\" m=" + vessel.GetTotalMass().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "t");
        }

        static string NearestBuiltIn(double lat, double lon)
        {
            double ksc = NavigationMath.Distance(lat, lon, -.0494058, -74.6073375, 600000), isl = NavigationMath.Distance(lat, lon, -1.5154, -71.9093, 600000);
            return isl < ksc ? "island" : "ksc";
        }

        void EngineWatchTick(float now, bool flying)
        {
            foreach (Part p in vessel.parts)
                foreach (var e in p.FindModulesImplementing<ModuleEngines>())
                {
                    bool running = e.EngineIgnited && !e.flameout, was;
                    engineRunning.TryGetValue(e, out was);
                    string act = PilotPolicy.EngineWatch(was, running, e.flameout, flying, now - EnginesCommandedAt < 5, e.allowRestart);
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
                    if (text.Trim().Length == 0 && PilotPolicy.IsFollowPlan(Str(a, "request", ""))) return Command("flightplan/follow", new Dictionary<string, object>());   // never replace the plan on "follow the plan"
                    var notes = new List<string>();
                    if (text.Trim().Length == 0)
                    {
                        List<string> unsure;
                        if (!PilotPolicy.TryPlan(Str(a, "request", ""), vessel.LandedOrSplashed, out text, out unsure, out notes))
                            return "Not flying that yet - I'm not sure about: \"" + string.Join("\", \"", unsure.ToArray()) + "\". Can you say those steps another way? (I understood: " + text.Replace("\n", " / ") + ")";
                    }
                    else
                    {   // model-written plan: prefer Luke's own words when they parse; else auto-correct line by line to the runner syntax
                        string req = Str(a, "request", ""), fromReq; List<string> u2, n2;
                        Func<string, bool> ok = l => { try { ParsePlan(l); return true; } catch (Exception) { return false; } };
                        if (req.Trim().Length > 0 && req.Trim() != text.Trim() && PilotPolicy.TryPlan(req, vessel.LandedOrSplashed, out fromReq, out u2, out n2) && ok(fromReq)) { text = fromReq; notes.AddRange(n2); }
                        else { string badStep; string fixd = PilotPolicy.FixPlan(text, ok, out badStep); if (badStep != null) return "Plan not valid: step \"" + badStep + "\" is not plan syntax. Use: " + NativePlan.Syntax; if (fixd != text) notes.Add("plan steps corrected to the runner syntax"); text = fixd; }
                    }
                    try { ParsePlan(text); } catch (ArgumentException ex) { return "Plan not valid: " + ex.Message; }   // the bad step only, no plan dump
                    AicsMenu.LoadPlan(text);
                    string note = notes.Count > 0 ? "\nNotes: " + string.Join("; ", notes.ToArray()) : "";
                    if (!Bool(a, "fly", true)) return "Plan written (in the Flight Plan editor):\n" + text + note;
                    return Command("flightplan/fly", Args("plan", text)) + "\n" + text + note;
                }
            }
            return null;
        }

        /// <summary>Landing panel rows (name, mode, lat, lon, builtin, runways) without the bridge.</summary>
        /// <summary>Autopilot window summary: job, plan step, targets, bank limit.</summary>
        internal static string ApSummary()
        {
            var f = instance; if (f == null || f.vessel == null) return "Job: none (no vessel)";
            string job = f.mode + (f.mode == "landing" && f.runway != null ? " (" + f.runway.Phase + ", " + (f.runway.Distance / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km)" : "");
            string step = f.plan != null && f.plan.Running && f.plan.Index < f.plan.Steps.Count ? "step " + (f.plan.Index + 1) + "/" + f.plan.Steps.Count + ": " + f.plan.Steps[f.plan.Index].Text : "no plan running";
            double lim = f.mode == "landing" && f.runway != null && f.runway.BankDeg > 20 ? f.runway.BankDeg : f.bankOverride ? 180 : FlightPolicy.BankLimit(f.vessel.srfSpeed);
            return PilotPolicy.ApLine(job, step, f.altitude, f.speed, f.heading, lim, Active);
        }

        internal static List<string[]> SpotRows()
        {
            var rows = new List<string[]>
            {
                new[] { "KSC Runway", "H", "-0.0486", "-74.7244", "1", "09/27" },
                new[] { "Island Runway", "H", "-1.5161", "-71.8567", "1", "09/27" },
            };
            if (instance == null || instance.vessel == null) return rows;
            foreach (var p in instance.spots.Points(instance.vessel.mainBody.bodyName))
                { if (FlightResidualPolicy.DuplicatesBuiltIn(p.Lat, p.Lon)) continue; string md = "V"; try { var row = instance.spots.Find(p.Name, instance.vessel.mainBody.bodyName); object mv; if (row != null && row.TryGetValue("mode", out mv) && mv != null) md = mv.ToString(); } catch (ArgumentException) { }
                rows.Add(new[] { p.Name, md, p.Lat.ToString("0.0000", CultureInfo.InvariantCulture), p.Lon.ToString("0.0000", CultureInfo.InvariantCulture), "0", "" }); }
            return rows;
        }
    }

}
