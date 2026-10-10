using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KSPChatBridge
{
    /// <summary>Tool trimming for small local models (Luke Oct 9): a keyword pre-router runs clear commands directly and
    /// otherwise offers only the 1-3 matched schemas (+ find_tool); vague asks get just find_tool. Pure (offline-testable).</summary>
    internal static class ToolRouter
    {
        internal const int MaxOffered = 3;

        /// <summary>Never auto-run (the model must call these, and they keep their own confirm gates).</summary>
        internal static readonly HashSet<string> Destructive = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "abort", "abort_ag", "stage", "recover_vessel", "eject_kerbal", "cut_engines", "deorbit_burn", "launch_craft", "reset_experiments",
          "mechjeb_ascent", "launch_to_target_plane", "transfer_to", "station_keep", "dock_with", "deploy_parachutes", "set_engines", "action_group" };

        static readonly HashSet<string> PlaneOnly = new HashSet<string> { "plane_hold", "roll", "hold_pattern", "follow_terrain", "fuel_check_return", "formation", "takeoff", "land_plane", "land_at_spot", "touch_and_go", "go_around", "circle_here", "plane_pitch", "flaps", "taxi_to", "land_at_ksc", "set_trim", "auto_trim_now", "trim", "get_trim_state", "afterburner" };
        static readonly HashSet<string> HeliOnly = new HashSet<string> { "heli_control", "prop_control" };
        static readonly HashSet<string> SpaceOnly = new HashSet<string> { "sync_orbit_altitude", "time_to_target", "warp_to_apoapsis", "warp_to_soi_change", "circularize", "change_apoapsis", "change_periapsis",
          "deorbit_burn", "change_inclination", "sun_lock", "antenna_lock", "mechjeb_ascent", "dock_with", "transfer_to", "match_target_plane", "launch_to_target_plane", "course_correction", "station_keep", "apsis_longitude", "land_at" };
        static readonly HashSet<string> RoverOnly = new HashSet<string> { "drive_to", "drive_to_building" };

        /// <summary>Is this tool sensible for the craft kind ("plane" | "heli" | "rocket" | "rover" | "" = unknown)?</summary>
        internal static bool Fits(string tool, string craft)
        {
            if (string.IsNullOrEmpty(craft)) return true;
            if (PlaneOnly.Contains(tool)) return craft == "plane";
            if (HeliOnly.Contains(tool)) return craft == "heli";
            if (SpaceOnly.Contains(tool)) return craft == "rocket";
            if (RoverOnly.Contains(tool)) return craft == "rover";
            return true;
        }

        /// <summary>Extra words per tool (beyond the words in its name and description).</summary>
        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "takeoff", "take off takeoff depart launch plane airborne" }, { "land", "land landing touchdown runway rwy ksc 27 09 island set down" },
            { "land_plane", "land landing runway rwy" }, { "land_at_spot", "land spot runway rwy airfield" }, { "set_gear", "gear wheels undercarriage" },
            { "set_brakes", "brakes brake parking" }, { "flaps", "flaps flap" }, { "set_lights", "lights light lamps" }, { "set_sas", "sas stability" },
            { "set_rcs", "rcs thrusters" }, { "plane_hold", "hold altitude heading speed cruise autopilot climb descend" }, { "set_heading", "heading turn to course bearing" },
            { "set_speed", "speed knots fast slow" }, { "set_altitude", "altitude climb descend feet meters height" }, { "set_throttle", "throttle power full max idle percent" },
            { "heli_control", "hover helicopter heli rotor" }, { "taxi_to", "taxi drive runway hangar" }, { "fuel_check", "fuel tank remaining" },
            { "get_status", "status how are we doing report situation" }, { "circularize", "circularize circular orbit" }, { "sun_lock", "sun point solar" },
            { "run_science", "science experiment experiments" }, { "deploy_parachutes", "chute chutes parachute parachutes" }, { "go_around", "go around abort landing missed approach" },
            { "fly_to", "fly to go to head to navigate" }, { "how_far", "how far distance" }, { "get_landing_eta", "eta how long arrive arrival" },
            { "transfer_to", "transfer mun minmus duna go to moon" }, { "warp_to_apoapsis", "warp apoapsis ap" }, { "change_apoapsis", "apoapsis raise ap" },
            { "change_periapsis", "periapsis pe lower" }, { "level_off", "level off level wings" }, { "hold_pattern", "holding pattern orbit circle loiter over" }, { "fuel_check_return", "bingo fuel return home rtb enough fuel get back" }, { "formation", "formation wingman wing escort echelon join up" }, { "tech_advisor", "tech tree research unlock node nodes r&d rnd science points" }, { "drive_to_building", "drive rover building buildings vab sph r&d tracking station mission control astronaut complex administration tour ksc" }, { "roll", "roll bank inverted upside down barrel wings level" }, { "autopilot", "autopilot ap engage disengage" }, { "make_flight_plan", "flight plan route mission out back turn around" }, { "follow_terrain", "terrain follow agl ground hug low nap earth height above" }, { "stage", "stage staging next stage" },
        };

        static readonly Regex Word = new Regex(@"[a-z0-9]+");
        static HashSet<string> Words(string s)
        {
            var h = new HashSet<string>();
            foreach (Match m in Word.Matches((s ?? "").ToLowerInvariant())) if (m.Value.Length > 1 || char.IsDigit(m.Value[0])) h.Add(m.Value);
            return h;
        }
        static readonly HashSet<string> Stop = new HashSet<string> { "the", "a", "an", "to", "at", "of", "and", "or", "for", "in", "on", "is", "it", "me", "my", "we", "us", "please", "can", "you", "now", "set", "get", "this", "that", "with" };

        /// <summary>Best tools for a message: [(name, score)] high first, filtered by craft. schemas: name -> description.</summary>
        internal static List<KeyValuePair<string, double>> Rank(string message, IDictionary<string, string> descriptions, string craft, int max = MaxOffered)
        {
            var msg = Words(message); msg.ExceptWith(Stop);
            var scored = new List<KeyValuePair<string, double>>();
            if (msg.Count == 0) return scored;
            foreach (var kv in descriptions)
            {
                if (!Fits(kv.Key, craft)) continue;
                var nameW = Words(kv.Key.Replace('_', ' ')); string al; Aliases.TryGetValue(kv.Key, out al);
                var aliasW = Words(al); var descW = Words(kv.Value);
                double s = 0;
                foreach (string w in msg)
                {
                    if (nameW.Contains(w)) s += 3; else if (aliasW.Contains(w)) s += 2; else if (descW.Contains(w) && !Stop.Contains(w)) s += .5;
                }
                if (s >= 2) scored.Add(new KeyValuePair<string, double>(kv.Key, s));
            }
            scored.Sort((x, y) => y.Value != x.Value ? y.Value.CompareTo(x.Value) : string.CompareOrdinal(x.Key, y.Key));
            if (scored.Count > max) scored.RemoveRange(max, scored.Count - max);
            return scored;
        }

        static readonly Regex Num = new Regex(@"(-?\d+(?:\.\d+)?)");
        /// <summary>Clear single-intent commands with parseable args -> (tool, argsJson) to run without the model. null otherwise.
        /// Destructive tools never come out of here.</summary>
        internal static KeyValuePair<string, string>? Direct(string message, string craft)
        {
            string m = (message ?? "").Trim().ToLowerInvariant().TrimEnd('.', '!', '?');
            Func<string, string, KeyValuePair<string, string>?> R = (t, a) => Fits(t, craft) && !Destructive.Contains(t) ? new KeyValuePair<string, string>(t, a) : (KeyValuePair<string, string>?)null;
            string confident; List<string> unsureP, notesP;
            if (Regex.IsMatch(m, @"^(make|write|create|build) (me )?(a )?(flight )?plan\b|^flight plan:|^plan:") && m.Length <= 400 && PilotPolicy.TryPlan(message, false, out confident, out unsureP, out notesP))
                return R("make_flight_plan", "{\"request\":" + MiniJson.Serialize(message.Trim()) + "}");
            if (Regex.IsMatch(m, @"^(land and circle|circle and land|circle (the )?runway (and|then) land)") && m.Length <= 80)   // circle first, then land
                return R("make_flight_plan", "{\"request\":" + MiniJson.Serialize("circle 1 lap, land at KSC") + "}");
            if (Regex.IsMatch(m, @"^(current job|what('?s| is) (the |our )?(current )?(job|status|plan)|status( report)?|sitrep)\??$")) return R("get_status", "{}");
            var bl = Regex.Match(m, @"^bank (\d{1,2})(?: deg\w*)?,? (?:and |then )?land(?: at)? (?:the )?(.+)$");
            if (bl.Success) return R("land_plane", "{\"name\":" + MiniJson.Serialize(bl.Groups[2].Value.Trim()) + ",\"bank\":" + bl.Groups[1].Value + "}");
            if (m.Length == 0 || m.Length > 60 || m.StartsWith("/") || m.Contains("?") || m.Contains(" and ") || m.Contains(" then ")) return null;
            if (Regex.IsMatch(m, @"^(gear|wheels) (down|out)$|^(lower|drop) (the )?gear$")) return R("set_gear", "{\"down\":true}");
            if (Regex.IsMatch(m, @"^(gear|wheels) up$|^(raise|retract) (the )?gear$")) return R("set_gear", "{\"down\":false}");
            if (Regex.IsMatch(m, @"^(brakes?|parking brake) on$|^(set|apply) (the )?brakes?$")) return R("set_brakes", "{\"on\":true}");
            if (Regex.IsMatch(m, @"^(brakes?|parking brake) off$|^release (the )?(parking )?brakes?$")) return R("set_brakes", "{\"on\":false}");
            if (Regex.IsMatch(m, @"^lights? (on|off)$")) return R("set_lights", "{\"on\":" + (m.EndsWith("on") ? "true" : "false") + "}");
            if (Regex.IsMatch(m, @"^sas (on|off)$")) return R("set_sas", "{\"enabled\":" + (m.EndsWith("on") ? "true" : "false") + "}");
            if (Regex.IsMatch(m, @"^rcs (on|off)$")) return R("set_rcs", "{\"on\":" + (m.EndsWith("on") ? "true" : "false") + "}");
            if (Regex.IsMatch(m, @"^(take ?off|takeoff)( now)?$")) return R("takeoff", "{}");
            if (Regex.IsMatch(m, @"^(i )?(overr?ide|authori[sz]e[ds]?)( (it|that|the cap|authori[sz]ed|override|granted|ok))?$")) return R("set_speed", "{\"speed\":\"last\",\"override\":true}");
            if (Regex.IsMatch(m, @"^(i )?authori[sz]e[ds]? (max|full|maximum) speed$")) return R("set_speed", "{\"speed\":\"max\"}");
            var so = Regex.Match(m, @"^set (?:the )?speed (?:to )?(\d{2,4})\b.*\b(authori[sz]e[ds]?|override)\b");
            if (so.Success) return R("set_speed", "{\"speed\":" + so.Groups[1].Value + ",\"override\":true}");
            if (Regex.IsMatch(m, @"^(set )?(speed )?(to )?(max(imum)?|full) speed$|^(set )?speed (to )?(max(imum)?|full)$|^(go )?flat out$")) return R("set_speed", "{\"speed\":\"max\"}");
            if (Regex.IsMatch(m, @"^(please )?(turn|swing|come) (it |her )?around$|^(do a |make a )?u-?turn$|^turn 180$|^reverse course$")) return R("set_heading", "{\"relative\":180}");
            if (Regex.IsMatch(m, @"^(full|max(imum)?) (throttle|power)$|^throttle (up|full|max)$")) return R("set_throttle", "{\"value\":100}");
            var tp = Regex.Match(m, @"^(?:set )?throttle (?:to )?(\d{1,3}) ?%?$"); if (tp.Success) return R("set_throttle", "{\"value\":" + Math.Max(5, Math.Min(100, int.Parse(tp.Groups[1].Value))) + "}");
            if (Regex.IsMatch(m, @"^(roll|flip|go) (inverted|upside down)$|^invert$")) return R("roll", "{\"inverted\":true}");
            if (Regex.IsMatch(m, @"^(roll|wings) level$|^level (the )?wings$")) return R("roll", "{\"level\":true}");
            var rb = Regex.Match(m, @"^(?:roll|bank) (left|right)(?: (\d{1,3}))?(?: ?deg(?:rees)?)?( override)?$");
            if (rb.Success) return R("roll", "{\"direction\":\"" + rb.Groups[1].Value + "\",\"degrees\":" + (rb.Groups[2].Success ? rb.Groups[2].Value : "15") + (rb.Groups[3].Success ? ",\"override\":true" : "") + "}");
            if (Regex.IsMatch(m, @"^(turn (on|back on) (the )?autopilot|autopilot (on|engage)|engage (the )?autopilot)$")) return R("autopilot", "{\"on\":true}");
            if (Regex.IsMatch(m, @"^(turn off (the )?autopilot|autopilot off|disengage (the )?autopilot)$")) return R("autopilot", "{\"on\":false}");
            if (Regex.IsMatch(m, @"^land( at| on)? (the )?(nearest|closest)( runway)?$")) return R("land", "{\"where\":\"nearest runway\"}");
            if (Regex.IsMatch(m, @"^circle( the)?( runway| here| field| ksc)?$")) return R("circle_here", "{}");
            if (Regex.IsMatch(m, @"\b(flying away from|passed|overflew|overshot|missed|went past) (the )?(ksp|ksc|runway|airport|base)\b|\bturn (back|around) and land\b|\bgo back and land\b")) return R("land", "{}");   // round 3: "we passed KSP" = resume the landing
            var f = Regex.Match(m, @"^flaps? ([0-3]|up|full|down)$"); if (f.Success) return R("flaps", "{\"setting\":\"" + f.Groups[1].Value + "\"}");
            if (Regex.IsMatch(m, @"^(retract|raise) (the )?flaps$")) return R("flaps", "{\"setting\":\"up\"}");
            var l = Regex.Match(m, @"^land( (now|here))?$"); if (l.Success) return R("land", "{}");
            var la = Regex.Match(m, @"^land (at |on )?(.+)$");
            if (la.Success && FlightResidualPolicy.BuiltInRunway(la.Groups[2].Value, "") != null) return R("land", "{\"where\":" + MiniJson.Serialize(la.Groups[2].Value.Trim()) + "}");
            if (Regex.IsMatch(m, @"^(status|report|how are we doing)$")) return R("get_status", "{}");
            if (Regex.IsMatch(m, @"^(go around|go-around)$")) return R("go_around", "{}");
            if (Regex.IsMatch(m, @"^(level off|level out|wings level)$")) return R("level_off", "{}");
            if (Regex.IsMatch(m, @"^circulari[sz]e$")) return R("circularize", "{}");
            var h = Regex.Match(m, @"^(?:heading|hdg|turn to|fly heading) (\d{1,3})$"); if (h.Success) return R("set_heading", "{\"heading\":" + h.Groups[1].Value + "}");
            return null;
        }

        internal const string FindToolSchema = "{\"type\":\"function\",\"function\":{\"name\":\"find_tool\",\"description\":\"Find the game tool for what you need to do; returns up to 3 tools you can then call.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}}}";

        static Dictionary<string, object> schemas;
        static Dictionary<string, string> descriptions;
        /// <summary>name -> schema object, parsed once from NativeToolSchemas.Json.</summary>
        internal static Dictionary<string, object> Schemas
        {
            get
            {
                if (schemas != null) return schemas;
                var s = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (object o in (IList)MiniJson.DeserializeObject(NativeToolSchemas.Json))
                {
                    var fn = (Dictionary<string, object>)((Dictionary<string, object>)o)["function"];
                    s[fn["name"].ToString()] = o; d[fn["name"].ToString()] = fn.ContainsKey("description") ? fn["description"].ToString() : "";
                }
                descriptions = d; return schemas = s;
            }
        }
        internal static Dictionary<string, string> Descriptions { get { var _ = Schemas; return descriptions; } }

        /// <summary>Tools to send for one request: matched schemas (craft-filtered) + find_tool. Always small.</summary>
        internal static List<object> Offer(string message, string craft)
        {
            var list = new List<object>();
            foreach (var kv in Rank(message, Descriptions, craft)) list.Add(Schemas[kv.Key]);
            list.Add(MiniJson.DeserializeObject(FindToolSchema));
            return list;
        }

        /// <summary>find_tool(query): the best schemas as JSON (returned to the model as the tool result).</summary>
        internal static string Find(string query, string craft, out List<object> found)
        {
            found = new List<object>();
            var names = new List<string>();
            foreach (var kv in Rank(query, Descriptions, craft)) { found.Add(Schemas[kv.Key]); names.Add(kv.Key); }
            if (found.Count == 0) return "No matching tool. Answer Luke directly, or try find_tool with other words.";
            return "Tools now available: " + string.Join(", ", names.ToArray()) + ". Call the right one. Schemas: " + MiniJson.Serialize(found);
        }
    }
}
