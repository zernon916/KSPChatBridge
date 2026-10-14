using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletCrewScroll;

        void TabletCrew(Rect area)
        {
            Vessel v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            float row = Mathf.Max(44, tabletFont * 2.6f), gap = 8;
            float width = area.width - 26, pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            tabletCrewScroll = GUILayout.BeginScrollView(tabletCrewScroll);
            if (v == null)
            {
                Rect none = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "CREW / EVA");
                TabletUi.Label(new Rect(none.x, none.y, none.width, row), "flight scene only", true);
                GUILayout.Space(pad + row + gap);
            }
            else if (v.isEVA)
            {
                Rect eva = TabletUi.CardPanel(new Rect(0, 0, width, pad + row * 2), "CREW / EVA");
                TabletUi.Label(new Rect(eva.x, eva.y, eva.width, row), "On EVA: board the nearest free seat with a hatch (within 50 m).", true);
                if (!string.IsNullOrEmpty(crewMsg)) TabletUi.Label(new Rect(eva.x, eva.y + row, eva.width, row), crewMsg, true);
                GUILayout.Space(pad + row * 2 + gap);
            }
            else
            {
                var crew = v.GetVesselCrew();
                int count = crew == null ? 0 : crew.Count;
                float h = pad + row * (count > 0 ? count + 1 : 2);
                Rect card = TabletUi.CardPanel(new Rect(0, 0, width, h), "CREW / EVA");
                float y = card.y;
                if (count == 0)
                {
                    TabletUi.Label(new Rect(card.x, y, card.width, row), "no crew aboard", true);
                    y += row;
                }
                else
                {
                    foreach (ProtoCrewMember k in crew)
                    {
                        TabletUi.Label(new Rect(card.x, y, card.width, row), k.name + " (" + k.experienceTrait.Title + ")", true);
                        y += row;
                    }
                }
                if (!string.IsNullOrEmpty(crewMsg)) TabletUi.Label(new Rect(card.x, y, card.width, row), crewMsg, true);
                GUILayout.Space(h + gap);
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}