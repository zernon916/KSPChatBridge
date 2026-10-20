// RS2: science inventory - every science part on the vessel (stock, DMagic, StationScience, other mods' experiment subclasses,
// containers, labs, other IScienceDataContainer data holders) and the pure run decision. Game-side scan: NativeRoverScience.cs.
using System;
using System.Globalization;

namespace KSPChatBridge
{
    internal enum ScienceSource { Stock, DMagic, StationScience, ModExperiment, Container, Lab, Holder }

    internal sealed class ScienceItem
    {
        internal string Id = "", Part = "", Type = "";
        internal ScienceSource Source;
        internal bool Rerunnable, Resettable, Inoperable, Available, Held;
        internal double Xmit = 1, ValueHere;
        internal int Stored;
        internal bool IsExperiment { get { return Source == ScienceSource.Stock || Source == ScienceSource.DMagic || Source == ScienceSource.StationScience || Source == ScienceSource.ModExperiment; } }
    }

    internal static class ScienceInventory
    {
        internal static ScienceSource SourceOf(string typeName, string assembly, bool isExperiment, bool isContainer, bool isLab)
        {
            string t = typeName ?? "", a = assembly ?? "";
            if (isLab) return ScienceSource.Lab;
            if (isExperiment)
            {
                if (t == "ModuleScienceExperiment") return ScienceSource.Stock;
                if (t.StartsWith("DM", StringComparison.Ordinal) || a.StartsWith("DMagic", StringComparison.OrdinalIgnoreCase)) return ScienceSource.DMagic;
                if (t == "StationExperiment" || t.StartsWith("StnSci", StringComparison.Ordinal) || a.StartsWith("StationScience", StringComparison.OrdinalIgnoreCase)) return ScienceSource.StationScience;
                return ScienceSource.ModExperiment;
            }
            return isContainer ? ScienceSource.Container : ScienceSource.Holder;
        }

        internal static string SourceName(ScienceSource s)
        {
            switch (s)
            {
                case ScienceSource.Stock: return "stock"; case ScienceSource.DMagic: return "DMagic"; case ScienceSource.StationScience: return "StationScience";
                case ScienceSource.ModExperiment: return "mod"; case ScienceSource.Container: return "container"; case ScienceSource.Lab: return "lab"; default: return "data holder";
            }
        }

        /// <summary>Stock deploy path: only the stock class takes the silent gatherData call; mod subclasses use their own DeployExperiment.</summary>
        internal static bool SilentGather(ScienceSource s) { return s == ScienceSource.Stock; }

        /// <summary>RS2 run decision for one experiment module here: null = RUN, else why not. Runs whenever the subject yields MORE than
        /// minScience (default 0 = any science). bestLater = best value still ahead on the tour for a one-shot that cannot be reset (NaN = unknown).</summary>
        internal static string Decide(ScienceItem e, double minScience, bool ranThisType, bool canReset, double bestLater)
        {
            if (ranThisType) return "spare copy";
            if (e.Inoperable && !canReset) return "used (needs a scientist reset)";
            if (e.Stored > 0) return "holding data";
            if (!e.Available) return "not available here";
            if (!(e.ValueHere > Math.Max(0, double.IsNaN(minScience) ? 0 : minScience))) return "0 sci";
            if (e.Held) return "already on board";
            if (!e.Rerunnable && !canReset && !double.IsNaN(bestLater) && bestLater > e.ValueHere * 1.5) return "one-shot kept for a better spot";
            return null;
        }

        internal static string Line(ScienceItem e, string decision)
        {
            var ci = CultureInfo.InvariantCulture;
            string s = e.Part + " [" + SourceName(e.Source) + "]";
            if (!e.IsExperiment) return s + (e.Stored > 0 ? " holds " + e.Stored + " result(s)" : " empty");
            s += " " + e.Id + ": worth " + e.ValueHere.ToString("0.0", ci) + " here" + (e.Xmit < .999 ? ", transmit " + (100 * e.Xmit).ToString("0", ci) + "%" : "");
            if (!e.Rerunnable) s += ", one-shot"; if (e.Stored > 0) s += ", holds " + e.Stored;
            return s + " - " + (decision == null ? "RUN" : decision);
        }

        /// <summary>RS2: KSC facility (KscRoverMap biome) -> the stock mini-biome name used for science subjects (Vessel.GetLandedAtString).
        /// Unknown = null (fall back to the map biome). Needs an in-game check per experiment (subjects only appear once run).</summary>
        internal static string KscMiniBiome(string facility)
        {
            switch (facility ?? "")
            {
                case "LaunchPad": return "LaunchPad"; case "Runway": return "Runway"; case "VehicleAssemblyBuilding": return "VAB"; case "SpaceplaneHangar": return "SPH";
                case "ResearchAndDevelopment": return "R&D"; case "TrackingStation": return "TrackingStation"; case "AstronautComplex": return "AstronautComplex";
                case "MissionControl": return "MissionControl"; case "Administration": return "Administration"; case "Crawlerway": return "Crawlerway"; case "KSC": return "KSC";
                default: return null;
            }
        }
    }
}