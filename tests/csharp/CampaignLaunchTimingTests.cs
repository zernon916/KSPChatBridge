using System;
using KSPChatBridge;

internal static class CampaignLaunchTimingTests
{
    static int checks;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("CampaignLaunchTimingTests: " + name);
        checks++;
    }

    static bool Near(double actual, double expected, double tolerance)
    {
        return Math.Abs(actual - expected) <= tolerance;
    }

    internal static void Run()
    {
        checks = 0;
        bool ascending;
        double wait = MechJebPolicy.LaunchWindow(
            new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 },
            new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 },
            new[] { 0.0, 0.0, 1.0 }, 1, out ascending);
        Check(Near(wait, Math.PI / 2, 1e-8), "finds the next site crossing of a target orbital plane");
        Check(ascending, "identifies the ascending crossing");

        wait = MechJebPolicy.LaunchWindow(
            new[] { 0.0, 1.0, 0.0 }, new[] { 0.0, 2.0, 0.0 },
            new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 },
            new[] { 0.0, 0.0, 1.0 }, 1, out ascending);
        Check(double.IsNaN(wait), "reports no launch window when the plane never crosses the site");

        Console.WriteLine("CampaignLaunchTimingTests: " + checks + " behavior checks passed.");
    }
}
