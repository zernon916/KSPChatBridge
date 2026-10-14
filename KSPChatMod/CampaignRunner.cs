namespace KSPChatBridge
{
    internal enum CampaignRunnerState
    {
        Idle,
        WaitingWindow,
        Launching,
        Ascending,
        Transfer,
        Done,
        Failed
    }

    internal enum CampaignRunnerInput
    {
        Start,
        LaunchWindowReady,
        LaunchComplete,
        AscentComplete,
        TransferComplete,
        Fail,
        Abort
    }

    internal sealed class CampaignRunner
    {
        internal CampaignRunnerState State { get; private set; }

        internal bool Apply(CampaignRunnerInput input)
        {
            CampaignRunnerState next;
            switch (input)
            {
                case CampaignRunnerInput.Start:
                    if (State != CampaignRunnerState.Idle) return false;
                    next = CampaignRunnerState.WaitingWindow;
                    break;
                case CampaignRunnerInput.LaunchWindowReady:
                    if (State != CampaignRunnerState.WaitingWindow) return false;
                    next = CampaignRunnerState.Launching;
                    break;
                case CampaignRunnerInput.LaunchComplete:
                    if (State != CampaignRunnerState.Launching) return false;
                    next = CampaignRunnerState.Ascending;
                    break;
                case CampaignRunnerInput.AscentComplete:
                    if (State != CampaignRunnerState.Ascending) return false;
                    next = CampaignRunnerState.Transfer;
                    break;
                case CampaignRunnerInput.TransferComplete:
                    if (State != CampaignRunnerState.Transfer) return false;
                    next = CampaignRunnerState.Done;
                    break;
                case CampaignRunnerInput.Fail:
                case CampaignRunnerInput.Abort:
                    if (State == CampaignRunnerState.Idle || State == CampaignRunnerState.Done ||
                        State == CampaignRunnerState.Failed)
                        return false;
                    next = CampaignRunnerState.Failed;
                    break;
                default:
                    return false;
            }

            State = next;
            return true;
        }

        internal bool Reset()
        {
            if (State != CampaignRunnerState.Done && State != CampaignRunnerState.Failed)
                return false;
            State = CampaignRunnerState.Idle;
            return true;
        }
    }
}
