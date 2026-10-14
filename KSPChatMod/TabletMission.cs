using UnityEngine;

namespace KSPChatBridge
{
    public partial class AicsMenu
    {
        static Vector2 tabletMissionScroll;

        void TabletMission(Rect area)
        {
            float row = Mathf.Max(44, tabletFont * 2.6f);
            float width = area.width - 26;
            float pad = 16 + tabletFont * 1.6f;
            GUILayout.BeginArea(area);
            try
            {
                tabletMissionScroll = GUILayout.BeginScrollView(tabletMissionScroll);
                try
                {
                    Rect card = TabletUi.CardPanel(new Rect(0, 0, width, pad + row), "MISSION");
                    TabletUi.Label(new Rect(card.x, card.y, card.width, row), "MISSION - planner coming", true);
                    GUILayout.Space(pad + row);
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
    }
}
