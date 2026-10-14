using System.Collections.Generic;

namespace KSPChatBridge
{
    internal sealed class LegResult
    {
        public string Leg;
        public int Stage;
        public double Spare;
    }

    internal static class StageAllocator
    {
        internal static List<LegResult> Allocate(double[] stageDv, string[] legNames, double[] legDv)
        {
            List<LegResult> results = new List<LegResult>();
            if (stageDv == null || legNames == null || legDv == null) return results;

            double[] remainingByStage = (double[])stageDv.Clone();
            int stageIndex = 0;
            int legCount = legNames.Length < legDv.Length ? legNames.Length : legDv.Length;

            for (int legIndex = 0; legIndex < legCount; legIndex++)
            {
                double left = legDv[legIndex];
                if (left <= 0) continue;

                LegResult lastResult = null;
                while (left > 0 && stageIndex < remainingByStage.Length)
                {
                    double available = remainingByStage[stageIndex];
                    if (available <= 0)
                    {
                        stageIndex++;
                        continue;
                    }

                    double used = left < available ? left : available;
                    left -= used;
                    remainingByStage[stageIndex] -= used;
                    lastResult = new LegResult
                    {
                        Leg = legNames[legIndex],
                        Stage = stageIndex + 1
                    };
                    results.Add(lastResult);

                    if (remainingByStage[stageIndex] <= 0) stageIndex++;
                }

                if (lastResult != null)
                {
                    lastResult.Spare = left > 0
                        ? -left
                        : remainingByStage[lastResult.Stage - 1];
                }
            }

            return results;
        }
    }
}
