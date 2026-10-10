using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KSPChatBridge
{
    // P5-3: vessel lifecycle + science ported in-mod (stage, recover, launch_craft, parachutes, eject, run/reset science,
    // science watcher). Decisions in SciencePolicy; the watcher runs from SafetyTick.
    public partial class NativeFlightController
    {
        string sciLastKey, sciLastVessel; float sciNextPoll, sciLastPost = -999, sciTransmitAt = -1;

        string ScienceMode { get { object v; return settingsData.TryGetValue("science_mode", out v) && v is string ? SciencePolicy.NormalizeMode((string)v) ?? "remind" : "remind"; } }

        double EcPct()
        {
            double amt = 0, max = 0;
            foreach (Part p in vessel.parts) foreach (PartResource r in p.Resources) if (r.resourceName == "ElectricCharge") { amt += r.amount; max += r.maxAmount; }
            return SciencePolicy.EcPct(amt, max);
        }

        bool SubjectDone(ModuleScienceExperiment e)
        {
            try
            {
                var exp = ResearchAndDevelopment.GetExperiment(e.experimentID); if (exp == null) return false;
                var sit = ScienceUtil.GetExperimentSituation(vessel);
                string biome = exp.BiomeIsRelevantWhile(sit) ? ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude) : "";
                var subj = ResearchAndDevelopment.GetExperimentSubject(exp, sit, vessel.mainBody, biome, biome);
                return subj != null && subj.scienceCap > 0 && subj.science >= subj.scienceCap * .99f;
            }
            catch (Exception) { return false; }
        }

        bool Available(ModuleScienceExperiment e)
        {
            try
            {
                var exp = ResearchAndDevelopment.GetExperiment(e.experimentID);
                return exp != null && exp.IsAvailableWhile(ScienceUtil.GetExperimentSituation(vessel), vessel.mainBody);
            }
            catch (Exception) { return false; }
        }

        List<string> RunExperiments(bool onlyRerunnable, out string blocker)
        {
            var ran = new List<string>();
            blocker = SciencePolicy.RunBlocker(EcPct());
            if (blocker != null) return ran;
            foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                try
                {
                    var ex0 = ResearchAndDevelopment.GetExperiment(e.experimentID); bool sci = false; foreach (var cm in vessel.GetVesselCrew()) sci |= cm.trait == "Scientist";
                    if (!SciencePolicy.CanRun(e.usageReqMaskInternal, vessel.GetCrewCount(), e.part.protoModuleCrew.Count, sci, vessel.IsControllable, ex0 != null && ex0.requireAtmosphere, vessel.mainBody.atmosphere && vessel.altitude < vessel.mainBody.atmosphereDepth))
                    { Debug.Log("[KSPChatBridge] science skip (requirements): " + e.experimentID + " on " + e.part.partInfo.title); continue; }
                    if (!SciencePolicy.Eligible(e.Inoperable, e.GetScienceCount() > 0, Available(e), SubjectDone(e), e.rerunnable, onlyRerunnable)) continue;
                    e.DeployExperiment(); ran.Add(e.part.partInfo.title);
                }
                catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] science run: " + ex.Message); }
            }
            return ran;
        }

        int TransmitAll(bool onlyRerunnable)
        {
            IScienceDataTransmitter tx = null;
            foreach (var t in vessel.FindPartModulesImplementing<IScienceDataTransmitter>()) if (t != null && t.CanTransmit()) { tx = t; break; }
            if (tx == null) return 0;
            int sent = 0;
            foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                if (onlyRerunnable && !e.rerunnable) continue;
                var data = e.GetData(); if (data == null || data.Length == 0) continue;
                tx.TransmitData(new List<ScienceData>(data));
                foreach (var d in data) e.DumpData(d);
                sent++;
            }
            return sent;
        }

        void ScienceTick()
        {
            float now = Time.realtimeSinceStartup;
            if (sciTransmitAt > 0 && now >= sciTransmitAt)
            {
                sciTransmitAt = -1;
                try { int n = TransmitAll(ScienceMode != "auto"); if (n > 0) ChatWindow.Notice("Science: transmitted " + n + " experiment(s)."); }
                catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] science transmit: " + ex.Message); }
            }
            if (now < sciNextPoll) return;
            sciNextPoll = now + (float)SciencePolicy.PollSeconds;
            string mode = ScienceMode;
            string sit = ScienceUtil.GetExperimentSituation(vessel).ToString();
            string biome = ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude);
            string id = NativeIds.Vessel(vessel.id), key = id + "|" + vessel.mainBody.bodyName + "|" + sit + "|" + biome;
            string change = SciencePolicy.SituationChange(mode, sciLastKey, sciLastVessel, key, id);
            if (mode == "off") { sciLastKey = null; return; }
            sciLastKey = key; sciLastVessel = id;
            if (change != "act") return;
            string blocker; var ran = RunExperiments(mode != "auto", out blocker);
            int left = 0;
            if (mode != "auto") foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
                if (!e.rerunnable && SciencePolicy.Eligible(e.Inoperable, e.GetScienceCount() > 0, Available(e), SubjectDone(e), false, false)) left++;
            if (SciencePolicy.ShouldTransmit(true, ran.Count, EcPct())) sciTransmitAt = now + 2;
            if (!SciencePolicy.ShouldPost(mode, ran.Count, blocker, left, now - sciLastPost)) return;
            sciLastPost = now;
            string where = sit + " over " + vessel.mainBody.bodyName + (biome != "" ? " (" + biome + ")" : "");
            ChatWindow.Notice("New science situation: " + where + "." + (ran.Count > 0 ? " Ran " + string.Join(", ", ran.ToArray()) + "." : "")
                + (blocker != null ? " Not running experiments: " + blocker + "." : "") + (left > 0 ? " " + left + " one-shot experiment(s) available - say 'run science'." : ""));
        }

        string LifecycleCommand(string name, Dictionary<string, object> a)
        {
            switch (name)
            {
                case "stage":
                {
                    int before = vessel.currentStage;
                    KSP.UI.Screens.StageManager.ActivateNextStage();
                    return "Staged: stage " + before + " -> " + vessel.currentStage + ".";
                }
                case "recover_vessel":
                    if (!vessel.IsRecoverable) return "Vessel can't be recovered now (situation " + vessel.situation.ToString().ToLowerInvariant() + ").";
                    { string n = vessel.vesselName; Stop(); GameEvents.OnVesselRecoveryRequested.Fire(vessel); return "Recovered " + n + "."; }
                case "launch_craft":
                {
                    string craft = Str(a, "craft_name", "");
                    if (!SciencePolicy.SafeCraftName(craft)) return "craft_name required (file name only).";
                    string editor = SciencePolicy.EditorFolder(Str(a, "editor", "VAB"));
                    string path = Path.Combine(Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), HighLogic.SaveFolder), "Ships"), editor);
                    path = Path.Combine(path, craft + ".craft");
                    if (!File.Exists(path)) return "No " + editor + " craft named " + craft + ".";
                    var node = ConfigNode.Load(path);
                    var manifest = HighLogic.CurrentGame.CrewRoster.DefaultCrewForVessel(node, VesselCrewManifest.FromConfigNode(node));
                    string site = SciencePolicy.LaunchSite(editor, Str(a, "site", ""));
                    Stop();
                    FlightDriver.StartWithNewLaunch(path, HighLogic.CurrentGame.flagURL, site, manifest);
                    return "Launching " + craft + " at " + site + ".";
                }
                case "deploy_parachutes":
                {
                    var chutes = vessel.FindPartModulesImplementing<ModuleParachute>();
                    if (chutes.Count == 0) return "No parachutes on this craft.";
                    string gate = SciencePolicy.ParachuteGate(Bool(a, "force", false), vessel.situation.ToString(), vessel.verticalSpeed);
                    if (gate != null) return gate;
                    int n = 0;
                    foreach (var c in chutes) if (c.deploymentState == ModuleParachute.deploymentStates.STOWED) { c.Deploy(); n++; }
                    return "Armed " + n + " parachute(s); they open when pressure/speed are safe.";
                }
                case "eject_kerbal":
                {
                    string gate = FlightResidualPolicy.ConfirmGate(Bool(a, "confirmed", false), "ejecting a kerbal"); if (gate != null) return gate;
                    return ChatWindow.Eject(Str(a, "name", null));
                }
                case "run_science":
                {
                    string blocker; var ran = RunExperiments(false, out blocker);
                    if (blocker != null) return "Not running experiments: " + blocker + ".";
                    bool tx = SciencePolicy.ShouldTransmit(Bool(a, "transmit", true), ran.Count, EcPct());
                    if (tx) sciTransmitAt = Time.realtimeSinceStartup + 2;
                    return "Ran " + ran.Count + ": " + (ran.Count == 0 ? "none available" : string.Join(", ", ran.ToArray())) + "." + (tx ? " Transmitting in 2 s." : "");
                }
                case "reset_experiments":
                {
                    bool discard = Bool(a, "discard_data", false), scientist = false;
                    foreach (var k in vessel.GetVesselCrew()) if (k.experienceTrait != null && k.experienceTrait.TypeName == "Scientist") { scientist = true; break; }
                    var done = new List<string>(); var skipped = new List<string>();
                    foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
                    {
                        if (e.Inoperable) { if (!scientist) { skipped.Add(e.part.partInfo.title + " (needs a scientist)"); continue; } e.ResetExperiment(); done.Add(e.part.partInfo.title); }
                        else if (discard && e.GetScienceCount() > 0) { foreach (var d in e.GetData()) e.DumpData(d); done.Add(e.part.partInfo.title); }
                    }
                    return "Reset " + done.Count + ": " + (done.Count == 0 ? "none" : string.Join(", ", done.ToArray())) + "." + (skipped.Count > 0 ? " Skipped: " + string.Join(", ", skipped.ToArray()) + "." : "");
                }
                case "set_science_watcher":
                {
                    string m = SciencePolicy.NormalizeMode(Str(a, "mode", ""));
                    if (m == null) return "Unknown mode '" + Str(a, "mode", "") + "'. Use auto, remind or off.";
                    settingsData["science_mode"] = m; Save(); sciLastKey = null;
                    return "Science watcher mode: " + m + ".";
                }
            }
            return null;
        }
    }
}
