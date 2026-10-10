using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace KSPChatBridge
{
    internal static class LocalVesselState
    {
        internal static List<string[]> Systems = new List<string[]>();
        internal static List<string[]> Flight = new List<string[]>();
        internal static string Level = "", Alarm = "";
        internal static int AlarmSequence;
        internal static string Json = "{}";
        internal static bool Available;
        static string vesselId = "";
        static string F(double n) { return n.ToString("0.0", CultureInfo.InvariantCulture); }
        static string N(double n) { return double.IsNaN(n) || double.IsInfinity(n) ? "null" : n.ToString("R", CultureInfo.InvariantCulture); }

        internal static void Clear()
        {
            Available = false; Systems = new List<string[]>(); Flight = new List<string[]>();
            Level = Alarm = vesselId = ""; Json = "{}";
        }
        static double PitchDeg(Vessel v) { var t = v.ReferenceTransform; return t == null ? 0 : Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(t.up, v.upAxis)))) * 180 / Math.PI; }
        static double BankDeg(Vessel v) { var t = v.ReferenceTransform; return t == null ? 0 : -Math.Atan2(Vector3d.Dot(t.right, v.upAxis), Vector3d.Dot(-t.forward, v.upAxis)) * 180 / Math.PI; }
        internal static void Sample(Vessel v)
        {
            var rows = new List<string[]>();
            bool flying = !v.LandedOrSplashed;
            var amounts = new Dictionary<string, double>();
            var capacities = new Dictionary<string, double>();
            double hot = 0; string hottest = ""; var engineFuel = new HashSet<string>(); var used = new HashSet<string> { "ElectricCharge" };
            if (vesselId != v.id.ToString()) startFraction.Clear();
            foreach (Part p in v.parts)
            {
                double ratio = Math.Max(p.temperature / Math.Max(1, p.maxTemp), p.skinTemperature / Math.Max(1, p.skinMaxTemp));
                if (ratio > hot) { hot = ratio; hottest = p.partInfo.title; }
                foreach (PartResource r in p.Resources)
                {
                    if (!amounts.ContainsKey(r.resourceName)) { amounts[r.resourceName] = 0; capacities[r.resourceName] = 0; }
                    amounts[r.resourceName] += r.amount; capacities[r.resourceName] += r.maxAmount;
                }
                foreach (PartModule m in p.Modules)
                {
                    var eng = m as ModuleEngines; if (eng != null) foreach (var pr in eng.propellants) { engineFuel.Add(pr.name); used.Add(pr.name); }
                    var rcs = m as ModuleRCS; if (rcs != null) foreach (var pr in rcs.propellants) used.Add(pr.name);
                    var engine = m as ModuleEngines;
                    if (engine != null) rows.Add(new[] { engine.flameout && engine.EngineIgnited ? "fail" : "ok", p.partInfo.title,
                        engine.EngineIgnited ? F(engine.finalThrust) + " kN" + (engine.flameout ? " FLAMEOUT" : "") : "Engine off" });
                    var intake = m as ModuleResourceIntake;
                    if (intake != null) rows.Add(new[] { intake.intakeEnabled ? "ok" : "caution", "Intake " + p.partInfo.title, intake.intakeEnabled ? "Open" : "Closed" });
                    var surface = m as ModuleControlSurface;
                    if (surface != null) rows.Add(new[] { "ok", "Surface " + p.partInfo.title,
                        (surface.ignorePitch ? "" : "P") + (surface.ignoreRoll ? "" : "R") + (surface.ignoreYaw ? "" : "Y") + " / " + F(surface.authorityLimiter) + "%" });
                    var wheel = m as ModuleReactionWheel;
                    if (wheel != null) rows.Add(new[] { wheel.wheelState.ToString() == "Active" ? "ok" : "caution", "Wheel " + p.partInfo.title, wheel.wheelState.ToString() });
                    var chute = m as ModuleParachute;
                    if (chute != null) rows.Add(new[] { "ok", "Chute " + p.partInfo.title, chute.deploymentState.ToString() });
                }
            }
            var resources = new StringBuilder("{");
            foreach (var pair in amounts)
            {
                double cap = capacities[pair.Key];
                double fraction = cap > 0 ? pair.Value / cap : 0;
                double f0; if (!startFraction.TryGetValue(pair.Key, out f0)) startFraction[pair.Key] = f0 = cap > 0 ? fraction : 1;
                rows.Add(new[] { PilotPolicy.ResourceLevel(fraction, cap, used.Contains(pair.Key), engineFuel.Contains(pair.Key), !flying, f0), pair.Key,
                    F(pair.Value) + " / " + F(cap) + (cap > 0 ? " (" + F(100 * fraction) + "%)" : "") });
                if (resources.Length > 1) resources.Append(',');
                resources.Append(ChatWindow.JsonStr(pair.Key)).Append(':').Append(cap > 0 ? N(fraction) : "null");
            }
            resources.Append('}');
            rows.Add(new[] { "ok", "Gear", v.ActionGroups[KSPActionGroup.Gear] ? "Down" : "Up" });
            bool brk = v.ActionGroups[KSPActionGroup.Brakes]; rows.Add(new[] { PilotPolicy.BrakesRowLevel(flying, brk, NativeFlightController.AirbrakesByAutopilot), "Brakes", brk ? (flying && NativeFlightController.AirbrakesByAutopilot ? "Airbrakes (AP)" : "On") : "Off" });
            rows.Add(new[] { hot >= .9 ? "fail" : hot >= .75 ? "caution" : "ok", "Temperature", F(hot * 100) + "% " + hottest });
            rows.Add(new[] { v.geeForce > 7 ? "fail" : v.geeForce > 5 ? "caution" : "ok", "G-load", F(v.geeForce) + " g" });
            string pilot = "No crew";
            foreach (var crew in v.GetVesselCrew()) { pilot = crew.name; if (crew.trait == "Pilot") break; }
            rows.Add(new[] { "ok", "Pilot", pilot });
            foreach (var rotor in RotorTelemetry.Rows)
                rows.Add(new[] { RotorPlacement.Status(rotor.Rpm, rotor.Limit, rotor.Motor, rotor.Brake, flying), rotor.Label,
                    (float.IsNaN(rotor.Rpm) ? "Unknown" : F(rotor.Rpm)) + " / " + F(rotor.Limit) + " RPM" });
            string level = "", alarm = "";
            foreach (var row in rows)
                if (row[0] == "fail" || (row[0] == "caution" && level != "warning"))
                { level = row[0] == "fail" ? "warning" : "caution"; alarm = row[1] + ": " + row[2]; }
            // Only alarm identity/severity changes re-arm acknowledgement, not fluctuating numbers.
            string identity = level + ":" + (alarm.Contains(":") ? alarm.Split(':')[0] : alarm);
            if (identity != alarmIdentity || vesselId != v.id.ToString()) { AlarmSequence++; alarmIdentity = identity; }
            Level = level; Alarm = alarm; vesselId = v.id.ToString(); Systems = rows;
            Flight = new List<string[]> {
                new[] { "alt", F(v.altitude) + " m MSL / " + F(v.radarAltitude) + " m AGL" },
                new[] { "speed", F(v.srfSpeed) + " m/s" }, new[] { "V/S", F(v.verticalSpeed) + " m/s" },
                new[] { "bank", F(BankDeg(v)) + " deg" }, new[] { "pitch", F(PitchDeg(v)) + " deg" },
                new[] { "throttle", F(v.ctrlState.mainThrottle * 100) + "%" }, new[] { "pilot", pilot }
            };
            Json = "{\"resources\":" + resources + ",\"temperature\":" + N(hot) + ",\"hottest\":" + ChatWindow.JsonStr(hottest)
                + ",\"altitude\":" + N(v.altitude) + ",\"agl\":" + N(v.radarAltitude) + ",\"speed\":" + N(v.srfSpeed)
                + ",\"vs\":" + N(v.verticalSpeed) + ",\"g\":" + N(v.geeForce) + "}";
            Available = true;
        }
        static string alarmIdentity = ""; static readonly Dictionary<string, double> startFraction = new Dictionary<string, double>();
    }
}
