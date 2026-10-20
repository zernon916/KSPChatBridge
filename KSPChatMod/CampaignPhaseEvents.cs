using UnityEngine;

namespace KSPChatBridge
{
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    internal sealed class CampaignPhaseEvents : MonoBehaviour
    {
        void Start()
        {   // GROK TEST1b: instance forwarders - KSP's EventData.Add NREs on static handlers
            try { GameEvents.onLaunch.Add(OnLaunch); GameEvents.onVesselSituationChange.Add(OnSituation); }
            catch (System.Exception ex) { Debug.LogWarning("[KSPChatBridge] campaign events: " + ex.Message); }
        }
        void OnLaunch(EventReport r) { AicsMenu.OnCampaignLaunch(r); }
        void OnSituation(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> c) { AicsMenu.OnCampaignSituationChanged(c); }

        void Update()
        {
            AicsMenu.UpdateCampaignProgress();
        }

        void OnDestroy()
        {
            try { GameEvents.onLaunch.Remove(OnLaunch); GameEvents.onVesselSituationChange.Remove(OnSituation); } catch (System.Exception) { }
        }
    }
}
