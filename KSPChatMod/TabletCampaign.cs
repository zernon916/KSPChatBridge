using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletCampaignScroll;
        static Campaign tabletCampaign;
        static bool tabletCampaignLoaded;
        static bool tabletCampaignNeedsBackup;
        static int tabletCampaignEditing = -1;
        static string tabletCampaignNameT, tabletCampaignGoalT = "", tabletCampaignCraftT = "";
        static string tabletCampaignAltT = "80000", tabletCampaignIncT = "0", tabletCampaignMessage = "";
        static readonly Dictionary<CampaignPhase, string> tabletCampaignDvChecks = new Dictionary<CampaignPhase, string>();
        static float nextCampaignProgressCheck;

        void TabletCampaign(Rect area)
        {
            TabletUi.Ensure(tabletFont);
            EnsureTabletCampaignLoaded();
            GUILayout.BeginArea(area);
            try
            {
                tabletCampaignScroll = GUILayout.BeginScrollView(tabletCampaignScroll);
                try
                {
                    float width = area.width - 26, gap = 8, row = Mathf.Max(44, tabletFont * 2.5f);
                    float header = 16 + tabletFont * 1.6f, y = 0;
                    Rect campaignCard = TabletUi.CardPanel(new Rect(0, y, width, header + row * 2 + gap + 16), "CAMPAIGN");
                    TabletUi.Label(new Rect(campaignCard.x, campaignCard.y, campaignCard.width * .25f, row), "Name");
                    tabletCampaignNameT = TabletUi.Field(new Rect(campaignCard.x + campaignCard.width * .25f, campaignCard.y,
                        campaignCard.width * .75f, row), tabletCampaignNameT);
                    if (TabletUi.Button(new Rect(campaignCard.x, campaignCard.y + row + gap, campaignCard.width, row),
                        "SAVE CAMPAIGN"))
                    {
                        if (string.IsNullOrWhiteSpace(tabletCampaignNameT))
                            tabletCampaignMessage = "Campaign name is required.";
                        else
                        {
                            tabletCampaign.Name = tabletCampaignNameT.Trim();
                            SaveTabletCampaign(true);
                        }
                    }
                    y += header + row * 2 + gap * 2 + 16;

                    float editorHeight = header + row * 5 + gap + 16;
                    Rect editor = TabletUi.CardPanel(new Rect(0, y, width, editorHeight),
                        tabletCampaignEditing < 0 ? "NEW PHASE" : "EDIT PHASE " + (tabletCampaignEditing + 1));
                    float labelWidth = Mathf.Min(editor.width * .4f, Mathf.Max(112, editor.width * .24f));
                    CampaignEditorField(editor, labelWidth, row, 0, "Goal", ref tabletCampaignGoalT);
                    CampaignEditorField(editor, labelWidth, row, 1, "Craft", ref tabletCampaignCraftT);
                    CampaignEditorField(editor, labelWidth, row, 2, "Target altitude (m)", ref tabletCampaignAltT);
                    CampaignEditorField(editor, labelWidth, row, 3, "Inclination (deg)", ref tabletCampaignIncT);
                    float buttonY = editor.y + row * 4 + gap;
                    if (TabletUi.Button(new Rect(editor.x, buttonY, editor.width * .62f, row),
                        tabletCampaignEditing < 0 ? "ADD PHASE" : "UPDATE PHASE"))
                        SaveCampaignPhase();
                    if (tabletCampaignEditing >= 0 && TabletUi.Button(
                        new Rect(editor.x + editor.width * .64f, buttonY, editor.width * .36f, row), "CANCEL"))
                        ResetCampaignEditor();
                    y += editorHeight + gap;

                    if (!string.IsNullOrEmpty(tabletCampaignMessage))
                    {
                        TabletUi.Label(new Rect(8, y, width - 16, row), tabletCampaignMessage, true);
                        y += row + gap;
                    }

                    for (int i = 0; i < tabletCampaign.Phases.Count; i++)
                    {
                        CampaignPhase phase = tabletCampaign.Phases[i];
                        float cardHeight = header + row * 8 + gap * 3 + 16;
                        Rect card = TabletUi.CardPanel(new Rect(0, y, width, cardHeight), "PHASE " + (i + 1));
                        TabletUi.Label(new Rect(card.x, card.y, card.width, row), phase.Goal);
                        TabletUi.Label(new Rect(card.x, card.y + row, card.width, row),
                            phase.Craft + "  |  " + phase.Alt.ToString("0", CultureInfo.InvariantCulture) +
                            " m altitude  |  " + phase.Inc.ToString("0.##", CultureInfo.InvariantCulture) +
                            " deg inclination  |  " + (string.IsNullOrEmpty(phase.Status) ? "Planned" : phase.Status), true);
                        float phaseButtonY = card.y + row * 2 + gap;
                        float buttonWidth = (card.width - 16) / 3;
                        if (TabletUi.Button(new Rect(card.x, phaseButtonY, buttonWidth, row), "MOVE UP", false, i > 0))
                            MoveCampaignPhase(i, -1);
                        if (TabletUi.Button(new Rect(card.x + buttonWidth + 8, phaseButtonY, buttonWidth, row), "MOVE DOWN", false,
                            i < tabletCampaign.Phases.Count - 1))
                            MoveCampaignPhase(i, 1);
                        if (TabletUi.Button(new Rect(card.x + (buttonWidth + 8) * 2, phaseButtonY, buttonWidth, row), "EDIT"))
                            EditCampaignPhase(i);
                        string dvMessage;
                        if (!tabletCampaignDvChecks.TryGetValue(phase, out dvMessage))
                            dvMessage = "DV: Tap CHECK DV to compare the saved mission plan.";
                        TabletUi.Label(new Rect(card.x, card.y + row * 3 + gap, card.width, row), dvMessage, true);
                        if (TabletUi.Button(new Rect(card.x, card.y + row * 4 + gap * 2, card.width, row), "CHECK DV"))
                            tabletCampaignDvChecks[phase] = CheckCampaignPhaseDv(phase);
                        Vessel trackedVessel = FindCampaignVessel(phase.VesselId);
                        TabletUi.Label(new Rect(card.x, card.y + row * 5 + gap * 2, card.width, row),
                            CampaignTracking.VesselLabel(phase, trackedVessel == null ? null : trackedVessel.vesselName,
                                trackedVessel != null && trackedVessel == FlightGlobals.ActiveVessel), true);
                        TabletUi.Label(new Rect(card.x, card.y + row * 6 + gap * 3, card.width, row),
                            CampaignLaunchWindowLabel(i), true);
                        Vessel activeVessel = FlightGlobals.ActiveVessel;
                        if (TabletUi.Button(new Rect(card.x, card.y + row * 7 + gap * 3, card.width, row),
                            "TRACK ACTIVE VESSEL", false, activeVessel != null))
                        {
                            CampaignTracking.Track(phase, activeVessel.id.ToString());
                            SaveTabletCampaign();
                        }
                        y += cardHeight + gap;
                    }
                    GUILayout.Space(y + gap);
                }
                finally
                {
                    GUILayout.EndScrollView();
                }
            }
            finally
            {
                GUILayout.EndArea();
            }
        }

        static void EnsureTabletCampaignLoaded()
        {
            if (!tabletCampaignLoaded)
            {
                string path = Path.Combine(AicsCore.PluginDataDirectory, "campaign.json");
                try
                {
                    tabletCampaign = CampaignStore.Load(path);
                }
                catch (InvalidDataException)
                {
                    StartFreshCampaign();
                }
                catch (IOException)
                {
                    StartFreshCampaign();
                }
                catch (ArgumentException)
                {
                    StartFreshCampaign();
                }
                catch (UnauthorizedAccessException)
                {
                    StartFreshCampaign();
                }
                tabletCampaignNameT = tabletCampaign.Name;
                tabletCampaignLoaded = true;
            }
        }

        internal static void OnCampaignLaunch(EventReport report)
        {
            Vessel vessel = report != null && report.origin != null ? report.origin.vessel : FlightGlobals.ActiveVessel;
            if (vessel == null || string.IsNullOrWhiteSpace(vessel.id.ToString())) return;
            EnsureTabletCampaignLoaded();
            if (tabletCampaign == null || tabletCampaign.Phases == null) return;

            string vesselId = vessel.id.ToString();
            foreach (CampaignPhase phase in tabletCampaign.Phases)
            {
                if (!CampaignPhaseChatter.TryStart(phase, vesselId)) continue;
                SaveCampaignEvent(phase, vessel, true);
                break;
            }
        }

        internal static void OnCampaignSituationChanged(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> change)
        {
            Vessel vessel = change.host;
            if (vessel == null || change.to != Vessel.Situations.ORBITING) return;
            TryCompleteCampaignPhase(vessel);
        }

        internal static void UpdateCampaignProgress()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextCampaignProgressCheck) return;
            nextCampaignProgressCheck = now + 1f;

            EnsureTabletCampaignLoaded();
            if (tabletCampaign == null || tabletCampaign.Phases == null || FlightGlobals.Vessels == null) return;

            foreach (CampaignPhase phase in tabletCampaign.Phases)
            {
                if (phase == null || !string.Equals(phase.Status, "Started", StringComparison.OrdinalIgnoreCase))
                    continue;
                Vessel vessel = FindCampaignVessel(phase.VesselId);
                if (vessel != null) TryCompleteCampaignPhase(phase, vessel);
            }
        }

        static void TryCompleteCampaignPhase(Vessel vessel)
        {
            if (vessel == null || vessel.orbit == null || vessel.orbit.referenceBody == null) return;
            EnsureTabletCampaignLoaded();
            if (tabletCampaign == null || tabletCampaign.Phases == null) return;

            string vesselId = vessel.id.ToString();
            foreach (CampaignPhase phase in tabletCampaign.Phases)
            {
                if (!TryCompleteCampaignPhase(phase, vesselId, vessel)) continue;
                break;
            }
        }

        static bool TryCompleteCampaignPhase(CampaignPhase phase, Vessel vessel)
        {
            return TryCompleteCampaignPhase(phase, vessel == null ? null : vessel.id.ToString(), vessel);
        }

        static bool TryCompleteCampaignPhase(CampaignPhase phase, string vesselId, Vessel vessel)
        {
            if (string.IsNullOrWhiteSpace(vesselId) || vessel == null || vessel.orbit == null ||
                vessel.orbit.referenceBody == null || !CampaignPhaseChatter.TryCompleteAtTargetOrbit(phase,
                    vesselId, vessel.orbit.ApA, vessel.orbit.PeA, vessel.orbit.inclination))
                return false;

            SaveCampaignEvent(phase, vessel, false);
            return true;
        }

        static void SaveCampaignEvent(CampaignPhase phase, Vessel vessel, bool started)
        {
            SaveTabletCampaign();
            string goal = string.IsNullOrEmpty(phase.Goal) ? "campaign phase" : phase.Goal;
            string message = started ? "Phase started: " + goal + ". Engines are lit - let's fly." :
                "Phase complete: " + goal + ". Target orbit achieved.";
            ChatLog.Write("campaign", message);
            List<ProtoCrewMember> crew = vessel.GetVesselCrew();
            if (crew == null || crew.Count == 0) return;
            ProtoCrewMember speaker = null;
            foreach (ProtoCrewMember member in crew)
            {
                if (member == null) continue;
                if (speaker == null) speaker = member;
                if (IntercomTalk.NormTrait(member.trait) == "pilot")
                {
                    speaker = member;
                    break;
                }
            }
            if (speaker != null)
                ChatWindow.Notice(IntercomTalk.Fmt(speaker.name, speaker.trait, message));
        }

        void CampaignEditorField(Rect editor, float labelWidth, float row, int index, string label, ref string value)
        {
            float y = editor.y + row * index;
            TabletUi.Label(new Rect(editor.x, y, labelWidth, row), label, true);
            value = TabletUi.Field(new Rect(editor.x + labelWidth, y, editor.width - labelWidth, row), value);
        }

        void SaveCampaignPhase()
        {
            double alt, inc;
            if (!double.TryParse(tabletCampaignAltT, NumberStyles.Float, CultureInfo.InvariantCulture, out alt) ||
                !double.TryParse(tabletCampaignIncT, NumberStyles.Float, CultureInfo.InvariantCulture, out inc))
            {
                tabletCampaignMessage = "Enter numeric altitude and inclination values.";
                return;
            }

            var phase = new CampaignPhase
            {
                Goal = (tabletCampaignGoalT ?? "").Trim(),
                Craft = (tabletCampaignCraftT ?? "").Trim(),
                Status = tabletCampaignEditing >= 0 ? tabletCampaign.Phases[tabletCampaignEditing].Status : "Planned",
                VesselId = tabletCampaignEditing >= 0 ? tabletCampaign.Phases[tabletCampaignEditing].VesselId : null,
                Alt = alt,
                Inc = inc
            };
            try
            {
                if (tabletCampaignEditing < 0) tabletCampaign.AddPhase(phase);
                else tabletCampaign.UpdatePhase(tabletCampaignEditing, phase);
                tabletCampaignDvChecks.Clear();
                ResetCampaignEditor();
                SaveTabletCampaign();
            }
            catch (ArgumentException ex)
            {
                tabletCampaignMessage = ex.Message;
            }
        }

        void MoveCampaignPhase(int index, int direction)
        {
            int target = index + direction;
            if (tabletCampaign.MovePhase(index, direction))
            {
                if (tabletCampaignEditing == index) tabletCampaignEditing = target;
                else if (tabletCampaignEditing == target) tabletCampaignEditing = index;
                SaveTabletCampaign();
            }
        }

        void EditCampaignPhase(int index)
        {
            CampaignPhase phase = tabletCampaign.Phases[index];
            tabletCampaignEditing = index;
            tabletCampaignGoalT = phase.Goal;
            tabletCampaignCraftT = phase.Craft;
            tabletCampaignAltT = phase.Alt.ToString("0.##", CultureInfo.InvariantCulture);
            tabletCampaignIncT = phase.Inc.ToString("0.##", CultureInfo.InvariantCulture);
            tabletCampaignMessage = "";
            tabletCampaignScroll.y = 0;
        }

        void ResetCampaignEditor()
        {
            tabletCampaignEditing = -1;
            tabletCampaignGoalT = "";
            tabletCampaignCraftT = "";
            tabletCampaignAltT = "80000";
            tabletCampaignIncT = "0";
        }

        static void SaveTabletCampaign(bool confirmUnreadableReplacement = false)
        {
            if (string.IsNullOrWhiteSpace(tabletCampaignNameT))
            {
                tabletCampaignMessage = "Campaign name is required.";
                return;
            }
            if (tabletCampaignNeedsBackup && !confirmUnreadableReplacement)
            {
                tabletCampaignMessage = "Press SAVE CAMPAIGN to replace the unreadable file.";
                return;
            }
            try
            {
                tabletCampaign.Name = tabletCampaignNameT.Trim();
                string path = Path.Combine(AicsCore.PluginDataDirectory, "campaign.json");
                if (tabletCampaignNeedsBackup && File.Exists(path))
                    File.Copy(path, path + ".bad", false);
                CampaignStore.Save(path, tabletCampaign);
                tabletCampaignNeedsBackup = false;
                tabletCampaignMessage = "Campaign saved.";
            }
            catch (IOException ex)
            {
                tabletCampaignMessage = "Could not save campaign: " + ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                tabletCampaignMessage = "Could not save campaign: " + ex.Message;
            }
            catch (ArgumentException ex)
            {
                tabletCampaignMessage = "Could not save campaign: " + ex.Message;
            }
        }

        static void StartFreshCampaign()
        {
            tabletCampaign = new Campaign();
            tabletCampaignNeedsBackup = true;
            tabletCampaignMessage = "Campaign file unreadable, started fresh (old file kept).";
        }

        static string CheckCampaignPhaseDv(CampaignPhase phase)
        {
            if (phase == null || string.IsNullOrWhiteSpace(phase.Craft))
                return "DV: Phase craft is required.";

            MissionData mission = MissionStore.Load(phase.Craft);
            if (mission == null && !string.IsNullOrWhiteSpace(phase.VesselId) &&
                !string.Equals(phase.Craft, phase.VesselId, StringComparison.Ordinal))
                mission = MissionStore.Load(phase.VesselId);
            if (mission == null)
                return "DV: No saved Mission Planner plan for " + phase.Craft + ".";

            double required;
            if (!CampaignDvCheck.TryRequiredDeltaV(mission.Legs, out required))
                return "DV: Saved mission plan has no valid legs.";

            Vessel vessel = FlightGlobals.ActiveVessel;
            bool matches = vessel != null &&
                ((!string.IsNullOrEmpty(vessel.vesselName) &&
                    string.Equals(vessel.vesselName, phase.Craft, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrEmpty(phase.VesselId) &&
                    string.Equals(vessel.id.ToString(), phase.VesselId, StringComparison.Ordinal)));
            if (!matches)
                return "DV: " + FormatCampaignDv(required) + " m/s planned; load " + phase.Craft + " to compare.";

            double available;
            if (!TryStageDeltaV(out available))
                return "DV: " + FormatCampaignDv(required) + " m/s planned; stock actual stage dV unavailable.";

            if (CampaignDvCheck.HasEnoughDeltaV(required, available))
                return "DV: " + FormatCampaignDv(required) + " m/s planned; " +
                    FormatCampaignDv(available) + " m/s stock actual - SUFFICIENT.";

            return "DV: " + FormatCampaignDv(required) + " m/s planned; " +
                    FormatCampaignDv(available) + " m/s stock actual - SHORT " +
                FormatCampaignDv(required - available) + " m/s.";
        }

        static string FormatCampaignDv(double value)
        {
            return value.ToString("#,0", CultureInfo.InvariantCulture);
        }

        static Vessel FindCampaignVessel(string vesselId)
        {
            if (string.IsNullOrWhiteSpace(vesselId) || FlightGlobals.Vessels == null) return null;
            foreach (Vessel vessel in FlightGlobals.Vessels)
                if (vessel != null && CampaignTracking.IsAssignedTo(vesselId, vessel.id.ToString()))
                    return vessel;
            return null;
        }

        string CampaignLaunchWindowLabel(int phaseIndex)
        {
            if (phaseIndex <= 0)
                return "Launch window: an earlier tracked phase is required.";

            CampaignPhase earlierPhase = null;
            for (int i = phaseIndex - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(tabletCampaign.Phases[i].VesselId))
                {
                    earlierPhase = tabletCampaign.Phases[i];
                    break;
                }
            }
            if (earlierPhase == null)
                return "Launch window: track an earlier phase vessel first.";

            Vessel target = FindCampaignVessel(earlierPhase.VesselId);
            if (target == null)
                return "Launch window: earlier phase vessel is not loaded.";

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
                return "Launch window: no active launch vessel.";
            if (target == active)
                return "Launch window: the earlier phase vessel cannot be the launch vessel.";
            if (active.situation != Vessel.Situations.PRELAUNCH && active.situation != Vessel.Situations.LANDED)
                return "Launch window: available only while the launch vessel is on the pad or landed.";

            Orbit targetOrbit = target.orbit;
            CelestialBody body = active.mainBody;
            if (body == null || targetOrbit == null || targetOrbit.referenceBody != body)
                return "Launch window: both vessels must use the launch site's reference body.";
            if (!CampaignLaunchFinite(body.rotationPeriod) || body.rotationPeriod <= 0)
                return "Launch window: launch-site rotation is unavailable.";

            double now = Planetarium.GetUniversalTime();
            if (!CampaignLaunchFinite(now))
                return "Launch window: universal time is unavailable.";
            Vector3d position = targetOrbit.getRelativePositionAtUT(now).xzy;
            Vector3d velocity = targetOrbit.getOrbitalVelocityAtUT(now).xzy;
            Vector3d normal = Vector3d.Cross(position, velocity);
            if (!CampaignLaunchFinite(position) || !CampaignLaunchFinite(velocity) ||
                !CampaignLaunchFinite(normal) || normal.sqrMagnitude <= 0)
                return "Launch window: earlier vessel orbit data is unavailable.";
            Vector3d site = body.GetWorldSurfacePosition(active.latitude, active.longitude, 0) - body.position;
            Vector3d east = (body.GetWorldSurfacePosition(active.latitude, active.longitude + .01, 0) -
                body.GetWorldSurfacePosition(active.latitude, active.longitude, 0)).normalized;
            Vector3d northAxis = (body.GetWorldSurfacePosition(90, 0, 0) - body.position).normalized;
            if (!CampaignLaunchFinite(site) || !CampaignLaunchFinite(east) || east.sqrMagnitude <= 0 ||
                !CampaignLaunchFinite(northAxis) || northAxis.sqrMagnitude <= 0)
                return "Launch window: launch-site position is unavailable.";
            Vector3d parallel = northAxis * Vector3d.Dot(site, northAxis);
            Vector3d perpendicular = site - parallel;
            bool ascending;
            double wait = MechJebPolicy.LaunchWindow(
                new[] { normal.x, normal.y, normal.z },
                new[] { parallel.x, parallel.y, parallel.z },
                new[] { perpendicular.x, perpendicular.y, perpendicular.z },
                new[] { east.x, east.y, east.z },
                new[] { northAxis.x, northAxis.y, northAxis.z },
                2 * Math.PI / body.rotationPeriod, out ascending);
            if (!CampaignLaunchFinite(wait))
                return "Launch window: the earlier vessel's plane does not cross this launch site.";

            string targetName = string.IsNullOrEmpty(target.vesselName) ? "earlier vessel" : target.vesselName;
            return "Plane window to " + targetName + ": in " + FormatCampaignLaunchWait(wait) +
                (ascending ? " (ascending); not a rendezvous guarantee." : " (descending); not a rendezvous guarantee.");
        }

        static string FormatCampaignLaunchWait(double seconds)
        {
            if (seconds >= 3600)
                return (seconds / 3600).ToString("0", CultureInfo.InvariantCulture) + " h " +
                    ((seconds % 3600) / 60).ToString("00", CultureInfo.InvariantCulture) + " min";
            if (seconds >= 60)
                return (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min " +
                    (seconds % 60).ToString("00", CultureInfo.InvariantCulture) + " s";
            return seconds.ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        static bool CampaignLaunchFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        static bool CampaignLaunchFinite(Vector3d value)
        {
            return CampaignLaunchFinite(value.x) && CampaignLaunchFinite(value.y) &&
                CampaignLaunchFinite(value.z);
        }
    }
}
