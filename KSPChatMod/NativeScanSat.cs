using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace KSPChatBridge
{
    // SCANsat mapping: coverage report, mapping-orbit planner, and crew chatter hooks (soft dependency via ScanSatLink).
    public partial class NativeFlightController
    {
        float nextScanCheck; string scanBody = ""; readonly Dictionary<string, int> scanMilestone = new Dictionary<string, int>(); bool? inScanBand;

        string ScanSatCommand(string name, Dictionary<string, object> a)
        {
            if (name != "scan_coverage" && name != "mapping_orbit") return null;
            if (!ScanSatLink.Installed) return "SCANsat " + ScanSatLink.Missing + ".";
            var body = vessel.mainBody;
            object bn; if (a != null && a.TryGetValue("body", out bn) && bn is string && ((string)bn).Length > 0)
            {
                var b = FlightGlobals.Bodies.Find(x => string.Equals(x.bodyName, (string)bn, StringComparison.OrdinalIgnoreCase));
                if (b == null) return "No body named " + bn + "."; body = b;
            }
            var scanners = ScanSatLink.Scanners(vessel);
            if (name == "scan_coverage")
            {
                var cov = ScanSatLink.Coverage(body); var sb = new StringBuilder("{\"body\":\"" + body.bodyName + "\",\"coverage_pct\":{");
                bool first = true; foreach (var kv in cov) { sb.Append((first ? "" : ",") + "\"" + kv.Key + "\":" + kv.Value.ToString("0.0", Inv)); first = false; }
                sb.Append("},\"scanners\":[");
                for (int i = 0; i < scanners.Count; i++) { var s = scanners[i];
                    sb.Append((i > 0 ? "," : "") + "{\"part\":\"" + s.Part.Replace("\"", "'") + "\",\"types\":\"" + (s.Types.Length > 0 ? s.Types : s.Name).Replace("\"", "'") + "\",\"fov_deg\":" + s.Fov.ToString("0.#", Inv)
                        + ",\"min_km\":" + (s.Min / 1000).ToString("0", Inv) + ",\"best_km\":" + (s.Best / 1000).ToString("0", Inv) + ",\"max_km\":" + (s.Max / 1000).ToString("0", Inv) + ",\"scanning\":" + (s.Scanning ? "true" : "false") + "}"); }
                sb.Append("],\"altitude_km\":" + (vessel.altitude / 1000).ToString("0.0", Inv) + "}");
                return sb.ToString();
            }
            // mapping_orbit
            if (scanners.Count == 0) return "No SCANsat scanner parts on " + vessel.vesselName + " - nothing to map with.";
            var mbm = new List<float[]>(); foreach (var s in scanners) mbm.Add(new[] { s.Min, s.Best, s.Max });
            bool overlap; double alt = ScanSatPolicy.MappingAltitude(mbm, body.atmosphere ? body.atmosphereDepth : 0, out overlap);
            string plan = "Mapping orbit for " + body.bodyName + ": circular " + (alt / 1000).ToString("0", Inv) + " km, inclination " + ScanSatPolicy.Inclination.ToString("0", Inv)
                + " deg (near-polar so every latitude gets covered)." + (overlap ? "" : " Note: the scanners' altitude ranges don't overlap; this favors the average best altitude.");
            object ex; bool execute = a != null && a.TryGetValue("execute", out ex) && ex is bool && (bool)ex;
            if (body != vessel.mainBody) return plan + " (Get into " + body.bodyName + "'s SOI first.)";
            string steps = " Steps: 1) change_inclination " + ScanSatPolicy.Inclination.ToString("0", Inv) + ", 2) change_apoapsis " + (alt / 1000).ToString("0", Inv) + ", 3) change_periapsis " + (alt / 1000).ToString("0", Inv) + ", then turn the scanners on.";
            if (!execute) return plan + steps + " Say 'set it up' to start (one burn at a time).";
            if (vessel.situation != Vessel.Situations.ORBITING) return plan + " Need a stable orbit first." + steps;
            double inc = vessel.orbit.inclination;
            string first2 = Math.Abs(inc - ScanSatPolicy.Inclination) > 2 ? OrbitalCommand("change_inclination", new Dictionary<string, object> { { "inclination_deg", ScanSatPolicy.Inclination } })
                : Math.Abs(vessel.orbit.ApA - alt) > alt * 0.05 ? OrbitalCommand("change_apoapsis", new Dictionary<string, object> { { "altitude_km", alt / 1000 } })
                : Math.Abs(vessel.orbit.PeA - alt) > alt * 0.05 ? OrbitalCommand("change_periapsis", new Dictionary<string, object> { { "altitude_km", alt / 1000 } })
                : "Already in the mapping orbit - turn the scanners on.";
            return plan + " Next burn: " + first2 + " Call mapping_orbit execute again after each burn for the next step.";
        }

        /// <summary>Called from ChatterTick: coverage milestones and ideal-altitude band changes, at most every 15 s.</summary>
        List<CrewLine> MappingTick(IList<KeyValuePair<string, string>> crew, string pilot)
        {
            if (Time.realtimeSinceStartup < nextScanCheck) return null;
            nextScanCheck = Time.realtimeSinceStartup + 15f;
            if (!ScanSatLink.Installed || vessel.situation != Vessel.Situations.ORBITING) { inScanBand = null; return null; }
            var scanners = ScanSatLink.Scanners(vessel); if (scanners.Count == 0) { inScanBand = null; return null; }
            var body = vessel.mainBody;
            if (scanBody != body.bodyName) { scanBody = body.bodyName; scanMilestone.Clear(); inScanBand = null; }
            var cov = ScanSatLink.Coverage(body); string kind = null, what = null;
            if (cov != null) foreach (var kv in cov)
            {
                int ms = ScanSatPolicy.Milestone(kv.Value); int prev;
                if (!scanMilestone.TryGetValue(kv.Key, out prev)) { scanMilestone[kv.Key] = ms; continue; }   // first look: no spam
                if (ms > prev) { scanMilestone[kv.Key] = ms; kind = "milestone"; what = kv.Key + " " + ms; }
            }
            bool band = false; foreach (var s in scanners) if (vessel.altitude >= s.Min && vessel.altitude <= s.Max) band = true;
            if (kind == null && inScanBand != null && inScanBand.Value != band) { kind = band ? "band_in" : "band_out"; what = ""; }
            inScanBand = band;
            if (kind == null) return null;
            var line = MappingChatter.Line(kind, what, body.bodyName, crew, pilot, new System.Random());
            return line == null ? null : new List<CrewLine> { line };
        }
    }
}
