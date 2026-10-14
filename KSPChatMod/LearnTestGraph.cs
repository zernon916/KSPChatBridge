using System;
using System.Collections.Generic;

namespace KSPChatBridge
{
    /// <summary>Finite, x-sorted chart data for the tablet's LEARN test cards.</summary>
    internal sealed class LearnTestGraph
    {
        internal readonly string XLabel, YLabel, Unit;
        internal readonly float[] X, Y;
        internal int Count { get { return X.Length; } }

        LearnTestGraph(string xLabel, string yLabel, string unit, float[] x, float[] y)
        {
            XLabel = xLabel;
            YLabel = yLabel;
            Unit = unit;
            X = x;
            Y = y;
        }

        internal static LearnTestGraph FromPoints(IEnumerable<double[]> points, int xIndex, int yIndex, string xLabel, string yLabel, string unit)
        {
            var sorted = new List<double[]>();
            if (points != null && xIndex >= 0 && yIndex >= 0)
            {
                foreach (var point in points)
                {
                    if (point == null || point.Length <= Math.Max(xIndex, yIndex)) continue;
                    double x = point[xIndex], y = point[yIndex];
                    if (!IsFiniteFloat(x) || !IsFiniteFloat(y)) continue;
                    sorted.Add(new[] { x, y });
                }
            }
            sorted.Sort((a, b) => a[0].CompareTo(b[0]));

            var xs = new float[sorted.Count];
            var ys = new float[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                xs[i] = (float)sorted[i][0];
                ys[i] = (float)sorted[i][1];
            }
            return new LearnTestGraph(xLabel ?? "", yLabel ?? "", unit ?? "", xs, ys);
        }

        static bool IsFiniteFloat(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) <= float.MaxValue;
        }
    }
}
