// RS7h: KSP glue for the rover auto-EVA science (RoverEva.cs is the tested logic). After a tour stop's on-board experiments: one crew
// member steps out of their own hatch (FlightEVA.spawnEVA), runs the kerbal's EVA report / surface sample where they stand (data kept on
// the kerbal), the data is moved into the rover's science container and the kerbal boards the same part (KerbalEVA.BoardPart). While the
// kerbal is outside the controller stays bound to the rover; on any failure the rover stays parked with a tablet alert until the kerbal
// is back aboard (or Resume once nobody is outside).
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        RoverEvaRun roverEva; Vessel evaVessel; Part evaPart; ProtoCrewMember evaCrew; int evaStopN; bool evaWantSample, evaWantReport, evaWantEvaSci, evaSciPending;
        ModuleScienceExperiment evaSciModule; const double EvaSciHoldS = 25;   // RS7p: EVA Science animation (golf etc.) before its data exists
        const string EvaKit = "evaScienceKit";

        /// <summary>RS7p: this crew member carries the EVA science kit (cargo inventory, KSP 1.11+).</summary>
        static bool HasEvaKit(ProtoCrewMember pcm)
        {
            if (pcm == null) return false;
            try { var inv = pcm.KerbalInventoryModule; if (inv != null) return inv.ContainsPart(EvaKit); } catch (Exception) { }
            try { var n = pcm.InventoryNode; return n != null && n.ToString().IndexOf(EvaKit, StringComparison.OrdinalIgnoreCase) >= 0; } catch (Exception) { return false; }
        }
        /// <summary>RS7p: a kit carrier sits in a cabin with a hatch (auto-EVA can take them out).</summary>
        bool EvaKitCarrierAboard()
        {
            if (vessel == null) return false;
            foreach (Part p in vessel.parts)
                if (p.CrewCapacity > 0 && p.airlock != null && p.protoModuleCrew != null)
                    foreach (var pcm in p.protoModuleCrew) if (HasEvaKit(pcm)) return true;
            return false;
        }
        const string EvaAlert = "EVA science: board your Kerbal manually, then Resume";

        bool RoverEvaBusy { get { return roverEva != null && roverEva.Busy; } }
        internal static bool RoverEvaWaiting { get { var i = instance; return i != null && i.roverEva != null && i.roverEva.Phase == EvaPhase.Manual; } }

        /// <summary>Keep the controller on the rover while our kerbal is the active vessel (Bind would otherwise stop the tour).</summary>
        bool RoverEvaHoldsBind(Vessel active)
        {
            if (!RoverEvaBusy || active == null || vessel == null || active == vessel) return false;
            return active == evaVessel || (active.isEVA && evaCrew != null && active.GetVesselCrew().Contains(evaCrew));
        }

        bool SampleAllowed()
        {
            try
            {
                if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.CAREER) return true;
                return ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.ResearchAndDevelopment) > 0;   // surface samples need R&D level 2
            }
            catch (Exception) { return true; }
        }

        double EvaGain(string expId, HashSet<string> stored, string biome)
        {
            try
            {
                var s = ScienceAt(expId, ExperimentSituations.SrfLanded, vessel.mainBody, vessel.latitude, vessel.longitude, biome);
                if (s.Experiment == null || !s.Available || s.SubjectId.Length == 0 || stored.Contains(s.SubjectId)) return 0;
                return s.Value > RoverMinScience() ? s.Value : 0;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>Called after a stop's science: start an EVA when it is worth it. True = the tour waits for it.</summary>
        bool RoverEvaBegin(int stopN)
        {
            if (!RoverMode || RoverSetting(CraftOverrides.RoverAutoEva) < .5 || vessel == null || !vessel.LandedOrSplashed) return false;
            if (vessel.mainBody == null || !vessel.mainBody.isHomeWorld && !EvaPossibleHere()) return false;
            string biome = ScienceBiomeHere(); var stored = StoredSubjectIds();
            double sample = EvaGain("surfaceSample", stored, biome), report = EvaGain("evaReport", stored, biome);
            var cands = new List<KeyValuePair<Part, ProtoCrewMember>>(); var traits = new List<string>(); var kits = new List<bool>();
            foreach (Part p in vessel.parts)
                if (p.CrewCapacity > 0 && p.airlock != null && p.protoModuleCrew != null)
                    foreach (var pcm in p.protoModuleCrew) { cands.Add(new KeyValuePair<Part, ProtoCrewMember>(p, pcm)); traits.Add(pcm.trait); kits.Add(HasEvaKit(pcm)); }
            bool sampleOk = SampleAllowed(), kitAboard = kits.Contains(true);
            double evaSci = kitAboard ? EvaGain("evaScience", stored, biome) : 0;   // RS7p
            if (!RoverEvaPolicy.ShouldEva(true, cands.Count, sample, report, sampleOk, evaSci, kitAboard))
            {
                if ((sample > 0 || report > 0) && cands.Count == 0) ChatLog.Write("rover", "eva stop " + stopN + ": skipped (nobody in a cabin with a hatch)");
                return false;
            }
            int pi = RoverEvaPolicy.PickCrew(traits, kits, evaSci > 0); var pick = cands[pi];
            evaPart = pick.Key; evaCrew = pick.Value; evaVessel = null; evaStopN = stopN; evaWantSample = sampleOk && sample > 0; evaWantReport = report > 0;
            evaWantEvaSci = evaSci > 0 && kits[pi]; evaSciPending = false; evaSciModule = null;
            SetGroup(vessel, KSPActionGroup.Brakes, true);
            roverEva = new RoverEvaRun(); bool ok = false; string why = "";
            try { ok = FlightEVA.fetch != null && FlightEVA.fetch.spawnEVA(evaCrew, evaPart, evaPart.airlock, true); if (!ok) why = "hatch obstructed"; }
            catch (Exception ex) { why = ex.Message; }
            roverEva.Spawned(Time.realtimeSinceStartup, ok, why);
            ChatLog.Write("rover", "eva stop " + stopN + ": " + (ok ? "spawn ok (" + evaCrew.name + ", " + evaCrew.trait + ", from " + evaPart.partInfo.title + "; sample " + sample.ToString("0.0", CultureInfo.InvariantCulture) + ", report " + report.ToString("0.0", CultureInfo.InvariantCulture) + ", evaScience " + (evaWantEvaSci ? evaSci.ToString("0.0", CultureInfo.InvariantCulture) : kitAboard ? "0" : "no kit") + ")" : "spawn fail " + why + " - EVA science skipped"));
            if (!ok) { ChatWindow.Notice("EVA science skipped at this stop: " + why + "."); roverEva = null; return false; }
            return true;
        }

        bool EvaPossibleHere()
        {
            try { return GameVariables.Instance.UnlockedEVA(ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.AstronautComplex)); } catch (Exception) { return true; }
        }

        Vessel FindEvaVessel()
        {
            if (evaCrew == null) return null;
            foreach (Vessel v in FlightGlobals.Vessels) if (v != null && v.isEVA && v.loaded && v.GetVesselCrew().Contains(evaCrew)) return v;
            return null;
        }

        void RoverEvaTick()
        {
            float now = Time.realtimeSinceStartup;
            if (evaVessel == null || evaVessel.state == Vessel.State.DEAD) evaVessel = FindEvaVessel();
            bool exists = evaVessel != null && evaVessel.loaded;
            bool settled = exists && !evaVessel.packed && evaVessel.LandedOrSplashed && evaVessel.srfSpeed < .5;
            double drift = exists && evaPart != null ? Vector3d.Distance(evaVessel.CoMD, evaPart.transform.position) : 0;
            bool aboard = vessel != null && evaCrew != null && vessel.GetVesselCrew().Contains(evaCrew);
            if (vessel != null) SetGroup(vessel, KSPActionGroup.Brakes, true);
            if (evaSciPending && evaSciModule != null && SciCount(evaSciModule) > 0)
            { evaSciPending = false; ChatLog.Write("rover", "eva stop " + evaStopN + ": evaScience ok"); roverEva.DataReady(now); }   // RS7p
            switch (roverEva.Tick(now, exists, settled, drift, aboard))
            {
                case EvaAction.RunScience: RoverEvaScience(); roverEva.ScienceDone(now, evaSciPending ? EvaSciHoldS : 0); break;
                case EvaAction.TryBoard:
                    if (evaSciPending) { evaSciPending = false; ChatLog.Write("rover", "eva stop " + evaStopN + ": evaScience skip no data after " + EvaSciHoldS.ToString("0", CultureInfo.InvariantCulture) + " s (no valid EVA situation here?)"); }
                    RoverEvaBoard(); break;
                case EvaAction.Alert:
                    ChatLog.Write("rover", "eva stop " + evaStopN + ": boarded fail " + roverEva.Why + " - rover parked, waiting for the kerbal");
                    RoverAlert = EvaAlert + " (" + roverEva.Why + ")."; ChatWindow.Notice(RoverAlert); break;
                case EvaAction.Finish: RoverEvaFinish(); break;
            }
        }

        void RoverEvaScience()
        {
            if (evaVessel == null) { ChatLog.Write("rover", "eva stop " + evaStopN + ": no kerbal for the science"); return; }
            bool sciSeen = false;
            foreach (var e in evaVessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                if (e.experimentID == "evaScience" && evaWantEvaSci)
                {   // RS7p: the kerbal's own module (kit in their inventory); never a deployable ground experiment
                    sciSeen = true; string why = null;
                    if (e.Inoperable) why = "inoperable"; else if (SciCount(e) > 0) why = "already holds data";
                    else if (e.requiresInventoryPart) { var inv = evaVessel.FindPartModuleImplementing<ModuleInventoryPart>(); if (inv == null || !inv.ContainsPart(e.requiredInventoryPart)) why = "no " + e.requiredInventoryPart + " in the kerbal's inventory"; }
                    if (why != null) { ChatLog.Write("rover", "eva stop " + evaStopN + ": evaScience skip " + why); continue; }
                    try { DeploySilent(e); evaSciModule = e; evaSciPending = true; ChatLog.Write("rover", "eva stop " + evaStopN + ": evaScience started (waiting up to " + EvaSciHoldS.ToString("0", CultureInfo.InvariantCulture) + " s for its data)"); }
                    catch (Exception ex) { ChatLog.Write("rover", "eva stop " + evaStopN + ": evaScience skip " + ex.Message); }
                    continue;
                }
                bool want = (e.experimentID == "surfaceSample" && evaWantSample) || (e.experimentID == "evaReport" && evaWantReport);
                if (!want) continue;
                string tag = e.experimentID == "surfaceSample" ? "sample" : "report";
                if (e.Inoperable || SciCount(e) > 0) { ChatLog.Write("rover", "eva stop " + evaStopN + ": " + tag + " skipped (" + (e.Inoperable ? "inoperable" : "already holds data") + ")"); continue; }
                try { DeploySilent(e); ChatLog.Write("rover", "eva stop " + evaStopN + ": " + tag + " ok"); }
                catch (Exception ex) { ChatLog.Write("rover", "eva stop " + evaStopN + ": " + tag + " fail " + ex.Message); }
            }
            if (evaWantEvaSci && !sciSeen) ChatLog.Write("rover", "eva stop " + evaStopN + ": evaScience skip the kerbal has no EVA Science module (ground experiments are not deployed)");
        }

        void RoverEvaBoard()
        {
            if (evaVessel == null || evaPart == null) return;
            try
            {   // move the EVA data into the rover first (stock boarding does too; this way nothing rides on it)
                var cont = vessel.FindPartModuleImplementing<ModuleScienceContainer>(); var src = new List<IScienceDataContainer>();
                foreach (var m in evaVessel.FindPartModulesImplementing<IScienceDataContainer>()) if (m.GetScienceCount() > 0) src.Add(m);
                foreach (var m in src)   // RS7n/RS7p: silent-gathered EVA data has transmitBonus 0 -> fix before it moves (transmits at full value later)
                {
                    var me = m as ModuleScienceExperiment; float x = me != null ? me.xmitDataScalar : 1f;
                    foreach (var d in SciData(m)) if (d != null) SciencePolicy.FixTransmit(ref d.baseTransmitValue, ref d.transmitBonus, x);
                }
                if (cont != null && src.Count > 0) cont.StoreData(src, false);
            }
            catch (Exception ex) { ChatLog.Write("rover", "eva stop " + evaStopN + ": data move " + ex.Message + " (boarding carries it)"); }
            try { var k = evaVessel.FindPartModuleImplementing<KerbalEVA>(); if (k != null) k.BoardPart(evaPart); }
            catch (Exception ex) { ChatLog.Write("rover", "eva stop " + evaStopN + ": board try " + roverEva.BoardAttempts + " " + ex.Message); }
        }

        void RoverEvaFinish()
        {
            ChatLog.Write("rover", "eva stop " + evaStopN + ": boarded ok (" + (evaCrew != null ? evaCrew.name : "?") + ")");
            if (RoverAlert.StartsWith(EvaAlert, StringComparison.Ordinal)) RoverAlert = "";
            try { if (vessel != null && FlightGlobals.ActiveVessel != vessel) FlightGlobals.SetActiveVessel(vessel); } catch (Exception) { }
            roverEva = null; evaVessel = null; checklistDirty = true; sciTransmitAt = Time.realtimeSinceStartup + 2;
        }

        /// <summary>Tablet RESUME while waiting: only once nobody from this EVA is outside.</summary>
        internal static string RoverEvaResume()
        {
            var i = instance; if (i == null || i.roverEva == null) return "";
            if (i.evaVessel == null || i.evaVessel.state == Vessel.State.DEAD) i.evaVessel = i.FindEvaVessel();
            if (!i.roverEva.Resume(i.evaVessel != null)) return "Your Kerbal is still outside - board them first.";
            ChatLog.Write("rover", "eva stop " + i.evaStopN + ": resumed by the player");
            if (RoverAlert.StartsWith(EvaAlert, StringComparison.Ordinal)) RoverAlert = "";
            i.roverEva = null; i.evaVessel = null; i.checklistDirty = true; return "Tour resumed.";
        }
    }
}