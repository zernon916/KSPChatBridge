using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        private DvTracker dvTracker;
        private double lastMassForDv;
        private int lastLegForDv = -1;

        void DvTrackStep(Vessel v)
        {
            if (v == null) return;
            if (v.ctrlState == null) return;
            if (v.ctrlState.mainThrottle <= 0) return;

            double currentMass = v.totalMass;
            if (double.IsNaN(currentMass) || double.IsInfinity(currentMass) || currentMass <= 0) return;

            double currentIsp = 0;
            bool hasEngine = false;
            int engineCount = 0;
            foreach (Part p in v.Parts)
            {
                if (p == null) continue;
                foreach (PartModule m in p.Modules)
                {
                    var engine = m as ModuleEngines;
                    if (engine == null) continue;
                    if (!engine.EngineIgnited) continue;
                    if (engine.flameout) continue;
                    if (engine.finalThrust <= 0) continue;
                    hasEngine = true;
                    currentIsp += engine.realIsp;
                    engineCount++;
                }
            }
            if (!hasEngine) return;
            if (engineCount > 0) currentIsp /= engineCount;
            if (currentIsp <= 0) return;

            if (dvTracker == null)
            {
                var mission = MissionStore.Load(v.vesselName);
                if (mission == null || mission.Legs == null || mission.Legs.Count == 0) return;
                var planned = new List<double>();
                foreach (var leg in mission.Legs)
                {
                    if (leg != null) planned.Add(leg.PlannedDv);
                }
                if (planned.Count == 0) return;
                dvTracker = new DvTracker(planned);
                lastMassForDv = currentMass;
                lastLegForDv = 0;
                return;
            }

            int currentLeg = lastLegForDv;
            if (currentLeg < 0 || currentLeg >= dvTracker.LegCount) return;

            double dvIncrement = DvTracker.DvFromMassChange(lastMassForDv, currentMass, currentIsp);
            if (double.IsNaN(dvIncrement) || dvIncrement <= 0)
            {
                lastMassForDv = currentMass;
                return;
            }

            dvTracker.AddSample(currentLeg, dvIncrement);
            lastMassForDv = currentMass;

            if (dvTracker.IsBigDifference(currentLeg) && !dvTracker.AlreadyFlagged(currentLeg))
            {
                dvTracker.FlagBigDifference(currentLeg);
                double planned = dvTracker.Planned(currentLeg);
                double used = dvTracker.Used(currentLeg);
                double percent = dvTracker.PercentOverUnder(currentLeg);
                Debug.Log("[AICS] dv leg " + currentLeg + ": used " + used.ToString("F1") + " m/s vs planned " + planned.ToString("F1") + " m/s (" + percent.ToString("F1") + "% over)");
            }
        }
    }
}
