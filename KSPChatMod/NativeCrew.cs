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
            if (pilot == null) return new KeyValuePair<string, string>(speaker, "");
            string desc = "";
            try { desc = KerbalPersonality.Describe(pilot.Value.Key, pilot.Value.Value); } catch (Exception) { }
            return new KeyValuePair<string, string>(speaker, CrewVoice.Persona(pilot.Value.Key, pilot.Value.Value, desc, v.vesselName));
        }

        static bool ChatterOn { get { return BridgeLauncher.AiEnabled && !BridgeLauncher.UseBridge; } }

        /// <summary>Emergency intercom reactions (crew.py speak): pilot excluded, canned lines.</summary>
        internal static void CrewEmergency(string kind, string part)
        {
            if (instance == null || !ChatterOn || instance.vessel == null) return;
            var crew = CrewOf(instance.vessel); var p = CrewVoice.Pilot(crew);
            foreach (string line in instance.chatter.Emergency(kind, part, crew, p == null ? "" : p.Value.Key, Time.realtimeSinceStartup, instance.vessel.geeForce))
                ChatWindow.Notice(line);
        }

        void ChatterTick()
        {
            if (!ChatterOn || vessel == null || Time.realtimeSinceStartup < nextChatter) return;
            nextChatter = Time.realtimeSinceStartup + 1f;
            var crew = CrewOf(vessel); var p = CrewVoice.Pilot(crew);
            bool flying = vessel.situation == Vessel.Situations.FLYING;
            foreach (string line in chatter.TripTick(Time.realtimeSinceStartup, vessel.id.ToString(), flying, crew, p == null ? "" : p.Value.Key, mode != "idle", false, double.NaN))
                ChatWindow.Notice(line);
        }

        void OnPartDie(Part part)
        {
            if (part != null && vessel != null && part.vessel == vessel) CrewEmergency("parts", part.partInfo != null ? part.partInfo.title : part.name);
        }
    }
}
