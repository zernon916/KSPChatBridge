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

        /// <summary>Subject IDs already held in experiment modules or science containers on this vessel.</summary>
        HashSet<string> StoredSubjectIds()
        {
            var o = new HashSet<string>();
            foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                var data = e.GetData(); if (data == null) continue;
                foreach (var d in data) if (d != null && !string.IsNullOrEmpty(d.subjectID)) o.Add(d.subjectID);
            }
            foreach (var c in vessel.FindPartModulesImplementing<ModuleScienceContainer>())
            {
                var data = c.GetData(); if (data == null) continue;
                foreach (var d in data) if (d != null && !string.IsNullOrEmpty(d.subjectID)) o.Add(d.subjectID);
            }
            return o;
        }

        List<string> RunExperiments(bool onlyRerunnable, out string blocker)
        {
            var ran = new List<string>();
            blocker = SciencePolicy.RunBlocker(EcPct());
            if (blocker != null) return ran;
            var sit = ScienceUtil.GetExperimentSituation(vessel);
            var body = vessel.mainBody;
            string sitName = sit.ToString(), bodyName = body != null ? body.bodyName : "?";
            var groups = new Dictionary<string, List<ModuleScienceExperiment>>();
            foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                string id = e.experimentID ?? "";
                List<ModuleScienceExperiment> g; if (!groups.TryGetValue(id, out g)) groups[id] = g = new List<ModuleScienceExperiment>();
                g.Add(e);
            }
            var stored = StoredSubjectIds();
            bool scientist = false; foreach (var cm in vessel.GetVesselCrew()) scientist |= cm.trait == "Scientist";
            int crew = vessel.GetCrewCount(); bool controllable = vessel.IsControllable;
            bool inAtmo = body != null && body.atmosphere && vessel.altitude < body.atmosphereDepth;
            foreach (var kv in groups)
            {
                string expId = kv.Key;
                var exp = ResearchAndDevelopment.GetExperiment(expId);
                string biome = "";
                double value = 0; string subjectId = "";
                if (exp != null && body != null)
                {
                    try
                    {
                        biome = exp.BiomeIsRelevantWhile(sit) ? ScienceUtil.GetExperimentBiome(body, vessel.latitude, vessel.longitude) : "";
                        var subj = ResearchAndDevelopment.GetExperimentSubject(exp, sit, body, biome, biome);
                        if (subj != null) { subjectId = subj.id; value = ResearchAndDevelopment.GetScienceValue(exp.baseValue * exp.dataScale, subj); }
                    }
                    catch (Exception) { }
                }
                bool storedOnboard = subjectId.Length > 0 && stored.Contains(subjectId);
                bool available = exp != null && body != null && exp.IsAvailableWhile(sit, body);
                bool ranThisType = false;
                foreach (var e in kv.Value)
                {
                    try
                    {
                        if (!SciencePolicy.CanRun(e.usageReqMaskInternal, crew, e.part.protoModuleCrew.Count, scientist, controllable, exp != null && exp.requireAtmosphere, inAtmo))
                        {
                            Debug.Log(SciencePolicy.FormatDecision(expId, bodyName, biome, sitName, false, value, "requirements"));
                            continue;
                        }
                        string skip = SciencePolicy.AutoSkipReason(e.Inoperable, e.GetScienceCount() > 0, available, storedOnboard, value, ranThisType, onlyRerunnable, e.rerunnable);
                        if (skip != null) { Debug.Log(SciencePolicy.FormatDecision(expId, bodyName, biome, sitName, false, value, skip)); continue; }
                        DeploySilent(e);   // Luke Oct 12 22:41: no stock results dialog mid-flight; transmit/store handled by TransmitAll
                        ranThisType = true; ran.Add(e.part.partInfo.title);
                        if (subjectId.Length > 0) stored.Add(subjectId);
                        Debug.Log(SciencePolicy.FormatDecision(expId, bodyName, biome, sitName, true, value, null));
                    }
                    catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] science run: " + ex.Message); }
                }
            }
            return ran;
        }

        /// <summary>Run an experiment WITHOUT the stock ExperimentsResultDialog: ModuleScienceExperiment.gatherData(silent: true)
        /// via reflection; fallback = DeployExperiment and close the dialog a moment later.</summary>
        void DeploySilent(ModuleScienceExperiment e)
        {
            try
            {
                var m = typeof(ModuleScienceExperiment).GetMethod("gatherData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                var it = m != null ? m.Invoke(e, new object[] { true }) as System.Collections.IEnumerator : null;
                if (it != null) { e.StartCoroutine(it); return; }
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] silent science failed, using stock deploy: " + ex.Message); }
            e.DeployExperiment(); StartCoroutine(CloseResultsDialog());
        }
        System.Collections.IEnumerator CloseResultsDialog()
        {
            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForSeconds(.25f);
                var d = UnityEngine.Object.FindObjectOfType<KSP.UI.Screens.Flight.Dialogs.ExperimentsResultDialog>();
                if (d != null) { UnityEngine.Object.Destroy(d.gameObject); yield break; }
            }
        }
        /// <summary>EC on the vessel (amount, max).</summary>
        void Ec(out double amt, out double max)
        {
            amt = 0; max = 0; var ec = PartResourceLibrary.Instance.GetDefinition("ElectricCharge");
            if (ec != null) vessel.GetConnectedResourceTotals(ec.id, out amt, out max);
        }
        internal const double EcReserveFrac = .10, EcReserveMin = 5, AntennaMaxQ = 1.0;   // kPa: stock deployables break in the airstream
        ModuleDeployableAntenna sciExtended;   // antenna AUTO science opened (retract after the transmission)
        bool HasData(bool onlyRerunnable) { foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>()) if ((!onlyRerunnable || e.rerunnable) && e.GetScienceCount() > 0) return true; return false; }
        /// <summary>Extend a retracted deployable antenna if safe; null = extending, else why not.</summary>
        string ExtendAntennaIfSafe()
        {
            double q = vessel.dynamicPressurekPa; bool inAtmo = vessel.mainBody.atmosphere && vessel.altitude < vessel.mainBody.atmosphereDepth;
            ModuleDeployableAntenna pick = null; string unsafeWhy = null;
            foreach (var a in vessel.FindPartModulesImplementing<ModuleDeployableAntenna>())
            {
                if (a.deployState != ModuleDeployablePart.DeployState.RETRACTED || a.part.FindModuleImplementing<IScienceDataTransmitter>() == null) continue;
                string w = null;
                if (a.part.ShieldedFromAirstream) w = "shielded/in a closed bay";
                else if (a.part.temperature > .5 * a.part.maxTemp || a.part.skinTemperature > .5 * a.part.skinMaxTemp) w = "hot (reentry heating)";
                else if (inAtmo && a.isBreakable && q > AntennaMaxQ) w = "airstream";
                if (w != null) { unsafeWhy = a.part.partInfo.title + " unsafe: " + w; continue; }
                pick = a; break;
            }
            if (pick == null) return unsafeWhy != null ? "antenna " + unsafeWhy + " (q " + q.ToString("0.00") + " kPa)" : "no usable antenna";
            pick.Extend(); sciExtended = pick;
            ChatLog.Write("science", "[science] EXTEND " + pick.part.partInfo.title + " (q " + q.ToString("0.00") + " kPa)");
            return null;
        }
        /// <summary>Retract the antenna we opened once nothing is transmitting; also if the airstream turns unsafe.</summary>
        void RetractIfDone()
        {
            var a = sciExtended; if (a == null) return;
            if (a.part == null || a.vessel != vessel) { sciExtended = null; return; }
            if (a.deployState != ModuleDeployablePart.DeployState.EXTENDED) return;
            bool danger = vessel.mainBody.atmosphere && vessel.altitude < vessel.mainBody.atmosphereDepth && a.isBreakable && vessel.dynamicPressurekPa > AntennaMaxQ;
            var t = a.part.FindModuleImplementing<IScienceDataTransmitter>();
            if (!danger && (sciTransmitAt > 0 || (t != null && t.IsBusy()))) return;
            a.Retract(); sciExtended = null;
            ChatLog.Write("science", "[science] RETRACT " + a.part.partInfo.title + (danger ? " (airstream q " + vessel.dynamicPressurekPa.ToString("0.00") + " kPa)" : ""));
        }
        bool sciStored;   // data held back for lack of EC/antenna -> retry later

        /// <summary>Luke Oct 12 22:41: transmit only when the vessel's EC covers the WHOLE transmission (antenna EC/Mit x data Mits)
        /// plus a reserve (>= 10% battery); else keep the data onboard and retry when EC recovers. Returns experiments sent.</summary>
        int TransmitAll(bool onlyRerunnable)
        {
            sciStored = false;
            IScienceDataTransmitter tx = null; string txName = "";
            foreach (var t in vessel.FindPartModulesImplementing<IScienceDataTransmitter>())
                if (t != null && t.CanTransmit() && !t.IsBusy() && (tx == null || t.DataResourceCost < tx.DataResourceCost)) { tx = t; var pm = t as PartModule; txName = pm != null ? pm.part.partInfo.title : "antenna"; }
            double amt, max; Ec(out amt, out max); double reserve = Math.Max(EcReserveMin, EcReserveFrac * max);
            if (tx == null && HasData(onlyRerunnable))
            {   // Luke Oct 12 22:45: no usable (fixed/already open) antenna -> extend a retracted deployable one, only if SAFE
                string why = ExtendAntennaIfSafe();
                if (why != null) { sciStored = true; ChatLog.Write("science", "[science] " + why + " - STORED"); }
                else sciStored = true;   // extending: transmit on the retry once it is open
                return 0;
            }
            int sent = 0;
            foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                if (onlyRerunnable && !e.rerunnable) continue;
                var data = e.GetData(); if (data == null || data.Length == 0) continue;
                double mits = 0, sci = 0;
                foreach (var d in data)
                {
                    mits += d.dataAmount;
                    try { var sub = ResearchAndDevelopment.GetSubjectByID(d.subjectID); if (sub != null) sci += ResearchAndDevelopment.GetScienceValue(d.dataAmount, sub); } catch (Exception) { }
                }
                string id = e.experimentID ?? "?";
                if (tx == null) { sciStored = true; ChatLog.Write("science", "[science] " + id + ": STORED (no usable antenna)"); continue; }
                double need = mits * tx.DataResourceCost;
                if (amt - need < reserve) { sciStored = true; ChatLog.Write("science", "[science] " + id + ": STORED (low EC: need " + need.ToString("0") + " + reserve " + reserve.ToString("0") + ", have " + amt.ToString("0") + ")"); continue; }
                tx.TransmitData(new List<ScienceData>(data));
                foreach (var d in data) e.DumpData(d);
                amt -= need; sent++;
                ChatLog.Write("science", "[science] " + id + ": TRANSMIT " + sci.ToString("0.0") + " sci via " + txName + " (EC need " + need.ToString("0") + "/have " + (amt + need).ToString("0") + ")");
            }
            return sent;
        }

        void ScienceTick()
        {
            float now = Time.realtimeSinceStartup;
            try { RetractIfDone(); } catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] antenna retract: " + ex.Message); }
            if (sciTransmitAt > 0 && now >= sciTransmitAt)
            {
                sciTransmitAt = -1;
                try { int n = TransmitAll(ScienceMode != "auto"); if (n > 0) ChatWindow.Notice("Science: transmitted " + n + " experiment(s)."); if (sciStored && ScienceMode == "auto") sciTransmitAt = now + (sciExtended != null && sciExtended.deployState != ModuleDeployablePart.DeployState.EXTENDED ? 5 : 30); }   // stored for EC/antenna: retry in 30 s
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
            if (mode == "auto" ? ran.Count > 0 : SciencePolicy.ShouldTransmit(true, ran.Count, EcPct())) sciTransmitAt = now + 3;   // AUTO: TransmitAll checks the full EC cost itself (stores + retries when short)
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
