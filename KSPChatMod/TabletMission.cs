using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletMissionScroll;
        static string missionBody = "Mun";
        static string missionAltT = "30000";
        static bool missionLand;
        static bool missionReturn = true;
        static string[] missionBodyNames;

        void TabletMission(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f);
            float gap = 8;
            float width = area.width - 26;
            float pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            try
            {
                tabletMissionScroll = GUILayout.BeginScrollView(tabletMissionScroll);
                try
                {
                    List<StageInfo> stages = HighLogic.LoadedSceneIsEditor
                        ? StageReader.ForEditor()
                        : StageReader.ForFlight();
                    float cardHeight = pad + row * 3 + gap * 2;
                    Rect card = TabletUi.CardPanel(new Rect(0, 0, width, cardHeight), "MISSION");
                    string[] bodyNames = MissionBodyNames();
                    if (bodyNames.Length > 0)
                    {
                        int selected = Array.IndexOf(bodyNames, missionBody);
                        if (selected < 0) selected = 0;
                        selected = TabletUi.Segments(new Rect(card.x, card.y, card.width, row), bodyNames, selected);
                        missionBody = bodyNames[selected];
                    }

                    float y = card.y + row + gap;
                    float labelWidth = card.width * 0.32f;
                    TabletUi.Label(new Rect(card.x, y, labelWidth, row), "Orbit altitude (m)");
                    missionAltT = TabletUi.Field(new Rect(card.x + labelWidth, y, card.width - labelWidth, row), missionAltT);

                    y += row + gap;
                    float buttonWidth = (card.width - gap) / 2;
                    if (TabletUi.Button(new Rect(card.x, y, buttonWidth, row), "LAND: " + (missionLand ? "ON" : "OFF"), missionLand))
                        missionLand = !missionLand;
                    if (TabletUi.Button(new Rect(card.x + buttonWidth + gap, y, buttonWidth, row),
                        "RETURN: " + (missionReturn ? "ON" : "OFF"), missionReturn))
                        missionReturn = !missionReturn;

                    float stagesY = cardHeight + gap;
                    float stagesHeight = pad + row * Math.Max(1, stages.Count);
                    Rect stagesCard = TabletUi.CardPanel(new Rect(0, stagesY, width, stagesHeight), "STAGES");
                    if (stages.Count == 0)
                    {
                        TabletUi.Label(new Rect(stagesCard.x, stagesCard.y, stagesCard.width, row), "No stage data.", true);
                    }
                    else
                    {
                        for (int i = 0; i < stages.Count; i++)
                        {
                            StageInfo stage = stages[i];
                            string line = string.Format(CultureInfo.InvariantCulture,
                                "S{0} dv {1:0} m/s  TWR {2:0.00}  burn {3:0} s  Isp {4:0}",
                                stage.Stage, stage.DvActual, stage.TwrAsl, stage.BurnTime, stage.IspAsl);
                            TabletUi.Label(new Rect(stagesCard.x, stagesCard.y + i * row, stagesCard.width, row), line, true);
                        }
                    }

                    GUILayout.Space(stagesY + stagesHeight + gap);
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

        static string[] MissionBodyNames()
        {
            if (missionBodyNames != null) return missionBodyNames;

            List<string> names = new List<string>();
            if (FlightGlobals.Bodies != null)
            {
                foreach (CelestialBody body in FlightGlobals.Bodies)
                    if (body != null && !string.IsNullOrEmpty(body.bodyName))
                        names.Add(body.bodyName);
            }
            missionBodyNames = names.ToArray();
            return missionBodyNames;
        }
    }
}
