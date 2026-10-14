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

        void TabletCampaign(Rect area)
        {
            TabletUi.Ensure(tabletFont);
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
                        float cardHeight = header + row * 5 + gap * 2 + 16;
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

        void SaveTabletCampaign(bool confirmUnreadableReplacement = false)
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

        void StartFreshCampaign()
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
    }
}
