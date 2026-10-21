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
        RoverEvaRun roverEva; Vessel evaVessel; Part evaPart; ProtoCrewMember evaCrew; int evaStopN; bool evaWantSample, evaWantReport;
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
            var cands = new List<KeyValuePair<Part, ProtoCrewMember>>(); var traits = new List<string>();
            foreach (Part p in vessel.parts)
                if (p.CrewCapacity > 0 && p.airlock != null && p.protoModuleCrew != null)
                    foreach (var pcm in p.protoModuleCrew) { cands.Add(new KeyValuePair<Part, ProtoCrewMember>(p, pcm)); traits.Add(pcm.trait); }
            bool sampleOk = SampleAllowed();
            if (!RoverEvaPolicy.ShouldEva(true, cands.Count, sample, report, sampleOk))
            {
                if ((sample > 0 || report > 0) && cands.Count == 0) ChatLog.Write("rover", "eva stop " + stopN + ": skipped (nobody in a cabin with a hatch)");
                return false;
            }
            var pick = cands[RoverEvaPolicy.PickCrew(traits)];
            evaPart = pick.Key; evaCrew = pick.Value; evaVessel = null; evaStopN = stopN; evaWantSample = sampleOk && sample > 0; evaWantReport = report > 0;
            SetGroup(vessel, KSPActionGroup.Brakes, true);
            roverEva = new RoverEvaRun(); bool ok = false; string why = "";
            try { ok = FlightEVA.fetch != null && FlightEVA.fetch.spawnEVA(evaCrew, evaPart, evaPart.airlock, true); if (!ok) why = "hatch obstructed"; }
            catch (Exception ex) { why = ex.Message; }
            roverEva.Spawned(Time.realtimeSinceStartup, ok, why);
            ChatLog.Write("rover", "eva stop " + stopN + ": " + (ok ? "spawn ok (" + evaCrew.name + ", " + evaCrew.trait + ", from " + evaPart.partInfo.title + "; sample " + sample.ToString("0.0", CultureInfo.InvariantCulture) + ", report " + report.ToString("0.0", CultureInfo.InvariantCulture) + ")" : "spawn fail " + why + " - EVA science skipped"));
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
            switch (roverEva.Tick(now, exists, settled, drift, aboard))
            {
                case EvaAction.RunScience: RoverEvaScience(); roverEva.ScienceDone(now); break;
                case EvaAction.TryBoard: RoverEvaBoard(); break;
                case EvaAction.Alert:
                    ChatLog.Write("rover", "eva stop " + evaStopN + ": boarded fail " + roverEva.Why + " - rover parked, waiting for the kerbal");
                    RoverAlert = EvaAlert + " (" + roverEva.Why + ")."; ChatWindow.Notice(RoverAlert); break;
                case EvaAction.Finish: RoverEvaFinish(); break;
            }
        }

        void RoverEvaScience()
        {
            if (evaVessel == null) { ChatLog.Write("rover", "eva stop " + evaStopN + ": no kerbal for the science"); return; }
            foreach (var e in evaVessel.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                bool want = (e.experimentID == "surfaceSample" && evaWantSample) || (e.experimentID == "evaReport" && evaWantReport);
                if (!want) continue;
                string tag = e.experimentID == "surfaceSample" ? "sample" : "report";
                if (e.Inoperable || SciCount(e) > 0) { ChatLog.Write("rover", "eva stop " + evaStopN + ": " + tag + " skipped (" + (e.Inoperable ? "inoperable" : "already holds data") + ")"); continue; }
                try { DeploySilent(e); ChatLog.Write("rover", "eva stop " + evaStopN + ": " + tag + " ok"); }
                catch (Exception ex) { ChatLog.Write("rover", "eva stop " + evaStopN + ": " + tag + " fail " + ex.Message); }
            }
        }

        void RoverEvaBoard()
        {
            if (evaVessel == null || evaPart == null) return;
            try
            {   // move the EVA data into the rover first (stock boarding does too; this way nothing rides on it)
                var cont = vessel.FindPartModuleImplementing<ModuleScienceContainer>(); var src = new List<IScienceDataContainer>();
                foreach (var m in evaVessel.FindPartModulesImplementing<IScienceDataContainer>()) if (m.GetScienceCount() > 0) src.Add(m);
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