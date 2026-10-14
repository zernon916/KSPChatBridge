using System;
using KSPChatBridge;

internal static class ScienceTrackerTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("ScienceTrackerTests: " + name);
        passes++;
    }

    internal static void Run()
    {
        passes = 0;
        ScienceSubjectProgress progress = ScienceTracker.Create("goo@Kerbin", "Mystery Goo at Kerbin", 5, 20);
        Check(progress != null && progress.Science == 5 && progress.Cap == 20 && progress.Remaining == 15,
            "creates subject progress");
        Check(progress.Fraction == .25 && !progress.Complete, "calculates fraction and incomplete state");
        Check(ScienceTracker.Create("subject", null, -5, 10).Science == 0, "clamps negative science");
        Check(ScienceTracker.Create("subject", "title", 15, 10).Science == 10, "clamps science to subject cap");
        Check(ScienceTracker.Create("subject", "", double.NaN, 10).Science == 0, "normalizes unknown science and title");
        Check(ScienceTracker.Create("", "title", 1, 10) == null, "rejects missing subject id");
        Check(ScienceTracker.Create("subject", "title", 1, 0) == null &&
            ScienceTracker.Create("subject", "title", 1, double.PositiveInfinity) == null,
            "rejects invalid subject cap");
        Check(ScienceTracker.Create("done", "Done", 99, 100).Complete &&
            !ScienceTracker.Create("not-done", "Not done", 98.9, 100).Complete,
            "uses the 99 percent completion threshold");

        ScienceSubjectProgress low = ScienceTracker.Create("low", "Low", 1, 10);
        ScienceSubjectProgress high = ScienceTracker.Create("high", "High", 1, 30);
        ScienceSubjectProgress done = ScienceTracker.Create("done", "Done", 10, 10);
        var ordered = ScienceTracker.Snapshot(new[] { done, high, null, low });
        Check(ordered.Count == 3 && ordered[0] == high && ordered[1] == low && ordered[2] == done,
            "orders incomplete subjects by remaining science before completed subjects");
        Check(ScienceTracker.Snapshot(null).Count == 0, "handles an unavailable subject sequence");
        Console.WriteLine("ScienceTrackerTests: " + passes + " behavior checks passed.");
    }
}
