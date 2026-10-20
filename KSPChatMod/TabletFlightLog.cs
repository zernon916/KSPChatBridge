using System;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletFlightLogScroll;

        void TabletFlightLog(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            GUILayout.BeginArea(area);
            tabletFlightLogScroll = GUILayout.BeginScrollView(tabletFlightLogScroll);
            GUILayout.Label("RECENT FLIGHT EVENTS", tabletHead);

            FlightLogEntry[] entries = FlightLog.SnapshotNewestFirst();
            if (entries.Length == 0)
                GUILayout.Label("No flight events recorded yet.", tabletText);
            else
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.Label(FlightLog.Format(entries[i]), tabletText);
                    GUILayout.EndVertical();
                    GUILayout.Space(6);
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }

    internal static class FlightLogEvents
    {
        static bool registered;

        internal static void Register()
        {
            if (registered) return;
            try
            {   // GROK TEST1b: KSP's EventData.Add NREs on STATIC handlers (EvtDelegate reads evt.Target) - register instance forwarders
                GameEvents.onLaunch.Add(H.Launch);
                GameEvents.onStageActivate.Add(H.Stage);
                GameEvents.onVesselSOIChanged.Add(H.Soi);
                GameEvents.onVesselSituationChange.Add(H.Situation);
                GameEvents.onCrash.Add(H.Crash);
                GameEvents.onCrashSplashdown.Add(H.CrashSplash);
                registered = true;
            }
            catch (System.Exception ex) { UnityEngine.Debug.LogWarning("[KSPChatBridge] flight log events: " + ex.Message); }
        }

        internal static void Unregister()
        {
            if (!registered) return;
            try
            {
                GameEvents.onLaunch.Remove(H.Launch);
                GameEvents.onStageActivate.Remove(H.Stage);
                GameEvents.onVesselSOIChanged.Remove(H.Soi);
                GameEvents.onVesselSituationChange.Remove(H.Situation);
                GameEvents.onCrash.Remove(H.Crash);
                GameEvents.onCrashSplashdown.Remove(H.CrashSplash);
            }
            catch (System.Exception) { }
            registered = false;
        }
        sealed class Fwd
        {
            internal void Launch(EventReport r) { OnLaunch(r); }
            internal void Stage(int s) { OnStageActivate(s); }
            internal void Soi(GameEvents.HostedFromToAction<Vessel, CelestialBody> c) { OnSoiChanged(c); }
            internal void Situation(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> c) { OnSituationChanged(c); }
            internal void Crash(EventReport r) { OnCrash(r); }
            internal void CrashSplash(EventReport r) { OnCrashSplashdown(r); }
        }
        static readonly Fwd H = new Fwd();

        static void OnLaunch(EventReport report)
        {
            Vessel vessel = report != null && report.origin != null ? report.origin.vessel : FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            string text = report != null && !string.IsNullOrEmpty(report.sender) ? report.sender + " launched." : "Vessel launched.";
            Record(vessel, "Launch", text);
        }

        static void OnStageActivate(int stage)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;
            Record(vessel, "Stage", "Stage " + stage + " activated.");
        }

        static void OnSoiChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> change)
        {
            if (change.host == null || change.host != FlightGlobals.ActiveVessel) return;
            string from = change.from == null ? "Unknown" : change.from.bodyName;
            string to = change.to == null ? "Unknown" : change.to.bodyName;
            Record(change.host, "SOI", "SOI changed: " + from + " -> " + to + ".", to);
        }

        static void OnSituationChanged(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> change)
        {
            if (change.host == null || change.host != FlightGlobals.ActiveVessel) return;
            if (change.to != Vessel.Situations.LANDED && change.to != Vessel.Situations.SPLASHED) return;
            string kind = change.to == Vessel.Situations.SPLASHED ? "Splashdown" : "Landing";
            Record(change.host, kind, "Vessel " + (kind == "Landing" ? "landed." : "splashed down."));
        }

        static void OnCrash(EventReport report)
        {
            RecordCrash(report, "Crash");
        }

        static void OnCrashSplashdown(EventReport report)
        {
            RecordCrash(report, "Splashdown");
        }

        static void RecordCrash(EventReport report, string kind)
        {
            Vessel vessel = report != null && report.origin != null ? report.origin.vessel : FlightGlobals.ActiveVessel;
            Vessel active = FlightGlobals.ActiveVessel;
            if (vessel == null || (active != null && vessel != active)) return;
            string action = kind == "Splashdown" ? " splashed down." : " crashed.";
            string text = report != null && !string.IsNullOrEmpty(report.msg) ? report.msg :
                (report != null && !string.IsNullOrEmpty(report.sender) ? report.sender + action : "Vessel" + action);
            Record(vessel, kind, text);
        }

        static void Record(Vessel vessel, string kind, string text, string bodyOverride = null)
        {
            string body = bodyOverride;
            if (string.IsNullOrEmpty(body))
                body = vessel != null && vessel.mainBody != null ? vessel.mainBody.bodyName : "Unknown body";
            FlightLog.Add(Planetarium.GetUniversalTime(), body, kind, text);
        }
    }
}
