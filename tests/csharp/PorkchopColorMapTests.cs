using System;
using KSPChatBridge;

internal static class PorkchopColorMapTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("PorkchopColorMapTests: " + name);
        passes++;
    }

    internal static void Run()
    {
        passes = 0;
        int[,] levels = PorkchopColorMap.Levels(new double[,]
        {
            { 100, 200, 300 },
            { double.NaN, 400, double.PositiveInfinity }
        });
        Check(levels.GetLength(0) == 2 && levels.GetLength(1) == 3, "output preserves grid dimensions");
        Check(levels[0, 0] == 0, "minimum delta-v maps to the lowest color level");
        Check(levels[1, 1] == PorkchopColorMap.LevelCount - 1, "maximum delta-v maps to the highest color level");
        Check(levels[0, 0] <= levels[0, 1] && levels[0, 1] <= levels[0, 2] &&
            levels[0, 2] <= levels[1, 1], "color levels are monotonic with delta-v");
        Check(levels[1, 0] == -1 && levels[1, 2] == -1, "non-finite transfer cells are unavailable");

        int[,] equal = PorkchopColorMap.Levels(new double[,] { { 42, 42 } });
        Check(equal[0, 0] == 0 && equal[0, 1] == 0, "equal finite values map consistently");

        int[,] unavailable = PorkchopColorMap.Levels(new double[,]
        {
            { double.NaN, double.NegativeInfinity }
        });
        Check(unavailable[0, 0] == -1 && unavailable[0, 1] == -1, "all-invalid grid remains unavailable");

        int[,] empty = PorkchopColorMap.Levels(new double[0, 3]);
        Check(empty.GetLength(0) == 0 && empty.GetLength(1) == 3, "empty grid dimensions are preserved");

        bool rejectedNull = false;
        try { PorkchopColorMap.Levels(null); }
        catch (ArgumentNullException) { rejectedNull = true; }
        Check(rejectedNull, "null grid is rejected explicitly");
        Console.WriteLine("PorkchopColorMapTests: " + passes + " behavior checks passed.");
    }
}
