using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    // Native crew voice + intercom chatter (live bugs Oct 9: replies came from "AICS"; no crew chatter without the bridge).
    public partial class NativeFlightController
    {
        readonly CrewChatter chatter = new CrewChatter();
        float nextChatter;

        internal static List<KeyValuePair<string, string>> CrewOf(Vessel v)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (v == null) return list;
            foreach (ProtoCrewMember k in v.GetVesselCrew())
                list.Add(new KeyValuePair<string, string>(k.name, k.experienceTrait != null ? k.experienceTrait.TypeName : k.trait));
            return list;
        }

        internal static string AiName
        {
            get { object n; return instance != null && instance.settingsData != null && instance.settingsData.TryGetValue("ai_name", out n) && n != null ? n.ToString() : ""; }
        }

        /// <summary>(speaker shown in chat, persona lines for the system prompt) for the active vessel. Main thread.</summary>
        internal static KeyValuePair<string, string> Voice()
        {
            var v = FlightGlobals.ActiveVessel;
            var crew = CrewOf(v);
            var pilot = CrewVoice.Pilot(crew);
            string speaker = CrewVoice.Speaker(crew, AiName);
            if (pilot == null || speaker == CrewVoice.Autopilot) return new KeyValuePair<string, string>(speaker, "");
            string desc = "";
            try { desc = KerbalPersonality.Describe(pilot.Value.Key, pilot.Value.Value); } catch (Exception) { }
            return new KeyValuePair<string, string>(speaker, CrewVoice.Persona(pilot.Value.Key, pilot.Value.Value, desc, v.vesselName));
        }

        /// <summary>plane | heli | rocket | rover | "" for tool filtering (main thread).</summary>
        internal static string CraftKind()
        {
            if (instance == null || instance.vessel == null) return "";
            var v = instance.vessel;
            if (instance.props.HasLift(v)) return "heli";
            if (instance.IsPlane()) return "plane";
            bool motor = false, engine = false;
            foreach (Part p in v.parts) { motor |= p.FindModuleImplementing<ModuleWheels.ModuleWheelMotor>() != null; engine |= p.FindModuleImplementing<ModuleEngines>() != null; }
            if (motor && !engine) return "rover";
            return "rocket";
        }

        static bool ChatterOn { get { return AicsCore.AiEnabled; } }
        bool chatterInit;
        static bool poolLoaded;

        void InitChatter()
        {
            if (chatterInit) return; chatterInit = true;
            object v; chatter.Enabled = !(settingsData != null && settingsData.TryGetValue("crew_chatter", out v) && v is bool && !(bool)v);
            chatter.Describe = n => { try { return KerbalPersonality.Describe(n, "kerbal").Replace("a kerbal", "a kerbal"); } catch (Exception) { return ""; } };
            chatter.LovesDadJokes = n => { try { return KerbalPersonality.LikesPhrase(n, "loves dad jokes"); } catch (Exception) { return false; } };
            if (!poolLoaded)
            {
                poolLoaded = true;
                try
                {
                    string path = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPChatBridge/personalities.txt");
                    if (System.IO.File.Exists(path)) { var parsed = CrewPrompts.Parse(System.IO.File.ReadAllLines(path, System.Text.Encoding.UTF8)); if (parsed.Length >= 4) CrewPrompts.Pool = parsed; }
                }
                catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] personalities.txt: " + ex.Message); }
            }
        }

        /// <summary>'/crew chatter on|off' - saved in native settings.</summary>
        internal static string CrewCommand(string arg)
        {
            if (instance == null) return "Crew chatter: not in flight.";
            instance.InitChatter();
            string r = instance.chatter.Command(arg);
            instance.settingsData["crew_chatter"] = instance.chatter.Enabled;
            try { instance.Save(); } catch (Exception) { }
            return r;
        }

        /// <summary>Say lines in order: each model-written when the model is free, else canned; never blocks.</summary>
        void SpeakAll(List<CrewLine> lines, int i = 0, Action after = null)
        {
            if (lines == null || i >= lines.Count) { if (after != null) after(); return; }
            var line = lines[i];
            Action<string> post = text => { ChatLog.Write("chatter", line.Format(text) + (text == null ? " (canned)" : " (model)")); ChatWindow.Notice(line.Format(text)); chatter.Spoke(line.Name, Time.realtimeSinceStartup);
                if (i + 1 < lines.Count && lines[i + 1].Delay > 0) StartCoroutine(After((float)lines[i + 1].Delay, () => SpeakAll(lines, i + 1, after))); else SpeakAll(lines, i + 1, after); };
            string system = CrewPrompts.SystemFor(AicsCrewScenario.For(line.Name, line.Trait));
            if (!InModAiHost.TryCrewLine(system, line.Prompt, txt => post(line.Accept(txt)))) post(null);
        }

        System.Collections.IEnumerator After(float seconds, Action act) { yield return new WaitForSeconds(seconds); act(); }

        /// <summary>Problem events: pilot first, then every named kerbal aboard, staggered, rate-limited, canned fallback.</summary>
        internal static void CrewEmergency(string kind, string part)
        {
            if (instance == null || !ChatterOn || instance.vessel == null) return;
            instance.InitChatter();
            var crew = CrewOf(instance.vessel); var p = CrewVoice.Pilot(crew);
            ChatLog.Write("chatter", "event " + kind + " (" + part + ")");
            PilotEvents.Add(kind + ": " + part, PilotEvents.Now);
            var lines = instance.chatter.Emergency(kind, part, crew, p == null ? "" : p.Value.Key, Time.realtimeSinceStartup, instance.vessel.geeForce);
            if (lines.Count == 0) { ChatLog.Write("chatter", "skip: rate-limited (" + kind + ")"); return; }
            var names = new List<string>(); foreach (var c in crew) names.Add(c.Key);
            string pilot = lines[0].Name; var others = new List<string>(); foreach (var n in names) if (n != pilot) others.Add(n);
            string facts = CrewPrompts.FactsFor(kind, part, instance.vessel.geeForce);
            Action canned = () => PostScene(lines.ConvertAll(l => new KeyValuePair<string, string>(l.Name, l.Canned)), crew, "canned");
            bool asked = InModAiHost.TryCrewLine("You write short, characterful Kerbal crew intercom lines.", CrewScene.Prompt(facts, pilot, others), txt =>
            {
                var scene = CrewScene.Parse(txt, names);
                if (scene.Count > 0) PostScene(scene, crew, "model scene"); else canned();
            }, 220);
            if (!asked) canned();
        }

        static readonly System.Random sceneRng = new System.Random();
        /// <summary>Post scene lines staggered 2-3 s apart (thread-safe: Notice is a queue).</summary>
        static void PostScene(List<KeyValuePair<string, string>> scene, List<KeyValuePair<string, string>> crew, string how)
        {
            double t = 0;
            foreach (var kv in scene)
            {
                string trait = "pilot"; foreach (var c in crew) if (c.Key == kv.Key) trait = c.Value;
                string text = IntercomTalk.Fmt(kv.Key, trait, kv.Value);
                int ms = (int)(t * 1000);
                if (ms == 0) { ChatWindow.Notice(text); ChatLog.Write("chatter", text + " (" + how + ")"); }
                else { System.Threading.Timer tm = null; tm = new System.Threading.Timer(_ => { ChatWindow.Notice(text); ChatLog.Write("chatter", text + " (" + how + ")"); tm.Dispose(); }, null, ms, System.Threading.Timeout.Infinite); }
                lock (sceneRng) t += CrewScene.Gap(sceneRng);
            }
        }

        void ChatterTick()
        {
            if (!ChatterOn || vessel == null || Time.realtimeSinceStartup < nextChatter) return;
            nextChatter = Time.realtimeSinceStartup + 1f;
            InitChatter();
            var crew = CrewOf(vessel); var p = CrewVoice.Pilot(crew); string pilot = p == null ? "" : p.Value.Key;
            bool flying = vessel.situation == Vessel.Situations.FLYING;
            float now = Time.realtimeSinceStartup;
            var map = MappingTick(crew, pilot); if (map != null) { ChatLog.Write("chatter", "mapping line for " + map[0].Name); SpeakAll(map); return; }
            var talk = chatter.TalkTick(now, vessel.id.ToString(), flying, crew, pilot, mode != "idle", false, double.NaN);
            if (talk != null) { StartCoroutine(Converse(talk)); return; }
            var trip = chatter.TripTick(now, vessel.id.ToString(), flying, crew, pilot, mode != "idle", false, double.NaN);
            if (trip != null && trip.Count > 0) { SpeakAll(trip); return; }
            string skip0 = chatter.LastSkip;
            var solo = chatter.SoloTick(now, flying, crew, pilot, "altitude " + vessel.altitude.ToString("0") + " m, speed " + vessel.srfSpeed.ToString("0") + " m/s");
            if (solo != null) { ChatLog.Write("chatter", "ambient line for " + solo.Name + (InModAiHost.Busy ? " (model busy -> canned)" : "")); SpeakAll(new List<CrewLine> { solo }); }
            else if (chatter.LastSkip != skip0 && chatter.LastSkip.Length > 0) ChatLog.Write("chatter", "skip: " + chatter.LastSkip);
        }

        System.Collections.IEnumerator Converse(CrewTalk talk)
        {
            chatter.TalkRunning = true;
            var transcript = new List<KeyValuePair<string, string>>();
            var rng = new System.Random();
            for (int i = 0; i < talk.Lines; i++)
            {
                if (!ChatterOn || !chatter.Enabled || chatter.RecentlyEmergency(Time.realtimeSinceStartup)) break;
                var line = chatter.TalkLine(talk, i, transcript);
                string said = null; bool done = false;
                string system = CrewPrompts.SystemFor(AicsCrewScenario.For(line.Name, line.Trait));
                if (!InModAiHost.TryCrewLine(system, line.Prompt, txt => { said = line.Accept(txt); done = true; })) done = true;
                float until = Time.realtimeSinceStartup + 10f;
                while (!done && Time.realtimeSinceStartup < until) yield return null;
                if (chatter.RecentlyEmergency(Time.realtimeSinceStartup)) break;   // an emergency while generating: drop it
                string text = said ?? line.Canned;
                ChatWindow.Notice(line.Format(text)); chatter.Spoke(line.Name, Time.realtimeSinceStartup);
                transcript.Add(new KeyValuePair<string, string>(IntercomTalk.First(line.Name), text));
                if (i < talk.Lines - 1) yield return new WaitForSeconds(5f + (float)rng.NextDouble() * 5f);
            }
            chatter.TalkRunning = false;
        }

        /// <summary>'@Bob ...' / 'Bob, ...': a crew member answers in character. true = handled (not pilot chat).</summary>
        internal static bool TryIntercom(string text)
        {
            var v = FlightGlobals.ActiveVessel; if (v == null) return false;
            var crew = CrewOf(v); var p = CrewVoice.Pilot(crew);
            var known = new List<string>();
            try { foreach (ProtoCrewMember k in HighLogic.CurrentGame.CrewRoster.Crew) known.Add(k.name); } catch (Exception) { }
            var route = IntercomTalk.Route(text, crew, p == null ? "" : IntercomTalk.First(p.Value.Key), known);
            if (route == null || route.Item1 == "order") return false;   // orders go to the pilot (tools)
            if (route.Item1 == "absent") { ChatWindow.ReleasePendingChat(); ChatWindow.Notice("[INTERCOM] No answer - " + route.Item2.Key + " isn't aboard."); return true; }
            var member = route.Item2;
            string facts = v.situation.ToString().ToLowerInvariant().Replace('_', ' ') + ", " + v.altitude.ToString("0") + " m up, " + v.srfSpeed.ToString("0") + " m/s";
            InModAiHost.EnqueueIntercom(member, route.Item3, facts, p == null ? "" : p.Value.Key, CrewPrompts.SystemFor(AicsCrewScenario.For(member.Key, member.Value)));
            return true;
        }

        void OnPartDie(Part part)
        {
            if (part != null && vessel != null && part.vessel == vessel) CrewEmergency("parts", part.partInfo != null ? part.partInfo.title : part.name);
        }
    }

    /// <summary>Per-save crew personalities (Luke: Jeb differs between saves, stays himself within one).</summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]
    public class AicsCrewScenario : ScenarioModule
    {
        static readonly Dictionary<string, int[]> Map = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        static readonly System.Random Rng = new System.Random();

        internal static int[] For(string name, string trait)
        {
            if (string.IsNullOrEmpty(name)) return null;
            int[] pair;
            lock (Map) { if (!Map.TryGetValue(name, out pair)) { pair = CrewPrompts.Roll(trait, Rng); Map[name] = pair; } }
            return pair;
        }

        public override void OnLoad(ConfigNode node)
        {
            lock (Map)
            {
                Map.Clear();
                foreach (ConfigNode k in node.GetNodes("KERBAL"))
                {
                    int a, b; string n = k.GetValue("name");
                    if (!string.IsNullOrEmpty(n) && int.TryParse(k.GetValue("primary"), out a) && int.TryParse(k.GetValue("secondary"), out b)) Map[n] = new[] { a, b };
                }
            }
        }

        public override void OnSave(ConfigNode node)
        {
            lock (Map)
                foreach (var kv in Map)
                {
                    var k = node.AddNode("KERBAL");
                    k.AddValue("name", kv.Key); k.AddValue("primary", kv.Value[0]); k.AddValue("secondary", kv.Value.Length > 1 ? kv.Value[1] : kv.Value[0]);
                    k.AddValue("personality", CrewPrompts.Title(kv.Value));
                }
        }
    }
}
