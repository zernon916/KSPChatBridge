using System;

namespace KSPChatBridge
{
    internal static class PorkchopColorMap
    {
        internal const int LevelCount = 5;
        const int Unavailable = -1;

        internal static int[,] Levels(double[,] deltaV)
        {
            if (deltaV == null) throw new ArgumentNullException("deltaV");

            int departures = deltaV.GetLength(0), arrivals = deltaV.GetLength(1);
            int[,] levels = new int[departures, arrivals];
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (int i = 0; i < departures; i++)
            for (int j = 0; j < arrivals; j++)
            {
                double value = deltaV[i, j];
                if (!Finite(value)) continue;
                if (value < min) min = value;
                if (value > max) max = value;
            }

            if (!Finite(min)) return FillUnavailable(levels);
            for (int i = 0; i < departures; i++)
            for (int j = 0; j < arrivals; j++)
            {
                double value = deltaV[i, j];
                if (!Finite(value))
                {
                    levels[i, j] = Unavailable;
                    continue;
                }
                if (max == min)
                {
                    levels[i, j] = 0;
                    continue;
                }

                int level = (int)((value - min) / (max - min) * LevelCount);
                levels[i, j] = Math.Min(LevelCount - 1, level);
            }
            return levels;
        }

        static int[,] FillUnavailable(int[,] levels)
        {
            for (int i = 0; i < levels.GetLength(0); i++)
            for (int j = 0; j < levels.GetLength(1); j++)
                levels[i, j] = Unavailable;
            return levels;
        }

        static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
