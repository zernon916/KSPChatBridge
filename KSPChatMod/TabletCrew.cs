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
                Rect eva = TabletUi.CardPanel(new Rect(0, 0, width, pad + row * 4), "CREW / EVA");
                float yb = eva.y;
                TabletUi.Label(new Rect(eva.x, yb, eva.width, row), "On EVA: board the nearest free seat with a hatch (within 50 m).", true);
                yb += row;
                if (!TabletLink) { TabletUi.Label(new Rect(eva.x, yb, eva.width, row), "INFO ONLY - enable LIVE LINK", true); yb += row; }
                if (TabletUi.Button(new Rect(eva.x, yb, eva.width, row), "EVA: return to ship", false, TabletLink) && TabletLink)
                {
                    ChatLog.Write("g", "tablet crew: EVA return to ship");
                    crewMsg = BoardNearest(v);
                }
                yb += row;
                if (!string.IsNullOrEmpty(crewMsg)) TabletUi.Label(new Rect(eva.x, yb, eva.width, row), crewMsg, true);
                GUILayout.Space(pad + row * 4 + gap);
            }
            else
            {
                var crew = v.GetVesselCrew();
                int count = crew == null ? 0 : crew.Count;
                ProtoCrewMember selK = null;
                Part selP = null;
                foreach (Part p in v.parts)
                {
                    if (p.CrewCapacity <= 0) continue;
                    foreach (ProtoCrewMember k in p.protoModuleCrew)
                        if (k.name == crewPick) { selK = k; selP = p; }
                }
                int dests = 0;
                if (selK != null)
                    foreach (Part p in v.parts)
                        if (p != selP && p.CrewCapacity > p.protoModuleCrew.Count) dests++;
                int rows = 1 + (count > 0 ? count * 2 : 1) + (selK != null ? dests + 1 : 0) + (string.IsNullOrEmpty(crewMsg) ? 0 : 1);
                float h = pad + row * rows;
                Rect card = TabletUi.CardPanel(new Rect(0, 0, width, h), "CREW / EVA");
                float y = card.y;
                if (!TabletLink) { TabletUi.Label(new Rect(card.x, y, card.width, row), "INFO ONLY - enable LIVE LINK", true); y += row; }
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
                        if (TabletUi.Button(new Rect(card.x, y, card.width, row), crewPick == k.name ? "selected" : "move", crewPick == k.name, TabletLink) && TabletLink)
                        {
                            ChatLog.Write("g", "tablet crew: " + (crewPick == k.name ? "deselect " : "select ") + k.name);
                            crewPick = crewPick == k.name ? "" : k.name;
                        }
                        y += row;
                    }
                }
                if (selK != null)
                {
                    TabletUi.Label(new Rect(card.x, y, card.width, row), "Move " + selK.name + " to (free seats):", true);
                    y += row;
                    foreach (Part p in v.parts)
                    {
                        if (p == selP || p.CrewCapacity <= p.protoModuleCrew.Count) continue;
                        if (TabletUi.Button(new Rect(card.x, y, card.width, row), p.partInfo.title + "  (" + p.protoModuleCrew.Count + "/" + p.CrewCapacity + ")", false, TabletLink) && TabletLink)
                        {
                            ChatLog.Write("g", "tablet crew: move " + selK.name + " to " + p.partInfo.title);
                            crewMsg = MoveCrew(selK, selP, p);
                            crewPick = "";
                        }
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