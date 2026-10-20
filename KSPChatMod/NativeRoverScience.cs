// RS2-RS4 game side: science inventory scan (all science parts, worth-here values with the KSC mini-biome), rover science settings.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class NativeFlightController
    {
        /// <summary>RS2: run an experiment when it yields more than this (CRAFT rover_min_science; default 0 = any science).</summary>
        static double RoverMinScience() { double v = CraftOverrides.L(CraftOverrides.RoverMinScience); return double.IsNaN(v) ? 0 : Math.Max(0, v); }

        /// <summary>Science results held on board (experiments + containers).</summary>
        int HeldDataCount() { int n = 0; foreach (var h in vessel.FindPartModulesImplementing<IScienceDataContainer>()) n += h.GetScienceCount(); return n; }

        bool HasScientist() { foreach (var cm in vessel.GetVesselCrew()) if (cm.trait == "Scientist") return true; return false; }

        /// <summary>RS2: every science module on the vessel with its value here (subject from the mini-biome when landed on one).</summary>
        List<ScienceItem> ScienceInventoryScan()
        {
            var r = new List<ScienceItem>(); if (vessel == null || vessel.mainBody == null) return r;
            var sit = ScienceUtil.GetExperimentSituation(vessel); string biome = ScienceBiomeHere(); var stored = StoredSubjectIds();
            foreach (Part p in vessel.parts)
                foreach (PartModule m in p.Modules)
                {
                    var e = m as ModuleScienceExperiment; var c = m as ModuleScienceContainer; var lab = m as ModuleScienceLab; var h = m as IScienceDataContainer;
                    if (e == null && c == null && lab == null && h == null) continue;
                    var it = new ScienceItem { Part = p.partInfo != null ? p.partInfo.title : p.name, Type = m.GetType().Name };
                    it.Source = ScienceInventory.SourceOf(it.Type, m.GetType().Assembly.GetName().Name, e != null, c != null, lab != null);
                    try
                    {
                        if (e != null)
                        {
                            it.Id = e.experimentID ?? ""; it.Rerunnable = e.rerunnable; it.Resettable = e.resettable; it.Inoperable = e.Inoperable; it.Xmit = e.xmitDataScalar; it.Stored = e.GetScienceCount();
                            var s = ScienceAt(it.Id, sit, vessel.mainBody, vessel.latitude, vessel.longitude, biome);
                            it.Available = s.Available; it.ValueHere = s.Value; it.Held = s.SubjectId.Length > 0 && stored.Contains(s.SubjectId);
                        }
                        else if (h != null) it.Stored = h.GetScienceCount();
                    }
                    catch (Exception ex) { it.Type += " (" + ex.Message + ")"; }
                    r.Add(it);
                }
            return r;
        }

        static List<string> sciInvLines = new List<string>(); static float sciInvAt = -99;
        /// <summary>RS2: tablet SCIENCE TOUR list (rebuilt at most once a second - the GUI asks every frame).</summary>
        internal static List<string> ScienceInventoryLines()
        {
            var i = instance; if (!CraftReady) return new List<string> { "No active vessel." };
            if (Time.realtimeSinceStartup - sciInvAt < 1) return sciInvLines;
            sciInvAt = Time.realtimeSinceStartup; var r = new List<string>();
            try
            {
                var items = i.ScienceInventoryScan(); bool sci = i.HasScientist(); var ran = new HashSet<string>();
                r.Add("Here: " + i.vessel.mainBody.bodyName + " / " + i.ScienceBiomeHere() + " / " + ScienceUtil.GetExperimentSituation(i.vessel) + " | min " + RoverMinScience().ToString("0.0", CultureInfo.InvariantCulture) + " sci | " + i.RoverSciencePolicyText());
                foreach (var it in items)
                {
                    string d = it.IsExperiment ? ScienceInventory.Decide(it, RoverMinScience(), ran.Contains(it.Id), sci && it.Resettable, double.NaN) : null;
                    if (it.IsExperiment && d == null) ran.Add(it.Id);
                    r.Add(ScienceInventory.Line(it, d));
                }
                if (items.Count == 0) r.Add("No science parts on this vessel.");
            }
            catch (Exception ex) { r.Add("Science scan failed: " + ex.Message); }
            sciInvLines = r; return r;
        }

        /// <summary>RS4 fills this in (transmit / who-runs-science policy summary).</summary>
        string RoverSciencePolicyText() { return "transmit " + CraftOverrides.Find(CraftOverrides.RoverTransmit).Choices[RoverTransmitChoice()]; }
        static int RoverTransmitChoice() { double v = CraftOverrides.L(CraftOverrides.RoverTransmit); return double.IsNaN(v) ? 2 : Math.Max(0, Math.Min(3, (int)Math.Round(v))); }
    }
}