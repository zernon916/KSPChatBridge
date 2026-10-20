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

        List<string> RunExperiments(bool onlyRerunnable, out string blocker, double minValue = double.NaN)
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
                var science = new AvailableScience { Experiment = exp };
                if (exp != null && body != null)
                {
                    try
                    {
                        science = ScienceAt(expId, sit, body, vessel.latitude, vessel.longitude, ScienceBiomeHere());   // RS2: KSC mini-biome (landedAt) like stock gatherData
                    }
                    catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] science value: " + ex.Message); }
                }
                string biome = science.Biome;
                double value = science.Value; string subjectId = science.SubjectId;
                bool storedOnboard = subjectId.Length > 0 && stored.Contains(subjectId);
                bool available = science.Available;
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
                        string skip = SciencePolicy.AutoSkipReason(e.Inoperable, e.GetScienceCount() > 0, available, storedOnboard, value, ranThisType, onlyRerunnable, e.rerunnable, minValue);
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

        double ScienceValueAt(double latitude, double longitude, out int experimentCount, double minValue = double.NaN, string biomeName = null)
        {
            experimentCount = 0;
            if (vessel == null || vessel.mainBody == null || SciencePolicy.RunBlocker(EcPct()) != null) return 0;
            var sit = ScienceUtil.GetExperimentSituation(vessel);
            var body = vessel.mainBody;
            var groups = new Dictionary<string, List<ModuleScienceExperiment>>();
            foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                string id = e.experimentID ?? "";
                List<ModuleScienceExperiment> group;
                if (!groups.TryGetValue(id, out group)) groups[id] = group = new List<ModuleScienceExperiment>();
                group.Add(e);
            }
            var stored = StoredSubjectIds();
            bool scientist = false; foreach (var crewMember in vessel.GetVesselCrew()) scientist |= crewMember.trait == "Scientist";
            int crew = vessel.GetCrewCount(); bool controllable = vessel.IsControllable;
            bool inAtmo = body.atmosphere && vessel.altitude < body.atmosphereDepth;
            double totalValue = 0;
            foreach (var group in groups)
            {
                AvailableScience experiment;
                try { experiment = ScienceAt(group.Key, sit, body, latitude, longitude, biomeName); }
                catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] science route estimate: " + ex.Message); continue; }
                if (experiment.Experiment == null) continue;
                bool storedOnboard = experiment.SubjectId.Length > 0 && stored.Contains(experiment.SubjectId);
                foreach (ModuleScienceExperiment module in group.Value)
                {
                    if (!SciencePolicy.CanRun(module.usageReqMaskInternal, crew, module.part.protoModuleCrew.Count,
                        scientist, controllable, experiment.Experiment.requireAtmosphere, inAtmo)) continue;
                    string skip = SciencePolicy.AutoSkipReason(module.Inoperable, module.GetScienceCount() > 0,
                        experiment.Available, storedOnboard, experiment.Value, false, false, module.rerunnable, minValue);
                    if (skip != null) continue;
                    totalValue += experiment.Value;
                    experimentCount++;
                    break;
                }
            }
            return totalValue;
        }

        sealed class AvailableScience
        {
            internal ScienceExperiment Experiment;
            internal string Biome = "";
            internal string SubjectId = "";
            internal double Value;
            internal bool Available;
        }

        static AvailableScience ScienceAt(string experimentId, ExperimentSituations situation, CelestialBody body, double latitude, double longitude, string biomeName = null)
        {
            var result = new AvailableScience();
            result.Experiment = ResearchAndDevelopment.GetExperiment(experimentId);
            if (result.Experiment == null) return result;
            result.Available = result.Experiment.IsAvailableWhile(situation, body);
            try
            {
                result.Biome = result.Experiment.BiomeIsRelevantWhile(situation) ? (string.IsNullOrEmpty(biomeName) ? ScienceUtil.GetExperimentBiome(body, latitude, longitude) : biomeName) : "";
                var subject = ResearchAndDevelopment.GetExperimentSubject(result.Experiment, situation, body, result.Biome, result.Biome);
                if (subject != null)
                {
                    result.SubjectId = subject.id;
                    result.Value = ResearchAndDevelopment.GetScienceValue(result.Experiment.baseValue * result.Experiment.dataScale, subject);
                }
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] science value: " + ex.Message); }
            return result;
        }

        /// <summary>Run an experiment WITHOUT the stock ExperimentsResultDialog: ModuleScienceExperiment.gatherData(silent: true)
        /// via reflection; fallback = DeployExperiment and close the dialog a moment later.</summary>
        void DeploySilent(ModuleScienceExperiment e)
        {
            if (e.GetType() != typeof(ModuleScienceExperiment))
            {   // RS2: DMagic / StationScience / other mod subclasses run their own DeployExperiment (animations, custom data)
                ChatLog.Write("science", "[science] " + e.experimentID + ": " + e.GetType().Name + " DeployExperiment (mod experiment)"); e.DeployExperiment(); StartCoroutine(CloseResultsDialog()); return;
            }
            try
            {
                var m = typeof(ModuleScienceExperiment).GetMethod("gatherData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
                var it = m != null ? m.Invoke(e, new object[] { false }) as System.Collections.IEnumerator : null;   // Oct 12 23:53: the bool is showDialog (ILSpy, KSP 1.12), NOT silent - true popped the dialog
                if (it != null) { e.StartCoroutine(it); ChatLog.Write("science", "[science] " + e.experimentID + ": silent gather (gatherData showDialog=false)"); return; }
            }
            catch (Exception ex) { Debug.LogWarning("[KSPChatBridge] silent science failed, using stock deploy: " + ex.Message); }
            ChatLog.Write("science", "[science] " + e.experimentID + ": stock deploy + dialog dismiss (silent gather unavailable)"); e.DeployExperiment(); StartCoroutine(CloseResultsDialog());
        }
        System.Collections.IEnumerator CloseResultsDialog()
        {
            for (int i = 0; i < 300; i++)
            {
                yield return null;   // every frame: close it the moment it appears
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
        internal const double EcReserveFrac = .10, EcReserveMin = 5, AntennaMaxQ = 1.0, AntennaUnsafeGapS = 60;   // kPa: stock deployables (antennas AND solar panels, QWEN T2) break in the airstream; s: SYSTEM comms gap for the unsafe warning
        ModuleDeployableAntenna sciExtended;   // antenna AUTO science opened (retract after the transmission)
        float sciAntennaUnsafeAt = -999;   // Luke Oct 13 00:09: last SYSTEM post of the antenna-unsafe warning (max once per AntennaUnsafeGapS)
        bool HasData(bool onlyRerunnable) { foreach (var e in vessel.FindPartModulesImplementing<ModuleScienceExperiment>()) if ((!onlyRerunnable || e.rerunnable) && e.GetScienceCount() > 0) return true; return false; }
        /// <summary>Luke Oct 13 00:09: the speed that keeps q at or below AntennaMaxQ (KSP: q kPa = 0.0005*rho*v^2), i.e.
        /// sqrt(2*1000/rho) m/s. No air (or bad reading) -> infinity: any speed is safe out of the atmosphere.</summary>
        static double AntennaSafeSpeed(double atmDensity) { return atmDensity > 1e-6 && !double.IsNaN(atmDensity) ? Math.Sqrt(2 * AntennaMaxQ * 1000 / atmDensity) : double.PositiveInfinity; }
        /// <summary>Luke Oct 13 00:09: the airstream refusal the pilot can act on - "Antenna unsafe: slow below N m/s (now M m/s) or climb".</summary>
        string AntennaUnsafeText()
        {
            double safe = AntennaSafeSpeed(vessel.atmDensity), now = vessel.srfSpeed;
            if (double.IsInfinity(safe)) return "Antenna unsafe: climb out of the atmosphere";
            return "Antenna unsafe: slow below " + safe.ToString("0") + " m/s (now " + now.ToString("0") + " m/s) or climb";
        }
        /// <summary>Luke Oct 13 00:09: the same warning on SYSTEM comms, at most once per AntennaUnsafeGapS (the transmit retry runs every 5-30 s).</summary>
        void PostAntennaUnsafe(string text)
        {
            float t = Time.realtimeSinceStartup;
            if (t - sciAntennaUnsafeAt < (float)AntennaUnsafeGapS && t >= sciAntennaUnsafeAt) return;   // throttled: one comms line per minute
            sciAntennaUnsafeAt = t;
            ChatWindow.Notice("[SYSTEM] " + text);
            ChatLog.Write("science", "[science] SYSTEM " + text + " (q " + vessel.dynamicPressurekPa.ToString("0.00") + " kPa, rho " + vessel.atmDensity.ToString("0.000") + ", alt " + vessel.altitude.ToString("0") + " m)");
        }
        /// <summary>QWEN T2: the one deployable rule, shared by the science antenna and the solar panels: null = the
        /// part may go out (or stay out), else why not. A closed cargo bay shields the part from the airstream and
        /// reentry heat is its own reason to stay stowed; "airstream" is the only reason with a speed the pilot can
        /// fly (see AntennaSafeSpeed).</summary>
        internal static string DeployableUnsafe(Part part, bool isBreakable, double qKPa, bool inAtmo)
        {
            if (part == null) return "missing part";
            if (part.ShieldedFromAirstream) return "shielded/in a closed bay";
            if (part.temperature > .5 * part.maxTemp || part.skinTemperature > .5 * part.skinMaxTemp) return "hot (reentry heating)";
            if (inAtmo && isBreakable && qKPa > AntennaMaxQ) return "airstream";
            return null;
        }
        /// <summary>Extend a retracted deployable antenna if safe; null = extending, else why not.</summary>
        string ExtendAntennaIfSafe()
        {
            double q = vessel.dynamicPressurekPa; bool inAtmo = vessel.mainBody.atmosphere && vessel.altitude < vessel.mainBody.atmosphereDepth;
            ModuleDeployableAntenna pick = null; string unsafeWhy = null, streamTitle = null;
            foreach (var a in vessel.FindPartModulesImplementing<ModuleDeployableAntenna>())
            {
                if (a.deployState != ModuleDeployablePart.DeployState.RETRACTED || a.part.FindModuleImplementing<IScienceDataTransmitter>() == null) continue;
                string w = DeployableUnsafe(a.part, a.isBreakable, q, inAtmo);   // QWEN T2: the same rule solar panels use
                if (w == "airstream" && streamTitle == null) streamTitle = a.part.partInfo.title;
                if (w != null) { unsafeWhy = a.part.partInfo.title + " unsafe: " + w; continue; }
                pick = a; break;
            }
            if (pick == null)
            {
                if (unsafeWhy == null) return "no usable antenna";
                if (streamTitle != null)
                {   // Luke Oct 13 00:09: "airstream (q 4.20 kPa)" told the pilot nothing to do - report the speed that IS safe, and say it on SYSTEM comms (max once per 60 s)
                    string text = AntennaUnsafeText(); PostAntennaUnsafe(text);
                    return text + " (" + streamTitle + ", q " + q.ToString("0.00") + " kPa)";
                }
                return "antenna " + unsafeWhy + " (q " + q.ToString("0.00") + " kPa)";
            }
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
                if (RoverMode && !RoverTransmitOk(e, data)) continue;   // RS4: CRAFT transmit policy (kept on board, no retry loop)
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
