using System;

namespace KSPChatBridge
{
    internal static class CorrectionMath
    {
        // NaN periapsis means no encounter; miss is distance from the target center.
        internal delegate void Prediction(double radial, double normal, double prograde,
            out double periapsis, out double miss);

        internal static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        internal static double Tolerance(double target) { return Math.Max(2000, Math.Abs(target)*.05); }

        internal static double Score(double periapsis, double miss, double target)
        {
            if (!Finite(target)) return double.PositiveInfinity;
            // Disjoint ranges guarantee that acquiring/retaining an encounter comes first.
            if (Finite(periapsis))
            {
                double error = Math.Abs(periapsis-target)/Tolerance(target);
                return Finite(error) ? error/(1+error) : 1;
            }
            return Finite(miss) && miss >= 0 ? 2+miss : double.PositiveInfinity;
        }

        internal static bool Search(Prediction predict, double target,
            out double radial, out double normal, out double prograde, out double periapsis)
        {
            radial = normal = prograde = 0;
            periapsis = double.NaN;
            if (predict == null || !Finite(target)) return false;
            double[] best = { 0, 0, 0 };
            double miss;
            predict(0, 0, 0, out periapsis, out miss);
            double score = Score(periapsis, miss, target), step = 10;
            for (int iteration = 0; iteration < 60; iteration++)
            {
                if (Finite(periapsis) && Math.Abs(periapsis-target) <= Tolerance(target)) break;
                bool improved = false;
                for (int axis = 0; axis < 3; axis++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    double[] trial = (double[])best.Clone();
                    trial[axis] = Math.Max(-100, Math.Min(100, trial[axis]+sign*step));
                    double pea;
                    predict(trial[0], trial[1], trial[2], out pea, out miss);
                    double value = Score(pea, miss, target);
                    if (value < score)
                    { best = trial; score = value; periapsis = pea; improved = true; }
                }
                if (!improved) step *= .5;
                if (step < 1e-5) break;
            }
            radial = best[0]; normal = best[1]; prograde = best[2];
            return Finite(periapsis) && Math.Abs(periapsis-target) <= Tolerance(target);
        }
    }
}
