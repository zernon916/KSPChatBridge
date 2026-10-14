using UnityEngine;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    internal sealed class CampaignPhaseEvents : MonoBehaviour
    {
        void Start()
        {
            GameEvents.onLaunch.Add(AicsMenu.OnCampaignLaunch);
            GameEvents.onVesselSituationChange.Add(AicsMenu.OnCampaignSituationChanged);
        }

        void Update()
        {
            AicsMenu.UpdateCampaignProgress();
        }

        void OnDestroy()
        {
            GameEvents.onLaunch.Remove(AicsMenu.OnCampaignLaunch);
            GameEvents.onVesselSituationChange.Remove(AicsMenu.OnCampaignSituationChanged);
        }
    }
}
