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

                    List<LegBudget.Leg> legs = MissionLegs();
                    float legsY = stagesY + stagesHeight + gap;
                    float legsHeight = pad + row * Math.Max(1, legs.Count);
                    Rect legsCard = TabletUi.CardPanel(new Rect(0, legsY, width, legsHeight), "DELTA-V LEGS");
                    if (legs.Count == 0)
                    {
                        TabletUi.Label(new Rect(legsCard.x, legsCard.y, legsCard.width, row), "No legs available.", true);
                    }
                    else
                    {
                        for (int i = 0; i < legs.Count; i++)
                        {
                            string line = legs[i].Name + " " + legs[i].Dv.ToString("0", CultureInfo.InvariantCulture) + " m/s";
                            TabletUi.Label(new Rect(legsCard.x, legsCard.y + i * row, legsCard.width, row), line, true);
                        }
                    }

                    double[] stageDv = new double[stages.Count];
                    double have = 0;
                    for (int i = 0; i < stages.Count; i++)
                    {
                        stageDv[i] = stages[i].DvActual;
                        have += stageDv[i];
                    }
                    string[] legNames = new string[legs.Count];
                    double[] legDv = new double[legs.Count];
                    double need = 0;
                    for (int i = 0; i < legs.Count; i++)
                    {
                        legNames[i] = legs[i].Name;
                        legDv[i] = legs[i].Dv;
                        need += legDv[i];
                    }

                    List<LegResult> allocation = StageAllocator.Allocate(stageDv, legNames, legDv);
                    bool allSpare = legs.Count > 0 && allocation.Count > 0;
                    HashSet<string> allocatedLegs = new HashSet<string>();
                    for (int i = 0; i < allocation.Count; i++)
                    {
                        allocatedLegs.Add(allocation[i].Leg);
                        if (allocation[i].Spare < 0) allSpare = false;
                    }
                    if (allocatedLegs.Count < legs.Count) allSpare = false;

                    float allocationY = legsY + legsHeight + gap;
                    float barHeight = Mathf.Max(44, row * 1.4f);
                    float allocationHeight = pad + barHeight + gap + row * Math.Max(1, allocation.Count);
                    Rect allocationCard = TabletUi.CardPanel(new Rect(0, allocationY, width, allocationHeight), "STAGE ALLOCATION");
                    float fraction = need > 0 ? (float)Math.Max(0, Math.Min(1, have / need)) : 0;
                    string totals = have.ToString("0", CultureInfo.InvariantCulture) + " / " +
                        need.ToString("0", CultureInfo.InvariantCulture) + " m/s";
                    TabletUi.Bar(new Rect(allocationCard.x, allocationCard.y, allocationCard.width, barHeight),
                        "HAVE vs NEED", fraction, totals, allSpare ? TabletUi.Good : TabletUi.Bad);
                    if (allocation.Count == 0)
                    {
                        TabletUi.Label(new Rect(allocationCard.x, allocationCard.y + barHeight + gap,
                            allocationCard.width, row), "No stage allocation available.", true);
                    }
                    else
                    {
                        for (int i = 0; i < allocation.Count; i++)
                        {
                            LegResult result = allocation[i];
                            string line = string.Format(CultureInfo.InvariantCulture,
                                "{0} on S{1}, spare {2:0}", result.Leg, result.Stage, result.Spare);
                            TabletUi.Label(new Rect(allocationCard.x, allocationCard.y + barHeight + gap + i * row,
                                allocationCard.width, row), line, true);
                        }
                    }

                    GUILayout.Space(allocationY + allocationHeight + gap);
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

        List<LegBudget.Leg> MissionLegs()
        {
            CelestialBody home = BodyInfo.Find("Kerbin");
            CelestialBody destination = BodyInfo.Find(missionBody);
            List<LegBudget.Leg> legs = new List<LegBudget.Leg>();
            double orbitAlt;
            if (home == null || destination == null || destination == home ||
                destination.orbit == null ||
                !double.TryParse(missionAltT, NumberStyles.Float, CultureInfo.InvariantCulture, out orbitAlt) ||
                orbitAlt < 0)
                return legs;

            double launch = LegBudget.LaunchDv(home.gravParameter, home.Radius, orbitAlt,
                home.atmosphere ? home.atmosphereDepth : 0);
            double transferDv;
            double unusedDv;
            double unusedTime;
            RocketMath.Hohmann(home.gravParameter, home.Radius + orbitAlt, destination.orbit.semiMajorAxis,
                out transferDv, out unusedDv, out unusedTime);
            return LegBudget.Legs(launch, transferDv, 0, 0, 0);
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
