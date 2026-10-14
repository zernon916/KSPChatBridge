using System;
using System.Collections.Generic;
using KSPChatBridge;

internal static class LearnTestGraphsTests
{
    static int checks;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("LearnTestGraphsTests: " + name);
        checks++;
    }

    internal static void Run()
    {
        checks = 0;
        var graph = LearnTestGraph.FromPoints(new[]
        {
            new[] { 3.0, 30.0 },
            new[] { 1.0, 10.0 },
            new[] { 2.0, 20.0 }
        }, 0, 1, "Speed", "Climb rate", "m/s");
        Check(graph.Count == 3, "keeps valid points");
        Check(graph.X[0] == 1 && graph.X[1] == 2 && graph.X[2] == 3, "sorts points by x");
        Check(graph.Y[0] == 10 && graph.Y[2] == 30, "keeps y values paired while sorting");
        Check(graph.XLabel == "Speed" && graph.YLabel == "Climb rate" && graph.Unit == "m/s", "preserves graph labels");

        graph = LearnTestGraph.FromPoints(new List<double[]>
        {
            null,
            new[] { 1.0 },
            new[] { double.NaN, 2.0 },
            new[] { 2.0, double.PositiveInfinity },
            new[] { (double)float.MaxValue * 2, 3.0 },
            new[] { 4.0, 40.0 }
        }, 0, 1, null, null, null);
        Check(graph.Count == 1 && graph.X[0] == 4 && graph.Y[0] == 40, "filters missing and non-finite points");
        Check(graph.XLabel == "" && graph.YLabel == "" && graph.Unit == "", "normalizes missing labels");

        graph = LearnTestGraph.FromPoints(null, 0, 1, "x", "y", "");
        Check(graph.Count == 0, "handles missing samples");
        graph = LearnTestGraph.FromPoints(new[] { new[] { 1.0, 2.0 } }, -1, 1, "x", "y", "");
        Check(graph.Count == 0, "rejects invalid value indexes");
        Console.WriteLine("LearnTestGraphsTests: " + checks + " behavior checks passed.");
    }
}
