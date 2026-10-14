using System;
using KSPChatBridge;

internal static class CommsStatusTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("CommsStatusTests: " + name);
        passes++;
    }

    internal static void Run()
    {
        passes = 0;
        Check(CommsStatus.SignalFraction(.25) == .25f && CommsStatus.SignalPercent(.25) == "25%",
            "formats normalized signal strength");
        Check(CommsStatus.SignalFraction(-1) == 0 && CommsStatus.SignalFraction(2) == 1,
            "bounds signal strength");
        Check(CommsStatus.SignalPercent(double.NaN) == "--" &&
            CommsStatus.SignalPercent(double.PositiveInfinity) == "--",
            "handles invalid signal strength");
        Check(CommsStatus.FormatDelay(0) == "0 ms" && CommsStatus.FormatDelay(.125) == "125 ms",
            "formats sub-second signal delay");
        Check(CommsStatus.FormatDelay(1.25) == "1.25 s", "formats second-scale signal delay");
        Check(CommsStatus.FormatDelay(-1) == "--" && CommsStatus.FormatDelay(double.NaN) == "--",
            "handles invalid signal delay");
        Check(CommsStatus.FormatName("Probe_Full") == "PROBE FULL" &&
            CommsStatus.FormatName(null) == "UNKNOWN", "formats enum names and missing values");
        Console.WriteLine("CommsStatusTests: " + passes + " behavior checks passed.");
    }
}
