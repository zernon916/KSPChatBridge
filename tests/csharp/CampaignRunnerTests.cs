using System;
using KSPChatBridge;

internal static class CampaignRunnerTests
{
    static int passes;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("CampaignRunnerTests: " + name);
        passes++;
    }

    internal static void Run()
    {
        passes = 0;
        var runner = new CampaignRunner();
        Check(runner.State == CampaignRunnerState.Idle, "starts idle");
        Check(!runner.Apply(CampaignRunnerInput.LaunchWindowReady) &&
            !runner.Apply(CampaignRunnerInput.Abort), "rejects transitions while idle");

        Check(runner.Apply(CampaignRunnerInput.Start) &&
            runner.State == CampaignRunnerState.WaitingWindow, "starts waiting for a window");
        Check(!runner.Apply(CampaignRunnerInput.LaunchComplete) &&
            runner.State == CampaignRunnerState.WaitingWindow, "rejects out-of-order launch completion");
        Check(runner.Apply(CampaignRunnerInput.LaunchWindowReady) &&
            runner.State == CampaignRunnerState.Launching, "advances when the launch window is ready");
        Check(runner.Apply(CampaignRunnerInput.LaunchComplete) &&
            runner.State == CampaignRunnerState.Ascending, "advances after launch");
        Check(!runner.Reset(), "cannot reset an active run");
        Check(runner.Apply(CampaignRunnerInput.AscentComplete) &&
            runner.State == CampaignRunnerState.Transfer, "advances after ascent");
        Check(runner.Apply(CampaignRunnerInput.TransferComplete) &&
            runner.State == CampaignRunnerState.Done, "completes after transfer");
        Check(!runner.Apply(CampaignRunnerInput.Fail) &&
            !runner.Apply(CampaignRunnerInput.Start), "done is terminal until reset");
        Check(runner.Reset() && runner.State == CampaignRunnerState.Idle, "resets a completed run");

        runner.Apply(CampaignRunnerInput.Start);
        Check(runner.Apply(CampaignRunnerInput.Fail) &&
            runner.State == CampaignRunnerState.Failed, "failure ends an active run");
        Check(!runner.Apply(CampaignRunnerInput.Abort) &&
            runner.Reset() && runner.State == CampaignRunnerState.Idle, "failed run is terminal and resettable");

        runner.Apply(CampaignRunnerInput.Start);
        runner.Apply(CampaignRunnerInput.LaunchWindowReady);
        runner.Apply(CampaignRunnerInput.LaunchComplete);
        Check(runner.Apply(CampaignRunnerInput.Abort) &&
            runner.State == CampaignRunnerState.Failed, "abort fails an in-progress run");

        Console.WriteLine("CampaignRunnerTests: " + passes + " behavior checks passed.");
    }
}
