using System;
using KSPChatBridge;

internal static class TransferDiagramMathTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("TransferDiagramMathTests: " + name);
        passes++;
    }

    static bool Near(double actual, double expected, double tolerance)
    {
        return Math.Abs(actual - expected) <= tolerance;
    }

    internal static void Run()
    {
        passes = 0;
        double x, y;
        Check(TransferDiagramMath.TryPoint(10, 0, out x, out y) && Near(x, 10, 1e-9) && Near(y, 0, 1e-9),
            "polar point at zero angle");
        Check(TransferDiagramMath.TryPoint(10, Math.PI / 2, out x, out y) && Near(x, 0, 1e-9) && Near(y, 10, 1e-9),
            "polar point at quarter turn");
        Check(!TransferDiagramMath.TryPoint(0, 0, out x, out y) && double.IsNaN(x) && double.IsNaN(y),
            "non-positive orbit radius rejected");

        const double r1 = 10, r2 = 30, start = .4;
        Check(TransferDiagramMath.TryHohmannPoint(r1, r2, start, 0, out x, out y) &&
            Near(x, r1 * Math.Cos(start), 1e-9) && Near(y, r1 * Math.Sin(start), 1e-9),
            "outbound transfer begins at source position");
        Check(TransferDiagramMath.TryHohmannPoint(r1, r2, start, 1, out x, out y) &&
            Near(x, r2 * Math.Cos(start + Math.PI), 1e-9) && Near(y, r2 * Math.Sin(start + Math.PI), 1e-9),
            "outbound transfer ends at destination radius opposite source");
        Check(TransferDiagramMath.TryHohmannPoint(r1, r2, start, .5, out x, out y) &&
            double.IsNaN(x) == false && double.IsNaN(y) == false && Math.Sqrt(x * x + y * y) > r1,
            "outbound transfer curves outside source orbit");

        Check(TransferDiagramMath.TryHohmannPoint(r2, r1, start, 0, out x, out y) &&
            Near(Math.Sqrt(x * x + y * y), r2, 1e-9),
            "inbound transfer begins at source radius");
        Check(TransferDiagramMath.TryHohmannPoint(r2, r1, start, 1, out x, out y) &&
            Near(Math.Sqrt(x * x + y * y), r1, 1e-9) &&
            Near(x, r1 * Math.Cos(start + Math.PI), 1e-9) && Near(y, r1 * Math.Sin(start + Math.PI), 1e-9),
            "inbound transfer ends at destination position");
        Check(!TransferDiagramMath.TryHohmannPoint(r1, r2, start, -0.1, out x, out y),
            "fraction before departure rejected");
        Check(!TransferDiagramMath.TryHohmannPoint(r1, double.PositiveInfinity, start, .5, out x, out y),
            "non-finite radius rejected");
        Console.WriteLine("TransferDiagramMathTests: " + passes + " behavior checks passed.");
    }
}
