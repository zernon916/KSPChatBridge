using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class DvTracker
    {
        private readonly List<double> plannedDv;
        private readonly List<double> usedDv;
        private readonly double bigDifferenceThreshold;
        private readonly List<bool> bigDifferenceFlagged;

        public DvTracker(List<double> plannedDvPerLeg, double bigDifferencePercent = 10.0)
        {
            if (plannedDvPerLeg == null) throw new ArgumentNullException(nameof(plannedDvPerLeg));
            if (bigDifferencePercent <= 0) throw new ArgumentException("Threshold must be positive");
            plannedDv = new List<double>(plannedDvPerLeg);
            usedDv = new List<double>();
            bigDifferenceThreshold = bigDifferencePercent / 100.0;
            bigDifferenceFlagged = new List<bool>();
            foreach (double dv in plannedDv)
            {
                usedDv.Add(0.0);
                bigDifferenceFlagged.Add(false);
            }
        }

        public int LegCount { get { return plannedDv.Count; } }

        public double Planned(int leg)
        {
            if (leg < 0 || leg >= plannedDv.Count) return double.NaN;
            return plannedDv[leg];
        }

        public double Used(int leg)
        {
            if (leg < 0 || leg >= usedDv.Count) return double.NaN;
            return usedDv[leg];
        }

        public double Remaining(int leg)
        {
            if (leg < 0 || leg >= plannedDv.Count) return double.NaN;
            return plannedDv[leg] - usedDv[leg];
        }

        public double PercentOverUnder(int leg)
        {
            if (leg < 0 || leg >= plannedDv.Count) return double.NaN;
            if (plannedDv[leg] <= 0) return double.NaN;
            if (usedDv[leg] == 0) return -100.0;
            return (usedDv[leg] - plannedDv[leg]) / plannedDv[leg] * 100.0;
        }

        public bool IsBigDifference(int leg)
        {
            if (leg < 0 || leg >= plannedDv.Count) return false;
            if (plannedDv[leg] <= 0) return false;
            return usedDv[leg] > plannedDv[leg] * (1.0 + bigDifferenceThreshold);
        }

        public bool AlreadyFlagged(int leg)
        {
            if (leg < 0 || leg >= bigDifferenceFlagged.Count) return false;
            return bigDifferenceFlagged[leg];
        }

        public void FlagBigDifference(int leg)
        {
            if (leg >= 0 && leg < bigDifferenceFlagged.Count)
                bigDifferenceFlagged[leg] = true;
        }

        public void AddSample(int leg, double dvIncrement)
        {
            if (leg < 0 || leg >= usedDv.Count) return;
            if (double.IsNaN(dvIncrement) || double.IsInfinity(dvIncrement) || dvIncrement < 0) return;
            usedDv[leg] += dvIncrement;
        }

        public static double DvFromMassChange(double mass0, double mass1, double isp)
        {
            if (mass0 <= 0 || mass1 <= 0 || mass1 > mass0) return double.NaN;
            if (isp <= 0) return double.NaN;
            const double g0 = 9.80665;
            return isp * g0 * Math.Log(mass0 / mass1);
        }
    }
}
