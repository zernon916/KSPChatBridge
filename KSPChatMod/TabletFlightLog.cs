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
            GameEvents.onLaunch.Add(OnLaunch);
            GameEvents.onStageActivate.Add(OnStageActivate);
            GameEvents.onVesselSOIChanged.Add(OnSoiChanged);
            GameEvents.onVesselSituationChange.Add(OnSituationChanged);
            GameEvents.onCrash.Add(OnCrash);
            registered = true;
        }

        internal static void Unregister()
        {
            if (!registered) return;
            GameEvents.onLaunch.Remove(OnLaunch);
            GameEvents.onStageActivate.Remove(OnStageActivate);
            GameEvents.onVesselSOIChanged.Remove(OnSoiChanged);
            GameEvents.onVesselSituationChange.Remove(OnSituationChanged);
            GameEvents.onCrash.Remove(OnCrash);
            registered = false;
        }

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
            Vessel vessel = report != null && report.origin != null ? report.origin.vessel : FlightGlobals.ActiveVessel;
            Vessel active = FlightGlobals.ActiveVessel;
            if (vessel == null || (active != null && vessel != active)) return;
            string text = report != null && !string.IsNullOrEmpty(report.msg) ? report.msg :
                (report != null && !string.IsNullOrEmpty(report.sender) ? report.sender + " crashed." : "Vessel crashed.");
            Record(vessel, "Crash", text);
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
